using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.Outlook;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Services;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class OutlookToolHandler : IToolHandler
    {
        private readonly string _toolId;
        private readonly OutlookToolAdapter _adapter;
        private readonly HostRuntime _runtime;
        private readonly ChatSession _session;
        private readonly OutlookSearchResourceService _search;

        internal OutlookToolHandler(
            string toolId,
            OutlookToolAdapter adapter,
            HostRuntime runtime,
            ChatSession session, ResourceGatewayService gateway)
        {
            if (!OutlookToolIds.Owns(toolId))
                throw new ArgumentException(
                    "An exact Outlook tool id is required.", nameof(toolId));
            _toolId = toolId;
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _session = session;
            _search = new OutlookSearchResourceService(gateway);
        }

        internal static ToolBinding BindingFor(string toolId)
        {
            if (!OutlookToolIds.Owns(toolId)) return null;
            if (toolId == OutlookToolIds.SearchMail) return new ToolBinding("outlook.search.mail.resource.v1");
            return new ToolBinding(
                "outlook." +
                toolId.Substring("outlook.".Length).Replace('_', '.') +
                ".v1");
        }

        public Task<ToolHandlerResult> ExecuteAsync(
            ToolHandlerContext context,
            CancellationToken cancellationToken)
        {
            if (_session == null)
                return Failure(
                    "Outlook operations require an active chat session.",
                    "outlook_session_required", false);
            try
            {
                if (_toolId == OutlookToolIds.SearchMail)
                    return Task.FromResult(_runtime.ReadDocument(Target(_session), cancellationToken, delegate {
                        context.MarkDispatchPossible();
                        return _search.Search(_session, context.Arguments, cancellationToken);
                    }));
                var outcome = OutlookToolIds.IsRead(_toolId)
                    ? _runtime.ReadDocument(
                        Target(_session), cancellationToken, delegate
                        {
                            context.MarkDispatchPossible();
                            return _adapter.Execute(
                                _toolId, context.Arguments, null,
                                cancellationToken);
                        })
                    : _runtime.ExecuteDocumentMutation(
                        Target(_session), cancellationToken, delegate
                        {
                            return _adapter.Execute(
                                _toolId, context.Arguments,
                                context.MarkDispatchPossible,
                                cancellationToken);
                        }, terminalOutcome => context.Complete(Result(terminalOutcome)), context.CompleteFailure);
                return Task.FromResult(Result(outcome));
            }
            catch (OfficeDocumentGuardException ex)
                when (!context.MayHaveDispatched)
            {
                return Failure(ex.Message, ex.ErrorCode, ex.Retryable);
            }
            catch (HostRuntime.MutationLockException ex)
                when (!context.MayHaveDispatched)
            {
                return Failure(
                    ex.Message,
                    ex.Retryable ? "tool_mutation_busy" :
                        "tool_mutation_lock_unavailable",
                    ex.Retryable);
            }
        }

        private static ToolHandlerResult Result(OutlookOutcome outcome)
        {
            if (outcome == null)
                throw new InvalidOperationException(
                    "Outlook operation returned no outcome.");
            RuntimeResult result;
            if (outcome.Status == OutlookOutcomeStatus.Ok)
                result = RuntimeResult.Ok(outcome.Message, outcome.DataJson);
            else if (outcome.Status == OutlookOutcomeStatus.Unknown)
                result = RuntimeResult.Unknown(outcome.Message, outcome.DataJson);
            else result = RuntimeResult.Error(outcome.Message, outcome.DataJson);
            return new ToolHandlerResult(result, Effect(outcome.Effect));
        }

        private static ToolEffectEvidence Effect(OutlookEffect effect)
        {
            switch (effect)
            {
                case OutlookEffect.VerifiedNoChange:
                    return ToolEffectEvidence.VerifiedNoChange;
                case OutlookEffect.VerifiedChange:
                    return ToolEffectEvidence.VerifiedChange;
                case OutlookEffect.Unknown:
                    return ToolEffectEvidence.Unknown;
                default:
                    return ToolEffectEvidence.None;
            }
        }

        private static OfficeDocumentExecutionExpectation Target(
            ChatSession session)
        {
            return new OfficeDocumentExecutionExpectation
            {
                Host = session.Host,
                DocumentKey = session.DocumentKey,
                RuntimeDocumentKey = session.LastRun == null
                    ? string.Empty : session.LastRun.DocumentRuntimeKey
            };
        }

        private static Task<ToolHandlerResult> Failure(
            string message, string code, bool retryable)
        {
            return Task.FromResult(new ToolHandlerResult(
                RuntimeResult.Error(message,
                    JsonConvert.SerializeObject(new { code, retryable })),
                ToolEffectEvidence.None));
        }
    }
}
