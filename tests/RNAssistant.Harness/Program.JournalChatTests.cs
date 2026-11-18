using System;
using RNAssistant.Core.Agent;
using RNAssistant.Core.ModelProtocol;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Globalization;
using Newtonsoft.Json.Linq;
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
                Directory.CreateDirectory(paths.ChatDirectory);
                var legacyChatPath = Path.Combine(paths.ChatDirectory, "legacy-chat.jsonl");
                var legacyResourcePath = Path.Combine(paths.ResourceAuthorityDirectory, "legacy-resource.jsonl");
                File.WriteAllText(legacyChatPath, "preserved legacy history");
                File.WriteAllText(legacyResourcePath, "preserved legacy resources");
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
                AssertEqual("preserved legacy history", File.ReadAllText(legacyChatPath), "new Store leaves legacy chat bytes untouched");
                AssertEqual("preserved legacy resources", File.ReadAllText(legacyResourcePath), "new resource owner leaves legacy resource bytes untouched");
                AssertTrue(File.Exists(paths.JournalGenerationFile), "new Store publishes an explicit generation marker");
                var before = StorageJournal.Inventory(paths).SelectMany(stream => stream.Segments).ToDictionary(file => file, File.ReadAllBytes);
                RuntimeThrows<NotSupportedException>(() => store.Commit(loaded, new ConversationCommit(removed: new[] { user.Id })));
                var protocol = new ChatMessage { Role = "assistant", ProtocolMessage = true, ResponseModelAttemptId = "unaccepted",
                    ResponseProtocolVersion = ConversationResponse.ProtocolVersion,
                    Content = ModelProtocolWire.WriteAcceptedDecision("finish", "Answer ready.", "Unaccepted runtime message",
                        results: new ConversationResultClaim[0], outcome: ConversationOutcome.Completed) };
                RuntimeThrows<InvalidOperationException>(() => store.Commit(loaded, new ConversationCommit(new[] { protocol })));
                AssertTrue(store.ListHeaders(true).All(header => header.JsonlByteLength > 0 && header.CasBlobCount > 0),
                    "native catalog exposes retained storage usage");
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

        private static void JournalTextChatRawFingerprintRebuild()
        {
            WithTempPaths(paths => {
                const string scope = "document:text-raw-fingerprint";
                var journal = new ConversationJournal(paths, scope);
                IConversationStore store = journal;
                var blobs = new ChatBlobStore(paths);
                var session = store.CreateTransient("Word", "legacy", "Document", "Legacy");
                store.Commit(session, new ConversationCommit());
                var message = new ChatMessage { Role = "user", Content = "Legacy question" };
                session.Messages.Add(message); var commit = ConversationCommit.UserInput(message);
                store.Commit(session, commit);
                var card = new ConversationCard(session.Id, session.Title, ConversationRunPolicy.ForSession(session), session.CreatedUtc,
                    address: new ConversationMetadata(session.Id, 0, session.Host, session.DocumentKey, null, scope.Substring(9)));
                var header = new ChatMessage { Id = message.Id, Role = message.Role, Content = null, CreatedUtc = message.CreatedUtc };
                var body = PayloadRef.FromBlob(blobs.StoreText(message.Content, "text/plain"));
                var context = PayloadRef.FromBlob(blobs.StoreText("{}", "application/json"));
                var fact = new ConversationChatCommit(card, session.Revision, 1,
                    new[] { new ConversationTextEntry(header, body, null) }, context, DateTime.UtcNow);
                var payload = JObject.FromObject(fact);
                AssertTrue(((JObject)((JArray)payload["Messages"])[0]["Header"]).Remove("ResourceModelReferences"),
                    "fixture omits a default collection added after the accepted frame");
                var fingerprintData = (JObject)payload.DeepClone(); fingerprintData.Remove("CreatedUtc");
                var fingerprint = RNAssistant.Core.Tools.TextPatternEngine.Sha256(fingerprintData.ToString(Newtonsoft.Json.Formatting.None));
                var tail = journal.Journal.CatchUp(null, (record, position) => { }, null);
                journal.Journal.Append(tail, "legacy-metadata", fingerprint, "chat.committed", payload);
                journal.Rebuild();
                AssertEqual("Legacy question", store.Load(session.Id).Messages.Single().Content,
                    "index rebuild validates the accepted raw payload after typed defaults are applied");
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

                // Warm the same SQLite/CAS path without retaining a loaded session.
                journal.ReadStateWindow(session.Id, "Word", "cost", ChatMessageWindowViewDto.PageSize);
                store.Load(session.Id);
                records = journal.Journal.ReadRecords; bytes = journal.Journal.ReadBytes;
                bodies = journal.ChatBodyReads; allocated = GC.GetAllocatedBytesForCurrentThread();
                var timings = new double[30];
                for (var poll = 0; poll < timings.Length; poll++)
                {
                    var timer = Stopwatch.StartNew();
                    var state = journal.ReadStateWindow(session.Id, "Word", "cost", ChatMessageWindowViewDto.PageSize);
                    timings[poll] = timer.Elapsed.TotalMilliseconds;
                    AssertEqual(count, state.MessageCount, "state poll retains the total count");
                    AssertEqual(Math.Min(count, ChatMessageWindowViewDto.PageSize), state.Window.PageMessages.Count,
                        "state poll hydrates one bounded latest page");
                    AssertEqual(Math.Max(0, count - ChatMessageWindowViewDto.PageSize), state.Window.StartIndex,
                        "state poll retains the canonical page offset");
                }
                Array.Sort(timings);
                var p50 = timings[(timings.Length - 1) / 2];
                var p95 = timings[(int)Math.Ceiling(timings.Length * 0.95) - 1];
                var pollBodies = journal.ChatBodyReads - bodies;
                var pollRecords = journal.Journal.ReadRecords - records;
                var pollBytes = journal.Journal.ReadBytes - bytes;
                var pollAllocation = GC.GetAllocatedBytesForCurrentThread() - allocated;
                AssertTrue(pollBodies <= timings.Length * (Math.Min(count, ChatMessageWindowViewDto.PageSize) + 2),
                    "state polls hydrate no earlier message page");
                AssertTrue(pollRecords <= timings.Length * 16, "state-poll journal records are bounded independently of H");
                Console.WriteLine("journal-state-poll H={0}: bodyReads={1}, records={2}, bytes={3}, allocated={4}, p50ms={5:F3}, p95ms={6:F3}",
                    count, pollBodies, pollRecords, pollBytes, pollAllocation, p50, p95);

                if (Environment.GetEnvironmentVariable("RNA_PROJECT_CHAT_COST") == "1")
                {
                    records = journal.Journal.ReadRecords; bytes = journal.Journal.ReadBytes;
                    bodies = journal.ChatBodyReads; allocated = GC.GetAllocatedBytesForCurrentThread();
                    var fullTimings = new double[30];
                    for (var poll = 0; poll < fullTimings.Length; poll++)
                    {
                        var timer = Stopwatch.StartNew();
                        var full = store.Load(session.Id);
                        fullTimings[poll] = timer.Elapsed.TotalMilliseconds;
                        AssertEqual(count, full.Messages.Count, "full-load comparison retains exact history");
                    }
                    Array.Sort(fullTimings);
                    var fullP50 = fullTimings[(fullTimings.Length - 1) / 2];
                    var fullP95 = fullTimings[(int)Math.Ceiling(fullTimings.Length * 0.95) - 1];
                    var fullBodies = journal.ChatBodyReads - bodies;
                    var fullRecords = journal.Journal.ReadRecords - records;
                    var fullBytes = journal.Journal.ReadBytes - bytes;
                    var fullAllocation = GC.GetAllocatedBytesForCurrentThread() - allocated;
                    AssertTrue(fullBodies >= count * fullTimings.Length,
                        "full-load comparison hydrates every historical body");
                    AssertTrue(count < 1000 || pollBodies * 4 < fullBodies,
                        "bounded polling removes history-proportional body hydration for long chats");
                    Console.WriteLine("journal-full-poll H={0}: bodyReads={1}, records={2}, bytes={3}, allocated={4}, p50ms={5:F3}, p95ms={6:F3}",
                        count, fullBodies, fullRecords, fullBytes, fullAllocation, fullP50, fullP95);
                }

                store.SetTitle(session, "Late metadata");
                var lateState = journal.ReadStateWindow(session.Id, "Word", "cost", ChatMessageWindowViewDto.PageSize);
                AssertEqual("Late metadata", lateState.Session.Title, "poll observes a late metadata update");
                AssertEqual(session.Revision, lateState.Session.Revision, "poll returns the exact late revision");
                AssertEqual(count, lateState.MessageCount, "late metadata does not reload history");
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
