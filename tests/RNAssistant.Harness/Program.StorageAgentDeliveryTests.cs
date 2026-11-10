using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private const string DeliveryTestScope = "workspace:atomic-delivery";

        private static void ConversationJournalDeliveryAndWait()
        {
            foreach (var encrypted in new[] { false, true }) WithTempPaths(paths => {
                var protector = encrypted ? new StorageProtector(HistoryIntegrityModes.HmacSha256,
                    HistoryEncryptionModes.Aes256CbcHmacSha256, "synthetic-delivery", Enumerable.Repeat((byte)7, 16).ToArray()) : StorageProtector.None;
                var owner = new ConversationJournal(paths, DeliveryTestScope, protector);
                foreach (var chat in new[] { "root", "peer", "third" })
                    owner.CreateChat("create-" + chat, chat, chat, ConversationRunPolicy.For(ChatModes.Plan));
                owner.CommitCall("root", "call-send", JournalAgentCall("send", "run"));
                var tail = owner.AcceptedCall("root", "run", "send").Position.Sequence;
                Func<AgentMessageReceipt> send = () => owner.SendAgent("send", "root", "run", "send", AgentChatAddress.IdFor("peer"),
                    "root", "chain", 2, "Please inspect the source", expectReply: true, assignment: true);
                owner.Journal.Fault = point => { if (point == "before_append") throw new IOException("before acceptance"); };
                RuntimeThrows<IOException>(() => send());
                owner.Journal.Fault = null;
                AssertTrue(owner.AgentReceipt("send") == null && owner.PendingAgentInputs("peer", 0, 10).Count == 0, "before-frame fault accepts neither receipt nor input");
                owner.Journal.Fault = point => { if (point == "after_flush") throw new IOException("lost delivery acknowledgement/notify"); };
                RuntimeThrows<IOException>(() => send());
                owner = new ConversationJournal(paths, DeliveryTestScope, protector);
                var accepted = owner.AgentReceipt("send");
                AssertEqual(tail + 1, accepted.Position.Sequence, "input receipt and membership use exactly one frame");
                AssertEqual(accepted.Fact.MessageId, owner.PendingAgentInputs("peer", 0, 1).Single().Fact.MessageId, "lost notify retains exact pending input");
                AssertEqual(ConversationInputStatus.Pending, owner.InputState(accepted.Fact.MessageId).Status, "recovery does not start pending work");
                var bytes = owner.Journal.WrittenBytes;
                AssertEqual(accepted.Fact.MessageId, send().Fact.MessageId, "retry returns canonical message id");
                AssertEqual(bytes, owner.Journal.WrittenBytes, "retry writes no local handshake/duplicate frame");
                RuntimeThrows<InvalidOperationException>(() => owner.SendAgent("send", "root", "run", "send", AgentChatAddress.IdFor("peer"),
                    "root", "chain", 2, "different text", expectReply: true, assignment: true));
                AssertTrue(new ConversationJournal(paths, "workspace:other", protector).FindAgent(AgentChatAddress.IdFor("peer")) == null, "same database gives no cross-scope address grant");

                var requested = accepted.Fact.MessageId;
                owner.ChangeInput("take", "peer", requested, 0, ConversationInputStatus.Delivering, "peer-run");
                RuntimeThrows<InvalidOperationException>(() => owner.ChangeInput("wrong-run", "peer", requested, 1, ConversationInputStatus.Applied, "other-run"));
                owner.ChangeInput("apply", "peer", requested, 1, ConversationInputStatus.Applied, "peer-run");
                AssertEqual(ConversationInputStatus.Applied, owner.ChangeInput("take", "peer", requested, 0, ConversationInputStatus.Delivering, "peer-run").Status,
                    "old transition retry cannot roll back current applied state");
                var applied = owner.AppliedAgentMessage(requested);
                AssertEqual("Please inspect the source", applied.Content, "applied user message derives from accepted immutable input body");
                AssertEqual("peer-run", applied.RunId, "applied input retains exact accepting run");
                AssertEqual("root", applied.AgentCommunication.Sender.ChatId, "applied input retains peer provenance");
                AssertTrue(ConversationInputMessage.ModelText(applied).Contains("not the human user"), "peer input cannot become human authority");

                owner.CommitCall("root", "call-third", JournalAgentCall("third", "run"));
                RuntimeThrows<InvalidOperationException>(() => owner.SendAgent("third", "root", "run", "third", AgentChatAddress.IdFor("third"), "root", "chain", 2, "over limit"));
                owner.CommitCall("third", "call-forged", JournalAgentCall("forged", "run"));
                RuntimeThrows<InvalidOperationException>(() => owner.SendAgent("forged", "third", "run", "forged", AgentChatAddress.IdFor("root"), "root", "chain", 3,
                    "forged reply", replyTo: requested));

                owner.CommitCall("peer", "call-reply", JournalAgentCall("reply", "peer-run"));
                owner.Fault = point => { throw new IOException("reply index unavailable"); };
                var reply = owner.SendAgent("reply", "peer", "peer-run", "reply", AgentChatAddress.IdFor("root"), "root", "chain", 2,
                    "inspected", replyTo: requested, resolution: AgentAssignmentOutcome.Completed);
                AssertTrue(!reply.ProjectionAvailable, "reply JSONL remains accepted when index fails");
                owner = new ConversationJournal(paths, DeliveryTestScope, protector);
                owner.ChangeInput("take-reply", "root", reply.Fact.MessageId, 0, ConversationInputStatus.Delivering, "next-run");
                owner.ChangeInput("apply-reply", "root", reply.Fact.MessageId, 1, ConversationInputStatus.Applied, "next-run");
                AssertEqual(0, owner.PendingAgentInputs("root", 0, 10).Count, "applied reply is absent from pending RAM window");
                var now = DateTime.UtcNow;
                var condition = new AgentWaitCondition(now, now.AddSeconds(10), new[] { requested }, true, true);
                var reopened = new ConversationJournal(paths, DeliveryTestScope, protector);
                var reads = reopened.Journal.ReadRecords;
                var matched = reopened.MatchReplies("root", condition);
                AssertTrue(matched.Ready, "wait registered after applied reply finds historical completion");
                AssertEqual(reply.Fact.MessageId, matched.Matches.Single().Fact.MessageId, "wait matches exact peer and original message id");
                AssertTrue(reopened.Journal.ReadRecords - reads <= 3, "wait reads boundary/request/reply only without body/history hydration");
                RuntimeThrows<InvalidOperationException>(() => reopened.MatchReplies("third", condition));
                owner.CommitCall("peer", "call-duplicate-resolution", JournalAgentCall("duplicate-resolution", "peer-run"));
                RuntimeThrows<InvalidOperationException>(() => owner.SendAgent("duplicate-resolution", "peer", "peer-run", "duplicate-resolution", AgentChatAddress.IdFor("root"),
                    "root", "chain", 2, "another resolution", replyTo: requested, resolution: AgentAssignmentOutcome.Failed));

                var canonical = Directory.EnumerateFiles(paths.Root, "*.jsonl", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
                reopened.Rebuild();
                foreach (var pair in canonical) AssertTrue(pair.Value.SequenceEqual(File.ReadAllBytes(pair.Key)), "delivery rebuild preserves canonical bytes");
                AssertTrue(reopened.MatchReplies("root", condition).Ready, "wait correlation survives index deletion/rebuild");
                AssertEqual(accepted.Fact.MessageId, reopened.AgentReceipt("send").Fact.MessageId, "send receipt survives rebuild");
                AssertEqual(0, reopened.PendingAgentInputs("peer", 0, 1).Count, "rebuild never requeues applied input");
            });
        }

        private static void ConversationJournalDeliveryProcessRaces()
        {
            WithTempPaths(paths => {
                var owner = new ConversationJournal(paths, DeliveryTestScope);
                foreach (var chat in new[] { "root", "a", "b" }) owner.CreateChat("create-" + chat, chat, chat, ConversationRunPolicy.For(ChatModes.Agent));
                foreach (var op in new[] { "last-a", "last-b", "same", "spawn-a", "spawn-b" })
                    owner.CommitCall("root", "call-" + op, JournalAgentCall(op, "run", op.StartsWith("spawn-", StringComparison.Ordinal) ? AgentCommunicationToolCatalog.Spawn : AgentCommunicationToolCatalog.Send));
                using (var a = StartDeliveryProbe(paths.Root, "last-a", "a", "last", 2))
                using (var b = StartDeliveryProbe(paths.Root, "last-b", "b", "last", 2))
                {
                    AssertTrue(a.WaitForExit(15000) && b.WaitForExit(15000), "last participant processes complete");
                    AssertEqual(1, new[] { a.ExitCode, b.ExitCode }.Count(code => code == 0), "one process claims last participant slot");
                    AssertEqual(1, new[] { a.ExitCode, b.ExitCode }.Count(code => code == 12), "loser sees explicit participant conflict");
                }
                using (var a = StartDeliveryProbe(paths.Root, "same", "a", "duplicate", 2))
                using (var b = StartDeliveryProbe(paths.Root, "same", "a", "duplicate", 2))
                {
                    AssertTrue(a.WaitForExit(15000) && b.WaitForExit(15000), "duplicate sender processes complete");
                    AssertEqual(0, a.ExitCode, "first duplicate sender accepted"); AssertEqual(0, b.ExitCode, "second duplicate sender accepted");
                    AssertEqual(a.StandardOutput.ReadToEnd().Trim(), b.StandardOutput.ReadToEnd().Trim(), "both processes receive one canonical message identity");
                }
                var receipts = new[] { owner.AgentReceipt("last-a"), owner.AgentReceipt("last-b") }.Count(value => value != null);
                AssertEqual(1, receipts, "failed membership race leaves no input/receipt residue");
                using (var a = StartDeliveryProbe(paths.Root, "spawn-a", "a", "spawn-race", 2))
                using (var b = StartDeliveryProbe(paths.Root, "spawn-b", "b", "spawn-race", 2))
                {
                    AssertTrue(a.WaitForExit(15000) && b.WaitForExit(15000), "spawn participant race completes");
                    AssertEqual(1, new[] { a.ExitCode, b.ExitCode }.Count(code => code == 0), "one atomic spawn claims the last participant slot");
                    AssertEqual(1, new[] { a.ExitCode, b.ExitCode }.Count(code => code == 12), "losing spawn leaves no visible child");
                }
                owner.Rebuild();
                AssertTrue(owner.AgentReceipt("same") != null, "concurrent delivery survives full rebuild");
            });
        }

        private static void ConversationJournalAtomicSpawn()
        {
            WithTempPaths(paths => {
                var owner = new ConversationJournal(paths, DeliveryTestScope);
                owner.CreateChat("create-root", "root", "Root", ConversationRunPolicy.For(ChatModes.Plan), model: new ModelRunMetadata { Model = "fixture", ReasoningEnabled = true });
                owner.CommitCall("root", "call-spawn", JournalAgentCall("spawn", "run", AgentCommunicationToolCatalog.Spawn));
                var before = owner.AcceptedCall("root", "run", "spawn").Position.Sequence;
                Func<AgentSpawnJournalReceipt> spawn = () => owner.SpawnAgent("spawn", "root", "run", "spawn", "root", "spawn-chain", 2, "Research", "inspect source", true, true);
                var childId = "child-" + TextPatternEngine.Sha256(DeliveryTestScope + "\nspawn");
                owner.Journal.Fault = point => { if (point == "before_append") throw new IOException("before spawn"); };
                RuntimeThrows<IOException>(() => spawn()); owner.Journal.Fault = null;
                AssertTrue(owner.FindAgent(AgentChatAddress.IdFor(childId)) == null && owner.SpawnReceipt("spawn") == null, "failed spawn has no orphan child/receipt");
                owner.Journal.Fault = point => { if (point == "after_flush") throw new IOException("lost spawn acknowledgement"); };
                RuntimeThrows<IOException>(() => spawn());
                owner = new ConversationJournal(paths, DeliveryTestScope);
                var receipt = owner.SpawnReceipt("spawn");
                AssertEqual(before + 1, receipt.Position.Sequence, "child membership initial input and receipt use one frame");
                AssertEqual(childId, receipt.Fact.Child.ChatId, "retry-independent runtime child identity");
                AssertEqual("fixture", owner.FindAgent(AgentChatAddress.IdFor(childId)).Model.Model, "child preserves captured model configuration");
                AssertEqual(ChatModes.Plan, receipt.Fact.Child.Policy.Mode, "spawn never escalates source mode");
                AssertEqual(receipt.Fact.Input.MessageId, owner.PendingAgentInputs(childId, 0, 1).Single().Fact.MessageId, "initial input cannot be omitted after accepted spawn");
                var bytes = owner.Journal.WrittenBytes;
                AssertEqual(childId, spawn().Fact.Child.ChatId, "spawn lost-ack retry returns original child");
                AssertEqual(bytes, owner.Journal.WrittenBytes, "spawn retry appends no handshake");
                RuntimeThrows<InvalidOperationException>(() => owner.SpawnAgent("spawn", "root", "run", "spawn", "root", "spawn-chain", 2, "Research", "different text", true, true));
                var input = receipt.Fact.Input.MessageId;
                owner.ChangeInput("take-spawn", childId, input, 0, ConversationInputStatus.Delivering, "child-run");
                owner.ChangeInput("apply-spawn", childId, input, 1, ConversationInputStatus.Applied, "child-run");
                AssertEqual("inspect source", owner.AppliedAgentMessage(input).Content, "spawned initial message uses the common applied-input path");
                owner.CommitCall("root", "call-empty-spawn", JournalAgentCall("empty-spawn", "run", AgentCommunicationToolCatalog.Spawn));
                var empty = owner.SpawnAgent("empty-spawn", "root", "run", "empty-spawn", "root", "empty-chain", 2, "Idle child");
                AssertTrue(empty.Fact.Input == null && owner.PendingAgentInputs(empty.Fact.Child.ChatId, 0, 1).Count == 0, "empty spawn creates no synthetic input/run");
                var canonical = Directory.EnumerateFiles(paths.Root, "*.jsonl", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
                owner.Rebuild();
                foreach (var pair in canonical) AssertTrue(pair.Value.SequenceEqual(File.ReadAllBytes(pair.Key)), "spawn rebuild preserves bytes");
                AssertEqual(childId, spawn().Fact.Child.ChatId, "spawn retry after rebuild preserves receipt");
                AssertEqual(ConversationInputStatus.Applied, owner.InputState(input).Status, "spawn rebuild does not replay applied initial input");
            });
        }

        private static Process StartDeliveryProbe(string root, string operation, string target, string chain, int limit)
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { typeof(Program).Assembly.Location, "--conversation-send-probe", root, operation, target, chain, limit.ToString() }) start.ArgumentList.Add(argument);
            return Process.Start(start);
        }

        private static int ConversationSendProbe(string root, string operation, string target, string chain, int limit)
        {
            try
            {
                var owner = new ConversationJournal(AppDataPaths.CreateForRoot(root), DeliveryTestScope);
                if (operation.StartsWith("spawn-", StringComparison.Ordinal))
                {
                    var spawned = owner.SpawnAgent(operation, "root", "run", operation, "root", chain, limit, "Child", "same text");
                    Console.WriteLine(spawned.Fact.Child.ChatId); return 0;
                }
                var receipt = owner.SendAgent(operation, "root", "run", operation, AgentChatAddress.IdFor(target), "root", chain, limit, "same text");
                Console.WriteLine(receipt.Fact.MessageId); return 0;
            }
            catch (InvalidOperationException error) when (error.Message == "Participant limit reached.") { return 12; }
        }

        private static ChatMessage JournalAgentCall(string call, string run, string tool = AgentCommunicationToolCatalog.Send)
        {
            var message = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall { Id = call, Name = tool,
                Arguments = new Dictionary<string, object> { ["agent_id"] = "peer", ["message"] = "source" } }, "", "Contact the exact peer.", null,
                ToolResultRoles.User, new AcceptedToolCallOrigin("step-" + call, "model-" + call, 0, 1));
            message.RunId = run; return message;
        }
    }
}
