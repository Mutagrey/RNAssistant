using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Agent;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Office.Services
{
    internal sealed partial class ConversationKernelAdapter
    {
        public async Task<AgentModelResult> SendAsync(AgentModelRequest request, CancellationToken cancellationToken)
        {
            if (_preparationFailure != null) return _preparationFailure;
            try { await EnsureModelSessionAsync(cancellationToken).ConfigureAwait(false); }
            catch (PromptBudgetExceededException ex) { return AgentModelResult.Failed(ModelProtocolFailureKind.PromptBudgetExceeded, ex.Message); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return AgentModelResult.Failed(ModelProtocolFailureKind.Infrastructure, ex.Message); }
            try { _modelSession.EndResponse(request.StepId); }
            catch (Exception ex) { return AgentModelResult.Failed(ModelProtocolFailureKind.Infrastructure, ex.Message); }
            try
            {
                if (_catalogGeneration != _executor.CaptureCatalogGeneration())
                {
                    // Publish complete descriptor/schema/binding and skill snapshots together at
                    // the request boundary. Exact admitted optional schemas are reconstructed by
                    // the existing admission journal; changed ones must be admitted again.
                    var fresh = _refresh == null
                        ? new ConversationRunInput(_input.Settings, _input.Context, _input.Tools,
                            _executor.CaptureSkills().Skills, _input.Attachments)
                        : await _refresh(cancellationToken).ConfigureAwait(false);
                    UseInput(fresh);
                    _modelSession.RebindAuthority(_catalog, _skillSnapshot, _input.Settings, _input.Context, _catalogGeneration);
                }
                _lastModel = await _protocol.GetResponseAsync(
                    _modelSession.CreateRequest(request.StepId,
                        new ModelProtocolCallContext(ConversationProtocolContext.BatchSafeReadIds(_catalog))),
                    ConversationStreamProgressProjector.ForProtocol(_progress), cancellationToken).ConfigureAwait(false);
            }
            catch (ResourceRequestException ex) when (ex.ErrorCode == "RESOURCE_CATALOG_CHANGED")
            {
                return AgentModelResult.Failed(ModelProtocolFailureKind.Infrastructure, ex.Message);
            }
            finally
            {
                _modelSession.ReleaseRequestMedia();
            }
            if (_lastModel == null) return AgentModelResult.Failed(ModelProtocolFailureKind.Infrastructure, "Missing model result.");
            _contextUsage = _lastModel.ContextUsage ?? _contextUsage;
            if (_lastModel.Failure != null)
                return AgentModelResult.Failed(_lastModel.Failure.Kind, _lastModel.Failure.Message);
            if (_lastModel.ProviderRefusal != null) return AgentModelResult.Refused(_lastModel.ProviderRefusal);
            var response = _lastModel.Response;
            return AgentModelResult.Accepted(new AgentResponseDraft(response.Message, response.ToolCalls.Select(call =>
                new ToolCallDraft(call.Name, call.Arguments.ToString(Formatting.None))), response.Final));
        }

        private async Task EnsureModelSessionAsync(CancellationToken cancellationToken)
        {
            if (_modelSession != null) return;
            // On confirmation this runs AFTER the real executor result is durably
            // accounted. Fresh catalog, document context and media stay outside Core.
            if (_confirmedCommand != null && _refresh != null)
                UseInput(await _refresh(cancellationToken).ConfigureAwait(false));
            _modelSession = await ConversationModelSession.CreateAsync(_adapter, _compaction, _attachments, _eventStore,
                _policy.Mode, _text, _session, _input.Context, _input.Settings, _catalog, _skills,
                _input.Attachments, _confirmedCommand != null, _progress, cancellationToken,
                _executor.ResourceAuthority, _executor.Payloads, () => _skillSnapshot, _catalogGeneration).ConfigureAwait(false);
        }

    }
}
