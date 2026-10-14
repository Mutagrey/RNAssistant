using System;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.Office.Runtime;

namespace RNAssistant.Office.Tools
{
    internal sealed class ExcelChartToolHandler : IReadOnlyToolHandler, IManagedMutationToolHandler
    {
        private readonly string _toolId;
        private readonly ExcelChartToolAdapter _adapter;
        private readonly HostRuntime _runtime;
        private readonly ChatSession _session;

        internal ExcelChartToolHandler(
            string toolId,
            ExcelChartToolAdapter adapter,
            HostRuntime runtime,
            ChatSession session)
        {
            if (!ExcelChartToolIds.Owns(toolId))
                throw new ArgumentException(
                    "Unsupported Excel chart tool id.", nameof(toolId));
            _toolId = toolId;
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _session = session;
        }

        internal static ToolBinding BindingFor(string toolId)
        {
            if (string.Equals(
                toolId, ExcelChartToolIds.CreateChatChart,
                StringComparison.Ordinal))
                return new ToolBinding("excel.chart.chat.v1");
            if (string.Equals(
                toolId, ExcelChartToolIds.UpsertChart,
                StringComparison.Ordinal))
                return new ToolBinding("excel.chart.upsert.v1");
            if (string.Equals(
                toolId, ExcelChartToolIds.DeleteChart,
                StringComparison.Ordinal))
                return new ToolBinding("excel.chart.delete.v1");
            return null;
        }

        public Task<ToolHandlerResult> ExecuteAsync(
            ToolHandlerContext context,
            CancellationToken cancellationToken)
        {
            if (_session == null)
                return OfficeToolFailure.Rejected(
                    "Excel chart operations require an active chat session.",
                    "excel_chart_session_required");
            try
            {
                var target = Target(_session);
                var outcome = ExcelChartToolIds.IsMutation(_toolId)
                    ? _runtime.ExecuteDocumentMutation(
                        target, cancellationToken, delegate
                        {
                            return _adapter.Execute(
                                _toolId, context.Arguments,
                                context.MarkDispatchPossible,
                                cancellationToken);
                        }, terminalOutcome => context.Complete(OfficeDomainResult.Project(terminalOutcome)), context.CompleteFailure)
                    : _runtime.ReadDocument(
                        target, cancellationToken, delegate
                        {
                            return _adapter.Execute(
                                _toolId, context.Arguments, null,
                                cancellationToken);
                        });
                return Task.FromResult(OfficeDomainResult.Project(outcome));
            }
            catch (OfficeDocumentGuardException ex)
                when (!context.MayHaveDispatched)
            {
                return OfficeToolFailure.Guard(ex);
            }
            catch (HostRuntime.MutationLockException ex)
                when (!context.MayHaveDispatched)
            {
                return OfficeToolFailure.Lock(ex);
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

    }
}
