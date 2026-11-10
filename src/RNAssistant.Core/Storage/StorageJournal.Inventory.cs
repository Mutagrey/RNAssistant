using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Storage
{
    internal sealed partial class StorageJournal
    {
        // Enumerate authority independently of SQLite, including a flushed stream
        // with no cursor. Unknown entries fail closed; no recursive link traversal.
        internal static IReadOnlyList<JournalStream> Inventory(AppDataPaths paths)
        {
            var streams = new List<JournalStream>();
            var conversations = Path.Combine(paths.Root, "conversation-streams");
            CheckDirectory(conversations);
            if (Directory.Exists(conversations))
                foreach (var directory in Directory.EnumerateFileSystemEntries(conversations))
                {
                    CheckDirectory(directory);
                    if (!Directory.Exists(directory) || !LowerHash(Path.GetFileName(directory)))
                        throw new InvalidDataException("Unknown conversation stream entry.");
                    AddStream(streams, directory, false);
                }
            var resources = Path.Combine(paths.Root, "resource-stream");
            CheckDirectory(resources);
            if (Directory.Exists(resources)) AddStream(streams, resources, true);
            return streams;
        }

        private static void CheckDirectory(string path)
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
                throw new InvalidDataException("Journal inventory cannot follow links or non-directories.");
        }

        private static void AddStream(List<JournalStream> result, string directory, bool resources)
        {
            var segments = new List<string>();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (StorageFileSystem.IsReparsePoint(entry) || Directory.Exists(entry))
                    throw new InvalidDataException("Journal inventory contains a link or nested directory.");
                if (Path.GetFileName(entry) == "append.lck") continue;
                if (UnpublishedHeader(entry)) continue;
                int number;
                if (!int.TryParse(Path.GetFileNameWithoutExtension(entry), out number) || number < 1 ||
                    Path.GetFileName(entry) != number.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + ".jsonl")
                    throw new InvalidDataException("Journal inventory contains an unknown canonical file.");
                segments.Add(entry);
            }
            if (segments.Count == 0) return; // an unused owner, no canonical facts
            segments.Sort(StringComparer.Ordinal);
            for (var index = 0; index < segments.Count; index++)
                if (Path.GetFileName(segments[index]) != (index + 1).ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + ".jsonl")
                    throw new InvalidDataException("Journal inventory contains a segment gap.");
            var scope = ReadStreamIdentity(directory);
            if (resources ? scope != "resources" : string.IsNullOrWhiteSpace(scope) ||
                !(scope.StartsWith("document:", StringComparison.Ordinal) || scope.StartsWith("workspace:", StringComparison.Ordinal)) ||
                scope.EndsWith(":", StringComparison.Ordinal) || Path.GetFileName(directory) != TextPatternEngine.Sha256(scope))
                throw new InvalidDataException("Journal directory and canonical identity differ.");
            result.Add(new JournalStream { Directory = directory, Identity = scope, Segments = segments.ToArray(), Resources = resources });
        }

        private static bool LowerHash(string value)
        { return value?.Length == 64 && value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f'); }

        private static bool UnpublishedHeader(string path)
        {
            var name = Path.GetFileName(path); int segment;
            return name.Length == 51 && name.Substring(8, 7) == ".jsonl." && name.EndsWith(".tmp", StringComparison.Ordinal) &&
                name.Substring(15, 32).All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f') &&
                int.TryParse(name.Substring(0, 8), out segment) && segment > 0 && name.Substring(0, 8) == segment.ToString("D8");
        }

        internal JournalCursor Scan(Action<JournalRecord, JournalCursor> visit, CancellationToken token)
        { return CatchUp(null, visit, null, token, recover: false); }
    }

    internal sealed class JournalStream
    {
        internal string Directory, Identity;
        internal string[] Segments;
        internal bool Resources;
    }
}
