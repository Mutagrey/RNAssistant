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
    internal sealed class ExcelSheetToolHandler : IManagedMutationToolHandler
    {
        internal static readonly ToolBinding AddBinding =
            new ToolBinding("excel.sheet.add.v1");
        internal static readonly ToolBinding RenameBinding =
            new ToolBinding("excel.sheet.rename.v1");

        private readonly string _toolId;
        private readonly ExcelSheetToolAdapter _adapter;
        private readonly HostRuntime _runtime;
        private readonly ChatSession _session;

        internal ExcelSheetToolHandler(
            string toolId,
            ExcelSheetToolAdapter adapter,
            HostRuntime runtime,
            ChatSession session)
        {
            if (!ExcelSheetToolIds.Owns(toolId))
                throw new ArgumentException(
                    "An exact Excel sheet tool id is required.", nameof(toolId));
            _toolId = toolId;
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _session = session;
        }

        internal static ToolBinding BindingFor(string toolId)
        {
            return string.Equals(toolId, ExcelSheetToolIds.AddSheet, StringComparison.Ordinal)
                ? AddBinding : RenameBinding;
        }

        public Task<ToolHandlerResult> ExecuteAsync(
            ToolHandlerContext context,
            CancellationToken cancellationToken)
        {
            if (_session == null)
                return OfficeToolFailure.Rejected(
                    "Excel sheet operations require an active chat session.",
                    "excel_sheet_session_required");
            try
            {
                var outcome = _runtime.ExecuteDocumentMutation(
                    Target(_session), cancellationToken, delegate
                    {
                        return string.Equals(
                            _toolId, ExcelSheetToolIds.AddSheet,
                            StringComparison.Ordinal)
                            ? _adapter.Add(
                                context.Arguments,
                                context.MarkDispatchPossible,
                                cancellationToken)
                            : _adapter.Rename(
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

        private static ToolHandlerResult Result(ExcelSheetOutcome outcome)
        {
            if (outcome == null)
                throw new InvalidOperationException(
                    "Excel sheet operation returned no outcome.");
            RuntimeResult result;
            if (outcome.Status == ExcelSheetOutcomeStatus.Ok)
                result = RuntimeResult.Ok(outcome.Message, outcome.DataJson);
            else if (outcome.Status == ExcelSheetOutcomeStatus.Unknown)
                result = RuntimeResult.Unknown(outcome.Message, outcome.DataJson);
            else result = RuntimeResult.Error(outcome.Message, outcome.DataJson);
            return new ToolHandlerResult(result, Effect(outcome.Effect),
                recovery: outcome.Status == ExcelSheetOutcomeStatus.Error
                    ? OfficeToolFailure.DefiniteDomain(outcome.Retryable) : null);
        }

        private static ToolEffectEvidence Effect(ExcelSheetEffect effect)
        {
            switch (effect)
            {
                case ExcelSheetEffect.VerifiedNoChange:
                    return ToolEffectEvidence.VerifiedNoChange;
                case ExcelSheetEffect.VerifiedChange:
                    return ToolEffectEvidence.VerifiedChange;
                case ExcelSheetEffect.Unknown:
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
    }
}
