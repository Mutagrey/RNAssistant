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
                _lastModel = await _protocol.GetResponseAsync(
                    _modelSession.CreateRequest(request.StepId,
                        new ModelProtocolCallContext(ConversationProtocolContext.BatchSafeReadIds(_catalog))),
                    ConversationStreamProgressProjector.ForProtocol(_progress), cancellationToken).ConfigureAwait(false);
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
                new ToolCallDraft(call.Name, JsonConvert.SerializeObject(call.Arguments, Formatting.None)))));
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
                _input.Attachments, _confirmedCommand != null, _progress, cancellationToken).ConfigureAwait(false);
        }

    }
}
