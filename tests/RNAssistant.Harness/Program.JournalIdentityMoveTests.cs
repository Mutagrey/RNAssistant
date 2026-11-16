using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void StorageJournalIdentityMoveRestore()
        {
            WithTempPaths(paths => {
                var workspaces = new WorkspaceStore(paths);
                var workspace = workspaces.Open(Path.Combine(paths.Root, "prepared-project"));
                var resources = new ResourceJournal(paths);
                var files = new WorkspaceFileService(paths, resources, resources, resources);
                var source = files.CreateText(workspace, "before.txt", "prepared source");
                var revision = resources.GetRevision(source.Evidence.ScopeId, source.Reference);
                // MoveText prepares the canonical attempt before saving its
                // path intent. This is that real crash boundary, before dispatch.
                var prepared = ((IResourceMutationJournal)resources).Prepare(source.Evidence.ScopeId, "files.move",
                    new ResourceIdentity(source.Reference.Uri), source.Reference.Revision, revision.Payload);
                var backup = Path.Combine(paths.Root, "prepared-backup");
                JournalCasService(paths, StorageProtector.None).BackupJournals(backup);
                var restored = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "prepared-restore"), _ => StorageProtector.None);
                var pending = new ResourceJournal(restored).Unresolved().Single();
                AssertEqual(prepared.AttemptId, pending.AttemptId, "prepared move identity remains exact without an undispatched path intent");
                AssertEqual(MutationAttemptState.Prepared, pending.State, "restore does not abandon or dispatch a prepared move");
                AssertEqual("prepared source", File.ReadAllText(Path.Combine(workspace.RootPath, "before.txt")), "prepared restore does not touch live source bytes");
                AssertTrue(!Directory.Exists(Path.Combine(restored.WorkspaceDirectory, "file-moves")), "restore does not invent a move intent");
            });

            WithTempPaths(paths => {
                var workspaces = new WorkspaceStore(paths);
                var workspace = workspaces.Open(Path.Combine(paths.Root, "pending-project"));
                var resources = new ResourceJournal(paths);
                var files = new WorkspaceFileService(paths, resources, resources, resources);
                var source = files.CreateText(workspace, "before.txt", "move exactly once");
                var dispatches = 0;
                files.FaultPoint = point => { if (point == "after-dispatch") throw new IOException("Lost move read-back."); };
                var failure = RuntimeThrows<WorkspaceFileException>(() => files.MoveText(workspace, "before.txt", source.Reference, "after.txt", () => dispatches++));
                files.FaultPoint = null;
                AssertEqual("effect_unknown", failure.Code, "actual move can finish before its read-back fails");
                var pending = resources.Unresolved().Single();
                AssertEqual(MutationAttemptState.DispatchMayHaveOccurred, pending.State, "actual owner retains uncertain dispatch");
                var relativeIntent = "workspaces/file-moves/" + pending.AttemptId + ".json";
                var intentPath = Path.Combine(paths.Root, relativeIntent.Replace('/', Path.DirectorySeparatorChar));
                var intentBytes = File.ReadAllBytes(intentPath);
                var maintenance = JournalCasService(paths, StorageProtector.None);
                var backup = Path.Combine(paths.Root, "pending-backup");
                maintenance.BackupJournals(backup);
                var journalBytes = StorageJournal.Inventory(paths).SelectMany(stream => stream.Segments)
                    .ToDictionary(path => Path.GetRelativePath(paths.Root, path), File.ReadAllBytes);

                var movedRoot = workspace.RootPath + "-relocated";
                Directory.Move(workspace.RootPath, movedRoot);
                var restored = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "pending-restore"), _ => StorageProtector.None);
                var restoredResources = new ResourceJournal(restored);
                var restoredPending = restoredResources.Unresolved().Single();
                AssertEqual(pending.AttemptId, restoredPending.AttemptId, "pending move identity survives isolated restore");
                AssertEqual(MutationAttemptState.DispatchMayHaveOccurred, restoredPending.State, "restore neither replays nor reconciles the uncertain move");
                foreach (var item in journalBytes)
                    AssertTrue(item.Value.SequenceEqual(File.ReadAllBytes(Path.Combine(restored.Root, item.Key))), "restore leaves canonical resource records byte-identical");
                AssertTrue(intentBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(restored.Root, relativeIntent.Replace('/', Path.DirectorySeparatorChar)))), "restore preserves the exact unresolved intent bytes");
                AssertTrue(!File.Exists(Path.Combine(movedRoot, "before.txt")), "restore does not recreate the moved source");
                AssertEqual("move exactly once", File.ReadAllText(Path.Combine(movedRoot, "after.txt")), "restore leaves the already moved target unchanged");
                AssertEqual(1, dispatches, "only the requested original move dispatched");

                var restoredWorkspaces = new WorkspaceStore(restored);
                AssertEqual(workspace.RootPath, restoredWorkspaces.OpenConnectedHistory(workspace.WorkspaceId).RootPath, "restore does not guess the new live project path");
                var reconnected = restoredWorkspaces.ConnectProject(movedRoot, false);
                AssertEqual(workspace.WorkspaceId, reconnected.WorkspaceId, "explicit existing owner reconnect preserves workspace identity");
                var relocatedLocator = Path.Combine(restored.WorkspaceDirectory, "file-locators",
                    TextPatternEngine.Sha256(Path.Combine(movedRoot, "before.txt")) + ".json");
                AssertEqual(pending.ScopeId.Id, (string)JObject.Parse(File.ReadAllText(relocatedLocator))["FileId"], "explicit reconnect relocates the same pending file identity");
                var restoredIntentPath = Path.Combine(restored.Root, relativeIntent.Replace('/', Path.DirectorySeparatorChar));
                var relocatedIntentBytes = File.ReadAllBytes(restoredIntentPath);
                var relocatedIntent = JObject.Parse(File.ReadAllText(restoredIntentPath));
                AssertEqual(Path.Combine(movedRoot, "after.txt"), (string)relocatedIntent["TargetAbsolutePath"], "explicit reconnect relocates pending intent through its owner");
                AssertEqual(MutationAttemptState.DispatchMayHaveOccurred, restoredResources.Unresolved().Single().State, "reconnect does not resolve uncertain effects");

                try
                {
                    File.Delete(intentPath);
                    var rejected = Path.Combine(paths.Root, "missing-intent-backup");
                    RuntimeThrows<InvalidDataException>(() => maintenance.BackupJournals(rejected));
                    AssertTrue(!Directory.Exists(rejected), "missing dispatched intent cannot publish a backup");
                }
                finally { File.WriteAllBytes(intentPath, intentBytes); }
                var manifestPath = Path.Combine(backup, "backup.json");
                var manifestBytes = File.ReadAllBytes(manifestPath);
                try
                {
                    RewriteIdentityBackupManifest(backup, manifest => manifest.Files.RemoveAll(item => item.Path == relativeIntent));
                    var rejected = Path.Combine(paths.Root, "missing-intent-restore");
                    RuntimeThrows<InvalidDataException>(() => CasMaintenanceService.RestoreJournals(backup, rejected, _ => StorageProtector.None));
                    AssertTrue(!Directory.Exists(rejected), "correctly checksummed incomplete intent restore publishes no destination");
                }
                finally { File.WriteAllBytes(manifestPath, manifestBytes); }

                // Explicit inspection is separate from restore. Recreate the
                // existing documented failed-cleanup state after it completes.
                var restoredFiles = new WorkspaceFileService(restored, restoredResources, restoredResources, restoredResources);
                AssertEqual(WorkspaceRecoveryOutcome.UnknownAfterDispatch,
                    restoredFiles.ReconcileUncertain(reconnected, "after.txt").Outcome, "explicit owner inspection preserves unknown causality");
                File.WriteAllBytes(restoredIntentPath, relocatedIntentBytes);
                var current = restoredFiles.ReadText(reconnected, "after.txt");
                restoredFiles.MoveText(reconnected, "after.txt", current.Reference, "final.txt");
                var staleBackup = Path.Combine(paths.Root, "stale-intent-backup");
                JournalCasService(restored, StorageProtector.None).BackupJournals(staleBackup);
                var staleRestored = CasMaintenanceService.RestoreJournals(staleBackup, Path.Combine(paths.Root, "stale-intent-restore"), _ => StorageProtector.None);
                AssertEqual(0, new ResourceJournal(staleRestored).Unresolved().Count, "stale completed intent never revives a resolved move");
                AssertTrue(relocatedIntentBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(staleRestored.Root, relativeIntent.Replace('/', Path.DirectorySeparatorChar)))), "stale completed intent survives without rewriting its former paths");
                AssertEqual("move exactly once", File.ReadAllText(Path.Combine(movedRoot, "final.txt")), "stale intent restore leaves the later live move intact");
                AssertTrue(!Directory.EnumerateDirectories(paths.Root, ".rna-backup-*").Any(), "failed restore stages are removed");
            });
        }
    }
}
