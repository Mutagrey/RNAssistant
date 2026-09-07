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
    internal sealed class ExcelReadToolHandler : IReadOnlyToolHandler
    {
        internal static readonly ToolBinding InspectBinding = new ToolBinding("excel.read.inspect.v1");

        private readonly string _toolId;
        private readonly ExcelReadToolAdapter _adapter;
        private readonly HostRuntime _runtime;
        private readonly ChatSession _session;

        internal ExcelReadToolHandler(string toolId, ExcelReadToolAdapter adapter, HostRuntime runtime, ChatSession session)
        {
            if (!ExcelReadToolIds.Owns(toolId)) throw new ArgumentException("An exact Excel read tool id is required.", nameof(toolId));
            _toolId = toolId;
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _session = session;
        }

        internal static ToolBinding BindingFor(string toolId)
        {
            return ExcelReadToolIds.Owns(toolId) ? InspectBinding : null;
        }

        public Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken cancellationToken)
        {
            if (_session == null)
                return OfficeToolFailure.Rejected(
                    "Excel reads require an active chat session.",
                    "excel_read_session_required");
            try
            {
                var result = _runtime.ReadDocument(Target(_session), cancellationToken, delegate
                {
                    context.MarkDispatchPossible();
                    return Result(_adapter.ExecuteOutcome(
                        _toolId, context.Arguments));
                });
                return Task.FromResult(result);
            }
            catch (OfficeDocumentGuardException ex)
            {
                return OfficeToolFailure.Guard(ex);
            }
            catch (HostRuntime.MutationLockException ex)
            {
                return OfficeToolFailure.Lock(ex);
            }
        }

        private static ToolHandlerResult Result(ExcelReadOutcome outcome)
        {
            if (outcome == null)
                throw new InvalidOperationException(
                    "Excel read returned no outcome.");
            var result = outcome.Success
                ? RuntimeResult.Ok(outcome.Message, outcome.DataJson)
                : RuntimeResult.Error(outcome.Message, outcome.DataJson);
            return new ToolHandlerResult(result, ToolEffectEvidence.None,
                recovery: outcome.Success ? null :
                    OfficeToolFailure.DefiniteDomain(outcome.Retryable));
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
    }
}
