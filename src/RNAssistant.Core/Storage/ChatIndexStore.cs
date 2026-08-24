using System;
using System.IO;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Storage
{
    internal sealed class ChatIndexStore
    {
        internal const string SidecarSuffix = ".summary.json";
        private const int CurrentIndexFormatVersion = 2;
        private readonly JsonFileStore _json = new JsonFileStore();

        public ChatSessionHeader LoadOrCreate(string sessionPath, Func<string, ChatSession> loadSession)
        {
            if (string.IsNullOrWhiteSpace(sessionPath) || !File.Exists(sessionPath) || loadSession == null)
            {
                return null;
            }

            var sessionInfo = new FileInfo(sessionPath);
            var entry = _json.Load<ChatIndexEntry>(SidecarPath(sessionPath), null);
            if (IsCurrent(entry, sessionInfo))
            {
                return entry.Header;
            }

            var session = loadSession(sessionPath);
            if (session == null)
            {
                return null;
            }

            Save(sessionPath, session);
            return ChatSessionHeaderFactory.Create(session);
        }

        public void Save(string sessionPath, ChatSession session)
        {
            if (session == null || string.IsNullOrWhiteSpace(sessionPath) || !File.Exists(sessionPath))
            {
                return;
            }

            try
            {
                var sessionInfo = new FileInfo(sessionPath);
                _json.Save(SidecarPath(sessionPath), new ChatIndexEntry
                {
                    IndexFormatVersion = CurrentIndexFormatVersion,
                    SessionFormatVersion = session.FormatVersion,
                    SessionLength = sessionInfo.Length,
                    SessionLastWriteUtcTicks = sessionInfo.LastWriteTimeUtc.Ticks,
                    Header = ChatSessionHeaderFactory.Create(session)
                });
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (JsonException)
            {
            }
        }

        public void Delete(string sessionPath)
        {
            try
            {
                var sidecarPath = SidecarPath(sessionPath);
                if (File.Exists(sidecarPath)) File.Delete(sidecarPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        internal static bool IsSidecarPath(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && path.EndsWith(SidecarSuffix, StringComparison.OrdinalIgnoreCase);
        }

        internal static string SidecarPath(string sessionPath)
        {
            var directory = Path.GetDirectoryName(sessionPath) ?? string.Empty;
            var name = Path.GetFileNameWithoutExtension(sessionPath) ?? string.Empty;
            return Path.Combine(directory, name + SidecarSuffix);
        }

        private static bool IsCurrent(ChatIndexEntry entry, FileInfo sessionInfo)
        {
            if (entry == null || entry.Header == null || string.IsNullOrWhiteSpace(entry.Header.Id) ||
                entry.IndexFormatVersion != CurrentIndexFormatVersion ||
                entry.SessionFormatVersion < 1 || entry.SessionFormatVersion > ChatSession.CurrentFormatVersion ||
                entry.SessionLength != sessionInfo.Length ||
                entry.SessionLastWriteUtcTicks != sessionInfo.LastWriteTimeUtc.Ticks)
            {
                return false;
            }

            var expectedName = AppDataPaths.SafeFileName(entry.Header.Id) + ".json";
            return string.Equals(sessionInfo.Name, expectedName, StringComparison.OrdinalIgnoreCase) &&
                entry.Header.Revision == ChatStore.ReadRevision(sessionInfo.FullName);
        }

        private sealed class ChatIndexEntry
        {
            public int IndexFormatVersion { get; set; }
            public int SessionFormatVersion { get; set; }
            public long SessionLength { get; set; }
            public long SessionLastWriteUtcTicks { get; set; }
            public ChatSessionHeader Header { get; set; }
        }
    }
}
