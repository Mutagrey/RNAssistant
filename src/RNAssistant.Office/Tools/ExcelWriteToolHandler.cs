using System;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.Office.Runtime;

namespace RNAssistant.Office.Tools
{
    internal sealed class ExcelWriteToolHandler : IManagedMutationToolHandler
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
                return OfficeToolFailure.Rejected(
                    "Excel writes require an active chat session.",
                    "excel_write_session_required");
            try
            {
                var outcome = _runtime.ExecuteDocumentMutation(Target(_session), cancellationToken, delegate
                {
                    return _adapter.Execute(context.Arguments,
                        context.MarkDispatchPossible, cancellationToken);
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
                RuntimeDocumentKey = session.LastRun == null ? string.Empty : session.LastRun.DocumentRuntimeKey
            };
        }
    }
}
