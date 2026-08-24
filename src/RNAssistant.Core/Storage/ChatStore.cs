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
            var header = ListHeaders().FirstOrDefault(item =>
                string.Equals(item.Id, sessionId, StringComparison.OrdinalIgnoreCase));
            return header == null ? null : Load(header.Host, header.DocumentKey, header.Id);
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
            if (session == null) throw new ArgumentNullException("session");
            if (string.IsNullOrWhiteSpace(type)) throw new ArgumentException("Event type is required.", "type");

            var dataToken = data == null ? null : JToken.FromObject(data);
            var correlatedStepId = ResolveStepId(stepId, dataToken);
            ChatBlobReference payload = null;
            if (payloadText != null)
            {
                payload = _blobs.StoreText(payloadText, payloadContentType);
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
                    var revision = session.Revision;
                    if (string.Equals(type, SessionEventTypes.LlmRequest, StringComparison.Ordinal))
                    {
                        var started = AppendEvent(path, session.Id, revision, SessionEventTypes.StepStarted,
                            BuildStepLifecycleData(dataToken, "running", false, null), null,
                            runId, turnId, correlatedStepId);
                        revision = started.Sequence;
                        session.Revision = revision;
                    }

                    var appended = AppendEvent(path, session.Id, revision, type,
                        dataToken, payload, runId, turnId, correlatedStepId);
                    revision = appended.Sequence;
                    session.Revision = revision;

                    if (string.Equals(type, SessionEventTypes.LlmResponse, StringComparison.Ordinal) ||
                        string.Equals(type, SessionEventTypes.LlmFailure, StringComparison.Ordinal))
                    {
                        var status = StepTerminalStatus(type, dataToken);
                        var ended = AppendEvent(path, session.Id, revision, SessionEventTypes.StepEnded,
                            BuildStepLifecycleData(dataToken, status, false, appended.EventId), null,
                            runId, turnId, correlatedStepId);
                        session.Revision = ended.Sequence;
                    }
                    return appended;
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
                    foreach (var stepId in open)
                    {
                        var ended = AppendEvent(path, session.Id, session.Revision, SessionEventTypes.StepEnded,
                            new JObject
                            {
                                ["Status"] = string.IsNullOrWhiteSpace(status) ? "interrupted" : status,
                                ["Synthetic"] = true,
                                ["Error"] = string.IsNullOrWhiteSpace(error) ? JValue.CreateNull() : new JValue(error)
                            }, null, runId, TurnIdForRun(log == null ? null : log.Events, runId), stepId);
                        session.Revision = ended.Sequence;
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
            return List().Select(ChatSessionHeaderFactory.Create).Where(header => header != null).ToList();
        }

        public IReadOnlyList<ChatSessionHeader> ListHeaders(string host, string documentKey, string documentTitle)
        {
            return List(host, documentKey, documentTitle)
                .Select(ChatSessionHeaderFactory.Create)
                .Where(header => header != null)
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

        private long ReadRevision(string path)
        {
            var log = ReadEventLog(path);
            return log == null || log.Events.Count == 0 ? 0 : log.Events[log.Events.Count - 1].Sequence;
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
            var stored = exists ? Project(ReadEventLog(path), false) : null;
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
                SessionEvent appended;
                if (!exists)
                {
                    var initialType = string.IsNullOrWhiteSpace(session.ParentSessionId)
                        ? SessionEventTypes.SessionCreated
                        : SessionEventTypes.SessionForked;
                    appended = AppendEvent(path, session.Id, 0, initialType,
                        ToProjectionToken(session), null, CurrentRunId(session), CurrentTurnId(session), null);
                }
                else
                {
                    var operations = BuildOperations(stored, session);
                    var correlationRunId = CurrentRunId(session) ?? CurrentRunId(stored);
                    var correlationTurnId = CurrentTurnId(session) ?? CurrentTurnId(stored);
                    appended = AppendEvent(path, session.Id, storedRevision, SessionEventTypes.SessionCommit,
                        new JObject { ["Operations"] = JArray.FromObject(operations) }, null,
                        correlationRunId, correlationTurnId, null);
                }
                durableRevision = appended.Sequence;
                session.Revision = durableRevision;
                appended = AppendTurnLifecycleEvents(path, session.Id, appended,
                    stored == null ? null : stored.LastRun, session.LastRun);
                durableRevision = appended.Sequence;
                session.Revision = durableRevision;
                RebuildHtmlWorkspaceProjection(session);
                RebuildContextCheckpointProjection(session);
                RebuildChartActivityProjection(session);
            }
            catch
            {
                try
                {
                    if (File.Exists(path)) durableRevision = ReadRevision(path);
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

        private SessionEvent AppendEvent(
            string path,
            string sessionId,
            long expectedRevision,
            string type,
            JToken data,
            ChatBlobReference payload,
            string runId,
            string turnId,
            string stepId)
        {
            var log = ReadEventLog(path);
            var actualRevision = log == null || log.Events.Count == 0 ? 0 : log.Events[log.Events.Count - 1].Sequence;
            if (actualRevision != expectedRevision)
            {
                throw new ChatConcurrencyException("Chat was changed by another RNAssistant instance. Reload the chat before saving again.");
            }
            if (log != null && log.HasIncompleteTail)
            {
                RewriteValidEvents(path, log.Events);
            }

            var previous = log == null || log.Events.Count == 0 ? null : log.Events[log.Events.Count - 1];
            var sessionEvent = new SessionEvent
            {
                SessionId = sessionId,
                Sequence = actualRevision + 1,
                Type = type,
                RunId = runId,
                TurnId = turnId,
                StepId = stepId,
                PreviousHash = previous == null ? null : previous.Hash,
                Data = data == null ? null : data.DeepClone(),
                Payload = payload
            };
            var protector = Protection();
            sessionEvent.HashAlgorithm = protector.CurrentHashAlgorithm;
            sessionEvent.ProtectionKeyId = protector.UsesHmac || protector.Encrypts ? protector.KeyId : null;
            ProtectEventData(sessionEvent, protector);
            sessionEvent.Hash = ComputeHash(sessionEvent, protector);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream, Utf8))
            {
                writer.WriteLine(JsonConvert.SerializeObject(sessionEvent, Formatting.None));
                writer.Flush();
                stream.Flush(true);
            }
            return sessionEvent;
        }

        private ChatSession LoadSession(string path, bool hydrateActiveArtifacts)
        {
            try
            {
                var session = Project(ReadEventLog(path), hydrateActiveArtifacts);
                if (session == null) return null;
                NormalizeSession(session, session.Host, session.DocumentKey, session.DocumentTitle);
                return session;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (JsonException) { return null; }
            catch (ChatConcurrencyException) { return null; }
        }

        private ChatSession Project(EventLogReadResult log, bool hydrateActiveArtifacts)
        {
            if (log == null || log.Events.Count == 0) return null;
            JObject root = null;
            foreach (var sessionEvent in log.Events)
            {
                if (string.Equals(sessionEvent.Type, SessionEventTypes.SessionCreated, StringComparison.Ordinal) ||
                    string.Equals(sessionEvent.Type, SessionEventTypes.SessionForked, StringComparison.Ordinal))
                {
                    if (root != null || sessionEvent.Data == null || sessionEvent.Data.Type != JTokenType.Object) return null;
                    root = (JObject)sessionEvent.Data.DeepClone();
                    continue;
                }
                if (!string.Equals(sessionEvent.Type, SessionEventTypes.SessionCommit, StringComparison.Ordinal)) continue;
                if (root == null || sessionEvent.Data == null) return null;
                var operations = sessionEvent.Data["Operations"] == null
                    ? new List<SessionOperation>()
                    : sessionEvent.Data["Operations"].ToObject<List<SessionOperation>>();
                ApplyOperations(root, operations);
            }
            if (root == null) return null;
            var session = root.ToObject<ChatSession>();
            session.Revision = log.Events[log.Events.Count - 1].Sequence;
            RebuildHtmlWorkspaceProjection(session);
            RebuildContextCheckpointProjection(session);
            RebuildChartActivityProjection(session);
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
            if (!beforeOrder.SequenceEqual(afterOrder, StringComparer.OrdinalIgnoreCase))
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

        private static void ApplyOperations(JObject root, IEnumerable<SessionOperation> operations)
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
                        Upsert(root, "Messages", data["Value"]);
                        break;
                    case SessionOperationTypes.MessageRemove:
                        Remove(root, "Messages", (string)data["Id"]);
                        break;
                    case SessionOperationTypes.MessagesReorder:
                        Reorder(root, "Messages", data["Ids"] as JArray);
                        break;
                    case SessionOperationTypes.ArtifactUpsert:
                    case SessionOperationTypes.ArtifactRevisionCreated:
                        Upsert(root, "Artifacts", data["Value"]);
                        break;
                    case SessionOperationTypes.ArtifactRemove:
                        Remove(root, "Artifacts", (string)data["Id"]);
                        break;
                    case SessionOperationTypes.ArtifactsReorder:
                        Reorder(root, "Artifacts", data["Ids"] as JArray);
                        break;
                    default:
                        throw new JsonException("Unsupported session operation: " + operation.Type);
                }
            }
        }

        private static void Upsert(JObject root, string property, JToken value)
        {
            var item = value as JObject;
            var id = item == null ? null : (string)item["Id"];
            if (item == null || string.IsNullOrWhiteSpace(id)) throw new JsonException("Upsert operation requires an object id.");
            var items = EnsureArray(root, property);
            var existing = items.OfType<JObject>().FirstOrDefault(candidate =>
                string.Equals((string)candidate["Id"], id, StringComparison.OrdinalIgnoreCase));
            if (existing == null) items.Add(item.DeepClone());
            else existing.Replace(item.DeepClone());
        }

        private static void Remove(JObject root, string property, string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            var items = EnsureArray(root, property);
            var existing = items.OfType<JObject>().FirstOrDefault(candidate =>
                string.Equals((string)candidate["Id"], id, StringComparison.OrdinalIgnoreCase));
            if (existing != null) existing.Remove();
        }

        private static void Reorder(JObject root, string property, JArray ids)
        {
            var items = EnsureArray(root, property);
            var byId = items.OfType<JObject>()
                .Where(item => !string.IsNullOrWhiteSpace((string)item["Id"]))
                .ToDictionary(item => (string)item["Id"], item => item, StringComparer.OrdinalIgnoreCase);
            var reordered = new JArray();
            foreach (var id in (ids ?? new JArray()).Values<string>())
            {
                JObject item;
                if (!string.IsNullOrWhiteSpace(id) && byId.TryGetValue(id, out item))
                {
                    reordered.Add(item.DeepClone());
                    byId.Remove(id);
                }
            }
            foreach (var item in items.OfType<JObject>())
            {
                var id = (string)item["Id"];
                if (!string.IsNullOrWhiteSpace(id) && byId.ContainsKey(id))
                {
                    reordered.Add(item.DeepClone());
                    byId.Remove(id);
                }
            }
            root[property] = reordered;
        }

        private static JArray EnsureArray(JObject root, string property)
        {
            var items = root[property] as JArray;
            if (items != null) return items;
            items = new JArray();
            root[property] = items;
            return items;
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
                var reference = _blobs.StoreText(artifact.InlineText,
                    string.IsNullOrWhiteSpace(artifact.MimeType) ? "text/plain; charset=utf-8" : artifact.MimeType);
                artifact.ContentSha256 = reference.Sha256;
                artifact.ContentByteLength = reference.ByteLength;
            }
        }

        private void EnsureWorkspaceArtifact(ChatSession session)
        {
            if (session == null) return;
            var workspace = session.HtmlWorkspace ?? new HtmlWorkspace();
            var hasContent = (workspace.Files != null && workspace.Files.Any(item => item != null)) ||
                (workspace.DataSources != null && workspace.DataSources.Any(item => item != null));
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
            var active = FindArtifact(session, session.ActiveHtmlArtifactId);
            if (active == null || !HydrateArtifact(active))
            {
                session.HtmlWorkspace = new HtmlWorkspace();
                return;
            }
            var activeSnapshot = ParseWorkspaceSnapshot(active);
            if (activeSnapshot == null)
            {
                session.HtmlWorkspace = new HtmlWorkspace();
                return;
            }

            var workspace = HtmlWorkspaceCopyService.CreateWorkspaceFromSnapshot(activeSnapshot);
            workspace.UpdatedUtc = active.CreatedUtc;
            var current = active;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { active.Id };
            while (!string.IsNullOrWhiteSpace(current.ParentArtifactId) && visited.Add(current.ParentArtifactId))
            {
                current = FindArtifact(session, current.ParentArtifactId);
                if (current == null || !HydrateArtifact(current)) break;
                var snapshot = ParseWorkspaceSnapshot(current);
                if (snapshot == null) break;
                workspace.History.Add(snapshot);
            }
            workspace.History = HtmlWorkspaceHistoryPolicy.Trim(workspace.History);

            current = active;
            while (current != null)
            {
                var child = (session.Artifacts ?? new List<ChatArtifact>())
                    .Where(item => item != null &&
                        string.Equals(item.Kind, ChatArtifactKinds.HtmlWorkspace, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(item.ParentArtifactId, current.Id, StringComparison.OrdinalIgnoreCase) &&
                        !visited.Contains(item.Id))
                    .OrderByDescending(item => item.CreatedUtc)
                    .FirstOrDefault();
                if (child == null || !visited.Add(child.Id) || !HydrateArtifact(child)) break;
                var snapshot = ParseWorkspaceSnapshot(child);
                if (snapshot == null) break;
                workspace.RedoHistory.Add(snapshot);
                current = child;
            }
            workspace.RedoHistory = HtmlWorkspaceHistoryPolicy.Trim(workspace.RedoHistory);
            session.HtmlWorkspace = workspace;
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
            artifact.InlineText = _blobs.ReadText(new ChatBlobReference
            {
                Sha256 = artifact.ContentSha256,
                ByteLength = artifact.ContentByteLength.Value,
                ContentType = artifact.MimeType
            });
            return artifact.InlineText != null;
        }

        private static ChatArtifact FindArtifact(ChatSession session, string artifactId)
        {
            if (session == null || string.IsNullOrWhiteSpace(artifactId)) return null;
            return (session.Artifacts ?? new List<ChatArtifact>()).FirstOrDefault(item =>
                item != null && string.Equals(item.Id, artifactId, StringComparison.OrdinalIgnoreCase));
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
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var lines = File.ReadAllLines(path, Utf8);
            var result = new EventLogReadResult();
            var protector = Protection();
            for (var index = 0; index < lines.Length; index++)
            {
                if (string.IsNullOrWhiteSpace(lines[index])) continue;
                SessionEvent sessionEvent;
                try
                {
                    sessionEvent = JsonConvert.DeserializeObject<SessionEvent>(lines[index]);
                }
                catch (JsonException)
                {
                    if (index == lines.Length - 1)
                    {
                        result.HasIncompleteTail = true;
                        break;
                    }
                    throw new ChatConcurrencyException("The chat event log contains an invalid record.");
                }
                ValidateEvent(result.Events, sessionEvent, protector);
                HydrateEventData(sessionEvent, protector);
                result.Events.Add(sessionEvent);
            }
            return result;
        }

        private static void ValidateEvent(
            IReadOnlyList<SessionEvent> previousEvents,
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
            var previous = previousEvents.Count == 0 ? null : previousEvents[previousEvents.Count - 1];
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

        private SessionEvent AppendTurnLifecycleEvents(
            string path,
            string sessionId,
            SessionEvent tail,
            ChatRunRecord before,
            ChatRunRecord after)
        {
            var beforeTurnId = RunTurnId(before);
            var afterTurnId = RunTurnId(after);
            if (string.IsNullOrWhiteSpace(beforeTurnId) && string.IsNullOrWhiteSpace(afterTurnId)) return tail;

            if (string.IsNullOrWhiteSpace(beforeTurnId))
            {
                tail = AppendTurnStarted(path, sessionId, tail, after);
                if (IsTerminalRunStatus(after == null ? null : after.Status))
                {
                    tail = AppendTurnEnded(path, sessionId, tail, after, after.Status);
                }
                return tail;
            }

            if (string.IsNullOrWhiteSpace(afterTurnId))
            {
                if (IsTerminalRunStatus(before == null ? null : before.Status)) return tail;
                var status = string.Equals(before == null ? null : before.Status, "waiting_confirmation", StringComparison.OrdinalIgnoreCase)
                    ? "cancelled"
                    : "completed";
                return AppendTurnEnded(path, sessionId, tail, before, status);
            }

            if (!string.Equals(beforeTurnId, afterTurnId, StringComparison.OrdinalIgnoreCase))
            {
                if (!IsTerminalRunStatus(before == null ? null : before.Status))
                {
                    tail = AppendTurnEnded(path, sessionId, tail, before, "superseded");
                }
                tail = AppendTurnStarted(path, sessionId, tail, after);
                if (IsTerminalRunStatus(after == null ? null : after.Status))
                {
                    tail = AppendTurnEnded(path, sessionId, tail, after, after.Status);
                }
                return tail;
            }

            if (!IsTerminalRunStatus(before == null ? null : before.Status) &&
                IsTerminalRunStatus(after == null ? null : after.Status))
            {
                tail = AppendTurnEnded(path, sessionId, tail, after, after.Status);
            }
            return tail;
        }

        private SessionEvent AppendTurnStarted(string path, string sessionId, SessionEvent tail, ChatRunRecord run)
        {
            var turnId = RunTurnId(run);
            return AppendEvent(path, sessionId, tail.Sequence, SessionEventTypes.TurnStarted,
                BuildTurnLifecycleData(run, "running"), null,
                run == null ? null : run.RunId, turnId, null);
        }

        private SessionEvent AppendTurnEnded(
            string path,
            string sessionId,
            SessionEvent tail,
            ChatRunRecord run,
            string status)
        {
            var turnId = RunTurnId(run);
            return AppendEvent(path, sessionId, tail.Sequence, SessionEventTypes.TurnEnded,
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

            public EventLogReadResult()
            {
                Events = new List<SessionEvent>();
            }
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
