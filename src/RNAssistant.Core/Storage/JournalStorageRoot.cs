using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Core.Storage
{
    // Keeps canonical journals in one explicit generation root. The create-only
    // marker records the format while all derived state remains outside it.
    internal static class JournalStorageRoot
    {
        private const string Format = "rnassistant-journal-storage";
        private const int Version = 1;
        private static readonly object GenerationSync = new object();

        internal static void Ensure(AppDataPaths paths)
        {
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            lock (GenerationSync)
            {
                StorageFileSystem.EnsureRegularDirectory(paths.JournalStorageDirectory);
                StorageFileSystem.EnsureRegularDirectory(paths.JournalConversationDirectory);
                StorageFileSystem.EnsureRegularDirectory(paths.JournalResourceDirectory);
                var path = paths.JournalGenerationFile;
                if (!File.Exists(path))
                {
                    var bytes = new UTF8Encoding(false).GetBytes(
                        "{\"Format\":\"" + Format + "\",\"Version\":" + Version + "}");
                    PublishMarker(path, bytes);
                }
                if (StorageFileSystem.IsReparsePoint(path))
                    throw new InvalidDataException("Journal generation marker cannot be a link.");
                var info = new FileInfo(path);
                if (!info.Exists || info.Length < 2 || info.Length > 1024)
                    throw new InvalidDataException("Journal generation marker is missing or invalid.");
                JObject root;
                using (var text = File.OpenText(path))
                using (var reader = new JsonTextReader(text) { MaxDepth = 4, DateParseHandling = DateParseHandling.None })
                {
                    root = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                    if (reader.Read()) throw new InvalidDataException("Trailing journal generation data.");
                }
                if (root.Properties().Count() != 2 || (string)root["Format"] != Format ||
                    root["Version"]?.Type != JTokenType.Integer || (int)root["Version"] != Version)
                    throw new InvalidDataException("Unsupported journal storage generation.");
            }
        }

        internal static string ConversationDirectory(AppDataPaths paths, string scope)
        {
            Ensure(paths);
            return Path.Combine(paths.JournalConversationDirectory,
                RNAssistant.Core.Tools.TextPatternEngine.Sha256(scope));
        }

        internal static string ResourceDirectory(AppDataPaths paths)
        {
            Ensure(paths);
            return Path.Combine(paths.JournalResourceDirectory, "resource-stream");
        }

        private static void PublishMarker(string path, byte[] bytes)
        {
            // The marker bytes are constant and self-validating. A durable temporary
            // followed by create-only move makes a racing identical publication
            // harmless without retaining a publication-lock file in the root.
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                StorageFileSystem.WriteBytesDurable(temporary, bytes);
                try { File.Move(temporary, path); }
                catch (IOException) when (File.Exists(path)) { }
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
            }
        }
    }
}
