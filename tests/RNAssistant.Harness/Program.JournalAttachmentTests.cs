using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using RNAssistant.Application.Conversations;
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
        private static ChatAttachment JournalUpload(AppDataPaths paths, ChatSession chat, string name, string text)
        {
            var attachments = new AttachmentStore(paths);
            var upload = attachments.Import(name, "text/plain", Encoding.UTF8.GetBytes(text), chat.Id);
            attachments.CommitToCas(new ChatMessage { Attachments = new List<ChatAttachment> { upload } });
            return upload;
        }

        private static void LinkJournalUpload(AppDataPaths paths, ChatSession chat, ChatMessage message)
        {
            chat.Messages.Add(message);
            if (chat.DocumentAuthorityId == null) ChatMessageAttachmentService.Link(chat, message);
            else
            {
                var resources = new ResourceJournal(paths);
                new ChatResourceIngestionService(new AttachmentStore(paths),
                    new DocumentArtifactStore(resources, resources, new ChatBlobStore(paths)))
                    .CommitAndLink(chat, message, chat.Messages.Count - 1);
            }
        }

        private static void JournalAttachmentConsumers()
        {
            foreach (var office in new[] { true, false }) WithJournalInput(office, (paths, journal, chat, scope) => {
                IConversationStore store = journal;
                var upload = JournalUpload(paths, chat, "report.txt", "exact original\nsecond line");
                var message = new ChatMessage { Role = "user", Content = "Read the attachment",
                    Attachments = new List<ChatAttachment> { upload },
                    AttachmentAnalysis = new AttachmentAnalysisContext { Content = new string('a', 70000), AttachmentIds = new List<string> { upload.Id } } };
                LinkJournalUpload(paths, chat, message);
                var command = ConversationCommit.UserInput(message);
                journal.Journal.Fault = point => { if (point == "after_flush") throw new IOException("lost acknowledgement"); };
                RuntimeThrows<IOException>(() => store.Commit(chat, command)); journal.Journal.Fault = null;
                store.Commit(chat, command);
                var revision = chat.Revision; store.Commit(chat, command);
                AssertEqual(revision, chat.Revision, "retry does not duplicate an attachment message");
                AssertTrue(upload.ExtractedText != null, "normalization does not mutate the caller's preview");
                var linked = chat.Artifacts.Single();
                var reference = ChatResourceUri.CreateArtifactRevision(chat, linked);
                var later = new ChatMessage { Role = "user", Content = "Use the same original", ResourceRefs = new List<ResourceRef> { reference } };
                chat.Messages.Add(later);
                journal.Fault = point => { if (point == "before_index") throw new IOException("projection unavailable"); };
                store.Commit(chat, ConversationCommit.UserInput(later)); journal.Fault = null;
                if (office) { ArtifactWorkingSet.Set(chat, linked, true); store.Commit(chat, new ConversationCommit()); }
                if (office) store.Move(chat, chat.Host, chat.DocumentKey + "-moved", "Moved document");
                journal.Rebuild(); chat = store.Load(chat.Id);
                AssertEqual(2, chat.Messages.Count, "rebuild preserves message membership");
                AssertEqual(70000, chat.Messages[0].AttachmentAnalysis.Content.Length, "analysis is hydrated from CAS");
                AssertTrue(chat.Messages[0].Attachments.Single().ExtractedText == null, "header has no extracted body copy");
                var reads = journal.ChatBodyReads;
                var page = store.ReadMessageWindow(chat.Id, 2, 1);
                AssertEqual(2L, journal.ChatBodyReads - reads, "page reads one message and reference-only working set, not old analysis or file bodies");
                AssertEqual(upload.ContentSha256, page.Artifacts.Single().ContentSha256, "later reference addresses the original without loading its source message body");
                AssertEqual(office, page.DetachedArtifactIds.Contains(linked.Id), "attachment membership survives restart");
                reads = journal.ChatBodyReads; store.ListHeaders();
                AssertEqual(reads, journal.ChatBodyReads, "catalog does not hydrate attachment bodies or working set");
                if (office)
                {
                    var resources = new ResourceJournal(paths);
                    var original = new DocumentArtifactStore(resources, resources, new ChatBlobStore(paths)).Read(ConversationMetadata.From(chat), reference);
                    AssertEqual(upload.ContentSha256, original.OriginalAttachment.ContentSha256, "Office retains existing document authority");
                    new ChatHistoryEditService(_ => { }, (session, reason) => { }).Clear(chat, chat.Context);
                    store.Commit(chat, ConversationCommit.RewriteHistory(new[] { message.Id, later.Id }, chat.Messages));
                    AssertEqual(upload.Id, new DocumentArtifactStore(resources, resources, new ChatBlobStore(paths))
                        .Read(ConversationMetadata.From(chat), reference).OriginalAttachment.Id, "clearing chat does not delete the document original");
                }
                else
                {
                    var provider = new WorkspaceChatAttachmentResourceProvider(paths, chat);
                    var target = provider.Find("report.txt").Items.Single().Target;
                    var observation = provider.Read(target, 100);
                    AssertTrue(observation != null, "real Workspace provider reads its restored attachment");
                    var backup = Path.Combine(paths.Root, "attachment-backup");
                    JournalCasService(paths, StorageProtector.None).BackupJournals(backup);
                    var restored = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "attachment-restore"), _ => StorageProtector.None);
                    var copy = ((IConversationStore)new ConversationJournal(restored, scope)).Load(chat.Id);
                    AssertEqual("exact original\nsecond line", new AttachmentStore(restored).ReadExtractedText(copy.Messages[0].Attachments.Single()),
                        "clean restore includes original and extraction CAS closure");
                    AssertTrue(new WorkspaceChatAttachmentResourceProvider(restored, copy).Read(target, 100) != null, "restored real resource consumer");
                }
            });
        }

        private static void JournalAttachmentInputProof()
        {
            foreach (var office in new[] { true, false }) WithJournalInput(office, (paths, journal, chat, scope) => {
                IConversationStore store = journal;
                var input = InboxInput("with-upload", "accepted input", InputDelivery.Queue);
                input.Attachments.Add(JournalUpload(paths, chat, "queued.txt", "queued original"));
                var inbox = new ConversationInboxService(journal);
                journal.Journal.Fault = point => { if (point == "after_flush") throw new IOException("lost input receipt"); };
                RuntimeThrows<IOException>(() => inbox.Submit(chat, input)); journal.Journal.Fault = null;
                journal.Rebuild(); chat = store.Load(chat.Id); inbox = new ConversationInboxService(journal);
                var accepted = inbox.ReadAccepted(chat, input.OperationId).Current;
                AssertEqual(input.Attachments.Single().ContentSha256, accepted.Attachments.Single().ContentSha256, "queued-only upload survives replay");
                var altered = JsonConvert.DeserializeObject<ConversationInput>(JsonConvert.SerializeObject(accepted));
                altered.Revision++; altered.Attachments.Clear();
                var revision = chat.Revision;
                RuntimeThrows<InvalidOperationException>(() => new ConversationInputJournal(journal).Append(chat, new ConversationInputEvent { Input = altered }));
                AssertEqual(revision, store.Load(chat.Id).Revision, "input edit cannot silently discard accepted files");
                if (!office)
                {
                    var backup = Path.Combine(paths.Root, "queue-backup");
                    JournalCasService(paths, StorageProtector.None).BackupJournals(backup);
                    var restored = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "queue-restore"), _ => StorageProtector.None);
                    var copy = new ConversationJournal(restored, scope);
                    var receipt = new ConversationInboxService(copy).ReadAccepted(((IConversationStore)copy).Load(chat.Id), input.OperationId).Current;
                    AssertEqual("queued original", new AttachmentStore(restored).ReadExtractedText(receipt.Attachments.Single()), "pending inputs root their files before any message exists");
                }
                inbox.Resume(chat); accepted = inbox.TakeNext(chat);
                var message = new ChatMessage { Id = accepted.Id, Role = "user", Content = accepted.Text,
                    RunId = accepted.AcceptedRunId, Attachments = accepted.Attachments };
                LinkJournalUpload(paths, chat, message); store.Commit(chat, ConversationCommit.UserInput(message));
                inbox.Applied(chat, accepted.Id); journal.Rebuild(); chat = store.Load(chat.Id);
                AssertEqual(ConversationInputStatus.Applied, new ConversationInboxService(journal).ReadAccepted(chat, input.OperationId).Current.Status,
                    "delivery proves exact files as well as text");
            });
            WithJournalInput(false, (paths, journal, chat, scope) => {
                var inbox = new ConversationInboxService(journal);
                var input = InboxInput("missing-upload", "same text", InputDelivery.Queue);
                input.Attachments.Add(JournalUpload(paths, chat, "proof.txt", "required"));
                inbox.Submit(chat, input); input = inbox.TakeNext(chat);
                PersistJournalInput(journal, chat, input); // Deliberately omits the accepted files.
                RuntimeThrows<InvalidDataException>(() => inbox.Applied(chat, input.Id));
                AssertEqual(ConversationInputStatus.Delivering, inbox.ReadAccepted(chat, input.OperationId).Current.Status,
                    "same text without the files is not successful delivery");
            });
        }

        private static void JournalAttachmentImmutability()
        {
            WithJournalInput(false, (paths, journal, chat, scope) => {
                IConversationStore store = journal;
                var upload = JournalUpload(paths, chat, "immutable.txt", "original");
                var message = new ChatMessage { Role = "user", Content = "question", Attachments = new List<ChatAttachment> { upload } };
                LinkJournalUpload(paths, chat, message); store.Commit(chat, ConversationCommit.UserInput(message));
                var loaded = store.Load(chat.Id); var changed = loaded.Messages.Single();
                changed.Attachments[0].FileName = "rebound.txt";
                RuntimeThrows<InvalidDataException>(() => store.Commit(loaded, new ConversationCommit(new[] { changed })));
                loaded = store.Load(chat.Id); changed = loaded.Messages.Single(); changed.Content = "edited question";
                store.Commit(loaded, ConversationCommit.RewriteHistory(new[] { changed.Id }, loaded.Messages));
                AssertEqual(upload.Id, store.Load(chat.Id).Messages.Single().Attachments.Single().Id, "text edit preserves attachment identity");
                loaded = store.Load(chat.Id); changed = loaded.Messages.Single(); changed.Attachments.Clear();
                RuntimeThrows<InvalidOperationException>(() => store.Commit(loaded, ConversationCommit.RewriteHistory(new[] { changed.Id }, loaded.Messages)));
                loaded = store.Load(chat.Id);
                var duplicate = new ChatMessage { Role = "user", Content = "duplicate", Attachments = new List<ChatAttachment> { upload }, ResourceRefs = message.ResourceRefs };
                loaded.Messages.Add(duplicate);
                RuntimeThrows<InvalidOperationException>(() => store.Commit(loaded, ConversationCommit.UserInput(duplicate)));
                var badInput = InboxInput("unstaged", "missing bytes", InputDelivery.Queue);
                badInput.Attachments.Add(new ChatAttachment { Size = 3, ContentByteLength = 3, ContentSha256 = new string('a', 64), ContentType = "text/plain" });
                RuntimeThrows<InvalidDataException>(() => new ConversationInboxService(journal).Submit(chat, badInput));
            });
        }
    }
}
