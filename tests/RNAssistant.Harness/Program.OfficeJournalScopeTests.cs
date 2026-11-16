using System;
using System.IO;
using System.Linq;
using RNAssistant.Application.Conversations;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void WithOfficeJournalScope(Action<AppDataPaths, FakeOfficeAdapter, DocumentAuthorityRegistry,
            ConversationJournalRouter, ChatSessionService> test)
        {
            WithTempPaths(paths => {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                adapter.DocumentKeyValue = "doc-a"; adapter.RuntimeDocumentKeyValue = "runtime-a";
                adapter.DocumentPathValue = Path.Combine(paths.Root, "a.xlsx");
                var identities = new DocumentAuthorityRegistry(paths);
                ChatSessionService sessions = null;
                var router = new ConversationJournalRouter(paths, (host, key) => sessions.ResolveConversationScope(host, key));
                sessions = new ChatSessionService(adapter, router, null, identities);
                test(paths, adapter, identities, router, sessions);
            });
        }

        private static void OfficeJournalAddressedScopes()
        {
            WithOfficeJournalScope((paths, adapter, identities, router, sessions) => {
                IConversationStore store = router;
                var a = sessions.CreatePersistentChat("A");
                var pathA = adapter.DocumentPathValue;
                var pathB = Path.Combine(paths.Root, "b.xlsx");
                var authorityB = identities.Resolve("Excel", "runtime-b", pathB).Id;
                var b = store.CreateTransient("Excel", "doc-b", "B", "B", authorityB);
                b.DocumentPath = pathB; store.Commit(b, new ConversationCommit());

                // UI New chat on historical B while the Office adapter remains A.
                var newB = sessions.CreatePersistentChatForDocument("B new", "Excel", "doc-b", "B", pathB);
                AssertEqual(authorityB, newB.DocumentAuthorityId, "addressed draft is created in B's authority");
                AssertTrue(newB.DocumentAuthorityId != a.DocumentAuthorityId, "B does not inherit active A authority");
                AssertEqual(pathA, identities.List().Single(item => item.AuthorityId == a.DocumentAuthorityId).Locator,
                    "creating B does not rebind A's locator");
                AssertEqual(pathB, identities.List().Single(item => item.AuthorityId == authorityB).Locator,
                    "B's original registry entry is retained");

                sessions.SelectSession(a.Id);
                AssertEqual(2, store.ListHeaders("Excel", "doc-b", "B").Count, "foreign addressed catalog resolves B");
                AssertEqual(b.Id, sessions.SelectSession(b.Id).Id, "foreign chat selection is not routed to A");
                AssertEqual(b.Id, store.LoadActiveSessionId("Excel", "doc-b"), "B selection is persisted in B journal");
                sessions.SelectSession(a.Id);
                AssertTrue(store.DeleteDocument("Excel", "doc-b"), "foreign addressed document deletion reaches B");
                AssertTrue(store.Load(b.Id) == null && store.Load(newB.Id) == null, "B chats are deleted");
                AssertTrue(store.Load(a.Id) != null, "deleting B preserves A history");

                // No saved chat exists for C yet: the explicit UI path supplies identity before CreateTransient.
                var pathC = Path.Combine(paths.Root, "c.xlsx");
                var c = sessions.CreatePersistentChatForDocument("C new", "Excel", "opaque-c", "C", pathC);
                AssertTrue(c.DocumentAuthorityId != a.DocumentAuthorityId && c.DocumentAuthorityId != authorityB,
                    "new foreign document gets its own authority before its first commit");
                AssertEqual(pathC, identities.List().Single(item => item.AuthorityId == c.DocumentAuthorityId).Locator,
                    "new document identity uses the explicitly selected path");

                RuntimeThrows<InvalidOperationException>(() => store.CreateTransient("Excel", "unknown-document", "Unknown", "Unknown"));
                var exactA = (IConversationStore)new ConversationJournal(paths, "document:" + a.DocumentAuthorityId);
                RuntimeThrows<InvalidOperationException>(() => exactA.CreateTransient("Excel", "doc-b", "B", "Bad", authorityB));
                RuntimeThrows<InvalidOperationException>(() => store.CreateTransient("Workspace", "workspace", "Workspace", "Bad", authorityB));
                AssertEqual(pathA, identities.List().Single(item => item.AuthorityId == a.DocumentAuthorityId).Locator,
                    "unknown/mismatched addresses cannot alter the active identity");
            });
        }

        private static void OfficeJournalSaveAsScopes()
        {
            WithOfficeJournalScope((paths, adapter, identities, router, sessions) => {
                IConversationStore store = router;
                adapter.DocumentKeyValue = "Excel:Runtime:unsaved";
                adapter.DocumentPathValue = string.Empty;
                var original = sessions.CreatePersistentChat("Unsaved");
                var authority = original.DocumentAuthorityId;
                var firstPath = Path.Combine(paths.Root, "first.xlsx");
                adapter.DocumentPathValue = firstPath; adapter.DocumentKeyValue = "Excel:Path:" + firstPath;
                var saved = sessions.LoadSession(null);
                AssertEqual(original.Id, saved.Id, "first save retains chat identity");
                AssertEqual(authority, saved.DocumentAuthorityId, "first save retains authority");
                AssertEqual(adapter.DocumentKeyValue, saved.DocumentKey, "first save updates document address");

                var nextPath = Path.Combine(paths.Root, "renamed.xlsx");
                adapter.DocumentPathValue = nextPath; adapter.DocumentKeyValue = "Excel:Path:" + nextPath;
                var moved = sessions.LoadSession(null);
                AssertEqual(original.Id, moved.Id, "Save As retains chat identity");
                AssertEqual(authority, moved.DocumentAuthorityId, "Save As retains authority");
                AssertEqual(nextPath, identities.List().Single(item => item.AuthorityId == authority).Locator,
                    "Save As changes only the same authority's locator");
                AssertEqual(1, store.ListHeaders(false).Count, "Save As creates no second history");

                adapter.RuntimeDocumentKeyValue = "runtime-reopened";
                ChatSessionService reopened = null;
                var restarted = new ConversationJournalRouter(paths, (host, key) => reopened.ResolveConversationScope(host, key));
                reopened = new ChatSessionService(adapter, restarted, null, new DocumentAuthorityRegistry(paths));
                var loaded = reopened.LoadSession(null);
                AssertEqual(original.Id, loaded.Id, "reopened path resolves its existing journal");
                AssertEqual(authority, loaded.DocumentAuthorityId, "restart preserves moved authority");
            });
        }

        private static void OfficeJournalGroupScopeSwitch()
        {
            WithOfficeJournalScope((paths, adapter, identities, router, sessions) => {
                IConversationStore store = router;
                var a = sessions.CreatePersistentChat("A");
                var pathB = Path.Combine(paths.Root, "b.xlsx");
                var b = sessions.CreatePersistentChatForDocument("B", "Excel", "doc-b", "B", pathB);
                var resources = new ResourceJournal(paths); var bodies = new ChatBlobStore(paths);
                Func<ChatSession, ConversationGroupService> groups = session => new ConversationGroupService(store,
                    DocumentArtifactStore.PlanScope(session), resources, resources, resources, bodies,
                    (source, execution) => router.MutationOrigin(source, execution));
                groups(a).Change(a, new ChatGroupChange { Action = ChatGroupAction.Create, Name = "A team" }, "group-a");
                groups(b).Change(b, new ChatGroupChange { Action = ChatGroupAction.Create, Name = "B team" }, "group-b");
                var service = new AgentCommunicationService(() => 2);
                service.Register(new AgentConversationScope { Key = "office",
                    Contains = session => sessions.IsCurrentDocument(session), ResolveGroups = groups,
                    ReadCatalog = () => new AgentConversationEndpoint[0] });
                AssertEqual("A team", service.Group(a, 2, new AgentGroupRequest { Action = "list" }, "list-a", null).Groups.Single().Name,
                    "first document reads its groups");
                adapter.DocumentKeyValue = "doc-b"; adapter.RuntimeDocumentKeyValue = "runtime-b"; adapter.DocumentPathValue = pathB;
                AssertEqual("B team", service.Group(b, 2, new AgentGroupRequest { Action = "list" }, "list-b", null).Groups.Single().Name,
                    "agent group owner follows the exact source after document switch");
                AssertEqual(0, service.Discover(b, 2, group: "B team").Peers.Count,
                    "group-filtered discovery uses the switched scope too");
                RuntimeThrows<AgentCommunicationException>(() => service.Group(a, 2,
                    new AgentGroupRequest { Action = "list" }, "stale-a", null));
                AssertEqual("A team", groups(a).Read(a).Single().Name, "switching group owner leaves A unchanged");
            });
        }

        private static void JournalOwnersSurviveRuntimeDataClear()
        {
            WithTempPaths(paths => {
                var router = new ConversationJournalRouter(paths, (host, key) => "document:clear-owner");
                IConversationStore store = router;
                var resources = new ResourceJournal(paths);
                var bodies = new ChatBlobStore(paths);
                var scope = new ResourceAuthorityScopeId("document", "clear-owner");
                var reference = new ResourceRef("rna://state/document/clear-owner/source", "1");
                var old = store.CreateTransient("Excel", "doc", "Document", "Before clear");
                store.Commit(old, new ConversationCommit());
                var before = PayloadRef.FromBlob(bodies.StoreText("before clear", "text/plain"));
                resources.RegisterRevision(scope, new ResourceRevisionMetadata(reference, before.Sha256, before));
                AssertTrue(store.Load("missing-before-clear") == null, "consume the router's one-time discovery path");
                AssertTrue(resources.GetRevision(scope, reference) != null, "old resource revision is readable");

                paths.ClearRuntimeData();
                AssertEqual(0, store.ListHeaders(false).Count, "same router sees no retained headers after clear");
                AssertTrue(store.Load(old.Id) == null, "cleared chat cannot be returned from cached journal owner");
                AssertTrue(resources.GetRevision(scope, reference) == null, "same resource owner sees no cleared revision");
                var fresh = store.CreateTransient("Excel", "doc", "Document", "After clear");
                store.Commit(fresh, new ConversationCommit());
                AssertEqual(fresh.Id, store.ListHeaders(false).Single().Id, "same cached scope accepts fresh history");
                AssertEqual("After clear", store.Load(fresh.Id).Title, "addressed reads find fresh index rows without discovery reset");
                var after = PayloadRef.FromBlob(bodies.StoreText("after clear", "text/plain"));
                resources.RegisterRevision(scope, new ResourceRevisionMetadata(reference, after.Sha256, after));
                AssertEqual(after.Sha256, resources.GetRevision(scope, reference).Payload.Sha256,
                    "same resource owner can register fresh canonical bytes after clear");
            });
        }
    }
}
