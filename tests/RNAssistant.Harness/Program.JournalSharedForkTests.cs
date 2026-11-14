using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Runtime;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static ChatSession ForkSharedJournal(AppDataPaths paths, ConversationJournal journal, ResourceJournal resources,
            ChatSession source, int count)
        {
            IConversationStore store = journal;
            if (source.WorkspaceId != null)
                return WorkspaceConversationService.ForkHistorySession(paths, store, new WorkspaceStore(paths).OpenConnected(source.WorkspaceId),
                    source, source.Messages[count - 1].Id, new AttachmentStore(paths), resources, resources);
            var child = PrepareTextFork(store, source, count);
            var executor = new OfficeToolExecutor(FakeOfficeAdapter.ForHost(source.Host), new VbaJournalStore(paths),
                new SkillStore(paths), new ToolStore(paths), () => new AppSettings(), paths: paths,
                resourceAuthorityStore: resources, resourceMutationJournal: resources, persistResourceFacts: store.Commit);
            var copies = ChatCloneService.PrepareForkResources(source, child, store.LoadArtifactBody,
                new ResourceForkService(executor.ResourceAuthority, executor.Payloads));
            executor.MutateChatResources(child, new ChatResourceMutationIntent(ChatResourceMutationKind.Fork,
                child.ForkedThroughMessageId, count - 1, source: source, fork: copies), () => child);
            return child;
        }

        private static void JournalSharedForkConsumers()
        {
            foreach (var office in new[] { true, false }) WithJournalInput(office, (paths, journal, source, scope) => {
                IConversationStore store = journal; var resources = new ResourceJournal(paths); var blobs = new ChatBlobStore(paths);
                var owner = new DocumentArtifactStore(resources, resources, blobs);
                var beforeResources = new ChatMessage { Role = "user", Content = "Before shared resources" };
                source.Messages.Add(beforeResources); store.Commit(source, ConversationCommit.UserInput(beforeResources));
                var first = PublishJournalPlan(source, journal, resources, owner, PlanDocumentToolCatalog.SaveToolId,
                    JournalPlanCommand(source, owner), JournalPlanContext());
                var sourceRef = ChatResourceUri.CreateArtifactRevision(source, first);
                var message = new ChatMessage { Role = "user", Content = "Exact shared source",
                    ResourceRefs = new List<ResourceRef> { sourceRef } };
                if (office)
                {
                    message.Attachments.Add(JournalUpload(paths, source, "original.txt", "Immutable original"));
                    message.AttachmentAnalysis = new AttachmentAnalysisContext { Content = "Retained analysis",
                        AttachmentIds = new List<string> { message.Attachments[0].Id } };
                    LinkJournalUpload(paths, source, message);
                }
                else source.Messages.Add(message);
                store.Commit(source, ConversationCommit.UserInput(message));
                var selected = PublishJournalPlan(source, journal, resources, owner, PlanDocumentToolCatalog.SaveToolId,
                    JournalPlanCommand(source, owner, "# Selected"), JournalPlanContext());
                if (office)
                {
                    ArtifactWorkingSet.Set(source, source.Artifacts.Single(item => item.OriginalAttachment != null), true);
                    store.Commit(source, new ConversationCommit());
                }
                source = store.Load(source.Id);
                var identity = ArtifactWorkingSet.Identity(source, selected); var authority = DocumentArtifactStore.PlanScope(source);
                var head = resources.GetHead(authority, identity).Revision;
                var prefix = ForkSharedJournal(paths, journal, resources, source, 1);
                AssertEqual(selected.Id, prefix.ActivePlanDocumentArtifactId, "shared selection is independent of the dialogue prefix");
                AssertEqual(source.Artifacts.Count, prefix.Artifacts.Count, "fork retains the shared working set, including off-prefix originals");
                AssertTrue(prefix.Artifacts.All(item => item.InlineText == null), "fork copies shared metadata without body hydration");
                AssertTrue(!object.ReferenceEquals(source.ArtifactLinks[0], prefix.ArtifactLinks[0]), "working-set decisions are detached projections");
                var full = ForkSharedJournal(paths, journal, resources, source, source.Messages.Count);
                AssertEqual(head.Uri, resources.GetHead(authority, identity).Revision.Uri, "fork never republishes or rewinds the shared Plan head");
                journal.Rebuild(); full = store.Load(full.Id);
                var reads = journal.ChatBodyReads; var page = store.ReadMessageWindow(full.Id, 2, 1);
                AssertEqual(office ? 3L : 2L, journal.ChatBodyReads - reads, "page reads text/manifest and optional analysis, never Plan or original bodies");
                AssertEqual(sourceRef.Uri, page.PageMessages.Single().ResourceRefs[0].Uri, "inherited message keeps the original exact revision");
                AssertEqual(selected.Id, page.ActivePlanDocumentArtifactId, "page keeps current chat selection distinct from historical message refs");
                if (office)
                {
                    var original = source.Artifacts.Single(item => item.OriginalAttachment != null);
                    AssertTrue(page.DetachedArtifactIds.Contains(original.Id), "fork retains explicit original detachment");
                    AssertEqual(message.Attachments[0].ContentSha256, full.Messages[1].Attachments[0].ContentSha256, "same original CAS bytes");
                    AssertEqual("Retained analysis", full.Messages[1].AttachmentAnalysis.Content, "analysis stays in original immutable occurrence");
                    AssertEqual(source.Id, full.Artifacts.Single(item => item.Id == original.Id).SourceChatId, "artifact provenance remains with its publisher");
                }
                PublishJournalPlan(source, journal, resources, owner, PlanDocumentToolCatalog.SaveToolId,
                    JournalPlanCommand(source, owner, "# Later shared head"), JournalPlanContext());
                AssertEqual("# Selected", owner.ReadPlanSelection(full, selected.Id).InlineText, "later publication cannot change a fork's exact selected bytes");
                RuntimeThrows<ToolMutationPreparationException>(() => PlanDocumentPublication.Prepare(full, JournalPlanContext(), owner,
                    PlanDocumentToolCatalog.SaveToolId, JournalPlanCommand(full, owner, "Stale edit")));
                var tombstone = PublishJournalPlan(source, journal, resources, owner, PlanDocumentToolCatalog.DeleteToolId,
                    JournalPlanCommand(source, owner), JournalPlanContext());
                var removed = ForkSharedJournal(paths, journal, resources, store.Load(source.Id), 2);
                AssertTrue(removed.ActivePlanDocumentArtifactId == null, "fork cannot resurrect a removed shared Plan");
                AssertTrue(store.ReadMessageWindow(removed.Id, 2, 1).ApplicableTombstoneIds.Contains(tombstone.Id), "off-prefix tombstone follows the shared working set");

                var pending = PrepareTextFork(store, store.Load(source.Id), 2);
                ArtifactWorkingSet.CopySharedForFork(store.Load(source.Id), pending);
                var command = new ConversationCommit();
                journal.Journal.Fault = point => { if (point == "after_flush") throw new IOException("fork acknowledgement lost"); };
                RuntimeThrows<IOException>(() => store.Commit(pending, command)); journal.Journal.Fault = null;
                var acceptedRevision = pending.Revision;
                store.Delete(source.Host, source.DocumentKey, source.Id); store.Commit(pending, command);
                AssertEqual(acceptedRevision, pending.Revision, "resource fork retry survives source deletion without another commit");
                journal.Rebuild();
                AssertEqual(sourceRef.Uri, store.Load(full.Id).Messages[1].ResourceRefs[0].Uri, "source deletion/rebuild retains inherited shared references");
                if (!office)
                {
                    var backup = Path.Combine(paths.Root, "shared-fork-backup"); JournalCasService(paths, StorageProtector.None).BackupJournals(backup);
                    var restored = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "shared-fork-restore"), _ => StorageProtector.None);
                    var restoredStore = (IConversationStore)new ConversationJournal(restored, scope);
                    var restoredResources = new ResourceJournal(restored);
                    AssertEqual("# Selected", new DocumentArtifactStore(restoredResources, restoredResources, new ChatBlobStore(restored))
                        .ReadPlanSelection(restoredStore.Load(full.Id), selected.Id).InlineText, "shared bytes and fork refs survive journal/CAS restore");
                }
            });
        }

        private static void VerifyJournalSharedForkRuntime(AppDataPaths paths, ConversationJournal journal, ResourceJournal resources,
            ChatSession source, string selectedId)
        {
            IConversationStore store = journal;
            var selected = source.Artifacts.Single(item => item.Id == selectedId);
            var identity = ArtifactWorkingSet.Identity(source, selected);
            var scope = source.WorkspaceId == null ? ResourceAuthorityScopeId.Document(new DocumentAuthorityId(source.DocumentAuthorityId)) :
                new ResourceAuthorityScopeId("workspace", source.WorkspaceId);
            var head = resources.GetHead(scope, identity);
            var child = ForkSharedJournal(paths, journal, resources, source, source.Messages.Count);
            var loaded = store.Load(child.Id);
            AssertEqual(source.Messages.Count, loaded.Messages.Count, "real kernel resource history is inherited exactly");
            AssertEqual(0, journal.ReadWindow(child.Id, long.MaxValue, 64).Count, "fork is not new tool execution evidence");
            AssertEqual(head.AuthorityGeneration, resources.GetHead(scope, identity).AuthorityGeneration, "shared head is not republished during fork");
            if (selected.Kind == ChatArtifactKinds.HtmlWorkspace)
                AssertEqual(source.HtmlWorkspace.Files.Single().Content, loaded.HtmlWorkspace.Files.Single().Content, "exact selected HTML restores through its existing owner");
            else AssertEqual(selectedId, loaded.ActivePlanDocumentArtifactId, "real Plan consumer keeps the source selection");
        }
    }
}
