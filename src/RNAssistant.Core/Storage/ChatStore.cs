using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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

    public sealed class ChatStore
    {
        private static readonly object PersistenceSync = new object();
        private static readonly JsonSerializerSettings PersistenceJsonSettings = new JsonSerializerSettings
        {
            ContractResolver = new ChatPersistenceContractResolver()
        };
        private readonly AppDataPaths _paths;
        private readonly JsonFileStore _json;
        private readonly ChatIndexStore _index;
        private readonly HtmlArtifactBodyStore _htmlArtifactBodies;

        public ChatStore(AppDataPaths paths)
        {
            _paths = paths;
            _json = new JsonFileStore();
            _index = new ChatIndexStore();
            _htmlArtifactBodies = new HtmlArtifactBodyStore(paths);
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

            if (IsPersisted(session))
            {
                SaveActiveSessionId(host, documentKey, session.Id);
            }
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
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return null;
            }

            var session = LoadSession(GetSessionPath(host, documentKey, sessionId));
            if (!IsSupported(session))
            {
                return null;
            }

            NormalizeSession(session, host, documentKey, session.DocumentTitle);
            return session;
        }

        public ChatSession Load(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return null;
            }

            var header = ListHeaders().FirstOrDefault(item =>
                string.Equals(item.Id, sessionId, StringComparison.OrdinalIgnoreCase));
            var session = header == null ? null : Load(header.Host, header.DocumentKey, header.Id);
            return session ?? List().FirstOrDefault(item =>
                string.Equals(item.Id, sessionId, StringComparison.OrdinalIgnoreCase));
        }

        public void Save(ChatSession session)
        {
            SaveInternal(session, false);
        }

        private void SaveInternal(ChatSession session, bool allowRelocatedSession)
        {
            if (session == null)
            {
                throw new ArgumentNullException("session");
            }

            lock (PersistenceSync)
            {
                NormalizeSession(session, session.Host, session.DocumentKey, session.DocumentTitle);
                var path = GetSessionPath(session.Host, session.DocumentKey, session.Id);
                using (AcquireDocumentLock(session.Host, session.DocumentKey))
                {
                    var exists = File.Exists(path);
                    var storedRevision = exists ? ReadRevision(path) : 0;
                    if (exists && storedRevision != session.Revision)
                    {
                        throw new ChatConcurrencyException(
                            "Chat was changed by another RNAssistant instance. Reload the chat before saving again.");
                    }
                    if (!exists && session.Revision > 0 && !allowRelocatedSession)
                    {
                        throw new ChatConcurrencyException(
                            "Chat storage changed while this session was open. Reload the chat before saving again.");
                    }

                    var previousRevision = session.Revision;
                    var previousUpdatedUtc = session.UpdatedUtc;
                    session.Revision = Math.Max(previousRevision, storedRevision) + 1;
                    session.UpdatedUtc = DateTime.UtcNow;
                    try
                    {
                        _htmlArtifactBodies.SaveMissing(session);
                        _json.Save(path, session, PersistenceJsonSettings);
                    }
                    catch
                    {
                        session.Revision = previousRevision;
                        session.UpdatedUtc = previousUpdatedUtc;
                        throw;
                    }
                    _index.Save(path, session);
                    _htmlArtifactBodies.Prune(session);
                }
            }
        }

        public bool LoadHtmlArtifactBody(ChatSession session, string artifactId)
        {
            lock (PersistenceSync)
            {
                return _htmlArtifactBodies.Hydrate(session, artifactId);
            }
        }

        public void LoadHtmlArtifactBodies(ChatSession session, IEnumerable<string> artifactIds)
        {
            if (artifactIds == null)
            {
                return;
            }

            lock (PersistenceSync)
            {
                foreach (var artifactId in artifactIds
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    _htmlArtifactBodies.Hydrate(session, artifactId);
                }
            }
        }

        public ChatSession Move(ChatSession session, string host, string documentKey, string documentTitle)
        {
            if (session == null)
            {
                return null;
            }

            var oldPath = GetSessionPath(session.Host, session.DocumentKey, session.Id);
            var sourceRevision = session.Revision;
            if (File.Exists(oldPath))
            {
                lock (PersistenceSync)
                {
                    using (AcquireDocumentPathLock(oldPath))
                    {
                        if (File.Exists(oldPath) && ReadRevision(oldPath) != sourceRevision)
                        {
                            throw new ChatConcurrencyException(
                                "Chat changed before document identity migration. Reload it before moving.");
                        }
                    }
                }
            }
            var oldHost = session.Host;
            var oldDocumentKey = session.DocumentKey;
            var oldDocumentTitle = session.DocumentTitle;
            var oldContextHost = session.Context == null ? null : session.Context.Host;
            var oldContextDocumentKey = session.Context == null ? null : session.Context.DocumentKey;
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
                SaveInternal(session, true);
            }
            catch
            {
                session.Host = oldHost;
                session.DocumentKey = oldDocumentKey;
                session.DocumentTitle = oldDocumentTitle;
                if (session.Context != null)
                {
                    session.Context.Host = oldContextHost;
                    session.Context.DocumentKey = oldContextDocumentKey;
                }
                throw;
            }

            var newPath = GetSessionPath(session.Host, session.DocumentKey, session.Id);
            if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
            {
                lock (PersistenceSync)
                {
                    using (AcquireDocumentPathLock(oldPath))
                    {
                        // The source may have been updated while the destination copy was written.
                        // Never delete a newer source revision; leaving a duplicate is recoverable.
                        if (File.Exists(oldPath) && ReadRevision(oldPath) == sourceRevision)
                        {
                            File.Delete(oldPath);
                            _index.Delete(oldPath);
                        }
                    }
                }
            }

            SaveActiveSessionId(host, documentKey, session.Id);
            return session;
        }

        public void MoveDocument(string oldHost, string oldDocumentKey, string newHost, string newDocumentKey, string documentTitle)
        {
            var activeId = LoadActiveSessionId(oldHost, oldDocumentKey);
            foreach (var session in List(oldHost, oldDocumentKey, documentTitle))
            {
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
            if (session == null)
            {
                return;
            }

            session.Messages.Clear();
            Save(session);
        }

        public bool Delete(string host, string documentKey, string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return false;
            }

            var path = GetSessionPath(host, documentKey, sessionId);
            if (!File.Exists(path))
            {
                return false;
            }

            lock (PersistenceSync)
            {
                using (AcquireDocumentLock(host, documentKey))
                {
                    if (!File.Exists(path)) return false;
                    File.Delete(path);
                    _index.Delete(path);
                    _htmlArtifactBodies.DeleteSession(sessionId);
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
            if (!Directory.Exists(directory))
            {
                return false;
            }

            lock (PersistenceSync)
            {
                using (AcquireDocumentLock(host, documentKey))
                {
                    if (!Directory.Exists(directory)) return false;
                    var sessionIds = SafeGetSessionFiles(directory)
                        .Select(ReadSessionId)
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    Directory.Delete(directory, true);
                    foreach (var sessionId in sessionIds)
                    {
                        _htmlArtifactBodies.DeleteSession(sessionId);
                    }
                }
            }
            return true;
        }

        public bool IsPersisted(ChatSession session)
        {
            return session != null &&
                File.Exists(GetSessionPath(session.Host, session.DocumentKey, session.Id));
        }

        public IReadOnlyList<ChatSession> List()
        {
            if (!Directory.Exists(_paths.ChatDirectory))
            {
                return new List<ChatSession>();
            }

            var sessions = new List<ChatSession>();
            foreach (var directory in SafeGetDirectories(_paths.ChatDirectory))
            {
                sessions.AddRange(SafeGetSessionFiles(directory)
                    .Select(LoadSession)
                    .Where(IsSupported)
                    .Select(s =>
                    {
                        NormalizeSession(s, s.Host, s.DocumentKey, s.DocumentTitle);
                        return s;
                    }));
            }

            return sessions
                .OrderByDescending(s => s.UpdatedUtc)
                .ToList();
        }

        public IReadOnlyList<ChatSession> List(string host, string documentKey, string documentTitle)
        {
            var directory = GetDocumentDirectory(host, documentKey);
            if (!Directory.Exists(directory))
            {
                return new List<ChatSession>();
            }

            return SafeGetSessionFiles(directory)
                .Select(LoadSession)
                .Where(IsSupported)
                .Select(s =>
                {
                    NormalizeSession(s, host, documentKey, documentTitle);
                    return s;
                })
                .OrderByDescending(s => s.UpdatedUtc)
                .ToList();
        }

        public IReadOnlyList<ChatSessionHeader> ListHeaders()
        {
            if (!Directory.Exists(_paths.ChatDirectory))
            {
                return new List<ChatSessionHeader>();
            }

            var headers = new List<ChatSessionHeader>();
            foreach (var directory in SafeGetDirectories(_paths.ChatDirectory))
            {
                headers.AddRange(SafeGetSessionFiles(directory)
                    .Select(path => LoadHeader(path, LoadIndexedSession))
                    .Where(header => header != null));
            }
            return headers.OrderByDescending(header => header.UpdatedUtc).ToList();
        }

        public IReadOnlyList<ChatSessionHeader> ListHeaders(string host, string documentKey, string documentTitle)
        {
            var directory = GetDocumentDirectory(host, documentKey);
            if (!Directory.Exists(directory))
            {
                return new List<ChatSessionHeader>();
            }

            return SafeGetSessionFiles(directory)
                .Select(path => LoadHeader(path, value => LoadIndexedSession(value, host, documentKey, documentTitle)))
                .Where(header => header != null)
                .OrderByDescending(header => header.UpdatedUtc)
                .ToList();
        }

        public string LoadActiveSessionId(string host, string documentKey)
        {
            var path = GetActivePath(host, documentKey);
            if (!File.Exists(path))
            {
                return string.Empty;
            }

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
            catch (IOException)
            {
                return string.Empty;
            }
            catch (UnauthorizedAccessException)
            {
                return string.Empty;
            }
            catch (ChatConcurrencyException)
            {
                return string.Empty;
            }
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
                        StorageFileSystem.WriteAllTextAtomic(path, sessionId ?? string.Empty);
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (ChatConcurrencyException)
            {
            }
        }

        private ChatSessionHeader LoadHeader(string path, Func<string, ChatSession> loadSession)
        {
            try
            {
                return _index.LoadOrCreate(path, loadSession);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            catch (ChatConcurrencyException)
            {
                return null;
            }
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

        internal static long ReadRevision(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                using (var textReader = new StreamReader(stream))
                using (var reader = new JsonTextReader(textReader))
                {
                    while (reader.Read())
                    {
                        if (reader.TokenType != JsonToken.PropertyName || reader.Depth != 1 ||
                            !string.Equals(Convert.ToString(reader.Value), "Revision", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        if (!reader.Read()) return 0;
                        long revision;
                        return long.TryParse(Convert.ToString(reader.Value), out revision) ? Math.Max(0, revision) : 0;
                    }
                }
                return 0;
            }
            catch (JsonException ex)
            {
                throw new ChatConcurrencyException("Cannot verify the stored chat revision: " + ex.Message);
            }
        }

        private static void NormalizeSession(ChatSession session, string host, string documentKey, string documentTitle)
        {
            ChatSessionNormalizer.Normalize(session, host, documentKey, documentTitle);
        }

        private static bool IsSupported(ChatSession session)
        {
            return session != null &&
                session.FormatVersion >= 1 &&
                session.FormatVersion <= ChatSession.CurrentFormatVersion;
        }

        private ChatSession LoadIndexedSession(string path)
        {
            var session = LoadSession(path, false);
            if (!IsSupported(session)) return null;
            NormalizeSession(session, session.Host, session.DocumentKey, session.DocumentTitle);
            return session;
        }

        private ChatSession LoadIndexedSession(string path, string host, string documentKey, string documentTitle)
        {
            var session = LoadSession(path, false);
            if (!IsSupported(session)) return null;
            NormalizeSession(session, host, documentKey, documentTitle);
            return session;
        }

        private ChatSession LoadSession(string path)
        {
            return LoadSession(path, true);
        }

        private ChatSession LoadSession(string path, bool hydrateActiveHtmlArtifact)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                var root = JObject.Parse(File.ReadAllText(path));
                var formatVersion = root.GetValue("FormatVersion", StringComparison.OrdinalIgnoreCase);
                int version;
                if (formatVersion == null ||
                    formatVersion.Type != JTokenType.Integer ||
                    !int.TryParse(formatVersion.ToString(), out version) ||
                    version < 1 ||
                    version > ChatSession.CurrentFormatVersion)
                {
                    return null;
                }

                var session = root.ToObject<ChatSession>();
                if (hydrateActiveHtmlArtifact)
                {
                    _htmlArtifactBodies.Hydrate(session, session == null ? null : session.ActiveHtmlArtifactId);
                }
                return session;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string ReadSessionId(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return string.Empty;
                }

                return (string)JObject.Parse(File.ReadAllText(path)).GetValue("Id", StringComparison.OrdinalIgnoreCase) ?? string.Empty;
            }
            catch (IOException)
            {
                return string.Empty;
            }
            catch (UnauthorizedAccessException)
            {
                return string.Empty;
            }
            catch (JsonException)
            {
                return string.Empty;
            }
        }

        private string GetDocumentDirectory(string host, string documentKey)
        {
            return Path.Combine(_paths.ChatDirectory, AppDataPaths.SafeFileName((host ?? string.Empty) + "|" + (documentKey ?? string.Empty)));
        }

        private string GetSessionPath(string host, string documentKey, string sessionId)
        {
            return Path.Combine(GetDocumentDirectory(host, documentKey), AppDataPaths.SafeFileName(sessionId ?? string.Empty) + ".json");
        }

        private string GetActivePath(string host, string documentKey)
        {
            return Path.Combine(GetDocumentDirectory(host, documentKey), "active.txt");
        }

        private static IEnumerable<string> SafeGetDirectories(string directory)
        {
            try
            {
                return Directory.GetDirectories(directory);
            }
            catch (IOException)
            {
                return new string[0];
            }
            catch (UnauthorizedAccessException)
            {
                return new string[0];
            }
        }

        private static IEnumerable<string> SafeGetSessionFiles(string directory)
        {
            try
            {
                return Directory.GetFiles(directory, "*.json")
                    .Where(path => !ChatIndexStore.IsSidecarPath(path))
                    .ToArray();
            }
            catch (IOException)
            {
                return new string[0];
            }
            catch (UnauthorizedAccessException)
            {
                return new string[0];
            }
        }

        private sealed class ChatPersistenceContractResolver : DefaultContractResolver
        {
            protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
            {
                var property = base.CreateProperty(member, memberSerialization);
                if (member.DeclaringType == typeof(ChatArtifact) &&
                    string.Equals(member.Name, "InlineText", StringComparison.Ordinal))
                {
                    property.ShouldSerialize = value => !HtmlArtifactBodyStore.IsExternalized(value as ChatArtifact);
                }

                return property;
            }
        }
    }
}
