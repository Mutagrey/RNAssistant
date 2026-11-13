using System;
using System.IO;
using System.Linq;
using System.Threading;
using RNAssistant.Application.Conversations;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void AcceptJournalPeerCall(IConversationStore store, ChatSession chat, string callId, string runId,
            string tool = AgentCommunicationToolCatalog.Send)
        {
            var initial = CheckpointState(runId, RunLifecycle.Running, 0, 0);
            chat.LastRun = new ChatRunRecord { RunId = runId, TurnId = initial.Summary.TurnId, StartedUtc = DateTime.UtcNow,
                KernelState = initial, ConversationPolicy = ConversationRunPolicy.For(ChatModes.Agent) };
            var started = new AgentRunEvent(AgentRunEventKind.Started, initial.Summary, limits: initial.Limits);
            new ConversationRunCommit(chat, started, new ChatMessage[0]).Persist(store.CommitRun, chat);
            var summary = new RunSummary(runId, initial.Summary.TurnId, RunLifecycle.Running, new ToolCounts(), 1, 0, "", null, null);
            var call = new ToolCall(callId, tool, "{\"agent_id\":\"peer\",\"message\":\"source\"}");
            var accepted = new AgentRunEvent(AgentRunEventKind.ResponseAccepted, summary,
                response: new AgentResponse("execute", "Contact the exact peer.", null, new[] { call }));
            chat.LastRun.KernelState = AgentRunState.Apply(chat.LastRun.KernelState, accepted);
            ConversationRunProjection.Apply(chat.LastRun);
            var message = JournalAgentCall(callId, runId, tool); chat.Messages.Add(message);
            new ConversationRunCommit(chat, accepted, new[] { message }).Persist(store.CommitRun, chat);
        }

        private static void PersistPeerInput(IConversationStore store, ChatSession chat, ConversationInput input, string run)
        {
            var message = new ChatMessage { Id = input.Id, Role = "user", Content = input.Text,
                AgentCommunication = input.AgentCommunication, RunId = run };
            chat.Messages.Add(message); store.Commit(chat, ConversationCommit.UserInput(message));
        }

        private static void JournalPeerAtomicInbox()
        {
            foreach (var office in new[] { true, false }) WithJournalInput(office, (paths, journal, source, scope) => {
                IConversationStore store = journal;
                var target = store.CreateTransient(source.Host, source.DocumentKey, source.DocumentTitle, "Peer");
                store.Commit(target, new ConversationCommit());
                AcceptJournalPeerCall(store, source, "send", "source-run");
                var sourceBefore = store.Load(source.Id); var targetBefore = store.Load(target.Id);
                var before = journal.AcceptedCall(source.Id, "source-run", "send").Position.Sequence;
                Func<AgentMessageReceipt> send = () => journal.SendAgent("send", source.Id, "source-run", "send",
                    AgentChatAddress.IdFor(target.Id), source.Id, "peer-chain", 2, "Native peer input", expectReply: true,
                    recipientRuntimeKey: target.WorkspaceId ?? target.DocumentKey);
                journal.Journal.Fault = point => { if (point == "before_append") throw new IOException("before acceptance"); };
                try { RuntimeThrows<IOException>(() => send()); } finally { journal.Journal.Fault = null; }
                AssertTrue(journal.AgentReceipt("send") == null, "no partial delivery before acceptance");
                AssertEqual(sourceBefore.Revision, store.ReadMetadata(source.Id).Revision, "failed acceptance leaves sender head unchanged");
                AssertEqual(targetBefore.Revision, store.ReadMetadata(target.Id).Revision, "failed acceptance leaves recipient head unchanged");
                journal.Journal.Fault = point => { if (point == "after_flush") throw new IOException("lost acknowledgement"); };
                try { RuntimeThrows<IOException>(() => send()); } finally { journal.Journal.Fault = null; }
                var receipt = journal.AgentReceipt("send");
                AssertEqual(before + 1, receipt.Position.Sequence, "delivery, membership, input and both heads share one frame");
                var written = journal.Journal.WrittenBytes;
                AssertEqual(receipt.Fact.MessageId, send().Fact.MessageId, "uncertain acknowledgement resolves to the original message identity");
                AssertEqual(written, journal.Journal.WrittenBytes, "retry does not append another delivery");
                source = store.Load(source.Id); target = store.Load(target.Id);
                AssertEqual(receipt.Position.Sequence, source.Revision, "sender cursor includes its outgoing delivery");
                AssertEqual(source.Revision, target.Revision, "recipient sees the same atomic frame");
                AssertEqual(source.StorageHeadHash, target.StorageHeadHash, "same durable frame hash, separately scoped chat cursors");
                AssertEqual(receipt.Fact.CreatedUtc, target.UpdatedUtc, "new input advances activity metadata");
                var sourceFeed = new ConversationEventFeed(journal).Read(ConversationMetadata.From(source), ConversationEventFeed.Cursor(sourceBefore));
                var targetFeed = new ConversationEventFeed(journal).Read(ConversationMetadata.From(target), ConversationEventFeed.Cursor(targetBefore));
                AssertEqual("agent.accepted", sourceFeed.Events.Single().Type, "sender event is native delivery, not a fabricated legacy envelope");
                AssertEqual(SessionEventTypes.ConversationInputChanged, targetFeed.Events.Single().Type, "recipient event uses its existing Inbox owner");
                var events = (IEventStore)journal;
                AssertEqual(0, events.ReadAgentRequests(ConversationMetadata.From(sourceBefore), new[] { receipt.Fact.MessageId }).Count, "old source prefix cannot see the new request");
                AssertEqual(0, events.ReadAgentRequests(ConversationMetadata.From(target), new[] { receipt.Fact.MessageId }).Count, "foreign request cannot be used for correlation");
                AssertEqual(target.Id, events.ReadAgentRequests(ConversationMetadata.From(source), new[] { receipt.Fact.MessageId }).Single().Target.ChatId, "own request is addressed exactly");
                journal.Rebuild(); target = store.Load(target.Id);
                var inbox = new ConversationInboxService(journal);
                AssertTrue(inbox.Snapshot(target).Paused && !inbox.TryStartWorker(target), "restart never auto-delivers queued peer input");
                inbox.Resume(target); var input = inbox.TakeNext(target);
                AssertEqual("Native peer input", input.Text, "recipient hydrates the original CAS text");
                var wrong = new ChatMessage { Id = input.Id, Role = "user", Content = "wrong body", AgentCommunication = input.AgentCommunication };
                target.Messages.Add(wrong);
                RuntimeThrows<InvalidDataException>(() => store.Commit(target, ConversationCommit.UserInput(wrong)));
                wrong.Content = input.Text; wrong.AgentCommunication = null;
                RuntimeThrows<InvalidDataException>(() => store.Commit(target, ConversationCommit.UserInput(wrong)));
                target.Messages.Remove(wrong);
                PersistPeerInput(store, target, input, "target-run"); inbox.Applied(target, input.Id);
                AssertEqual(ConversationInputStatus.Applied, journal.InputState(receipt.Fact.MessageId).Status, "Applied retains an exact persisted message receipt");
                journal.Rebuild(); target = store.Load(target.Id);
                AssertEqual(0, new ConversationInboxService(journal).ReadUndelivered(target).Count, "rebuild does not replay the applied peer input");
                AssertEqual(1, target.Messages.Count(item => item.Id == receipt.Fact.MessageId), "one accepted input remains one user message");
                if (!office)
                {
                    var backup = Path.Combine(paths.Root, "peer-backup"); JournalCasService(paths, StorageProtector.None).BackupJournals(backup);
                    var restored = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "peer-restore"), _ => StorageProtector.None);
                    var copy = new ConversationJournal(restored, scope); IConversationStore copied = copy;
                    AssertEqual(ConversationInputStatus.Applied, new ConversationInboxService(copy)
                        .ReadAccepted(copied.Load(target.Id), "send").Current.Status, "restore preserves the exact peer receipt and body");
                    AssertEqual(source.StorageHeadHash, copied.Load(source.Id).StorageHeadHash, "restore retains the sender head without dispatch");
                }
            });
        }
    }
}
