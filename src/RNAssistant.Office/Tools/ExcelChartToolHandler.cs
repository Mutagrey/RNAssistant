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
    internal sealed class ExcelChartToolHandler : IToolHandler
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
                return Failure(
                    "Excel chart operations require an active chat session.",
                    "excel_chart_session_required", false);
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
                        }, terminalOutcome => context.Complete(Result(terminalOutcome)), context.CompleteFailure)
                    : _runtime.ReadDocument(
                        target, cancellationToken, delegate
                        {
                            return _adapter.Execute(
                                _toolId, context.Arguments, null,
                                cancellationToken);
                        });
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
                return Failure(ex.Message,
                    ex.Retryable ? "tool_mutation_busy" :
                        "tool_mutation_lock_unavailable",
                    ex.Retryable);
            }
        }

        private static ToolHandlerResult Result(ExcelChartOutcome outcome)
        {
            if (outcome == null)
                throw new InvalidOperationException(
                    "Excel chart operation returned no outcome.");
            RuntimeResult result;
            if (outcome.Status == ExcelChartOutcomeStatus.Ok)
                result = RuntimeResult.Ok(outcome.Message, outcome.DataJson);
            else if (outcome.Status == ExcelChartOutcomeStatus.Unknown)
                result = RuntimeResult.Unknown(outcome.Message, outcome.DataJson);
            else result = RuntimeResult.Error(outcome.Message, outcome.DataJson);
            return new ToolHandlerResult(result, Effect(outcome.Effect));
        }

        private static ToolEffectEvidence Effect(ExcelChartEffect effect)
        {
            switch (effect)
            {
                case ExcelChartEffect.VerifiedNoChange:
                    return ToolEffectEvidence.VerifiedNoChange;
                case ExcelChartEffect.VerifiedChange:
                    return ToolEffectEvidence.VerifiedChange;
                case ExcelChartEffect.Unknown:
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
