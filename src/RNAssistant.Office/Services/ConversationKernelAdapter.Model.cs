using RNAssistant.Application;
using RNAssistant.Core.Llm;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Agent;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Tools;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Office.Diagnostics;

namespace RNAssistant.Office.Services
{
    internal sealed partial class ConversationKernelAdapter
    {
        public async Task<AgentModelResult> SendAsync(AgentModelRequest request, CancellationToken cancellationToken)
        {
            if (_preparationFailure != null) return _preparationFailure;
            var contextTimer = Stopwatch.StartNew();
            try
            {
                if (_catalogGeneration != _executor.CaptureCatalogGeneration())
                {
                    // Publish complete descriptor/schema/binding and skill snapshots together at
                    // the request boundary. Exact admitted optional schemas are reconstructed by
                    // the existing admission journal; changed ones must be admitted again.
                    await RefreshAuthorityAsync(cancellationToken).ConfigureAwait(false);
                }
                var authorityMs = contextTimer.ElapsedMilliseconds;
                try
                {
                    await EnsureModelSessionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (ResourceRequestException ex) when (ex.ErrorCode == "RESOURCE_CATALOG_CHANGED")
                {
                    await RefreshAuthorityAsync(cancellationToken).ConfigureAwait(false);
                    await EnsureModelSessionAsync(cancellationToken).ConfigureAwait(false);
                }
                RefreshBrowserContext();
                var sessionMs = contextTimer.ElapsedMilliseconds - authorityMs;
                ModelProtocolRequest preparedRequest;
                try
                {
                    preparedRequest = await PrepareRequestAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (ResourceRequestException ex) when (ex.ErrorCode == "RESOURCE_CATALOG_CHANGED")
                {
                    // A completed tool may publish a new catalog generation between
                    // adapter construction and the next request. Rebind preparation
                    // once; no model call or tool effect is replayed.
                    await RefreshAuthorityAsync(cancellationToken).ConfigureAwait(false);
                    preparedRequest = await PrepareRequestAsync(request, cancellationToken).ConfigureAwait(false);
                }
                var requestMs = contextTimer.ElapsedMilliseconds - authorityMs - sessionMs;
                preparedRequest.Options.TraceRequestCause = request.RequestCause;
                preparedRequest.Options.ContextPreparation = new LlmContextPreparationTiming
                {
                    TotalMs = contextTimer.ElapsedMilliseconds,
                    AuthorityMs = authorityMs,
                    SetupMs = sessionMs,
                    CompileMs = requestMs
                };
                if (contextTimer.ElapsedMilliseconds >= 500)
                    RuntimeLog.Info("Model context timing: authority=" + authorityMs +
                        "ms, session=" + sessionMs + "ms, request=" + requestMs + "ms.");
                preparedRequest.RetainAttemptAsync = request.RetainAttemptAsync;
                _lastModel = await _protocol.GetResponseAsync(
                    preparedRequest,
                    ModelProtocolProgress.ForConversation(request.RunId, request.StepId,
                        value => _progress?.Invoke("model_progress", null, new ChatActivity { ModelProgress = value }),
                        () => RuntimeLog.Error("Optional model parser diagnostic append failed.")), cancellationToken).ConfigureAwait(false);
            }
            catch (PromptBudgetExceededException ex)
            {
                return AgentModelResult.Failed(ModelProtocolFailureKind.PromptBudgetExceeded, ex.Message);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ResourceRequestException ex) when (ex.ErrorCode == "RESOURCE_CATALOG_CHANGED")
            {
                return AgentModelResult.Failed(ModelProtocolFailureKind.Infrastructure, ex.Message);
            }
            catch (Exception ex)
            {
                return AgentModelResult.Failed(ModelProtocolFailureKind.Infrastructure, ex.Message);
            }
            finally
            {
                if (_modelSession != null) _pendingMessageUpdates.AddRange(_modelSession.ReleaseRequestMedia());
            }
            if (_lastModel == null) return AgentModelResult.Failed(ModelProtocolFailureKind.Infrastructure, "Missing model result.");
            _contextUsage = _lastModel.ContextUsage ?? _contextUsage;
            if (_lastModel.Failure != null)
                return AgentModelResult.Failed(_lastModel.Failure.Kind, _lastModel.Failure.Message);
            if (_lastModel.ProviderRefusal != null) return AgentModelResult.Refused(_lastModel.ProviderRefusal);
            var response = _lastModel.Response;
            return AgentModelResult.Accepted(new AgentResponseDraft(response.Action, response.Reason, response.Message,
                response.Action == ConversationResponse.ExecuteAction
                    ? response.ToolCalls.Select(call => new ToolCallDraft(call.Name, call.Arguments.ToString(Formatting.None)))
                    : null, response.Decision, response.Results, response.Outcome),
                _lastModel.SourceModelAttemptId, _lastModel.Completion?.AttemptSnapshot);
        }

        private async Task EnsureModelSessionAsync(CancellationToken cancellationToken)
        {
            if (_modelSession != null) return;
            // On confirmation this runs AFTER the real executor result is durably
            // accounted. Fresh catalog, document context and media stay outside Core.
            if (_catalogGeneration != _executor.CaptureCatalogGeneration() ||
                _confirmedCommand != null && _refresh != null)
            {
                var fresh = _refresh == null
                    ? new ConversationRunInput(_input.Settings, _input.Context, _input.Tools,
                        _input.Skills, _input.Attachments)
                    : await _refresh(cancellationToken).ConfigureAwait(false);
                if (_steered) fresh = new ConversationRunInput(_input.Settings, fresh.Context, fresh.Tools,
                    fresh.Skills, _input.Attachments, fresh.UpdatedMessages);
                UseInput(fresh);
            }
            string referencedTarget, referencedError;
            ResolveReferencedBrowserTarget(out referencedTarget, out referencedError);
            _modelSession = await ConversationModelSession.CreateAsync(_adapter, _compaction, _attachments, _eventStore,
                _policy.Mode, _text, _session, _input.Context, _input.Settings, _catalog, _skills,
                _input.Attachments, _confirmedCommand != null, _progress, cancellationToken,
                _executor.ResourceAuthority, _executor.Payloads, () => _skillSnapshot, _catalogGeneration,
                _conversations as IConversationContextStore,
                browserPageTarget: CaptureBoundBrowserPageTarget(),
                referencedResourceTarget: referencedTarget,
                referencedResourceError: referencedError, ownsSearch: (run, call) =>
                    (_nativeTools.HandlerFor(ResourceToolCatalog.SearchToolId) as IToolContinuationOwner)?.OwnsResult(run, call) == true).ConfigureAwait(false);
        }

        private void RefreshBrowserContext()
        {
            if (_modelSession == null) return;
            string referencedTarget, referencedError;
            ResolveReferencedBrowserTarget(out referencedTarget, out referencedError);
            _modelSession.SetBrowserContext(CaptureBoundBrowserPageTarget(),
                referencedTarget, referencedError);
        }

        private string CaptureBoundBrowserPageTarget()
        {
            var source = _executor.ExternalBrowserReadSource;
            if (source == null || string.IsNullOrWhiteSpace(_session?.Id)) return null;
            var targets = source.TargetsFor(_session.Id);
            return targets != null && targets.Count == 1 && targets[0] != null
                ? targets[0].Target : null;
        }

        private void ResolveReferencedBrowserTarget(out string target, out string error)
        {
            target = null;
            error = null;
            var text = _text ?? string.Empty;
            if (text.IndexOf("/resources/", StringComparison.Ordinal) < 0) return;

            var resolved = new List<string>();
            var found = false;
            foreach (Match match in Regex.Matches(text,
                @"/resources/[^\s<>""']+", RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100)))
            {
                found = true;
                var locator = match.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}');
                BrowserLocalLocator parsed;
                if (!BrowserLocalLocator.TryParse(locator, out parsed) ||
                    parsed.Kind != BrowserLocatorKind.Resource &&
                    parsed.Kind != BrowserLocatorKind.ResourceVersion ||
                    string.IsNullOrWhiteSpace(parsed.RelativePath))
                {
                    error = "resource_target_not_found";
                    continue;
                }
                try
                {
                    var selected = _executor.ResourceGateway.ResolveBrowserDisplayLocator(
                        _session, locator);
                    if (!resolved.Contains(selected.Target, StringComparer.Ordinal))
                        resolved.Add(selected.Target);
                }
                catch (ResourceRequestException ex)
                {
                    error = ex.ErrorCode;
                }
            }
            if (!found) return;
            if (!string.IsNullOrWhiteSpace(error)) return;
            if (resolved.Count == 1) target = resolved[0];
            else if (resolved.Count > 1) error = "resource_target_ambiguous";
        }

        private async Task<ModelProtocolRequest> PrepareRequestAsync(AgentModelRequest request, CancellationToken cancellationToken)
        {
            var firstMessage = _session.Messages.Count;
            try
            {
                return await _modelSession.PrepareRequestAsync(request.StepId,
                    new ModelProtocolCallContext(ConversationProtocolContext.SequentialBatchIds(_catalog),
                        reflectionEnabled: request.ReflectionLimit != 0),
                    cancellationToken, ModelProtocolWire.RuntimeFeedback(request)).ConfigureAwait(false);
            }
            finally { _pendingMessageUpdates.AddRange(_session.Messages.Skip(firstMessage)); }
        }

        private async Task RefreshAuthorityAsync(CancellationToken cancellationToken)
        {
            var fresh = _refresh == null
                ? new ConversationRunInput(_input.Settings, _input.Context, _input.Tools,
                    _input.Skills, _input.Attachments)
                : await _refresh(cancellationToken).ConfigureAwait(false);
            UseInput(fresh);
            if (_modelSession != null)
                _modelSession.RebindAuthority(_catalog, _skillSnapshot, _input.Settings, _input.Context, _catalogGeneration);
        }

    }
}
