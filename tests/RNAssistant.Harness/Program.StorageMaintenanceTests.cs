using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;
using RNAssistant.Application.Conversations;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void StorageJournalMaintenanceAndRestore()
        {
            foreach (var encrypted in new[] { false, true }) WithTempPaths(paths => {
                var protector = encrypted ? new StorageProtector(HistoryIntegrityModes.HmacSha256,
                    HistoryEncryptionModes.Aes256CbcHmacSha256, "synthetic-backup", Enumerable.Repeat((byte)4, 16).ToArray()) : StorageProtector.None;
                var blobs = new ChatBlobStore(paths, () => protector);
                if (encrypted)
                {
                    // Byte-copy contract only; real DPAPI CurrentUser qualification
                    // needs Windows. This injected protector is derived in the test.
                    File.WriteAllText(paths.SettingsFile, JsonConvert.SerializeObject(new AppSettings { HistoryKeySource = HistoryKeySources.CustomSecret }));
                    File.WriteAllBytes(paths.HistoryProtectionSaltFile, Enumerable.Repeat((byte)4, 16).ToArray());
                    File.WriteAllBytes(paths.HistorySecretFile, new byte[] { 10, 20, 30, 40 });
                }
                var journal = new ConversationJournal(paths, DeliveryTestScope, protector);
                var policy = ConversationRunPolicy.For(ChatModes.Agent);
                journal.CreateChat("create-root", "root", "Root", policy);
                journal.CreateChat("create-peer", "peer", "Peer", policy);
                journal.CommitCall("root", "send-call", JournalAgentCall("send", "run"));
                var input = journal.SendAgent("send", "root", "run", "send", AgentChatAddress.IdFor("peer"), "root", "chain", 2, "Retain this accepted input");
                var nested = PayloadRef.FromBlob(blobs.StoreText("nested unresolved prepared state", "text/plain"));
                var execution = new ToolExecutionContext(new ToolCall("write", "core.write", "{}"),
                    new ToolPolicySnapshot("core.write", "fixture", new ToolPolicy(ToolEffect.Write, ToolVerification.Tool, false, false, new[] { "agent" })),
                    "run", "turn-run", "step", DateTime.UtcNow, false, 19, JsonConvert.SerializeObject(new { Payload = nested }));
                var initial = CheckpointState("run", RunLifecycle.Running, 0, 0);
                var saved = journal.CommitRun("checkpoint", "root", null, "invocation", new AgentRunState(initial.Summary, initial.Limits, execution), policy);

                var resources = new ResourceJournal(paths, protector);
                var scope = new ResourceAuthorityScopeId("workspace", "backup");
                var target = new ResourceIdentity("rna://file/backup/source");
                var body = PayloadRef.FromBlob(blobs.StoreText("resource before possible dispatch", "text/plain"));
                var revision = new ResourceRevisionMetadata(new ResourceRef(target.Uri, "r1"), body.Sha256, body);
                resources.Publish("initial", new ResourcePublication(scope, target, null, revision,
                    new ResourceRevisionView(revision.Reference, "text", body.Sha256, body, ResourceCoverage.Whole()), null, null, "initial"));
                var binding = ConversationResourceBinding.ForCall(DeliveryTestScope, "root", "run", "write", scope, target);
                journal.CommitCall("root", "write-call", JournalMutationCall("write", "run", "step"), resourceBinding: binding);
                resources.Prepare("prepare", new MutationAttempt(binding.AttemptId, scope, "core.write", target, "r1", body,
                    TextPatternEngine.Sha256("intended"), MutationAttemptState.Prepared, DateTime.UtcNow));
                resources.MarkDispatch("dispatch", scope, binding.AttemptId);
                var unknownEffect = new ResourceEffect("unknown", "core.write", ResourceEffectOutcome.UnknownAfterDispatch,
                    new[] { new ResourceImpact(target, ResourceImpactRelation.Exact, before: revision.Reference) });
                var unknown = resources.Publish("unknown", new ResourcePublication(scope, target, resources.Head(scope, target), null, null,
                    unknownEffect, binding.AttemptId, "unknown-commit"));
                journal.CommitResult("root", "terminal-unknown", "write", "core.write", "step", "run",
                    new ToolResultMaterialization(ToolResult.Unknown("Outcome unresolved"), resourceEffect: unknownEffect, authorityCommitId: "unknown-commit") {
                        ExecutionProgress = new ToolExecutionProgress(ToolExecutionOutcome.Unknown) }, resourceOutcome: unknown);

                // A different canonical stream has no index cursor after crash.
                var unindexed = new ConversationJournal(paths, "document:unindexed", protector);
                unindexed.Fault = point => { if (point == "before_index") throw new IOException("derived index failed"); };
                unindexed.CreateChat("unindexed-card", "other", "Other", policy);
                var unindexedRun = unindexed.CommitRun("unindexed-run", "other", null, "other-invocation",
                    CheckpointState("other-run", RunLifecycle.Running, 0, 0), policy);
                File.Delete(JournalIndex.IndexPath(paths));
                var before = StorageJournal.Inventory(paths).SelectMany(stream => stream.Segments)
                    .ToDictionary(file => file, File.ReadAllBytes);
                var orphan = blobs.StoreText("uncommitted old body", "text/plain");
                var young = blobs.StoreText("uncommitted young body", "text/plain");
                File.SetLastWriteTimeUtc(blobs.PathFor(orphan.Sha256), DateTime.UtcNow.AddHours(-2));
                var maintenance = JournalCasService(paths, protector);
                var collected = maintenance.Collect();
                AssertTrue(collected.Completed, "independent inventory validates every canonical stream without the production index: " +
                    string.Join("; ", collected.Health.Issues.Select(issue => issue.Kind + ": " + issue.Message)));
                AssertEqual(1, collected.DeletedBlobCount, "only an old unreferenced body is collected");
                AssertTrue(blobs.HasVerifiedReference(young), "new-generation orphan grace retains a recent crash leftover");
                AssertTrue(blobs.HasVerifiedReference(nested.ToBlobReference()), "nested prepared state stays reachable");
                AssertTrue(blobs.HasVerifiedReference(body.ToBlobReference()), "unresolved dispatch retains before-state body");
                AssertTrue(blobs.HasVerifiedReference(unindexedRun.Fact.Body.ToBlobReference()), "a flushed stream with no cursor retains its own body");
                AssertTrue(before.All(pair => pair.Value.SequenceEqual(File.ReadAllBytes(pair.Key))), "maintenance never rewrites accepted JSONL");
                AssertTrue(!File.Exists(JournalIndex.IndexPath(paths)), "maintenance does not use or recreate the production index");

                var backup = Path.Combine(paths.Root, "backup");
                maintenance.BackupJournals(backup);
                AssertTrue(!Directory.EnumerateFiles(backup, "*.sqlite", SearchOption.AllDirectories).Any(), "backup contains authority and closure, no index");
                var restored = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "restored"), _ => protector);
                if (encrypted) AssertTrue(File.ReadAllBytes(paths.HistorySecretFile).SequenceEqual(File.ReadAllBytes(restored.HistorySecretFile)), "protected key file is copied without decoding/rekeying");
                var reopened = new ConversationJournal(restored, DeliveryTestScope, protector);
                AssertEqual(input.Fact.MessageId, reopened.AgentReceipt("send").Fact.MessageId, "accepted input and receipt rebuild without the original index");
                AssertEqual("Retain this accepted input", reopened.ReadAgentText(reopened.AgentReceipt("send")), "protected message restores exact bytes");
                AssertEqual(saved.Position.Hash, reopened.CurrentRun("root").Position.Hash, "run checkpoint preserves immutable identity");
                AssertEqual("unknown-commit", reopened.Receipt("terminal-unknown").Fact.ResourceOutcome.CommitId, "cross-owner outcome receipt survives index-free restore");
                AssertEqual(execution.PreparedStateJson, reopened.ReadRunState(reopened.CurrentRun("root")).InFlightTool.PreparedStateJson, "nested unresolved checkpoint restores");
                AssertEqual(resources.Attempt(scope, binding.AttemptId).Fact.Attempt.State,
                    new ResourceJournal(restored, protector).Attempt(scope, binding.AttemptId).Fact.Attempt.State, "possible dispatch remains unresolved, never replayed");
                AssertTrue(new ConversationJournal(restored, "document:unindexed", protector).FindAgent(AgentChatAddress.IdFor("other")) != null,
                    "stream missing from the source index is included in backup");
                foreach (var pair in before)
                    AssertTrue(pair.Value.SequenceEqual(File.ReadAllBytes(Path.Combine(restored.Root, CasMaintenanceService.RelativePath(paths.Root, pair.Key)))), "restored canonical bytes are unchanged");
                RuntimeThrows<IOException>(() => CasMaintenanceService.RestoreJournals(backup, restored.Root, _ => protector));
                if (encrypted)
                    RuntimeThrows<InvalidDataException>(() => CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "wrong-key"), _ => StorageProtector.None));
                var backupBody = Directory.EnumerateFiles(Path.Combine(backup, "data", "chat-blobs"), "*.blob", SearchOption.AllDirectories).First();
                File.AppendAllText(backupBody, "corrupt");
                RuntimeThrows<InvalidDataException>(() => CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "corrupt-copy"), _ => protector));
                AssertTrue(!Directory.Exists(Path.Combine(paths.Root, "corrupt-copy")), "corrupt restore publishes no destination");
                File.Delete(backupBody);
                RuntimeThrows<InvalidDataException>(() => CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "missing-copy"), _ => protector));
                AssertTrue(!Directory.EnumerateDirectories(paths.Root, ".rna-backup-*").Any(), "failed restore removes only its private staging root");

                var unknownPath = Path.Combine(Path.GetDirectoryName(journal.Journal.SegmentPath(1)), "renamed-segment.dat");
                File.WriteAllText(unknownPath, "unknown");
                AssertTrue(!maintenance.Collect().Completed, "unknown canonical entry blocks all deletion");
                File.Delete(unknownPath);
                var segment = journal.Journal.SegmentPath(1);
                File.AppendAllText(segment, "{\"incomplete\":");
                var torn = File.ReadAllBytes(segment);
                AssertTrue(!maintenance.Audit().CanGarbageCollect && torn.SequenceEqual(File.ReadAllBytes(segment)), "audit refuses an incomplete tail without truncation/adoption");
            });
        }

        private static CasMaintenanceService JournalCasService(AppDataPaths paths, StorageProtector protector)
        {
            var leases = ConversationLeaseService.ForOffice(paths);
            return new CasMaintenanceService(paths, new ChatStore(paths, () => protector), new VbaJournalStore(paths, () => protector),
                () => protector, () => leases.AcquireMaintenance(), () => {
                    if (leases.AnyHeld()) throw new InvalidOperationException("Active run or storage publication blocks maintenance.");
                });
        }

        private static void ApplicationBackupRestoresBothGenerations()
        {
            WithTempPaths(paths => {
                var parent = Path.GetDirectoryName(paths.Root);
                var projectRoot = Path.Combine(parent, "project-" + Guid.NewGuid().ToString("N"));
                var pluginSourceRoot = Path.Combine(parent, "plugin-source-" + Guid.NewGuid().ToString("N"));
                var backup = Path.Combine(parent, "application-backup-" + Guid.NewGuid().ToString("N"));
                var restoredRoot = Path.Combine(parent, "application-restore-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var legacy = new ChatStore(paths, () => StorageProtector.None)
                        .Create("Excel", "legacy-document", "Legacy.xlsx", "Legacy chat");
                    var nativeStore = new ConversationJournal(paths, "document:application-backup", StorageProtector.None);
                    var native = nativeStore.CreateChat("application-native", "native-document", "Native", ConversationRunPolicy.For(ChatModes.Agent));
                    var pluginFixture = SeedLegacyPluginImport(paths, StorageProtector.None, pluginSourceRoot);
                    ResourceJournal pluginResources;
                    var nativePlugins = NativeLegacyPluginTarget(paths, StorageProtector.None, out pluginResources);
                    var importedPlugin = nativePlugins.ImportLegacyRegistration(pluginFixture.Registration.PluginId, pluginFixture.Origin);
                    AssertEqual(2, nativePlugins.ListVersions(pluginFixture.Registration.PluginId, null).Items.Count,
                        "legacy plugin versions are migrated before application backup");
                    var workspaces = new WorkspaceStore(paths);
                    workspaces.Open(projectRoot);
                    Directory.CreateDirectory(Path.Combine(paths.ToolsDirectory, "common", "custom"));
                    File.WriteAllText(Path.Combine(paths.ToolsDirectory, "common", "custom", "tool.json"), "{\"id\":\"custom\"}");
                    Directory.CreateDirectory(Path.Combine(paths.SkillsDirectory, "custom"));
                    File.WriteAllText(Path.Combine(paths.SkillsDirectory, "custom", "SKILL.md"), "# Custom");
                    Directory.CreateDirectory(Path.Combine(paths.Root, "assets"));
                    File.WriteAllText(Path.Combine(paths.Root, "assets", "asset.txt"), "retained asset");
                    Directory.CreateDirectory(Path.Combine(paths.Root, "logs"));
                    File.WriteAllText(Path.Combine(paths.Root, "logs", "runtime.log"), "excluded log");
                    Directory.CreateDirectory(Path.Combine(paths.Root, "plugin-results", "result"));
                    File.WriteAllText(Path.Combine(paths.Root, "plugin-results", "result", "value.txt"), "excluded result");
                    var result = JournalCasService(paths, StorageProtector.None).BackupApplication(backup);
                    AssertEqual(2, result.ExternalWorkspaceRoots.Count, "backup records project and plugin source roots");
                    AssertTrue(File.Exists(Path.Combine(backup, "backup.json")), "application manifest is published");
                    AssertTrue(File.Exists(Path.Combine(backup, "data", "chats", AppDataPaths.SafeFileName("Excel|legacy-document"),
                        AppDataPaths.SafeFileName(legacy.Id) + ".events.jsonl")), "legacy chat is copied");
                    AssertTrue(Directory.EnumerateFiles(Path.Combine(backup, "data", "storage-v2", "conversation-streams"),
                        "*.jsonl", SearchOption.AllDirectories).Any(), "native stream is copied");
                    AssertTrue(!File.Exists(Path.Combine(backup, "data", "storage-v2", "index.sqlite")), "derived native index is omitted");
                    AssertTrue(!Directory.Exists(Path.Combine(backup, "data", "logs")), "optional logs are omitted");
                    AssertTrue(!Directory.Exists(Path.Combine(backup, "data", "plugin-results")), "result staging is omitted");

                    var restored = CasMaintenanceService.RestoreApplication(backup, restoredRoot, _ => StorageProtector.None);
                    var restoredPaths = restored.RestoredPaths;
                    AssertTrue(restored.IndexRebuilt && restored.RestartRequired, "restore reports rebuild and explicit restart");
                    AssertEqual(0, restored.MissingExternalWorkspaceRoots.Count, "existing external workspaces are reported available");
                    AssertTrue(new ChatStore(restoredPaths).ListHeaders(false).Any(item => item.Id == legacy.Id), "legacy chat restores");
                    AssertTrue(StorageJournal.Inventory(restoredPaths).All(stream => stream.Segments.All(File.Exists)),
                        "all native journals restore");
                    ResourceJournal restoredPluginResources;
                    var restoredPlugins = NativeLegacyPluginTarget(restoredPaths, StorageProtector.None, out restoredPluginResources);
                    AssertEqual(JsonConvert.SerializeObject(importedPlugin.Registration),
                        JsonConvert.SerializeObject(restoredPlugins.Get(pluginFixture.Registration.PluginId)),
                        "migrated plugin registration restores without identity drift");
                    AssertEqual(2, restoredPlugins.ListVersions(pluginFixture.Registration.PluginId, null).Items.Count,
                        "migrated plugin version catalog restores");
                    var restoredRelease = restoredPlugins.ReadSavedRelease(pluginFixture.Registration.PluginId,
                        pluginFixture.Latest.VersionId);
                    AssertEqual("<main>Saved version 3</main>", Encoding.UTF8.GetString(
                        restoredPlugins.ReadFile(restoredRelease, "index.html")),
                        "restored plugin release reads its immutable CAS bytes");
                    AssertEqual("retained asset", File.ReadAllText(Path.Combine(restoredPaths.Root, "assets", "asset.txt")), "local asset restores byte-exactly");
                    AssertTrue(File.Exists(Path.Combine(restoredPaths.Root, "tools", "common", "custom", "tool.json")), "tool package restores");
                    AssertTrue(File.Exists(Path.Combine(restoredPaths.Root, "skills", "custom", "SKILL.md")), "skill package restores");
                    AssertTrue(File.Exists(JournalIndex.IndexPath(restoredPaths)), "native index is rebuilt in the restored root");
                    AssertTrue(!Directory.Exists(Path.Combine(restoredPaths.Root, "logs")), "excluded logs stay excluded");
                    RuntimeThrows<IOException>(() => CasMaintenanceService.RestoreApplication(backup, restoredPaths.Root, _ => StorageProtector.None));

                    File.AppendAllText(Path.Combine(backup, "data", "assets", "asset.txt"), "corrupt");
                    var corruptTarget = restoredPaths.Root + "-corrupt";
                    RuntimeThrows<InvalidDataException>(() => CasMaintenanceService.RestoreApplication(backup, corruptTarget, _ => StorageProtector.None));
                    AssertTrue(!Directory.Exists(corruptTarget), "tampered application backup publishes no destination");
                }
                finally
                {
                    if (Directory.Exists(projectRoot)) Directory.Delete(projectRoot, true);
                    if (Directory.Exists(pluginSourceRoot)) Directory.Delete(pluginSourceRoot, true);
                    if (Directory.Exists(backup)) Directory.Delete(backup, true);
                    if (Directory.Exists(restoredRoot)) Directory.Delete(restoredRoot, true);
                }
            });
        }

        private static void StorageJournalIdentityBackupValidation()
        {
            WithTempPaths(paths => {
                var workspaces = new WorkspaceStore(paths);
                var workspace = workspaces.Open(Path.Combine(paths.Root, "identity-project"));
                var resources = new ResourceJournal(paths);
                var files = new WorkspaceFileService(paths, resources, resources, resources);
                var original = files.CreateText(workspace, "index.html", "<main>Retain file identity</main>");
                workspaces.SetHtmlProjectDefinition(workspace, null, new HtmlProjectDefinition { EntryPath = "index.html", Members = new[] { "index.html" } });
                var versions = new WorkspaceHtmlVersionService(paths, ConversationStore(new ChatStore(paths)), files, resources, resources);
                var browser = versions.Resolve(new BrowserResolveRequest { OwnerScope = new BrowserOwnerScope { Kind = BrowserOwnerKind.User, ScopeId = "local" },
                    Locator = BrowserLocalLocator.ForProject(workspace.WorkspaceId, "index.html") }).Target;
                var state = versions.State(new BrowserVersionRequest { Target = browser });
                var saved = versions.Command(new BrowserVersionCommand { Target = browser, Action = BrowserVersionAction.Save,
                    ExpectedStateRevision = state.StateRevision, ExpectedContentRevision = state.ContentRevision,
                    ExpectedDefinitionRevision = state.DefinitionRevision, OperationId = "identity-local-save" });
                AssertEqual(BrowserVersionOutcome.Saved, saved.Outcome, "user-local HTML saved without a chat: " + saved.Message);
                AssertTrue(!Directory.EnumerateFiles(paths.JournalConversationDirectory, "*.jsonl", SearchOption.AllDirectories).Any(),
                    "resource-only HTML fixture has no conversation identity to infer from");
                var movedRoot = workspace.RootPath + "-moved";
                Directory.Move(workspace.RootPath, movedRoot);
                workspace = workspaces.ConnectProject(movedRoot, false);
                var hidden = workspaces.Open(Path.Combine(paths.Root, "hidden-project"));
                workspaces.HideProject(hidden.WorkspaceId);
                var documents = new DocumentAuthorityRegistry(paths);
                var oldLocator = Path.Combine(paths.Root, "old.xlsx");
                var locator = Path.Combine(paths.Root, "renamed.xlsx");
                var authority = documents.Resolve("Excel", "before-restore", oldLocator);
                AssertEqual(authority.Id, documents.Resolve("Excel", "before-restore", locator).Id, "Save As keeps logical identity before backup");
                var maintenance = JournalCasService(paths, StorageProtector.None);
                var backup = Path.Combine(paths.Root, "identity-backup");
                maintenance.BackupJournals(backup);
                var restored = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "identity-restore"), _ => StorageProtector.None);
                var restoredWorkspaces = new WorkspaceStore(restored);
                AssertEqual(movedRoot, restoredWorkspaces.OpenConnectedHistory(workspace.WorkspaceId).RootPath, "a pre-backup project move retains its current association");
                AssertTrue(!restoredWorkspaces.ListConnectedMetadata().Any(item => item.WorkspaceId == hidden.WorkspaceId), "hidden state remains owned by the restored id record");
                var restoredResources = new ResourceJournal(restored);
                var restoredFiles = new WorkspaceFileService(restored, restoredResources, restoredResources, restoredResources);
                AssertEqual(original.Reference.Uri, restoredFiles.ReadText(restoredWorkspaces.OpenConnected(workspace.WorkspaceId), "index.html").Reference.Uri,
                    "file identity is retained through move, backup and restoration");
                var restoredDocuments = new DocumentAuthorityRegistry(restored);
                AssertEqual(authority.Id, restoredDocuments.Resolve("Excel", "new-runtime", locator).Id, "fresh Office runtime resolves the saved identity");
                var identityFiles = new[] {
                    "workspaces/" + workspace.WorkspaceId + ".json",
                    "workspaces/path-" + TextPatternEngine.Sha256(movedRoot) + ".json",
                    "workspaces/file-locators/" + TextPatternEngine.Sha256(Path.Combine(movedRoot, "index.html")) + ".json",
                    "resource-authority/documents.json"
                };
                var manifestPath = Path.Combine(backup, "backup.json");
                var manifestBytes = File.ReadAllBytes(manifestPath);
                var attempt = 0;
                Action reject = () => {
                    var target = Path.Combine(paths.Root, "rejected-identity-" + ++attempt);
                    RuntimeThrows<InvalidDataException>(() => CasMaintenanceService.RestoreJournals(backup, target, _ => StorageProtector.None));
                    AssertTrue(!Directory.Exists(target), "invalid identity restore publishes no destination");
                    AssertTrue(!Directory.EnumerateDirectories(paths.Root, ".rna-backup-*").Any(), "invalid identity restore cleans its private staging root");
                };
                foreach (var relative in identityFiles)
                {
                    var path = Path.Combine(backup, "data", relative.Replace('/', Path.DirectorySeparatorChar));
                    var bytes = File.ReadAllBytes(path);
                    AssertTrue(bytes.SequenceEqual(File.ReadAllBytes(Path.Combine(restored.Root, relative.Replace('/', Path.DirectorySeparatorChar)))), "identity owner bytes are copied unchanged");
                    try
                    {
                        File.AppendAllText(path, "corrupt"); reject();
                        File.Delete(path); reject();
                        File.WriteAllBytes(path, bytes);
                        if (!OperatingSystem.IsWindows())
                        {
                            var outside = Path.Combine(paths.Root, "outside-identity.json");
                            File.WriteAllBytes(outside, bytes); File.Delete(path);
                            File.CreateSymbolicLink(path, outside); reject(); File.Delete(path);
                        }
                    }
                    finally { File.WriteAllBytes(path, bytes); }
                }
                AssertTrue(authority.Id != restoredDocuments.Resolve("Excel", "old-path-runtime", oldLocator).Id, "old Save As locator does not become an identity alias");
                try
                {
                    RewriteIdentityBackupManifest(backup, manifest => manifest.Format = "rnassistant-journal-backup-v1"); reject();
                    File.WriteAllBytes(manifestPath, manifestBytes);
                    var missingPair = identityFiles[1];
                    RewriteIdentityBackupManifest(backup, manifest => manifest.Files.RemoveAll(item => item.Path == missingPair)); reject();
                    File.WriteAllBytes(manifestPath, manifestBytes);
                    RewriteIdentityBackupManifest(backup, manifest => manifest.Files.RemoveAll(item => item.Path.StartsWith("workspaces/", StringComparison.Ordinal) && item.Path.Split('/').Length == 2)); reject();
                    File.WriteAllBytes(manifestPath, manifestBytes);
                    RewriteIdentityBackupManifest(backup, manifest => manifest.Files.RemoveAll(item => item.Path.StartsWith("workspaces/file-locators/", StringComparison.Ordinal))); reject();
                    File.WriteAllBytes(manifestPath, manifestBytes);
                    RewriteIdentityBackupManifest(backup, manifest => manifest.Files[0].Path = "workspaces/../settings.json"); reject();
                    File.WriteAllBytes(manifestPath, manifestBytes);
                    var registryPath = Path.Combine(backup, "data", "resource-authority", "documents.json");
                    var registryBytes = File.ReadAllBytes(registryPath);
                    try
                    {
                        var malformed = JObject.Parse(File.ReadAllText(registryPath));
                        ((JArray)malformed["Documents"]).Add(malformed["Documents"][0].DeepClone());
                        File.WriteAllText(registryPath, malformed.ToString());
                        RewriteIdentityBackupManifest(backup, manifest => {
                            var entry = manifest.Files.Single(item => item.Path == "resource-authority/documents.json");
                            var bytes = File.ReadAllBytes(registryPath); entry.Bytes = bytes.Length;
                            using (var hash = SHA256.Create()) entry.Sha256 = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                        });
                        reject(); // Correct checksums do not make ambiguous authority records complete.
                    }
                    finally { File.WriteAllBytes(registryPath, registryBytes); }
                }
                finally { File.WriteAllBytes(manifestPath, manifestBytes); }
                var sourcePair = Path.Combine(paths.Root, identityFiles[1].Replace('/', Path.DirectorySeparatorChar));
                var sourcePairBytes = File.ReadAllBytes(sourcePair);
                var refusedBackup = Path.Combine(paths.Root, "incomplete-identity-backup");
                try
                {
                    File.Delete(sourcePair);
                    RuntimeThrows<InvalidDataException>(() => maintenance.BackupJournals(refusedBackup));
                    AssertTrue(!Directory.Exists(refusedBackup), "incomplete source owners cannot publish a backup");
                }
                finally { File.WriteAllBytes(sourcePair, sourcePairBytes); }
                var allConnections = Directory.GetFiles(paths.WorkspaceDirectory, "*.json").ToDictionary(path => path, File.ReadAllBytes);
                try
                {
                    foreach (var path in allConnections.Keys) File.Delete(path);
                    RuntimeThrows<InvalidDataException>(() => maintenance.BackupJournals(refusedBackup));
                    AssertTrue(!Directory.Exists(refusedBackup), "resource-only HTML cannot be backed up without its Workspace owner");
                }
                finally { foreach (var entry in allConnections) File.WriteAllBytes(entry.Key, entry.Value); }
                var unknown = Path.Combine(paths.WorkspaceDirectory, "unknown-owner.json");
                try
                {
                    File.WriteAllText(unknown, "{}");
                    RuntimeThrows<InvalidDataException>(() => maintenance.BackupJournals(refusedBackup));
                    AssertTrue(!Directory.Exists(refusedBackup), "unknown workspace state is not silently omitted");
                }
                finally { File.Delete(unknown); }
                AssertTrue(!Directory.EnumerateDirectories(paths.Root, ".rna-backup-*").Any(), "rejected source backup leaves no staging root");
            });
        }

        private static void RewriteIdentityBackupManifest(string backup, Action<JournalBackupManifest> change)
        {
            var path = Path.Combine(backup, "backup.json");
            var manifest = JsonConvert.DeserializeObject<JournalBackupManifest>(File.ReadAllText(path));
            change(manifest); manifest.Digest = null;
            manifest.Digest = StorageProtector.None.ComputeEventHash(stream => {
                using (var text = new StreamWriter(stream, new UTF8Encoding(false), 4096, true))
                using (var writer = new JsonTextWriter(text)) { JsonSerializer.CreateDefault().Serialize(writer, manifest); writer.Flush(); }
            }, manifest.HashAlgorithm, manifest.ProtectionKeyId);
            File.WriteAllText(path, JsonConvert.SerializeObject(manifest));
        }

        private static void StorageJournalMaintenanceExcludesPublish()
        {
            WithTempPaths(paths => {
                var maintenance = JournalCasService(paths, StorageProtector.None);
                using (ConversationLeaseService.ForScope(paths, DeliveryTestScope).Reserve("root"))
                    RuntimeThrows<InvalidOperationException>(() => maintenance.Collect());
                var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
                start.ArgumentList.Add(typeof(Program).Assembly.Location); start.ArgumentList.Add("--journal-maintenance-probe"); start.ArgumentList.Add(paths.Root);
                using (var child = Process.Start(start))
                {
                    try
                    {
                        AssertTrue(SpinWait.SpinUntil(() => File.Exists(Path.Combine(paths.Root, "publication-ready")) || child.HasExited, 10000), "publisher reports its activity");
                        AssertTrue(!child.HasExited, "publisher retains activity between CAS and JSONL");
                        RuntimeThrows<InvalidOperationException>(() => maintenance.Collect());
                        File.WriteAllText(Path.Combine(paths.Root, "release-publication"), "release");
                        AssertTrue(child.WaitForExit(10000) && child.ExitCode == 0, "publisher releases its activity");
                        AssertTrue(maintenance.Audit().CanGarbageCollect, "maintenance can resume after publication abort");
                    }
                    finally { if (!child.HasExited) { child.Kill(); child.WaitForExit(); } }
                }
            });
        }

        private static int StorageMaintenanceProbe(string root)
        {
            var paths = AppDataPaths.CreateForRoot(root);
            using (RuntimeStorageActivity.Acquire(paths))
            {
                new ChatBlobStore(paths).StoreText("pending publication", "text/plain");
                File.WriteAllText(Path.Combine(root, "publication-ready"), "ready");
                return SpinWait.SpinUntil(() => File.Exists(Path.Combine(root, "release-publication")), 15000) ? 0 : 3;
            }
        }
    }
}
