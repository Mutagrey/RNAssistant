using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Tools;
using RNAssistant.Runtime.Contracts;

namespace RNAssistant.Office.Services
{
    // A workspace run borrows one exact live Office document. Its chat identity
    // and durable file authority remain with the workspace owner.
    internal sealed class WorkspaceOfficeCapabilityService
    {
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly OfficeToolExecutor _executor;
        private readonly AppDataPaths _paths;

        internal WorkspaceOfficeCapabilityService(IOfficeApplicationAdapter adapter,
            OfficeToolExecutor executor, AppDataPaths paths)
        { _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _paths = paths ?? throw new ArgumentNullException(nameof(paths)); }

        internal WorkspaceOfficeRunTarget CaptureCurrentTarget()
        {
            var bound = (_adapter as IOfficeDocumentSessionProvider)?.DocumentSession;
            var runtime = bound == null ? _adapter.RuntimeDocumentKey : bound.RuntimeDocumentId;
            if (string.IsNullOrWhiteSpace(_adapter.HostName) ||
                string.IsNullOrWhiteSpace(_adapter.DocumentKey) && string.IsNullOrWhiteSpace(runtime))
                return null;
            var expected = new OfficeDocumentExecutionExpectation { Host = _adapter.HostName,
                DocumentKey = _adapter.DocumentKey, RuntimeDocumentKey = runtime };
            if (OfficeDocumentExecutionGuardState.Validate(_adapter, expected) != null) return null;
            var target = new WorkspaceOfficeRunTarget { Host = _adapter.HostName,
                DocumentKey = _adapter.DocumentKey, RuntimeDocumentKey = runtime,
                DocumentTitle = _adapter.DocumentTitle };
            var binding = OfficeSession(target, null, null);
            _executor.BindResourceAuthority(binding);
            target.DocumentAuthorityId = binding.DocumentAuthorityId;
            return target;
        }

        internal WorkspaceOfficeCapabilitySet ForTarget(WorkspaceOfficeRunTarget target,
            ChatSession workspaceSession, AppSettings settings, string runId)
        {
            if (target == null || workspaceSession == null || string.IsNullOrWhiteSpace(runId) ||
                string.IsNullOrWhiteSpace(target.Host) || string.IsNullOrWhiteSpace(target.DocumentAuthorityId) ||
                string.IsNullOrWhiteSpace(target.DocumentKey) && string.IsNullOrWhiteSpace(target.RuntimeDocumentKey))
                throw new InvalidOperationException("The workspace run has no exact Office target.");
            var available = string.Equals(_adapter.HostName, target.Host, StringComparison.OrdinalIgnoreCase) &&
                OfficeDocumentExecutionGuardState.Validate(_adapter, new OfficeDocumentExecutionExpectation
                { Host = target.Host, DocumentKey = target.DocumentKey,
                    RuntimeDocumentKey = target.RuntimeDocumentKey }) == null;
            // Preserve the accepted catalog across a host switch so pending
            // confirmation can finish with a definite no-effect refusal.
            var tools = OfficeToolCatalog.ForHost(target.Host)
                .Where(item => item.Id != "excel.chat.chart.create").ToArray();
            var result = new WorkspaceOfficeCapabilitySet { Target = target, Available = available,
                Tools = new WorkspaceToolContribution[0], ModelProjection = ModelToolResultProjection.Instance };
            if (!string.Equals(_adapter.HostName, target.Host, StringComparison.OrdinalIgnoreCase))
            {
                result.Tools = tools.Select(item => new WorkspaceToolContribution { Entry = item,
                    Handler = item.Policy.ExecutionClass == ToolExecutionClass.ReadOnly
                        ? (IToolHandler)new UnavailableOfficeRead()
                        : item.Policy.ExecutionClass == ToolExecutionClass.OpaqueAction
                            ? (IToolHandler)new UnavailableOfficeAction()
                            : new UnavailableOfficeMutation() }).ToArray();
                result.ResourceFind = new UnavailableOfficeRead();
                result.ResourceRead = new UnavailableOfficeRead();
                result.ResourceSearch = new UnavailableOfficeRead();
                result.MutationObserver = new UnavailableOfficeMutationObserver();
                return result;
            }

            var session = OfficeSession(target, workspaceSession, runId);
            var resourceTools = ResourceToolCatalog.GetControllerTools()
                .Where(item => item.Id == ResourceToolCatalog.FindToolId ||
                    item.Id == ResourceToolCatalog.ReadToolId || item.Id == ResourceToolCatalog.SearchToolId).ToArray();
            var native = _executor.CreateNativeRuntime(session, tools.Concat(resourceTools),
                settings ?? new AppSettings(), ChatModes.Agent, trace: false, manualRun: true);
            result.Tools = tools.Select(item => new WorkspaceToolContribution
                { Entry = item, Handler = native.HandlerFor(item.Id) }).ToArray();
            result.ResourceFind = native.HandlerFor(ResourceToolCatalog.FindToolId) as IReadOnlyToolHandler;
            result.ResourceRead = native.HandlerFor(ResourceToolCatalog.ReadToolId) as IReadOnlyToolHandler;
            result.ResourceSearch = native.HandlerFor(ResourceToolCatalog.SearchToolId) as IReadOnlyToolHandler;
            if (result.Tools.Any(item => item.Handler == null) || result.ResourceFind == null || result.ResourceRead == null || result.ResourceSearch == null)
                throw new InvalidOperationException("The bound Office tool handlers are incomplete.");
            result.MutationObserver = new ResourceMutationAuthorityObserver(
                _executor.ResourceAuthority, new ResourceMutationJournal(_paths), session, _executor.Payloads);
            return result;
        }

