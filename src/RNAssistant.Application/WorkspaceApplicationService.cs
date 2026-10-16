using System;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Application
{
    public sealed class WorkspaceApplicationException : InvalidOperationException
    {
        public string Code { get; private set; }
        public WorkspaceApplicationException(string code, string message) : base(message) { Code = code; }
    }

    // One opened-workspace context per application view/CLI invocation. This is
    // ephemeral UI/application state, independent of Office controllers and chats.
    public sealed class WorkspaceApplicationService
    {
        private readonly object _sync = new object();
        private readonly WorkspaceStore _workspaces;
        private readonly WorkspaceFileService _files;
        private readonly TabularProjectStore _projects;
        private WorkspaceDescriptor _workspace;
        private TabularProjectSnapshot _project;
        private string _bindingId;
        private long _revision;

        public WorkspaceApplicationService(AppDataPaths paths, WorkspaceFileService files = null)
        {
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            _workspaces = new WorkspaceStore(paths);
            _files = files ?? new WorkspaceFileService(paths);
            _projects = new TabularProjectStore(_files);
        }

        public WorkspaceStateDto ReadState()
        { lock (_sync) return State(); }

        public void Reset()
        {
            lock (_sync) { _workspace = null; _bindingId = null; ClearProject(); }
        }

        public WorkspaceStateDto OpenWorkspace(OpenWorkspaceRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            lock (_sync)
            {
                // Do not discard a usable selection if association fails.
                var opened = _workspaces.Open(request.RootPath, false, !request.ReadOnly);
                _workspace = opened;
                _bindingId = Guid.NewGuid().ToString("N");
                _project = null;
                _revision++;
                return State();
            }
        }

        public WorkspaceStateDto CreateTabularProject(CreateTabularProjectRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            lock (_sync)
            {
                RequireBinding(request.BindingId);
                ClearProject();
                _project = _projects.Create(_workspace, request.ManifestPath, request.Name);
                return State();
            }
        }

        public WorkspaceStateDto OpenTabularProject(WorkspaceProjectPathRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            lock (_sync)
            {
                RequireBinding(request.BindingId);
                ClearProject();
                _project = _projects.Open(_workspace, request.ManifestPath);
                return State();
            }
        }

        public WorkspaceReconcileResponse ReconcileFile(WorkspaceProjectPathRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            lock (_sync)
            {
                RequireBinding(request.BindingId);
                ClearProject();
                var result = _files.ReconcileUncertain(_workspace, request.ManifestPath);
                // Inspection resolves the journal only. It never repeats creation
                // or claims that the previous attempt succeeded.
                return new WorkspaceReconcileResponse { State = State(), Exists = result.Exists, Outcome = result.Outcome };
            }
        }

        private void RequireBinding(string bindingId)
        {
            if (_workspace == null || string.IsNullOrEmpty(bindingId) ||
                !string.Equals(_bindingId, bindingId, StringComparison.Ordinal))
                throw new WorkspaceApplicationException("workspace_binding_changed",
                    "Рабочая папка изменилась. Обновите её состояние перед следующим действием.");
        }

        private void ClearProject() { _project = null; _revision++; }

        private WorkspaceStateDto State()
        {
            return new WorkspaceStateDto { Revision = _revision,
                Workspace = _workspace == null ? null : new WorkspaceSelectionDto {
                    BindingId = _bindingId, WorkspaceId = _workspace.WorkspaceId, Name = _workspace.Name,
                    RootPath = _workspace.RootPath, ReadOnly = _workspace.ReadOnly },
                Project = _project == null ? null : new WorkspaceProjectDto {
                    Name = _project.Manifest.Name, ManifestPath = _project.ManifestPath,
                    FormatVersion = _project.Manifest.FormatVersion } };
        }
    }
}
