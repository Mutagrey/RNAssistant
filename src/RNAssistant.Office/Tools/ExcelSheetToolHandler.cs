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
    internal sealed class ExcelSheetToolHandler : IToolHandler
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
                return Failure(
                    "Excel sheet operations require an active chat session.",
                    "excel_sheet_session_required", false);
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
                    });
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
            return new ToolHandlerResult(result, Effect(outcome.Effect));
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
