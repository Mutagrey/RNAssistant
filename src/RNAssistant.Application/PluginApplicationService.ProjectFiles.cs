using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Application
{
    public sealed partial class PluginApplicationService
    {
        private const int ProjectFileChunkBytes = 256 * 1024;
        private const long ProjectFileMaxBytes = long.MaxValue;
        private readonly object _projectUploadSync = new object();
        private readonly Dictionary<string, ProjectFileUploadLease> _projectUploadLeases =
            new Dictionary<string, ProjectFileUploadLease>(StringComparer.Ordinal);

        private sealed class ProjectFileUploadLease
        {
            internal string Id, WorkspaceId, SessionId, StagePath, RelativePath;
            internal long ByteLength, Received;
            internal DateTime ExpiresUtc;
        }

        public ProjectChatUploadOpenDto BeginProjectChatFileUpload(ProjectChatUploadOpenRequest request)
        {
            if (request == null || request.ByteLength < 1 || request.ByteLength > ProjectFileMaxBytes)
                throw new PluginException("project_file_invalid", "Выберите непустой файл.");
            var name = SafeProjectFileName(request.FileName);
            using (PluginStore.AcquireRuntimeUse(_paths))
            {
                RequireProjectUploadChat(request.WorkspaceId, request.SessionId);
                lock (_projectUploadSync)
                {
                    if (_disposed) throw new PluginException("plugins_closed", "Plugin host is closing.");
                    ExpireProjectUploads();
                    if (_projectUploadLeases.Count >= 4)
                        throw new PluginException("project_file_busy", "Дождитесь завершения текущих загрузок.");
                    var directory = ProjectUploadDirectory();
                    var id = Guid.NewGuid().ToString("N");
                    var stage = Path.Combine(directory, id + ".upload");
                    using (new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                    var lease = new ProjectFileUploadLease { Id = id, WorkspaceId = request.WorkspaceId,
                        SessionId = request.SessionId, StagePath = stage,
                        RelativePath = "chat-files/" + id + "-" + name, ByteLength = request.ByteLength,
                        ExpiresUtc = DateTime.UtcNow.AddMinutes(10) };
                    _projectUploadLeases.Add(id, lease);
                    return new ProjectChatUploadOpenDto { LeaseId = id, MaxChunkBytes = ProjectFileChunkBytes,
                        RelativePath = lease.RelativePath };
                }
            }
        }

        public ProjectChatUploadProgressDto WriteProjectChatFileChunk(ProjectChatUploadChunkRequest request)
        {
            if (request == null || request.Base64 == null || request.Base64.Length > 4 * ((ProjectFileChunkBytes + 2) / 3))
                throw new PluginException("project_file_chunk_invalid", "Фрагмент файла превышает допустимый размер.");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(request.Base64); }
            catch (FormatException) { throw new PluginException("project_file_chunk_invalid", "Фрагмент файла повреждён."); }
            lock (_projectUploadSync)
            {
                var lease = RequireProjectUpload(request.WorkspaceId, request.SessionId, request.LeaseId);
                if (request.Offset != lease.Received || bytes.Length < 1 || bytes.Length > ProjectFileChunkBytes ||
                    lease.Received + bytes.Length > lease.ByteLength)
                    throw new PluginException("project_file_offset_invalid", "Фрагменты файла должны идти подряд в пределах заявленного размера.");
                try
                {
                    if (StorageFileSystem.IsReparsePoint(lease.StagePath))
                        throw new IOException("Upload staging was replaced by a link.");
                    using (var stream = new FileStream(lease.StagePath, FileMode.Open, FileAccess.Write, FileShare.None))
                    {
                        if (stream.Length != lease.Received) throw new IOException("Upload staging changed outside the lease.");
                        stream.Position = lease.Received;
                        stream.Write(bytes, 0, bytes.Length);
                    }
                    lease.Received += bytes.Length;
                    return new ProjectChatUploadProgressDto { NextOffset = lease.Received };
                }
                catch
                {
                    _projectUploadLeases.Remove(lease.Id);
                    DeleteProjectUploadStage(lease.StagePath);
                    throw;
                }
            }
        }

        public ProjectChatImportedFileDto CompleteProjectChatFileUpload(ProjectChatUploadLeaseRequest request,
            CancellationToken token = default(CancellationToken))
        {
            ProjectFileUploadLease lease;
            lock (_projectUploadSync)
            {
                lease = RequireProjectUpload(request?.WorkspaceId, request?.SessionId, request?.LeaseId);
                if (lease.Received != lease.ByteLength)
                    throw new PluginException("project_file_incomplete", "Дождитесь передачи всего файла.");
                // Completion is consumed once. An uncertain write is inspected, never replayed.
                _projectUploadLeases.Remove(lease.Id);
            }
            try
            {
                using (PluginStore.AcquireRuntimeUse(_paths))
                {
                    var workspace = RequireProjectUploadChat(lease.WorkspaceId, lease.SessionId);
                    token.ThrowIfCancellationRequested();
                    var files = new WorkspaceFileService(_paths);
                    var published = files.CommitStagedFile(workspace,
                        lease.RelativePath, lease.StagePath, null, ProjectFileMaxBytes, token: token);
                    var modelReadable = true;
                    try { files.ReadModelText(workspace, published.RelativePath, ResourceReadRequest.MinimumCharacters, token); }
                    catch (IOException) { modelReadable = false; }
                    catch (UnauthorizedAccessException) { modelReadable = false; }
                    return new ProjectChatImportedFileDto { RelativePath = published.RelativePath,
                        ByteLength = published.ByteLength, ModelReadable = modelReadable };
                }
            }
            finally
            {
                // A cleanup failure must not turn a verified publication into a false failure.
                try { DeleteProjectUploadStage(lease.StagePath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        public ProjectChatUploadProgressDto CancelProjectChatFileUpload(ProjectChatUploadLeaseRequest request)
        {
            lock (_projectUploadSync)
            {
                var lease = RequireProjectUpload(request?.WorkspaceId, request?.SessionId, request?.LeaseId);
                _projectUploadLeases.Remove(lease.Id);
                DeleteProjectUploadStage(lease.StagePath);
                return new ProjectChatUploadProgressDto { NextOffset = lease.Received };
            }
        }

        private WorkspaceDescriptor RequireProjectUploadChat(string workspaceId, string sessionId)
        {
            var workspace = RequireProjectWorkspace(workspaceId);
            if (workspace.ReadOnly)
                throw new PluginException("project_file_read_only", "Выбранный проект доступен только для чтения.");
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new PluginException("project_chat_invalid", "Откройте чат проекта перед добавлением файла.");
            var session = Conversations.GetSession(workspace, sessionId);
            if (session == null) throw new PluginException("project_chat_missing", "Чат отсутствует в этом проекте.");
            var lifecycle = session.LastRun?.KernelState?.Summary?.Lifecycle;
            if (lifecycle == RunLifecycle.Running || lifecycle == RunLifecycle.AwaitingConfirmation)
                throw new PluginException("project_chat_busy", "Дождитесь завершения работы агента.");
            return workspace;
        }

        private ProjectFileUploadLease RequireProjectUpload(string workspaceId, string sessionId, string leaseId)
        {
            ExpireProjectUploads();
            ProjectFileUploadLease lease;
            if (_disposed || string.IsNullOrWhiteSpace(leaseId) ||
                !_projectUploadLeases.TryGetValue(leaseId, out lease) ||
                lease.WorkspaceId != workspaceId || lease.SessionId != sessionId)
                throw new PluginException("project_file_lease_missing", "Загрузка файла истекла или относится к другому чату.");
            return lease;
        }

        private string ProjectUploadDirectory()
        {
            var directory = Path.Combine(_paths.Root, "project-chat-upload-leases");
            Directory.CreateDirectory(directory);
            StorageFileSystem.EnsureRegularDirectory(directory);
            foreach (var file in Directory.EnumerateFiles(directory, "*.upload"))
                if (!_projectUploadLeases.Values.Any(item => item.StagePath == file) &&
                    !StorageFileSystem.IsReparsePoint(file) && File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddHours(-1))
                    DeleteProjectUploadStage(file);
            return directory;
        }

        private void ExpireProjectUploads()
        {
            foreach (var lease in _projectUploadLeases.Values.Where(item => item.ExpiresUtc <= DateTime.UtcNow).ToArray())
            {
                _projectUploadLeases.Remove(lease.Id);
                DeleteProjectUploadStage(lease.StagePath);
            }
        }

        private void CancelAllProjectChatFileUploads()
        {
            lock (_projectUploadSync)
            {
                foreach (var lease in _projectUploadLeases.Values)
                {
                    try { DeleteProjectUploadStage(lease.StagePath); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                _projectUploadLeases.Clear();
            }
        }

        private static void DeleteProjectUploadStage(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        private static string SafeProjectFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 120 || name == "." || name == ".." ||
                name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(" ", StringComparison.Ordinal) ||
                name.Any(character => char.IsControl(character) || "<>:\"/\\|?*".IndexOf(character) >= 0))
                throw new PluginException("project_file_invalid", "Имя файла содержит недопустимые символы.");
            return name;
        }
    }
}
