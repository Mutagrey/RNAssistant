using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;

namespace RNAssistant.Runtime
{
    // ToolRuntime serializes writes. Files retain their own source-side journal.
    internal sealed class WorkspacePlanningObserver : IToolMutationObserver
    {
        private readonly WorkspaceTaskListObserver _tasks;
        private readonly WorkspacePlanService _plans;
        private readonly IToolMutationObserver _office;
        private readonly HashSet<string> _officeToolIds;
        private IToolMutationObserver _current;
        internal WorkspacePlanningObserver(WorkspaceTaskListObserver tasks, WorkspacePlanService plans,
            IToolMutationObserver office = null, IEnumerable<string> officeToolIds = null)
        { _tasks = tasks; _plans = plans; _office = office;
            _officeToolIds = new HashSet<string>(officeToolIds ?? Enumerable.Empty<string>(), System.StringComparer.Ordinal); }
        public string Prepare(ToolExecutionContext context, IDictionary<string, object> arguments, string preparedStateJson = null)
        {
            _current = ResourceMutationToolCatalog.Owns(context.Call.Name) && arguments.ContainsKey("target") && PlanSourceDocument.IsTarget(System.Convert.ToString(arguments["target"])) ? (IToolMutationObserver)_plans :
                _office != null && _officeToolIds.Contains(context.Call.Name) &&
                    (!ResourceMutationToolCatalog.Owns(context.Call.Name) || arguments.ContainsKey("target") && System.Convert.ToString(arguments["target"]).Contains(": ")) ? _office : _tasks;
            return _current.Prepare(context, arguments, preparedStateJson);
        }
        public void MarkDispatchMayHaveOccurred(string id) { _current.MarkDispatchMayHaveOccurred(id); }
        public ResourceAuthorityCommit Complete(string id, ToolExecutionRecord record, string preparedStateJson = null)
        { return _current.Complete(id, record, preparedStateJson); }
        public void AbandonBeforeDispatch(string id) { _current.AbandonBeforeDispatch(id); }
        public void ReleaseUnresolved(string id) { _current.ReleaseUnresolved(id); }
    }
}
