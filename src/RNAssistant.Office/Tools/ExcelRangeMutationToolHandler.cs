using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.Office.Runtime;

namespace RNAssistant.Office.Tools
{
    internal class ExcelRangeMutationToolHandler : IManagedMutationToolHandler
    {
        private static readonly ToolBinding FormatBinding =
            new ToolBinding("excel.range.format.v1");
        private static readonly ToolBinding ClearBinding =
            new ToolBinding("excel.range.clear.v1");
        private static readonly ToolBinding DeleteBinding =
            new ToolBinding("excel.rows_columns.delete.v1");
        private static readonly ToolBinding SortBinding =
            new ToolBinding("excel.range.sort.v1");
        private static readonly ToolBinding FilterBinding =
            new ToolBinding("excel.range.filter.v1");

        private readonly string _toolId;
        protected readonly ExcelRangeMutationToolAdapter _adapter;
        protected readonly HostRuntime _runtime;
        protected readonly ChatSession _session;

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
                case ExcelRangeMutationToolIds.DeleteRowsColumns: return DeleteBinding;
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
                return OfficeToolFailure.Rejected(
                    "Excel range mutations require an active chat session.",
                    "excel_range_session_required");
            try
            {
                var outcome = _runtime.ExecuteDocumentMutation(
                    Target(_session), cancellationToken, delegate
                    {
                        return _adapter.Execute(
                            _toolId,
                            context.Arguments,
                            context.MarkDispatchPossible,
                            cancellationToken, context.PreparedStateJson);
                    }, terminalOutcome => context.Complete(OfficeDomainResult.Project(terminalOutcome)), context.CompleteFailure);
                return Task.FromResult(OfficeDomainResult.Project(outcome));
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

        protected static OfficeDocumentExecutionExpectation Target(ChatSession session)
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

    internal sealed class ExcelDeleteRowsColumnsToolHandler : ExcelRangeMutationToolHandler, IPreparableToolHandler
    {
        internal ExcelDeleteRowsColumnsToolHandler(ExcelRangeMutationToolAdapter adapter, HostRuntime runtime, ChatSession session)
            : base(ExcelRangeMutationToolIds.DeleteRowsColumns, adapter, runtime, session) { }

        public Task<ToolPreparationResult> PrepareAsync(ToolHandlerContext context, CancellationToken cancellationToken)
        {
            try
            {
                if (_session == null) throw new ExcelRangeMutationBackendException("Deletion requires an active chat session.", "excel_range_session_required", false);
                var state = _runtime.ReadDocument(Target(_session), cancellationToken,
                    () => _adapter.PrepareDeletion(context.Arguments, cancellationToken));
                var selected = state.Selection;
                var message = "Delete " + selected.Count + " " + selected.Axis.ToString().ToLowerInvariant() +
                    " starting at " + selected.Start + " in " + (selected.Scope == ExcelDeletionScope.Table
                        ? "table '" + selected.Table + "' on " : "worksheet ") + selected.Sheet + ".";
                return Task.FromResult(new ToolPreparationResult(RNAssistant.Core.Tools.Contracts.ToolResult.Ok(message),
                    JsonConvert.SerializeObject(state)));
            }
            catch (OfficeDocumentGuardException ex) { return OfficeToolFailure.GuardPreparation(ex); }
            catch (HostRuntime.MutationLockException ex) { return OfficeToolFailure.LockPreparation(ex); }
            catch (ExcelRangeMutationBackendException ex)
            {
                return Task.FromResult(new ToolPreparationResult(RNAssistant.Core.Tools.Contracts.ToolResult.Error(ex.Message,
                    new JObject { ["code"] = ex.ErrorCode, ["retryable"] = false }.ToString(Formatting.None)), null));
            }
        }
    }
}
