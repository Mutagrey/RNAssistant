using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Application
{
    // Source preparation only. No parser/model lifecycle or durable status store.
    // The workspace application owns this bounded projection and its worker.
    internal sealed class WorkspaceSourceQueue : IDisposable
    {
        internal const int MaximumItems = 500;
        private readonly object _sync = new object();
        private readonly AppDataPaths _paths;
        private readonly WorkspaceFileService _files;
        private readonly TabularProjectStore _projects;
        private readonly WorkspaceDescriptor _workspace;
        private readonly TabularProjectSnapshot _project;
        private readonly string _binding;
        private readonly long _projectRevision;
        private readonly long _maximumFileBytes, _maximumBatchBytes;
        private readonly List<Item> _items;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private Task _worker = Task.CompletedTask;
        private CancellationTokenSource _currentCancellation;
        private CancellationTokenSource _pumpCancellation;
        private bool _pumpRunning, _paused, _disposed;
        private long _revision, _capturedBytes;
        private string _errorCode, _errorMessage;
        internal string Id { get; } = "sq_" + Guid.NewGuid().ToString("N");

        private sealed class Item
        {
            internal string Id, Path, ErrorCode, ErrorMessage;
            internal WorkspaceSourceState State;
            internal WorkspaceSourcePhase? Phase;
            internal WorkspaceFileSnapshot Snapshot;
            internal FileTransferProgress Progress;
        }

        internal WorkspaceSourceQueue(AppDataPaths paths, WorkspaceFileService files, TabularProjectStore projects,
            WorkspaceDescriptor workspace, TabularProjectSnapshot project, PrepareWorkspaceSourcesRequest request)
        {
            if (request.Paths == null || request.Paths.Count == 0 || request.Paths.Count > MaximumItems ||
                request.MaximumFileBytes < 1 || request.MaximumBatchBytes < request.MaximumFileBytes)
                throw Invalid("Укажите 1–500 файлов и положительные лимиты объёма файла и очереди.");
            var names = request.Paths.Select(path => (path ?? string.Empty).Replace('\\', '/')).ToArray();
            if (names.Any(path => string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Any(char.IsControl)) ||
                names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
                throw Invalid("Пути должны быть непустыми и не повторяться в очереди.");
            _paths = paths; _files = files; _projects = projects; _workspace = workspace; _project = project;
            _binding = request.BindingId; _projectRevision = request.ProjectRevision;
            _maximumFileBytes = request.MaximumFileBytes; _maximumBatchBytes = request.MaximumBatchBytes;
            _items = names.Select(path => new Item { Id = "si_" + Guid.NewGuid().ToString("N"), Path = path }).ToList();
        }

        internal bool HasUnfinished { get { lock (_sync) return _pumpRunning || _items.Any(item => item.State == WorkspaceSourceState.Pending); } }
        internal Task Drained { get { lock (_sync) return _worker; } }
        internal void Start() { lock (_sync) Schedule(); }

        internal WorkspaceSourceQueuePage Read(WorkspaceSourcePageRequest request)
        {
            if (request.Offset < 0 || request.Limit < 1 || request.Limit > 50 || request.Query?.Length > 128 ||
                request.Query != null && request.Query.Any(char.IsControl) || !Enum.IsDefined(typeof(WorkspaceSourceSort), request.Sort) ||
                request.State.HasValue && !Enum.IsDefined(typeof(WorkspaceSourceState), request.State.Value))
                throw Invalid("Недопустимые параметры страницы очереди.");
            lock (_sync)
            {
                IEnumerable<Item> matches = _items.Where(item =>
                    (!request.State.HasValue || item.State == request.State.Value) &&
                    (string.IsNullOrWhiteSpace(request.Query) || item.Path.IndexOf(request.Query, StringComparison.OrdinalIgnoreCase) >= 0));
                if (request.Sort == WorkspaceSourceSort.Path) matches = matches.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase);
                if (request.Sort == WorkspaceSourceSort.LargestFirst) matches = matches.OrderByDescending(item => item.Snapshot?.ByteLength ?? item.Progress?.TotalBytes ?? -1);
                var selected = matches.ToArray();
                var pending = _items.Count(item => item.State == WorkspaceSourceState.Pending);
                var running = _items.Count(item => item.State == WorkspaceSourceState.Running);
                var offset = selected.Length == 0 ? 0 : Math.Min(request.Offset, (selected.Length - 1) / request.Limit * request.Limit);
                return new WorkspaceSourceQueuePage {
                    QueueId = Id, Revision = _revision, BindingId = _binding, ProjectRevision = _projectRevision,
                    ManifestPath = _project.ManifestPath, Total = _items.Count, Pending = pending, Running = running,
                    Prepared = _items.Count(item => item.State == WorkspaceSourceState.Prepared),
                    Failed = _items.Count(item => item.State == WorkspaceSourceState.Failed),
                    Cancelled = _items.Count(item => item.State == WorkspaceSourceState.Cancelled),
                    State = !_pumpRunning && pending == 0 ? WorkspaceSourceQueueState.Finished
                        : _paused ? (running > 0 ? WorkspaceSourceQueueState.Pausing : WorkspaceSourceQueueState.Paused)
                        : WorkspaceSourceQueueState.Running,
                    CapturedBytes = _capturedBytes, MaximumFileBytes = _maximumFileBytes, MaximumBatchBytes = _maximumBatchBytes,
                    ErrorCode = _errorCode, ErrorMessage = _errorMessage, Matched = selected.Length, Offset = offset,
                    Items = selected.Skip(offset).Take(request.Limit).Select(item => new WorkspaceSourceItemDto {
                        Id = item.Id, Path = item.Path, State = item.State, Phase = item.Phase, ByteLength = item.Snapshot?.ByteLength,
                        ErrorCode = item.ErrorCode, ErrorMessage = item.ErrorMessage,
                        Progress = item.Progress == null ? null : new WorkspaceSourceProgressDto {
                            Stage = item.Progress.Stage, CompletedBytes = item.Progress.CompletedBytes, TotalBytes = item.Progress.TotalBytes }
                    }).ToArray() };
            }
        }

        internal void Control(WorkspaceSourceControlRequest request)
        {
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(WorkspaceSourceQueue));
                switch (request.Command)
                {
                    case WorkspaceSourceCommand.Pause: _paused = true; break;
                    case WorkspaceSourceCommand.Resume:
                        if (_errorCode == "project_changed" || _errorCode == "project_workspace_mismatch" || _errorCode == "project_unavailable")
                            throw new WorkspaceApplicationException("source_queue_reopen_project", "Отмените оставшиеся файлы и заново откройте изменившийся проект.");
                        _paused = false; _errorCode = _errorMessage = null; break;
                    case WorkspaceSourceCommand.CancelCurrent: _currentCancellation?.Cancel(); break;
                    case WorkspaceSourceCommand.CancelPending:
                        CancelPending(); if (_currentCancellation == null) _pumpCancellation?.Cancel(); break;
                    case WorkspaceSourceCommand.CancelAll: CancelPending(); _pumpCancellation?.Cancel(); break;
                    case WorkspaceSourceCommand.RetrySelected:
                        var ids = request.ItemIds;
                        if (ids == null || ids.Count == 0 || ids.Count > MaximumItems || ids.Distinct().Count() != ids.Count ||
                            ids.Any(id => !_items.Any(item => item.Id == id &&
                                (item.State == WorkspaceSourceState.Failed || item.State == WorkspaceSourceState.Cancelled))))
                            throw Invalid("Для повтора выберите только завершившиеся с ошибкой или отменённые файлы.");
                        foreach (var item in _items.Where(item => ids.Contains(item.Id)))
                        { item.State = WorkspaceSourceState.Pending; item.ErrorCode = item.ErrorMessage = null; item.Progress = null; }
                        break;
                    default: throw Invalid("Неизвестная команда очереди.");
                }
                _revision++;
                Schedule();
            }
        }

        internal Stream OpenPrepared(string itemId, CancellationToken token)
        {
            WorkspaceFileSnapshot source;
            lock (_sync)
            {
                var item = _items.SingleOrDefault(candidate => candidate.Id == itemId);
                if (_disposed || item?.State != WorkspaceSourceState.Prepared)
                    throw Invalid("Источник не подготовлен или очередь уже закрыта.");
                source = item.Snapshot;
            }
            // No UI-supplied path/ref may replace the accepted runtime pin.
            return _files.OpenExactRead(_workspace, source.RelativePath, source.Reference, _maximumFileBytes, token);
        }

        private void Schedule()
        {
            if (_disposed || _paused || _pumpRunning || !_items.Any(item => item.State == WorkspaceSourceState.Pending)) return;
            _pumpRunning = true;
            _pumpCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _worker = Task.Run((Action)Pump);
        }

        private void Pump()
        {
            try
            {
                using (RuntimeStorageActivity.Acquire(_paths, _pumpCancellation.Token))
                {
                    while (true)
                    {
                        Item item;
                        CancellationToken token;
                        long budget;
                        lock (_sync)
                        {
                            if (_disposed || _paused || _pumpCancellation.IsCancellationRequested) return;
                            item = _items.FirstOrDefault(candidate => candidate.State == WorkspaceSourceState.Pending);
                            if (item == null) return;
                            item.State = WorkspaceSourceState.Running;
                            item.Phase = WorkspaceSourcePhase.WaitingForReader;
                            _currentCancellation = CancellationTokenSource.CreateLinkedTokenSource(_pumpCancellation.Token);
                            token = _currentCancellation.Token;
                            budget = Math.Min(_maximumFileBytes, _maximumBatchBytes - _capturedBytes);
                            _revision++;
                        }
                        try
                        {
                            token.ThrowIfCancellationRequested();
                            WorkspaceFileSnapshot captured;
                            using (RuntimeStorageActivity.AcquireSourceReader(_paths, token))
                            {
                                lock (_sync) { item.Phase = WorkspaceSourcePhase.CheckingProject; _revision++; }
                                _projects.RequireUnchanged(_workspace, _project, token);
                                if (budget <= 0) throw new WorkspaceFileException("source_batch_limit", "Достигнут лимит объёма этой очереди.");
                                lock (_sync) { item.Phase = WorkspaceSourcePhase.Capturing; _revision++; }
                                captured = _files.CaptureFile(_workspace, item.Path, budget, token, progress => {
                                    lock (_sync) { item.Progress = progress; _revision++; }
                                });
                                lock (_sync) { _capturedBytes += captured.ByteLength; item.Phase = WorkspaceSourcePhase.CheckingProject; _revision++; }
                                _projects.RequireUnchanged(_workspace, _project, token);
                            }
                            lock (_sync)
                            {
                                if (token.IsCancellationRequested) item.State = WorkspaceSourceState.Cancelled;
                                else { item.Snapshot = captured; item.State = WorkspaceSourceState.Prepared; }
                            }
                        }
                        catch (OperationCanceledException)
                        { lock (_sync) item.State = WorkspaceSourceState.Cancelled; }
                        catch (Exception error)
                        {
                            lock (_sync)
                            {
                                var checkingProject = item.Phase == WorkspaceSourcePhase.CheckingProject;
                                var missingProject = checkingProject && IsMissing(error);
                                item.State = WorkspaceSourceState.Failed;
                                item.ErrorCode = missingProject ? "project_unavailable" : Code(error);
                                item.ErrorMessage = missingProject ? "Файл проекта недоступен. Заново откройте проект перед продолжением." : Message(error);
                                if (checkingProject || !IsLocalFailure(error))
                                { _paused = true; _errorCode = item.ErrorCode; _errorMessage = item.ErrorMessage; }
                            }
                        }
                        finally
                        {
                            lock (_sync) { item.Phase = null; _currentCancellation.Dispose(); _currentCancellation = null; _revision++; }
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            { lock (_sync) { _paused = true; _errorCode = Code(error); _errorMessage = Message(error); _revision++; } }
            finally
            {
                lock (_sync)
                {
                    _pumpRunning = false;
                    _pumpCancellation.Dispose(); _pumpCancellation = null;
                    _revision++;
                    // Resume may arrive while the preceding worker is releasing its lease.
                    Schedule();
                    if (_disposed) _lifetime.Dispose();
                }
            }
        }

        private static bool IsLocalFailure(Exception error)
        {
            var code = (error as WorkspaceFileException)?.Code;
            return IsMissing(error) ||
                code == "file_too_large" || code == "source_batch_limit" || code == "path_outside_mount" ||
                code == "protected_path" || code == "target_conflict";
        }
        private static bool IsMissing(Exception error)
        { return error is FileNotFoundException || error is DirectoryNotFoundException; }
        private static string Code(Exception error)
        { return (error as WorkspaceFileException)?.Code ?? (error as TabularProjectException)?.Code ??
            (IsMissing(error) ? "source_missing" : "source_storage_failed"); }
        private static string Message(Exception error)
        { return error is WorkspaceFileException || error is TabularProjectException ? error.Message
            : IsMissing(error) ? "Исходный файл не найден."
            : "Подготовка остановлена из-за ошибки доступа к хранилищу. Проверьте доступность файлов перед продолжением."; }
        private static WorkspaceApplicationException Invalid(string message)
        { return new WorkspaceApplicationException("source_queue_invalid", message); }
        private void CancelPending()
        { foreach (var item in _items.Where(item => item.State == WorkspaceSourceState.Pending)) item.State = WorkspaceSourceState.Cancelled; }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true; CancelPending(); _lifetime.Cancel(); _revision++;
                if (!_pumpRunning) _lifetime.Dispose();
            }
        }
    }
}
