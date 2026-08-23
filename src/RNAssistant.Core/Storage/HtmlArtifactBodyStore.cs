using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    internal sealed class HtmlArtifactBodyStore
    {
        private const string FileExtension = ".json";
        private readonly string _rootDirectory;

        public HtmlArtifactBodyStore(AppDataPaths paths)
        {
            if (paths == null)
            {
                throw new ArgumentNullException("paths");
            }

            _rootDirectory = paths.HtmlArtifactBodyDirectory;
        }

        public void SaveMissing(ChatSession session)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.Id))
            {
                return;
            }

            var bodies = HtmlArtifacts(session)
                .Where(artifact => !string.IsNullOrWhiteSpace(artifact.InlineText))
                .ToList();
            if (bodies.Count == 0)
            {
                return;
            }

            var directory = SessionDirectory(session.Id);
            Directory.CreateDirectory(directory);
            foreach (var artifact in bodies)
            {
                var path = ArtifactPath(directory, artifact.Id);
                if (!File.Exists(path))
                {
                    StorageFileSystem.WriteAllTextAtomic(path, artifact.InlineText);
                }
            }
        }

        public bool Hydrate(ChatSession session, string artifactId)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.Id) || string.IsNullOrWhiteSpace(artifactId))
            {
                return false;
            }

            var artifact = HtmlArtifacts(session).FirstOrDefault(item =>
                string.Equals(item.Id, artifactId, StringComparison.OrdinalIgnoreCase));
            if (artifact == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(artifact.InlineText))
            {
                return true;
            }

            try
            {
                var path = ArtifactPath(SessionDirectory(session.Id), artifact.Id);
                if (File.Exists(path))
                {
                    artifact.InlineText = File.ReadAllText(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return !string.IsNullOrWhiteSpace(artifact.InlineText);
        }

        public void Prune(ChatSession session)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.Id))
            {
                return;
            }

            var directory = SessionDirectory(session.Id);
            if (!Directory.Exists(directory))
            {
                return;
            }

            var expected = new HashSet<string>(
                HtmlArtifacts(session).Select(artifact => ArtifactFileName(artifact.Id)),
                StringComparer.OrdinalIgnoreCase);
            string[] files;
            try
            {
                files = Directory.GetFiles(directory, "*" + FileExtension);
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            foreach (var path in files.Where(path => !expected.Contains(Path.GetFileName(path))))
            {
                TryDeleteFile(path);
            }

            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        public void DeleteSession(string sessionId)
        {
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                StorageFileSystem.TryDeleteDirectory(SessionDirectory(sessionId));
            }
        }

        internal static bool IsExternalized(ChatArtifact artifact)
        {
            return artifact != null &&
                string.Equals(artifact.Kind, ChatArtifactKinds.HtmlWorkspace, StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<ChatArtifact> HtmlArtifacts(ChatSession session)
        {
            return (session.Artifacts ?? new List<ChatArtifact>())
                .Where(artifact => artifact != null &&
                    !string.IsNullOrWhiteSpace(artifact.Id) &&
                    IsExternalized(artifact));
        }

        private string SessionDirectory(string sessionId)
        {
            return Path.Combine(_rootDirectory, AppDataPaths.SafeFileName(sessionId));
        }

        private static string ArtifactPath(string directory, string artifactId)
        {
            return Path.Combine(directory, ArtifactFileName(artifactId));
        }

        private static string ArtifactFileName(string artifactId)
        {
            return AppDataPaths.SafeFileName(artifactId) + FileExtension;
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
