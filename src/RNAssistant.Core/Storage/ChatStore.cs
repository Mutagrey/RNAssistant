using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Storage
{
    public sealed class ChatStore
    {
        private static readonly object PersistenceSync = new object();
        private readonly AppDataPaths _paths;
        private readonly JsonFileStore _json;
        private readonly ChatIndexStore _index;

        public ChatStore(AppDataPaths paths)
        {
            _paths = paths;
            _json = new JsonFileStore();
            _index = new ChatIndexStore();
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
            lock (PersistenceSync)
            {
                NormalizeSession(session, session == null ? null : session.Host, session == null ? null : session.DocumentKey, session == null ? null : session.DocumentTitle);
                session.UpdatedUtc = DateTime.UtcNow;
                var path = GetSessionPath(session.Host, session.DocumentKey, session.Id);
                _json.Save(path, session);
                _index.Save(path, session);
            }
        }

        public ChatSession Move(ChatSession session, string host, string documentKey, string documentTitle)
        {
            if (session == null)
            {
                return null;
            }

            var oldPath = GetSessionPath(session.Host, session.DocumentKey, session.Id);
            session.Host = host;
            session.DocumentKey = documentKey;
            session.DocumentTitle = documentTitle;
            NormalizeSession(session, host, documentKey, documentTitle);
            Save(session);

            var newPath = GetSessionPath(session.Host, session.DocumentKey, session.Id);
            if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
            {
                lock (PersistenceSync)
                {
                    if (File.Exists(oldPath)) File.Delete(oldPath);
                    _index.Delete(oldPath);
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
                File.Delete(path);
                _index.Delete(path);
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
                Directory.Delete(directory, true);
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
                    .Select(path => _index.LoadOrCreate(path, LoadIndexedSession))
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
                .Select(path => _index.LoadOrCreate(path, value => LoadIndexedSession(value, host, documentKey, documentTitle)))
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
                return (File.ReadAllText(path) ?? string.Empty).Trim();
            }
            catch (IOException)
            {
                return string.Empty;
            }
        }

        public void SaveActiveSessionId(string host, string documentKey, string sessionId)
        {
            var path = GetActivePath(host, documentKey);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, sessionId ?? string.Empty);
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

        private static ChatSession LoadIndexedSession(string path)
        {
            var session = LoadSession(path);
            if (!IsSupported(session)) return null;
            NormalizeSession(session, session.Host, session.DocumentKey, session.DocumentTitle);
            return session;
        }

        private static ChatSession LoadIndexedSession(string path, string host, string documentKey, string documentTitle)
        {
            var session = LoadSession(path);
            if (!IsSupported(session)) return null;
            NormalizeSession(session, host, documentKey, documentTitle);
            return session;
        }

        private static ChatSession LoadSession(string path)
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

                return root.ToObject<ChatSession>();
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
    }
}
