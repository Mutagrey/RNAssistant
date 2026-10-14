using System;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.Office.Runtime;

namespace RNAssistant.Office.Tools
{
    internal sealed class ExcelRangeMutationToolHandler : IManagedMutationToolHandler
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
                            cancellationToken);
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
