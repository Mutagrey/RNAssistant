using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
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
                    Id = null,
                    Role = "user",
                    Content = "hello",
                    ArtifactIds = new List<string> { "artifact-a", "artifact-a", "" },
                    Activity = new ChatActivity
                    {
                        Kind = "notice",
                        Title = "Stored activity",
                        Status = "completed"
                    }
                });
                session.ContextCheckpoints.Add(new ContextCheckpoint { Id = null, ThroughMessageId = session.Messages[0].Id });
                session.Artifacts.Add(new ChatArtifact { Id = null, Kind = ChatArtifactKinds.Markdown, RelatedArtifactIds = null });
                store.Save(session);

                var loaded = store.Load("Word", "doc-key", session.Id);
                AssertTrue(loaded != null, "loaded session");
                AssertEqual("First", loaded.Title, "title");
                AssertEqual(1, loaded.Messages.Count, "message count");
                AssertEqual("hello", loaded.Messages[0].Content, "message content");
                AssertEqual("Stored activity", loaded.Messages[0].Activity.Title, "message activity title");
                AssertTrue(!string.IsNullOrWhiteSpace(loaded.Messages[0].Id), "missing message id normalized");
                AssertEqual(1, loaded.Messages[0].ArtifactIds.Count, "artifact refs deduplicated");
                AssertTrue(!string.IsNullOrWhiteSpace(loaded.Artifacts[0].Id), "missing artifact id normalized");
                AssertTrue(loaded.Artifacts[0].RelatedArtifactIds != null, "artifact relations normalized");
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
                File.WriteAllText(Path.Combine(documentDirectory, "future.json"), "{\"FormatVersion\":" + (ChatSession.CurrentFormatVersion + 1) + ",\"Messages\":{\"invalid\":true}}");

                var session = store.Create("Excel", "book", "Book", "Good");
                var serializationExceptions = 0;
                EventHandler<FirstChanceExceptionEventArgs> handler = delegate(object sender, FirstChanceExceptionEventArgs args)
                {
                    if (args.Exception is JsonSerializationException)
                    {
                        serializationExceptions++;
                    }
                };
                AppDomain.CurrentDomain.FirstChanceException += handler;
                IReadOnlyList<ChatSession> sessions;
                IReadOnlyList<ChatSession> allSessions;
                try
                {
                    sessions = store.List("Excel", "book", "Book");
                    allSessions = store.List();
                }
                finally
                {
                    AppDomain.CurrentDomain.FirstChanceException -= handler;
                }

                AssertEqual(1, sessions.Count, "document session count");
                AssertEqual(session.Id, sessions[0].Id, "session id");
                AssertEqual(1, allSessions.Count, "global session count");
                AssertEqual(1, store.ListHeaders().Count, "broken chats excluded from summary index");
                AssertEqual(1, Directory.GetFiles(documentDirectory, "*.summary.json").Length, "unsupported chats are not indexed");
                AssertEqual(0, serializationExceptions, "unsupported chat files are skipped before deserialization");
            });
        }

        private static void ChatSummaryIndexTracksSessionLifecycle()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new ChatStore(paths);
                var session = store.Create("Word", "doc-key", "Doc.docx", "Indexed chat");
                session.DocumentPath = "C:\\Docs\\Doc.docx";
                session.Messages.Add(new ChatMessage { Role = "user", Content = "visible" });
                session.Messages.Add(new ChatMessage { Role = "assistant", Content = "protocol", ProtocolMessage = true });
                session.HtmlWorkspace.Files.Add(new HtmlWorkspaceFile { Id = "index", Path = "index.html", Kind = "html", Content = "<h1>Hi</h1>" });
                session.HtmlWorkspace.DataSources.Add(new HtmlWorkspaceDataSource { Id = "data", Name = "data", Json = "{}" });
                session.LastRun = new ChatRunRecord
                {
                    RunId = "run-1",
                    RuntimeId = "runtime-1",
                    Status = "running",
                    Phase = "executing",
                    StartedUtc = DateTime.UtcNow
                };
                store.Save(session);

                var headers = store.ListHeaders();
                AssertEqual(1, headers.Count, "indexed header count");
                AssertEqual("Indexed chat", headers[0].Title, "indexed title");
                AssertEqual(1, headers[0].MessageCount, "indexed visible message count");
                AssertEqual(1, headers[0].HtmlFileCount, "indexed html file count");
                AssertEqual(1, headers[0].HtmlDataSourceCount, "indexed data source count");
                AssertEqual("runtime-1", headers[0].RunRuntimeId, "indexed run runtime");

                var oldDirectory = Path.Combine(paths.ChatDirectory, AppDataPaths.SafeFileName("Word|doc-key"));
                var sessionPath = Directory.GetFiles(oldDirectory, "*.json").Single(path => !ChatIndexStore.IsSidecarPath(path));
                var sidecarPath = ChatIndexStore.SidecarPath(sessionPath);
                AssertTrue(File.Exists(sidecarPath), "summary sidecar created");

                File.Delete(sidecarPath);
                AssertEqual(1, store.ListHeaders().Count, "missing sidecar rebuilt");
                AssertTrue(File.Exists(sidecarPath), "rebuilt sidecar persisted");

                File.WriteAllText(sidecarPath, "{ broken");
                AssertEqual("Indexed chat", store.ListHeaders()[0].Title, "broken sidecar rebuilt");
                AssertTrue(JObject.Parse(File.ReadAllText(sidecarPath)) != null, "rebuilt sidecar is valid json");

                var root = JObject.Parse(File.ReadAllText(sessionPath));
                root["Title"] = "Externally changed";
                File.WriteAllText(sessionPath, root.ToString(Formatting.Indented));
                headers = store.ListHeaders();
                AssertEqual("Externally changed", headers[0].Title, "stale sidecar refreshed");

                var moved = store.Move(store.Load(session.Id), "Word", "moved-doc", "Moved.docx");
                AssertEqual("moved-doc", moved.DocumentKey, "moved session key");
                AssertTrue(!File.Exists(sessionPath), "old session removed after move");
                AssertTrue(!File.Exists(sidecarPath), "old sidecar removed after move");
                AssertEqual("moved-doc", store.Load(session.Id).DocumentKey, "indexed global lookup after move");

                AssertTrue(store.Delete("Word", "moved-doc", session.Id), "indexed chat deleted");
                AssertEqual(0, store.ListHeaders().Count, "deleted chat removed from index");
            });
        }

        private static void DeletesDocumentChats()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new ChatStore(paths);
                var deletedWithArtifact = store.Create("Excel", "book-1", "Book1.xlsx", "First");
                store.Create("Excel", "book-1", "Book1.xlsx", "Second");
                var keptWithArtifact = store.Create("Excel", "book-2", "Book2.xlsx", "Keep");
                HtmlArtifactToolExecutor.UpsertFile(deletedWithArtifact, "index.html", "html", "delete", true);
                HtmlWorkspaceArtifactService.CaptureCurrent(deletedWithArtifact, "Delete");
                store.Save(deletedWithArtifact);
                HtmlArtifactToolExecutor.UpsertFile(keptWithArtifact, "index.html", "html", "keep", true);
                HtmlWorkspaceArtifactService.CaptureCurrent(keptWithArtifact, "Keep");
                store.Save(keptWithArtifact);

                AssertTrue(store.DeleteDocument("Excel", "book-1"), "document directory deleted");
                AssertEqual(0, store.List("Excel", "book-1", "Book1.xlsx").Count, "document chats deleted");
                AssertEqual(1, store.List("Excel", "book-2", "Book2.xlsx").Count, "other document preserved");
                AssertTrue(!Directory.Exists(BodyDirectory(paths, deletedWithArtifact.Id)), "deleted document artifact bodies removed");
                AssertTrue(Directory.Exists(BodyDirectory(paths, keptWithArtifact.Id)), "other document artifact bodies preserved");
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

        private static void AddressedTransientSessionSurvivesDocumentSwitch()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var service = new ChatSessionService(adapter, store);
                var draft = service.LoadSession(null);

                adapter.DocumentKeyValue = "other-doc";
                adapter.RuntimeDocumentKeyValue = "other-runtime-doc";
                var loaded = service.LoadAddressedSession(draft.Id);

                AssertEqual(draft.Id, loaded.Id, "addressed transient id");
                AssertEqual("doc", loaded.DocumentKey, "transient keeps original document");
                AssertTrue(!service.IsCurrentDocument(loaded), "transient is not rebound to another document");
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

                var archived = service.CreateChatForDocument(
                    "Archived draft",
                    "Word",
                    "archived-doc",
                    "Archive.docx",
                    "C:\\Docs\\Archive.docx");
                AssertEqual(ChatModes.Agent, archived.Mode, "document group draft defaults to agent mode");
                AssertEqual("archived-doc", archived.DocumentKey, "document group draft uses target document");
                AssertEqual("C:\\Docs\\Archive.docx", archived.DocumentPath, "document group draft keeps document path");
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
