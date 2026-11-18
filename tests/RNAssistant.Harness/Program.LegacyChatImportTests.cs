using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using RNAssistant.Application.Conversations;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static ChatSession LegacyImportFixture(AppDataPaths paths, ChatStore legacy, bool workspace = false)
        {
            var registry = new DocumentAuthorityRegistry(paths);
            var session = legacy.CreateTransient("Excel", "legacy-document", "Previous document", "Previous conversation");
            if (workspace)
            {
                var connected = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "legacy-project"));
                session.Host = "Workspace"; session.DocumentKey = session.WorkspaceId = connected.WorkspaceId;
            }
            else
            {
                session.DocumentPath = Path.Combine(paths.Root, "original.xlsx");
                session.DocumentAuthorityId = registry.Resolve(session.Host, "legacy-runtime", session.DocumentPath).Id;
            }
            return session;
        }

        private static void CopyLegacyImportNativeFiles(string source, string target)
        {
            foreach (var path in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                if (path.EndsWith("index.sqlite", StringComparison.Ordinal) || path.EndsWith("-wal", StringComparison.Ordinal) ||
                    path.EndsWith("-shm", StringComparison.Ordinal)) continue;
                var destination = Path.Combine(target, Path.GetRelativePath(source, path));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)); File.Copy(path, destination, true);
            }
        }

        private static void LegacyChatImportHistory()
        {
            foreach (var encrypted in new[] { false, true }) WithTempPaths(paths => {
                var protector = encrypted ? new StorageProtector(HistoryIntegrityModes.HmacSha256,
                    HistoryEncryptionModes.Aes256CbcHmacSha256, "import-key", Enumerable.Repeat((byte)43, 16).ToArray()) : StorageProtector.None;
                var legacy = new ChatStore(paths, () => protector); var source = LegacyImportFixture(paths, legacy);
                var blobs = new ChatBlobStore(paths, () => protector);
                var buried = PayloadRef.FromBlob(blobs.StoreText("complete separately retained old result", "text/plain"));
                for (var index = 0; index < 260; index++) source.Messages.Add(new ChatMessage { Role = index % 2 == 0 ? "user" : "assistant", Content = "old-" + index + "\n\U0001f642" });
                source.Messages.Add(new ChatMessage { Role = "tool", Content = "Previous execution result", RuntimeProjection = true,
                    ResultPayload = buried, RunId = "old-run" });
                source.LastRun = new ChatRunRecord { RunId = "old-run", TurnId = "old-turn", Status = "running", Phase = "running", StartedUtc = DateTime.UtcNow };
                legacy.Save(source);
                var item = legacy.ReadLegacyRecoveryChats().Items.Single();
                var original = LegacyRecoveryBytes(paths.ChatDirectory, paths.ResourceAuthorityDirectory, paths.ChatBlobDirectory);
                var router = new ConversationJournalRouter(paths, (host, key) => "document:" + source.DocumentAuthorityId, protector);
                var result = legacy.ImportLegacyRecoveryChat(item.SourceId, item.Revision, router, new DocumentAuthorityRegistry(paths));
                AssertEqual("imported", result.Status, "explicit import publishes an ordinary native chat");
                AssertEqual(source.Id, router.ListHeaders(false).Single().Id, "original chat identity returns to the ordinary catalog");
                var chat = router.Load(source.Id);
                AssertEqual(261, chat.Messages.Count, "one import fact retains more than the normal 128-message commit limit");
                AssertTrue(chat.LastRun == null && chat.Messages.All(message => message.LegacyHistory?.SourceRevision == item.Revision),
                    "imported occurrences have provenance without resuming an old run");
                AssertTrue(!chat.Messages[0].ExcludeFromModelContext && chat.Messages.Last().ExcludeFromModelContext,
                    "plain dialogue is reusable; old execution summaries are excluded");
                AssertTrue(chat.Messages.Select(message => message.Content).SequenceEqual(source.Messages.Select(message => message.Content)), "all old text is retained in order");
                RuntimeThrows<InvalidOperationException>(() => ((IEventStore)router).ReadFinishEvidence(chat, "old-run", new string[0]));
                var page = router.ReadMessageWindow(chat.Id, 129, 3);
                AssertTrue(page.PageMessages.Select(message => message.Content).SequenceEqual(source.Messages.Skip(126).Take(3).Select(message => message.Content)),
                    "paging across chunk boundaries has no missing/duplicated occurrences");
                var nativeMessage = new ChatMessage { Role = "user", Content = "Continue from this imported history" };
                chat.Messages.Add(nativeMessage); router.Commit(chat, ConversationCommit.UserInput(nativeMessage));
                AssertEqual(262, router.Load(chat.Id).Messages.Count, "new native input can follow imported history");
                var retainedRevision = chat.Revision;
                AssertEqual("alreadyImported", legacy.ImportLegacyRecoveryChat(item.SourceId, item.Revision, router, new DocumentAuthorityRegistry(paths)).Status,
                    "retry returns the original durable receipt");
                AssertEqual(retainedRevision, router.Load(chat.Id).Revision, "retry cannot overwrite later native messages");
                RequireLegacyRecoveryBytes(original);

                var restored = AppDataPaths.CreateForRoot(Path.Combine(paths.Root, "native-only"));
                CopyLegacyImportNativeFiles(paths.JournalStorageDirectory, restored.JournalStorageDirectory);
                CopyLegacyImportNativeFiles(paths.ChatBlobDirectory, restored.ChatBlobDirectory);
                AssertEqual(0, Directory.GetFiles(restored.ChatDirectory, "*", SearchOption.AllDirectories).Length, "clean root has no legacy chats");
                JournalIndex.Repair(restored, protector, true, CancellationToken.None);
                var recovered = new ConversationJournalRouter(restored, (host, key) => "document:" + source.DocumentAuthorityId, protector);
                AssertEqual(262, recovered.Load(chat.Id).Messages.Count, "native-only replay restores complete imported history and later input");
                AssertEqual(item.Revision, recovered.ReadLegacyImport(chat.Id, item.SourceId, item.Revision).SourceRevision,
                    "receipt does not depend on legacy files");
                var scan = new CasReachabilityScan(); JournalIndex.ScanCanonical(restored, protector, scan);
                AssertTrue(scan.References.Any(value => value.Reference.Sha256 == buried.Sha256), "native GC traces embedded original snapshot CAS references");
                AssertEqual(0, scan.Issues.Count, "native-only canonical closure is complete");
                AssertTrue(router.Delete(chat.Host, chat.DocumentKey, chat.Id), "imported chat remains deletable normally");
                var deleted = router.ReadLegacyImport(chat.Id, item.SourceId, item.Revision);
                AssertTrue(deleted.TargetDeleted && deleted.Status == "alreadyImported", "receipt survives later native deletion");
                AssertTrue(legacy.ImportLegacyRecoveryChat(item.SourceId, item.Revision, router, new DocumentAuthorityRegistry(paths)).TargetDeleted && router.Load(chat.Id) == null,
                    "repeat import never resurrects a deleted target");
            });
        }

        private static void LegacyChatImportResources()
        {
            foreach (var workspace in new[] { false, true }) WithTempPaths(paths => {
                var legacy = new ChatStore(paths); var source = LegacyImportFixture(paths, legacy, workspace);
                var attachments = new AttachmentStore(paths);
                var upload = attachments.Import("retained.txt", "text/plain", Encoding.UTF8.GetBytes("the exact original attachment"), source.Id);
                var message = new ChatMessage { Role = "user", Content = "Keep the attachment", Attachments = new List<ChatAttachment> { upload } };
                source.Messages.Add(message);
                if (workspace) { attachments.CommitToCas(message); ChatMessageAttachmentService.Link(source, message); }
                else new ChatResourceIngestionService(attachments, legacy.DocumentArtifacts).CommitAndLink(source, message, 0);
                legacy.Save(source);
                var item = legacy.ReadLegacyRecoveryChats().Items.Single();
                var original = LegacyRecoveryBytes(paths.ChatDirectory, paths.ResourceAuthorityDirectory, paths.ChatBlobDirectory);
                var scope = workspace ? "workspace:" + source.WorkspaceId : "document:" + source.DocumentAuthorityId;
                var router = new ConversationJournalRouter(paths, (host, key) => scope);
                legacy.ImportLegacyRecoveryChat(item.SourceId, item.Revision, router, new DocumentAuthorityRegistry(paths));
                RequireLegacyRecoveryBytes(original);
                var restored = AppDataPaths.CreateForRoot(Path.Combine(paths.Root, "native-resources-only"));
                CopyLegacyImportNativeFiles(paths.JournalStorageDirectory, restored.JournalStorageDirectory);
                CopyLegacyImportNativeFiles(paths.ChatBlobDirectory, restored.ChatBlobDirectory);
                JournalIndex.Repair(restored, StorageProtector.None, true, CancellationToken.None);
                var native = new ConversationJournalRouter(restored, (host, key) => scope); var chat = native.Load(source.Id);
                AssertEqual(1, chat.Artifacts.Count, "native-only replay retains attachment membership");
                var projectedLibrary = ArtifactLibraryProjectionService.Project(chat);
                AssertTrue(projectedLibrary.Heads.Any(head => head.Title == "retained.txt" ||
                    head.ArtifactId == "attachment_" + upload.Id),
                    "imported resource is visible in the native Artifact Library projection");
                AssertTrue(chat.Artifacts.Single().AvailabilityIssue == null, "imported artifact metadata is available from native resources");
                AssertEqual("the exact original attachment", new AttachmentStore(restored).ReadExtractedText(chat.Messages.Single().Attachments.Single()),
                    "exact attachment CAS remains usable without legacy files");
                var page = native.ReadMessageWindow(chat.Id, 1, 1);
                AssertEqual(upload.ContentSha256, page.Artifacts.Single().ContentSha256, "normal paged cards retain original identity");
                if (!workspace)
                {
                    var resources = new ResourceJournal(restored); var owner = new DocumentArtifactStore(resources, resources, new ChatBlobStore(restored));
                    var retained = owner.Read(ConversationMetadata.From(chat), message.ResourceRefs.Single());
                    AssertEqual(upload.ContentSha256, retained.OriginalAttachment.ContentSha256, "Office artifact publication is retained without logical head rewind");
                }
                var scan = new CasReachabilityScan(); JournalIndex.ScanCanonical(restored, StorageProtector.None, scan);
                AssertTrue(scan.References.Any(value => value.Reference.Sha256 == upload.ContentSha256), "native closure roots original attachment bytes");
            });
        }

        private static void LegacyChatImportGuards()
        {
            WithTempPaths(paths => {
                var legacy = new ChatStore(paths); var source = LegacyImportFixture(paths, legacy);
                source.Messages.Add(new ChatMessage { Role = "user", Content = "historical" }); legacy.Save(source);
                var item = legacy.ReadLegacyRecoveryChats().Items.Single(); var scope = "document:" + source.DocumentAuthorityId;
                var router = new ConversationJournalRouter(paths, (host, key) => scope); var identities = new DocumentAuthorityRegistry(paths);
                RuntimeThrows<LegacyChatImportException>(() => legacy.ImportLegacyRecoveryChat(item.SourceId, item.Revision + 1, router, identities));
                var current = router.CreateTransient(source.Host, source.DocumentKey, source.DocumentTitle, "Native", source.DocumentAuthorityId);
                current.Id = source.Id; router.Commit(current, new ConversationCommit());
                RuntimeThrows<LegacyChatImportException>(() => legacy.ImportLegacyRecoveryChat(item.SourceId, item.Revision, router, identities));
                AssertEqual("Native", router.Load(source.Id).Title, "import never replaces a preexisting native chat");
                router.Delete(current.Host, current.DocumentKey, current.Id);
                var before = LegacyRecoveryBytes(paths.JournalConversationDirectory);
                RuntimeThrows<LegacyChatImportException>(() => legacy.ImportLegacyRecoveryChat(item.SourceId, item.Revision, router, identities));
                RequireLegacyRecoveryBytes(before);
                AssertTrue(router.ReadLegacyImport(source.Id, item.SourceId, item.Revision) == null, "foreign tombstone cannot become an import receipt");
            });
            WithTempPaths(paths => {
                var legacy = new ChatStore(paths); var source = LegacyImportFixture(paths, legacy);
                var attachments = new AttachmentStore(paths);
                var upload = attachments.Import("removed.txt", "text/plain", Encoding.UTF8.GetBytes("retained but removed"), source.Id);
                var message = new ChatMessage { Role = "user", Content = "Old original", Attachments = new List<ChatAttachment> { upload } };
                source.Messages.Add(message);
                new ChatResourceIngestionService(attachments, legacy.DocumentArtifacts).CommitAndLink(source, message, 0);
                legacy.Save(source);
                var scope = new ResourceAuthorityScopeId("document", source.DocumentAuthorityId);
                using (new ResourceMutationJournal(paths).AcquireScope(scope)) legacy.DocumentArtifacts.Remove(source, message.ResourceRefs.Single());
                var item = legacy.ReadLegacyRecoveryChats().Items.Single();
                var router = new ConversationJournalRouter(paths, (host, key) => scope.ToString());
                RuntimeThrows<LegacyChatImportException>(() => legacy.ImportLegacyRecoveryChat(item.SourceId, item.Revision, router, new DocumentAuthorityRegistry(paths)));
                AssertTrue(router.Load(source.Id) == null, "unsupported removed original refuses the whole chat explicitly");
                AssertTrue(new ResourceJournal(paths).GetHead(scope, message.ResourceRefs.Single().Identity) == null,
                    "import never makes a removed old attachment current in the new store");
            });
            foreach (var fault in new[] { "after_legacy_import_append", "before_legacy_import_index_commit" }) WithTempPaths(paths => {
                var legacy = new ChatStore(paths); var source = LegacyImportFixture(paths, legacy);
                source.Messages.Add(new ChatMessage { Role = "user", Content = "historical" }); legacy.Save(source);
                var item = legacy.ReadLegacyRecoveryChats().Items.Single();
                var journal = new ConversationJournal(paths, "document:" + source.DocumentAuthorityId);
                var router = new ConversationJournalRouter(paths, (host, key) => "document:" + source.DocumentAuthorityId);
                AssertTrue(router.ReadLegacyImport(source.Id, item.SourceId, item.Revision) == null, "same router caches a pre-import absence");
                journal.Fault = point => { if (point == fault) throw new IOException("lost import acknowledgement"); };
                RuntimeThrows<IOException>(() => journal.ImportLegacyChat(source, JObject.FromObject(source), item.SourceId, item.Revision, CancellationToken.None));
                journal.Fault = null;
                var receipt = router.ReadLegacyImport(source.Id, item.SourceId, item.Revision);
                AssertTrue(receipt != null && receipt.Status == "alreadyImported", "lost acknowledgement is reconciled by canonical read-only receipt");
                AssertEqual(1, ((IConversationStore)journal).Load(source.Id).Messages.Count, "index replay applies the imported transcript atomically");
                var written = journal.Journal.WrittenBytes;
                journal.ReadLegacyImport(source.Id, item.SourceId, item.Revision, CancellationToken.None);
                AssertEqual(written, journal.Journal.WrittenBytes, "receipt query never appends or reimports");
            });
        }

        private static void LegacyChatImportNativeExecution()
        {
            foreach (var highWater in new[] { 0L, 7L }) WithTempPaths(paths => {
                var legacy = new ChatStore(paths); var source = LegacyImportFixture(paths, legacy);
                source.Messages.Add(new ChatMessage { Role = "user", Content = "Previous dialogue" });
                source.ToolCallReferenceSequence = source.EvidenceReferenceSequence = highWater; legacy.Save(source);
                var item = legacy.ReadLegacyRecoveryChats().Items.Single(); var scope = "document:" + source.DocumentAuthorityId;
                var router = new ConversationJournalRouter(paths, (host, key) => scope);
                legacy.ImportLegacyRecoveryChat(item.SourceId, item.Revision, router, new DocumentAuthorityRegistry(paths));
                var journal = new ConversationJournal(paths, scope); IConversationStore store = journal; var chat = store.Load(source.Id);
                AssertEqual(highWater, chat.ToolCallReferenceSequence, "import retains the historical alias high water");
                var initial = CheckpointState("new-run", RunLifecycle.Running, 0, 0);
                chat.LastRun = new ChatRunRecord { RunId = "new-run", TurnId = initial.Summary.TurnId, StartedUtc = DateTime.UtcNow,
                    KernelState = initial, ConversationPolicy = ConversationRunPolicy.For(ChatModes.Agent) };
                ConversationRunCommit prepare(AgentRunEvent fact, ChatMessage[] appended, ChatMessage toolActivity = null)
                {
                    chat.LastRun.KernelState = AgentRunState.Apply(chat.LastRun.KernelState, fact);
                    ConversationRunProjection.Apply(chat.LastRun); chat.Messages.AddRange(appended);
                    return new ConversationRunCommit(chat, fact, appended, toolActivity);
                }
                prepare(new AgentRunEvent(AgentRunEventKind.Started, initial.Summary, limits: initial.Limits), new ChatMessage[0]).Persist(store.CommitRun, chat);
                var summary = new RunSummary("new-run", initial.Summary.TurnId, RunLifecycle.Running, new ToolCounts(), 1, 0, "", null, null);
                var call = new ToolCall("new-read", "core.read", "{\"target\":\"source.txt\"}");
                var accepted = JournalReadCall(call.Id, "new-run", "new-step");
                prepare(new AgentRunEvent(AgentRunEventKind.ResponseAccepted, summary, response:
                    new AgentResponse("execute", "Read new source.", null, new[] { call })), new[] { accepted }).Persist(store.CommitRun, chat);
                AssertEqual(highWater + 1, accepted.ToolCallReference.Ordinal, "first native tool gets a new positive alias");
                var context = new ToolExecutionContext(call, new ToolPolicySnapshot(call.Name, "read-v1",
                    new ToolPolicy(ToolEffect.Read, ToolVerification.None, false, true, new[] { ChatModes.Agent })),
                    "new-run", summary.TurnId, "new-step", DateTime.UtcNow, false, 10);
                var activity = new ChatMessage { Role = "assistant", Content = "Reading", RunId = "new-run", ExcludeFromModelContext = true,
                    Activity = new ChatActivity { Kind = "tool", RunId = "new-run", ToolCallId = call.Id, ToolId = call.Name, StepId = "new-step" } };
                prepare(new AgentRunEvent(AgentRunEventKind.ToolStarted, summary, toolContext: context), new[] { activity }, activity).Persist(store.CommitRun, chat);
                var execution = new ToolExecutionRecord(context, ToolExecutionOutcome.Ok, DateTime.UtcNow, "Read finished",
                    result: new RNAssistant.Core.Tools.Contracts.ToolResult(RNAssistant.Core.Tools.Contracts.ToolResultStatus.Ok));
                summary = new RunSummary("new-run", summary.TurnId, RunLifecycle.Running, summary.ToolCounts.Add(execution), 1, 1, "", null, null);
                activity.Content = "Finished"; activity.ExecutionProgress = ToolExecutionProgress.Capture(execution);
                prepare(new AgentRunEvent(AgentRunEventKind.ToolCompleted, summary, execution: execution), new ChatMessage[0], activity).Persist(store.CommitRun, chat);
                var restored = store.Load(chat.Id);
                var evidence = ((IEventStore)journal).ReadFinishEvidence(restored, "new-run", new[] { "E" + (highWater + 1) });
                AssertEqual(3, evidence.Facts.Count, "only the new native call/start/terminal establish finish evidence");
                AssertEqual(highWater + 1, restored.EvidenceReferenceSequence, "terminal does not reuse a historical evidence alias");
            });
        }

        private static void LegacyChatMigrationBackgroundAll()
        {
            WithCanonicalLegacyTempPaths(paths => {
                var legacy = new ChatStore(paths);
                var first = LegacyImportFixture(paths, legacy);
                var attachmentStore = new AttachmentStore(paths);
                var upload = attachmentStore.Import("legacy-material.txt", "text/plain", Encoding.UTF8.GetBytes("retained material"), first.Id);
                var firstMessage = new ChatMessage { Role = "user", Content = "first legacy message",
                    Attachments = new List<ChatAttachment> { upload } };
                first.Messages.Add(firstMessage);
                new ChatResourceIngestionService(attachmentStore, legacy.DocumentArtifacts).CommitAndLink(first, firstMessage, 0);
                legacy.Save(first);
                var authority = first.DocumentAuthorityId;
                for (var index = 1; index < 26; index++)
                {
                    var chat = legacy.CreateTransient("Excel", "legacy-document", "Previous document", "Previous conversation " + index);
                    chat.DocumentPath = Path.Combine(paths.Root, "original.xlsx");
                    chat.DocumentAuthorityId = authority;
                    chat.Messages.Add(new ChatMessage { Role = "user", Content = "legacy message " + index });
                    legacy.Save(chat);
                }
                var scope = "document:" + authority;
                var router = new ConversationJournalRouter(paths, (host, key) => scope);
                using (var service = new LegacyChatMigrationService(paths, legacy, router, new DocumentAuthorityRegistry(paths)))
                {
                    var started = service.Start();
                    AssertTrue(started.Running, "migration starts as an acknowledged background job");
                    var deadline = DateTime.UtcNow.AddSeconds(30);
                    LegacyChatMigrationStatusDto status = started;
                    while (status.Running && DateTime.UtcNow < deadline)
                    {
                        Thread.Sleep(20);
                        status = service.Status();
                    }
                    AssertTrue(!status.Running, "background migration reaches a terminal state");
                    AssertTrue(string.Equals(status.Status, "completed", StringComparison.Ordinal),
                        "all readable legacy chats are processed sequentially: " + status.Status + " — " + status.Message);
                    AssertEqual(26, status.Total, "progress reports the complete archive page total");
                    AssertEqual(26, status.Processed, "progress advances through every page");
                    AssertEqual(26, status.Imported, "ordinary native imports retain every source chat");
                    AssertEqual(0, status.Failed + status.Unknown + status.Skipped, "healthy archive has no refused or unknown outcomes");
                    AssertEqual(26, router.ListHeaders(false).Count, "background migration publishes the full native catalog");
                    var imported = router.Load(first.Id);
                    var library = ArtifactLibraryProjectionService.Project(imported);
                    AssertTrue(library.Heads.Any(head => head.Title == "legacy-material.txt" || head.ArtifactId == "attachment_" + upload.Id),
                        "background migration publishes the imported resource into the native Materials projection");
                    AssertTrue(!ConversationLeaseService.ForOffice(paths).AnyHeld(),
                        "completed background migration releases its storage activity marker");
                }
            });
        }

        private static void WithCanonicalLegacyTempPaths(Action<AppDataPaths> action)
        {
            var temp = Path.GetTempPath();
            if (temp.StartsWith("/var/", StringComparison.Ordinal)) temp = "/private" + temp;
            var root = Path.Combine(temp, "RNAssistant.LegacyMigration." + Guid.NewGuid().ToString("N"));
            try { action(AppDataPaths.CreateForRoot(root)); }
            finally
            {
                for (var attempt = 0; Directory.Exists(root); attempt++)
                {
                    try { Directory.Delete(root, true); break; }
                    catch (IOException) when (Directory.Exists(root) && attempt < 4) { Thread.Sleep(50); }
                }
            }
        }
    }
}
