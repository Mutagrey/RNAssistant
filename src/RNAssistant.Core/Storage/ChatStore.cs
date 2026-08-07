using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
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

        public ChatSession LoadOrCreateActive(string host, string documentKey, string documentTitle)
        {
            var activeId = LoadActiveSessionId(host, documentKey);
            var session = string.IsNullOrWhiteSpace(activeId) ? null : Load(host, documentKey, activeId);
            if (session == null)
            {
                session = List(host, documentKey, documentTitle).FirstOrDefault();
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

            return List().FirstOrDefault(s =>
                string.Equals(s.Id, sessionId, StringComparison.OrdinalIgnoreCase));
        }

        public void Save(ChatSession session)
        {
            NormalizeSession(session, session == null ? null : session.Host, session == null ? null : session.DocumentKey, session == null ? null : session.DocumentTitle);
            session.UpdatedUtc = DateTime.UtcNow;
            _json.Save(GetSessionPath(session.Host, session.DocumentKey, session.Id), session);
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
            if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase) && File.Exists(oldPath))
            {
                File.Delete(oldPath);
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

            File.Delete(path);
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

            Directory.Delete(directory, true);
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
                sessions.AddRange(SafeGetFiles(directory)
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

            return SafeGetFiles(directory)
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
            if (session == null)
            {
                return;
            }

            session.FormatVersion = ChatSession.CurrentFormatVersion;
            if (string.IsNullOrWhiteSpace(session.Id))
            {
                session.Id = Guid.NewGuid().ToString("N");
            }
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
            session.Messages = session.Messages.Where(message => message != null).ToList();
            foreach (var message in session.Messages.Where(m => m != null))
            {
                if (string.IsNullOrWhiteSpace(message.Id))
                {
                    message.Id = Guid.NewGuid().ToString("N");
                }
                if (message.CreatedUtc == default(DateTime))
                {
                    message.CreatedUtc = session.CreatedUtc;
                }
                if (message.ArtifactIds == null)
                {
                    message.ArtifactIds = new List<string>();
                }
                message.ArtifactIds = message.ArtifactIds
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (message.ToolCalls == null)
                {
                    message.ToolCalls = new List<LlmToolCall>();
                }
                if (message.Attachments == null)
                {
                    message.Attachments = new List<ChatAttachment>();
                }
                message.Attachments = message.Attachments.Where(attachment => attachment != null).ToList();
                foreach (var attachment in message.Attachments.Where(a => a != null))
                {
                    if (attachment.PageTextLengths == null)
                    {
                        attachment.PageTextLengths = new List<int>();
                    }
                }
            }
            if (session.Context == null)
            {
                session.Context = new DocumentContext();
            }
            if (session.HtmlWorkspace == null)
            {
                session.HtmlWorkspace = new HtmlWorkspace();
            }
            if (session.HtmlWorkspace.Files == null)
            {
                session.HtmlWorkspace.Files = new List<HtmlWorkspaceFile>();
            }
            if (session.HtmlWorkspace.DataSources == null)
            {
                session.HtmlWorkspace.DataSources = new List<HtmlWorkspaceDataSource>();
            }
            if (session.HtmlWorkspace.History == null)
            {
                session.HtmlWorkspace.History = new List<HtmlWorkspaceSnapshot>();
            }
            if (session.HtmlWorkspace.RedoHistory == null)
            {
                session.HtmlWorkspace.RedoHistory = new List<HtmlWorkspaceSnapshot>();
            }
            if (session.HtmlWorkspace.UpdatedUtc == default(DateTime))
            {
                session.HtmlWorkspace.UpdatedUtc = session.UpdatedUtc == default(DateTime) ? DateTime.UtcNow : session.UpdatedUtc;
            }
            if (string.IsNullOrWhiteSpace(session.Context.Host))
            {
                session.Context.Host = session.Host;
            }
            if (string.IsNullOrWhiteSpace(session.Context.DocumentKey))
            {
                session.Context.DocumentKey = session.DocumentKey;
            }
            if (string.IsNullOrWhiteSpace(session.Context.Title))
            {
                session.Context.Title = session.Title;
            }
            if (session.Context.Notes == null)
            {
                session.Context.Notes = new List<ContextNote>();
            }
            if (session.ContextCheckpoints == null)
            {
                session.ContextCheckpoints = new List<ContextCheckpoint>();
            }
            foreach (var checkpoint in session.ContextCheckpoints.Where(item => item != null))
            {
                if (string.IsNullOrWhiteSpace(checkpoint.Id)) checkpoint.Id = Guid.NewGuid().ToString("N");
                if (checkpoint.CreatedUtc == default(DateTime)) checkpoint.CreatedUtc = session.UpdatedUtc;
            }
            session.ContextCheckpoints = session.ContextCheckpoints
                .Where(item => item != null)
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(item => item.CreatedUtc).First())
                .ToList();
            if (session.Artifacts == null)
            {
                session.Artifacts = new List<ChatArtifact>();
            }
            foreach (var artifact in session.Artifacts.Where(item => item != null))
            {
                if (string.IsNullOrWhiteSpace(artifact.Id)) artifact.Id = Guid.NewGuid().ToString("N");
                if (artifact.Revision <= 0) artifact.Revision = 1;
                if (artifact.CreatedUtc == default(DateTime)) artifact.CreatedUtc = session.UpdatedUtc;
                if (artifact.RelatedArtifactIds == null) artifact.RelatedArtifactIds = new List<string>();
            }
            session.Artifacts = session.Artifacts
                .Where(item => item != null)
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(item => item.CreatedUtc).First())
                .ToList();
            if (session.ActiveSkillIds == null)
            {
                session.ActiveSkillIds = new List<string>();
            }
            session.ActiveSkillIds = session.ActiveSkillIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var messageIds = new HashSet<string>(session.Messages.Where(item => item != null).Select(item => item.Id), StringComparer.OrdinalIgnoreCase);
            var activeCheckpoint = session.ContextCheckpoints.FirstOrDefault(item =>
                string.Equals(item.Id, session.ActiveContextCheckpointId, StringComparison.OrdinalIgnoreCase));
            if (activeCheckpoint == null || !messageIds.Contains(activeCheckpoint.ThroughMessageId))
            {
                session.ActiveContextCheckpointId = null;
            }
            var activeHtml = session.Artifacts.FirstOrDefault(item =>
                string.Equals(item.Id, session.ActiveHtmlArtifactId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Kind, ChatArtifactKinds.HtmlWorkspace, StringComparison.OrdinalIgnoreCase));
            if (activeHtml == null)
            {
                session.ActiveHtmlArtifactId = null;
            }
        }

        private static bool IsSupported(ChatSession session)
        {
            return session != null &&
                session.FormatVersion >= 1 &&
                session.FormatVersion <= ChatSession.CurrentFormatVersion;
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

        private static IEnumerable<string> SafeGetFiles(string directory)
        {
            try
            {
                return Directory.GetFiles(directory, "*.json");
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
