using System;
using RNAssistant.Core.Agent;
using RNAssistant.Core.ModelProtocol;
using System.IO;
using System.Linq;
using System.Globalization;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void JournalTextChatConsumers()
        {
            WithTempPaths(paths => {
                var scope = "document:" + DocumentAuthorityId.Create().Id;
                var journal = new ConversationJournal(paths, scope);
                IConversationStore store = journal;
                var office = new ChatSessionService(FakeOfficeAdapter.ForHost("Word"), store);
                var session = office.CreatePersistentChat("Office text");
                var user = new ChatMessage { Role = "user", Content = "Question" };
                var answer = new ChatMessage { Role = "assistant", Content = "Answer", ReasoningContent = "Reason" };
                var firstCommit = ConversationCommit.UserInput(user);
                session.Messages.Add(user); store.Commit(session, firstCommit);
                session.Messages.Add(answer); store.Commit(session, new ConversationCommit(new[] { answer }));
                var latest = session.Revision;
                RuntimeThrows<ChatConcurrencyException>(() => store.Commit(session, firstCommit));
                AssertEqual(latest, session.Revision, "an old receipt cannot rewind the session");
                var stale = store.Load(session.Id);
                var activity = store.ListHeaders(false).Single().LastActivityUtc;
                store.SetTitle(session, "Renamed"); store.SetModel(session, "test-model"); store.SetReasoningEnabled(session, true);
                AssertEqual(activity, store.ListHeaders(false).Single().LastActivityUtc, "preferences do not change activity");
                RuntimeThrows<ChatConcurrencyException>(() => store.SetTitle(stale, "Stale writer"));
                var bodyReads = journal.ChatBodyReads;
                AssertEqual(session.Revision, store.ReadMetadata(session.Id).Revision, "metadata observes accepted revision");
                AssertEqual(2, store.ListHeaders(false).Single().MessageCount, "catalog count");
                AssertEqual(bodyReads, journal.ChatBodyReads, "metadata and headers read no bodies");
                var window = store.ReadMessageWindow(session.Id, 2, 1);
                var views = ChatCloneService.CloneMessagesForBridge(window.Metadata, window.Artifacts, window.PageMessages);
                var presentation = ArtifactLibraryProjectionService.ProjectMessagePage(window, views);
                AssertEqual("Answer", views.Single().Content, "Office bridge presentation consumes the new page");
                AssertTrue(presentation != null, "Office artifact projection accepts a text page");
                AssertEqual(1, window.StartIndex, "bounded previous page");
                AssertEqual(null, store.ReadMessageWindow("Word", "foreign", session.Id, 2, 1), "exact scope is required");
                store = new ConversationJournal(paths, scope);
                AssertEqual(session.Id, store.LoadActiveSessionId(session.Host, session.DocumentKey), "selection survives restart");
                var loaded = store.LoadOrCreateActive(session.Host, session.DocumentKey, session.DocumentTitle);
                AssertEqual("Renamed", loaded.Title, "title survives restart");
                AssertEqual("test-model", loaded.Model, "model survives restart");
                AssertTrue(loaded.ReasoningEnabled, "reasoning preference survives restart");
                AssertEqual("Reason", loaded.Messages.Last().ReasoningContent, "reasoning body survives restart");
                AssertEqual(user.Id, loaded.Messages.First().Id, "message identities survive restart");
                var next = store.CreateTransient(session.Host, session.DocumentKey, session.DocumentTitle, "Newer chat");
                store.Commit(next, new ConversationCommit());
                AssertEqual(next.Id, store.ListHeaders(false).First().Id, "catalog keeps descending update order");
                var workspaces = new WorkspaceStore(paths);
                var workspace = workspaces.Open(Path.Combine(paths.Root, "project"));
                IConversationStore workspaceStore = new ConversationJournal(paths, "workspace:" + workspace.WorkspaceId);
                var workspaceChat = workspaces.CreateSession(workspaceStore, workspace, "Workspace text");
                var text = new ChatMessage { Role = "user", Content = "Workspace question" };
                workspaceChat.Messages.Add(text); workspaceStore.Commit(workspaceChat, ConversationCommit.UserInput(text));
                AssertEqual(text.Content, workspaces.ReadMessageWindow(workspaceStore, workspace, workspaceChat.Id, 1, 1).PageMessages.Single().Content,
                    "Workspace consumer uses the same journal page");
                AssertEqual(workspace.WorkspaceId, workspaces.LoadSession(workspaceStore, workspace, workspaceChat.Id).WorkspaceId, "Workspace ownership");
                AssertEqual(1, workspaces.ListSessions(workspaceStore, workspace).Length, "Workspace catalog");
                AssertTrue(!Directory.Exists(paths.ChatDirectory) || !Directory.EnumerateFiles(paths.ChatDirectory, "*", SearchOption.AllDirectories).Any(),
                    "new Store creates no legacy chat files");
                var before = StorageJournal.Inventory(paths).SelectMany(stream => stream.Segments).ToDictionary(file => file, File.ReadAllBytes);
                RuntimeThrows<NotSupportedException>(() => store.Commit(loaded, new ConversationCommit(removed: new[] { user.Id })));
                var protocol = new ChatMessage { Role = "assistant", ProtocolMessage = true, ResponseModelAttemptId = "unaccepted",
                    ResponseProtocolVersion = ConversationResponse.ProtocolVersion,
                    Content = ModelProtocolWire.WriteAcceptedDecision("finish", "Answer ready.", "Unaccepted runtime message",
                        results: new ConversationResultClaim[0], outcome: ConversationOutcome.Completed) };
                RuntimeThrows<InvalidOperationException>(() => store.Commit(loaded, new ConversationCommit(new[] { protocol })));
                RuntimeThrows<NotSupportedException>(() => store.ListHeaders(true));
                AssertTrue(before.All(item => item.Value.SequenceEqual(File.ReadAllBytes(item.Key))), "unsupported operations do not append");
                journal.CreateChat("unconnected", "agent-card", "Agent", ConversationRunPolicy.For(ChatModes.Agent));
                RuntimeThrows<NotSupportedException>(() => store.ListHeaders(false));
                RuntimeThrows<NotSupportedException>(() => store.ReadMessageWindow("agent-card", 0, 1));
                AssertEqual("Answer", store.ReadMessageWindow(session.Id, 2, 1).PageMessages.Single().Content,
                    "an unrelated integration does not block the addressed text chat");
            });
        }

        private static void JournalTextChatRecovery()
        {
            foreach (var encrypted in new[] { false, true }) WithTempPaths(paths => {
                var protector = encrypted ? new StorageProtector(HistoryIntegrityModes.HmacSha256,
                    HistoryEncryptionModes.Aes256CbcHmacSha256, "text-fixture", Enumerable.Repeat((byte)7, 16).ToArray()) : StorageProtector.None;
                if (encrypted)
                {
                    // Synthetic key files exercise backup closure, not Windows DPAPI.
                    File.WriteAllText(paths.SettingsFile, Newtonsoft.Json.JsonConvert.SerializeObject(new AppSettings { HistoryKeySource = HistoryKeySources.CustomSecret }));
                    File.WriteAllBytes(paths.HistoryProtectionSaltFile, Enumerable.Repeat((byte)7, 16).ToArray());
                    File.WriteAllBytes(paths.HistorySecretFile, new byte[] { 1, 2, 3, 4 });
                }
                const string scope = "document:text-recovery";
                var journal = new ConversationJournal(paths, scope, protector);
                IConversationStore store = journal;
                var session = store.CreateTransient("Word", "doc", "Document", "Text");
                store.Commit(session, new ConversationCommit());
                var message = new ChatMessage { Role = "user", Content = "Accepted once" };
                session.Messages.Add(message); var commit = ConversationCommit.UserInput(message);
                journal.Journal.Fault = point => { if (point == "after_flush") throw new IOException("lost acknowledgement"); };
                RuntimeThrows<IOException>(() => store.Commit(session, commit));
                journal.Journal.Fault = null;
                var sequence = session.Revision;
                store.Commit(session, commit);
                AssertEqual(sequence, session.Revision, "same command returns its original receipt after lost acknowledgement");
                AssertEqual(1, store.Load(session.Id).Messages.Count, "lost acknowledgement does not duplicate a message");
                message.Content = "Changed retry";
                RuntimeThrows<InvalidOperationException>(() => store.Commit(session, commit));
                message.Content = "Accepted once";
                journal.Fault = point => { if (point == "before_index") throw new IOException("derived projection failed"); };
                store.SetTitle(session, "Accepted metadata"); journal.Fault = null;
                AssertEqual("Accepted metadata", store.ListHeaders(false).Single().Title, "projection catches up with durable receipt");
                var before = StorageJournal.Inventory(paths).SelectMany(stream => stream.Segments).ToDictionary(file => file, File.ReadAllBytes);
                journal.Rebuild();
                AssertTrue(before.All(item => item.Value.SequenceEqual(File.ReadAllBytes(item.Key))), "rebuild does not rewrite history");
                AssertEqual(message.Content, store.ReadMessageWindow(session.Id, 1, 1).PageMessages.Single().Content, "indexed page survives rebuild");
                var backup = Path.Combine(paths.Root, "backup");
                JournalCasService(paths, protector).BackupJournals(backup);
                var restored = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "restore"), _ => protector);
                IConversationStore recovered = new ConversationJournal(restored, scope, protector);
                AssertEqual(message.Content, recovered.Load(session.Id).Messages.Single().Content, "canonical closure restores new text facts and CAS");
                var blobs = new ChatBlobStore(paths, () => protector);
                File.Delete(blobs.PathFor(RNAssistant.Core.Tools.TextPatternEngine.Sha256(message.Content)));
                AssertEqual(session.Id, store.ReadMetadata(session.Id).Id, "missing text body does not hide metadata");
                RuntimeThrows<InvalidDataException>(() => store.ReadMessageWindow(session.Id, 1, 1));
                AssertTrue(before.All(item => item.Value.SequenceEqual(File.ReadAllBytes(item.Key))), "missing body preserves canonical history");
            });
        }

        private static void JournalTextChatCostCurve()
        {
            foreach (var count in new[] { 100, 1000, 10000 }) WithTempPaths(paths => {
                var journal = new ConversationJournal(paths, "document:text-cost"); IConversationStore store = journal;
                var session = store.CreateTransient("Word", "cost", "Cost", "Cost");
                store.Commit(session, new ConversationCommit());
                // Equal batch sizes keep the compared last metadata frame equal-sized.
                for (var start = 0; start < count; start += 25)
                {
                    var batch = Enumerable.Range(start, 25).Select(index => new ChatMessage { Id = index.ToString("D8", CultureInfo.InvariantCulture),
                        Role = "user", Content = "Shared immutable text", CreatedUtc = session.CreatedUtc }).ToArray();
                    session.Messages.AddRange(batch); store.Commit(session, new ConversationCommit(batch));
                }
                store.ReadMetadata(session.Id); store.ListHeaders(false);
                var records = journal.Journal.ReadRecords; var bytes = journal.Journal.ReadBytes;
                var bodies = journal.ChatBodyReads; var allocated = GC.GetAllocatedBytesForCurrentThread();
                store.ReadMetadata(session.Id); var header = store.ListHeaders(false).Single();
                var metadataAllocation = GC.GetAllocatedBytesForCurrentThread() - allocated;
                var metadataRecords = journal.Journal.ReadRecords - records; var metadataBytes = journal.Journal.ReadBytes - bytes;
                AssertEqual(count, header.MessageCount, "metadata count at H=" + count);
                AssertEqual(bodies, journal.ChatBodyReads, "metadata cost excludes historical bodies");
                AssertTrue(metadataRecords <= 6 && metadataBytes < 150000, "metadata cost is bounded independently of H");
                var page = store.ReadMessageWindow(session.Id, count, 8);
                AssertEqual(8L, journal.ChatBodyReads - bodies, "only requested message bodies are hydrated");
                AssertEqual((count - 8).ToString("D8", CultureInfo.InvariantCulture), page.PageMessages.First().Id, "page order");
                var message = new ChatMessage { Role = "user", Content = "One appended message" };
                session.Messages.Add(message); bodies = journal.ChatBodyReads; records = journal.Journal.ReadRecords;
                bytes = journal.Journal.ReadBytes; allocated = GC.GetAllocatedBytesForCurrentThread();
                store.Commit(session, ConversationCommit.UserInput(message));
                AssertEqual(bodies, journal.ChatBodyReads, "append reads no historical bodies");
                AssertTrue(journal.Journal.ReadRecords - records <= 8, "append metadata reads stay bounded");
                Console.WriteLine("journal-text H={0}: metadata+header records={1}, bytes={2}, allocated={3}; append records={4}, bytes={5}, allocated={6}; page bodies=8",
                    count, metadataRecords, metadataBytes, metadataAllocation, journal.Journal.ReadRecords - records,
                    journal.Journal.ReadBytes - bytes, GC.GetAllocatedBytesForCurrentThread() - allocated);
                var initial = CheckpointState("cost-run", RunLifecycle.Running, 0, 0);
                session.LastRun = new ChatRunRecord { RunId = initial.Summary.RunId, TurnId = initial.Summary.TurnId,
                    StartedUtc = DateTime.UtcNow, KernelState = initial, ConversationPolicy = ConversationRunPolicy.For(ChatModes.Agent) };
                records = journal.Journal.ReadRecords; bytes = journal.Journal.ReadBytes; bodies = journal.ChatBodyReads;
                allocated = GC.GetAllocatedBytesForCurrentThread();
                new ConversationRunCommit(session, new AgentRunEvent(AgentRunEventKind.Started, initial.Summary, limits: initial.Limits),
                    new ChatMessage[0]).Persist(store.CommitRun, session);
                AssertEqual(bodies, journal.ChatBodyReads, "kernel append hydrates no historical bodies");
                AssertTrue(journal.Journal.ReadRecords - records <= 16, "kernel metadata reads are bounded independently of H");
                Console.WriteLine("journal-runtime H={0}: records={1}, bytes={2}, allocated={3}, history bodies=0", count,
                    journal.Journal.ReadRecords - records, journal.Journal.ReadBytes - bytes, GC.GetAllocatedBytesForCurrentThread() - allocated);
            });
        }
    }
}
