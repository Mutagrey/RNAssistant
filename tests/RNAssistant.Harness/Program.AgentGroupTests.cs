using System;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Application.Conversations;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static ChatSession GroupChat(ChatStore store, string title, string document = "group-doc")
        {
            var session = store.CreateTransient("Excel", document, "Groups.xlsx", title);
            session.DocumentAuthorityId = "doc_" + document;
            session.Messages.Add(new ChatMessage { Role = "user", Content = "Preserve history " + title });
            store.Save(session); return session;
        }
        private static ChatGroup GroupChange(ChatStore store, ChatSession scope, ChatGroupAction action, ChatGroup group,
            string name = null, params string[] members)
        {
            return store.ChangeChatGroup(scope, new ChatGroupChange { Action = action, GroupId = group?.Id,
                ExpectedRevision = group?.Revision, Name = name, Members = members }, Guid.NewGuid().ToString("N")).Group;
        }
        private static void AgentGroupsLifecycle()
        {
            WithTempPaths(paths => {
                var store = new ChatStore(paths); var parent = GroupChat(store, "Creator"); var child = GroupChat(store, "Child");
                child.AgentCreation = new AgentThreadCreation { Creator = AgentCommunicationService.Address(parent) };
                store.Save(child);
                var childPath = Path.Combine(paths.ChatDirectory, AppDataPaths.SafeFileName(child.Host + "|" + child.DocumentKey),
                    AppDataPaths.SafeFileName(child.Id) + ".events.jsonl");
                var before = File.ReadAllText(childPath);
                var first = GroupChange(store, parent, ChatGroupAction.Create, null, "Chess");
                var second = GroupChange(store, parent, ChatGroupAction.Create, null, "Review");
                first = GroupChange(store, parent, ChatGroupAction.Add, first, null, parent.Id, child.Id, child.Id);
                second = GroupChange(store, parent, ChatGroupAction.Add, second, null, child.Id);
                AssertEqual(2, first.Members.Count, "duplicate membership is a set");
                var repeated = store.ChangeChatGroup(parent, new ChatGroupChange { Action = ChatGroupAction.Add,
                    GroupId = first.Id, ExpectedRevision = first.Revision, Members = new[] { child.Id } }, "duplicate");
                AssertTrue(!repeated.Changed && repeated.Group.Revision == first.Revision, "repeated add has verified no change");
                AssertEqual(before, File.ReadAllText(childPath), "groups do not write the target chat/context/run");
                first = GroupChange(store, parent, ChatGroupAction.Remove, first, null, child.Id);
                AssertTrue(store.ReadChatGroups(parent).Single(g => g.Id == second.Id).Members.Contains(child.Id), "remove affects one group");
                var reopened = new ChatStore(paths); var header = reopened.ListHeaders(false).Single(h => h.Id == child.Id);
                AssertEqual(parent.Id, header.CreatorChatId, "creator survives header checkpoint/reopen");
                AssertEqual(child.DocumentAuthorityId, header.DocumentAuthorityId, "header carries exact document identity");
                AssertTrue(reopened.Delete(parent.Host, parent.DocumentKey, parent.Id), "creator deleted by existing owner");
                AssertEqual(2, reopened.ReadChatGroups(child).Count, "group lifetime is independent of its creator");
                AssertEqual(0, reopened.ReadChatGroups(child).Single(g => g.Id == first.Id).Members.Count, "deleted chat is hidden from every membership");
                GroupChange(reopened, child, ChatGroupAction.Delete, second);
                AssertEqual(before, File.ReadAllText(childPath), "deleting group preserves child history");
                var gc = CasService(paths, reopened, new VbaJournalStore(paths), () => StorageProtector.None).Collect();
                AssertTrue(gc.Completed, "existing resource journal is a CAS reachability source");
                AssertEqual("Chess", new ChatStore(paths).ReadChatGroups(child).Single().Name, "empty group survives restart and CAS GC");
                var forgotten = new ChatGroupChange { Action = ChatGroupAction.Add, GroupId = second.Id,
                    ExpectedRevision = second.Revision, Members = new[] { child.Id } };
                RuntimeThrows<ChatConcurrencyException>(() => reopened.ChangeChatGroup(child, forgotten, "late-add"));
            });
        }

        private static void AgentGroupsConcurrentChanges()
        {
            WithTempPaths(paths => {
                var store = new ChatStore(paths); var source = GroupChat(store, "Source"); var a = GroupChat(store, "A"); var b = GroupChat(store, "B");
                var group = GroupChange(store, source, ChatGroupAction.Create, null, "Work");
                var success = 0; var conflicts = 0;
                Parallel.ForEach(new[] { a.Id, b.Id }, id => {
                    try { GroupChange(new ChatStore(paths), source, ChatGroupAction.Add, group, null, id); Interlocked.Increment(ref success); }
                    catch (ChatConcurrencyException) { Interlocked.Increment(ref conflicts); }
                });
                AssertEqual(1, success, "one exact revision wins"); AssertEqual(1, conflicts, "stale mutation fails without overwrite");
                group = store.ReadChatGroups(source).Single();
                group = GroupChange(store, source, ChatGroupAction.Add, group, null, a.Id, b.Id);
                RuntimeThrows<ChatConcurrencyException>(() => GroupChange(store, source, ChatGroupAction.Create, null, "work"));
                var foreign = GroupChat(store, "Foreign", "elsewhere");
                RuntimeThrows<ChatConcurrencyException>(() => GroupChange(store, source, ChatGroupAction.Add, group, null, foreign.Id));
                Parallel.Invoke(() => store.Delete(b.Host, b.DocumentKey, b.Id), () => {
                    try { GroupChange(new ChatStore(paths), source, ChatGroupAction.Add, group, null, b.Id); }
                    catch (ChatConcurrencyException) { }
                });
                AssertTrue(!store.ReadChatGroups(source).Single().Members.Contains(b.Id), "add/delete race never resurrects a chat");
                var create = new ChatGroupChange { Action = ChatGroupAction.Create, Name = "Once" };
                var once = store.ChangeChatGroup(source, create, "same-operation");
                AssertTrue(!store.ChangeChatGroup(source, create, "same-operation").Changed, "verified creation identity is reused");
                GroupChange(store, source, ChatGroupAction.Delete, once.Group);
                RuntimeThrows<ChatConcurrencyException>(() => store.ChangeChatGroup(source, create, "same-operation"));
            });
        }

        private static void AgentGroupsToolAndDiscovery()
        {
            WithTempPaths(paths => {
                var store = new ChatStore(paths); var source = GroupChat(store, "Source"); var peer = GroupChat(store, "Peer");
                var inbox = new ConversationInboxService(EventStore(store)); var service = new AgentCommunicationService();
                var started = 0; var scope = StoredAgentScope(store, inbox, source, _ => started++);
                scope.Groups = new ConversationGroupService(store); service.Register(scope);
                var entry = AgentCommunicationToolCatalog.GetTools().Single(e => e.Id == AgentCommunicationToolCatalog.Group);
                var registry = new ToolHandlerRegistry(); registry.Register(new ToolRegistration(new ToolDescriptor(entry.Id, entry.Description,
                    entry.ArgumentSchemaJson), entry.Policy, entry.Binding, entry.ContractVersion), new AgentCommunicationToolHandler(service, inbox, source, 2));
                var runtime = new ToolRuntime(registry, "plan", false, true, null); var calls = 0;
                Func<string, ToolExecutionRecord> execute = json => {
                    var call = new ToolCall("group_" + ++calls, entry.Id, json);
                    return runtime.ExecuteAsync(new ToolExecutionContext(call, runtime.Describe(call), "run", "turn", "step_" + calls,
                        DateTime.UtcNow, false, 10), CancellationToken.None).GetAwaiter().GetResult();
                };
                AssertEqual(ToolExecutionOutcome.Ok, execute("{\"action\":\"create\",\"name\":\"Chess\"}").Outcome, "group tool uses exact managed mutation binding");
                var add = execute(new JObject { ["action"] = "add", ["group"] = "Chess", ["agent_ids"] = new JArray(AgentChatAddress.IdFor(peer.Id)) }.ToString());
                AssertEqual(ToolExecutionOutcome.Ok, add.Outcome, "group membership commits in Plan without running a peer");
                var listing = execute("{\"action\":\"list\"}");
                AssertEqual(ToolExecutionOutcome.Ok, listing.Outcome, "list verifies no mutation under the unified tool policy");
                AssertEqual(0, started, "group operations never start/wake participants");
                var page = service.Discover(source, 2, group: "Chess");
                AssertEqual(AgentChatAddress.IdFor(peer.Id), page.Peers.Single().AgentId, "group filter resolves the same participant");
                for (var i = 0; i < 53; i++) GroupChat(store, "Archived " + i);
                var first = service.Discover(source, 2); var next = service.Discover(source, 2, afterAgent: first.NextAgent);
                AssertTrue(first.Truncated && !next.Truncated && first.Peers.Count + next.Peers.Count == 55, "catalog pages all chats independently of participant budget");
                AssertEqual(55, first.Peers.Concat(next.Peers).Select(p => p.AgentId).Distinct().Count(), "no duplicate tree memberships in discovery");
                RuntimeThrows<AgentCommunicationException>(() => service.Group(source, 0, new AgentGroupRequest { Action = "list" }, "off", null));
                var projection = JsonConvert.SerializeObject(service.Group(source, 2, new AgentGroupRequest { Action = "list" }, "read", null));
                AssertTrue(!projection.Contains("cg_") && !projection.Contains("rna://") && !projection.Contains(peer.Id), "model sees names and public addresses only");
            });
        }
    }
}
