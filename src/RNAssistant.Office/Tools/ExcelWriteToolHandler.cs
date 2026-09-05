using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.Office.Runtime;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class ExcelWriteToolHandler : IToolHandler
    {
        internal static readonly ToolBinding Binding = new ToolBinding("excel.write.range.v1");

        private readonly ExcelWriteToolAdapter _adapter;
        private readonly HostRuntime _runtime;
        private readonly ChatSession _session;

        internal ExcelWriteToolHandler(ExcelWriteToolAdapter adapter, HostRuntime runtime, ChatSession session)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _session = session;
        }

        public Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken cancellationToken)
        {
            if (_session == null)
                return Failure("Excel writes require an active chat session.", "excel_write_session_required", false);
            try
            {
                var outcome = _runtime.ExecuteDocumentMutation(Target(_session), cancellationToken, delegate
                {
                    return _adapter.Execute(context.Arguments,
                        context.MarkDispatchPossible, cancellationToken);
                }, terminalOutcome => context.Complete(Result(terminalOutcome)), context.CompleteFailure);
                return Task.FromResult(Result(outcome));
            }
            catch (OfficeDocumentGuardException ex) when (!context.MayHaveDispatched)
            {
                return Failure(ex.Message, ex.ErrorCode, ex.Retryable);
            }
            catch (HostRuntime.MutationLockException ex) when (!context.MayHaveDispatched)
            {
                return Failure(ex.Message,
                    ex.Retryable ? "tool_mutation_busy" : "tool_mutation_lock_unavailable", ex.Retryable);
            }
        }

        private static ToolHandlerResult Result(ExcelWriteOutcome outcome)
        {
            if (outcome == null) throw new InvalidOperationException("Excel write returned no outcome.");
            RuntimeResult result;
            if (outcome.Status == ExcelWriteOutcomeStatus.Ok)
                result = RuntimeResult.Ok(outcome.Message, outcome.DataJson);
            else if (outcome.Status == ExcelWriteOutcomeStatus.Unknown)
                result = RuntimeResult.Unknown(outcome.Message, outcome.DataJson);
            else result = RuntimeResult.Error(outcome.Message, outcome.DataJson);
            return new ToolHandlerResult(result, Effect(outcome.Effect));
        }

        private static ToolEffectEvidence Effect(ExcelWriteEffect effect)
        {
            switch (effect)
            {
                case ExcelWriteEffect.VerifiedNoChange: return ToolEffectEvidence.VerifiedNoChange;
                case ExcelWriteEffect.VerifiedChange: return ToolEffectEvidence.VerifiedChange;
                case ExcelWriteEffect.Unknown: return ToolEffectEvidence.Unknown;
                default: return ToolEffectEvidence.None;
            }
        }

        private static OfficeDocumentExecutionExpectation Target(ChatSession session)
        {
            return new OfficeDocumentExecutionExpectation
            {
                Host = session.Host,
                DocumentKey = session.DocumentKey,
                RuntimeDocumentKey = session.LastRun == null ? string.Empty : session.LastRun.DocumentRuntimeKey
            };
        }

        private static Task<ToolHandlerResult> Failure(string message, string code, bool retryable)
        {
            return Task.FromResult(new ToolHandlerResult(RuntimeResult.Error(message,
                JsonConvert.SerializeObject(new { code, retryable })), ToolEffectEvidence.None));
        }
    }
}
