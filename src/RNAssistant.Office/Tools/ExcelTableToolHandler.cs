using System;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.Office.Runtime;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class ExcelTableToolHandler : IManagedMutationToolHandler
    {
        internal static readonly ToolBinding Binding =
            new ToolBinding("excel.table.add.v1");

        private readonly ExcelTableToolAdapter _adapter;
        private readonly HostRuntime _runtime;
        private readonly ChatSession _session;

        internal ExcelTableToolHandler(
            ExcelTableToolAdapter adapter,
            HostRuntime runtime,
            ChatSession session)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _session = session;
        }

        public Task<ToolHandlerResult> ExecuteAsync(
            ToolHandlerContext context,
            CancellationToken cancellationToken)
        {
            if (_session == null)
                return OfficeToolFailure.Rejected(
                    "Excel table operations require an active chat session.",
                    "excel_table_session_required");
            try
            {
                var outcome = _runtime.ExecuteDocumentMutation(
                    Target(_session), cancellationToken, delegate
                    {
                        return _adapter.Add(
                            context.Arguments,
                            context.MarkDispatchPossible,
                            cancellationToken);
                    }, terminalOutcome => context.Complete(Result(terminalOutcome)), context.CompleteFailure);
                return Task.FromResult(Result(outcome));
            }
            catch (OfficeDocumentGuardException ex) when (!context.MayHaveDispatched)
            {
                return OfficeToolFailure.Guard(ex);
            }
            catch (HostRuntime.MutationLockException ex) when (!context.MayHaveDispatched)
            {
                return OfficeToolFailure.Lock(ex);
            }
        }

        private static ToolHandlerResult Result(ExcelTableOutcome outcome)
        {
            if (outcome == null)
                throw new InvalidOperationException(
                    "Excel table operation returned no outcome.");
            RuntimeResult result;
            if (outcome.Status == ExcelTableOutcomeStatus.Ok)
                result = RuntimeResult.Ok(outcome.Message, outcome.DataJson);
            else if (outcome.Status == ExcelTableOutcomeStatus.Unknown)
                result = RuntimeResult.Unknown(outcome.Message, outcome.DataJson);
            else result = RuntimeResult.Error(outcome.Message, outcome.DataJson);
            return new ToolHandlerResult(result,
                outcome.Effect == ExcelTableEffect.VerifiedChange
                    ? ToolEffectEvidence.VerifiedChange
                    : outcome.Effect == ExcelTableEffect.Unknown
                        ? ToolEffectEvidence.Unknown
                        : ToolEffectEvidence.None,
                recovery: outcome.Status == ExcelTableOutcomeStatus.Error
                    ? OfficeToolFailure.DefiniteDomain(outcome.Retryable) : null);
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
    }
}
