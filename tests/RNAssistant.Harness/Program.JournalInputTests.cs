using System;
using System.IO;
using System.Linq;
using RNAssistant.Application.Conversations;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void WithJournalInput(bool office, Action<AppDataPaths, ConversationJournal, ChatSession, string> check)
        {
            WithTempPaths(paths => {
                var workspace = office ? null : new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "project"));
                var scope = office ? "document:" + DocumentAuthorityId.Create().Id : "workspace:" + workspace.WorkspaceId;
                var journal = new ConversationJournal(paths, scope); IConversationStore store = journal;
                var chat = office ? new ChatSessionService(FakeOfficeAdapter.ForHost("Excel"), store).CreatePersistentChat("Input") :
                    new WorkspaceStore(paths).CreateSession(store, workspace, "Input");
                try { check(paths, journal, chat, scope); }
                catch (Exception error) { Console.WriteLine(error); throw; }
            });
        }

        private static void PersistJournalInput(IConversationStore store, ChatSession chat, ConversationInput input)
        {
            var message = new ChatMessage { Id = input.Id, Role = "user", Content = input.Text, RunId = input.AcceptedRunId };
            chat.Messages.Add(message); store.Commit(chat, ConversationCommit.UserInput(message));
        }

        private static void JournalInputRecoveryAndRestore()
        {
            foreach (var office in new[] { true, false }) WithJournalInput(office, (paths, journal, chat, scope) => {
                IConversationStore store = journal;
                var inbox = new ConversationInboxService(journal);
                var first = InboxInput("first", "original", InputDelivery.Queue);
                inbox.Submit(chat, first);
                AssertTrue(inbox.Submit(chat, first) == null, "acceptance retry is read-only");
                inbox.Update(chat, new ConversationInputUpdate { InputId = first.Id, ExpectedRevision = 1, Text = "edited" }, "edit");
                RuntimeThrows<InvalidOperationException>(() => inbox.Update(chat,
                    new ConversationInputUpdate { InputId = first.Id, ExpectedRevision = 1, Text = "stale" }, "edit"));
                inbox.Submit(chat, InboxInput("removed", "discard", InputDelivery.Queue));
                inbox.Update(chat, new ConversationInputUpdate { InputId = "removed", ExpectedRevision = 1 }, "remove");
                journal.Rebuild(); chat = store.Load(chat.Id);
                var counted = new InboxFailingEvents(journal);
                inbox = new ConversationInboxService(counted);
                AssertTrue(inbox.Snapshot(chat).Paused, "restart does not replay accepted input");
                AssertTrue(!inbox.TryStartWorker(chat), "cold queue cannot dispatch");
                AssertEqual(2, counted.PayloadReads, "only the latest body per input is hydrated");
                var replay = new ConversationInputJournal(journal).Read(chat, false);
                AssertTrue(replay.All(item => item.Change.Input?.Text == null), "input headers do not duplicate CAS text");
                AssertEqual("edited", inbox.FindOperation(chat, first.OperationId, first.RequestFingerprint).Text, "edit retains the initial receipt fingerprint");
                inbox.Resume(chat);
                var input = inbox.TakeNext(chat); AssertEqual("edited", input.Text, "FIFO resumes the edited input");
                var beforeMessage = store.Load(chat.Id);
                PersistJournalInput(store, chat, input);
                var beforeApplied = store.Load(chat.Id);
                inbox.Applied(chat, input.Id);
                AssertEqual(ConversationInputStatus.Delivering, inbox.ReadAccepted(beforeMessage, first.OperationId).Current.Status,
                    "a stale message projection reads only its accepted prefix");
                AssertEqual(ConversationInputStatus.Delivering, inbox.ReadAccepted(beforeApplied, first.OperationId).Current.Status,
                    "a receipt cannot advance past the caller's cursor");
                AssertEqual(ConversationInputStatus.Applied, inbox.ReadAccepted(chat, first.OperationId).Current.Status, "delivery has a canonical receipt");
                journal.Rebuild(); chat = store.Load(chat.Id);
                inbox = new ConversationInboxService(journal);
                AssertEqual(ConversationInputStatus.Applied, inbox.ReadAccepted(chat, first.OperationId).Current.Status, "rebuild preserves delivery proof");
                AssertEqual(0, inbox.ReadUndelivered(chat).Count, "applied input is never replayed");
                if (!office)
                {
                    var backup = Path.Combine(paths.Root, "backup");
                    JournalCasService(paths, StorageProtector.None).BackupJournals(backup);
                    var restored = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "restore"), _ => StorageProtector.None);
                    var copy = new ConversationJournal(restored, scope); var loaded = ((IConversationStore)copy).Load(chat.Id);
                    AssertEqual(ConversationInputStatus.Applied, new ConversationInboxService(copy).ReadAccepted(loaded, first.OperationId).Current.Status,
                        "clean restore retains the original message receipt");
                    AssertEqual("edited", new ConversationInputJournal(copy).Read(loaded).Last(item => item.Change.Input?.Id == first.Id).Change.Input.Text,
                        "restore retains the immutable input body");
                }
            });
        }

        private static void JournalInputLostAcknowledgements()
        {
            foreach (var stage in new[] { "accept", "message", "applied" }) WithJournalInput(false, (paths, journal, chat, scope) => {
                IConversationStore store = journal;
                var inbox = new ConversationInboxService(journal);
                var input = InboxInput("accepted", "once", InputDelivery.Queue);
                input.AcceptedRunId = "accepted-run"; input.Status = ConversationInputStatus.Delivering;
                Action<Action> lose = action => {
                    journal.Journal.Fault = point => { if (point == "after_flush") throw new IOException("lost acknowledgement"); };
                    try { RuntimeThrows<IOException>(action); } finally { journal.Journal.Fault = null; }
                };
                if (stage == "accept") lose(() => inbox.Submit(chat, input));
                else
                {
                    inbox.Submit(chat, input);
                    if (stage == "message") lose(() => PersistJournalInput(store, chat, input));
                    else { PersistJournalInput(store, chat, input); lose(() => inbox.Applied(chat, input.Id)); }
                }
                journal.Rebuild(); chat = store.Load(chat.Id);
                inbox = new ConversationInboxService(journal);
                var revision = chat.Revision;
                AssertTrue(inbox.ReadAccepted(chat, input.OperationId, input.RequestFingerprint) != null, "receipt survives acknowledgement loss");
                AssertEqual(revision, store.Load(chat.Id).Revision, "receipt lookup does not append or dispatch");
                AssertEqual(stage == "accept" ? 0 : 1, chat.Messages.Count(item => item.Id == input.Id), "delivery is not duplicated");
                inbox.ReconcileAcceptedDelivery(chat);
                AssertEqual(stage == "accept" ? ConversationInputStatus.Delivering : ConversationInputStatus.Applied,
                    inbox.Find(chat, input.Id).Status, "recovery reconciles the saved user message before any retry");
                if (stage == "accept") AssertTrue(inbox.Snapshot(chat).Paused, "undelivered input requires explicit resume");
                AssertTrue(!inbox.TryStartWorker(chat), "lost acknowledgement never auto-replays input");
            });
        }

        private static void JournalInputProofAndScope()
        {
            WithJournalInput(false, (paths, journal, chat, scope) => {
                IConversationStore store = journal;
                var inbox = new ConversationInboxService(journal);
                var input = InboxInput("input", "accepted text", InputDelivery.Queue);
                input.AcceptedRunId = "accepted-run"; input.Status = ConversationInputStatus.Delivering;
                inbox.Submit(chat, input);
                var revision = chat.Revision;
                input.Revision++; input.Status = ConversationInputStatus.Applied;
                RuntimeThrows<ArgumentException>(() => new ConversationInputJournal(journal).Append(chat, new ConversationInputEvent { Input = input }));
                AssertEqual(revision, store.Load(chat.Id).Revision, "Applied before the persisted message fails before append");
                var collision = InboxInput("collision", "different identity", InputDelivery.Queue);
                collision.OperationId = input.OperationId; collision.Revision = 1;
                RuntimeThrows<InvalidOperationException>(() => new ConversationInputJournal(journal).Append(chat, new ConversationInputEvent { Input = collision }));
                var wrong = InboxInput(input.Id, "different body", InputDelivery.Queue); wrong.AcceptedRunId = input.AcceptedRunId;
                PersistJournalInput(store, chat, wrong);
                RuntimeThrows<InvalidDataException>(() => new ConversationInputJournal(journal).Append(chat, new ConversationInputEvent { Input = input }));
                AssertEqual(ConversationInputStatus.Delivering, inbox.ReadAccepted(chat, input.OperationId).Current.Status, "wrong message content is not delivery proof");
            });
            WithJournalInput(false, (paths, journal, chat, scope) => {
                IConversationStore store = journal;
                var inbox = new ConversationInboxService(journal);
                var input = InboxInput("abandoned", "not delivered", InputDelivery.Queue);
                input.Status = ConversationInputStatus.Delivering; input.AcceptedRunId = "run";
                inbox.Submit(chat, input); chat = store.Load(chat.Id);
                inbox = new ConversationInboxService(journal);
                AssertTrue(inbox.Snapshot(chat).Paused, "abandoned input is paused");
                inbox.Update(chat, new ConversationInputUpdate { InputId = input.Id, ExpectedRevision = 1 }, "remove");
                AssertEqual(ConversationInputStatus.Removed, inbox.ReadAccepted(chat, input.OperationId).Current.Status, "explicit removal of undelivered input survives restart");
                var attachment = InboxInput("attachment", "uncommitted", InputDelivery.Queue);
                attachment.Attachments.Add(new ChatAttachment());
                var revision = chat.Revision;
                RuntimeThrows<InvalidDataException>(() => inbox.Submit(chat, attachment));
                AssertEqual(revision, store.Load(chat.Id).Revision, "uncommitted originals fail before accepting input");
            });
        }
    }
}
