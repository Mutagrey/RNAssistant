using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Runtime;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static ToolExecutionContext JournalPlanContext()
        {
            var id = Guid.NewGuid().ToString("N");
            return new ToolExecutionContext(new ToolCall(id, "core.write", "{}"),
                new ToolPolicySnapshot("core.write", "fixture", true), id, id, id, DateTime.UtcNow, true, 1);
        }

        private static PlanResourceCommand JournalPlanCommand(ChatSession chat, DocumentArtifactStore owner,
            string markdown = "# First", int? restoreVersion = null)
        {
            var current = chat.ActivePlanDocumentArtifactId == null ? null : owner.ReadPlanSelection(chat, chat.ActivePlanDocumentArtifactId, false);
            return new PlanResourceCommand { Source = current == null ? null : ChatResourceUri.CreateArtifactRevision(chat, current),
                PlanId = current == null ? null : PlanDocumentService.PlanId(current), ArtifactId = current?.Id,
                Content = new PlanSourceDocument { Title = "Retained Plan", Markdown = markdown, Status = "draft" },
                RestoreVersion = restoreVersion ?? 0 };
        }

        private static ChatArtifact PublishJournalPlan(ChatSession chat, ConversationJournal journal, ResourceJournal resources,
            DocumentArtifactStore owner, string operation, PlanResourceCommand command, ToolExecutionContext context)
        {
            var scope = DocumentArtifactStore.PlanScope(chat);
            var impacts = PlanDocumentPublication.Prepare(chat, context, owner, operation, command);
            var attempt = resources.Prepare(scope, operation, impacts[0].Identity,
                resources.GetHead(scope, impacts[0].Identity)?.Revision?.Revision, intendedImpacts: impacts);
            attempt = resources.MarkDispatchMayHaveOccurred(attempt.AttemptId);
            var key = PlanDocumentService.CreationId(chat, context);
            var result = new PlanDocumentService().ApplyResource(chat, operation, command, () => { }, key);
            AssertTrue(result.Success, "shared Plan domain mutation succeeds");
            var readBack = PlanDocumentPublication.Retain(chat, attempt, key, owner);
            var record = new ToolExecutionRecord(context, ToolExecutionOutcome.Ok, DateTime.UtcNow,
                evidence: new ToolExecutionEvidence(ToolDispatchEvidence.MayHaveDispatched, ToolEffectEvidence.VerifiedChange));
            PlanDocumentPublication.Publish(resources, resources, resources, attempt, record, readBack);
            ArtifactWorkingSet.Set(chat, result.Artifact, false);
            ((IConversationStore)journal).Commit(chat, new ConversationCommit());
            return result.Artifact;
        }

        private static void JournalPlanLifecycle()
        {
            foreach (var office in new[] { true, false }) WithJournalInput(office, (paths, journal, chat, scope) => {
                IConversationStore store = journal;
                var resources = new ResourceJournal(paths);
                var owner = new DocumentArtifactStore(resources, resources, new ChatBlobStore(paths));
                journal.Fault = point => { if (point == "before_index") throw new IOException("index unavailable"); };
                var first = PublishJournalPlan(chat, journal, resources, owner, PlanDocumentToolCatalog.SaveToolId,
                    JournalPlanCommand(chat, owner), JournalPlanContext()); journal.Fault = null;
                journal.Rebuild(); chat = store.Load(chat.Id);
                AssertEqual(first.Id, chat.ActivePlanDocumentArtifactId, "selection survives a failed index apply without an origin message");
                AssertTrue(chat.Artifacts.Single().InlineText == null, "load hydrates reference metadata, not Markdown");
                var source = new ChatMessage { Role = "user", Content = "Keep this Plan", ResourceRefs = new List<ResourceRef> {
                    ChatResourceUri.CreateArtifactRevision(chat, first) } };
                chat.Messages.Add(source); store.Commit(chat, ConversationCommit.UserInput(source));
                var reads = journal.ChatBodyReads; var page = store.ReadMessageWindow(chat.Id, 1, 1);
                AssertEqual(2L, journal.ChatBodyReads - reads, "page reads one selected message and reference manifest");
                AssertEqual(first.Id, page.ActivePlanDocumentArtifactId, "page preserves exact selected version");
                AssertTrue(page.Artifacts.Single().InlineText == null, "page omits Plan bodies");
                var edit = JournalPlanCommand(chat, owner, "# Second"); var context = JournalPlanContext();
                journal.Journal.Fault = point => { if (point == "after_flush") throw new IOException("chat acknowledgement lost"); };
                RuntimeThrows<IOException>(() => PublishJournalPlan(chat, journal, resources, owner, PlanDocumentToolCatalog.SaveToolId, edit, context));
                journal.Journal.Fault = null; chat = store.Load(chat.Id);
                AssertEqual(2, owner.PlanHistory(chat, PlanDocumentService.PlanId(first), false).Count, "publication survives chat acknowledgement loss");
                RuntimeThrows<ToolMutationPreparationException>(() => PlanDocumentPublication.Prepare(chat, context, owner, PlanDocumentToolCatalog.SaveToolId, edit));
                var restored = PublishJournalPlan(chat, journal, resources, owner, PlanDocumentToolCatalog.RestoreToolId,
                    JournalPlanCommand(chat, owner, restoreVersion: 1), JournalPlanContext());
                AssertEqual(3, restored.Revision, "restore appends a revision");
                AssertEqual("# First", owner.ReadPlanSelection(chat, restored.Id).InlineText, "restore retains exact source bytes");
                var restoredRef = ChatResourceUri.CreateArtifactRevision(chat, restored);
                AssertEqual(ChatResourceUri.CreateArtifactRevision(chat, first).Uri,
                    resources.GetRevision(DocumentArtifactStore.PlanScope(chat), restoredRef).RestoredFrom.Uri, "restore provenance is canonical");
                var tombstone = PublishJournalPlan(chat, journal, resources, owner, PlanDocumentToolCatalog.DeleteToolId,
                    JournalPlanCommand(chat, owner), JournalPlanContext());
                var removed = new ChatMessage { Role = "user", Content = "Removed", ResourceRefs = new List<ResourceRef> { ChatResourceUri.CreateArtifactRevision(chat, tombstone) } };
                chat.Messages.Add(removed); store.Commit(chat, ConversationCommit.UserInput(removed));
                journal.Rebuild(); chat = store.Load(chat.Id);
                AssertTrue(chat.ActivePlanDocumentArtifactId == null, "removal clears only chat selection");
                AssertTrue(store.ReadMessageWindow(chat.Id, 2, 1).ApplicableTombstoneIds.Contains(tombstone.Id), "page preserves document/workspace tombstone semantics");
                var oldPage = store.ReadMessageWindow(chat.Id, 1, 1);
                AssertTrue(oldPage.Artifacts.Any(item => item.Id == first.Id) && oldPage.ApplicableTombstoneIds.Contains(tombstone.Id),
                    "an off-page tombstone prevents an old message card from presenting the Plan as live");
                var previous = chat.Messages.Select(item => item.Id).ToArray(); chat.Messages.Clear(); chat.Artifacts.Clear(); chat.ArtifactLinks.Clear();
                store.Commit(chat, ConversationCommit.RewriteHistory(previous, chat.Messages));
                AssertEqual(4, owner.PlanHistory(chat, PlanDocumentService.PlanId(first), false).Count, "clearing visible chat does not remove shared Plan lineage");
                AssertTrue(PlanDocumentService.IsTombstone(owner.ReadPlanSelection(chat, tombstone.Id)), "clear cannot resurrect a removed Plan");
                if (!office)
                {
                    var backup = Path.Combine(paths.Root, "plan-backup"); JournalCasService(paths, StorageProtector.None).BackupJournals(backup);
                    var paths2 = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "plan-restore"), _ => StorageProtector.None);
                    var resource2 = new ResourceJournal(paths2); var owner2 = new DocumentArtifactStore(resource2, resource2, new ChatBlobStore(paths2));
                    var chat2 = ((IConversationStore)new ConversationJournal(paths2, scope)).Load(chat.Id);
                    AssertEqual("# First", owner2.ReadPlanSelection(chat2, first.Id).InlineText, "restore retains bytes after all chat links were cleared");
                    AssertTrue(PlanDocumentService.IsTombstone(owner2.ReadPlanSelection(chat2, tombstone.Id)), "restore preserves removal");
                }
            });
        }

        private static void JournalPlanUnknownRecovery()
        {
            WithJournalInput(false, (paths, journal, chat, scope) => {
                IConversationStore store = journal; var resources = new ResourceJournal(paths); var blobs = new ChatBlobStore(paths);
                var owner = new DocumentArtifactStore(resources, resources, blobs);
                var first = PublishJournalPlan(chat, journal, resources, owner, PlanDocumentToolCatalog.SaveToolId,
                    JournalPlanCommand(chat, owner), JournalPlanContext());
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "project"));
                var consumer = new WorkspacePlanService(paths, store, blobs, workspace, chat, owner, resources, resources);
                var context = JournalPlanContext();
                var prepared = JsonConvert.SerializeObject(new PlanResourcePrepared { Operation = PlanDocumentToolCatalog.SaveToolId,
                    Command = JournalPlanCommand(chat, owner, "uncertain") });
                var attempt = consumer.Prepare(context, new Dictionary<string, object>(), prepared);
                consumer.MarkDispatchMayHaveOccurred(attempt); consumer.ReleaseUnresolved(attempt);
                journal.Rebuild(); chat = store.Load(chat.Id);
                consumer = new WorkspacePlanService(paths, store, blobs, workspace, chat, owner, resources, resources);
                consumer.ReconcileInterrupted();
                var head = resources.GetHead(DocumentArtifactStore.PlanScope(chat), DocumentArtifactStore.PlanIdentity(chat, PlanDocumentService.PlanId(first)));
                AssertEqual(HeadKnowledge.Unknown, head.Knowledge, "interrupted Plan dispatch is unknown, never replayed");
                AssertEqual(0, resources.Unresolved().Count, "the existing recovery owner records its terminal");
                RuntimeThrows<ToolMutationPreparationException>(() => consumer.Prepare(JournalPlanContext(), new Dictionary<string, object>(), prepared));
                AssertEqual("# First", owner.ReadPlanSelection(chat, first.Id).InlineText, "uncertainty preserves the exact previous snapshot");
                var independent = new ChatMessage { Role = "user", Content = "Continue independent work" };
                chat.Messages.Add(independent); store.Commit(chat, ConversationCommit.UserInput(independent));
                AssertEqual(independent.Id, store.Load(chat.Id).Messages.Last().Id, "unknown Plan does not block independent chat input");
            });
        }
    }
}
