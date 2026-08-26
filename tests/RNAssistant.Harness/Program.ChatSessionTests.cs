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
        private static void JsonFileStoreWritesAtomicUtf8()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var path = Path.Combine(paths.Root, "streamed.json");
                var store = new JsonFileStore();
                store.Save(path, new Dictionary<string, string>
                {
                    { "value", "Привет " + new string('x', 100000) }
                });
                store.Save(path, new Dictionary<string, string> { { "value", "Готово" } });

                var loaded = store.Load<Dictionary<string, string>>(path, null);
                AssertEqual("Готово", loaded["value"], "streamed json overwrite");
                AssertEqual(0, Directory.GetFiles(paths.Root, "streamed.json.*.tmp").Length, "atomic temp files cleaned");
                var bytes = File.ReadAllBytes(path);
                AssertTrue(bytes.Length > 3 && !(bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf),
                    "streamed json uses utf8 without bom");
            });
        }

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
                    ResourceRefs = new List<ResourceRef>
                    {
                        new ResourceRef(ResourceUri.Create("document", "selection")),
                        new ResourceRef(ResourceUri.Create("document", "selection")),
                        new ResourceRef("not-a-resource")
                    },
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
                AssertEqual(1, loaded.Messages[0].ResourceRefs.Count, "resource refs validated and deduplicated");
                AssertTrue(!string.IsNullOrWhiteSpace(loaded.Artifacts[0].Id), "missing artifact id normalized");
                AssertTrue(loaded.Artifacts[0].RelatedArtifactIds != null, "artifact relations normalized");
                var sessions = store.List("Word", "doc-key", "Doc");
                AssertEqual(1, sessions.Count, "document session count");
                AssertEqual(session.Id, sessions[0].Id, "session id");
                AssertEqual(session.Id, store.LoadActiveSessionId("Word", "doc-key"), "active id");
            });
        }

        private static void StaleChatRevisionIsRejected()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new ChatStore(paths);
                var created = store.Create("Excel", "revision-doc", "Revision.xlsx", "Initial");
                var first = store.Load(created.Id);
                var stale = store.Load(created.Id);

                first.Title = "First writer";
                store.Save(first);
                AssertTrue(first.Revision > created.Revision, "successful save advances revision");

                stale.Title = "Stale writer";
                try
                {
                    store.Save(stale);
                    throw new InvalidOperationException("stale save unexpectedly succeeded");
                }
                catch (ChatConcurrencyException)
                {
                }

                var loaded = store.Load(created.Id);
                AssertEqual("First writer", loaded.Title, "stale writer does not overwrite newer state");
                AssertEqual(loaded.Revision, store.ListHeaders()[0].Revision, "summary revision matches chat revision");

                var staleMover = store.Load(created.Id);
                loaded.Title = "Changed before move";
                store.Save(loaded);
                try
                {
                    store.Move(staleMover, "Excel", "revision-doc-moved", "Moved.xlsx");
                    throw new InvalidOperationException("stale move unexpectedly succeeded");
                }
                catch (ChatConcurrencyException)
                {
                }
                AssertEqual("Changed before move", store.Load(created.Id).Title,
                    "stale move does not delete a newer source revision");
                AssertEqual(0, store.List("Excel", "revision-doc-moved", "Moved.xlsx").Count,
                    "stale move does not create a destination copy");
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
                AssertTrue(Directory.GetFiles(paths.ChatBlobDirectory, "*.blob", SearchOption.AllDirectories).Length > 0,
                    "shared content-addressed blobs remain valid for other sessions");
            });
        }

        private static void ChatSessionServiceMigratesDocumentKey()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var journal = new VbaJournalStore(paths);
                var service = new ChatSessionService(adapter, store, journal);
                var session = service.LoadSession(null);
                session.Messages.Add(new ChatMessage { Role = "user", Content = "before save" });
                store.Save(session);
                var backup = journal.Save("Excel", "doc", "Harness.xlsx", "Module1", "StdModule", "Option Explicit");
                var interrupted = journal.PrepareMutation(new VbaMutationPreparation
                {
                    Operation = "write",
                    Host = "Excel",
                    DocumentKey = "doc",
                    RuntimeDocumentKey = adapter.RuntimeDocumentKey,
                    DocumentTitle = "Harness.xlsx",
                    ModuleName = "PendingModule",
                    ComponentType = "StdModule",
                    BeforeExists = false,
                    IntendedAfterExists = true
                }, string.Empty, "Sub Pending()\nEnd Sub");
                var otherSession = store.Create("Excel", "doc", "Harness.xlsx", "Other running chat");
                otherSession.DocumentPath = "C:\\Demo\\MockWorkbook.xlsx";
                store.Save(otherSession);

                var runOwned = true;
                service.RunOwnershipProvider = id => runOwned && string.Equals(id, otherSession.Id, StringComparison.OrdinalIgnoreCase);
                adapter.DocumentKeyValue = "saved-doc";
                adapter.DocumentPathValue = "C:\\Demo\\SavedWorkbook.xlsx";
                var stillRunning = service.LoadSession(null);
                AssertEqual("doc", stillRunning.DocumentKey, "active run postpones document migration");
                AssertEqual(2, store.List("Excel", "doc", "Harness.xlsx").Count, "all source chats remain while another chat owns a run");

                runOwned = false;
                var migrated = service.LoadSession(null);

                AssertEqual(session.Id, migrated.Id, "migrated session id");
                AssertEqual("saved-doc", migrated.DocumentKey, "migrated document key");
                AssertTrue(migrated.PreviousDocumentKeys.Contains("doc", StringComparer.OrdinalIgnoreCase),
                    "migrated session retains the previous live-resource document key");
                AssertEqual("saved-doc", migrated.Context.DocumentKey, "migrated chat context identity");
                AssertEqual(1, migrated.Messages.Count, "migrated message count");
                AssertEqual(0, store.List("Excel", "doc", "Harness.xlsx").Count, "old document sessions");
                var migratedSessions = store.List("Excel", "saved-doc", "Harness.xlsx");
                AssertEqual(2, migratedSessions.Count, "new document sessions");
                AssertTrue(migratedSessions.All(item => item.DocumentPath == "C:\\Demo\\SavedWorkbook.xlsx"),
                    "all migrated chats use the current full path");
                AssertEqual(0, journal.List("Excel", "doc").Count, "old VBA journal moved");
                var migratedBackups = journal.List("Excel", "saved-doc");
                AssertEqual(1, migratedBackups.Count, "VBA journal follows document identity");
                AssertEqual(backup.BackupId, migratedBackups[0].BackupId, "VBA backup identity preserved");
                AssertTrue(journal.ReadEvents("Excel", "saved-doc").Any(item =>
                    item.Type == VbaJournalEventTypes.DocumentIdentityChanged), "VBA identity migration is append-only");

                var executor = new OfficeToolExecutor(
                    adapter,
                    journal,
                    new SkillStore(paths),
                    null,
                    null,
                    null,
                    paths);
                var reconciled = ListVbaComponents(executor, migrated);
                AssertTrue(reconciled.Items.Count > 0,
                    "VBA resource access reconciles interrupted mutation after identity migration");
                AssertEqual(VbaMutationStatuses.NotApplied,
                    journal.ListMutations("Excel", "saved-doc").Single(item =>
                        item.Prepared.MutationId == interrupted.MutationId).Terminal.Status,
                    "migrated open mutation closes in the new journal");
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
                adapter.DocumentPathValue = "C:\\Demo\\Other.xlsx";

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
                var active = service.LoadSession(null);
                var archived = store.Create("Word", "archived-doc", "Archive.docx", "Archive chat");

                var loaded = service.LoadAddressedSession(archived.Id);

                AssertEqual(archived.Id, loaded.Id, "addressed session id");
                AssertEqual("Word", loaded.Host, "addressed host");
                AssertEqual("archived-doc", loaded.DocumentKey, "addressed document key");
                AssertEqual(active.Id, service.GetActiveSession().Id,
                    "addressed load preserves the selected chat");
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
                adapter.DocumentPathValue = "C:\\Demo\\Other.xlsx";
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

        private static void LoadingActiveChatRefreshesPersistedState()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var service = new ChatSessionService(adapter, store);
                var session = service.LoadSession(null);
                store.Save(session);
                service.NotifySaved(session);

                var changed = store.Load(session.Id);
                changed.Title = "Changed in another window";
                store.Save(changed);

                AssertEqual("Changed in another window", service.LoadSession(session.Id).Title,
                    "addressed active chat reloads its current persisted revision");
            });
        }

        private static void ChatSessionServiceFollowsOfficeDocumentSwitches()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var current = store.Create("Excel", "doc", "MockWorkbook.xlsx", "Current chat");
                current.DocumentPath = "C:\\Demo\\MockWorkbook.xlsx";
                current.Messages.Add(new ChatMessage { Role = "user", Content = "current history A" });
                store.Save(current);
                var currentSecond = store.Create("Excel", "doc", "MockWorkbook.xlsx", "Current chat 2");
                currentSecond.DocumentPath = "C:\\Demo\\MockWorkbook.xlsx";
                currentSecond.Messages.Add(new ChatMessage { Role = "user", Content = "current history B" });
                store.Save(currentSecond);
                var forecast = store.Create("Excel", "forecast-doc", "Forecast.xlsx", "Forecast chat");
                forecast.DocumentPath = "C:\\Demo\\Forecast.xlsx";
                forecast.Messages.Add(new ChatMessage { Role = "user", Content = "forecast history A" });
                store.Save(forecast);
                var forecastSecond = store.Create("Excel", "forecast-doc", "Forecast.xlsx", "Forecast chat 2");
                forecastSecond.DocumentPath = "C:\\Demo\\Forecast.xlsx";
                forecastSecond.Messages.Add(new ChatMessage { Role = "user", Content = "forecast history B" });
                store.Save(forecastSecond);
                store.SaveActiveSessionId("Excel", "doc", current.Id);
                store.SaveActiveSessionId("Excel", "forecast-doc", forecast.Id);

                var service = new ChatSessionService(adapter, store);
                AssertEqual(current.Id, service.LoadSession(null).Id, "current document active chat");
                AssertEqual("current history A", service.GetActiveSession().Messages[0].Content,
                    "current document history is isolated");

                AssertEqual(currentSecond.Id, service.LoadSession(currentSecond.Id).Id,
                    "second chat in current document selected");
                AssertEqual("current history B", service.GetActiveSession().Messages[0].Content,
                    "second current-document history is isolated");

                AssertEqual(forecast.Id, service.LoadSession(forecast.Id).Id, "archived chat selected");
                AssertEqual(forecast.Id, service.GetActiveSessionForOfficeState().Id,
                    "poll preserves intentional archive selection");

                adapter.ActivateDocument("forecast-doc");
                AssertEqual(forecast.Id, service.GetActiveSessionForOfficeState().Id,
                    "external Office switch restores target chat");
                AssertEqual(forecastSecond.Id, service.LoadSession(forecastSecond.Id).Id,
                    "second chat in forecast document selected");
                AssertEqual("forecast history B", service.GetActiveSession().Messages[0].Content,
                    "second forecast history is isolated");

                adapter.ActivateDocument("doc");
                var restored = service.GetActiveSessionForOfficeState();
                AssertEqual(currentSecond.Id, restored.Id, "returning to document restores its last active chat");
                AssertEqual("current history B", restored.Messages[0].Content,
                    "returning to document restores only its selected history");
                AssertTrue(service.IsCurrentDocument(restored), "restored chat belongs to current document");

                adapter.ActivateDocument("forecast-doc");
                var forecastRestored = service.GetActiveSessionForOfficeState();
                AssertEqual(forecastSecond.Id, forecastRestored.Id,
                    "returning to forecast restores its last active chat");
                AssertEqual("forecast history B", forecastRestored.Messages[0].Content,
                    "returning to forecast restores only its selected history");
            });
        }

        private static void LegacyChatRebindsByFullPath()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var current = store.Create("Excel", "doc", "MockWorkbook.xlsx", "Current chat");
                current.DocumentPath = "C:\\Demo\\MockWorkbook.xlsx";
                store.Save(current);
                var legacy = store.Create("Excel", "Excel:DocumentId:lost-id", "Forecast.xlsx", "Legacy forecast chat");
                legacy.DocumentPath = "C:\\Demo\\Forecast.xlsx";
                store.Save(legacy);
                var unrelated = store.Create("Excel", "Excel:DocumentId:lost-id", "Other.xlsx", "Unrelated chat in a legacy group");
                unrelated.DocumentPath = "C:\\Demo\\Other.xlsx";
                store.Save(unrelated);
                var duplicate = store.Create("Excel", "Excel:DocumentId:another-lost-id", "Forecast.xlsx", "Another legacy chat");
                duplicate.DocumentPath = "C:\\Demo\\Forecast.xlsx";
                store.Save(duplicate);
                var pathOnly = store.Create("Excel", "Excel:Path:C:\\Demo\\Forecast.xlsx", "Forecast.xlsx", "Path-only legacy chat");

                var service = new ChatSessionService(adapter, store);
                service.LoadSession(null);
                service.LoadSession(legacy.Id);
                var catalog = (IOfficeDocumentCatalog)adapter;
                var target = catalog.ListOpenDocuments().First(document =>
                    DocumentOpenService.SamePath(document.Path, legacy.DocumentPath));
                AssertTrue(catalog.ActivateDocument(target.DocumentKey), "matching open document activated by path");

                service.RunOwnershipProvider = id => string.Equals(id, legacy.Id, StringComparison.OrdinalIgnoreCase);
                var deferred = service.LoadSession(legacy.Id);
                AssertEqual("Excel:DocumentId:lost-id", deferred.DocumentKey,
                    "running legacy chat postpones identity reconciliation");
                service.RunOwnershipProvider = null;
                var rebound = service.GetActiveSessionForOfficeState();
                AssertEqual("forecast-doc", adapter.DocumentKey, "matching document is active");
                AssertEqual(legacy.Id, rebound.Id, "requested legacy chat remains active");
                var legacyRemainder = store.List("Excel", "Excel:DocumentId:lost-id", "Other.xlsx");
                AssertEqual(1, legacyRemainder.Count, "different-path chat remains in mixed legacy group");
                AssertEqual(unrelated.Id, legacyRemainder[0].Id, "unrelated chat identity is preserved");
                AssertEqual(0, store.List("Excel", "Excel:DocumentId:another-lost-id", "Forecast.xlsx").Count,
                    "duplicate legacy document identity removed after migration");
                AssertEqual(legacy.Id, store.Load("Excel", "forecast-doc", legacy.Id).Id,
                    "legacy chat moved to live document identity");
                AssertEqual(duplicate.Id, store.Load("Excel", "forecast-doc", duplicate.Id).Id,
                    "all same-path chats merged into live document identity");
                AssertEqual(pathOnly.Id, store.Load("Excel", "forecast-doc", pathOnly.Id).Id,
                    "document-key path fallback is migrated without stored metadata");
            });
        }

        private static void UnsavedDocumentChatsStayIsolated()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                adapter.DocumentKeyValue = "Excel:Runtime:unsaved-a";
                adapter.RuntimeDocumentKeyValue = "Excel:Runtime:unsaved-a";
                adapter.DocumentPathValue = string.Empty;
                var store = new ChatStore(paths);
                var service = new ChatSessionService(adapter, store);
                var first = service.LoadSession(null);
                first.Messages.Add(new ChatMessage { Role = "user", Content = "first unsaved workbook" });
                store.Save(first);
                service.NotifySaved(first);

                adapter.DocumentKeyValue = "Excel:Runtime:unsaved-b";
                adapter.RuntimeDocumentKeyValue = "Excel:Runtime:unsaved-b";
                var second = service.GetActiveSessionForOfficeState();
                AssertTrue(first.Id != second.Id, "new unsaved workbook gets its own transient chat");
                AssertEqual("Excel:Runtime:unsaved-b", second.DocumentKey, "second unsaved workbook identity");

                adapter.DocumentKeyValue = "Excel:Runtime:unsaved-a";
                adapter.RuntimeDocumentKeyValue = "Excel:Runtime:unsaved-a";
                AssertEqual(first.Id, service.GetActiveSessionForOfficeState().Id,
                    "returning to the first unsaved workbook restores its chat");
            });
        }

        private static void InterruptedRunIsRecoveredAsUnknown()
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
                session.Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    RunId = "earlier-run",
                    Activity = new ChatActivity
                    {
                        RunId = "earlier-run",
                        Status = "running",
                        ExecutionStatus = "executing",
                        PendingId = "old-pending"
                    }
                });
                session.Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    RunId = "old-run",
                    Activity = new ChatActivity
                    {
                        RunId = "old-run",
                        ToolCallId = "call-without-result",
                        ToolId = "excel.inspect",
                        Status = "running",
                        ExecutionStatus = "executing",
                        PendingId = "pending"
                    }
                });
                session.Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = "Running tool.",
                    ProtocolMessage = true,
                    ToolCallId = "call-without-result",
                    CreatedUtc = session.LastRun.StartedUtc.AddMilliseconds(1),
                    ToolCalls = new List<LlmToolCall>
                    {
                        new LlmToolCall { Id = "call-without-result", Name = "rna_excel_read_range" }
                    }
                });
                store.Save(session);

                var registry = new ChatRunRegistry(paths);
                var service = new ChatSessionService(adapter, store)
                {
                    RunOwnershipProvider = registry.IsExternallyRunning,
                    RunRecoveryLeaseProvider = value => registry.Start(value.Id, "recovery", value)
                };
                service.ReconcileInterruptedRuns("new-runtime");

                var recovered = store.Load(session.Id);
                AssertTrue(!registry.IsRunning(session.Id), "recovery lease released");
                AssertEqual("interrupted", recovered.LastRun.Status, "interrupted run status");
                AssertTrue(recovered.Messages.Any(message =>
                    message.Activity != null && message.Activity.ExecutionStatus == "interrupted_unknown"),
                    "uncertain running activity stored");
                AssertTrue(recovered.Messages.Any(message =>
                    message.Activity != null && message.Activity.PendingId == "old-pending" &&
                    message.Activity.ExecutionStatus == "executing"),
                    "activity from another run is not rewritten");
                AssertTrue(recovered.Messages.Any(message =>
                    message.Activity != null && message.Activity.Kind == "diagnostic" &&
                    message.Activity.ExecutionStatus == "interrupted_unknown"),
                    "restart diagnostic stored");
                AssertTrue(recovered.Messages.Any(message =>
                    message.ToolCallId == "call-without-result" && message.ExcludeFromModelContext),
                    "dangling native tool call excluded from replay");
            });
        }

        private static void InterruptedRunAtSavedBoundaryPreservesProtocol()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var session = store.Create(adapter.HostName, adapter.DocumentKey, adapter.DocumentTitle, "Interrupted safely");
                session.LastRun = new ChatRunRecord
                {
                    RunId = "safe-run",
                    RuntimeId = "old-runtime",
                    Status = "running",
                    Phase = "tool_result",
                    StartedUtc = DateTime.UtcNow
                };
                session.Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    RunId = "safe-run",
                    ProtocolMessage = true,
                    ToolCallId = "safe-call",
                    ToolCalls = new List<LlmToolCall>
                    {
                        new LlmToolCall { Id = "safe-call", Name = "excel.inspect" }
                    }
                });
                session.Messages.Add(new ChatMessage
                {
                    Role = "user",
                    RunId = "safe-run",
                    ProtocolMessage = true,
                    ToolCallId = "safe-call",
                    Content = "TOOL_RESULT: {\"ok\":true}"
                });
                store.Save(session);

                var registry = new ChatRunRegistry(paths);
                var service = new ChatSessionService(adapter, store)
                {
                    RunOwnershipProvider = registry.IsExternallyRunning,
                    RunRecoveryLeaseProvider = value => registry.Start(value.Id, "recovery", value)
                };
                service.ReconcileInterruptedRuns("new-runtime");

                var recovered = store.Load(session.Id);
                AssertTrue(recovered.Messages.Where(message => message.ProtocolMessage)
                    .All(message => !message.ExcludeFromModelContext),
                    "completed tool exchange remains replayable");
                AssertTrue(recovered.Messages.Any(message =>
                    message.Activity != null && message.Activity.Kind == "diagnostic" &&
                    message.Activity.ExecutionStatus == "interrupted"),
                    "safe persisted boundary is not reported as unknown effect");
            });
        }
    }
}
