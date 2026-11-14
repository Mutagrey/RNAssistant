using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Runtime;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void JournalCheckpointForkConsumers()
        {
            foreach (var office in new[] { true, false }) WithJournalInput(office, (paths, journal, source, scope) => {
                IConversationStore store = journal; var resources = new ResourceJournal(paths); var blobs = new ChatBlobStore(paths);
                var settings = new AppSettings { Model = "checkpoint-fork", AutoCompressContext = false, ContextWindowOverrideTokens = 65536 };
                var executor = office ? new OfficeToolExecutor(FakeOfficeAdapter.ForHost(source.Host), new VbaJournalStore(paths),
                    new SkillStore(paths), new ToolStore(paths), () => settings, paths: paths,
                    resourceAuthorityStore: resources, resourceMutationJournal: resources, persistResourceFacts: store.Commit) : null;
                for (var pass = 1; pass <= 2; pass++)
                {
                    AppendJournalContextHistory(store, source); var before = source.Messages.Count;
                    CompactJournalContext(paths, resources, source, settings, "Frozen summary " + pass, executor);
                    store.Commit(source, new ConversationCommit(source.Messages.Skip(before)));
                    source = store.Load(source.Id);
                }
                var selected = source.Artifacts.Single(item => item.Id == source.ActiveContextCheckpointId);
                var older = source.Artifacts.Single(item => item.Kind == ChatArtifactKinds.Compaction && item.Id != selected.Id);
                var originalBody = JsonConvert.SerializeObject(ContextCompactionRuntime.ActiveCheckpoint(source));
                var prefixCount = source.Messages.FindIndex(item => item.Id == older.SourceMessageId) + 1;
                var prefix = ForkSharedJournal(paths, journal, resources, source, prefixCount);
                AssertTrue(prefix.ActiveContextCheckpointId == null && prefix.ContextCheckpoints.Count == 0,
                    "a prefix outside the current selection cannot silently choose an older summary");
                AssertTrue(prefix.Artifacts.Any(item => item.Id == older.Id) && prefix.Artifacts.All(item => item.Id != selected.Id),
                    "the prefix retains historical checkpoint metadata without later local snapshots");

                var oldPath = blobs.PathFor(older.ContentSha256); var oldBytes = File.ReadAllBytes(oldPath);
                ChatSession child;
                try
                {
                    File.WriteAllText(oldPath, "unavailable older summary");
                    child = ForkSharedJournal(paths, journal, resources, source, source.Messages.Count);
                    AssertEqual(originalBody, JsonConvert.SerializeObject(ContextCompactionRuntime.ActiveCheckpoint(child)),
                        "selected claims and shared publication provenance stay exact; older bodies remain cold");
                }
                finally { File.WriteAllBytes(oldPath, oldBytes); }
                var selectedPath = blobs.PathFor(selected.ContentSha256); var selectedBytes = File.ReadAllBytes(selectedPath);
                try
                {
                    File.WriteAllText(selectedPath, "corrupt selected summary"); var before = journal.Journal.WrittenBytes;
                    RuntimeThrows<InvalidOperationException>(() => ForkSharedJournal(paths, journal, resources, source, source.Messages.Count));
                    AssertEqual(before, journal.Journal.WrittenBytes, "missing selected bytes cannot publish a fork or fall back");
                }
                finally { File.WriteAllBytes(selectedPath, selectedBytes); }
                journal.Rebuild(); child = store.Load(child.Id);
                AssertEqual(originalBody, JsonConvert.SerializeObject(ContextCompactionRuntime.ActiveCheckpoint(child)), "rebuild preserves immutable source claims");
                var reads = journal.ChatBodyReads;
                var page = store.ReadMessageWindow(child.Id, child.Messages.Count, 1);
                AssertEqual(2L, journal.ChatBodyReads - reads, "a page loads text and membership only, not checkpoint bodies");
                AssertTrue(page.Artifacts.Any(item => item.Id == selected.Id) && page.Artifacts.All(item => item.InlineText == null), "rebased links address cold local metadata");
                AssertEqual(ChatResourceUri.CreateArtifactRevision(child, selected).Uri, page.PageMessages.Single().ResourceRefs[0].Uri,
                    "presentation links follow the child without rewriting the canonical source message");
                AssertEqual(ChatResourceUri.CreateArtifactRevision(source, selected).Uri, store.Load(source.Id).Messages.Last().ResourceRefs[0].Uri,
                    "source presentation remains unchanged");
                var incomplete = PrepareTextFork(store, source, source.Messages.Count);
                ChatCloneService.PrepareForkResources(source, incomplete, store.LoadArtifactBody,
                    new ResourceForkService(new ResourceAuthorityService(resources, resources, resources, blobs), blobs));
                incomplete.Artifacts.RemoveAll(item => item.Id == older.Id);
                RuntimeThrows<InvalidDataException>(() => store.Commit(incomplete, new ConversationCommit()));
                AssertTrue(store.Load(incomplete.Id) == null, "an incomplete inherited checkpoint chain cannot create a child");

                AppendJournalContextHistory(store, child); var count = child.Messages.Count;
                CompactJournalContext(paths, resources, child, settings, "Child summary");
                store.Commit(child, new ConversationCommit(child.Messages.Skip(count))); child = store.Load(child.Id);
                AssertEqual(selected.Id, child.Artifacts.Single(item => item.Id == child.ActiveContextCheckpointId).ParentArtifactId,
                    "a child can append a new checkpoint to the retained local lineage");
                AssertEqual(originalBody, JsonConvert.SerializeObject(ContextCompactionRuntime.ActiveCheckpoint(store.Load(source.Id))), "child compaction does not alter its parent");
                var descendant = ForkSharedJournal(paths, journal, resources, child, child.Messages.Count);
                var canonicalSnapshots = 0; var forkFrames = 0;
                journal.Journal.Scan((record, position) => {
                    if (record.Kind != "chat.committed") return;
                    var fact = record.Data.ToObject<ConversationChatCommit>();
                    if (fact.Card.ChatId != descendant.Id) return;
                    forkFrames++; canonicalSnapshots += fact.Snapshots?.Count ?? 0;
                }, System.Threading.CancellationToken.None);
                AssertEqual(1, forkFrames, "one canonical fork frame");
                AssertEqual(0, canonicalSnapshots, "inherited snapshot metadata uses original locators, not another durable copy");

                var pending = PrepareTextFork(store, child, child.Messages.Count);
                ChatCloneService.PrepareForkResources(child, pending, store.LoadArtifactBody,
                    new ResourceForkService(new ResourceAuthorityService(resources, resources, resources, blobs), blobs));
                var command = new ConversationCommit();
                journal.Journal.Fault = point => { if (point == "after_flush") throw new IOException("checkpoint fork acknowledgement lost"); };
                RuntimeThrows<IOException>(() => store.Commit(pending, command)); journal.Journal.Fault = null;
                var accepted = pending.Revision;
                store.Delete(source.Host, source.DocumentKey, source.Id); store.Delete(child.Host, child.DocumentKey, child.Id);
                store.Commit(pending, command); AssertEqual(accepted, pending.Revision, "same-command retry keeps inherited snapshots after source deletion");
                journal.Rebuild(); descendant = store.Load(descendant.Id);
                AssertEqual("Child summary", ContextCompactionRuntime.ActiveCheckpoint(descendant).SummaryMarkdown, "two deleted ancestors do not remove selected bytes");
                WorkspaceConversationService.DeleteHistoryMessage(store, descendant, descendant.Messages[0].Id);
                var edited = ForkSharedJournal(paths, journal, resources, store.Load(descendant.Id), descendant.Messages.Count);
                AssertTrue(edited.ActiveContextCheckpointId == null, "fork cannot resurrect a summary invalidated by an earlier edit");
                if (!office)
                {
                    var backup = Path.Combine(paths.Root, "checkpoint-fork-backup"); JournalCasService(paths, StorageProtector.None).BackupJournals(backup);
                    var restored = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "checkpoint-fork-restore"), _ => StorageProtector.None);
                    var restoredStore = (IConversationStore)new ConversationJournal(restored, scope);
                    AssertEqual("Child summary", ContextCompactionRuntime.ActiveCheckpoint(restoredStore.Load(pending.Id)).SummaryMarkdown,
                        "journal/CAS restore retains inherited bodies and snapshot locators");
                }
            });
        }
    }
}