        private static ChatSession OfficeSession(WorkspaceOfficeRunTarget target,
            ChatSession workspaceSession, string runId)
        {
            return new ChatSession { Id = workspaceSession?.Id ?? Guid.NewGuid().ToString("N"),
                Host = target.Host, DocumentKey = target.DocumentKey, DocumentTitle = target.DocumentTitle,
                DocumentAuthorityId = target.DocumentAuthorityId,
                LastRun = runId == null ? null : new ChatRunRecord
                { RunId = runId, TurnId = runId, DocumentRuntimeKey = target.RuntimeDocumentKey },
                LastContextReceipt = workspaceSession?.LastContextReceipt,
                Messages = workspaceSession?.Messages ?? new List<ChatMessage>() };
        }
    }

    internal sealed class UnavailableOfficeRead : IReadOnlyToolHandler
    {
        public Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken cancellationToken)
        { return OfficeToolFailure.Rejected("The Office document pinned to this run is unavailable. No Office action was started.",
            "active_document_changed"); }
    }

    internal sealed class UnavailableOfficeMutation : IManagedMutationToolHandler
    {
        public Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken cancellationToken)
        { return OfficeToolFailure.Rejected("The Office document pinned to this run is unavailable. No Office action was started.",
            "active_document_changed"); }
    }

    internal sealed class UnavailableOfficeAction : IOpaqueActionToolHandler
    {
        public Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken cancellationToken)
        { return OfficeToolFailure.Rejected("The Office document pinned to this run is unavailable. No Office action was started.",
            "active_document_changed"); }
    }

    internal sealed class UnavailableOfficeMutationObserver : IToolMutationObserver
    {
        public string Prepare(ToolExecutionContext context, IDictionary<string, object> arguments, string preparedStateJson = null)
        { throw new ToolMutationPreparationException("active_document_changed",
            "The Office document pinned to this run is unavailable. No Office action was started."); }
        public void MarkDispatchMayHaveOccurred(string id) { throw new InvalidOperationException("No Office mutation was prepared."); }
        public ResourceAuthorityCommit Complete(string id, ToolExecutionRecord record, string preparedStateJson = null)
        { throw new InvalidOperationException("No Office mutation was prepared."); }
        public void AbandonBeforeDispatch(string id) { }
        public void ReleaseUnresolved(string id) { }
    }
}
