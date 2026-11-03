using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void DiagnosticsDoNotHydrateOrRecoverHistory()
        {
            WithTempPaths(paths =>
            {
                var adapter = FakeOfficeAdapter.ForHost("Word");
                var store = new ChatStore(paths);
                var saved = store.Create(adapter.HostName, adapter.DocumentKey, adapter.DocumentTitle, "History");
                saved.DocumentAuthorityId = "diagnostic-document";
                saved.Messages.Add(new ChatMessage { Role = "user", Content = new string('x', 12000) });
                saved.LastRun = new ChatRunRecord { RunId = "unfinished", RuntimeId = "previous", Status = "running" };
                store.Save(saved);
                var canonical = File.ReadAllBytes(SessionEventFile(paths, saved));
                var guarded = new CatalogReadStore(ConversationStore(new ChatStore(paths)))
                    { ForbidFullLoads = true, ForbidHeaderReads = true };
                var service = new ChatSessionService(adapter, guarded);
                var draft = service.CreateChat("Unsaved");
                var scope = service.LoadDiagnosticsSession(saved.Id);
                AssertEqual(saved.Id, scope.Id, "diagnostics retain the addressed chat");
                AssertEqual(saved.Revision, scope.Revision, "scope carries the durable revision");
                AssertEqual(saved.DocumentAuthorityId, scope.DocumentAuthorityId, "scope preserves resource authority");
                AssertEqual(0, scope.Messages.Count, "diagnostic scope does not hydrate the transcript");
                AssertTrue(scope.LastRun == null, "diagnostic scope does not restore an executable continuation");
                AssertEqual(draft.Id, service.ActiveTransientSessionId, "diagnostics do not change selection");
                AssertEqual(draft.Id, service.LoadDiagnosticsSession(draft.Id).Id, "unsaved chat keeps its exact empty scope");
                AssertTrue(!store.IsPersisted(draft), "empty diagnostic read does not persist the draft");
                RuntimeThrows<InvalidOperationException>(() => service.LoadDiagnosticsSession("absent"));
                AssertTrue(canonical.SequenceEqual(File.ReadAllBytes(SessionEventFile(paths, saved))),
                    "viewing diagnostics does not recover or write the unfinished run");
            });
        }

        private static void UnreadableProjectionIsNotMissingChat()
        {
            WithTempPaths(paths =>
            {
                var writer = new ChatStore(paths);
                var adapter = new FakeOfficeAdapter();
                var session = writer.CreateTransient(adapter.HostName, adapter.DocumentKey, adapter.DocumentTitle, "Retained history");
                session.Messages.Add(new ChatMessage { Role = "user", Content = "Do not lose this body" });
                writer.Commit(session, new ConversationCommit(session.Messages));
                var path = SessionEventFile(paths, session);
                var seed = JObject.Parse(File.ReadAllText(path));
                // Valid event integrity, but a body the current typed reader cannot interpret.
                seed["Data"]["MutationPolicy"] = new JArray(1, 2);
                var protector = typeof(ChatStore).GetMethod("Protection", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(writer, new object[0]);
                var record = typeof(ChatStore).GetMethod("ParseSessionEvent", BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, new object[] { seed.ToString(Formatting.None) });
                seed["Hash"] = (string)typeof(ChatStore).GetMethod("ComputeHash", BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, new[] { record, protector });
                File.WriteAllText(path, seed.ToString(Formatting.None) + "\n", new UTF8Encoding(false));
                File.Delete(path + ".projection.sqlite");
                var bytes = File.ReadAllBytes(path);
                var diagnostics = new System.Collections.Generic.List<string>();
                var reader = new ChatStore(paths) { PerformanceLog = diagnostics.Add };
                AssertEqual(1, reader.ReadEvents(session.Host, session.DocumentKey, session.Id).Count,
                    "fixture source has valid event integrity");
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    Exception failure = null;
                    try { new ChatSessionService(adapter, ConversationStore(reader)).SelectSession(session.Id); }
                    catch (Exception ex) { failure = ex; }
                    Console.WriteLine("PROJECTION_FAILURE attempt=" + attempt + " builds=" + reader.SqliteProjectionFullBuildCount +
                        " error=" + failure?.Message);
                    if (failure == null || !failure.Message.Contains("CHAT_PROJECTION_UNAVAILABLE"))
                        Console.WriteLine(string.Join("\n", diagnostics));
                    AssertTrue(failure != null && failure.Message.Contains("CHAT_PROJECTION_UNAVAILABLE") &&
                        failure.Message.Contains("MutationPolicy"), "existing unreadable history reports its cause instead of not found");
                }
                AssertEqual(1L, reader.SqliteProjectionFullBuildCount, "verified source is not indexed again for a typed materialization failure");
                AssertTrue(bytes.SequenceEqual(File.ReadAllBytes(path)), "failed opening never modifies source history");
                AssertTrue(diagnostics.Any(line => line.Contains("MutationPolicy")), "screen diagnostics identify the failing field");
            });
        }

        private static void TerminalCommitOmitsUnchangedMessages()
        {
            foreach (var status in new[] { "completed", "cancelled" })
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.CreateTransient("Excel", "terminal-save", "Save.xlsx", "Save");
                for (var i = 0; i < 300; i++) session.Messages.Add(new ChatMessage
                    { Role = "assistant", Content = i + ":" + new string('x', 8192) });
                session.LastRun = new ChatRunRecord { RunId = "run", TurnId = "turn", Status = "running" };
                store.Commit(session, new ConversationCommit(session.Messages));
                var path = SessionEventFile(paths, session);
                var previousBytes = new FileInfo(path).Length;
                session.LastRun.Status = status;
                session.Messages.Last().Content = "Terminal " + status;
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var timer = Stopwatch.StartNew();
                store.Commit(session, new ConversationCommit(session.Messages));
                var elapsed = timer.ElapsedMilliseconds;
                allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                var addedBytes = new FileInfo(path).Length - previousBytes;
                var commit = store.ReadEvents(session.Host, session.DocumentKey, session.Id).Last(item => item.Type == SessionEventTypes.SessionCommit);
                var writes = ((JArray)commit.Data["Operations"]).Count(item => (string)item["Type"] == SessionOperationTypes.MessageUpdated);
                Console.WriteLine("TERMINAL_SAVE status=" + status + " writes=" + writes + " bytes=" + addedBytes +
                    " elapsedMs=" + elapsed + " allocatedBytes=" + allocated);
                AssertEqual(1, writes, "only changed presentation is rewritten; kernel fact kinds remain distinct");
                AssertTrue(addedBytes < 32768, "finalizing a large history does not append its unchanged bodies again");
                var replay = new ChatStore(paths).Load(session.Id);
                AssertEqual(status, replay.LastRun.Status, "terminal state is durable");
                AssertEqual(300, replay.Messages.Count, "all original messages remain available");
                AssertEqual(session.Messages[0].Content, replay.Messages[0].Content, "unchanged content survives cold replay");
                AssertEqual(session.Messages.Last().Content, replay.Messages.Last().Content, "changed content survives cold replay");
            });
        }

        private static void SavedSessionDoesNotRewriteSelection()
        {
            WithTempPaths(paths =>
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var writes = new CatalogReadStore(ConversationStore(store));
                var service = new ChatSessionService(adapter, writes);
                var chat = service.CreateChat("First draft");
                var message = new ChatMessage { Role = "user", Content = "Persist once" };
                chat.Messages.Add(message);
                store.Commit(chat, ConversationCommit.UserInput(message));
                service.NotifySaved(chat);
                AssertEqual(1, writes.SelectionWrites, "first persistence selects the new draft once");
                var other = store.Create(adapter.HostName, adapter.DocumentKey, adapter.DocumentTitle, "Other pane selection");
                writes.SelectionWrites = 0;
                for (var i = 0; i < 10; i++) service.NotifySaved(chat);
                AssertEqual(0, writes.SelectionWrites, "save notifications do not rewrite the active pointer");
                AssertEqual(other.Id, store.LoadActiveSessionId(adapter.HostName, adapter.DocumentKey),
                    "background persistence preserves another pane's newer selection");
                service.SelectSession(chat.Id);
                AssertEqual(1, writes.SelectionWrites, "explicit navigation still selects exactly once");
            });
        }

        private static void ChatRecordCanonicalEncodingIsStable()
        {
            // Published schema-4 canonical bytes, including escapes, non-ASCII,
            // a JSON null and numeric/date tokens. Hashing is a storage contract.
            const string data = "{\"text\":\"Русский 😀 \\\" \\\\ \\n\",\"array\":[null,true,1.25,-2,\"2026-01-02\"]}";
            foreach (var encrypted in new[] { false, true })
            foreach (var hmac in new[] { false, true })
            {
                var protector = new StorageProtector(hmac ? HistoryIntegrityModes.HmacSha256 : HistoryIntegrityModes.Sha256,
                    encrypted ? HistoryEncryptionModes.Aes256CbcHmacSha256 : HistoryEncryptionModes.None,
                    "canonical event encoding", Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
                var item = new SessionEvent
                {
                    SessionId = "session", EventId = "event", Sequence = 7,
                    CreatedUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                    Type = SessionEventTypes.AssistantChunk, PreviousHash = "previous",
                    HashAlgorithm = protector.CurrentHashAlgorithm, ProtectionKeyId = protector.KeyId,
                    Data = JObject.Parse(data)
                };
                // Exercise a record larger than the encoder buffer and surrogate pairs.
                item.Data["large"] = string.Concat(Enumerable.Repeat("ж😀\n", 20000));
                if (encrypted) item.EncryptedData = Convert.ToBase64String(new byte[9001]);
                var canonical = "{\"SchemaVersion\":4,\"SessionId\":\"session\",\"Sequence\":7,\"EventId\":\"event\"," +
                    "\"CreatedUtc\":\"2026-01-02T03:04:05.0000000Z\",\"Type\":\"assistant.chunk\",\"RunId\":null," +
                    "\"TurnId\":null,\"StepId\":null,\"PreviousHash\":\"previous\",\"HashAlgorithm\":" +
                    JsonConvert.SerializeObject(item.HashAlgorithm) + ",\"ProtectionKeyId\":" +
                    JsonConvert.SerializeObject(item.ProtectionKeyId) + ",\"Data\":" +
                    (encrypted ? "null" : item.Data.ToString(Formatting.None)) + ",\"EncryptedData\":" +
                    JsonConvert.SerializeObject(item.EncryptedData) + ",\"Payload\":null}";
                var expected = protector.ComputeEventHash(Encoding.UTF8.GetBytes(canonical), item.HashAlgorithm, item.ProtectionKeyId);
                var hashMethod = typeof(ChatStore).GetMethod("ComputeHash", BindingFlags.NonPublic | BindingFlags.Static);
                AssertEqual(expected, (string)hashMethod.Invoke(null, new object[] { item, protector }),
                    "streamed verification preserves existing SHA/HMAC canonical bytes");
                if (encrypted) item.Data = null;
                var json = JsonConvert.SerializeObject(item);
                var parseMethod = typeof(ChatStore).GetMethod("ParseSessionEvent", BindingFlags.NonPublic | BindingFlags.Static);
                var parsed = (SessionEvent)parseMethod.Invoke(null, new object[] { json });
                AssertTrue(JToken.DeepEquals(JObject.Parse(json).ToObject<SessionEvent>().Data, parsed.Data),
                    "single-pass Data parsing preserves the previous typed reader");
            }
        }

        private static void ChatRecoveryOmitsStorageUsage()
        {
            WithTempPaths(paths =>
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var session = store.Create(adapter.HostName, adapter.DocumentKey, adapter.DocumentTitle, "Metadata");
                session.DocumentPath = "C:\\Demo\\MockWorkbook.xlsx";
                store.Save(session);
                store.AppendTrace(session, SessionEventTypes.AssistantChunk, new { count = 1 },
                    "diagnostic body", "text/plain", "run", "turn", "step");
                var guard = new CatalogReadStore(ConversationStore(store)) { ForbidStorageUsage = true };
                var service = new ChatSessionService(adapter, guard);
                service.ReconcileInterruptedRuns("metadata-reader");
                service.SelectSession(session.Id);
                AssertEqual(2, guard.MetadataReads, "recovery and alias resolution request metadata only");
                var metadata = store.ListHeaders(includeStorageUsage: false).Single();
                var full = store.ListHeaders().Single();
                AssertEqual(full.Revision, metadata.Revision, "metadata has the current validated revision");
                AssertEqual(full.Title, metadata.Title, "metadata preserves the catalog identity");
                AssertEqual(0L, metadata.CasStoredByteLength, "metadata does not calculate advisory storage usage");
                AssertEqual(0, metadata.CasMissingBlobCount, "omitted usage is not reported as a missing body");
                AssertTrue(full.CasStoredByteLength > 0, "the ordinary catalog still computes current usage");
            });
        }

        private static void ChatCatalogStructuredRecordsProfile()
        {
            if (Environment.GetEnvironmentVariable("RNA_CHAT_PERF") != "1") return;
            WithTempPaths(paths =>
            {
                var writer = new ChatStore(paths);
                var session = writer.Create("Word", "structured-profile", "Profile.docx", "300 messages");
                for (var index = 0; index < 300; index++)
                    session.Messages.Add(new ChatMessage { Role = "user", Content = new string('m', 768) });
                writer.Save(session);
                // Many structured nodes, unlike the older single-string trace fixture.
                var data = new JObject { ["items"] = new JArray(Enumerable.Range(0, 1200).Select(index =>
                    new JObject { ["id"] = index, ["content"] = new string('ж', 80), ["tags"] = new JArray("a", "b") })) };
                for (var i = 0; i < 90; i++)
                    writer.AppendTrace(session, SessionEventTypes.AssistantChunk, data, null, null, "run", "turn", "step");
                var path = SessionEventFile(paths, session);
                File.Delete(path + ".projection.sqlite");
                var reader = new ChatStore(paths);
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var timer = Stopwatch.StartNew();
                reader.ListHeaders(includeStorageUsage: false);
                var coldMs = timer.ElapsedMilliseconds;
                allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                timer.Restart();
                new ChatStore(paths).ListHeaders(includeStorageUsage: false);
                var restartMs = timer.ElapsedMilliseconds;
                timer.Restart();
                reader.ListHeaders(includeStorageUsage: false);
                Console.WriteLine("PROFILE structured records messages=300 journalMiB=" +
                    (new FileInfo(path).Length / 1048576.0).ToString("F1", CultureInfo.InvariantCulture) +
                    " coldMs=" + coldMs + " allocatedMiB=" + (allocated / 1048576.0).ToString("F1", CultureInfo.InvariantCulture) +
                    " restartMs=" + restartMs + " warmMs=" + timer.ElapsedMilliseconds);
            });
        }

        private static void ChatCreationSkipsStoredHistory()
        {
            WithTempPaths(paths =>
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var old = store.Create(adapter.HostName, adapter.DocumentKey, adapter.DocumentTitle, "Existing");
                for (var i = 0; i < 300; i++) old.Messages.Add(new ChatMessage { Role = "user", Content = "history " + i });
                store.Save(old);
                var bytes = File.ReadAllBytes(SessionEventFile(paths, old));
                var reads = new CatalogReadStore(ConversationStore(store)) { ForbidFullLoads = true, ForbidHeaderReads = true };
                var service = new ChatSessionService(adapter, reads);
                var draft = service.CreateChat("Fresh draft");
                AssertEqual(0, reads.FullLoads, "fresh controller does not load the previously selected chat");
                AssertEqual(0, reads.HeaderReads, "fresh creation defers the global alias catalog");
                AssertEqual(draft.Id, service.ActiveTransientSessionId, "the replaced draft can be addressed without loading history");
                AssertEqual(draft.Id, service.GetChatSummary(draft).Id, "new chat summary needs no catalog scan");
                AssertEqual(0, draft.Messages.Count, "new chat has an empty transcript");
                AssertEqual(0, reads.SelectionWrites, "an empty draft does not replace the persisted active pointer");
                service.SetActiveSession(old);
                reads.SelectionWrites = 0;
                var persistent = service.CreatePersistentChat("Persistent new chat");
                AssertEqual(0, reads.FullLoads, "warm creation also skips the previous transcript");
                AssertEqual(0, reads.HeaderReads, "persistent creation does not enumerate other chats");
                AssertEqual(1, reads.SelectionWrites, "persistent creation selects its new chat once");
                AssertTrue(store.IsPersisted(persistent), "host-row creation remains durable");
                AssertTrue(service.ActiveTransientSessionId == null, "a persisted chat must not be removed by the next creation delta");
                var archived = service.CreateChatForDocument("Archived", "Word", "archive", "Archive.docx", "C:\\Archive.docx");
                AssertEqual("archive", archived.DocumentKey, "document creation preserves its exact target");
                AssertEqual(0, reads.HeaderReads, "archived creation needs no history catalog");
                AssertTrue(bytes.SequenceEqual(File.ReadAllBytes(SessionEventFile(paths, old))), "existing chat history is unchanged");
                reads.ForbidFullLoads = reads.ForbidHeaderReads = false;
                service.LoadAddressedSession(persistent.Id);
                AssertEqual(1, reads.MetadataReads, "the next ordinary selection still reconciles pending aliases");
            });
        }

        private static void NewChatPreservesSaveAsMigration()
        {
            WithTempPaths(paths =>
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var journal = new VbaJournalStore(paths);
                var service = new ChatSessionService(adapter, ConversationStore(store), journal);
                var old = service.CreatePersistentChat("Before Save As");
                old.Messages.Add(new ChatMessage { Role = "user", Content = "Retain this history" });
                store.Save(old);
                journal.Save(adapter.HostName, adapter.DocumentKey, adapter.DocumentTitle, "Module1", "StdModule", "Option Explicit");
                var oldKey = adapter.DocumentKey;
                adapter.DocumentKeyValue = "saved-new-chat";
                adapter.DocumentPathValue = "C:\\Demo\\Saved.xlsx";
                var created = service.CreateChat("After Save As");
                AssertEqual(adapter.DocumentKey, created.DocumentKey, "new draft belongs to the saved document");
                AssertTrue(store.Load(adapter.HostName, oldKey, old.Id) == null, "known old binding is relocated");
                var moved = store.Load(adapter.HostName, adapter.DocumentKey, old.Id);
                AssertEqual("Retain this history", moved.Messages.Single().Content, "Save As retains the previous transcript");
                AssertEqual(1, journal.List(adapter.HostName, adapter.DocumentKey).Count, "VBA evidence follows the same relocation");
                AssertEqual(created.Id, service.ActiveTransientSessionId, "migration does not reselect the previous chat");
            });
        }
    }
}
