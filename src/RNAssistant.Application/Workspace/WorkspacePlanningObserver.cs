using System.Collections.Generic;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Runtime
{
    // ToolRuntime serializes writes. Files retain their own source-side journal.
    internal sealed class WorkspacePlanningObserver : IToolMutationObserver
    {
        private readonly WorkspaceTaskListObserver _tasks;
        private readonly WorkspacePlanService _plans;
        private IToolMutationObserver _current;
        internal WorkspacePlanningObserver(WorkspaceTaskListObserver tasks, WorkspacePlanService plans)
        { _tasks = tasks; _plans = plans; }
        public string Prepare(ToolExecutionContext context, IDictionary<string, object> arguments, string preparedStateJson = null)
        {
            _current = PlanDocumentToolCatalog.Owns(context.Call.Name) ? (IToolMutationObserver)_plans : _tasks;
            return _current.Prepare(context, arguments, preparedStateJson);
        }
        public void MarkDispatchMayHaveOccurred(string id) { _current.MarkDispatchMayHaveOccurred(id); }
        public ResourceAuthorityCommit Complete(string id, ToolExecutionRecord record, string preparedStateJson = null)
        { return _current.Complete(id, record, preparedStateJson); }
        public void AbandonBeforeDispatch(string id) { _current.AbandonBeforeDispatch(id); }
        public void ReleaseUnresolved(string id) { _current.ReleaseUnresolved(id); }
    }
}
