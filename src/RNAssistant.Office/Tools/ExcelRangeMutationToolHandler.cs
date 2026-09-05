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
    internal sealed class ExcelRangeMutationToolHandler : IToolHandler
    {
        private static readonly ToolBinding FormatBinding =
            new ToolBinding("excel.range.format.v1");
        private static readonly ToolBinding ClearBinding =
            new ToolBinding("excel.range.clear.v1");
        private static readonly ToolBinding SortBinding =
            new ToolBinding("excel.range.sort.v1");
        private static readonly ToolBinding FilterBinding =
            new ToolBinding("excel.range.filter.v1");

        private readonly string _toolId;
        private readonly ExcelRangeMutationToolAdapter _adapter;
        private readonly HostRuntime _runtime;
        private readonly ChatSession _session;

        internal ExcelRangeMutationToolHandler(
            string toolId,
            ExcelRangeMutationToolAdapter adapter,
            HostRuntime runtime,
            ChatSession session)
        {
            if (!ExcelRangeMutationToolIds.Owns(toolId))
                throw new ArgumentException(
                    "An exact Excel range mutation tool id is required.",
                    nameof(toolId));
            _toolId = toolId;
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _session = session;
        }

        internal static ToolBinding BindingFor(string toolId)
        {
            switch (toolId)
            {
                case ExcelRangeMutationToolIds.FormatRange: return FormatBinding;
                case ExcelRangeMutationToolIds.ClearRange: return ClearBinding;
                case ExcelRangeMutationToolIds.SortRange: return SortBinding;
                case ExcelRangeMutationToolIds.FilterRange: return FilterBinding;
                default: return null;
            }
        }

        public Task<ToolHandlerResult> ExecuteAsync(
            ToolHandlerContext context,
            CancellationToken cancellationToken)
        {
            if (_session == null)
                return Failure(
                    "Excel range mutations require an active chat session.",
                    "excel_range_session_required", false);
            try
            {
                var outcome = _runtime.ExecuteDocumentMutation(
                    Target(_session), cancellationToken, delegate
                    {
                        return _adapter.Execute(
                            _toolId,
                            context.Arguments,
                            context.MarkDispatchPossible,
                            cancellationToken);
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
                    ex.Retryable ? "tool_mutation_busy" :
                        "tool_mutation_lock_unavailable",
                    ex.Retryable);
            }
        }

        private static ToolHandlerResult Result(ExcelRangeMutationOutcome outcome)
        {
            if (outcome == null)
                throw new InvalidOperationException(
                    "Excel range mutation returned no outcome.");
            RuntimeResult result;
            if (outcome.Status == ExcelRangeMutationOutcomeStatus.Ok)
                result = RuntimeResult.Ok(outcome.Message, outcome.DataJson);
            else if (outcome.Status == ExcelRangeMutationOutcomeStatus.Unknown)
                result = RuntimeResult.Unknown(outcome.Message, outcome.DataJson);
            else result = RuntimeResult.Error(outcome.Message, outcome.DataJson);
            return new ToolHandlerResult(result, Effect(outcome.Effect));
        }

        private static ToolEffectEvidence Effect(ExcelRangeMutationEffect effect)
        {
            switch (effect)
            {
                case ExcelRangeMutationEffect.VerifiedNoChange:
                    return ToolEffectEvidence.VerifiedNoChange;
                case ExcelRangeMutationEffect.VerifiedChange:
                    return ToolEffectEvidence.VerifiedChange;
                case ExcelRangeMutationEffect.Unknown:
                    return ToolEffectEvidence.Unknown;
                default:
                    return ToolEffectEvidence.None;
            }
        }

        private static OfficeDocumentExecutionExpectation Target(ChatSession session)
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
