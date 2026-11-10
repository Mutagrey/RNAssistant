using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
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
                unindexed.Fault = point => { throw new IOException("derived index failed"); };
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
