using System;
using System.IO;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void JournalScopeRoutingConsumers()
        {
            WithTempPaths(paths => {
                Func<string, string, string> resolve = (host, key) => host == "Workspace"
                    ? "workspace:" + key : "document:" + key;
                var router = new ConversationJournalRouter(paths, resolve);
                IConversationStore store = router;

                var officeA = FakeOfficeAdapter.ForHost("Excel");
                officeA.DocumentKeyValue = "doc-a"; officeA.RuntimeDocumentKeyValue = "runtime-doc-a";
                var chatA = new ChatSessionService(officeA, store).CreatePersistentChat("Office A");
                var messageA = new ChatMessage { Role = "user", Content = "A" };
                chatA.Messages.Add(messageA); store.Commit(chatA, ConversationCommit.UserInput(messageA));

                var officeB = FakeOfficeAdapter.ForHost("Excel");
                officeB.DocumentKeyValue = "doc-b"; officeB.RuntimeDocumentKeyValue = "runtime-doc-b";
                var chatB = new ChatSessionService(officeB, store).CreatePersistentChat("Office B");
                var messageB = new ChatMessage { Role = "user", Content = "B" };
                chatB.Messages.Add(messageB); store.Commit(chatB, ConversationCommit.UserInput(messageB));

                var workspaces = new WorkspaceStore(paths);
                var workspace = workspaces.Open(Path.Combine(paths.Root, "routing-project"));
                var chatW = workspaces.CreateSession(store, workspace, "Workspace");
                var messageW = new ChatMessage { Role = "user", Content = "W" };
                chatW.Messages.Add(messageW); store.Commit(chatW, ConversationCommit.UserInput(messageW));

                AssertEqual(3, store.ListHeaders(false).Count, "one router lists all canonical Office/Workspace scopes");
                AssertEqual(chatA.Id, store.ListHeaders("Excel", "doc-a", "A").Single().Id, "Office scope resolves by exact document key");
                AssertEqual(chatW.Id, store.ListHeaders("Workspace", workspace.WorkspaceId, "W").Single().Id,
                    "Workspace scope resolves by workspace identity");
                AssertEqual("A", store.ReadMessageWindow(chatA.Id, 1, 1).PageMessages.Single().Content,
                    "addressed Office read routes without a caller-supplied scope");
                AssertEqual("W", store.ReadMessageWindow(chatW.Id, 1, 1).PageMessages.Single().Content,
                    "addressed Workspace read routes through the same owner");
                AssertTrue(store.ReadMessageWindow("Excel", "doc-b", chatA.Id, 1, 1) == null,
                    "host/key mismatch cannot cross a conversation scope");
                AssertTrue(store.Load("missing-chat") == null, "unknown chat is not assigned a scope");

                var events = (IEventStore)router;
                AssertTrue(events.RetainedReplayGuards && events.AtomicAgentDelivery,
                    "router advertises the canonical atomic event capabilities");
                AssertEqual(0, events.ReadToolPackAdmissions(ConversationMetadata.From(chatA), new string[0]).Count,
                    "event reads route to the addressed canonical journal");

                File.Delete(paths.JournalIndexFile);
                var restarted = new ConversationJournalRouter(paths, resolve);
                AssertEqual("B", ((IConversationStore)restarted).Load(chatB.Id).Messages.Single().Content,
                    "scope discovery rebuilds the shared index before routing an addressed chat");
                AssertEqual(3, ((IConversationStore)restarted).ListHeaders(false).Count,
                    "restart discovers every canonical stream independently");
            });
        }
    }
}
