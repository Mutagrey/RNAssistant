using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
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
    public sealed class ChatStore
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
        private static readonly JsonSerializerSettings ProjectionJsonSettings = new JsonSerializerSettings
        {
            ContractResolver = new ChatProjectionContractResolver(),
            DateTimeZoneHandling = DateTimeZoneHandling.Utc
        };

        private static readonly string[] MetadataProperties =
        {
            "FormatVersion", "Id", "ParentSessionId", "ParentSessionRevision", "ForkedThroughMessageId",
            "Host", "DocumentKey", "DocumentTitle", "DocumentPath",
            "Title", "Model", "Mode", "HtmlModeEnabled", "ReasoningEnabled", "CreatedUtc", "UpdatedUtc"
        };

        private readonly AppDataPaths _paths;
        private readonly ChatBlobStore _blobs;
        private readonly Func<StorageProtector> _protectionProvider;
        private readonly object _projectionCacheSync = new object();
        private readonly Dictionary<string, ProjectionCacheEntry> _projectionCache =
            new Dictionary<string, ProjectionCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly object _headerCacheSync = new object();
        private readonly Dictionary<string, HeaderCacheEntry> _headerCache =
            new Dictionary<string, HeaderCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private long _projectionCacheClock;
        private long _projectionCacheCharacters;
        private long _projectionFullReplayCount;
        private long _projectionIncrementalReplayCount;
        private long _headerCacheClock;
        private long _headerCacheCharacters;
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
            string[] paths;
            try
            {
                paths = Directory.Exists(_paths.ChatDirectory)
                    ? Directory.GetFiles(_paths.ChatDirectory, "*" + EventFileSuffix, SearchOption.AllDirectories)
                    : new string[0];
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                scan.AddSourceIssue(
                    CasHealthIssueKinds.SourceUnreadable,
                    "chat",
                    "chats",
                    "Chat event streams could not be enumerated: " + ex.Message);
                return;
            }

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
                    Directory.Delete(directory, true);
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
                RewriteValidEvents(path, log.Events);
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

        private static JObject ReplayProjectionRoot(IEnumerable<SessionEvent> events, JObject seedRoot)
        {
            var root = seedRoot == null ? null : (JObject)seedRoot.DeepClone();
            var replay = root == null ? null : new ProjectionReplayState(root);
            foreach (var sessionEvent in events ?? new List<SessionEvent>())
            {
                if (string.Equals(sessionEvent.Type, SessionEventTypes.SessionCreated, StringComparison.Ordinal) ||
                    string.Equals(sessionEvent.Type, SessionEventTypes.SessionForked, StringComparison.Ordinal))
                {
                    if (root != null || sessionEvent.Data == null || sessionEvent.Data.Type != JTokenType.Object) return null;
                    root = (JObject)sessionEvent.Data.DeepClone();
                    replay = new ProjectionReplayState(root);
                    continue;
                }
                if (!string.Equals(sessionEvent.Type, SessionEventTypes.SessionCommit, StringComparison.Ordinal)) continue;
                if (root == null || sessionEvent.Data == null) return null;
                var operations = sessionEvent.Data["Operations"] == null
                    ? new List<SessionOperation>()
                    : sessionEvent.Data["Operations"].ToObject<List<SessionOperation>>();
                ApplyOperations(root, operations, replay);
            }
            if (root == null || replay == null) return null;
            replay.Materialize(root);
            return root;
        }

        private ChatSession Project(
            JObject root,
            long sequence,
            string headHash,
            long tailByteOffset,
            long byteLength,
            long lastWriteUtcTicks,
            bool hydrateActiveArtifacts,
            bool rebuildDerivedProjections)
        {
            if (root == null) return null;
            var session = root.ToObject<ChatSession>();
            session.Revision = sequence;
            session.StorageHeadHash = headHash;
            session.StorageTailByteOffset = tailByteOffset;
            session.StorageByteLength = byteLength;
            session.StorageLastWriteUtcTicks = lastWriteUtcTicks;
            if (rebuildDerivedProjections)
            {
                RebuildHtmlWorkspaceProjection(session);
                RebuildContextCheckpointProjection(session);
                RebuildChartActivityProjection(session);
            }
            if (hydrateActiveArtifacts)
            {
                foreach (var artifact in (session.Artifacts ?? new List<ChatArtifact>()).Where(ShouldHydrateForActiveSession))
                {
                    HydrateArtifact(artifact);
                }
            }
            return session;
        }

        private static List<SessionOperation> BuildOperations(ChatSession beforeSession, ChatSession afterSession)
        {
            var before = ToProjectionToken(beforeSession);
            var after = ToProjectionToken(afterSession);
            var operations = new List<SessionOperation>();

            var metadata = new JObject();
            foreach (var property in MetadataProperties)
            {
                if (!JToken.DeepEquals(before[property], after[property]))
                {
                    metadata[property] = after[property] == null ? JValue.CreateNull() : after[property].DeepClone();
                }
            }
            if (metadata.HasValues) operations.Add(Operation(SessionOperationTypes.SessionMetadataSet, metadata));

            AddSetOperation(operations, before, after, "Context", SessionOperationTypes.ContextSet);
            AddRunOperation(operations, before["LastRun"], after["LastRun"]);
            AddListOperations(operations, before, after, "Messages", "Id",
                SessionOperationTypes.MessageUpsert, SessionOperationTypes.MessageRemove, SessionOperationTypes.MessagesReorder);
            AddListOperations(operations, before, after, "Artifacts", "Id",
                SessionOperationTypes.ArtifactUpsert, SessionOperationTypes.ArtifactRemove, SessionOperationTypes.ArtifactsReorder);

            var active = new JObject();
            foreach (var property in new[] { "ActiveContextCheckpointId", "ActiveHtmlArtifactId", "ActivePlanArtifactId" })
            {
                if (!JToken.DeepEquals(before[property], after[property]))
                {
                    active[property] = after[property] == null ? JValue.CreateNull() : after[property].DeepClone();
                }
            }
            if (active.HasValues) operations.Add(Operation(SessionOperationTypes.ActiveReferencesSet, active));
            return operations;
        }

        private static void AddSetOperation(
            ICollection<SessionOperation> operations,
            JObject before,
            JObject after,
            string property,
            string operationType)
        {
            if (!JToken.DeepEquals(before[property], after[property]))
            {
                operations.Add(Operation(operationType, new JObject
                {
                    ["Value"] = after[property] == null ? JValue.CreateNull() : after[property].DeepClone()
                }));
            }
        }

        private static void AddRunOperation(ICollection<SessionOperation> operations, JToken before, JToken after)
        {
            if (JToken.DeepEquals(before, after)) return;
            var type = IsNull(before)
                ? SessionOperationTypes.RunStarted
                : IsNull(after)
                    ? SessionOperationTypes.RunEnded
                    : SessionOperationTypes.RunUpdated;
            var data = new JObject
            {
                ["Value"] = after == null ? JValue.CreateNull() : after.DeepClone()
            };
            if (string.Equals(type, SessionOperationTypes.RunEnded, StringComparison.Ordinal))
            {
                data["Previous"] = before == null ? JValue.CreateNull() : before.DeepClone();
            }
            operations.Add(Operation(type, data));
        }

        private static bool IsNull(JToken value)
        {
            return value == null || value.Type == JTokenType.Null || value.Type == JTokenType.Undefined;
        }

        private static void AddListOperations(
            ICollection<SessionOperation> operations,
            JObject before,
            JObject after,
            string property,
            string idProperty,
            string upsertType,
            string removeType,
            string reorderType)
        {
            var beforeItems = (before[property] as JArray ?? new JArray()).OfType<JObject>().ToList();
            var afterItems = (after[property] as JArray ?? new JArray()).OfType<JObject>().ToList();
            var beforeById = beforeItems.Where(item => !string.IsNullOrWhiteSpace((string)item[idProperty]))
                .ToDictionary(item => (string)item[idProperty], item => item, StringComparer.OrdinalIgnoreCase);
            var afterById = afterItems.Where(item => !string.IsNullOrWhiteSpace((string)item[idProperty]))
                .ToDictionary(item => (string)item[idProperty], item => item, StringComparer.OrdinalIgnoreCase);

            foreach (var item in afterItems)
            {
                var id = (string)item[idProperty];
                JObject previous = null;
                var existed = !string.IsNullOrWhiteSpace(id) && beforeById.TryGetValue(id, out previous);
                if (!existed || !JToken.DeepEquals(previous, item))
                {
                    operations.Add(Operation(ResolveUpsertType(property, upsertType, previous, item),
                        new JObject { ["Value"] = item.DeepClone() }));
                }
            }
            foreach (var item in beforeItems)
            {
                var id = (string)item[idProperty];
                if (!string.IsNullOrWhiteSpace(id) && !afterById.ContainsKey(id))
                {
                    operations.Add(Operation(removeType, new JObject { ["Id"] = id }));
                }
            }

            var beforeOrder = beforeItems.Select(item => (string)item[idProperty]).ToList();
            var afterOrder = afterItems.Select(item => (string)item[idProperty]).ToList();
            var replayOrder = beforeOrder
                .Where(id => !string.IsNullOrWhiteSpace(id) && afterById.ContainsKey(id))
                .ToList();
            replayOrder.AddRange(afterOrder.Where(id =>
                !string.IsNullOrWhiteSpace(id) && !beforeById.ContainsKey(id)));
            if (!replayOrder.SequenceEqual(afterOrder, StringComparer.OrdinalIgnoreCase))
            {
                operations.Add(Operation(reorderType, new JObject { ["Ids"] = JArray.FromObject(afterOrder) }));
            }
        }

        private static SessionOperation Operation(string type, JObject data)
        {
            return new SessionOperation { Type = type, Data = data ?? new JObject() };
        }

        private static string ResolveUpsertType(string property, string fallback, JObject previous, JObject item)
        {
            if (string.Equals(property, "Artifacts", StringComparison.Ordinal))
            {
                return SessionOperationTypes.ArtifactRevisionCreated;
            }
            if (!string.Equals(property, "Messages", StringComparison.Ordinal)) return fallback;

            var activity = item["Activity"] as JObject;
            var status = activity == null ? null : (string)activity["Status"];
            var executionStatus = activity == null ? null : (string)activity["ExecutionStatus"];
            var toolCallId = activity == null ? null : (string)activity["ToolCallId"];
            if (!string.IsNullOrWhiteSpace(toolCallId) && string.Equals(status, "running", StringComparison.OrdinalIgnoreCase))
            {
                return SessionOperationTypes.ToolExecutionStarted;
            }
            if (!string.IsNullOrWhiteSpace(toolCallId) &&
                (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(status, "waiting", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(status, "waiting_confirmation", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(executionStatus, "waiting_confirmation", StringComparison.OrdinalIgnoreCase)))
            {
                return SessionOperationTypes.ToolExecutionFinished;
            }
            if ((bool?)item["ProtocolMessage"] == true)
            {
                var calls = item["ToolCalls"] as JArray;
                return calls != null && calls.Count > 0
                    ? SessionOperationTypes.ToolCallRecorded
                    : SessionOperationTypes.ToolResultRecorded;
            }
            if (previous == null && string.Equals((string)item["Role"], "user", StringComparison.OrdinalIgnoreCase))
            {
                return SessionOperationTypes.UserMessageAppended;
            }
            if (previous == null && string.Equals((string)item["Role"], "assistant", StringComparison.OrdinalIgnoreCase))
            {
                return SessionOperationTypes.AssistantMessageAppended;
            }
            return fallback;
        }

        private static void ApplyOperations(
            JObject root,
            IEnumerable<SessionOperation> operations,
            ProjectionReplayState replay)
        {
            foreach (var operation in operations ?? new List<SessionOperation>())
            {
                if (operation == null || string.IsNullOrWhiteSpace(operation.Type)) continue;
                var data = operation.Data ?? new JObject();
                switch (operation.Type)
                {
                    case SessionOperationTypes.SessionMetadataSet:
                    case SessionOperationTypes.ActiveReferencesSet:
                        foreach (var property in data.Properties()) root[property.Name] = property.Value.DeepClone();
                        break;
                    case SessionOperationTypes.ContextSet:
                        root["Context"] = CloneValue(data["Value"]);
                        break;
                    case SessionOperationTypes.RunStarted:
                    case SessionOperationTypes.RunUpdated:
                    case SessionOperationTypes.RunEnded:
                        root["LastRun"] = CloneValue(data["Value"]);
                        break;
                    case SessionOperationTypes.MessageUpsert:
                    case SessionOperationTypes.UserMessageAppended:
                    case SessionOperationTypes.AssistantMessageAppended:
                    case SessionOperationTypes.ToolCallRecorded:
                    case SessionOperationTypes.ToolResultRecorded:
                    case SessionOperationTypes.ToolExecutionStarted:
                    case SessionOperationTypes.ToolExecutionFinished:
                        replay.Upsert("Messages", data["Value"]);
                        break;
                    case SessionOperationTypes.MessageRemove:
                        replay.Remove("Messages", (string)data["Id"]);
                        break;
                    case SessionOperationTypes.MessagesReorder:
                        replay.Reorder("Messages", data["Ids"] as JArray);
                        break;
                    case SessionOperationTypes.ArtifactUpsert:
                    case SessionOperationTypes.ArtifactRevisionCreated:
                        replay.Upsert("Artifacts", data["Value"]);
                        break;
                    case SessionOperationTypes.ArtifactRemove:
                        replay.Remove("Artifacts", (string)data["Id"]);
                        break;
                    case SessionOperationTypes.ArtifactsReorder:
                        replay.Reorder("Artifacts", data["Ids"] as JArray);
                        break;
                    default:
                        throw new JsonException("Unsupported session operation: " + operation.Type);
                }
            }
        }

        private static JToken CloneValue(JToken value)
        {
            return value == null ? JValue.CreateNull() : value.DeepClone();
        }

        private void ExternalizeArtifacts(ChatSession session)
        {
            foreach (var artifact in session.Artifacts ?? new List<ChatArtifact>())
            {
                if (artifact == null || string.IsNullOrEmpty(artifact.InlineText)) continue;
                if (CanReuseArtifactBody(artifact)) continue;
                Interlocked.Increment(ref _artifactCasExternalizationCount);
                var reference = _blobs.StoreText(artifact.InlineText,
                    string.IsNullOrWhiteSpace(artifact.MimeType) ? "text/plain; charset=utf-8" : artifact.MimeType,
                    ArtifactBodyReference(artifact));
                artifact.ContentSha256 = reference.Sha256;
                artifact.ContentByteLength = reference.ByteLength;
                RememberArtifactBody(artifact);
            }
        }

        private bool CanReuseArtifactBody(ChatArtifact artifact)
        {
            return artifact != null && artifact.ContentByteLength.HasValue &&
                artifact.StorageContentByteLength.HasValue &&
                artifact.ContentByteLength.Value == artifact.StorageContentByteLength.Value &&
                artifact.StorageInlineTextTrusted &&
                string.Equals(artifact.ContentSha256, artifact.StorageContentSha256, StringComparison.OrdinalIgnoreCase) &&
                _blobs.HasStoredReference(ArtifactBodyReference(artifact));
        }

        private static ChatBlobReference ArtifactBodyReference(ChatArtifact artifact)
        {
            return artifact == null || !artifact.ContentByteLength.HasValue
                ? null
                : new ChatBlobReference
                {
                    Sha256 = artifact.ContentSha256,
                    ByteLength = artifact.ContentByteLength.Value,
                    ContentType = artifact.MimeType
                };
        }

        private static void RememberArtifactBody(ChatArtifact artifact)
        {
            if (artifact == null) return;
            artifact.StorageInlineTextTrusted = true;
            artifact.StorageContentSha256 = artifact.ContentSha256;
            artifact.StorageContentByteLength = artifact.ContentByteLength;
        }

        private void EnsureWorkspaceArtifact(ChatSession session)
        {
            if (session == null) return;
            var workspace = session.HtmlWorkspace ?? new HtmlWorkspace();
            var hasContent = (workspace.Files != null && workspace.Files.Any(item => item != null)) ||
                (workspace.DataSources != null && workspace.DataSources.Any(item => item != null));
            if (session.HtmlWorkspaceRecovery != null && !session.HtmlWorkspaceRecovery.CanMutate)
            {
                if (hasContent)
                {
                    throw new InvalidOperationException("HTML workspace mutation is blocked until a healthy revision is selected.");
                }
                return;
            }
            var current = FindArtifact(session, session.ActiveHtmlArtifactId);
            if (!hasContent && current == null) return;
            if (current != null) HydrateArtifact(current);

            var snapshot = HtmlWorkspaceCopyService.CaptureSnapshot(workspace, "HTML workspace");
            if (current != null && WorkspaceStateEquals(current.InlineText, snapshot)) return;
            var artifact = new ChatArtifact
            {
                Kind = ChatArtifactKinds.HtmlWorkspace,
                Title = "HTML workspace",
                MimeType = "application/vnd.rnassistant.html-workspace+json",
                ParentArtifactId = current == null ? null : current.Id,
                Revision = current == null ? 1 : Math.Max(1, current.Revision + 1),
                InlineText = SerializeWorkspaceState(snapshot),
                ModelContextPolicy = "reference",
                MetadataJson = JsonConvert.SerializeObject(new
                {
                    activeFileId = snapshot.ActiveFileId,
                    fileCount = snapshot.Files.Count,
                    dataSourceCount = snapshot.DataSources.Count
                })
            };
            session.Artifacts = session.Artifacts ?? new List<ChatArtifact>();
            session.Artifacts.Add(artifact);
            session.ActiveHtmlArtifactId = artifact.Id;
        }

        private static void EnsureChartArtifacts(ChatSession session)
        {
            if (session == null) return;
            session.Artifacts = session.Artifacts ?? new List<ChatArtifact>();
            foreach (var message in session.Messages ?? new List<ChatMessage>())
            {
                var activity = message == null ? null : message.Activity;
                JObject chart;
                if (activity == null || !TryParseChart(activity.DataJson, out chart)) continue;
                message.ArtifactIds = message.ArtifactIds ?? new List<string>();
                var linked = session.Artifacts.LastOrDefault(item => item != null &&
                    message.ArtifactIds.Contains(item.Id, StringComparer.OrdinalIgnoreCase) &&
                    string.Equals(item.Kind, ChatArtifactKinds.Chart, StringComparison.OrdinalIgnoreCase));
                var normalized = chart.ToString(Formatting.None);
                if (linked != null && string.Equals(linked.InlineText, normalized, StringComparison.Ordinal)) continue;
                var artifact = new ChatArtifact
                {
                    Kind = ChatArtifactKinds.Chart,
                    Title = (string)chart["title"] ?? (string)chart["Title"] ?? activity.Title ?? "Диаграмма",
                    MimeType = "application/vnd.rnassistant.chart+json",
                    SourceMessageId = message.Id,
                    RunId = message.RunId,
                    ParentArtifactId = linked == null ? null : linked.Id,
                    Revision = linked == null ? 1 : Math.Max(1, linked.Revision + 1),
                    InlineText = normalized,
                    ModelContextPolicy = "reference"
                };
                session.Artifacts.Add(artifact);
                if (linked != null) message.ArtifactIds.RemoveAll(id =>
                    string.Equals(id, linked.Id, StringComparison.OrdinalIgnoreCase));
                message.ArtifactIds.Add(artifact.Id);
            }
        }

        private void RebuildChartActivityProjection(ChatSession session)
        {
            if (session == null) return;
            foreach (var message in session.Messages ?? new List<ChatMessage>())
            {
                if (message == null || message.Activity == null) continue;
                var artifact = (session.Artifacts ?? new List<ChatArtifact>()).LastOrDefault(item => item != null &&
                    (message.ArtifactIds ?? new List<string>()).Contains(item.Id, StringComparer.OrdinalIgnoreCase) &&
                    string.Equals(item.Kind, ChatArtifactKinds.Chart, StringComparison.OrdinalIgnoreCase));
                if (artifact == null || !HydrateArtifact(artifact)) continue;
                message.Activity.DataJson = artifact.InlineText;
            }
        }

        private static bool TryParseChart(string json, out JObject chart)
        {
            chart = null;
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                chart = JObject.Parse(json);
                var type = (string)chart["type"] ?? (string)chart["Type"];
                if (string.Equals(type, "rnassistant.chart", StringComparison.OrdinalIgnoreCase)) return true;
                chart = null;
                return false;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private void RebuildHtmlWorkspaceProjection(ChatSession session)
        {
            if (session == null) return;
            var activeId = session.ActiveHtmlArtifactId;
            if (string.IsNullOrWhiteSpace(activeId))
            {
                session.HtmlWorkspace = new HtmlWorkspace();
                session.HtmlWorkspaceRecovery = HtmlWorkspaceNavigationService.CreateRecoveryState(
                    session, HtmlWorkspaceRecoveryStatuses.Empty, null, null, null, null, true);
                return;
            }

            var active = FindHtmlArtifact(session, activeId);
            if (active == null)
            {
                session.HtmlWorkspace = new HtmlWorkspace();
                session.HtmlWorkspaceRecovery = HtmlWorkspaceNavigationService.CreateRecoveryState(
                    session,
                    HtmlWorkspaceRecoveryStatuses.Degraded,
                    HtmlWorkspaceRecoveryIssues.ActiveArtifactMissing,
                    "The active HTML workspace revision metadata is missing. Select another revision before editing.",
                    activeId,
                    activeId,
                    false);
                return;
            }
            if (!HydrateArtifact(active))
            {
                session.HtmlWorkspace = new HtmlWorkspace();
                session.HtmlWorkspaceRecovery = HtmlWorkspaceNavigationService.CreateRecoveryState(
                    session,
                    HtmlWorkspaceRecoveryStatuses.Degraded,
                    HtmlWorkspaceRecoveryIssues.ActiveBodyUnavailable,
                    "The active HTML workspace body is missing, corrupt, or cannot be decrypted. Select another revision before editing.",
                    activeId,
                    activeId,
                    false);
                return;
            }
            var activeSnapshot = ParseWorkspaceSnapshot(active);
            if (activeSnapshot == null)
            {
                session.HtmlWorkspace = new HtmlWorkspace();
                session.HtmlWorkspaceRecovery = HtmlWorkspaceNavigationService.CreateRecoveryState(
                    session,
                    HtmlWorkspaceRecoveryStatuses.Degraded,
                    HtmlWorkspaceRecoveryIssues.ActiveBodyInvalid,
                    "The active HTML workspace body is invalid. Select another revision before editing.",
                    activeId,
                    activeId,
                    false);
                return;
            }

            var workspace = HtmlWorkspaceCopyService.CreateWorkspaceFromSnapshot(activeSnapshot);
            workspace.UpdatedUtc = active.CreatedUtc;
            var current = active;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { active.Id };
            string issue = null;
            string message = null;
            string problemArtifactId = null;
            long historyCharacters = 0;
            while (!string.IsNullOrWhiteSpace(current.ParentArtifactId))
            {
                if (workspace.History.Count >= HtmlWorkspaceHistoryPolicy.MaxItems ||
                    historyCharacters >= HtmlWorkspaceHistoryPolicy.MaxContentCharacters)
                {
                    break;
                }
                problemArtifactId = current.ParentArtifactId;
                if (!visited.Add(problemArtifactId))
                {
                    issue = HtmlWorkspaceRecoveryIssues.LineageCycle;
                    message = "The HTML workspace revision lineage contains a cycle. The active revision is readable, but older undo history is incomplete.";
                    break;
                }
                current = FindHtmlArtifact(session, problemArtifactId);
                if (current == null)
                {
                    issue = HtmlWorkspaceRecoveryIssues.ParentArtifactMissing;
                    message = "An older HTML workspace revision is missing. The active revision is readable, but undo history is incomplete.";
                    break;
                }
                if (!HydrateArtifact(current))
                {
                    issue = HtmlWorkspaceRecoveryIssues.ParentBodyUnavailable;
                    message = "An older HTML workspace body is unavailable. The active revision is readable, but undo history is incomplete.";
                    break;
                }
                var snapshot = ParseWorkspaceSnapshot(current);
                if (snapshot == null)
                {
                    issue = HtmlWorkspaceRecoveryIssues.ParentBodyInvalid;
                    message = "An older HTML workspace body is invalid. The active revision is readable, but undo history is incomplete.";
                    break;
                }
                var snapshotCharacters = HtmlWorkspaceHistoryPolicy.EstimateContentCharacters(snapshot);
                if (snapshotCharacters > HtmlWorkspaceHistoryPolicy.MaxContentCharacters ||
                    historyCharacters + snapshotCharacters > HtmlWorkspaceHistoryPolicy.MaxContentCharacters)
                {
                    problemArtifactId = null;
                    break;
                }
                workspace.History.Add(snapshot);
                historyCharacters += snapshotCharacters;
            }

            workspace.RedoBranches = HtmlWorkspaceNavigationService.GetRedoBranches(session);
            session.HtmlWorkspace = workspace;
            session.HtmlWorkspaceRecovery = HtmlWorkspaceNavigationService.CreateRecoveryState(
                session,
                issue == null ? HtmlWorkspaceRecoveryStatuses.Healthy : HtmlWorkspaceRecoveryStatuses.Degraded,
                issue,
                message,
                active.Id,
                problemArtifactId,
                true);
        }

        private static HtmlWorkspaceSnapshot ParseWorkspaceSnapshot(ChatArtifact artifact)
        {
            if (artifact == null || string.IsNullOrWhiteSpace(artifact.InlineText)) return null;
            try
            {
                var snapshot = JsonConvert.DeserializeObject<HtmlWorkspaceSnapshot>(artifact.InlineText);
                if (snapshot == null) return null;
                snapshot.Id = artifact.Id;
                snapshot.Label = string.IsNullOrWhiteSpace(artifact.Title) ? "HTML workspace" : artifact.Title;
                snapshot.CreatedUtc = artifact.CreatedUtc;
                return snapshot;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static bool WorkspaceStateEquals(string existingJson, HtmlWorkspaceSnapshot candidate)
        {
            if (string.IsNullOrWhiteSpace(existingJson) || candidate == null) return false;
            try
            {
                var existing = JsonConvert.DeserializeObject<HtmlWorkspaceSnapshot>(existingJson);
                return existing != null &&
                    string.Equals(existing.ActiveFileId, candidate.ActiveFileId, StringComparison.OrdinalIgnoreCase) &&
                    JToken.DeepEquals(JArray.FromObject(existing.Files ?? new List<HtmlWorkspaceFile>()),
                        JArray.FromObject(candidate.Files ?? new List<HtmlWorkspaceFile>())) &&
                    JToken.DeepEquals(JArray.FromObject(existing.DataSources ?? new List<HtmlWorkspaceDataSource>()),
                        JArray.FromObject(candidate.DataSources ?? new List<HtmlWorkspaceDataSource>()));
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string SerializeWorkspaceState(HtmlWorkspaceSnapshot snapshot)
        {
            snapshot = snapshot ?? new HtmlWorkspaceSnapshot();
            return JsonConvert.SerializeObject(new
            {
                snapshot.ActiveFileId,
                Files = snapshot.Files ?? new List<HtmlWorkspaceFile>(),
                DataSources = snapshot.DataSources ?? new List<HtmlWorkspaceDataSource>()
            }, Formatting.None);
        }

        private void RebuildContextCheckpointProjection(ChatSession session)
        {
            if (session == null) return;
            var checkpoints = new List<ContextCheckpoint>();
            foreach (var artifact in (session.Artifacts ?? new List<ChatArtifact>())
                .Where(item => item != null &&
                    string.Equals(item.Kind, ChatArtifactKinds.Compaction, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.CreatedUtc))
            {
                if (!HydrateArtifact(artifact)) continue;
                try
                {
                    var checkpoint = JsonConvert.DeserializeObject<ContextCheckpoint>(artifact.InlineText);
                    if (checkpoint == null || string.IsNullOrWhiteSpace(checkpoint.ThroughMessageId)) continue;
                    checkpoint.Id = artifact.Id;
                    checkpoint.CreatedUtc = artifact.CreatedUtc;
                    checkpoints.Add(checkpoint);
                    var sourceMessage = (session.Messages ?? new List<ChatMessage>()).FirstOrDefault(item =>
                        item != null && string.Equals(item.Id, artifact.SourceMessageId, StringComparison.OrdinalIgnoreCase));
                    if (sourceMessage != null && sourceMessage.Activity != null &&
                        string.Equals(sourceMessage.Activity.Kind, "compaction", StringComparison.OrdinalIgnoreCase))
                    {
                        sourceMessage.Content = checkpoint.SummaryMarkdown;
                        sourceMessage.Activity.ResultMessage = checkpoint.SummaryMarkdown;
                        sourceMessage.Activity.DataJson = artifact.MetadataJson;
                    }
                }
                catch (JsonException)
                {
                }
            }
            session.ContextCheckpoints = checkpoints;
            if (!checkpoints.Any(item => string.Equals(item.Id, session.ActiveContextCheckpointId, StringComparison.OrdinalIgnoreCase)))
            {
                session.ActiveContextCheckpointId = null;
            }
        }

        private bool HydrateArtifact(ChatArtifact artifact)
        {
            if (artifact == null) return false;
            if (!string.IsNullOrEmpty(artifact.InlineText)) return true;
            if (string.IsNullOrWhiteSpace(artifact.ContentSha256) || !artifact.ContentByteLength.HasValue) return false;
            artifact.InlineText = _blobs.ReadText(ArtifactBodyReference(artifact));
            if (artifact.InlineText == null) return false;
            RememberArtifactBody(artifact);
            return true;
        }

        private static ChatArtifact FindArtifact(ChatSession session, string artifactId)
        {
            if (session == null || string.IsNullOrWhiteSpace(artifactId)) return null;
            return (session.Artifacts ?? new List<ChatArtifact>()).FirstOrDefault(item =>
                item != null && string.Equals(item.Id, artifactId, StringComparison.OrdinalIgnoreCase));
        }

        private static ChatArtifact FindHtmlArtifact(ChatSession session, string artifactId)
        {
            var artifact = FindArtifact(session, artifactId);
            return artifact != null && string.Equals(artifact.Kind, ChatArtifactKinds.HtmlWorkspace, StringComparison.OrdinalIgnoreCase)
                ? artifact
                : null;
        }

        private static bool ShouldHydrateForActiveSession(ChatArtifact artifact)
        {
            if (artifact == null || string.Equals(artifact.Kind, ChatArtifactKinds.HtmlWorkspace, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            var mimeType = artifact.MimeType ?? string.Empty;
            return mimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
                mimeType.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0 ||
                mimeType.IndexOf("xml", StringComparison.OrdinalIgnoreCase) >= 0 ||
                string.Equals(artifact.Kind, ChatArtifactKinds.Plan, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(artifact.Kind, ChatArtifactKinds.Markdown, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(artifact.Kind, ChatArtifactKinds.ToolResult, StringComparison.OrdinalIgnoreCase);
        }

        private static JObject ToProjectionToken(ChatSession session)
        {
            return JObject.FromObject(session, JsonSerializer.Create(ProjectionJsonSettings));
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
                using (var reader = new JsonlByteReader(path, startByteOffset))
                {
                    result.ByteLength = reader.Length;
                    JsonlByteLine line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line.Text))
                        {
                            if (!line.Terminated) result.HasIncompleteTail = true;
                            continue;
                        }
                        SessionEvent sessionEvent;
                        try
                        {
                            sessionEvent = JsonConvert.DeserializeObject<SessionEvent>(line.Text);
                        }
                        catch (JsonException)
                        {
                            if (!line.Terminated && line.NextOffset == reader.Length)
                            {
                                result.HasIncompleteTail = true;
                                break;
                            }
                            throw new ChatConcurrencyException("The chat event log contains an invalid record.");
                        }
                        ValidateEvent(previousEvent, sessionEvent, protector);
                        HydrateEventData(sessionEvent, protector);
                        sessionEvent.StorageByteOffset = line.Offset;
                        result.Events.Add(sessionEvent);
                        result.TailNextByteOffset = line.NextOffset;
                        previousEvent = sessionEvent;
                        if (!line.Terminated)
                        {
                            result.HasIncompleteTail = true;
                            break;
                        }
                    }
                }
                var after = CaptureStorageFileState(path);
                result.IsStableSnapshot = before != null && after != null &&
                    before.ByteLength == result.ByteLength && after.ByteLength == result.ByteLength &&
                    before.LastWriteUtcTicks == after.LastWriteUtcTicks;
                result.LastWriteUtcTicks = result.IsStableSnapshot ? after.LastWriteUtcTicks : 0;
            }
            catch (DecoderFallbackException)
            {
                throw new ChatConcurrencyException("The chat event log contains invalid UTF-8.");
            }
            return result;
        }

        private HeaderReadResult ReadHeader(string path)
        {
            HeaderReadResult cached;
            if (TryReadHeaderCache(path, out cached)) return cached;

            var result = ReadHeaderLog(path, 0, null, new ChatHeaderReducer(_blobs));
            if (result != null && result.Tail != null)
            {
                Interlocked.Increment(ref _headerFullReplayCount);
                if (CanCacheHeader(result)) StoreHeaderCache(path, result);
            }
            return result;
        }

        private HeaderReadResult ReadHeaderLog(
            string path,
            long startByteOffset,
            SessionEvent previousEvent,
            ChatHeaderReducer reducer)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var result = new HeaderReadResult
            {
                Reducer = reducer ?? new ChatHeaderReducer(_blobs),
                Tail = previousEvent,
                TailNextByteOffset = startByteOffset
            };
            var protector = Protection();
            var before = CaptureStorageFileState(path);
            try
            {
                using (var reader = new JsonlByteReader(path, startByteOffset))
                {
                    result.ByteLength = reader.Length;
                    JsonlByteLine line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line.Text))
                        {
                            if (!line.Terminated) result.HasIncompleteTail = true;
                            continue;
                        }
                        SessionEvent sessionEvent;
                        try
                        {
                            sessionEvent = JsonConvert.DeserializeObject<SessionEvent>(line.Text);
                        }
                        catch (JsonException)
                        {
                            if (!line.Terminated && line.NextOffset == reader.Length)
                            {
                                result.HasIncompleteTail = true;
                                break;
                            }
                            throw new ChatConcurrencyException("The chat event log contains an invalid record.");
                        }
                        ValidateEvent(previousEvent, sessionEvent, protector);
                        HydrateEventData(sessionEvent, protector);
                        sessionEvent.StorageByteOffset = line.Offset;
                        result.Reducer.Apply(sessionEvent);
                        result.Tail = sessionEvent;
                        result.TailNextByteOffset = line.NextOffset;
                        previousEvent = sessionEvent;
                        if (!line.Terminated)
                        {
                            result.HasIncompleteTail = true;
                            break;
                        }
                    }
                }
                var after = CaptureStorageFileState(path);
                result.IsStableSnapshot = before != null && after != null &&
                    before.ByteLength == result.ByteLength && after.ByteLength == result.ByteLength &&
                    before.LastWriteUtcTicks == after.LastWriteUtcTicks;
                result.LastWriteUtcTicks = result.IsStableSnapshot ? after.LastWriteUtcTicks : 0;
            }
            catch (DecoderFallbackException)
            {
                throw new ChatConcurrencyException("The chat event log contains invalid UTF-8.");
            }
            return result;
        }

        private static bool CanCacheHeader(HeaderReadResult result)
        {
            return result != null && result.Reducer != null && result.Reducer.IsValid &&
                result.Tail != null && !result.HasIncompleteTail && result.IsStableSnapshot &&
                result.TailNextByteOffset == result.ByteLength;
        }

        private bool TryReadHeaderCache(string path, out HeaderReadResult result)
        {
            result = null;
            HeaderCacheEntry cached;
            if (!TryGetHeaderCache(path, out cached)) return false;

            var current = CaptureStorageFileState(path);
            if (current == null || current.ByteLength < cached.ByteLength ||
                current.ByteLength == cached.ByteLength && current.LastWriteUtcTicks != cached.LastWriteUtcTicks)
            {
                RemoveHeaderCache(path);
                return false;
            }

            var boundary = ReadValidatedEventAtOffset(
                path,
                cached.SessionId,
                cached.Sequence,
                cached.HeadHash,
                cached.TailByteOffset,
                cached.ByteLength,
                current.ByteLength);
            if (boundary == null)
            {
                RemoveHeaderCache(path);
                return false;
            }

            if (current.ByteLength == cached.ByteLength)
            {
                result = new HeaderReadResult
                {
                    Reducer = cached.Reducer,
                    Tail = boundary,
                    ByteLength = cached.ByteLength,
                    LastWriteUtcTicks = cached.LastWriteUtcTicks,
                    TailNextByteOffset = cached.ByteLength,
                    IsStableSnapshot = true
                };
                return true;
            }

            HeaderReadResult suffix;
            try
            {
                suffix = ReadHeaderLog(path, cached.ByteLength, boundary, cached.Reducer.Clone());
            }
            catch
            {
                RemoveHeaderCache(path);
                throw;
            }
            if (suffix == null)
            {
                RemoveHeaderCache(path);
                return false;
            }

            if (CanCacheHeader(suffix))
            {
                StoreHeaderCache(path, suffix);
                Interlocked.Increment(ref _headerIncrementalReplayCount);
            }
            else
            {
                RemoveHeaderCache(path);
            }
            result = suffix;
            return true;
        }

        private bool TryGetHeaderCache(string path, out HeaderCacheEntry entry)
        {
            entry = null;
            var key = ProjectionCacheKey(path);
            lock (_headerCacheSync)
            {
                if (!_headerCache.TryGetValue(key, out entry)) return false;
                entry.LastAccess = ++_headerCacheClock;
                return true;
            }
        }

        private HeaderCacheEntry StoreHeaderCache(string path, HeaderReadResult result)
        {
            if (!CanCacheHeader(result)) return null;
            var estimatedCharacters = result.Reducer.EstimatedCharacters;
            var key = ProjectionCacheKey(path);
            var entry = new HeaderCacheEntry
            {
                SessionId = result.Tail.SessionId,
                Sequence = result.Tail.Sequence,
                HeadHash = result.Tail.Hash,
                TailByteOffset = result.Tail.StorageByteOffset,
                ByteLength = result.ByteLength,
                LastWriteUtcTicks = result.LastWriteUtcTicks,
                Reducer = result.Reducer,
                EstimatedCharacters = estimatedCharacters
            };
            lock (_headerCacheSync)
            {
                HeaderCacheEntry replaced;
                if (_headerCache.TryGetValue(key, out replaced))
                {
                    _headerCacheCharacters -= replaced.EstimatedCharacters;
                    _headerCache.Remove(key);
                }
                if (estimatedCharacters > MaxHeaderCacheCharacters) return entry;
                entry.LastAccess = ++_headerCacheClock;
                _headerCache[key] = entry;
                _headerCacheCharacters += estimatedCharacters;
                while (_headerCache.Count > MaxHeaderCacheEntries ||
                    _headerCacheCharacters > MaxHeaderCacheTotalCharacters)
                {
                    var oldest = _headerCache.OrderBy(item => item.Value.LastAccess).First();
                    _headerCacheCharacters -= oldest.Value.EstimatedCharacters;
                    _headerCache.Remove(oldest.Key);
                }
            }
            return entry;
        }

        private void RemoveHeaderCache(string path)
        {
            var key = ProjectionCacheKey(path);
            lock (_headerCacheSync)
            {
                HeaderCacheEntry removed;
                if (!_headerCache.TryGetValue(key, out removed)) return;
                _headerCacheCharacters -= removed.EstimatedCharacters;
                _headerCache.Remove(key);
            }
        }

        private void ClearHeaderCache()
        {
            lock (_headerCacheSync)
            {
                _headerCache.Clear();
                _headerCacheCharacters = 0;
            }
        }

        private void MoveHeaderCache(string oldPath, string newPath)
        {
            var oldKey = ProjectionCacheKey(oldPath);
            var newKey = ProjectionCacheKey(newPath);
            lock (_headerCacheSync)
            {
                HeaderCacheEntry entry;
                if (!_headerCache.TryGetValue(oldKey, out entry)) return;
                HeaderCacheEntry replaced;
                if (!string.Equals(oldKey, newKey, StringComparison.OrdinalIgnoreCase) &&
                    _headerCache.TryGetValue(newKey, out replaced))
                {
                    _headerCacheCharacters -= replaced.EstimatedCharacters;
                    _headerCache.Remove(newKey);
                }
                _headerCache.Remove(oldKey);
                entry.LastAccess = ++_headerCacheClock;
                _headerCache[newKey] = entry;
            }
        }

        private SessionEvent ReadValidatedTail(
            string path,
            string sessionId,
            long expectedRevision,
            string expectedHeadHash,
            long expectedByteLength,
            long expectedLastWriteUtcTicks,
            long expectedTailByteOffset)
        {
            try
            {
                if (expectedByteLength <= 0 || expectedLastWriteUtcTicks <= 0 ||
                    expectedTailByteOffset < 0 || expectedTailByteOffset >= expectedByteLength) return null;
                var file = new FileInfo(path);
                if (!file.Exists || file.Length != expectedByteLength ||
                    file.LastWriteTimeUtc.Ticks != expectedLastWriteUtcTicks) return null;
                return ReadValidatedEventAtOffset(path, sessionId, expectedRevision, expectedHeadHash,
                    expectedTailByteOffset, expectedByteLength, expectedByteLength);
            }
            catch (Exception ex) when (
                ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentOutOfRangeException ||
                ex is JsonException || ex is DecoderFallbackException || ex is CryptographicException)
            {
                return null;
            }
        }

        private SessionEvent ReadValidatedEventAtOffset(
            string path,
            string sessionId,
            long expectedRevision,
            string expectedHeadHash,
            long expectedByteOffset,
            long expectedNextByteOffset,
            long expectedSnapshotLength)
        {
            try
            {
                JsonlByteLine line;
                using (var reader = new JsonlByteReader(path, expectedByteOffset))
                {
                    if (reader.Length != expectedSnapshotLength) return null;
                    line = reader.ReadLine();
                    if (line == null || !line.Terminated || line.NextOffset != expectedNextByteOffset ||
                        string.IsNullOrWhiteSpace(line.Text)) return null;
                }

                var sessionEvent = JsonConvert.DeserializeObject<SessionEvent>(line.Text);
                var protector = Protection();
                if (sessionEvent == null || sessionEvent.SchemaVersion != SessionEvent.CurrentSchemaVersion ||
                    sessionEvent.Sequence != expectedRevision ||
                    !string.Equals(sessionEvent.SessionId, sessionId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(sessionEvent.Hash, expectedHeadHash, StringComparison.OrdinalIgnoreCase) ||
                    !ValidHashAlgorithm(sessionEvent.HashAlgorithm) ||
                    !string.IsNullOrWhiteSpace(sessionEvent.EncryptedData) && sessionEvent.Data != null ||
                    !ProtectionMatches(sessionEvent, protector) ||
                    !string.Equals(sessionEvent.Hash, ComputeHash(sessionEvent, protector), StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
                sessionEvent.StorageByteOffset = line.Offset;
                return sessionEvent;
            }
            catch (Exception ex) when (
                ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentOutOfRangeException ||
                ex is JsonException || ex is DecoderFallbackException || ex is CryptographicException)
            {
                return null;
            }
        }

        private static void CaptureStorageState(ChatSession session, string path)
        {
            if (session == null) return;
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                session.StorageByteLength = 0;
                session.StorageLastWriteUtcTicks = 0;
                return;
            }
            file.Refresh();
            session.StorageByteLength = file.Length;
            session.StorageLastWriteUtcTicks = file.LastWriteTimeUtc.Ticks;
        }

        private static StorageFileState CaptureStorageFileState(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var file = new FileInfo(path);
            file.Refresh();
            return file.Exists
                ? new StorageFileState
                {
                    ByteLength = file.Length,
                    LastWriteUtcTicks = file.LastWriteTimeUtc.Ticks
                }
                : null;
        }

        private static bool CanCacheProjection(EventLogReadResult log)
        {
            return log != null && log.Events.Count > 0 && !log.HasIncompleteTail &&
                log.IsStableSnapshot && log.TailNextByteOffset == log.ByteLength;
        }

        private bool TryReadProjectionCache(string path, out ProjectionCacheEntry result)
        {
            result = null;
            ProjectionCacheEntry cached;
            if (!TryGetProjectionCache(path, out cached)) return false;

            var current = CaptureStorageFileState(path);
            if (current == null || current.ByteLength < cached.ByteLength ||
                current.ByteLength == cached.ByteLength &&
                current.LastWriteUtcTicks != cached.LastWriteUtcTicks)
            {
                RemoveProjectionCache(path);
                return false;
            }

            var boundary = ReadValidatedEventAtOffset(
                path,
                cached.SessionId,
                cached.Sequence,
                cached.HeadHash,
                cached.TailByteOffset,
                cached.ByteLength,
                current.ByteLength);
            if (boundary == null)
            {
                RemoveProjectionCache(path);
                return false;
            }

            if (current.ByteLength == cached.ByteLength)
            {
                result = cached;
                return true;
            }

            EventLogReadResult suffix;
            try
            {
                suffix = ReadEventLog(path, cached.ByteLength, boundary);
            }
            catch
            {
                RemoveProjectionCache(path);
                throw;
            }
            if (!CanCacheProjection(suffix))
            {
                RemoveProjectionCache(path);
                return false;
            }

            var root = suffix.Events.Any(IsProjectionEvent)
                ? ReplayProjectionRoot(suffix.Events, cached.Root)
                : cached.Root;
            if (root == null)
            {
                RemoveProjectionCache(path);
                return false;
            }
            var tail = LastEvent(suffix);
            result = StoreProjectionCache(path, root, tail.SessionId, tail.Sequence, tail.Hash,
                tail.StorageByteOffset, suffix.ByteLength, suffix.LastWriteUtcTicks);
            Interlocked.Increment(ref _projectionIncrementalReplayCount);
            return result != null;
        }

        private static bool IsProjectionEvent(SessionEvent sessionEvent)
        {
            return sessionEvent != null &&
                (string.Equals(sessionEvent.Type, SessionEventTypes.SessionCreated, StringComparison.Ordinal) ||
                 string.Equals(sessionEvent.Type, SessionEventTypes.SessionForked, StringComparison.Ordinal) ||
                 string.Equals(sessionEvent.Type, SessionEventTypes.SessionCommit, StringComparison.Ordinal));
        }

        private bool TryGetProjectionCache(string path, out ProjectionCacheEntry entry)
        {
            entry = null;
            var key = ProjectionCacheKey(path);
            lock (_projectionCacheSync)
            {
                if (!_projectionCache.TryGetValue(key, out entry)) return false;
                entry.LastAccess = ++_projectionCacheClock;
                return true;
            }
        }

        private void StoreProjectionCache(string path, JObject root, ChatSession session)
        {
            if (session == null) return;
            StoreProjectionCache(path, root, session.Id, session.Revision, session.StorageHeadHash,
                session.StorageTailByteOffset, session.StorageByteLength, session.StorageLastWriteUtcTicks);
        }

        private ProjectionCacheEntry StoreProjectionCache(
            string path,
            JObject root,
            string sessionId,
            long sequence,
            string headHash,
            long tailByteOffset,
            long byteLength,
            long lastWriteUtcTicks)
        {
            if (root == null || string.IsNullOrWhiteSpace(sessionId) || sequence <= 0 ||
                string.IsNullOrWhiteSpace(headHash) || tailByteOffset < 0 ||
                byteLength <= tailByteOffset || lastWriteUtcTicks <= 0) return null;
            var key = ProjectionCacheKey(path);
            var estimatedCharacters = EstimateProjectionCharacters(root, MaxProjectionCacheCharacters + 1);
            var entry = new ProjectionCacheEntry
            {
                SessionId = sessionId,
                Sequence = sequence,
                HeadHash = headHash,
                TailByteOffset = tailByteOffset,
                ByteLength = byteLength,
                LastWriteUtcTicks = lastWriteUtcTicks,
                Root = root,
                EstimatedCharacters = estimatedCharacters
            };
            lock (_projectionCacheSync)
            {
                ProjectionCacheEntry replaced;
                if (_projectionCache.TryGetValue(key, out replaced))
                {
                    _projectionCacheCharacters -= replaced.EstimatedCharacters;
                    _projectionCache.Remove(key);
                }
                if (estimatedCharacters > MaxProjectionCacheCharacters) return entry;
                entry.LastAccess = ++_projectionCacheClock;
                _projectionCache[key] = entry;
                _projectionCacheCharacters += estimatedCharacters;
                while (_projectionCache.Count > MaxProjectionCacheEntries ||
                    _projectionCacheCharacters > MaxProjectionCacheTotalCharacters)
                {
                    var oldest = _projectionCache.OrderBy(item => item.Value.LastAccess).First();
                    _projectionCacheCharacters -= oldest.Value.EstimatedCharacters;
                    _projectionCache.Remove(oldest.Key);
                }
            }
            return entry;
        }

        private static long EstimateProjectionCharacters(JToken root, long stopAfter)
        {
            long total = 0;
            var pending = new Stack<JToken>();
            pending.Push(root);
            while (pending.Count > 0 && total <= stopAfter)
            {
                var token = pending.Pop();
                var objectValue = token as JObject;
                if (objectValue != null)
                {
                    foreach (var property in objectValue.Properties())
                    {
                        total += property.Name.Length + 4L;
                        pending.Push(property.Value);
                    }
                    continue;
                }
                var arrayValue = token as JArray;
                if (arrayValue != null)
                {
                    total += arrayValue.Count;
                    foreach (var value in arrayValue) pending.Push(value);
                    continue;
                }
                var scalar = token as JValue;
                var text = scalar == null || scalar.Value == null ? null : scalar.Value as string;
                total += text == null ? 32L : text.Length + 2L;
            }
            return total;
        }

        private void AdvanceProjectionCache(
            string path,
            string sessionId,
            long expectedRevision,
            string expectedHeadHash,
            long expectedByteLength,
            IReadOnlyList<SessionEvent> appended)
        {
            if (appended == null || appended.Count == 0) return;
            ProjectionCacheEntry cached;
            if (!TryGetProjectionCache(path, out cached) ||
                cached.Sequence != expectedRevision || cached.ByteLength != expectedByteLength ||
                !string.Equals(cached.SessionId, sessionId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(cached.HeadHash, expectedHeadHash, StringComparison.OrdinalIgnoreCase)) return;
            var state = CaptureStorageFileState(path);
            if (state == null) return;
            var root = appended.Any(IsProjectionEvent)
                ? ReplayProjectionRoot(appended, cached.Root)
                : cached.Root;
            var tail = appended[appended.Count - 1];
            if (root == null || tail.StorageByteOffset >= state.ByteLength)
            {
                RemoveProjectionCache(path);
                return;
            }
            StoreProjectionCache(path, root, tail.SessionId, tail.Sequence, tail.Hash,
                tail.StorageByteOffset, state.ByteLength, state.LastWriteUtcTicks);
        }

        private void RemoveProjectionCache(string path)
        {
            var key = ProjectionCacheKey(path);
            lock (_projectionCacheSync)
            {
                ProjectionCacheEntry removed;
                if (!_projectionCache.TryGetValue(key, out removed)) return;
                _projectionCacheCharacters -= removed.EstimatedCharacters;
                _projectionCache.Remove(key);
            }
        }

        private void ClearProjectionCache()
        {
            lock (_projectionCacheSync)
            {
                _projectionCache.Clear();
                _projectionCacheCharacters = 0;
            }
        }

        private void MoveProjectionCache(string oldPath, string newPath)
        {
            var oldKey = ProjectionCacheKey(oldPath);
            var newKey = ProjectionCacheKey(newPath);
            lock (_projectionCacheSync)
            {
                ProjectionCacheEntry entry;
                if (!_projectionCache.TryGetValue(oldKey, out entry)) return;
                ProjectionCacheEntry replaced;
                if (!string.Equals(oldKey, newKey, StringComparison.OrdinalIgnoreCase) &&
                    _projectionCache.TryGetValue(newKey, out replaced))
                {
                    _projectionCacheCharacters -= replaced.EstimatedCharacters;
                    _projectionCache.Remove(newKey);
                }
                _projectionCache.Remove(oldKey);
                entry.LastAccess = ++_projectionCacheClock;
                _projectionCache[newKey] = entry;
            }
        }

        private static string ProjectionCacheKey(string path)
        {
            return Path.GetFullPath(path ?? string.Empty);
        }

        private static void ValidateEvent(
            SessionEvent previous,
            SessionEvent sessionEvent,
            StorageProtector protector)
        {
            if (sessionEvent == null || sessionEvent.SchemaVersion != SessionEvent.CurrentSchemaVersion ||
                string.IsNullOrWhiteSpace(sessionEvent.SessionId) || string.IsNullOrWhiteSpace(sessionEvent.Type) ||
                !ValidHashAlgorithm(sessionEvent.HashAlgorithm) ||
                !string.IsNullOrWhiteSpace(sessionEvent.EncryptedData) && sessionEvent.Data != null ||
                !ProtectionMatches(sessionEvent, protector))
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
            var plaintext = Utf8.GetBytes(sessionEvent.Data == null
                ? "null"
                : sessionEvent.Data.ToString(Formatting.None));
            sessionEvent.EncryptedData = Convert.ToBase64String(
                protector.Protect(plaintext, EventProtectionPurpose(sessionEvent)));
            sessionEvent.Data = null;
        }

        private static void HydrateEventData(SessionEvent sessionEvent, StorageProtector protector)
        {
            if (sessionEvent == null || string.IsNullOrWhiteSpace(sessionEvent.EncryptedData)) return;
            try
            {
                var stored = Convert.FromBase64String(sessionEvent.EncryptedData);
                var plaintext = (protector ?? StorageProtector.None).Unprotect(
                    stored,
                    EventProtectionPurpose(sessionEvent));
                var parsed = JToken.Parse(Utf8.GetString(plaintext));
                sessionEvent.Data = parsed.Type == JTokenType.Null ? null : parsed;
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

        private static bool ValidHashAlgorithm(string value)
        {
            return string.Equals(value, HistoryIntegrityModes.Sha256, StringComparison.Ordinal) ||
                string.Equals(value, HistoryIntegrityModes.HmacSha256, StringComparison.Ordinal);
        }

        private static bool ProtectionMatches(SessionEvent sessionEvent, StorageProtector protector)
        {
            protector = protector ?? StorageProtector.None;
            if (!string.Equals(sessionEvent.HashAlgorithm, protector.CurrentHashAlgorithm, StringComparison.Ordinal)) return false;
            if (protector.Encrypts != !string.IsNullOrWhiteSpace(sessionEvent.EncryptedData)) return false;
            if (protector.UsesHmac || protector.Encrypts)
            {
                return !string.IsNullOrWhiteSpace(sessionEvent.ProtectionKeyId) &&
                    string.Equals(sessionEvent.ProtectionKeyId, protector.KeyId, StringComparison.OrdinalIgnoreCase);
            }
            return string.IsNullOrWhiteSpace(sessionEvent.ProtectionKeyId);
        }

        private StorageProtector Protection()
        {
            return _protectionProvider() ?? StorageProtector.None;
        }

        private static void RewriteValidEvents(string path, IEnumerable<SessionEvent> events)
        {
            var content = string.Join("\n", (events ?? new List<SessionEvent>())
                .Select(sessionEvent => JsonConvert.SerializeObject(sessionEvent, Formatting.None)));
            if (content.Length > 0) content += "\n";
            StorageFileSystem.WriteAllTextAtomic(path, content, Utf8);
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
                ? new CompositeDisposable(AcquireDocumentDirectoryLock(firstDirectory), AcquireDocumentDirectoryLock(secondDirectory))
                : new CompositeDisposable(AcquireDocumentDirectoryLock(secondDirectory), AcquireDocumentDirectoryLock(firstDirectory));
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
            try { return Directory.GetDirectories(directory); }
            catch (IOException) { return new string[0]; }
            catch (UnauthorizedAccessException) { return new string[0]; }
        }

        private static IEnumerable<string> SafeGetSessionFiles(string directory)
        {
            try { return Directory.GetFiles(directory, "*" + EventFileSuffix); }
            catch (IOException) { return new string[0]; }
            catch (UnauthorizedAccessException) { return new string[0]; }
        }

        private IEnumerable<string> SafeFindSessionFiles(string sessionId)
        {
            if (!Directory.Exists(_paths.ChatDirectory)) return new string[0];
            var fileName = AppDataPaths.SafeFileName(sessionId ?? string.Empty) + EventFileSuffix;
            try
            {
                return Directory.GetFiles(_paths.ChatDirectory, fileName, SearchOption.AllDirectories);
            }
            catch (IOException) { return new string[0]; }
            catch (UnauthorizedAccessException) { return new string[0]; }
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
            public long LastAccess { get; set; }
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
            public long LastAccess { get; set; }
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

        private sealed class ProjectionReplayState
        {
            private readonly ProjectionReplayList _messages;
            private readonly ProjectionReplayList _artifacts;

            public ProjectionReplayState(JObject root)
            {
                _messages = new ProjectionReplayList(root == null ? null : root["Messages"] as JArray);
                _artifacts = new ProjectionReplayList(root == null ? null : root["Artifacts"] as JArray);
            }

            public void Upsert(string property, JToken value)
            {
                List(property).Upsert(value);
            }

            public void Remove(string property, string id)
            {
                List(property).Remove(id);
            }

            public void Reorder(string property, JArray ids)
            {
                List(property).Reorder(ids);
            }

            public void Materialize(JObject root)
            {
                root["Messages"] = _messages.Materialize();
                root["Artifacts"] = _artifacts.Materialize();
            }

            private ProjectionReplayList List(string property)
            {
                if (string.Equals(property, "Messages", StringComparison.Ordinal)) return _messages;
                if (string.Equals(property, "Artifacts", StringComparison.Ordinal)) return _artifacts;
                throw new JsonException("Unsupported projection list: " + property);
            }
        }

        private sealed class ProjectionReplayList
        {
            private List<ProjectionReplayItem> _ordered;
            private readonly Dictionary<string, ProjectionReplayItem> _byId;

            public ProjectionReplayList(JArray source)
            {
                _ordered = new List<ProjectionReplayItem>();
                _byId = new Dictionary<string, ProjectionReplayItem>(StringComparer.OrdinalIgnoreCase);
                foreach (var value in (source ?? new JArray()).OfType<JObject>())
                {
                    var item = new ProjectionReplayItem
                    {
                        Id = (string)value["Id"],
                        Value = value,
                        Active = true
                    };
                    _ordered.Add(item);
                    if (!string.IsNullOrWhiteSpace(item.Id) && !_byId.ContainsKey(item.Id))
                    {
                        _byId.Add(item.Id, item);
                    }
                }
            }

            public void Upsert(JToken value)
            {
                var objectValue = value as JObject;
                var id = objectValue == null ? null : (string)objectValue["Id"];
                if (objectValue == null || string.IsNullOrWhiteSpace(id))
                {
                    throw new JsonException("Upsert operation requires an object id.");
                }
                ProjectionReplayItem existing;
                if (_byId.TryGetValue(id, out existing))
                {
                    existing.Value = objectValue;
                    return;
                }
                var item = new ProjectionReplayItem
                {
                    Id = id,
                    Value = objectValue,
                    Active = true
                };
                _ordered.Add(item);
                _byId[id] = item;
            }

            public void Remove(string id)
            {
                if (string.IsNullOrWhiteSpace(id)) return;
                ProjectionReplayItem existing;
                if (!_byId.TryGetValue(id, out existing)) return;
                existing.Active = false;
                _byId.Remove(id);
                var duplicate = _ordered.FirstOrDefault(item => item.Active &&
                    string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
                if (duplicate != null) _byId[id] = duplicate;
            }

            public void Reorder(JArray ids)
            {
                var remaining = new Dictionary<string, ProjectionReplayItem>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in _ordered.Where(value => value.Active && !string.IsNullOrWhiteSpace(value.Id)))
                {
                    if (remaining.ContainsKey(item.Id))
                    {
                        throw new JsonException("Projection list contains duplicate ids.");
                    }
                    remaining.Add(item.Id, item);
                }

                var reordered = new List<ProjectionReplayItem>();
                foreach (var id in (ids ?? new JArray()).Values<string>())
                {
                    ProjectionReplayItem item;
                    if (!string.IsNullOrWhiteSpace(id) && remaining.TryGetValue(id, out item))
                    {
                        reordered.Add(item);
                        remaining.Remove(id);
                    }
                }
                foreach (var item in _ordered)
                {
                    if (!item.Active || string.IsNullOrWhiteSpace(item.Id) || !remaining.ContainsKey(item.Id)) continue;
                    reordered.Add(item);
                    remaining.Remove(item.Id);
                }

                _ordered = reordered;
                _byId.Clear();
                foreach (var item in _ordered) _byId[item.Id] = item;
            }

            public JArray Materialize()
            {
                var result = new JArray();
                foreach (var item in _ordered.Where(value => value.Active))
                {
                    result.Add(item.Value.DeepClone());
                }
                return result;
            }
        }

        private sealed class ProjectionReplayItem
        {
            public string Id { get; set; }
            public JObject Value { get; set; }
            public bool Active { get; set; }
        }

        private sealed class CompositeDisposable : IDisposable
        {
            private readonly IDisposable _first;
            private readonly IDisposable _second;

            public CompositeDisposable(IDisposable first, IDisposable second)
            {
                _first = first;
                _second = second;
            }

            public void Dispose()
            {
                if (_second != null) _second.Dispose();
                if (_first != null) _first.Dispose();
            }
        }

        private sealed class ChatProjectionContractResolver : DefaultContractResolver
        {
            protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
            {
                var property = base.CreateProperty(member, memberSerialization);
                if (member.DeclaringType == typeof(ChatArtifact) &&
                    string.Equals(member.Name, "InlineText", StringComparison.Ordinal))
                {
                    property.ShouldSerialize = value => string.IsNullOrWhiteSpace((value as ChatArtifact)?.ContentSha256);
                }
                if (member.DeclaringType == typeof(ChatSession) &&
                    string.Equals(member.Name, "HtmlWorkspace", StringComparison.Ordinal))
                {
                    property.ShouldSerialize = value => false;
                }
                if (member.DeclaringType == typeof(ChatSession) &&
                    string.Equals(member.Name, "ContextCheckpoints", StringComparison.Ordinal))
                {
                    property.ShouldSerialize = value => false;
                }
                if (member.DeclaringType == typeof(ChatMessage) &&
                    string.Equals(member.Name, "Content", StringComparison.Ordinal))
                {
                    property.ShouldSerialize = value => !IsCompactionMessage(value as ChatMessage);
                }
                if (member.DeclaringType == typeof(ChatActivity) &&
                    string.Equals(member.Name, "ResultMessage", StringComparison.Ordinal))
                {
                    property.ShouldSerialize = value => !IsCompactionActivity(value as ChatActivity);
                }
                if (member.DeclaringType == typeof(ChatActivity) &&
                    string.Equals(member.Name, "DataJson", StringComparison.Ordinal))
                {
                    property.ShouldSerialize = value =>
                    {
                        if (IsCompactionActivity(value as ChatActivity)) return false;
                        JObject ignored;
                        return !TryParseChart((value as ChatActivity)?.DataJson, out ignored);
                    };
                }
                return property;
            }

            private static bool IsCompactionMessage(ChatMessage message)
            {
                return message != null && IsCompactionActivity(message.Activity) &&
                    message.ArtifactIds != null && message.ArtifactIds.Count > 0;
            }

            private static bool IsCompactionActivity(ChatActivity activity)
            {
                return activity != null &&
                    string.Equals(activity.Kind, "compaction", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(activity.Status, "completed", StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
