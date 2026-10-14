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

        private static void CasGcRejectsMisplacedResourceJournals()
        {
            foreach (var scenario in new[] { "published-all", "published-view", "pending-revision", "pending-view" })
            WithTempPaths(paths =>
            {
                var blobs = new ChatBlobStore(paths);
                var authority = new ResourceAuthorityStore(paths);
                var scope = new ResourceAuthorityScopeId("conversation", "misplaced");
                var exact = new ResourceRef("rna://state/conversation/misplaced/source", "r1");
                var body = PayloadRef.FromBlob(blobs.StoreText("retained source", "text/plain"));
                var part = PayloadRef.FromBlob(blobs.StoreText("retained view part", "text/plain"));
                authority.RegisterRevision(scope, new ResourceRevisionMetadata(exact, body.Sha256, body));
                authority.RegisterView(scope, new ResourceRevisionView(exact, "index", body.Sha256,
                    body, ResourceCoverage.Whole(), new[] { part }));
                if (scenario.StartsWith("published", StringComparison.Ordinal))
                    authority.Publish(ResourceAuthorityCommit.Create(scope, 0, null,
                        new[] { new ResourceHeadChange(exact.Identity, null, ResourceHeadState.Known(exact, 1)) },
                        AuthorityCommitReason.InitialObservation));
                var orphan = blobs.StoreText("unreferenced crash leftover", "text/plain");
                var service = CasService(paths, new ChatStore(paths), new VbaJournalStore(paths), () => StorageProtector.None);
                var control = service.Audit();
                AssertTrue(control.ReachabilityComplete, scenario + ": canonical roots are complete");
                AssertEqual(2, control.ReferencedBlobCount, scenario + ": source and view part are roots");
                AssertEqual(1, control.OrphanBlobCount, scenario + ": only the crash leftover is orphaned");

                var suffix = scenario.EndsWith("view", StringComparison.Ordinal) ? ".views.jsonl" : ".revisions.jsonl";
                var moved = Directory.GetFiles(paths.ResourceAuthorityDirectory, "*.jsonl")
                    .Where(path => scenario == "published-all" || path.EndsWith(suffix, StringComparison.Ordinal))
                    .ToDictionary(path => path, File.ReadAllText);
                var nested = Path.Combine(paths.ResourceAuthorityDirectory, "misplaced", "nested");
                Directory.CreateDirectory(nested);
                foreach (var entry in moved)
                    File.Move(entry.Key, Path.Combine(nested, Path.GetFileName(entry.Key)));

                var report = service.Audit();
                AssertTrue(!report.ReachabilityComplete && !report.CanGarbageCollect,
                    scenario + ": misplaced journals block reachability and collection");
                AssertTrue(report.Issues.Any(item => item.SourceType == "resource-authority" &&
                    item.SourceId.Contains("misplaced") && item.BlocksGarbageCollection),
                    scenario + ": the misplaced source is identified");
                var result = service.Collect();
                AssertTrue(!result.Completed && result.DeletedBlobCount == 0, scenario + ": collection refuses all deletion");
                AssertEqual("retained source", blobs.ReadText(body.ToBlobReference()), scenario + ": source survives");
                AssertEqual("retained view part", blobs.ReadText(part.ToBlobReference()), scenario + ": view part survives");
                AssertTrue(File.Exists(blobs.PathFor(orphan.Sha256)), scenario + ": even the true orphan is retained");
                foreach (var entry in moved)
                {
                    var misplaced = Path.Combine(nested, Path.GetFileName(entry.Key));
                    AssertEqual(entry.Value, File.ReadAllText(misplaced), scenario + ": GC does not rewrite sources");
                    AssertTrue(!File.Exists(entry.Key), scenario + ": GC does not relocate sources");
                    File.Move(misplaced, entry.Key);
                }
                var recovered = service.Collect();
                AssertTrue(recovered.Completed && recovered.DeletedBlobCount == 1,
                    scenario + ": canonical roots allow only the true orphan to be deleted");
                AssertEqual(2, recovered.Health.ReferencedBlobCount, scenario + ": retained roots remain intact");
            });
        }

        private static void CasGcRejectsResourceJournalLinks()
        {
            foreach (var directoryLink in new[] { false, true })
            WithTempPaths(paths =>
            {
                var blobs = new ChatBlobStore(paths);
                var authority = new ResourceAuthorityStore(paths);
                var scope = new ResourceAuthorityScopeId("conversation", "linked");
                var evidence = PublishContinuitySource(authority, blobs, scope, "source", "retained linked source");
                authority.RegisterView(scope, new ResourceRevisionView(evidence.Resource, "text",
                    evidence.Payload.Sha256, evidence.Payload, ResourceCoverage.Whole()));
                var viewPath = Directory.GetFiles(paths.ResourceAuthorityDirectory, "*.views.jsonl").Single();
                var viewText = File.ReadAllText(viewPath);
                var external = Path.Combine(Path.GetTempPath(), "RNAssistant.Harness.LinkedJournals." + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(external);
                var externalView = Path.Combine(external, Path.GetFileName(viewPath));
                var link = directoryLink ? Path.Combine(paths.ResourceAuthorityDirectory, "linked") : viewPath;
                try
                {
                    File.Move(viewPath, externalView);
                    try
                    {
                        if (directoryLink) Directory.CreateSymbolicLink(link, external);
                        else File.CreateSymbolicLink(link, externalView);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is PlatformNotSupportedException)
                    { return; }
                    var orphan = blobs.StoreText("orphan blocked by linked source", "text/plain");
                    var service = CasService(paths, new ChatStore(paths), new VbaJournalStore(paths), () => StorageProtector.None);
                    var report = service.Audit();
                    AssertTrue(!report.ReachabilityComplete && report.Issues.Any(item =>
                        item.Kind == CasHealthIssueKinds.SourceUnreadable && item.SourceType == "resource-authority"),
                        "linked resource source is unreadable and blocks reachability");
                    var result = service.Collect();
                    AssertTrue(!result.Completed && result.DeletedBlobCount == 0, "linked journals block all collection");
                    AssertEqual("retained linked source", blobs.ReadText(evidence.Payload.ToBlobReference()), "referenced bytes survive");
                    AssertTrue(File.Exists(blobs.PathFor(orphan.Sha256)), "orphan is retained while a source is linked");
                    AssertEqual(viewText, File.ReadAllText(externalView), "external journal is unchanged");
                }
                finally
                {
                    if (directoryLink && Directory.Exists(link)) Directory.Delete(link);
                    else if (!directoryLink && File.Exists(link)) File.Delete(link);
                    Directory.Delete(external, true);
                }
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

        private static void CasMaintenanceRejectsReparsePointTraversal()
        {
            WithTempPaths(paths =>
            {
                var external = Path.Combine(Path.GetTempPath(), "RNAssistant.Harness.External." + Guid.NewGuid().ToString("N"));
                var link = Path.Combine(paths.ChatBlobDirectory, "aa");
                Directory.CreateDirectory(external);
                try
                {
                    try
                    {
                        Directory.CreateSymbolicLink(link, external);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is PlatformNotSupportedException)
                    {
                        return;
                    }

                    var externalBlob = Path.Combine(external, new string('a', 64) + ".blob");
                    File.WriteAllText(externalBlob, "outside-cas");
                    var service = CasService(paths, new ChatStore(paths), new VbaJournalStore(paths), () => StorageProtector.None);

                    var report = service.Audit();
                    AssertTrue(!report.ReachabilityComplete, "CAS reparse point blocks reachability");
                    AssertTrue(!report.CanGarbageCollect, "CAS reparse point blocks GC");
                    AssertTrue(report.Issues.Any(item => item.Kind == CasHealthIssueKinds.BlobUnreadable && item.BlocksGarbageCollection),
                        "CAS reparse point is reported as blocking");
                    AssertTrue(!service.Collect().Completed, "GC refuses storage reached through a reparse point");
                    AssertTrue(File.Exists(externalBlob), "GC never deletes through a reparse point");
                }
                finally
                {
                    try { if (Directory.Exists(link)) Directory.Delete(link); } catch (IOException) { }
                    if (Directory.Exists(external)) Directory.Delete(external, true);
                }
            });
        }

        private static void ManagedStorageRejectsReparseRoots()
        {
            WithTempPaths(paths =>
            {
                var external = Path.Combine(Path.GetTempPath(), "RNAssistant.Harness.ExternalRoot." + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(external);
                Directory.Delete(paths.ChatBlobDirectory);
                try
                {
                    try
                    {
                        Directory.CreateSymbolicLink(paths.ChatBlobDirectory, external);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is PlatformNotSupportedException)
                    {
                        Directory.CreateDirectory(paths.ChatBlobDirectory);
                        return;
                    }

                    var externalBlob = Path.Combine(external, new string('b', 64) + ".blob");
                    File.WriteAllText(externalBlob, "outside-managed-root");
                    var retainedChat = Path.Combine(paths.ChatDirectory, "retain-before-rejected-reset.jsonl");
                    File.WriteAllText(retainedChat, "chat-history");
                    var service = CasService(paths, new ChatStore(paths), new VbaJournalStore(paths), () => StorageProtector.None);
                    var report = service.Audit();
                    AssertTrue(!report.CanGarbageCollect, "reparse CAS root blocks GC");
                    AssertTrue(report.Issues.Any(item => item.Kind == CasHealthIssueKinds.BlobUnreadable && item.BlocksGarbageCollection),
                        "reparse CAS root is reported");

                    var ensureRejected = false;
                    try { paths.Ensure(); }
                    catch (IOException) { ensureRejected = true; }
                    AssertTrue(ensureRejected, "managed reparse root is rejected during ensure");

                    var resetRejected = false;
                    try { paths.ClearRuntimeData(); }
                    catch (IOException) { resetRejected = true; }
                    AssertTrue(resetRejected, "runtime reset refuses a managed reparse root");
                    AssertTrue(File.Exists(retainedChat), "a late invalid root cannot partially delete chat history");
                    AssertTrue(File.Exists(externalBlob), "managed operations never traverse the reparse root");

                    var deleteRoot = Path.Combine(paths.AttachmentDirectory, "safe-delete");
                    Directory.CreateDirectory(deleteRoot);
                    Directory.CreateSymbolicLink(Path.Combine(deleteRoot, "external"), external);
                    AssertTrue(StorageFileSystem.TryDeleteDirectory(deleteRoot), "safe recursive delete removes the local tree");
                    AssertTrue(File.Exists(externalBlob), "safe recursive delete never traverses a child reparse point");
                }
                finally
                {
                    try
                    {
                        if (Directory.Exists(paths.ChatBlobDirectory) && StorageFileSystem.IsReparsePoint(paths.ChatBlobDirectory))
                        {
                            Directory.Delete(paths.ChatBlobDirectory);
                        }
                    }
                    catch (IOException) { }
                    if (!Directory.Exists(paths.ChatBlobDirectory)) Directory.CreateDirectory(paths.ChatBlobDirectory);
                    if (Directory.Exists(external)) Directory.Delete(external, true);
                }
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
