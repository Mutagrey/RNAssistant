using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void JournalResourceWorkspace()
        {
            WithTempPaths(paths => {
                var resources = new ResourceJournal(paths);
                var peer = new ResourceJournal(paths); var notifications = 0;
                peer.Changed += (sender, args) => notifications++;
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "project"));
                var files = new WorkspaceFileService(paths, resources, resources, resources);
                var writes = 0;
                var initial = files.CreateText(workspace, "source.txt", "before\n", () => writes++);
                var scope = initial.Evidence.ScopeId;
                var changed = files.ReplaceText(workspace, "source.txt", initial.Reference, "after\n", () => writes++);
                AssertEqual(2, writes, "one dispatch for each requested file change");
                AssertEqual(2, notifications, "existing cross-instance notifications survive the backend change");
                AssertEqual("before\n", files.ReadExactText(workspace, "source.txt", initial.Reference).Text, "retained source survives replace");
                AssertEqual(0, resources.Unresolved().Count, "verified publication resolves the attempt in the same frame");
                RuntimeThrows<InvalidOperationException>(() => resources.MarkDispatchMayHaveOccurred(changed.AuthorityCommit.MutationAttemptId));

                files.FaultPoint = point => { if (point == "after-dispatch") throw new IOException("lost read-back"); };
                try { files.ReplaceText(workspace, "source.txt", changed.Reference, "uncertain\n", () => writes++); throw new Exception("Expected uncertain write."); }
                catch (WorkspaceFileException error) { AssertEqual("effect_unknown", error.Code, "uncertain write is explicit"); }
                AssertEqual("uncertain\n", File.ReadAllText(Path.Combine(workspace.RootPath, "source.txt")), "actual file effect occurred once");
                resources = new ResourceJournal(paths);
                files = new WorkspaceFileService(paths, resources, resources, resources);
                AssertEqual(MutationAttemptState.DispatchMayHaveOccurred, resources.Unresolved().Single().State, "restart retains unknown dispatch");
                RuntimeThrows<WorkspaceFileException>(() => files.ReplaceText(workspace, "source.txt", changed.Reference, "must not repeat", () => writes++));
                AssertEqual(3, writes, "pending effect cannot repeat");
                files.CreateText(workspace, "independent.txt", "independent");
                var recovery = files.ReconcileUncertain(workspace, "source.txt");
                AssertEqual(WorkspaceRecoveryOutcome.UnknownAfterDispatch, recovery.Outcome, "explicit inspection never invents known causality");
                AssertEqual(3, writes, "inspection and restart never replay");
                AssertEqual(ResourceEffectOutcome.UnknownAfterDispatch, resources.Capture(scope).Commits.Last().Effect.Outcome, "unknown remains in canonical history");
                AssertEqual("uncertain\n", files.ReadText(workspace, "source.txt").Text, "explicit inspection publishes current bytes");
                var current = files.ReadText(workspace, "source.txt");
                files.DeleteText(workspace, "source.txt", current.Reference);
                var restored = files.RestoreDeletedText(workspace, "source.txt", expectedDeleted: current.Reference);
                var moved = files.MoveText(workspace, "source.txt", restored.Reference, "renamed.txt");
                AssertEqual(current.Reference.Uri, moved.Reference.Uri, "delete, explicit restore and move preserve logical identity");
                AssertEqual("uncertain\n", files.ReadText(workspace, "renamed.txt").Text, "file lifecycle keeps exact bytes");
                AssertTrue(!Directory.EnumerateFiles(paths.ResourceAuthorityDirectory, "*.jsonl").Any(), "real file consumer writes only the new resource stream");
            });
        }

        private static void JournalResourceOffice()
        {
            WithTempPaths(paths => {
                var resources = new ResourceJournal(paths); var bodies = new ChatBlobStore(paths);
                var authority = new ResourceAuthorityService(resources, resources, resources, bodies);
                var session = new ChatSession { DocumentAuthorityId = "doc-journal-office" };
                var observer = new ResourceMutationAuthorityObserver(authority, resources, session, bodies);
                var arguments = new Dictionary<string, object> { ["moduleName"] = "Module1" };
                var context = new ToolRuntimeFixture().Context(JsonConvert.SerializeObject(arguments), toolId: "vba.module.replace");
                var attemptId = observer.Prepare(context, arguments);
                var attempt = resources.Unresolved().Single();
                AssertEqual(MutationAttemptState.Prepared, attempt.State, "Office observer persists before dispatch");
                observer.MarkDispatchMayHaveOccurred(attemptId);
                var body = PayloadRef.FromBlob(bodies.StoreText("Sub Main()\nEnd Sub", "text/plain"));
                var readBack = attempt.IntendedImpacts.Select(impact => new ResourceMutationReadBack(impact.Identity,
                    true, "text", body.Sha256, body)).ToArray();
                var record = new ToolExecutionRecord(context, ToolExecutionOutcome.Ok, DateTime.UtcNow,
                    evidence: new ToolExecutionEvidence(ToolDispatchEvidence.MayHaveDispatched, ToolEffectEvidence.VerifiedChange),
                    result: ToolResult.Ok("read-back verified"), resourceReadBack: readBack);
                var commit = observer.Complete(attemptId, record);
                AssertEqual(2, commit.HeadChanges.Count, "module and project are published atomically");
                AssertEqual(0, resources.Unresolved().Count, "Office terminal and attempt resolution share a frame");
                AssertEqual(commit.CommitId, resources.Capture(attempt.ScopeId).FindMutationCommit(attemptId).CommitId, "exact mutation receipt is addressable");

                var lost = observer.Prepare(context, arguments);
                observer.MarkDispatchMayHaveOccurred(lost);
                observer.ReleaseUnresolved(lost); // simulated process exit, with no read-back
                resources = new ResourceJournal(paths);
                authority = new ResourceAuthorityService(resources, resources, resources, bodies);
                ResourceMutationAuthorityObserver.ReconcileInterrupted(authority, resources);
                var snapshot = resources.Capture(attempt.ScopeId);
                AssertTrue(snapshot.Heads.Values.All(head => head.Knowledge == HeadKnowledge.Unknown), "interrupted Office mutation cannot retain known current heads");
                AssertEqual(ResourceEffectOutcome.UnknownAfterDispatch, snapshot.FindMutationCommit(lost).Effect.Outcome, "recovery records unknown without invocation");
                RuntimeThrows<ResourceAuthorityConflictException>(() => resources.Prepare(attempt.ScopeId, "vba.module.replace", attempt.Target));
                AssertEqual(body.Sha256, resources.GetRevision(attempt.ScopeId, commit.HeadChanges[0].After.Revision).Payload.Sha256, "prior exact Office revision remains available");
            });
        }

        private static void JournalResourcePortRecovery()
        {
            WithTempPaths(paths => {
                var resources = new ResourceJournal(paths); var bodies = new ChatBlobStore(paths);
                var scope = new ResourceAuthorityScopeId("workspace", "resource-ports");
                var target = new ResourceIdentity("rna://file/resource-ports/source");
                var body = PayloadRef.FromBlob(bodies.StoreText("immutable source", "text/plain"));
                var exact = new ResourceRef(target.Uri, "r1");
                var revision = new ResourceRevisionMetadata(exact, body.Sha256, body);
                var view = new ResourceRevisionView(exact, "text", body.Sha256, body, ResourceCoverage.Whole());
                resources.RegisterRevision(scope, revision); resources.RegisterView(scope, view);
                var bytes = resources.Journal.WrittenBytes;
                resources.RegisterRevision(scope, revision); resources.RegisterView(scope, view);
                AssertEqual(bytes, resources.Journal.WrittenBytes, "registration retries are immutable receipts");
                var other = PayloadRef.FromBlob(bodies.StoreText("substituted", "text/plain"));
                RuntimeThrows<InvalidOperationException>(() => resources.RegisterView(scope, new ResourceRevisionView(exact, "text", other.Sha256, other, ResourceCoverage.Whole())));
                var initial = ResourceAuthorityCommit.Create(scope, 0, null, new[] { new ResourceHeadChange(target, null, ResourceHeadState.Known(exact, 1)) }, AuthorityCommitReason.InitialObservation);
                resources.Publish(initial);
                var frozen = resources.Capture(scope);
                var attempt = resources.Prepare(scope, "files.replace", target, exact.Revision, body);
                resources.MarkDispatchMayHaveOccurred(attempt.AttemptId);
                var effect = new ResourceEffect("effect-receipt", "files.replace", ResourceEffectOutcome.VerifiedNoChange,
                    new[] { new ResourceImpact(target, ResourceImpactRelation.Exact, before: exact, after: exact) });
                var terminal = ResourceAuthorityCommit.Create(scope, 1, effect, new ResourceHeadChange[0], AuthorityCommitReason.MutationEffect, attempt.AttemptId);
                resources.Journal.Fault = point => { if (point == "after_flush") throw new IOException("lost terminal acknowledgement"); };
                RuntimeThrows<IOException>(() => resources.Publish(terminal));
                resources.Journal.Fault = null;
                resources = new ResourceJournal(paths);
                AssertEqual(terminal.CommitId, resources.Resolve(attempt.AttemptId, terminal.CommitId).LinkedAuthorityCommitId, "restart recovers own terminal without replay");
                AssertTrue(resources.Publish(terminal).Duplicate, "lost acknowledgement retry returns original receipt");
                AssertEqual(1, frozen.Commits.Count, "lazy history stays pinned before the terminal");
                AssertTrue(frozen.FindMutationCommit(attempt.AttemptId) == null, "snapshot cannot see future outcomes");
                var abandoned = resources.Prepare(scope, "files.replace", target, exact.Revision, body);
                resources.AbandonBeforeDispatch(abandoned.AttemptId);
                RuntimeThrows<InvalidOperationException>(() => resources.MarkDispatchMayHaveOccurred(abandoned.AttemptId));

                for (var i = 2; i < 1000; i++)
                {
                    resources.Publish(ResourceAuthorityCommit.Create(scope, i, null, new ResourceHeadChange[0], AuthorityCommitReason.MetadataTransition));
                    if (i != 99 && i != 999) continue;
                    var reads = resources.Journal.ReadRecords; var readBytes = resources.Journal.ReadBytes;
                    var captured = resources.Capture(scope);
                    AssertEqual(terminal.CommitId, captured.FindMutationCommit(attempt.AttemptId).CommitId, "indexed receipt ignores unrelated history");
                    AssertTrue(resources.Journal.ReadRecords - reads <= 5, "head capture plus addressed outcome does not read full effect history");
                    Console.WriteLine("RESOURCE_PORT history=" + (i + 1) + " capture+receipt-records=" + (resources.Journal.ReadRecords - reads) +
                        " bytes=" + (resources.Journal.ReadBytes - readBytes));
                }
                var heads = resources.ReadHeads(scope, new[] { new ResourceHeadRange("rna://file/", "rna://file0") }, 0, 1);
                AssertEqual(1, heads.Total, "bounded heads retain total");
                var views = resources.ReadViews(scope, exact, 0, 1);
                resources.RegisterView(scope, new ResourceRevisionView(exact, "raw", body.Sha256, body, ResourceCoverage.Whole()));
                AssertEqual(0, resources.ReadViews(scope, exact, 1, 1, views.SnapshotCount).Items.Count, "pinned view inventory excludes later registration");
                resources.Fault = point => { throw new IOException("projection unavailable"); };
                var indexedLate = ResourceAuthorityCommit.Create(scope, 1000, null, new ResourceHeadChange[0], AuthorityCommitReason.MetadataTransition);
                resources.Publish(indexedLate); resources.Fault = null;
                var canonical = StorageJournal.Inventory(paths).SelectMany(stream => stream.Segments).ToDictionary(file => file, File.ReadAllBytes);
                using (var db = JournalIndex.Open(paths))
                using (var command = db.CreateCommand())
                {
                    command.CommandText = "UPDATE resource_attempt SET operation=(SELECT operation FROM resource_attempt WHERE attempt=$other) WHERE attempt=$attempt";
                    command.Parameters.AddWithValue("$other", abandoned.AttemptId); command.Parameters.AddWithValue("$attempt", attempt.AttemptId);
                    command.ExecuteNonQuery();
                }
                RuntimeThrows<InvalidDataException>(() => resources.Resolve(attempt.AttemptId, terminal.CommitId));
                JournalIndex.Repair(paths, StorageProtector.None, true, CancellationToken.None);
                AssertEqual(terminal.CommitId, resources.Resolve(attempt.AttemptId, terminal.CommitId).LinkedAuthorityCommitId, "rebuild recovers exact attempt after a substituted index locator");
                AssertTrue(canonical.All(pair => pair.Value.SequenceEqual(File.ReadAllBytes(pair.Key))), "index rebuild never rewrites resource truth");
                var backup = Path.Combine(paths.Root, "backup"); JournalCasService(paths, StorageProtector.None).BackupJournals(backup);
                var restored = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "restored"), _ => StorageProtector.None);
                var reopened = new ResourceJournal(restored);
                AssertEqual(1001L, reopened.Capture(scope).Generation, "restore retains authority generation");
                AssertEqual(terminal.CommitId, reopened.Capture(scope).FindMutationCommit(attempt.AttemptId).CommitId, "restore retains exact terminal receipt");
                AssertEqual(body.Sha256, reopened.GetView(scope, exact, "text").Payload.Sha256, "registered views remain CAS roots");
                AssertEqual(0, reopened.Unresolved().Count, "restore never resurrects resolved or abandoned work");
                File.Delete(bodies.PathFor(body.Sha256));
                RuntimeThrows<InvalidDataException>(() => resources.Publish(ResourceAuthorityCommit.Create(scope, 1001, null,
                    new[] { new ResourceHeadChange(target, resources.Head(scope, target), ResourceHeadState.Known(exact, 1002)) }, AuthorityCommitReason.MetadataTransition)));
            });
        }
    }
}
