using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void CasGcCollectsBlobBeforeEventOrphan()
        {
            WithTempPaths(paths =>
            {
                var chats = new ChatStore(paths);
                var vba = new VbaJournalStore(paths);
                var blobs = new ChatBlobStore(paths);
                var service = CasService(paths, chats, vba, () => StorageProtector.None);

                var session = chats.Create("Word", "cas-doc", "CAS.docx", "CAS health");
                var artifact = new ChatArtifact
                {
                    Kind = ChatArtifactKinds.Markdown,
                    Title = "Durable artifact",
                    MimeType = "text/markdown",
                    InlineText = "referenced-artifact-body"
                };
                session.Artifacts.Add(artifact);
                var attachmentBody = blobs.StoreText("referenced-attachment-body", "text/plain");
                var extractedBody = blobs.StoreText("referenced-extracted-text", "text/plain; charset=utf-8");
                var message = new ChatMessage { Role = "user", Content = "attached" };
                message.Attachments.Add(new ChatAttachment
                {
                    FileName = "reference.txt",
                    ContentType = "text/plain",
                    Size = attachmentBody.ByteLength,
                    ContentSha256 = attachmentBody.Sha256,
                    ContentByteLength = attachmentBody.ByteLength,
                    ExtractedTextSha256 = extractedBody.Sha256,
                    ExtractedTextByteLength = extractedBody.ByteLength
                });
                session.Messages.Add(message);
                chats.Save(session);
                chats.AppendTrace(session, SessionEventTypes.LlmRequest, new { Step = "request" },
                    "referenced-model-payload", "application/json", "run", "turn", "step");
                var backup = vba.Save("Word", "cas-doc", "CAS.docx", "Module1", "standard", "Sub Referenced()\nEnd Sub");

                // Crash injection: the immutable blob became durable, but its event never did.
                var orphan = blobs.StoreText("blob-before-event-crash", "text/plain");
                var orphanPath = blobs.PathFor(orphan.Sha256);
                var report = service.Audit();

                AssertTrue(report.ReachabilityComplete, "CAS reachability is complete");
                AssertEqual(1, report.ChatStreamCount, "one chat stream scanned");
                AssertEqual(1, report.VbaJournalCount, "one VBA journal scanned");
                AssertEqual(0, report.MissingBlobCount, "no missing CAS blobs");
                AssertEqual(0, report.CorruptBlobCount, "no corrupt CAS blobs");
                AssertEqual(5, report.ReferencedBlobCount, "artifact, attachment, extracted text, model, and VBA refs found");
                AssertEqual(1, report.OrphanBlobCount, "blob-before-event is an orphan");
                AssertTrue(File.Exists(orphanPath), "orphan exists before GC");

                var collected = service.Collect();
                AssertTrue(collected.Completed, "CAS GC completed");
                AssertEqual(1, collected.DeletedBlobCount, "one orphan deleted");
                AssertTrue(!File.Exists(orphanPath), "orphan deleted");
                AssertEqual(0, collected.Health.OrphanBlobCount, "post-GC audit has no orphans");

                var loaded = chats.Load(session.Host, session.DocumentKey, session.Id);
                AssertEqual("referenced-artifact-body", loaded.Artifacts.Single(item => item.Id == artifact.Id).InlineText,
                    "referenced chat artifact remains readable");
                AssertContains(vba.Find("Word", "cas-doc", backup.BackupId, null).Code, "Referenced",
                    "referenced VBA backup remains readable");
            });
        }

        private static void CasHealthReportsMissingAndCorruptBlobs()
        {
            WithTempPaths(paths =>
            {
                var chats = new ChatStore(paths);
                var vba = new VbaJournalStore(paths);
                var blobs = new ChatBlobStore(paths);
                var service = CasService(paths, chats, vba, () => StorageProtector.None);
                var session = chats.Create("Excel", "broken-cas", "Broken.xlsx", "Broken CAS");
                var artifact = new ChatArtifact
                {
                    Kind = ChatArtifactKinds.Markdown,
                    Title = "Missing",
                    MimeType = "text/plain",
                    InlineText = "missing-reference-body"
                };
                session.Artifacts.Add(artifact);
                chats.Save(session);
                var trace = chats.AppendTrace(session, SessionEventTypes.LlmResponse, new { Step = "response" },
                    "corrupt-reference-body", "application/json", "run", "turn", "step");

                File.Delete(blobs.PathFor(artifact.ContentSha256));
                File.WriteAllText(blobs.PathFor(trace.Payload.Sha256), "corrupt");
                var report = service.Audit();

                AssertTrue(report.ReachabilityComplete, "blob damage does not hide source reachability");
                AssertTrue(report.CanGarbageCollect, "GC can still distinguish unrelated proven orphans");
                AssertTrue(!report.Healthy, "damaged CAS is unhealthy");
                AssertEqual(1, report.MissingBlobCount, "missing reference reported");
                AssertEqual(1, report.CorruptBlobCount, "corrupt reference reported");
                AssertEqual(0, report.OrphanBlobCount, "referenced damage is never classified as orphan");
            });
        }

        private static void CasGcFailsClosedForInvalidSources()
        {
            WithTempPaths(paths =>
            {
                var chats = new ChatStore(paths);
                var vba = new VbaJournalStore(paths);
                var blobs = new ChatBlobStore(paths);
                var service = CasService(paths, chats, vba, () => StorageProtector.None);
                vba.Save("Excel", "journal-doc", "Journal.xlsx", "Module1", "standard", "Sub Before()\nEnd Sub");
                var orphan = blobs.StoreText("must-survive-corrupt-journal", "text/plain");
                var orphanPath = blobs.PathFor(orphan.Sha256);
                var journalPath = Path.Combine(paths.VbaJournalDirectory,
                    AppDataPaths.SafeFileName("Excel|journal-doc"), "mutations.events.jsonl");
                var lines = File.ReadAllLines(journalPath);
                var first = JObject.Parse(lines[0]);
                first["Data"]["ModuleName"] = "TamperedModule";
                lines[0] = first.ToString(Formatting.None);
                File.WriteAllLines(journalPath, lines);

                var report = service.Audit();
                AssertTrue(!report.ReachabilityComplete, "corrupt VBA journal blocks reachability");
                AssertTrue(!report.CanGarbageCollect, "corrupt VBA journal blocks GC");
                AssertTrue(report.Issues.Any(item => item.SourceType == "vba" && item.BlocksGarbageCollection),
                    "blocking VBA issue is reported");
                var result = service.Collect();
                AssertTrue(!result.Completed, "GC refuses corrupt VBA journal");
                AssertEqual(0, result.DeletedBlobCount, "GC deletes nothing on invalid source");
                AssertTrue(File.Exists(orphanPath), "orphan survives fail-closed GC");
            });

            WithTempPaths(paths =>
            {
                var chats = new ChatStore(paths);
                var vba = new VbaJournalStore(paths);
                var blobs = new ChatBlobStore(paths);
                var service = CasService(paths, chats, vba, () => StorageProtector.None);
                var session = chats.Create("Word", "tail-doc", "Tail.docx", "Tail");
                var orphan = blobs.StoreText("must-survive-incomplete-chat-tail", "text/plain");
                File.AppendAllText(SessionEventFile(paths, session), "{\"SchemaVersion\":");

                var report = service.Audit();
                AssertTrue(!report.ReachabilityComplete, "incomplete chat tail blocks reachability");
                AssertTrue(report.Issues.Any(item => item.Kind == CasHealthIssueKinds.IncompleteTail && item.SourceType == "chat"),
                    "incomplete chat tail is explicit");
                AssertTrue(!service.Collect().Completed, "GC refuses incomplete chat tail");
                AssertTrue(File.Exists(blobs.PathFor(orphan.Sha256)), "orphan survives incomplete-tail GC");
            });
        }

        private static void CasHealthScansProtectedStreams()
        {
            WithTempPaths(paths =>
            {
                var salt = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
                var protector = new StorageProtector(
                    HistoryIntegrityModes.HmacSha256,
                    HistoryEncryptionModes.Aes256CbcHmacSha256,
                    "portable history secret",
                    salt);
                var chats = new ChatStore(paths, () => protector);
                var vba = new VbaJournalStore(paths, () => protector);
                var service = CasService(paths, chats, vba, () => protector);
                var session = chats.Create("PowerPoint", "protected-cas", "Protected.pptx", "Protected");
                session.Artifacts.Add(new ChatArtifact
                {
                    Kind = ChatArtifactKinds.Markdown,
                    Title = "Encrypted",
                    MimeType = "text/plain",
                    InlineText = "encrypted-artifact-body"
                });
                chats.Save(session);
                vba.Save("PowerPoint", "protected-cas", "Protected.pptx", "Module1", "standard", "Sub Encrypted()\nEnd Sub");

                var report = service.Audit();
                AssertTrue(report.Healthy, "protected streams and CAS validate with matching key");
                AssertEqual(2, report.ReferencedBlobCount, "protected chat and VBA references discovered");

                var wrong = new StorageProtector(
                    HistoryIntegrityModes.HmacSha256,
                    HistoryEncryptionModes.Aes256CbcHmacSha256,
                    "wrong history secret",
                    salt);
                var wrongService = CasService(
                    paths,
                    new ChatStore(paths, () => wrong),
                    new VbaJournalStore(paths, () => wrong),
                    () => wrong);
                var wrongReport = wrongService.Audit();
                AssertTrue(!wrongReport.ReachabilityComplete, "wrong key blocks protected reachability");
                AssertTrue(!wrongReport.CanGarbageCollect, "wrong key blocks protected GC");
            });
        }

        private static void CasMaintenanceAlwaysUsesGate()
        {
            WithTempPaths(paths =>
            {
                var entered = 0;
                var disposed = 0;
                var checkedQuiescence = 0;
                var service = new CasMaintenanceService(
                    paths,
                    new ChatStore(paths),
                    new VbaJournalStore(paths),
                    () => StorageProtector.None,
                    () =>
                    {
                        entered += 1;
                        return new CallbackDisposable(() => disposed += 1);
                    },
                    () => checkedQuiescence += 1);

                service.Audit();
                service.Collect();
                AssertEqual(2, entered, "audit and GC enter maintenance gate");
                AssertEqual(2, checkedQuiescence, "audit and GC verify quiescence");
                AssertEqual(2, disposed, "audit and GC release maintenance gate");
            });
        }

        private static CasMaintenanceService CasService(
            AppDataPaths paths,
            ChatStore chats,
            VbaJournalStore vba,
            Func<StorageProtector> protector)
        {
            return new CasMaintenanceService(paths, chats, vba, protector, () => null, () => { });
        }

        private sealed class CallbackDisposable : IDisposable
        {
            private readonly Action _dispose;

            public CallbackDisposable(Action dispose)
            {
                _dispose = dispose;
            }

            public void Dispose()
            {
                if (_dispose != null) _dispose();
            }
        }
    }
}
