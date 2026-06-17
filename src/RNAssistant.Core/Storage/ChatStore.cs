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
            var path = GetPath(host, documentKey);
            var session = _json.Load(path, (ChatSession)null);
            if (session != null)
            {
                return session;
            }

            return new ChatSession { Host = host, DocumentKey = documentKey, Title = title };
        }

        public void Save(ChatSession session)
        {
            session.UpdatedUtc = DateTime.UtcNow;
            _json.Save(GetPath(session.Host, session.DocumentKey), session);
        }

        public IReadOnlyList<ChatSession> List()
        {
            if (!Directory.Exists(_paths.ChatDirectory))
            {
                return new List<ChatSession>();
            }

            return Directory.GetFiles(_paths.ChatDirectory, "*.json")
                .Select(p => _json.Load(p, (ChatSession)null))
                .Where(s => s != null)
                .OrderByDescending(s => s.UpdatedUtc)
                .ToList();
        }

        private string GetPath(string host, string documentKey)
        {
            return Path.Combine(_paths.ChatDirectory, AppDataPaths.SafeFileName((host ?? string.Empty) + "|" + (documentKey ?? string.Empty)) + ".json");
        }
    }
}

