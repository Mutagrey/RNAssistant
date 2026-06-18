using System;
using System.IO;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    public sealed class ContextStore
    {
        private readonly AppDataPaths _paths;
        private readonly JsonFileStore _json;

        public ContextStore(AppDataPaths paths)
        {
            _paths = paths;
            _json = new JsonFileStore();
        }

        public DocumentContext LoadOrCreate(string host, string documentKey, string title)
        {
            var context = _json.Load(GetPath(host, documentKey), (DocumentContext)null);
            return context ?? new DocumentContext { Host = host, DocumentKey = documentKey, Title = title };
        }

        public void Save(DocumentContext context)
        {
            context.UpdatedUtc = DateTime.UtcNow;
            _json.Save(GetPath(context.Host, context.DocumentKey), context);
        }

        public void Clear(string host, string documentKey)
        {
            var path = GetPath(host, documentKey);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        public bool Exists(string host, string documentKey)
        {
            return File.Exists(GetPath(host, documentKey));
        }

        public DocumentContext Move(string oldHost, string oldDocumentKey, string newHost, string newDocumentKey, string title)
        {
            var oldPath = GetPath(oldHost, oldDocumentKey);
            var context = _json.Load(oldPath, (DocumentContext)null);
            if (context == null)
            {
                return null;
            }

            context.Host = newHost;
            context.DocumentKey = newDocumentKey;
            context.Title = title;
            Save(context);

            var newPath = GetPath(newHost, newDocumentKey);
            if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase) && File.Exists(oldPath))
            {
                File.Delete(oldPath);
            }

            return context;
        }

        private string GetPath(string host, string documentKey)
        {
            return Path.Combine(_paths.ContextDirectory, AppDataPaths.SafeFileName((host ?? string.Empty) + "|" + (documentKey ?? string.Empty)) + ".json");
        }
    }
}
