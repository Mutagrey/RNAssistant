using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Storage
{
    public sealed partial class WorkspaceFileService
    {
        internal static bool IsBackupRelativePath(string relative)
        {
            var parts = (relative ?? string.Empty).Split('/');
            if (parts.Length != 3 || parts[0] != "workspaces") return false;
            return parts[1] == "file-locators" && WorkspaceStore.BackupHexFile(parts[2], "", ".json", 64) ||
                parts[1] == "file-moves" && (WorkspaceStore.BackupHexFile(parts[2], "ma_", ".json", 32) ||
                    WorkspaceStore.BackupHexFile(parts[2], "ma_", ".json", 64));
        }

        // Called while WorkspaceStore holds catalog then locators, or on a
        // private restore root. Validation never opens live project files.
        internal static string[] GetBackupFiles(AppDataPaths paths, IDictionary<string, string> workspaceRoots)
        {
            var files = new List<string>();
            var locators = new Dictionary<string, FileLocator>(StringComparer.Ordinal);
            var intents = new List<FileMoveIntent>();
            foreach (var name in new[] { "file-locators", "file-moves" })
            {
                var directory = Path.Combine(paths.WorkspaceDirectory, name);
                if (!Directory.Exists(directory) && !File.Exists(directory)) continue;
                WorkspaceStore.RequireBackupDirectory(directory);
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    var fileName = Path.GetFileName(path);
                    if (name == "file-locators" && fileName == "locators.lck")
                    { WorkspaceStore.RequireBackupLock(path); continue; }
                    if (!IsBackupRelativePath("workspaces/" + name + "/" + fileName))
                        throw new InvalidDataException("Unknown workspace file identity record.");
                    if (name == "file-locators")
                    {
                        var locator = WorkspaceStore.ReadBackupRecord<FileLocator>(path, new[] { "FileId", "AbsolutePath" });
                        if (!WorkspaceStore.BackupHexFile(locator.FileId, "file_", "", 32) ||
                            !WorkspaceStore.BackupAbsolutePath(locator.AbsolutePath) ||
                            fileName != TextPatternEngine.Sha256(locator.AbsolutePath) + ".json")
                            throw new InvalidDataException("Workspace file locator differs from its identity or filename.");
                        locators.Add(locator.AbsolutePath, locator);
                    }
                    else
                    {
                        var intent = ReadBackupMoveIntent(path);
                        string root;
                        if (fileName != intent.AttemptId + ".json" || !WorkspaceStore.BackupHexFile(intent.FileId, "file_", "", 32) ||
                            !WorkspaceStore.BackupHexFile(intent.ExpectedSha256, "", "", 64) ||
                            !workspaceRoots.TryGetValue(intent.WorkspaceId, out root) ||
                            BackupMovePath(root, intent.SourceRelativePath) != intent.SourceAbsolutePath ||
                            BackupMovePath(root, intent.TargetRelativePath) != intent.TargetAbsolutePath ||
                            intent.SourceAbsolutePath == intent.TargetAbsolutePath)
                            throw new InvalidDataException("Workspace move intent has inconsistent identity or paths.");
                        intents.Add(intent);
                    }
                    files.Add(path);
                }
            }
            foreach (var group in locators.Values.GroupBy(locator => locator.FileId).Where(group => group.Count() > 1))
            {
                var entries = group.ToArray();
                if (entries.Length != 2 || !intents.Any(intent => intent.FileId == group.Key &&
                    entries.Any(entry => entry.AbsolutePath == intent.SourceAbsolutePath) &&
                    entries.Any(entry => entry.AbsolutePath == intent.TargetAbsolutePath)))
                    throw new InvalidDataException("Workspace file identity has ambiguous locators.");
            }
            return files.ToArray();
        }

        private static string BackupMovePath(string root, string relative)
        {
            var result = root;
            foreach (var part in PathParts(relative, true)) result = Path.Combine(result, part);
            return Path.GetFullPath(result);
        }

        private static FileMoveIntent ReadBackupMoveIntent(string path)
        { return WorkspaceStore.ReadBackupRecord<FileMoveIntent>(path, new[] {
            "AttemptId", "FileId", "WorkspaceId", "SourceRelativePath", "TargetRelativePath",
            "SourceAbsolutePath", "TargetAbsolutePath", "ExpectedSha256" }); }

        internal static void ValidateBackupScopes(AppDataPaths paths, IEnumerable<string> scopes)
        {
            var directory = Path.Combine(paths.WorkspaceDirectory, "file-locators");
            var ids = Directory.Exists(directory) ? new HashSet<string>(Directory.EnumerateFiles(directory, "*.json")
                .Select(path => WorkspaceStore.ReadBackupRecord<FileLocator>(path, new[] { "FileId", "AbsolutePath" }).FileId), StringComparer.Ordinal) :
                new HashSet<string>(StringComparer.Ordinal);
            foreach (var scope in scopes)
            {
                if (scope == null || !scope.StartsWith("file:", StringComparison.Ordinal)) continue;
                var id = scope.Substring("file:".Length);
                if (WorkspaceStore.BackupHexFile(id, "file_", "", 32) && !ids.Contains(id))
                    throw new InvalidDataException("Retained workspace file is missing its durable locator.");
            }
        }

        // The maintenance owner supplies the latest states from its verified
        // canonical scan. Do not consult a possibly stale index or reconcile.
        internal static void ValidateBackupMoveIntents(AppDataPaths paths, IEnumerable<MutationAttempt> attempts)
        {
            var moves = attempts.Where(item => item.Operation == "files.move").ToDictionary(item => item.AttemptId, StringComparer.Ordinal);
            var directory = Path.Combine(paths.WorkspaceDirectory, "file-moves");
            var intents = Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.json")
                .Select(ReadBackupMoveIntent).ToDictionary(item => item.AttemptId, StringComparer.Ordinal) :
                new Dictionary<string, FileMoveIntent>(StringComparer.Ordinal);
            var locatorDirectory = Path.Combine(paths.WorkspaceDirectory, "file-locators");
            var locators = Directory.Exists(locatorDirectory) ? Directory.EnumerateFiles(locatorDirectory, "*.json")
                .Select(path => WorkspaceStore.ReadBackupRecord<FileLocator>(path, new[] { "FileId", "AbsolutePath" }))
                .ToDictionary(item => item.AbsolutePath, StringComparer.Ordinal) : new Dictionary<string, FileLocator>(StringComparer.Ordinal);
            foreach (var intent in intents.Values)
            {
                MutationAttempt attempt;
                if (!moves.TryGetValue(intent.AttemptId, out attempt) || !attempt.ScopeId.Equals(Scope(intent.FileId)) ||
                    !attempt.Target.Equals(Identity(intent.FileId)) || attempt.Payload == null ||
                    intent.ExpectedSha256 != attempt.Payload.Sha256)
                    throw new InvalidDataException("Workspace move intent differs from its canonical attempt.");
                if (attempt.State != MutationAttemptState.Prepared && attempt.State != MutationAttemptState.DispatchMayHaveOccurred) continue;
                FileLocator source, target;
                locators.TryGetValue(intent.SourceAbsolutePath, out source);
                locators.TryGetValue(intent.TargetAbsolutePath, out target);
                if (source == null && target == null || source != null && source.FileId != intent.FileId ||
                    target != null && target.FileId != intent.FileId)
                    throw new InvalidDataException("Pending workspace move has missing or conflicting file locators.");
            }
            foreach (var attempt in moves.Values.Where(item => item.State == MutationAttemptState.Prepared ||
                item.State == MutationAttemptState.DispatchMayHaveOccurred))
                if (attempt.State == MutationAttemptState.DispatchMayHaveOccurred && !intents.ContainsKey(attempt.AttemptId) ||
                    !locators.Values.Any(locator => attempt.ScopeId.Equals(Scope(locator.FileId)) && attempt.Target.Equals(Identity(locator.FileId))))
                    throw new InvalidDataException("Pending workspace move is missing its durable identity metadata.");
            foreach (var group in locators.Values.GroupBy(locator => locator.FileId).Where(group => group.Count() > 1))
                if (!intents.Values.Any(intent => intent.FileId == group.Key && moves[intent.AttemptId].State == MutationAttemptState.DispatchMayHaveOccurred))
                    throw new InvalidDataException("Duplicate workspace file locators are not explained by a pending move.");
        }
    }
}
