using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;
using RNAssistant.Runtime;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void JournalWorkspaceOriginalFork()
        {
            WithJournalInput(false, (paths, journal, source, scope) => {
                IConversationStore store = journal; var resources = new ResourceJournal(paths); var blobs = new ChatBlobStore(paths);
                var original = JournalUpload(paths, source, "original.txt", "Original bytes\nSecond line");
                var message = new ChatMessage { Role = "user", Content = "Original question",
                    Attachments = new List<ChatAttachment> { original }, AttachmentAnalysis = new AttachmentAnalysisContext {
                        Content = new string('a', 70000), AttachmentIds = new List<string> { original.Id } } };
                LinkJournalUpload(paths, source, message); store.Commit(source, ConversationCommit.UserInput(message));
                var originalId = "attachment_" + original.Id;
                var later = new ChatMessage { Role = "user", Content = "Use its exact retained original",
                    ResourceRefs = new List<ResourceRef> { ChatResourceUri.CreateArtifactRevision(source, source.Artifacts.Single()) } };
                source.Messages.Add(later); store.Commit(source, ConversationCommit.UserInput(later));
                var outside = new ChatMessage { Role = "user", Content = "Outside prefix", Attachments =
                    new List<ChatAttachment> { JournalUpload(paths, source, "future.txt", "Not selected") } };
                LinkJournalUpload(paths, source, outside); store.Commit(source, ConversationCommit.UserInput(outside));
                source = store.Load(source.Id);

                var forged = PrepareTextFork(store, source, 2);
                ChatCloneService.PrepareForkResources(source, forged, store.LoadArtifactBody,
                    new ResourceForkService(new ResourceAuthorityService(resources, resources, resources, blobs), blobs));
                forged.Artifacts.Add(source.Artifacts.Single(item => item.Id != originalId).CopyMetadata());
                RuntimeThrows<InvalidDataException>(() => store.Commit(forged, new ConversationCommit()));
                AssertTrue(store.Load(forged.Id) == null, "a retained but off-prefix Workspace original cannot be injected");
                forged.Artifacts.RemoveAll(item => item.Id != originalId);
                forged.Messages[0].Attachments[0].FileName = "Rebound.txt";
                RuntimeThrows<InvalidDataException>(() => store.Commit(forged, new ConversationCommit()));
                AssertTrue(store.Load(forged.Id) == null, "fork cannot rebind accepted original metadata");

                var count = store.ListHeaders(false).Count;
                journal.Journal.Fault = point => { if (point == "before_append") throw new IOException("before fork"); };
                RuntimeThrows<IOException>(() => ForkSharedJournal(paths, journal, resources, source, 2));
                journal.Journal.Fault = null;
                AssertEqual(count, store.ListHeaders(false).Count, "failed append creates no partial original branch");
                journal.Fault = point => {
                    if (point == "after_fork_index") { journal.Fault = null; throw new IOException("before original locators"); }
                };
                var bytes = journal.Journal.WrittenBytes;
                var branch = ForkSharedJournal(paths, journal, resources, source, 2); journal.Fault = null;
                AssertTrue(journal.Journal.WrittenBytes - bytes < 16384, "large analysis/body remain in original CAS, outside the bounded fork frame");
                branch = store.Load(branch.Id);
                AssertEqual(1, branch.Artifacts.Count, "original membership follows the exact prefix");
                var reads = journal.ChatBodyReads;
                var page = store.ReadMessageWindow(branch.Id, 2, 1);
                AssertEqual(2L, journal.ChatBodyReads - reads, "later-reference page reads text/manifest, not source analysis or attachment bodies");
                AssertEqual(original.ContentSha256, page.Artifacts.Single().ContentSha256, "page resolves the inherited original header");
                var revision = source.Revision;
                source.Messages[0].Content = "Changed parent question";
                store.Commit(source, ConversationCommit.RewriteHistory(source.Messages.Select(item => item.Id), source.Messages));
                AssertTrue(source.Revision > revision, "parent attachment occurrence now has a later locator");
                var descendant = ForkSharedJournal(paths, journal, resources, branch, 2);
                WorkspaceConversationService.DeleteHistoryMessage(paths, store, branch, message.Id, resources, resources);
                var hidden = ForkSharedJournal(paths, journal, resources, store.Load(branch.Id), 1);
                store.Delete(source.Host, source.DocumentKey, source.Id); store.Delete(branch.Host, branch.DocumentKey, branch.Id);
                journal.Rebuild(); descendant = store.Load(descendant.Id);
                AssertEqual("Original question", descendant.Messages[0].Content, "two generations retain the frozen original occurrence");
                AssertEqual(70000, descendant.Messages[0].AttachmentAnalysis.Content.Length, "analysis survives ancestor deletion/rebuild");
                AssertEqual(message.Id, store.Load(hidden.Id).Artifacts.Single().SourceMessageId, "hidden original metadata retains its accepted source identity");
                var provider = new WorkspaceChatAttachmentResourceProvider(paths, descendant);
                var target = provider.Find("original.txt").Items.Single().Target;
                AssertTrue(provider.Read(target, 100) != null, "actual Workspace reader uses the inherited child binding");
                var backup = Path.Combine(paths.Root, "original-fork-backup"); JournalCasService(paths, StorageProtector.None).BackupJournals(backup);
                var restored = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "original-fork-restore"), _ => StorageProtector.None);
                var restoredStore = (IConversationStore)new ConversationJournal(restored, scope);
                var copy = restoredStore.Load(descendant.Id);
                AssertTrue(new WorkspaceChatAttachmentResourceProvider(restored, copy).Read(target, 100) != null, "restored native resource consumer");
                AssertEqual("Original bytes\nSecond line", new AttachmentStore(restored).ReadExtractedText(copy.Messages[0].Attachments.Single()),
                    "restore preserves original and extraction CAS closure");
                AssertEqual(original.ContentSha256, restoredStore.ReadMessageWindow(hidden.Id, 1, 1).Artifacts.Single().ContentSha256,
                    "restore retains this branch's hidden-source original through its inherited locator");
            });
        }
    }
}
