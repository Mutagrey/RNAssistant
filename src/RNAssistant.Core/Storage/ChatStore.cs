using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Storage
{
    public sealed class ChatConcurrencyException : InvalidOperationException
    {
        public ChatConcurrencyException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// Append-only canonical session store. ChatSession is a rebuildable projection and is never
    /// persisted as a mutable snapshot. Each Save appends one atomic commit containing typed state
    /// operations; model traffic is appended to the same stream as non-projecting trace events.
    /// </summary>
    public sealed partial class ChatStore
    {
        private const string EventFileSuffix = ".events.jsonl";
        private const int MaxProjectionCacheEntries = 16;
        private const long MaxProjectionCacheCharacters = 4L * 1024 * 1024;
        private const long MaxProjectionCacheTotalCharacters = 16L * 1024 * 1024;
        private const int MaxHeaderCacheEntries = 64;
        private const long MaxHeaderCacheCharacters = 512L * 1024;
        private const long MaxHeaderCacheTotalCharacters = 4L * 1024 * 1024;
        private static readonly object PersistenceSync = new object();
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private static readonly HashSet<string> SessionEventProperties = new HashSet<string>(
            new[]
            {
                "SchemaVersion", "SessionId", "Sequence", "EventId", "CreatedUtc", "Type",
                "RunId", "TurnId", "StepId", "PreviousHash", "HashAlgorithm", "ProtectionKeyId",
                "Hash", "Data", "EncryptedData", "Payload"
            },
            StringComparer.Ordinal);

        private readonly AppDataPaths _paths;
        private readonly ChatBlobStore _blobs;
        private readonly Func<StorageProtector> _protectionProvider;
        private readonly BoundedLruCache<ProjectionCacheEntry> _projectionCache;
        private readonly BoundedLruCache<HeaderCacheEntry> _headerCache;
        private long _projectionFullReplayCount;
        private long _projectionIncrementalReplayCount;
        private long _headerFullReplayCount;
        private long _headerIncrementalReplayCount;
        private long _artifactCasExternalizationCount;

        internal long ProjectionFullReplayCount
        {
            get { return Interlocked.Read(ref _projectionFullReplayCount); }
        }

        internal long ProjectionIncrementalReplayCount
        {
            get { return Interlocked.Read(ref _projectionIncrementalReplayCount); }
        }

        internal long HeaderFullReplayCount
        {
            get { return Interlocked.Read(ref _headerFullReplayCount); }
        }

        internal long HeaderIncrementalReplayCount
        {
            get { return Interlocked.Read(ref _headerIncrementalReplayCount); }
        }

        internal long ArtifactCasExternalizationCount
        {
            get { return Interlocked.Read(ref _artifactCasExternalizationCount); }
        }

        public ChatStore(AppDataPaths paths)
            : this(paths, null)
        {
        }

        public ChatStore(AppDataPaths paths, Func<StorageProtector> protectionProvider)
        {
            _paths = paths ?? throw new ArgumentNullException("paths");
            _protectionProvider = protectionProvider ?? (() => StorageProtector.None);
            _blobs = new ChatBlobStore(paths, _protectionProvider);
            _projectionCache = new BoundedLruCache<ProjectionCacheEntry>(
                MaxProjectionCacheEntries,
                MaxProjectionCacheCharacters,
                MaxProjectionCacheTotalCharacters,
                entry => entry == null ? 0 : entry.EstimatedCharacters,
                StringComparer.OrdinalIgnoreCase);
            _headerCache = new BoundedLruCache<HeaderCacheEntry>(
                MaxHeaderCacheEntries,
                MaxHeaderCacheCharacters,
                MaxHeaderCacheTotalCharacters,
                entry => entry == null ? 0 : entry.EstimatedCharacters,
                StringComparer.OrdinalIgnoreCase);
        }

        public ChatSession LoadOrCreateActive(string host, string documentKey, string documentTitle)
        {
            var activeId = LoadActiveSessionId(host, documentKey);
            var session = string.IsNullOrWhiteSpace(activeId) ? null : Load(host, documentKey, activeId);
            if (session == null)
            {
                var header = ListHeaders(host, documentKey, documentTitle).FirstOrDefault();
                session = header == null ? null : Load(host, documentKey, header.Id);
            }
            if (session == null)
            {
                session = CreateTransient(host, documentKey, documentTitle, "New chat");
            }

            if (IsPersisted(session)) SaveActiveSessionId(host, documentKey, session.Id);
            return session;
        }

        public ChatSession Create(string host, string documentKey, string documentTitle, string title)
        {
            var session = CreateTransient(host, documentKey, documentTitle, title);
            Save(session);
            SaveActiveSessionId(host, documentKey, session.Id);
            return session;
        }

        public ChatSession CreateTransient(string host, string documentKey, string documentTitle, string title)
        {
            var session = new ChatSession
            {
                Host = host,
                DocumentKey = documentKey,
                DocumentTitle = documentTitle,
                Title = string.IsNullOrWhiteSpace(title) ? "New chat" : title,
                Mode = ChatModes.Agent
            };
            NormalizeSession(session, host, documentKey, documentTitle);
            return session;
        }

        public ChatSession Load(string host, string documentKey, string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return null;
            var session = LoadSession(GetSessionPath(host, documentKey, sessionId), true);
            if (session == null) return null;
            NormalizeSession(session, host, documentKey, session.DocumentTitle);
            return session;
        }

        public ChatSession Load(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return null;
            ChatSession selected = null;
            foreach (var path in SafeFindSessionFiles(sessionId))
            {
                var session = LoadSession(path, true);
                if (session != null && string.Equals(session.Id, sessionId, StringComparison.OrdinalIgnoreCase))
                {
                    if (selected == null || session.UpdatedUtc > selected.UpdatedUtc) selected = session;
                }
            }
            return selected;
        }

        public void Save(ChatSession session)
        {
            SaveInternal(session, null, false);
        }

        public SessionEvent AppendTrace(
            ChatSession session,
            string type,
            object data,
            string payloadText,
            string payloadContentType,
            string runId,
            string turnId,
            string stepId)
        {
            return AppendTraceCore(
                session,
                type,
                data,
                payloadText == null ? null : Utf8.GetBytes(payloadText),
                payloadContentType,
                runId,
                turnId,
                stepId);
        }

        public SessionEvent AppendTraceBytes(
            ChatSession session,
            string type,
            object data,
            byte[] payloadBytes,
            string payloadContentType,
            string runId,
            string turnId,
            string stepId)
        {
            return AppendTraceCore(
                session,
                type,
                data,
                payloadBytes,
                payloadContentType,
                runId,
                turnId,
                stepId);
        }

        private SessionEvent AppendTraceCore(
            ChatSession session,
            string type,
            object data,
            byte[] payloadBytes,
            string payloadContentType,
            string runId,
            string turnId,
            string stepId)
        {
            if (session == null) throw new ArgumentNullException("session");
            if (string.IsNullOrWhiteSpace(type)) throw new ArgumentException("Event type is required.", "type");

            var dataToken = data == null ? null : JToken.FromObject(data);
            var correlatedStepId = ResolveStepId(stepId, dataToken);
            ChatBlobReference payload = null;
            if (payloadBytes != null)
            {
                payload = _blobs.StoreBytes(payloadBytes, payloadContentType);
            }

            lock (PersistenceSync)
            {
                var path = GetSessionPath(session.Host, session.DocumentKey, session.Id);
                using (AcquireDocumentLock(session.Host, session.DocumentKey))
                {
                    if (!File.Exists(path))
                    {
                        throw new ChatConcurrencyException("The chat must be persisted before trace events can be appended.");
                    }
                    var pending = new List<PendingSessionEvent>();
                    if (string.Equals(type, SessionEventTypes.LlmRequest, StringComparison.Ordinal))
                    {
                        pending.Add(PendingEvent(SessionEventTypes.StepStarted,
                            BuildStepLifecycleData(dataToken, "running", false, null), null,
                            runId, turnId, correlatedStepId));
                    }

                    var trace = PendingEvent(type, dataToken, payload, runId, turnId, correlatedStepId);
                    pending.Add(trace);

                    if (string.Equals(type, SessionEventTypes.LlmResponse, StringComparison.Ordinal) ||
                        string.Equals(type, SessionEventTypes.LlmFailure, StringComparison.Ordinal))
                    {
                        var status = StepTerminalStatus(type, dataToken);
                        pending.Add(PendingEvent(SessionEventTypes.StepEnded,
                            BuildStepLifecycleData(dataToken, status, false, trace.EventId), null,
                            runId, turnId, correlatedStepId));
                    }

                    var appended = AppendEvents(
                        path,
                        session.Id,
                        session.Revision,
                        session.StorageHeadHash,
                        session.StorageByteLength,
                        session.StorageLastWriteUtcTicks,
                        session.StorageTailByteOffset,
                        pending,
                        null);
                    AdvanceHeaderCache(path, session.Id, session.Revision, session.StorageHeadHash,
                        session.StorageByteLength, appended);
                    AdvanceProjectionCache(path, session.Id, session.Revision, session.StorageHeadHash,
                        session.StorageByteLength, appended);
                    var tail = appended[appended.Count - 1];
                    session.Revision = tail.Sequence;
                    session.StorageHeadHash = tail.Hash;
                    session.StorageTailByteOffset = tail.StorageByteOffset;
                    CaptureStorageState(session, path);
                    return appended.First(item => string.Equals(item.EventId, trace.EventId, StringComparison.Ordinal));
                }
            }
        }

        public int CloseOpenSteps(ChatSession session, string runId, string status, string error)
        {
            if (session == null || string.IsNullOrWhiteSpace(runId)) return 0;
            lock (PersistenceSync)
            {
                var path = GetSessionPath(session.Host, session.DocumentKey, session.Id);
                using (AcquireDocumentLock(session.Host, session.DocumentKey))
                {
                    var log = ReadEventLog(path);
                    var actualRevision = log == null || log.Events.Count == 0
                        ? 0
                        : log.Events[log.Events.Count - 1].Sequence;
                    if (actualRevision != session.Revision)
                    {
                        throw new ChatConcurrencyException("Chat was changed by another RNAssistant instance. Reload the chat before saving again.");
                    }
                    var open = OpenStepIds(log == null ? null : log.Events, runId);
                    var turnId = TurnIdForRun(log == null ? null : log.Events, runId);
                    var pending = open.Select(stepId => PendingEvent(
                        SessionEventTypes.StepEnded,
                        new JObject
                        {
                            ["Status"] = string.IsNullOrWhiteSpace(status) ? "interrupted" : status,
                            ["Synthetic"] = true,
                            ["Error"] = string.IsNullOrWhiteSpace(error) ? JValue.CreateNull() : new JValue(error)
                        },
                        null,
                        runId,
                        turnId,
                        stepId)).ToList();
                    if (pending.Count > 0)
                    {
                        var appended = AppendEvents(
                            path,
                            session.Id,
                            session.Revision,
                            session.StorageHeadHash,
                            session.StorageByteLength,
                            session.StorageLastWriteUtcTicks,
                            session.StorageTailByteOffset,
                            pending,
                            log);
                        AdvanceHeaderCache(path, session.Id, session.Revision, session.StorageHeadHash,
                            session.StorageByteLength, appended);
                        AdvanceProjectionCache(path, session.Id, session.Revision, session.StorageHeadHash,
                            session.StorageByteLength, appended);
                        var tail = appended[appended.Count - 1];
                        session.Revision = tail.Sequence;
                        session.StorageHeadHash = tail.Hash;
                        session.StorageTailByteOffset = tail.StorageByteOffset;
                        CaptureStorageState(session, path);
                    }
                    return open.Count;
                }
            }
        }

        public IReadOnlyList<SessionEvent> ReadEvents(string host, string documentKey, string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return new List<SessionEvent>();
            var path = GetSessionPath(host, documentKey, sessionId);
            lock (PersistenceSync)
            {
                using (AcquireDocumentLock(host, documentKey))
                {
                    var log = ReadEventLog(path);
                    return log == null ? new List<SessionEvent>() : log.Events;
                }
            }
        }

        public IReadOnlyList<SessionEvent> ReadCompleteEvents(string host, string documentKey, string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return new List<SessionEvent>();
            var path = GetSessionPath(host, documentKey, sessionId);
            lock (PersistenceSync)
            {
                using (AcquireDocumentLock(host, documentKey))
                {
                    var log = ReadEventLog(path);
                    if (log != null && log.HasIncompleteTail)
                    {
                        throw new ChatConcurrencyException("The chat event log has an incomplete tail and cannot be exported.");
                    }
                    return log == null ? new List<SessionEvent>() : log.Events;
                }
            }
        }

        internal void ScanCasReferences(CasReachabilityScan scan)
        {
            if (scan == null) throw new ArgumentNullException("scan");
            var paths = StorageFileSystem.GetFilesRecursive(
                _paths.ChatDirectory,
                "*" + EventFileSuffix,
                (path, message) => scan.AddSourceIssue(
                    CasHealthIssueKinds.SourceUnreadable,
                    "chat",
                    CasMaintenanceService.RelativePath(_paths.ChatDirectory, path),
                    message)).ToArray();

            foreach (var path in paths.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                scan.ChatStreamCount += 1;
                var sourceId = CasMaintenanceService.RelativePath(_paths.ChatDirectory, path);
                try
                {
                    lock (PersistenceSync)
                    {
                        using (AcquireDocumentPathLock(path))
                        {
                            var log = ReadEventLog(path);
                            if (log == null || log.Events.Count == 0)
                            {
                                scan.AddSourceIssue(CasHealthIssueKinds.SourceInvalid, "chat", sourceId,
                                    "The chat event stream is empty or invalid.");
                                continue;
                            }
                            foreach (var sessionEvent in log.Events)
                            {
                                scan.AddReference(sessionEvent.Payload, "chat", sourceId,
                                    "event#" + sessionEvent.Sequence + ".Payload");
                                scan.AddTokenReferences(sessionEvent.Data, "chat", sourceId,
                                    "event#" + sessionEvent.Sequence + ".Data");
                            }
                            if (log.HasIncompleteTail)
                            {
                                scan.AddSourceIssue(CasHealthIssueKinds.IncompleteTail, "chat", sourceId,
                                    "The chat event stream has an incomplete final record.");
                            }

                            var projected = Project(log, false);
                            if (projected == null)
                            {
                                scan.AddSourceIssue(CasHealthIssueKinds.SourceInvalid, "chat", sourceId,
                                    "The chat event stream cannot be projected.");
                                continue;
                            }
                            var canonicalPath = GetSessionPath(projected.Host, projected.DocumentKey, projected.Id);
                            if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(canonicalPath), StringComparison.OrdinalIgnoreCase))
                            {
                                scan.AddSourceIssue(CasHealthIssueKinds.SourceInvalid, "chat", sourceId,
                                    "The chat event stream is outside its canonical document/session path.");
                            }
                        }
                    }
                }
                catch (Exception ex) when (
                    ex is IOException || ex is UnauthorizedAccessException || ex is JsonException ||
                    ex is InvalidOperationException || ex is ArgumentException || ex is CryptographicException ||
                    ex is DecoderFallbackException)
                {
                    scan.AddSourceIssue(CasHealthIssueKinds.SourceUnreadable, "chat", sourceId,
                        "The chat event stream could not be validated: " + ex.Message);
                }
            }
        }

        public string ReadEventPayload(SessionEvent sessionEvent)
        {
            return sessionEvent == null || sessionEvent.Payload == null
                ? null
                : _blobs.ReadText(sessionEvent.Payload);
        }

        public bool HasOpenToolExecution(ChatSession session, string runId)
        {
            if (session == null || string.IsNullOrWhiteSpace(runId)) return false;
            var events = ReadEvents(session.Host, session.DocumentKey, session.Id);
            var open = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sessionEvent in events)
            {
                if (!string.Equals(sessionEvent.Type, SessionEventTypes.SessionCommit, StringComparison.Ordinal) ||
                    sessionEvent.Data == null) continue;
                var operations = sessionEvent.Data["Operations"] as JArray;
                foreach (var operation in operations == null ? new List<JToken>() : operations.ToList())
                {
                    var type = (string)operation["Type"];
                    if (!string.Equals(type, SessionOperationTypes.ToolExecutionStarted, StringComparison.Ordinal) &&
                        !string.Equals(type, SessionOperationTypes.ToolExecutionFinished, StringComparison.Ordinal)) continue;
                    var value = operation["Data"] == null ? null : operation["Data"]["Value"];
                    var activity = value == null ? null : value["Activity"];
                    var activityRunId = (string)(activity == null ? null : activity["RunId"]);
                    var toolCallId = (string)(activity == null ? null : activity["ToolCallId"]);
                    if (!string.Equals(activityRunId, runId, StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrWhiteSpace(toolCallId)) continue;
                    if (string.Equals(type, SessionOperationTypes.ToolExecutionStarted, StringComparison.Ordinal)) open.Add(toolCallId);
                    else open.Remove(toolCallId);
                }
            }
            return open.Count > 0;
        }

        public bool LoadArtifactBody(ChatSession session, string artifactId)
        {
            if (session == null || string.IsNullOrWhiteSpace(artifactId)) return false;
            var artifact = (session.Artifacts ?? new List<ChatArtifact>()).FirstOrDefault(item =>
                item != null && string.Equals(item.Id, artifactId, StringComparison.OrdinalIgnoreCase));
            return HydrateArtifact(artifact);
        }

        public bool TryActivateHtmlWorkspaceRevision(ChatSession session, string artifactId, out string error)
        {
            error = null;
            if (session == null || string.IsNullOrWhiteSpace(artifactId))
            {
                error = "HTML workspace revision is required.";
                return false;
            }
            var artifact = FindHtmlArtifact(session, artifactId);
            if (artifact == null)
            {
                error = "HTML workspace revision metadata was not found.";
                return false;
            }
            if (!HydrateArtifact(artifact))
            {
                error = "HTML workspace revision body is missing, corrupt, or cannot be decrypted.";
                return false;
            }
            if (ParseWorkspaceSnapshot(artifact) == null)
            {
                error = "HTML workspace revision body is invalid.";
                return false;
            }

            session.ActiveHtmlArtifactId = artifact.Id;
            RebuildHtmlWorkspaceProjection(session);
            if (session.HtmlWorkspaceRecovery == null || !session.HtmlWorkspaceRecovery.CanMutate)
            {
                error = session.HtmlWorkspaceRecovery == null
                    ? "HTML workspace revision could not be projected."
                    : session.HtmlWorkspaceRecovery.Message;
                return false;
            }
            return true;
        }

        public void LoadArtifactBodies(ChatSession session, IEnumerable<string> artifactIds)
        {
            if (session == null || artifactIds == null) return;
            foreach (var id in artifactIds.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                LoadArtifactBody(session, id);
            }
        }

        public ChatSession Move(ChatSession session, string host, string documentKey, string documentTitle)
        {
            if (session == null) return null;
            var oldHost = session.Host;
            var oldDocumentKey = session.DocumentKey;
            var oldDocumentTitle = session.DocumentTitle;
            var oldPath = GetSessionPath(oldHost, oldDocumentKey, session.Id);

            session.Host = host;
            session.DocumentKey = documentKey;
            session.DocumentTitle = documentTitle;
            if (session.Context != null)
            {
                session.Context.Host = host;
                session.Context.DocumentKey = documentKey;
            }
            NormalizeSession(session, host, documentKey, documentTitle);

            try
            {
                if (!File.Exists(oldPath))
                {
                    if (session.Revision > 0)
                    {
                        throw new ChatConcurrencyException("The source chat event log no longer exists.");
                    }
                    return session;
                }

                lock (PersistenceSync)
                {
                    var newPath = GetSessionPath(host, documentKey, session.Id);
                    using (AcquireTwoDocumentLocks(oldHost, oldDocumentKey, host, documentKey))
                    {
                        SaveInternalLocked(session, oldPath, true);
                        if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
                        {
                            if (File.Exists(newPath))
                            {
                                throw new ChatConcurrencyException("The destination already contains this chat.");
                            }
                            Directory.CreateDirectory(Path.GetDirectoryName(newPath));
                            File.Move(oldPath, newPath);
                            MoveProjectionCache(oldPath, newPath);
                            MoveHeaderCache(oldPath, newPath);
                        }
                    }
                }
            }
            catch
            {
                session.Host = oldHost;
                session.DocumentKey = oldDocumentKey;
                session.DocumentTitle = oldDocumentTitle;
                if (session.Context != null)
                {
                    session.Context.Host = oldHost;
                    session.Context.DocumentKey = oldDocumentKey;
                }
                throw;
            }

            SaveActiveSessionId(host, documentKey, session.Id);
            return session;
        }

        public void MoveDocument(string oldHost, string oldDocumentKey, string newHost, string newDocumentKey, string documentTitle)
        {
            MoveDocument(oldHost, oldDocumentKey, newHost, newDocumentKey, documentTitle, null);
        }

        public void MoveDocument(
            string oldHost,
            string oldDocumentKey,
            string newHost,
            string newDocumentKey,
            string documentTitle,
            string documentPath)
        {
            var activeId = LoadActiveSessionId(oldHost, oldDocumentKey);
            foreach (var session in List(oldHost, oldDocumentKey, documentTitle))
            {
                if (!string.IsNullOrWhiteSpace(documentPath)) session.DocumentPath = documentPath.Trim();
                Move(session, newHost, newDocumentKey, documentTitle);
            }
            if (!string.IsNullOrWhiteSpace(activeId))
            {
                SaveActiveSessionId(newHost, newDocumentKey, activeId);
                SaveActiveSessionId(oldHost, oldDocumentKey, string.Empty);
            }
        }

        public void ClearMessages(string host, string documentKey, string sessionId)
        {
            var session = Load(host, documentKey, sessionId);
            if (session == null) return;
            session.Messages.Clear();
            Save(session);
        }

        public bool Delete(string host, string documentKey, string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return false;
            var path = GetSessionPath(host, documentKey, sessionId);
            if (!File.Exists(path)) return false;
            lock (PersistenceSync)
            {
                using (AcquireDocumentLock(host, documentKey))
                {
                    if (!File.Exists(path)) return false;
                    File.Delete(path);
                    RemoveProjectionCache(path);
                    RemoveHeaderCache(path);
                }
            }
            if (string.Equals(LoadActiveSessionId(host, documentKey), sessionId, StringComparison.OrdinalIgnoreCase))
            {
                SaveActiveSessionId(host, documentKey, string.Empty);
            }
            return true;
        }

        public bool DeleteDocument(string host, string documentKey)
        {
            var directory = GetDocumentDirectory(host, documentKey);
            if (!Directory.Exists(directory)) return false;
            lock (PersistenceSync)
            {
                using (AcquireDocumentLock(host, documentKey))
                {
                    if (!Directory.Exists(directory)) return false;
                    if (!StorageFileSystem.TryDeleteDirectory(directory)) return false;
                    ClearProjectionCache();
                    ClearHeaderCache();
                }
            }
            return true;
        }

        public bool IsPersisted(ChatSession session)
        {
            return session != null && File.Exists(GetSessionPath(session.Host, session.DocumentKey, session.Id));
        }

        public IReadOnlyList<ChatSession> List()
        {
            if (!Directory.Exists(_paths.ChatDirectory)) return new List<ChatSession>();
            var sessions = new List<ChatSession>();
            foreach (var directory in SafeGetDirectories(_paths.ChatDirectory))
            {
                sessions.AddRange(SafeGetSessionFiles(directory)
                    .Select(path => LoadSession(path, false))
                    .Where(session => session != null));
            }
            return sessions.OrderByDescending(session => session.UpdatedUtc).ToList();
        }

        public IReadOnlyList<ChatSession> List(string host, string documentKey, string documentTitle)
        {
            var directory = GetDocumentDirectory(host, documentKey);
            if (!Directory.Exists(directory)) return new List<ChatSession>();
            return SafeGetSessionFiles(directory)
                .Select(path => LoadSession(path, false))
                .Where(session => session != null)
                .Select(session =>
                {
                    NormalizeSession(session, host, documentKey, documentTitle);
                    return session;
                })
                .OrderByDescending(session => session.UpdatedUtc)
                .ToList();
        }

        public IReadOnlyList<ChatSessionHeader> ListHeaders()
        {
            if (!Directory.Exists(_paths.ChatDirectory)) return new List<ChatSessionHeader>();
            var headers = new List<ChatSessionHeader>();
            foreach (var directory in SafeGetDirectories(_paths.ChatDirectory))
            {
                headers.AddRange(SafeGetSessionFiles(directory)
                    .Select(path => LoadHeader(path, null, null, null))
                    .Where(header => header != null));
            }
            return headers.OrderByDescending(header => header.UpdatedUtc).ToList();
        }

        public IReadOnlyList<ChatSessionHeader> ListHeaders(string host, string documentKey, string documentTitle)
        {
            var directory = GetDocumentDirectory(host, documentKey);
            if (!Directory.Exists(directory)) return new List<ChatSessionHeader>();
            return SafeGetSessionFiles(directory)
                .Select(path => LoadHeader(path, host, documentKey, documentTitle))
                .Where(header => header != null)
                .OrderByDescending(header => header.UpdatedUtc)
                .ToList();
        }

        public string LoadActiveSessionId(string host, string documentKey)
        {
            var path = GetActivePath(host, documentKey);
            if (!File.Exists(path)) return string.Empty;
            try
            {
                lock (PersistenceSync)
                {
                    using (AcquireDocumentLock(host, documentKey))
                    {
                        return File.Exists(path) ? (File.ReadAllText(path) ?? string.Empty).Trim() : string.Empty;
                    }
                }
            }
            catch (IOException) { return string.Empty; }
            catch (UnauthorizedAccessException) { return string.Empty; }
            catch (ChatConcurrencyException) { return string.Empty; }
        }

        public void SaveActiveSessionId(string host, string documentKey, string sessionId)
        {
            var path = GetActivePath(host, documentKey);
            try
            {
                lock (PersistenceSync)
                {
                    using (AcquireDocumentLock(host, documentKey))
                    {
                        StorageFileSystem.WriteAllTextAtomic(path, sessionId ?? string.Empty, Utf8);
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ChatConcurrencyException) { }
        }

        private void SaveInternal(ChatSession session, string explicitPath, bool allowRelocatedSession)
        {
            if (session == null) throw new ArgumentNullException("session");
            lock (PersistenceSync)
            {
                NormalizeSession(session, session.Host, session.DocumentKey, session.DocumentTitle);
                var path = explicitPath ?? GetSessionPath(session.Host, session.DocumentKey, session.Id);
                using (AcquireDocumentPathLock(path))
                {
                    SaveInternalLocked(session, path, allowRelocatedSession);
                }
            }
        }

        private void SaveInternalLocked(ChatSession session, string path, bool allowRelocatedSession)
        {
            EnsureChartArtifacts(session);
            EnsureWorkspaceArtifact(session);
            ExternalizeArtifacts(session);
            var exists = File.Exists(path);
            EventLogReadResult log = null;
            var stored = exists ? ReadProjectedSession(path, false, false, out log) : null;
            var storedRevision = stored == null ? 0 : stored.Revision;
            if (exists && stored == null)
            {
                throw new ChatConcurrencyException("The chat event log is invalid or corrupted.");
            }
            if (exists && storedRevision != session.Revision)
            {
                throw new ChatConcurrencyException("Chat was changed by another RNAssistant instance. Reload the chat before saving again.");
            }
            if (!exists && session.Revision > 0 && !allowRelocatedSession)
            {
                throw new ChatConcurrencyException("Chat storage changed while this session was open. Reload the chat before saving again.");
            }

            var previousRevision = session.Revision;
            var previousUpdatedUtc = session.UpdatedUtc;
            var durableRevision = previousRevision;
            session.UpdatedUtc = DateTime.UtcNow;
            try
            {
                var pending = new List<PendingSessionEvent>();
                if (!exists)
                {
                    var initialType = string.IsNullOrWhiteSpace(session.ParentSessionId)
                        ? SessionEventTypes.SessionCreated
                        : SessionEventTypes.SessionForked;
                    pending.Add(PendingEvent(initialType,
                        ToProjectionToken(session), null, CurrentRunId(session), CurrentTurnId(session), null));
                }
                else
                {
                    var operations = BuildOperations(stored, session);
                    var correlationRunId = CurrentRunId(session) ?? CurrentRunId(stored);
                    var correlationTurnId = CurrentTurnId(session) ?? CurrentTurnId(stored);
                    pending.Add(PendingEvent(SessionEventTypes.SessionCommit,
                        new JObject { ["Operations"] = JArray.FromObject(operations) }, null,
                        correlationRunId, correlationTurnId, null));
                }
                AddTurnLifecycleEvents(pending, stored == null ? null : stored.LastRun, session.LastRun);
                var appended = AppendEvents(
                    path,
                    session.Id,
                    storedRevision,
                    stored == null ? null : stored.StorageHeadHash,
                    stored == null ? 0 : stored.StorageByteLength,
                    stored == null ? 0 : stored.StorageLastWriteUtcTicks,
                    stored == null ? 0 : stored.StorageTailByteOffset,
                    pending,
                    log);
                AdvanceHeaderCache(
                    path,
                    session.Id,
                    storedRevision,
                    stored == null ? null : stored.StorageHeadHash,
                    stored == null ? 0 : stored.StorageByteLength,
                    appended);
                var tail = appended[appended.Count - 1];
                durableRevision = tail.Sequence;
                session.Revision = durableRevision;
                session.StorageHeadHash = tail.Hash;
                session.StorageTailByteOffset = tail.StorageByteOffset;
                CaptureStorageState(session, path);
                StoreProjectionCache(path, ToProjectionToken(session), session);
                RebuildHtmlWorkspaceProjection(session);
                RebuildContextCheckpointProjection(session);
                RebuildChartActivityProjection(session);
            }
            catch
            {
                try
                {
                    if (File.Exists(path))
                    {
                        var recovered = ReadEventLog(path);
                        var recoveredTail = LastEvent(recovered);
                        durableRevision = recoveredTail == null ? 0 : recoveredTail.Sequence;
                        session.StorageHeadHash = recoveredTail == null ? null : recoveredTail.Hash;
                        session.StorageTailByteOffset = recoveredTail == null ? 0 : recoveredTail.StorageByteOffset;
                        CaptureStorageState(session, path);
                    }
                }
                catch
                {
                    // Keep the last revision that this writer knows was made durable.
                }
                session.Revision = durableRevision;
                if (durableRevision == previousRevision) session.UpdatedUtc = previousUpdatedUtc;
                throw;
            }
        }

        private IReadOnlyList<SessionEvent> AppendEvents(
            string path,
            string sessionId,
            long expectedRevision,
            string expectedHeadHash,
            long expectedByteLength,
            long expectedLastWriteUtcTicks,
            long expectedTailByteOffset,
            IReadOnlyList<PendingSessionEvent> pending,
            EventLogReadResult validatedLog)
        {
            if (pending == null || pending.Count == 0) return new List<SessionEvent>();

            var log = validatedLog;
            var previous = LastEvent(log);
            if (log == null && expectedRevision > 0 && !string.IsNullOrWhiteSpace(expectedHeadHash))
            {
                previous = ReadValidatedTail(path, sessionId, expectedRevision, expectedHeadHash,
                    expectedByteLength, expectedLastWriteUtcTicks, expectedTailByteOffset);
            }
            if (previous == null && (expectedRevision > 0 || File.Exists(path)))
            {
                log = ReadEventLog(path);
                previous = LastEvent(log);
            }

            var actualRevision = previous == null ? 0 : previous.Sequence;
            if (actualRevision != expectedRevision)
            {
                throw new ChatConcurrencyException("Chat was changed by another RNAssistant instance. Reload the chat before saving again.");
            }
            if (log != null && log.HasIncompleteTail)
            {
                JsonlRecordWriter.RewriteAll(path, log.Events, Utf8);
            }

            var protector = Protection();
            var appended = new List<SessionEvent>();
            foreach (var item in pending)
            {
                var sessionEvent = new SessionEvent
                {
                    EventId = item.EventId,
                    CreatedUtc = item.CreatedUtc,
                    SessionId = sessionId,
                    Sequence = previous == null ? 1 : previous.Sequence + 1,
                    Type = item.Type,
                    RunId = item.RunId,
                    TurnId = item.TurnId,
                    StepId = item.StepId,
                    PreviousHash = previous == null ? null : previous.Hash,
                    Data = item.Data == null ? null : item.Data.DeepClone(),
                    Payload = item.Payload
                };
                sessionEvent.HashAlgorithm = protector.CurrentHashAlgorithm;
                sessionEvent.ProtectionKeyId = protector.UsesHmac || protector.Encrypts ? protector.KeyId : null;
                ProtectEventData(sessionEvent, protector);
                sessionEvent.Hash = ComputeHash(sessionEvent, protector);
                appended.Add(sessionEvent);
                previous = sessionEvent;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var appendOffset = File.Exists(path) ? new FileInfo(path).Length : 0;
            var serialized = new List<byte[]>();
            foreach (var sessionEvent in appended)
            {
                sessionEvent.StorageByteOffset = appendOffset;
                var bytes = Utf8.GetBytes(JsonConvert.SerializeObject(sessionEvent, Formatting.None));
                serialized.Add(bytes);
                appendOffset += bytes.LongLength + 1;
            }

            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                if (stream.Length != appended[0].StorageByteOffset)
                {
                    throw new ChatConcurrencyException("Chat was changed while the append batch was being prepared.");
                }
                foreach (var bytes in serialized)
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.WriteByte((byte)'\n');
                }
                stream.Flush(true);
            }
            return appended;
        }

        private ChatSession LoadSession(string path, bool hydrateActiveArtifacts)
        {
            try
            {
                EventLogReadResult ignored;
                var session = ReadProjectedSession(path, hydrateActiveArtifacts, true, out ignored);
                if (session == null) return null;
                NormalizeSession(session, session.Host, session.DocumentKey, session.DocumentTitle);
                CaptureStorageState(session, path);
                return session;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (JsonException) { return null; }
            catch (ChatConcurrencyException) { return null; }
        }

        private ChatSessionHeader LoadHeader(
            string path,
            string host,
            string documentKey,
            string documentTitle)
        {
            try
            {
                var result = ReadHeader(path);
                return result == null || result.Tail == null || result.Reducer == null
                    ? null
                    : result.Reducer.CreateHeader(
                        _blobs,
                        result.Tail.Sequence,
                        result.ByteLength,
                        host,
                        documentKey,
                        documentTitle);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (JsonException) { return null; }
            catch (ChatConcurrencyException) { return null; }
        }

        private ChatSession Project(EventLogReadResult log, bool hydrateActiveArtifacts)
        {
            return Project(log, hydrateActiveArtifacts, true);
        }

        private ChatSession Project(EventLogReadResult log, bool hydrateActiveArtifacts, bool rebuildDerivedProjections)
        {
            if (log == null || log.Events.Count == 0) return null;
            var root = ReplayProjectionRoot(log.Events, null);
            var tail = LastEvent(log);
            return Project(root, tail.Sequence, tail.Hash, tail.StorageByteOffset,
                log.ByteLength, log.LastWriteUtcTicks, hydrateActiveArtifacts, rebuildDerivedProjections);
        }

        private ChatSession ReadProjectedSession(
            string path,
            bool hydrateActiveArtifacts,
            bool rebuildDerivedProjections,
            out EventLogReadResult validatedLog)
        {
            validatedLog = null;
            ProjectionCacheEntry cached;
            if (TryReadProjectionCache(path, out cached))
            {
                return Project(cached.Root, cached.Sequence, cached.HeadHash, cached.TailByteOffset,
                    cached.ByteLength, cached.LastWriteUtcTicks,
                    hydrateActiveArtifacts, rebuildDerivedProjections);
            }

            validatedLog = ReadEventLog(path);
            if (validatedLog == null || validatedLog.Events.Count == 0) return null;
            var root = ReplayProjectionRoot(validatedLog.Events, null);
            var tail = LastEvent(validatedLog);
            Interlocked.Increment(ref _projectionFullReplayCount);
            var session = Project(root, tail.Sequence, tail.Hash, tail.StorageByteOffset,
                validatedLog.ByteLength, validatedLog.LastWriteUtcTicks,
                hydrateActiveArtifacts, rebuildDerivedProjections);
            if (CanCacheProjection(validatedLog)) StoreProjectionCache(path, root, session);
            return session;
        }

        private EventLogReadResult ReadEventLog(string path)
        {
            return ReadEventLog(path, 0, null);
        }

        private EventLogReadResult ReadEventLog(string path, long startByteOffset, SessionEvent previousEvent)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var result = new EventLogReadResult();
            var protector = Protection();
            var before = CaptureStorageFileState(path);
            try
            {
                var summary = JsonlRecordReader.Read(
                    path,
                    startByteOffset,
                    ParseSessionEvent,
                    (sessionEvent, line) =>
                    {
                        ValidateEvent(previousEvent, sessionEvent, protector);
                        HydrateEventData(sessionEvent, protector);
                        sessionEvent.StorageByteOffset = line.Offset;
                        result.Events.Add(sessionEvent);
                        previousEvent = sessionEvent;
                    });
                result.ByteLength = summary.ByteLength;
                result.TailNextByteOffset = summary.TailNextByteOffset;
                result.HasIncompleteTail = summary.HasIncompleteTail;
                var after = CaptureStorageFileState(path);
                result.IsStableSnapshot = before != null && after != null &&
                    before.ByteLength == result.ByteLength && after.ByteLength == result.ByteLength &&
                    before.LastWriteUtcTicks == after.LastWriteUtcTicks;
                result.LastWriteUtcTicks = result.IsStableSnapshot ? after.LastWriteUtcTicks : 0;
            }
            catch (JsonlRecordException ex)
            {
                throw new ChatConcurrencyException(ex.Kind == JsonlRecordErrorKind.BlankRecord
                    ? "The chat event log contains a blank record."
                    : "The chat event log contains an invalid record.");
            }
            catch (DecoderFallbackException)
            {
                throw new ChatConcurrencyException("The chat event log contains invalid UTF-8.");
            }
            return result;
        }

        private static void ValidateEvent(
            SessionEvent previous,
            SessionEvent sessionEvent,
            StorageProtector protector)
        {
            if (sessionEvent == null || sessionEvent.SchemaVersion != SessionEvent.CurrentSchemaVersion ||
                string.IsNullOrWhiteSpace(sessionEvent.SessionId) || string.IsNullOrWhiteSpace(sessionEvent.Type) ||
                !EventProtectionSupport.IsSupportedHashAlgorithm(sessionEvent.HashAlgorithm) ||
                !string.IsNullOrWhiteSpace(sessionEvent.EncryptedData) && sessionEvent.Data != null ||
                !EventProtectionSupport.Matches(
                    protector,
                    sessionEvent.HashAlgorithm,
                    sessionEvent.ProtectionKeyId,
                    sessionEvent.EncryptedData))
            {
                throw new ChatConcurrencyException("The chat event log contains an unsupported record.");
            }
            var expectedSequence = previous == null ? 1 : previous.Sequence + 1;
            var expectedPreviousHash = previous == null ? null : previous.Hash;
            if (sessionEvent.Sequence != expectedSequence ||
                previous != null && !string.Equals(sessionEvent.SessionId, previous.SessionId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(sessionEvent.PreviousHash ?? string.Empty, expectedPreviousHash ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(sessionEvent.Hash, ComputeHash(sessionEvent, protector), StringComparison.OrdinalIgnoreCase))
            {
                throw new ChatConcurrencyException("The chat event log integrity check failed.");
            }
        }

        private static SessionEvent ParseSessionEvent(string text)
        {
            var root = JObject.Parse(text, new JsonLoadSettings
            {
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
            });
            var unknown = root.Properties().FirstOrDefault(property => !SessionEventProperties.Contains(property.Name));
            if (unknown != null)
            {
                throw new JsonSerializationException("Unsupported session event property: " + unknown.Name + ".");
            }
            return root.ToObject<SessionEvent>();
        }

        private static string ComputeHash(SessionEvent sessionEvent, StorageProtector protector)
        {
            var canonical = new JObject
            {
                ["SchemaVersion"] = sessionEvent.SchemaVersion,
                ["SessionId"] = sessionEvent.SessionId,
                ["Sequence"] = sessionEvent.Sequence,
                ["EventId"] = sessionEvent.EventId,
                ["CreatedUtc"] = sessionEvent.CreatedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                ["Type"] = sessionEvent.Type,
                ["RunId"] = sessionEvent.RunId == null ? JValue.CreateNull() : new JValue(sessionEvent.RunId),
                ["TurnId"] = sessionEvent.TurnId == null ? JValue.CreateNull() : new JValue(sessionEvent.TurnId),
                ["StepId"] = sessionEvent.StepId == null ? JValue.CreateNull() : new JValue(sessionEvent.StepId),
                ["PreviousHash"] = sessionEvent.PreviousHash == null ? JValue.CreateNull() : new JValue(sessionEvent.PreviousHash),
                ["HashAlgorithm"] = sessionEvent.HashAlgorithm,
                ["ProtectionKeyId"] = sessionEvent.ProtectionKeyId == null ? JValue.CreateNull() : new JValue(sessionEvent.ProtectionKeyId),
                ["Data"] = string.IsNullOrWhiteSpace(sessionEvent.EncryptedData) && sessionEvent.Data != null
                    ? sessionEvent.Data.DeepClone()
                    : JValue.CreateNull(),
                ["EncryptedData"] = string.IsNullOrWhiteSpace(sessionEvent.EncryptedData)
                    ? JValue.CreateNull()
                    : new JValue(sessionEvent.EncryptedData),
                ["Payload"] = sessionEvent.Payload == null ? JValue.CreateNull() : JToken.FromObject(sessionEvent.Payload)
            };
            try
            {
                var bytes = Utf8.GetBytes(canonical.ToString(Formatting.None));
                return (protector ?? StorageProtector.None).ComputeEventHash(
                    bytes,
                    sessionEvent.HashAlgorithm,
                    sessionEvent.ProtectionKeyId);
            }
            catch (CryptographicException ex)
            {
                throw new ChatConcurrencyException("The chat event log protection key is unavailable or invalid: " + ex.Message);
            }
        }

        private static void ProtectEventData(SessionEvent sessionEvent, StorageProtector protector)
        {
            if (sessionEvent == null || protector == null || !protector.Encrypts) return;
            sessionEvent.EncryptedData = EventProtectionSupport.ProtectPayload(
                sessionEvent.Data,
                protector,
                EventProtectionPurpose(sessionEvent),
                Utf8);
            sessionEvent.Data = null;
        }

        private static void HydrateEventData(SessionEvent sessionEvent, StorageProtector protector)
        {
            if (sessionEvent == null || string.IsNullOrWhiteSpace(sessionEvent.EncryptedData)) return;
            try
            {
                sessionEvent.Data = EventProtectionSupport.UnprotectPayload(
                    sessionEvent.EncryptedData,
                    protector,
                    EventProtectionPurpose(sessionEvent),
                    Utf8);
            }
            catch (FormatException ex)
            {
                throw new ChatConcurrencyException("The encrypted chat event is invalid: " + ex.Message);
            }
            catch (CryptographicException ex)
            {
                throw new ChatConcurrencyException("The encrypted chat event could not be authenticated: " + ex.Message);
            }
            catch (JsonException ex)
            {
                throw new ChatConcurrencyException("The decrypted chat event is invalid: " + ex.Message);
            }
        }

        private static string EventProtectionPurpose(SessionEvent sessionEvent)
        {
            return new JObject
            {
                ["SchemaVersion"] = sessionEvent.SchemaVersion,
                ["SessionId"] = sessionEvent.SessionId,
                ["Sequence"] = sessionEvent.Sequence,
                ["EventId"] = sessionEvent.EventId,
                ["CreatedUtc"] = sessionEvent.CreatedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                ["Type"] = sessionEvent.Type,
                ["RunId"] = sessionEvent.RunId == null ? JValue.CreateNull() : new JValue(sessionEvent.RunId),
                ["TurnId"] = sessionEvent.TurnId == null ? JValue.CreateNull() : new JValue(sessionEvent.TurnId),
                ["StepId"] = sessionEvent.StepId == null ? JValue.CreateNull() : new JValue(sessionEvent.StepId),
                ["PreviousHash"] = sessionEvent.PreviousHash == null ? JValue.CreateNull() : new JValue(sessionEvent.PreviousHash),
                ["HashAlgorithm"] = sessionEvent.HashAlgorithm,
                ["ProtectionKeyId"] = sessionEvent.ProtectionKeyId,
                ["Payload"] = sessionEvent.Payload == null ? JValue.CreateNull() : JToken.FromObject(sessionEvent.Payload)
            }.ToString(Formatting.None);
        }

        private StorageProtector Protection()
        {
            return _protectionProvider() ?? StorageProtector.None;
        }

        private IDisposable AcquirePathLock(string targetPath)
        {
            var directory = Path.Combine(_paths.Root, "locks");
            Directory.CreateDirectory(directory);
            var normalized = Path.GetFullPath(targetPath ?? _paths.Root);
            var lockPath = Path.Combine(directory, "chat_" + AppDataPaths.SafeFileName(normalized) + ".lck");
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                try
                {
                    return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException)
                {
                    if (DateTime.UtcNow >= deadline)
                    {
                        throw new ChatConcurrencyException("Timed out waiting for another RNAssistant instance to finish saving this chat.");
                    }
                    Thread.Sleep(25);
                }
            }
        }

        private IDisposable AcquireDocumentLock(string host, string documentKey)
        {
            return AcquireDocumentDirectoryLock(GetDocumentDirectory(host, documentKey));
        }

        private IDisposable AcquireDocumentPathLock(string path)
        {
            return AcquireDocumentDirectoryLock(Path.GetDirectoryName(path ?? string.Empty));
        }

        private IDisposable AcquireDocumentDirectoryLock(string directory)
        {
            return AcquirePathLock((directory ?? _paths.ChatDirectory) + ".document");
        }

        private IDisposable AcquireTwoDocumentLocks(string firstHost, string firstKey, string secondHost, string secondKey)
        {
            var firstDirectory = GetDocumentDirectory(firstHost, firstKey);
            var secondDirectory = GetDocumentDirectory(secondHost, secondKey);
            if (string.Equals(firstDirectory, secondDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return AcquireDocumentDirectoryLock(firstDirectory);
            }
            return string.Compare(firstDirectory, secondDirectory, StringComparison.OrdinalIgnoreCase) < 0
                ? DisposablePair.Acquire(
                    () => AcquireDocumentDirectoryLock(firstDirectory),
                    () => AcquireDocumentDirectoryLock(secondDirectory))
                : DisposablePair.Acquire(
                    () => AcquireDocumentDirectoryLock(secondDirectory),
                    () => AcquireDocumentDirectoryLock(firstDirectory));
        }

        private string GetDocumentDirectory(string host, string documentKey)
        {
            return Path.Combine(_paths.ChatDirectory,
                AppDataPaths.SafeFileName((host ?? string.Empty) + "|" + (documentKey ?? string.Empty)));
        }

        private string GetSessionPath(string host, string documentKey, string sessionId)
        {
            return Path.Combine(GetDocumentDirectory(host, documentKey),
                AppDataPaths.SafeFileName(sessionId ?? string.Empty) + EventFileSuffix);
        }

        private string GetActivePath(string host, string documentKey)
        {
            return Path.Combine(GetDocumentDirectory(host, documentKey), "active.txt");
        }

        private static IEnumerable<string> SafeGetDirectories(string directory)
        {
            return StorageFileSystem.GetDirectories(directory);
        }

        private static IEnumerable<string> SafeGetSessionFiles(string directory)
        {
            return StorageFileSystem.GetFiles(directory, "*" + EventFileSuffix);
        }

        private IEnumerable<string> SafeFindSessionFiles(string sessionId)
        {
            if (!Directory.Exists(_paths.ChatDirectory)) return new string[0];
            var fileName = AppDataPaths.SafeFileName(sessionId ?? string.Empty) + EventFileSuffix;
            return StorageFileSystem.GetFilesRecursive(_paths.ChatDirectory, fileName);
        }

        private static string CurrentRunId(ChatSession session)
        {
            return session == null || session.LastRun == null ? null : session.LastRun.RunId;
        }

        private static string CurrentTurnId(ChatSession session)
        {
            return session == null || session.LastRun == null
                ? null
                : RunTurnId(session.LastRun);
        }

        private static void AddTurnLifecycleEvents(
            ICollection<PendingSessionEvent> pending,
            ChatRunRecord before,
            ChatRunRecord after)
        {
            var beforeTurnId = RunTurnId(before);
            var afterTurnId = RunTurnId(after);
            if (string.IsNullOrWhiteSpace(beforeTurnId) && string.IsNullOrWhiteSpace(afterTurnId)) return;

            if (string.IsNullOrWhiteSpace(beforeTurnId))
            {
                pending.Add(TurnStarted(after));
                if (IsTerminalRunStatus(after == null ? null : after.Status))
                {
                    pending.Add(TurnEnded(after, after.Status));
                }
                return;
            }

            if (string.IsNullOrWhiteSpace(afterTurnId))
            {
                if (IsTerminalRunStatus(before == null ? null : before.Status)) return;
                var status = string.Equals(before == null ? null : before.Status, "waiting_confirmation", StringComparison.OrdinalIgnoreCase)
                    ? "cancelled"
                    : "completed";
                pending.Add(TurnEnded(before, status));
                return;
            }

            if (!string.Equals(beforeTurnId, afterTurnId, StringComparison.OrdinalIgnoreCase))
            {
                if (!IsTerminalRunStatus(before == null ? null : before.Status))
                {
                    pending.Add(TurnEnded(before, "superseded"));
                }
                pending.Add(TurnStarted(after));
                if (IsTerminalRunStatus(after == null ? null : after.Status))
                {
                    pending.Add(TurnEnded(after, after.Status));
                }
                return;
            }

            if (!IsTerminalRunStatus(before == null ? null : before.Status) &&
                IsTerminalRunStatus(after == null ? null : after.Status))
            {
                pending.Add(TurnEnded(after, after.Status));
            }
        }

        private static PendingSessionEvent TurnStarted(ChatRunRecord run)
        {
            var turnId = RunTurnId(run);
            return PendingEvent(SessionEventTypes.TurnStarted,
                BuildTurnLifecycleData(run, "running"), null,
                run == null ? null : run.RunId, turnId, null);
        }

        private static PendingSessionEvent TurnEnded(ChatRunRecord run, string status)
        {
            var turnId = RunTurnId(run);
            return PendingEvent(SessionEventTypes.TurnEnded,
                BuildTurnLifecycleData(run, string.IsNullOrWhiteSpace(status) ? "completed" : status), null,
                run == null ? null : run.RunId, turnId, null);
        }

        private static JObject BuildTurnLifecycleData(ChatRunRecord run, string status)
        {
            return new JObject
            {
                ["RunId"] = run == null || string.IsNullOrWhiteSpace(run.RunId) ? JValue.CreateNull() : new JValue(run.RunId),
                ["TurnId"] = string.IsNullOrWhiteSpace(RunTurnId(run)) ? JValue.CreateNull() : new JValue(RunTurnId(run)),
                ["Status"] = status ?? string.Empty,
                ["Phase"] = run == null || string.IsNullOrWhiteSpace(run.Phase) ? JValue.CreateNull() : new JValue(run.Phase),
                ["StartedUtc"] = run == null || run.StartedUtc == default(DateTime)
                    ? JValue.CreateNull()
                    : new JValue(run.StartedUtc.ToUniversalTime())
            };
        }

        private static PendingSessionEvent PendingEvent(
            string type,
            JToken data,
            ChatBlobReference payload,
            string runId,
            string turnId,
            string stepId)
        {
            return new PendingSessionEvent
            {
                EventId = Guid.NewGuid().ToString("N"),
                CreatedUtc = DateTime.UtcNow,
                Type = type,
                Data = data,
                Payload = payload,
                RunId = runId,
                TurnId = turnId,
                StepId = stepId
            };
        }

        private static SessionEvent LastEvent(EventLogReadResult log)
        {
            return log == null || log.Events.Count == 0
                ? null
                : log.Events[log.Events.Count - 1];
        }

        private static bool IsTerminalRunStatus(string status)
        {
            return string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "interrupted", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "superseded", StringComparison.OrdinalIgnoreCase);
        }

        private static string RunTurnId(ChatRunRecord run)
        {
            if (run == null) return null;
            return string.IsNullOrWhiteSpace(run.TurnId) ? run.RunId : run.TurnId;
        }

        private static string ResolveStepId(string stepId, JToken data)
        {
            if (!string.IsNullOrWhiteSpace(stepId)) return stepId;
            var source = data as JObject;
            return source == null ? null : (string)(source["RequestId"] ?? source["requestId"]);
        }

        private static JObject BuildStepLifecycleData(
            JToken data,
            string status,
            bool synthetic,
            string sourceEventId)
        {
            var source = data as JObject;
            return new JObject
            {
                ["RequestId"] = JsonString(source, "RequestId", "requestId"),
                ["Purpose"] = JsonString(source, "Purpose", "purpose"),
                ["Model"] = JsonString(source, "Model", "model"),
                ["ResponseFormat"] = JsonString(source, "ResponseFormat", "responseFormat"),
                ["Status"] = status ?? string.Empty,
                ["Synthetic"] = synthetic,
                ["FailureKind"] = JsonString(source, "FailureKind", "failureKind"),
                ["Error"] = JsonString(source, "Error", "error"),
                ["SourceEventId"] = string.IsNullOrWhiteSpace(sourceEventId)
                    ? JValue.CreateNull()
                    : new JValue(sourceEventId)
            };
        }

        private static string StepTerminalStatus(string eventType, JToken data)
        {
            if (string.Equals(eventType, SessionEventTypes.LlmResponse, StringComparison.Ordinal)) return "completed";
            var source = data as JObject;
            var failureKind = source == null ? null : (string)(source["FailureKind"] ?? source["failureKind"]);
            return !string.IsNullOrWhiteSpace(failureKind) &&
                (failureKind.IndexOf("cancel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 failureKind.IndexOf("OperationCanceled", StringComparison.OrdinalIgnoreCase) >= 0)
                    ? "cancelled"
                    : "failed";
        }

        private static JToken JsonString(JObject source, string primary, string alternate)
        {
            if (source == null) return JValue.CreateNull();
            var value = source[primary] ?? source[alternate];
            return value == null || value.Type == JTokenType.Null
                ? JValue.CreateNull()
                : new JValue((string)value);
        }

        private static List<string> OpenStepIds(IEnumerable<SessionEvent> events, string runId)
        {
            var open = new List<string>();
            foreach (var sessionEvent in events ?? new List<SessionEvent>())
            {
                if (sessionEvent == null ||
                    !string.Equals(sessionEvent.RunId, runId, StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(sessionEvent.StepId)) continue;
                if (string.Equals(sessionEvent.Type, SessionEventTypes.StepStarted, StringComparison.Ordinal))
                {
                    if (!open.Contains(sessionEvent.StepId, StringComparer.OrdinalIgnoreCase)) open.Add(sessionEvent.StepId);
                }
                else if (string.Equals(sessionEvent.Type, SessionEventTypes.StepEnded, StringComparison.Ordinal))
                {
                    open.RemoveAll(value => string.Equals(value, sessionEvent.StepId, StringComparison.OrdinalIgnoreCase));
                }
            }
            return open;
        }

        private static string TurnIdForRun(IEnumerable<SessionEvent> events, string runId)
        {
            return (events ?? new List<SessionEvent>())
                .Where(item => item != null &&
                    string.Equals(item.RunId, runId, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(item.TurnId))
                .Select(item => item.TurnId)
                .LastOrDefault() ?? runId;
        }

        private static void NormalizeSession(ChatSession session, string host, string documentKey, string documentTitle)
        {
            ChatSessionNormalizer.Normalize(session, host, documentKey, documentTitle);
        }

        private sealed class EventLogReadResult
        {
            public List<SessionEvent> Events { get; private set; }
            public bool HasIncompleteTail { get; set; }
            public bool IsStableSnapshot { get; set; }
            public long ByteLength { get; set; }
            public long LastWriteUtcTicks { get; set; }
            public long TailNextByteOffset { get; set; }

            public EventLogReadResult()
            {
                Events = new List<SessionEvent>();
            }
        }

        private sealed class StorageFileState
        {
            public long ByteLength { get; set; }
            public long LastWriteUtcTicks { get; set; }
        }

        private sealed class HeaderReadResult
        {
            public ChatHeaderReducer Reducer { get; set; }
            public SessionEvent Tail { get; set; }
            public bool HasIncompleteTail { get; set; }
            public bool IsStableSnapshot { get; set; }
            public long ByteLength { get; set; }
            public long LastWriteUtcTicks { get; set; }
            public long TailNextByteOffset { get; set; }
        }

        private sealed class HeaderCacheEntry
        {
            public string SessionId { get; set; }
            public long Sequence { get; set; }
            public string HeadHash { get; set; }
            public long TailByteOffset { get; set; }
            public long ByteLength { get; set; }
            public long LastWriteUtcTicks { get; set; }
            public ChatHeaderReducer Reducer { get; set; }
            public long EstimatedCharacters { get; set; }
        }

        private sealed class ProjectionCacheEntry
        {
            public string SessionId { get; set; }
            public long Sequence { get; set; }
            public string HeadHash { get; set; }
            public long TailByteOffset { get; set; }
            public long ByteLength { get; set; }
            public long LastWriteUtcTicks { get; set; }
            public JObject Root { get; set; }
            public long EstimatedCharacters { get; set; }
        }

        private sealed class PendingSessionEvent
        {
            public string EventId { get; set; }
            public DateTime CreatedUtc { get; set; }
            public string Type { get; set; }
            public string RunId { get; set; }
            public string TurnId { get; set; }
            public string StepId { get; set; }
            public JToken Data { get; set; }
            public ChatBlobReference Payload { get; set; }
        }

    }
}
