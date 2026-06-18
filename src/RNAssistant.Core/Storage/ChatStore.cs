using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    public sealed class ChatStore
    {
        private readonly AppDataPaths _paths;
        private readonly JsonFileStore _json;

        public ChatStore(AppDataPaths paths)
        {
            _paths = paths;
            _json = new JsonFileStore();
        }

        public ChatSession LoadOrCreate(string host, string documentKey, string title)
        {
            return LoadOrCreateActive(host, documentKey, title);
        }

        public ChatSession LoadOrCreateActive(string host, string documentKey, string documentTitle)
        {
            EnsureMigrated(host, documentKey, documentTitle);

            var activeId = LoadActiveSessionId(host, documentKey);
            var session = string.IsNullOrWhiteSpace(activeId) ? null : Load(host, documentKey, activeId);
            if (session == null)
            {
                session = List(host, documentKey, documentTitle).FirstOrDefault();
            }
            if (session == null)
            {
                session = Create(host, documentKey, documentTitle, "New chat");
            }

            SaveActiveSessionId(host, documentKey, GetSessionId(session));
            return session;
        }

        public ChatSession Create(string host, string documentKey, string documentTitle, string title)
        {
            var session = new ChatSession
            {
                Host = host,
                DocumentKey = documentKey,
                DocumentTitle = documentTitle,
                Title = string.IsNullOrWhiteSpace(title) ? "New chat" : title
            };
            NormalizeSession(session, host, documentKey, documentTitle);
            Save(session);
            SaveActiveSessionId(host, documentKey, GetSessionId(session));
            return session;
        }

        public ChatSession Load(string host, string documentKey, string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return null;
            }

            EnsureMigrated(host, documentKey, null);
            var session = _json.Load(GetSessionPath(host, documentKey, sessionId), (ChatSession)null);
            if (session == null)
            {
                return null;
            }

            NormalizeSession(session, host, documentKey, session.DocumentTitle);
            return session;
        }

        public void Save(ChatSession session)
        {
            NormalizeSession(session, session == null ? null : session.Host, session == null ? null : session.DocumentKey, session == null ? null : session.DocumentTitle);
            session.UpdatedUtc = DateTime.UtcNow;
            _json.Save(GetSessionPath(session.Host, session.DocumentKey, GetSessionId(session)), session);
        }

        public ChatSession Move(ChatSession session, string host, string documentKey, string documentTitle)
        {
            if (session == null)
            {
                return null;
            }

            var oldPath = GetSessionPath(session.Host, session.DocumentKey, GetSessionId(session));
            session.Host = host;
            session.DocumentKey = documentKey;
            session.DocumentTitle = documentTitle;
            NormalizeSession(session, host, documentKey, documentTitle);
            Save(session);

            var newPath = GetSessionPath(session.Host, session.DocumentKey, GetSessionId(session));
            if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase) && File.Exists(oldPath))
            {
                File.Delete(oldPath);
            }

            SaveActiveSessionId(host, documentKey, GetSessionId(session));
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

            File.Delete(path);
            if (string.Equals(LoadActiveSessionId(host, documentKey), sessionId, StringComparison.OrdinalIgnoreCase))
            {
                SaveActiveSessionId(host, documentKey, string.Empty);
            }

            return true;
        }

        public IReadOnlyList<ChatSession> List()
        {
            if (!Directory.Exists(_paths.ChatDirectory))
            {
                return new List<ChatSession>();
            }

            var sessions = new List<ChatSession>();
            foreach (var directory in Directory.GetDirectories(_paths.ChatDirectory))
            {
                sessions.AddRange(Directory.GetFiles(directory, "*.json")
                    .Select(p => _json.Load(p, (ChatSession)null))
                    .Where(s => s != null)
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
            EnsureMigrated(host, documentKey, documentTitle);

            var directory = GetDocumentDirectory(host, documentKey);
            if (!Directory.Exists(directory))
            {
                return new List<ChatSession>();
            }

            return Directory.GetFiles(directory, "*.json")
                .Select(p => _json.Load(p, (ChatSession)null))
                .Where(s => s != null)
                .Select(s =>
                {
                    NormalizeSession(s, host, documentKey, documentTitle);
                    return s;
                })
                .OrderByDescending(s => s.UpdatedUtc)
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

        public static string GetSessionId(ChatSession session)
        {
            if (session == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(session.SessionId))
            {
                return session.SessionId;
            }

            return string.IsNullOrWhiteSpace(session.Id) ? string.Empty : session.Id;
        }

        private void EnsureMigrated(string host, string documentKey, string documentTitle)
        {
            var directory = GetDocumentDirectory(host, documentKey);
            if (Directory.Exists(directory) && Directory.GetFiles(directory, "*.json").Length > 0)
            {
                WriteMigrationMarker(host, documentKey);
                return;
            }

            if (File.Exists(GetMigrationMarkerPath(host, documentKey)))
            {
                return;
            }

            var legacyPath = GetLegacyPath(host, documentKey);
            var legacy = _json.Load(legacyPath, (ChatSession)null);
            if (legacy == null)
            {
                return;
            }

            NormalizeSession(legacy, host, documentKey, documentTitle);
            if (string.Equals(legacy.Title, documentTitle, StringComparison.OrdinalIgnoreCase))
            {
                legacy.Title = "Chat";
            }

            if (legacy.CreatedUtc == default(DateTime) && legacy.Messages != null && legacy.Messages.Count > 0)
            {
                legacy.CreatedUtc = legacy.Messages.Min(m => m == null ? DateTime.UtcNow : m.CreatedUtc);
            }

            Save(legacy);
            SaveActiveSessionId(host, documentKey, GetSessionId(legacy));
            WriteMigrationMarker(host, documentKey);
        }

        private static void NormalizeSession(ChatSession session, string host, string documentKey, string documentTitle)
        {
            if (session == null)
            {
                return;
            }

            var id = GetSessionId(session);
            if (string.IsNullOrWhiteSpace(id))
            {
                id = Guid.NewGuid().ToString("N");
            }

            session.Id = id;
            session.SessionId = id;
            if (string.IsNullOrWhiteSpace(session.Host))
            {
                session.Host = host ?? string.Empty;
            }
            if (string.IsNullOrWhiteSpace(session.DocumentKey))
            {
                session.DocumentKey = documentKey ?? string.Empty;
            }
            if (string.IsNullOrWhiteSpace(session.DocumentTitle))
            {
                session.DocumentTitle = documentTitle ?? session.Title ?? string.Empty;
            }
            if (string.IsNullOrWhiteSpace(session.Title))
            {
                session.Title = "New chat";
            }
            if (session.CreatedUtc == default(DateTime))
            {
                session.CreatedUtc = session.UpdatedUtc == default(DateTime) ? DateTime.UtcNow : session.UpdatedUtc;
            }
            if (session.UpdatedUtc == default(DateTime))
            {
                session.UpdatedUtc = session.CreatedUtc;
            }
            if (session.Messages == null)
            {
                session.Messages = new List<ChatMessage>();
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

        private string GetMigrationMarkerPath(string host, string documentKey)
        {
            return Path.Combine(GetDocumentDirectory(host, documentKey), "migrated.txt");
        }

        private void WriteMigrationMarker(string host, string documentKey)
        {
            var path = GetMigrationMarkerPath(host, documentKey);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (!File.Exists(path))
            {
                File.WriteAllText(path, DateTime.UtcNow.ToString("O"));
            }
        }

        private string GetLegacyPath(string host, string documentKey)
        {
            return Path.Combine(_paths.ChatDirectory, AppDataPaths.SafeFileName((host ?? string.Empty) + "|" + (documentKey ?? string.Empty)) + ".json");
        }
    }
}
