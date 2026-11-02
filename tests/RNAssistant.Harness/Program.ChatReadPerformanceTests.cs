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
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
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
    }
}
