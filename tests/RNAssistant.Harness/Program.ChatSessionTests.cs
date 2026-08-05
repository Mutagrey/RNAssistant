using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Storage;
using RNAssistant.Office;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Office.WebView;
using RNAssistant.Desktop;
using RNAssistant.OfficeHosts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void CreatesAndListsChatsInTempRoot()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new ChatStore(paths);
                var session = store.Create("Word", "doc-key", "Doc", "First");
                session.Messages.Add(new ChatMessage
                {
                    Role = "user",
                    Content = "hello",
                    Activity = new ChatActivity
                    {
                        Kind = "notice",
                        Title = "Stored activity",
                        Status = "completed"
                    }
                });
                store.Save(session);

                var loaded = store.Load("Word", "doc-key", session.Id);
                AssertTrue(loaded != null, "loaded session");
                AssertEqual("First", loaded.Title, "title");
                AssertEqual(1, loaded.Messages.Count, "message count");
                AssertEqual("hello", loaded.Messages[0].Content, "message content");
                AssertEqual("Stored activity", loaded.Messages[0].Activity.Title, "message activity title");

                var sessions = store.List("Word", "doc-key", "Doc");
                AssertEqual(1, sessions.Count, "document session count");
                AssertEqual(session.Id, sessions[0].Id, "session id");
                AssertEqual(session.Id, store.LoadActiveSessionId("Word", "doc-key"), "active id");
            });
        }

        private static void SkipsBrokenChatFiles()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new ChatStore(paths);
                var documentDirectory = Path.Combine(paths.ChatDirectory, AppDataPaths.SafeFileName("Excel|book"));
                Directory.CreateDirectory(documentDirectory);
                File.WriteAllText(Path.Combine(documentDirectory, "broken.json"), "{ broken");
                File.WriteAllText(Path.Combine(documentDirectory, "unsupported.json"), "{\"Id\":\"unsupported\",\"Host\":\"Excel\",\"DocumentKey\":\"book\"}");

                var session = store.Create("Excel", "book", "Book", "Good");
                var sessions = store.List("Excel", "book", "Book");
                AssertEqual(1, sessions.Count, "document session count");
                AssertEqual(session.Id, sessions[0].Id, "session id");

                var allSessions = store.List();
                AssertEqual(1, allSessions.Count, "global session count");
            });
        }

        private static void DeletesDocumentChats()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new ChatStore(paths);
                store.Create("Excel", "book-1", "Book1.xlsx", "First");
                store.Create("Excel", "book-1", "Book1.xlsx", "Second");
                store.Create("Excel", "book-2", "Book2.xlsx", "Keep");

                AssertTrue(store.DeleteDocument("Excel", "book-1"), "document directory deleted");
                AssertEqual(0, store.List("Excel", "book-1", "Book1.xlsx").Count, "document chats deleted");
                AssertEqual(1, store.List("Excel", "book-2", "Book2.xlsx").Count, "other document preserved");
            });
        }

        private static void ChatSessionServiceMigratesDocumentKey()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var service = new ChatSessionService(adapter, store);
                var session = service.LoadSession(null);
                session.Messages.Add(new ChatMessage { Role = "user", Content = "before save" });
                store.Save(session);

                adapter.DocumentKeyValue = "saved-doc";
                var migrated = service.LoadSession(null);

                AssertEqual(session.Id, migrated.Id, "migrated session id");
                AssertEqual("saved-doc", migrated.DocumentKey, "migrated document key");
                AssertEqual(1, migrated.Messages.Count, "migrated message count");
                AssertEqual(0, store.List("Excel", "doc", "Harness.xlsx").Count, "old document sessions");
                AssertEqual(1, store.List("Excel", "saved-doc", "Harness.xlsx").Count, "new document sessions");
            });
        }

        private static void ChatSessionServiceFallsBackForStaleRequestedId()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var service = new ChatSessionService(adapter, store);
                var oldSession = service.LoadSession(null);
                var oldId = oldSession.Id;
                oldSession.Messages.Add(new ChatMessage { Role = "user", Content = "old doc" });
                store.Save(oldSession);

                adapter.DocumentKeyValue = "other-doc";
                adapter.RuntimeDocumentKeyValue = "other-runtime-doc";

                var current = service.LoadSession(oldId, true);

                AssertTrue(!string.Equals(oldId, current.Id, StringComparison.OrdinalIgnoreCase), "fallback created current session");
                AssertEqual("other-doc", current.DocumentKey, "fallback document key");
                AssertEqual(0, current.Messages.Count, "fallback message count");
                AssertEqual(1, store.List("Excel", "doc", "Harness.xlsx").Count, "old document preserved");
                AssertEqual(0, store.List("Excel", "other-doc", "Harness.xlsx").Count, "empty fallback remains transient");
            });
        }

        private static void AddressedSessionLoadsExplicitChatAcrossDocuments()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var service = new ChatSessionService(adapter, store);
                var archived = store.Create("Word", "archived-doc", "Archive.docx", "Archive chat");

                var loaded = service.LoadAddressedSession(archived.Id);

                AssertEqual(archived.Id, loaded.Id, "addressed session id");
                AssertEqual("Word", loaded.Host, "addressed host");
                AssertEqual("archived-doc", loaded.DocumentKey, "addressed document key");
            });
        }

        private static void AddressedSessionDoesNotFallbackToDifferentChat()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var service = new ChatSessionService(adapter, store);
                var first = store.Create(adapter.HostName, adapter.DocumentKey, adapter.DocumentTitle, "First");
                var second = store.Create(adapter.HostName, adapter.DocumentKey, adapter.DocumentTitle, "Second");
                var removedId = first.Id;
                var activeId = second.Id;
                service.SetActiveSession(second);

                AssertTrue(store.Delete(adapter.HostName, adapter.DocumentKey, removedId), "deleted addressed chat");
                var threw = false;
                try
                {
                    service.LoadAddressedSession(removedId);
                }
                catch (InvalidOperationException ex)
                {
                    threw = ex.Message.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0;
                }

                AssertTrue(threw, "missing addressed chat rejected");
                AssertEqual(activeId, service.GetActiveSession().Id, "active chat preserved");
            });
        }

        private static void EmptyChatDraftsAreNotPersisted()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var service = new ChatSessionService(adapter, store);

                var first = service.LoadSession(null);
                var second = service.CreateChat("Another draft");

                AssertTrue(!string.Equals(first.Id, second.Id, StringComparison.OrdinalIgnoreCase), "new draft id");
                AssertEqual(ChatModes.Agent, first.Mode, "initial draft defaults to agent mode");
                AssertEqual(ChatModes.Agent, second.Mode, "new draft defaults to agent mode");
                AssertEqual(0, store.List(adapter.HostName, adapter.DocumentKey, adapter.DocumentTitle).Count, "empty drafts not persisted");
                AssertTrue(!store.IsPersisted(second), "active draft remains in memory");
                AssertEqual(second.Id, service.GetActiveSession().Id, "active draft survives list refresh");

                var draftSummaries = service.GetChatSummaries(second.Id);
                AssertEqual(1, draftSummaries.Count, "active transient draft is visible in chat tree");
                AssertEqual(ChatModes.Agent, draftSummaries[0].Mode, "visible transient draft keeps agent mode");

                var offline = service.CreateChatForDocument(
                    "Offline draft",
                    "Word",
                    "archived-doc",
                    "Archive.docx",
                    "C:\\Docs\\Archive.docx");
                AssertEqual(ChatModes.Agent, offline.Mode, "document group draft defaults to agent mode");
                AssertEqual("archived-doc", offline.DocumentKey, "document group draft uses target document");
                AssertEqual("C:\\Docs\\Archive.docx", offline.DocumentPath, "document group draft keeps document path");
            });
        }

        private static void BackgroundSaveKeepsActiveChat()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var service = new ChatSessionService(adapter, store);
                var first = store.Create(adapter.HostName, adapter.DocumentKey, adapter.DocumentTitle, "First");
                var second = store.Create(adapter.HostName, adapter.DocumentKey, adapter.DocumentTitle, "Second");
                service.SetActiveSession(second);

                first.Messages.Add(new ChatMessage { Role = "assistant", Content = "background result" });
                store.Save(first);
                service.NotifySaved(first);

                AssertEqual(second.Id, service.GetActiveSession().Id, "background save does not select chat");
                AssertEqual(second.Id, store.LoadActiveSessionId(adapter.HostName, adapter.DocumentKey), "stored active chat remains selected");
            });
        }

        private static void InterruptedRunIsRecoveredAsCancelled()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var session = store.Create(adapter.HostName, adapter.DocumentKey, adapter.DocumentTitle, "Interrupted");
                session.LastRun = new ChatRunRecord
                {
                    RunId = "old-run",
                    RuntimeId = "old-runtime",
                    Status = "running",
                    Phase = "executing",
                    StartedUtc = DateTime.UtcNow
                };
                store.Save(session);

                new ChatSessionService(adapter, store).ReconcileInterruptedRuns("new-runtime");

                var recovered = store.Load(session.Id);
                AssertEqual("cancelled", recovered.LastRun.Status, "interrupted run status");
                AssertTrue(recovered.Messages.Any(message =>
                    message.Activity != null && message.Activity.ExecutionStatus == "application_restarted"),
                    "restart diagnostic stored");
            });
        }
    }
}
