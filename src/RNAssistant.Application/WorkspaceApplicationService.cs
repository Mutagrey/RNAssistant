using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    public sealed class WorkspaceApplicationService : IDisposable
    {
        private readonly object _sync = new object();
        private readonly WorkspaceStore _workspaces;
        private readonly WorkspaceFileService _files;
        private readonly TabularProjectStore _projects;
        private readonly AppDataPaths _paths;
        private WorkspaceSourceQueue _sources;
        private bool _disposed;
        private WorkspaceDescriptor _workspace;
        private TabularProjectSnapshot _project;
        private string _bindingId;
        private long _revision;

        public WorkspaceApplicationService(AppDataPaths paths, WorkspaceFileService files = null)
        {
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            _paths = paths;
            _workspaces = new WorkspaceStore(paths);
            _files = files ?? new WorkspaceFileService(paths);
            _projects = new TabularProjectStore(_files);
        }

        public WorkspaceStateDto ReadState()
        { lock (_sync) return State(); }

        public void Reset()
        {
            lock (_sync) { RequireIdleSources(); _workspace = null; _bindingId = null; ClearProject(); }
        }

        public WorkspaceStateDto OpenWorkspace(OpenWorkspaceRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            lock (_sync)
            {
                RequireIdleSources();
                // Do not discard a usable selection if association fails.
                var opened = _workspaces.Open(request.RootPath, false, !request.ReadOnly);
                _workspace = opened;
                _bindingId = Guid.NewGuid().ToString("N");
                ClearProject();
                return State();
            }
        }

        public WorkspaceStateDto CreateTabularProject(CreateTabularProjectRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            lock (_sync)
            {
                RequireBinding(request.BindingId);
                RequireIdleSources();
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
                RequireIdleSources();
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
                RequireIdleSources();
                ClearProject();
                var result = _files.ReconcileUncertain(_workspace, request.ManifestPath);
                // Inspection resolves the journal only. It never repeats creation
                // or claims that the previous attempt succeeded.
                return new WorkspaceReconcileResponse { State = State(), Exists = result.Exists, Outcome = result.Outcome };
            }
        }

        public WorkspaceStateDto SaveTabularProjectSources(SaveTabularProjectSourcesRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            lock (_sync)
            {
                RequireBinding(request.BindingId);
                RequireIdleSources();
                if (_project == null || request.ProjectRevision != _revision || request.ManifestPath != _project.ManifestPath)
                    throw new WorkspaceApplicationException("project_selection_changed", "Проект изменился. Обновите его состояние.");
                var expected = _project;
                // Revoke the old selection before any possible effect. A lost/unknown
                // write cannot leave a stale project or prepared queue usable.
                ClearProject();
                _project = _projects.SaveSources(_workspace, expected, request.Paths);
                return State();
            }
        }

        public TabularProjectDraftSaveDto SaveTabularProjectDraft(SaveTabularProjectDraftRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            lock (_sync)
            {
                var expected = RequireSelectedProject(request);
                RequireIdleSources();
                // An unverified create must revoke the selection so the same UI
                // request cannot be silently retried as a second snapshot.
                ClearProject();
                var draft = _projects.SaveDraft(_workspace, expected, request.File, request.Input);
                _project = expected;
                return new TabularProjectDraftSaveDto { State = State(), Draft = DraftDto(draft) };
            }
        }

        public TabularProjectDraftListDto ListTabularProjectDrafts(WorkspaceSelectedProjectRequest request,
            CancellationToken token = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            WorkspaceDescriptor workspace;
            TabularProjectSnapshot project;
            lock (_sync) { project = RequireSelectedProject(request); workspace = _workspace; }
            var drafts = _projects.ListDrafts(workspace, project, token);
            lock (_sync)
            {
                RequireSelectedProject(request);
                return new TabularProjectDraftListDto { State = State(), Items = drafts.Select(DraftDto).ToArray() };
            }
        }

        public TabularProjectDraftContentDto OpenTabularProjectDraft(OpenTabularProjectDraftRequest request,
            CancellationToken token = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            WorkspaceDescriptor workspace;
            TabularProjectSnapshot project;
            lock (_sync) { project = RequireSelectedProject(request); workspace = _workspace; }
            var draft = _projects.ReadDraft(workspace, project, request.File, token);
            lock (_sync)
            {
                RequireSelectedProject(request);
                return new TabularProjectDraftContentDto { State = State(), Draft = DraftDto(draft), Input = draft.Input };
            }
        }

        public TabularProjectTemplateListDto ListTabularProjectTemplates(ListTabularProjectTemplatesRequest request,
            CancellationToken token = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.Offset < 0 || request.Offset > 1000)
                throw new WorkspaceApplicationException("template_offset_invalid", "Укажите смещение шаблонов от 0 до 1000.");
            WorkspaceDescriptor workspace;
            TabularProjectSnapshot project;
            lock (_sync) { project = RequireSelectedProject(request); workspace = _workspace; }
            var heads = _projects.ListTemplateHeads(workspace, project, token);
            lock (_sync)
            {
                RequireSelectedProject(request);
                return new TabularProjectTemplateListDto { State = State(), Offset = request.Offset,
                    Total = heads.Count, Items = heads.Skip(request.Offset).Take(50).Select(item =>
                        new TabularProjectTemplateHeadDto { Id = item.Id, HeadFile = item.HeadFile,
                            SavedAt = item.SavedAt, Archived = item.Archived, Versions = item.Versions }).ToArray() };
            }
        }

        public TabularProjectTemplateContentDto OpenTabularProjectTemplate(OpenTabularProjectTemplateRequest request,
            CancellationToken token = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            WorkspaceDescriptor workspace;
            TabularProjectSnapshot project;
            lock (_sync) { project = RequireSelectedProject(request); workspace = _workspace; }
            var file = _projects.ReadTemplate(workspace, project, request.Id, request.HeadFile, token);
            lock (_sync)
            {
                RequireSelectedProject(request);
                return new TabularProjectTemplateContentDto { State = State(), Id = request.Id,
                    HeadFile = request.HeadFile, Template = FileDto(file) };
            }
        }

        public TabularProjectDictionaryContentDto ReadTabularProjectDictionary(WorkspaceSelectedProjectRequest request,
            CancellationToken token = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            WorkspaceDescriptor workspace;
            TabularProjectSnapshot project;
            lock (_sync) { project = RequireSelectedProject(request); workspace = _workspace; }
            var file = _projects.ReadDictionary(workspace, project, token);
            lock (_sync)
            {
                RequireSelectedProject(request);
                return new TabularProjectDictionaryContentDto { State = State(),
                    Dictionary = file == null ? null : FileDto(file) };
            }
        }

        private TabularProjectSnapshot RequireSelectedProject(WorkspaceSelectedProjectRequest request)
        {
            RequireBinding(request.BindingId);
            if (_project == null || request.ProjectRevision != _revision || request.ManifestPath != _project.ManifestPath)
                throw new WorkspaceApplicationException("project_selection_changed", "Проект изменился. Обновите его состояние.");
            return _project;
        }

        private static TabularProjectDraftDto DraftDto(TabularProjectDraft draft)
        {
            return new TabularProjectDraftDto { File = draft.FileName, SavedAt = draft.SavedAt,
                Label = draft.Label, ByteLength = draft.ByteLength };
        }

        private static TabularProjectFileDto FileDto(TabularProjectStoredText file)
        {
            return new TabularProjectFileDto { File = file.FileName, Format = file.Format,
                Sha256 = file.Sha256, Input = file.Input };
        }

        private void RequireBinding(string bindingId)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WorkspaceApplicationService));
            if (_workspace == null || string.IsNullOrEmpty(bindingId) ||
                !string.Equals(_bindingId, bindingId, StringComparison.Ordinal))
                throw new WorkspaceApplicationException("workspace_binding_changed",
                    "Рабочая папка изменилась. Обновите её состояние перед следующим действием.");
        }

        public WorkspaceSourceQueuePage PrepareSources(PrepareWorkspaceSourcesRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            lock (_sync)
            {
                RequireBinding(request.BindingId);
                RequireIdleSources();
                if (_project == null || request.ProjectRevision != _revision)
                    throw new WorkspaceApplicationException("project_selection_changed", "Проект изменился. Обновите его состояние.");
                var queue = new WorkspaceSourceQueue(_paths, _files, _projects, _workspace, _project, request);
                _sources?.Dispose();
                _sources = queue;
                queue.Start();
                return queue.Read(new WorkspaceSourcePageRequest { QueueId = queue.Id });
            }
        }

        public WorkspaceFilesPage BrowseFiles(BrowseWorkspaceFilesRequest request, CancellationToken token = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            WorkspaceDescriptor workspace;
            var binding = request.BindingId;
            lock (_sync) { RequireBinding(binding); workspace = _workspace; }
            var directory = (request.Directory ?? string.Empty).Replace('\\', '/');
            if (directory.StartsWith("/", StringComparison.Ordinal))
                throw new WorkspaceFileException("path_outside_mount", "Укажите относительный путь внутри рабочей папки.");
            directory = directory.TrimEnd('/');
            // Metadata I/O must not hold the application lock used by queue controls.
            var page = _files.ListPage(workspace, directory, request.Query, 50, request.Offset, token);
            lock (_sync) RequireBinding(binding);
            return new WorkspaceFilesPage { BindingId = binding, Directory = directory,
                Offset = page.Offset, Matched = page.MatchedEntries, ScanLimitReached = page.ScanLimitReached,
                Items = page.Entries.Select(entry => new WorkspaceFileEntryDto { Name = entry.Name,
                    Path = string.IsNullOrEmpty(directory) ? entry.Name : directory + "/" + entry.Name,
                    IsDirectory = entry.IsDirectory, ByteLength = entry.ByteLength }).ToArray() };
        }

        public WorkspaceSourceQueuePage ReadSourceQueue(WorkspaceSourcePageRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            lock (_sync) return RequireQueue(request.QueueId).Read(request);
        }

        public WorkspaceSourceQueuePage ControlSourceQueue(WorkspaceSourceControlRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            lock (_sync)
            {
                var queue = RequireQueue(request.QueueId);
                queue.Control(request);
                return queue.Read(new WorkspaceSourcePageRequest { QueueId = queue.Id });
            }
        }

        public Task<WorkspaceSourceReader> OpenPreparedSourceAsync(string queueId, string itemId,
            CancellationToken token = default(CancellationToken), Action<FileTransferProgress> progress = null)
        {
            // Admission pins the selection before the background open can begin.
            lock (_sync) return RequireQueue(queueId).OpenPreparedAsync(itemId, token, progress);
        }

        public Task CloseAsync()
        {
            lock (_sync)
            {
                _disposed = true;
                _sources?.Dispose();
                return _sources?.Drained ?? Task.CompletedTask;
            }
        }
        public void Dispose() { CloseAsync(); }

        public void EnsureNoSourceWork()
        { lock (_sync) RequireIdleSources(); }

        private WorkspaceSourceQueue RequireQueue(string id)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WorkspaceApplicationService));
            if (_sources == null || _sources.Id != id)
                throw new WorkspaceApplicationException("source_queue_changed", "Очередь изменилась. Обновите состояние проекта.");
            return _sources;
        }

        private void RequireIdleSources()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WorkspaceApplicationService));
            if (_sources?.HasUnfinished == true)
                throw new WorkspaceApplicationException("source_queue_active", "Завершите работу с источниками перед сменой проекта.");
        }

        private void ClearProject()
        { _sources?.Dispose(); _sources = null; _project = null; _revision++; }

        private WorkspaceStateDto State()
        {
            return new WorkspaceStateDto { Revision = _revision, SourceQueueId = _sources?.Id,
                Workspace = _workspace == null ? null : new WorkspaceSelectionDto {
                    BindingId = _bindingId, WorkspaceId = _workspace.WorkspaceId, Name = _workspace.Name,
                    RootPath = _workspace.RootPath, ReadOnly = _workspace.ReadOnly },
                Project = _project == null ? null : new WorkspaceProjectDto {
                    Name = _project.Manifest.Name, ManifestPath = _project.ManifestPath,
                    FormatVersion = _project.Manifest.FormatVersion,
                    Sources = _project.Manifest.Sources.Select(source => new TabularProjectSourceDto {
                        SourceId = source.SourceId, Path = source.Path }).ToArray() } };
        }
    }
}
