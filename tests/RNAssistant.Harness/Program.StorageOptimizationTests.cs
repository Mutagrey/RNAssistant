using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Core.ModelProtocol;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Runtime;
using RNAssistant.Runtime.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Application.Conversations;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void RetainedResultHostParity()
        {
            WithTempPaths(paths => {
                var source = new string('q', 12000) + "SOURCE_END";
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "workspace"));
                var file = Path.Combine(workspace.RootPath, "source.txt"); File.WriteAllText(file, source, new UTF8Encoding(false));
                var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start();
                var port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
                ChatMessage retained = null;
                ChatMessage retainedCall = null;
                using (var listener = new HttpListener())
                using (var service = new WorkspaceConversationService(paths))
                {
                    listener.Prefixes.Add("http://127.0.0.1:" + port + "/"); listener.Start();
                    var settings = new AppSettings { BaseUrl = "http://127.0.0.1:" + port, Model = "test-model",
                        StreamResponses = false, SmartChatTitles = false, AutoCompressContext = false,
                        AutoConfirmToolActions = true, ContextWindowOverrideTokens = 65536,
                        RequestTimeoutSeconds = 5, MaxAgentFormatRetries = 0 };
                    var chat = service.CreateSession(workspace, "Retained sources");
                    var calls = 0;
                    var server = Task.Run(async () => {
                        try
                        {
                            while (calls < 9)
                            {
                                var request = await listener.GetContextAsync().ConfigureAwait(false);
                                string wire;
                                using (var reader = new StreamReader(request.Request.InputStream)) wire = await reader.ReadToEndAsync().ConfigureAwait(false);
                                var number = ++calls;
                                string response;
                                if (number == 1 || number == 2)
                                    response = ModelProtocolWire.WriteAcceptedCall("Read an exact view.", "", new ConversationToolCall {
                                        Name = "core.read", Arguments = number == 1 ? new JObject { ["target"] = "source.txt" } :
                                            new JObject { ["target"] = "source.txt", ["startChar"] = 0, ["charCount"] = 128 } });
                                else if (number == 3) response = LoadToolSchemaResponse("core.write");
                                else if (number == 4 || number == 7)
                                    response = ScriptedVerifiedFinish(new ChatStore(paths).Load(chat.Id), "Report the result.", "Done.",
                                        "Exact read/result recorded.", number == 4 ? new[] { "core.read", "core.read" } : new[] { "core.write" });
                                else if (number == 5)
                                {
                                    AssertContains(wire, "SOURCE_END", "new run receives retained whole bytes while live file is locked");
                                    response = ModelProtocolWire.WriteAcceptedDecision("finish", "Use the delivered retained text.", "Retained text is available.",
                                        outcome: ConversationOutcome.Completed, results: new ConversationResultClaim[0]);
                                }
                                else if (number == 6 || number == 8)
                                {
                                    AssertContains(wire, number == 6 ? "SOURCE_END" : "VERIFIED_NEW", "mutation request receives exact prior-run source");
                                    response = ModelProtocolWire.WriteAcceptedCall("Replace the delivered complete source.", "", new ConversationToolCall {
                                        Name = "core.write", Arguments = new JObject { ["target"] = "source.txt", ["content"] = number == 6 ? "VERIFIED_NEW" : "MUST_NOT_WRITE" } });
                                }
                                else response = ModelProtocolWire.WriteAcceptedDecision("finish", "The source changed externally.", "Blocked by a source conflict.",
                                    outcome: ConversationOutcome.UnableToComplete, results: new ConversationResultClaim[0]);
                                await SteeringReply(request, response).ConfigureAwait(false);
                            }
                        }
                        catch (HttpListenerException) { }
                        catch (ObjectDisposedException) { }
                        catch (Exception error) { Console.WriteLine(error); throw; }
                    });
                    try
                    {
                        Action<string> submit = text => {
                            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
                                service.SubmitInputAsync(workspace,
                                    new WorkspaceSubmitInputRequest { SessionId = chat.Id, Message = text, Mode = ChatModes.Agent }, settings,
                                    () => "fixture-key", cancellationToken: timeout.Token).GetAwaiter().GetResult();
                        };
                        submit("Read the complete source, then a short excerpt.");
                        AssertEqual(4, calls, "initial read sequence uses no model retries");
                        retained = service.GetSession(workspace, chat.Id).Messages.First(message => message.ResultBody?.Kind == ToolResultBodyKind.ResourceText &&
                            message.ResourceEvidence.Any(evidence => evidence.Complete));
                        retainedCall = service.GetSession(workspace, chat.Id).Messages.Single(message =>
                            message.AcceptedCallOrigin != null && message.ToolCallId == retained.ToolCallId);
                        AssertTrue(!retained.Content.Contains("SOURCE_END"), "Workspace retains compact occurrence metadata");
                        using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None)) submit("Use the retained text.");
                        AssertEqual(5, calls, "retained request does not refresh the live file");
                        submit("Replace the source using its delivered whole view.");
                        AssertEqual("VERIFIED_NEW", File.ReadAllText(file), "a later partial read and new run do not erase delivered whole authority");
                        File.WriteAllText(file, "EXTERNAL_CHANGE", new UTF8Encoding(false));
                        submit("Replace the source again.");
                        AssertEqual("EXTERNAL_CHANGE", File.ReadAllText(file), "live precondition refuses changed source without overwriting it");
                        var terminal = service.GetSession(workspace, chat.Id).Messages.Last(message => message.ProtocolMessage &&
                            message.ToolName == "core.write" && message.Role == "user");
                        AssertContains(ToolResultPersistence.ReadContent(terminal, new ChatBlobStore(paths)), "target_conflict", "conflict is explicit");
                        AssertEqual(9, calls, "there are no tool/model retries to restore per-run guards");
                    }
                    finally { listener.Stop(); AssertTrue(server.Wait(10000), "scripted provider stops"); }
                }
                WithTempExecutor((executor, adapter) => {
                    var office = NewSession(adapter);
                    office.Messages.Add(RNAssistant.Core.Llm.HistoricalContextProjector.Project(retainedCall));
                    office.LastRun = new ChatRunRecord { RunId = retained.RunId, TurnId = "office-initial" };
                    var metadata = retained.ResultBody;
                    var result = new ToolResultMaterialization(ToolResult.OkRead(metadata.Message,
                        metadata.Read.CopyText(source), retained.ResourceRefs), resourceEvidence: retained.ResourceEvidence)
                        { ExecutionProgress = retained.ExecutionProgress };
                    var observation = retained.ResourceEvidence.Single();
                    var payload = PayloadRef.FromBlob(executor.Payloads.StoreText(source, "text/plain"));
                    executor.ResourceAuthority.Revisions.RegisterRevision(observation.ScopeId,
                        new ResourceRevisionMetadata(observation.Resource, payload.Sha256, payload));
                    var head = ResourceHeadState.Known(observation.Resource, 1);
                    executor.ResourceAuthority.Store.Publish(ResourceAuthorityCommit.Create(observation.ScopeId, 0, null,
                        new[] { new ResourceHeadChange(head.Identity, null, head) }, AuthorityCommitReason.InitialObservation));
                    var catalog = executor.GetControllerTools().ToList();
                    var generation = executor.ResourceAuthority.Store.Capture(CatalogPublicationService.ScopeId).Generation;
                    var store = new ChatStore(FixturePaths.Value); store.Save(office);
                    using (var model = ConversationModelSession.CreateAsync(adapter, null, null, EventStore(store), ChatModes.Agent,
                        "Read source.", office, NewContext(adapter), new AppSettings { AutoCompressContext = false, ContextWindowOverrideTokens = 65536 }, catalog,
                        null, null, false, null, CancellationToken.None, authority: executor.ResourceAuthority,
                        payloads: executor.Payloads, catalogGeneration: generation).GetAwaiter().GetResult())
                    {
                        model.AppendConfirmedResult(new ToolInvocation { ToolCallId = retained.ToolCallId, ToolId = retained.ToolName,
                            RuntimeStepId = retained.ResultStepId }, result);
                        office.LastRun = new ChatRunRecord { RunId = "office-next-run", TurnId = "office-next-turn" };
                        var request = model.CreateRequest("next-request", new ModelProtocolCallContext(new string[0]));
                        AssertContains(string.Join("\n", request.AcceptedMessages.Select(message => message.Content)), "SOURCE_END", "Office next run sends the retained source");
                        var delivery = new ChatMessage(); model.AttachResponseEvidence(delivery);
                        AssertTrue(delivery.ResourceEvidence.Any(item => item.Complete && item.Resource.Uri == observation.Resource.Uri),
                            "Office actual request publishes complete delivery evidence");
                    }
                    var actual = office.Messages.Last(message => message.ToolCallId == retained.ToolCallId && message.Role == "user");
                    AssertEqual(retained.ResultPayload.Sha256, actual.ResultPayload.Sha256, "real Office and Workspace adapters share body identity");
                    AssertEqual(retained.Content, actual.Content, "real adapters persist the same compact result shape");
                    AssertEqual(source, executor.Payloads.ReadText(actual.ResultPayload.ToBlobReference()), "Office retains only source bytes in this body");
                });
            });
        }

        private static void RetainedResultsQueriesAndContext()
        {
            WithTempPaths(paths => {
                var source = new string('s', 12000) + "SOURCE_END";
                var payloads = new ChatBlobStore(paths);
                var payload = PayloadRef.FromBlob(payloads.StoreText(source, "text/plain"));
                var reference = new ResourceRef("rna://file/retained/source", "revision1");
                var scope = new ResourceAuthorityScopeId("workspace", "retained");
                var evidence = new ResourceEvidence("observed1", scope, reference, "text", ResourceCoverage.Whole(), true,
                    1, payload, observedAt: new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc));
                var projection = new ResourceReadProjection { Target = "source.txt", Type = "file", Scope = "workspace",
                    Representation = "text", Text = source, Complete = true, SourceComplete = true,
                    Coverage = ResourceCoverage.Whole(), ReturnedCharacters = source.Length, TotalCharacters = source.Length,
                    RawContentIncluded = true, HydratedForNextModelStep = true };
                var result = new ToolResultMaterialization(ToolResult.OkRead("Read complete.", projection, new[] { reference }),
                    resourceEvidence: new[] { evidence }) { ExecutionProgress = new ToolExecutionProgress(ToolExecutionOutcome.Ok,
                        evidenceEligibility: EvidenceEligibility.Eligible) };
                var journal = new ConversationJournal(paths, "workspace:retained");
                var accepted = JournalReadCall("call1", "old-run", FixtureCallOrigin("call1").StepId);
                journal.CommitCall("chat1", "accepted1", accepted);
                var first = journal.CommitResult("chat1", "op1", "call1", "core.read", FixtureCallOrigin("call1").StepId, "old-run", result);
                AssertEqual(payload.Sha256, first.Fact.Result.ResultPayload.Sha256, "source view reuses the existing raw CAS body");
                AssertTrue(!first.Fact.Result.Content.Contains("SOURCE_END"), "occurrence metadata contains no source body");
                AssertEqual("E1", first.Fact.Result.EvidenceReference.Alias, "first exact terminal alias");
                var originalBytes = File.ReadAllBytes(journal.Journal.SegmentPath(1));
                var retry = journal.CommitResult("chat1", "op1", "call1", "core.read", FixtureCallOrigin("call1").StepId, "old-run", result);
                AssertEqual(first.Position.Hash, retry.Position.Hash, "retry returns the same canonical receipt");
                AssertTrue(File.ReadAllBytes(journal.Journal.SegmentPath(1)).SequenceEqual(originalBytes), "retry appends no frame");
                for (var i = 2; i <= 32; i++)
                {
                    journal.CommitCall("chat1", "accepted" + i, JournalReadCall("call" + i, "run" + i, FixtureCallOrigin("call" + i).StepId));
                    var before = journal.Journal.ReadRecords;
                    var occurrence = journal.CommitResult("chat1", "op" + i, "call" + i, "core.read",
                        FixtureCallOrigin("call" + i).StepId, "run" + i, result);
                    AssertEqual(payload.Sha256, occurrence.Fact.Result.ResultPayload.Sha256, "new calls retain the same immutable body");
                    AssertTrue(journal.Journal.ReadRecords - before <= 3, "warm append reads only the boundary and its accepted call, not historical results");
                }
                journal.CommitCall("chat2", "accepted-other", JournalReadCall("other", "other-run", "step"));
                var secondChat = journal.CommitResult("chat2", "other", "other", "core.read", "step", "other-run", result);
                AssertEqual("E1", secondChat.Fact.Result.EvidenceReference.Alias, "E1 of another chat does not collide");
                AssertEqual(1, Directory.EnumerateFiles(paths.ChatBlobDirectory, "*.blob", SearchOption.AllDirectories).Count(),
                    "repeated source reads create no per-call body/envelope copies");
                var refused = false;
                try { journal.CommitResult("chat2", "op1", "call1", "core.read", "step", "run", result); }
                catch (InvalidOperationException) { refused = true; }
                AssertTrue(refused, "operation identity cannot bind a different chat/command");
                journal.CommitCall("chat1", "accepted-fault", JournalReadCall("fault", "run", "step"));
                journal.Fault = stage => { throw new IOException("derived index unavailable"); };
                var committed = journal.CommitResult("chat1", "index-fault", "fault", "core.read", "step", "run", result);
                AssertTrue(!committed.ProjectionAvailable, "index failure preserves a committed receipt");
                journal.Fault = null;
                var recovered = new ConversationJournal(paths, "workspace:retained").Receipt("index-fault");
                AssertEqual(committed.Position.Hash, recovered.Position.Hash, "restart catches up known outcome without redispatch");
                var bytes = File.ReadAllBytes(journal.Journal.SegmentPath(1));
                journal.Rebuild();
                AssertTrue(File.ReadAllBytes(journal.Journal.SegmentPath(1)).SequenceEqual(bytes), "rebuild preserves committed prefix");
                var page = journal.ReadWindow("chat1", long.MaxValue, 4);
                AssertEqual(4, page.Count, "history hydrates only selected metadata records");
                AssertTrue(page.All(item => item.Fact.ChatId == "chat1"), "window is exactly chat scoped");
                AssertTrue(journal.ReadWindow("chat1", page[0].Position.Sequence, 4).All(item => item.Position.Sequence < page[0].Position.Sequence),
                    "keyset cursor does not repeat or skip a selected boundary");
                var restored = journal.Receipt("op1").Fact.Result;
                AssertEqual(first.Fact.Result.ResourceEvidence[0].EvidenceId, restored.ResourceEvidence[0].EvidenceId, "replay preserves observation identity");
                var authority = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new[] {
                    new ResourceAuthoritySnapshot(scope, 1, null, 0, new[] { ResourceHeadState.Known(reference, 1) }) }),
                    "pack", new SkillCatalogSnapshot(null), null, 100);
                var compiler = new ModelContextCompiler(payloads, projection: RNAssistant.Office.Services.ModelToolResultProjection.Instance);
                var context = new ConversationJournal(paths, "workspace:retained").OpenContext("chat1", first.Position.Sequence + 1, 1);
                AssertEqual(accepted.Content, context[0].Content, "restart uses the original accepted arguments, not a manufactured call");
                var compiled = compiler.Compile(authority, new ChatMessage[0], context,
                    null, new ToolCatalogEntry[0], new AppSettings(), 20000);
                AssertContains(string.Join("\n", compiled.Messages.Select(message => message.Content)), "SOURCE_END", "next request hydrates selected retained source");
                AssertTrue(compiled.DeliveredObservations.Any(item => item.Resource.Uri == reference.Uri && item.Resource.Revision == reference.Revision && item.Complete),
                    "only actual full delivery contributes complete evidence");
                AssertEqual(1, compiled.Receipt.HydratedPayloads, "selected source body is decoded once");
                AssertTrue(!restored.Content.Contains("SOURCE_END"), "request hydration does not mutate durable occurrence");

                journal.CommitCall("guards", "accepted-unknown", JournalReadCall("unknown", "run", "step"));
                var unknown = journal.CommitResult("guards", "unknown", "unknown", "core.read", "step", "run",
                    new ToolResultMaterialization(ToolResult.Unknown("No verified outcome.")) {
                        ExecutionProgress = new ToolExecutionProgress(ToolExecutionOutcome.Unknown, evidenceEligibility: EvidenceEligibility.None) });
                AssertTrue(unknown.Fact.Result.EvidenceReference == null, "ineligible/unknown terminal never receives E authority");
                AssertEqual("T1", unknown.Fact.Result.ToolCallReference.Alias, "terminal keeps its runtime execution identity");
                journal.CommitCall("guards", "accepted-known", JournalReadCall("known", "run", "next"));
                var known = journal.CommitResult("guards", "known", "known", "core.read", "next", "run", result);
                AssertEqual("E1", known.Fact.Result.EvidenceReference.Alias, "ineligible terminal does not consume an evidence ordinal");
                RuntimeThrows<InvalidOperationException>(() => journal.CommitResult("guards", "duplicate-terminal", "known", "core.read", "next", "run", result));
                journal.CommitCall("guards", "accepted-missing", JournalReadCall("missing", "run", "missing"));
                var absent = new ResourceEvidence("missing", scope, reference, "text", ResourceCoverage.Whole(), true, 1,
                    new PayloadRef(new string('a', 64), 12, "text/plain"));
                var missing = new ToolResultMaterialization(result.Result, resourceEvidence: new[] { absent }) { ExecutionProgress = result.ExecutionProgress };
                var bytesBeforeMissing = journal.Journal.WrittenBytes;
                RuntimeThrows<InvalidDataException>(() => journal.CommitResult("guards", "missing", "missing", "core.read", "missing", "run", missing));
                AssertEqual(bytesBeforeMissing, journal.Journal.WrittenBytes, "missing body is refused before canonical acceptance");

                var arbitrary = "{\"text\":\"" + new string('x', 10000) + "\",\"rows\":[1,2],\"nested\":{\"text\":\"keep\"}}";
                var domain = ToolResultPersistence.Create("domain", "example.inspect", "step",
                    new ToolResultMaterialization(ToolResult.Ok("Domain result", arbitrary)), payloads, "run");
                ToolResultWireReadResult parsed; string error;
                AssertTrue(ToolResultHistoryReader.TryRead(ToolResultPersistence.Hydrate(domain, payloads), out parsed, out error), "opaque domain result rehydrates");
                AssertTrue(JToken.DeepEquals(JToken.Parse(arbitrary), parsed.Data), "arbitrary text/rows properties are not treated as source fields");
                domain.ResultBody = null;
                RuntimeThrows<InvalidDataException>(() => ToolResultPersistence.ReadContent(domain, payloads));
            });
        }

        private static ChatMessage JournalReadCall(string callId, string run, string step)
        {
            var call = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall {
                Id = callId, Name = "core.read", Arguments = new Dictionary<string, object> { ["target"] = "source.txt" } },
                "", "Read the exact source.", null, ToolResultRoles.User, new AcceptedToolCallOrigin(step, "attempt:" + callId, 0, 1));
            call.RunId = run;
            return call;
        }

        private static int ConversationJournalProbe(string root, string chat)
        {
            var paths = AppDataPaths.CreateForRoot(root);
            var journal = new ConversationJournal(paths, "workspace:process-probe");
            for (var i = 0; i < 24; i++)
            {
                var id = chat + ":" + i;
                journal.CommitCall(chat, "accepted:" + id, JournalReadCall(id, "run:" + chat, "step:" + id));
                journal.CommitResult(chat, "result:" + id, id, "core.read", "step:" + id, "run:" + chat,
                    JournalResult(ToolResult.Ok("Read complete.", "{\"text\":\"" + new string('v', 10000) + "\"}")));
                if (chat == "child" && i == 0) File.WriteAllText(Path.Combine(root, "child-ready"), "ready");
            }
            return 0;
        }

        private static void ConversationJournalRepairAndProcesses()
        {
            WithTempPaths(paths => {
                var journal = new ConversationJournal(paths, "workspace:process-probe");
                var other = new ConversationJournal(paths, "document:other");
                other.CommitCall("other", "accepted", JournalReadCall("call", "run", "step"));
                var retained = other.CommitResult("other", "result", "call", "core.read", "step", "run",
                    JournalResult(ToolResult.Ok("Result", "{\"text\":\"" + new string('p', 10000) + "\"}")));
                var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add(typeof(Program).Assembly.Location); start.ArgumentList.Add("--conversation-journal-probe");
                start.ArgumentList.Add(paths.Root); start.ArgumentList.Add("child");
                var leases = ConversationLeaseService.ForOffice(paths);
                using (leases.Reserve("live-model-wait"))
                using (RuntimeStorageActivity.Acquire(paths))
                using (var child = Process.Start(start))
                {
                    try
                    {
                        var ready = Stopwatch.StartNew();
                        while (!File.Exists(Path.Combine(paths.Root, "child-ready")) && !child.HasExited && ready.ElapsedMilliseconds < 10000) Thread.Sleep(20);
                        AssertTrue(File.Exists(Path.Combine(paths.Root, "child-ready")), "separate writer is running");
                        var parent = Task.Run(() => ConversationJournalProbe(paths.Root, "parent"));
                        journal.Rebuild();
                        AssertTrue(leases.AnyHeld(), "repair does not drain or cancel a live model lease");
                        AssertTrue(new ChatBlobStore(paths).HasVerifiedReference(retained.Fact.Result.ResultPayload.ToBlobReference()), "CAS pin/body survives repair");
                        AssertTrue(parent.Wait(15000), "other metadata continuation resumes after repair");
                        AssertTrue(child.WaitForExit(15000), "separate process finishes after repair");
                        AssertEqual(0, child.ExitCode, "separate process: " + child.StandardError.ReadToEnd());
                    }
                    finally { if (!child.HasExited) { child.Kill(); child.WaitForExit(); } }
                }
                AssertEqual(24, journal.ReadWindow("child", long.MaxValue, 128).Count, "all child receipts survive shared repair");
                AssertEqual(24, journal.ReadWindow("parent", long.MaxValue, 128).Count, "all parent receipts survive shared repair");
                AssertEqual("E1", journal.Receipt("result:child:0").Fact.Result.EvidenceReference.Alias, "child E1");
                AssertEqual("E1", journal.Receipt("result:parent:0").Fact.Result.EvidenceReference.Alias, "parent E1");
                var before = Directory.EnumerateFiles(Path.Combine(paths.Root, "conversation-streams"), "*.jsonl", SearchOption.AllDirectories)
                    .ToDictionary(path => path, File.ReadAllBytes);
                File.WriteAllText(Path.Combine(paths.Root, "index.sqlite"), "broken disposable index");
                AssertEqual(retained.Position.Hash, other.Receipt("result").Position.Hash, "corrupt shared index rebuilds exact cross-scope receipt");
                AssertEqual(24, journal.ReadWindow("child", long.MaxValue, 128).Count, "repair discovers streams independently of the broken index");
                foreach (var pair in before) AssertTrue(pair.Value.SequenceEqual(File.ReadAllBytes(pair.Key)), "repair preserves canonical prefix");
                using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(paths.Root, "index.sqlite"), Pooling = false }.ToString()))
                {
                    db.Open(); using (var command = db.CreateCommand()) { command.CommandText = "DROP TABLE result_fact"; command.ExecuteNonQuery(); }
                }
                var sqlError = false;
                try { journal.Receipt("result:child:0"); }
                catch (SqliteException error) { sqlError = error.SqliteErrorCode == 1; }
                AssertTrue(sqlError, "SQL/schema error 1 is surfaced, not silently reset as corruption");
                journal.Rebuild();
                AssertEqual("E1", other.Receipt("result").Fact.Result.EvidenceReference.Alias, "explicit repair restores every stream's aliases");
            });
        }

        private static void StorageJournalDurability()
        {
            foreach (var encrypted in new[] { false, true })
            WithTempPaths(paths => {
                var protection = encrypted ? new StorageProtector(HistoryIntegrityModes.HmacSha256,
                    HistoryEncryptionModes.Aes256CbcHmacSha256, "synthetic-only", Enumerable.Repeat((byte)7, 16).ToArray()) : StorageProtector.None;
                var journal = new StorageJournal(Path.Combine(paths.Root, "journal"), "document:one", protection, rotationRecords: 2);
                var facts = new List<JournalRecord>();
                JournalCursor cursor;
                using (journal.AcquireAppendLock())
                {
                    cursor = journal.CatchUp(null, (fact, position) => facts.Add(fact), null);
                    cursor = journal.Append(cursor, "op1", "fp1", "result", new JObject { ["chat"] = "one", ["e"] = "E1" });
                    var firstBytes = File.ReadAllBytes(journal.SegmentPath(1));
                    cursor = journal.Append(cursor, "op2", "fp2", "result", new JObject { ["chat"] = "two", ["e"] = "E1" });
                    var closedBytes = File.ReadAllBytes(journal.SegmentPath(1));
                    cursor = journal.Append(cursor, "op3", "fp3", "result", new JObject { ["chat"] = "one", ["e"] = "E2" });
                    AssertEqual(2, cursor.Segment, "rotation publishes the next deterministic header");
                    AssertTrue(File.ReadAllBytes(journal.SegmentPath(1)).SequenceEqual(closedBytes), "closed segment is byte immutable");
                    AssertTrue(closedBytes.Take(firstBytes.Length).SequenceEqual(firstBytes), "append preserves committed prefix");
                    var original = File.ReadAllBytes(journal.SegmentPath(2));
                    var restarted = new StorageJournal(Path.Combine(paths.Root, "journal"), "document:one", protection, rotationRecords: 2);
                    var replay = restarted.CatchUp(null, (fact, position) => facts.Add(fact), null);
                    AssertEqual(3, facts.Count, "index loss streams all facts once");
                    AssertEqual("two", (string)facts[1].Data["chat"], "typed data survives protection/rebuild");
                    AssertEqual(cursor.Hash, replay.Hash, "replay preserves exact boundary");
                    AssertTrue(File.ReadAllBytes(journal.SegmentPath(2)).SequenceEqual(original), "rebuild does not rewrite authority");

                    // Recovery, not ordinary locator reads, may terminate a valid no-LF frame.
                    using (var tail = new FileStream(journal.SegmentPath(2), FileMode.Open, FileAccess.Write)) tail.SetLength(original.Length - 1);
                    var completed = restarted.CatchUp(null, (fact, position) => { }, null);
                    AssertTrue(File.ReadAllBytes(journal.SegmentPath(2)).SequenceEqual(original), "valid no-LF is adopted without losing its fact");
                    File.AppendAllText(journal.SegmentPath(2), "{\"Version\":", new UTF8Encoding(false));
                    restarted.CatchUp(completed, (fact, position) => { }, null);
                    AssertTrue(File.ReadAllBytes(journal.SegmentPath(2)).SequenceEqual(original), "only torn unaccepted suffix is truncated");

                    journal.Fault = stage => { if (stage == "after_flush") throw new IOException("lost ack"); };
                    try { journal.Append(completed, "op4", "fp4", "result", new JObject { ["chat"] = "one", ["e"] = "E3" }); }
                    catch (IOException) { }
                    facts.Clear();
                    var accepted = restarted.CatchUp(completed, (fact, position) => facts.Add(fact), null);
                    AssertEqual(1, facts.Count, "lost ack is recovered as one committed operation");
                    AssertEqual("op4", facts[0].Operation, "receipt lookup can recover its original operation identity");
                    AssertEqual(4L, accepted.Sequence, "lost ack consumes exactly one sequence");
                    journal.Fault = null;

                    var corruptDirectory = Path.Combine(paths.Root, "corrupt");
                    Directory.CreateDirectory(corruptDirectory);
                    File.Copy(journal.SegmentPath(1), Path.Combine(corruptDirectory, "00000001.jsonl"));
                    var bad = JObject.Parse(File.ReadAllLines(journal.SegmentPath(2)).Last());
                    bad["Hash"] = new string('0', 64);
                    var corrupt = new StorageJournal(corruptDirectory, "document:one", protection, rotationRecords: 2);
                    File.WriteAllText(corrupt.SegmentPath(2), File.ReadAllLines(journal.SegmentPath(2))[0] + "\n" +
                        bad.ToString(Formatting.None), new UTF8Encoding(false));
                    var corruptBytes = File.ReadAllBytes(corrupt.SegmentPath(2));
                    var badHashRejected = false;
                    try { corrupt.CatchUp(null, (fact, position) => { }, null); }
                    catch (InvalidDataException) { badHashRejected = true; }
                    AssertTrue(badHashRejected, "complete wrong-hash no-LF frame is not a torn suffix");
                    AssertTrue(File.ReadAllBytes(corrupt.SegmentPath(2)).SequenceEqual(corruptBytes), "bad integrity preserves authority bytes");

                    File.AppendAllText(journal.SegmentPath(2), "{\"Version\":999}\n", new UTF8Encoding(false));
                    var invalid = File.ReadAllBytes(journal.SegmentPath(2));
                    var rejected = false;
                    try { restarted.CatchUp(accepted, (fact, position) => { }, null); }
                    catch (InvalidDataException) { rejected = true; }
                    AssertTrue(rejected, "full invalid contract is not a crash tail");
                    AssertTrue(File.ReadAllBytes(journal.SegmentPath(2)).SequenceEqual(invalid), "invalid full frame is never erased");

                    var other = new StorageJournal(Path.Combine(paths.Root, "large"), "document:large", protection);
                    using (other.AcquireAppendLock())
                    {
                        var start = other.CatchUp(null, (fact, position) => { }, null);
                        var before = File.ReadAllBytes(other.SegmentPath(1));
                        rejected = false;
                        try { other.Append(start, "large", "large", "result", new JValue(new string('x', StorageJournal.MaximumFrameBytes))); }
                        catch (InvalidDataException) { rejected = true; }
                        AssertTrue(rejected, "encoded frame byte limit includes protection overhead");
                        AssertTrue(File.ReadAllBytes(other.SegmentPath(1)).SequenceEqual(before), "oversized frame is refused before append");
                    }
                }
            });
            WithTempPaths(paths => {
                ChatStore.InitializeSqliteProvider();
                using (var db = new SqliteConnection(new SqliteConnectionStringBuilder {
                    DataSource = Path.Combine(paths.Root, "index.sqlite"), Pooling = false }.ToString()))
                {
                    db.Open();
                    using (var command = db.CreateCommand())
                    {
                        command.CommandText = "CREATE TABLE facts (stream TEXT,chat TEXT,e TEXT,op TEXT,position TEXT,PRIMARY KEY(stream,chat,e),UNIQUE(stream,op)); CREATE TABLE cursor (stream TEXT PRIMARY KEY,position TEXT)";
                        command.ExecuteNonQuery();
                    }
                    var journal = new StorageJournal(Path.Combine(paths.Root, "journal"), "workspace:one");
                    Action<JournalRecord, JournalCursor> apply = (fact, position) => {
                        using (var transaction = db.BeginTransaction())
                        using (var command = db.CreateCommand())
                        {
                            command.Transaction = transaction;
                            command.CommandText = "INSERT INTO facts VALUES ($stream,$chat,$e,$op,$position); INSERT OR REPLACE INTO cursor VALUES ($stream,$position)";
                            command.Parameters.AddWithValue("$stream", fact.Stream);
                            command.Parameters.AddWithValue("$chat", (string)fact.Data["chat"]);
                            command.Parameters.AddWithValue("$e", (string)fact.Data["e"]);
                            command.Parameters.AddWithValue("$op", fact.Operation);
                            command.Parameters.AddWithValue("$position", JsonConvert.SerializeObject(position));
                            command.ExecuteNonQuery(); transaction.Commit();
                        }
                    };
                    using (journal.AcquireAppendLock())
                    {
                        var header = journal.CatchUp(null, apply, null);
                        var first = journal.Append(header, "delivery", "fp", "result", new JObject { ["chat"] = "a", ["e"] = "E1" });
                        apply(journal.Read(first), first);
                        // JSONL accepts the second fact but index application/ack is lost.
                        var second = journal.Append(first, "delivery2", "fp2", "result", new JObject { ["chat"] = "b", ["e"] = "E1" });
                        var bytes = File.ReadAllBytes(journal.SegmentPath(1));
                        var resumed = journal.CatchUp(first, apply, null);
                        AssertEqual(second.Hash, resumed.Hash, "suffix applies accepted fact after index failure");
                        using (var command = db.CreateCommand())
                        {
                            command.CommandText = "SELECT position FROM facts WHERE stream='workspace:one' AND op='delivery2'";
                            var receipt = JsonConvert.DeserializeObject<JournalCursor>((string)command.ExecuteScalar());
                            AssertEqual("delivery2", journal.Read(receipt).Operation, "retry returns an exact canonical receipt locator");
                            command.CommandText = "DELETE FROM facts; DELETE FROM cursor"; command.ExecuteNonQuery();
                        }
                        journal.CatchUp(null, apply, null);
                        using (var command = db.CreateCommand())
                        {
                            command.CommandText = "SELECT COUNT(*) FROM facts";
                            AssertEqual(2L, (long)command.ExecuteScalar(), "index alone rebuilds from the journal without duplicate occurrences");
                        }
                        AssertTrue(File.ReadAllBytes(journal.SegmentPath(1)).SequenceEqual(bytes), "index repair preserves every accepted byte");
                    }
                }
            });
        }

        // P0 uses the installed managed/native provider, not the system sqlite CLI.
        private static int StorageIndexProbe(string path, string chat)
        {
            ChatStore.InitializeSqliteProvider();
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource = path, Pooling = false, DefaultTimeout = 10 }.ToString()))
            {
                db.Open();
                using (var command = db.CreateCommand())
                {
                    command.CommandText = "PRAGMA busy_timeout=10000; CREATE TABLE IF NOT EXISTS facts (scope TEXT NOT NULL, chat TEXT NOT NULL, fact TEXT NOT NULL, sequence INTEGER NOT NULL, PRIMARY KEY(scope,chat,fact)); CREATE TABLE IF NOT EXISTS cursors (stream TEXT PRIMARY KEY, sequence INTEGER NOT NULL)";
                    command.ExecuteNonQuery();
                }
                using (var transaction = db.BeginTransaction())
                using (var command = db.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "INSERT OR IGNORE INTO facts VALUES ('scope', $chat, 'E1', 1); INSERT OR REPLACE INTO cursors VALUES ($chat, 1)";
                    command.Parameters.AddWithValue("$chat", chat);
                    command.ExecuteNonQuery();
                    transaction.Commit();
                }
                using (var command = db.CreateCommand())
                {
                    command.CommandText = "SELECT sqlite_version()";
                    Console.WriteLine("SQLITE native=" + command.ExecuteScalar() + " managed=" + typeof(SqliteConnection).Assembly.GetName().Version);
                }
            }
            return 0;
        }

        private static void StorageOptimizationCostCurve()
        {
            if (Environment.GetEnvironmentVariable("RNA_STORAGE_OPT_COST") != "1")
            { Console.WriteLine("SKIP cost curve: set RNA_STORAGE_OPT_COST=1"); return; }
            foreach (var history in new[] { 100, 1000, 10000 })
            WithTempPaths(paths => {
                var candidatePaths = AppDataPaths.CreateForRoot(Path.Combine(paths.Root, "new-root"));
                var journal = new ConversationJournal(candidatePaths, "workspace:cost");
                var store = new ChatStore(paths); var bodies = new ChatBlobStore(paths);
                var session = store.Create("Workspace", "cost", "Cost fixture", "Cost fixture");
                var result = JournalResult(ToolResult.OkRead("Read", new ResourceReadProjection {
                    Target = "source.txt", Scope = "workspace", Representation = "text", Type = "file", Text = new string('s', 12000),
                    Complete = true, SourceComplete = true, Coverage = ResourceCoverage.Whole(), ReturnedCharacters = 12000, TotalCharacters = 12000 }, null));
                for (var i = 0; i <= history; i++)
                {
                    var call = JournalReadCall("call:" + i, "run:" + i, "step:" + i);
                    journal.CommitCall(session.Id, "accepted:" + i, call); session.Messages.Add(call);
                    call.ToolCallReference = ModelReference.Create(ModelReferenceKind.ToolCall, i + 1);
                    if (i == history) break;
                    var terminal = journal.CommitResult(session.Id, "result:" + i, call.ToolCallId, "core.read", call.AcceptedCallOrigin.StepId, call.RunId, result);
                    var oldResult = ToolResultPersistence.Create(call.ToolCallId, "core.read", call.AcceptedCallOrigin.StepId, result, bodies, call.RunId);
                    oldResult.EvidenceReference = terminal.Fact.Result.EvidenceReference;
                    oldResult.ToolCallReference = call.ToolCallReference;
                    session.Messages.Add(oldResult);
                }
                store.Save(session); // identical accepted history/body fixture, outside measurement
                var last = session.Messages.Last();
                var path = SessionEventFile(paths, session); var oldBytes = new FileInfo(path).Length;
                var oldVisits = store.MaterializedMessageCount;
                var oldAllocation = GC.GetAllocatedBytesForCurrentThread(); var oldCpu = Process.GetCurrentProcess().TotalProcessorTime;
                var timer = Stopwatch.StartNew();
                var message = ToolResultPersistence.Create(last.ToolCallId, "core.read", last.AcceptedCallOrigin.StepId, result, bodies, last.RunId);
                session.Messages.Add(message); store.Commit(session, ConversationCommit.ToolResult(message));
                var oldMs = timer.Elapsed.TotalMilliseconds; var oldCpuMs = (Process.GetCurrentProcess().TotalProcessorTime - oldCpu).TotalMilliseconds;
                var oldAllocated = GC.GetAllocatedBytesForCurrentThread() - oldAllocation;
                var reads = journal.Journal.ReadRecords; var readBytes = journal.Journal.ReadBytes; var bytes = journal.Journal.WrittenBytes;
                var allocation = GC.GetAllocatedBytesForCurrentThread(); var cpu = Process.GetCurrentProcess().TotalProcessorTime;
                timer.Restart();
                journal.CommitResult(session.Id, "result:" + history, last.ToolCallId, "core.read", last.AcceptedCallOrigin.StepId, last.RunId, result);
                var appendMs = timer.Elapsed.TotalMilliseconds; var cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds;
                var allocated = GC.GetAllocatedBytesForCurrentThread() - allocation;
                var appendReads = journal.Journal.ReadRecords - reads;
                AssertTrue(appendReads <= 3, "same exact command reads only boundary/accepted call independently of archive length");
                var restarted = new ConversationJournal(candidatePaths, "workspace:cost");
                var context = restarted.OpenContext(session.Id, long.MaxValue, 10);
                AssertEqual(20, context.Count, "restart loads fixed active set, not complete history");
                AssertTrue(restarted.Journal.ReadRecords <= 21, "bounded startup locators plus one boundary");
                Console.WriteLine("STORAGE_COMPARE " + JsonConvert.SerializeObject(new { historyPairs = history, activeMessages = context.Count,
                    aggregate = new { materializedMessages = store.MaterializedMessageCount - oldVisits,
                        appendBytes = new FileInfo(path).Length - oldBytes, allocatedBytes = oldAllocated, cpuMs = oldCpuMs, elapsedMs = oldMs },
                    journal = new { appendReads, readBytes = journal.Journal.ReadBytes - readBytes, appendBytes = journal.Journal.WrittenBytes - bytes,
                        allocatedBytes = allocated, cpuMs, elapsedMs = appendMs, restartRecords = restarted.Journal.ReadRecords } }));
            });
        }

        private static ToolResultMaterialization JournalResult(ToolResult result)
        { return new ToolResultMaterialization(result) { ExecutionProgress = new ToolExecutionProgress(ToolExecutionOutcome.Ok, evidenceEligibility: EvidenceEligibility.Eligible) }; }

        private static void StorageOptimizationBaseline()
        {
            if (Environment.GetEnvironmentVariable("RNA_STORAGE_OPT_BASELINE") != "1")
            {
                Console.WriteLine("SKIP storage optimization baseline: set RNA_STORAGE_OPT_BASELINE=1");
                return;
            }
            WithTempPaths(paths => {
                var index = Path.Combine(paths.Root, "index-probe.sqlite");
                StorageIndexProbe(index, "first");
                var start = new ProcessStartInfo("dotnet") { UseShellExecute = false,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add(typeof(Program).Assembly.Location);
                start.ArgumentList.Add("--storage-index-probe");
                start.ArgumentList.Add(index);
                start.ArgumentList.Add("second");
                using (var child = Process.Start(start))
                {
                    // A separate process opens and writes while this process uses the same file.
                    StorageIndexProbe(index, "first");
                    AssertTrue(child.WaitForExit(15000), "second process finishes its short index transaction");
                    Console.Write(child.StandardOutput.ReadToEnd());
                    AssertEqual(0, child.ExitCode, "second process provider: " + child.StandardError.ReadToEnd());
                }
                using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = index, Pooling = false }.ToString()))
                {
                    db.Open();
                    using (var command = db.CreateCommand())
                    {
                        command.CommandText = "SELECT COUNT(*) FROM facts f JOIN cursors c ON c.stream=f.chat AND c.sequence=f.sequence";
                        AssertEqual(2L, (long)command.ExecuteScalar(), "E1 is scoped per chat and retry preserves one receipt");
                    }
                }
            });
            foreach (var history in new[] { 100, 1000, 10000 })
            WithTempPaths(paths => {
                var store = new ChatStore(paths);
                var session = store.Create("Word", "storage-baseline", "Synthetic", "Synthetic");
                for (var i = 0; i < history; i++)
                    session.Messages.Add(new ChatMessage { Role = "user", Content = new string('x', 128) });
                store.Save(session); // fixture setup, excluded from all measurements below
                var path = SessionEventFile(paths, session);
                var beforeLength = new FileInfo(path).Length;
                var beforeVisits = store.MaterializedMessageCount;
                var beforeAllocation = GC.GetAllocatedBytesForCurrentThread();
                var message = new ChatMessage { Role = "user", Content = "one small input" };
                session.Messages.Add(message);
                var timer = Stopwatch.StartNew();
                store.Commit(session, ConversationCommit.UserInput(message));
                var elapsed = timer.Elapsed.TotalMilliseconds;
                var allocated = GC.GetAllocatedBytesForCurrentThread() - beforeAllocation;
                var materialized = store.MaterializedMessageCount - beforeVisits;
                var appendBytes = new FileInfo(path).Length - beforeLength;
                timer.Restart();
                var page = new ChatStore(paths).ReadMessageWindow(session.Id, history + 1, 20);
                var pageMs = timer.Elapsed.TotalMilliseconds;
                AssertEqual(20, page.Session.Messages.Count, "fixed history window");
                AssertTrue(materialized >= history, "baseline exposes aggregate materialization on a small commit");
                var checkpoint = new ContextCheckpoint { ThroughMessageId = session.Messages[history - 21].Id,
                    Claims = new List<StructuredContextClaim> { new StructuredContextClaim { ClaimId = "baseline",
                        Kind = "constraint", Text = "Continue.", SourceRoles = new List<string> { "user" },
                        SourceMessageIds = new List<string> { session.Messages[0].Id } } } };
                session.ContextCheckpoints.Add(checkpoint); session.ActiveContextCheckpointId = checkpoint.Id;
                var active = session.Messages.Skip(history - 19).ToArray();
                timer.Restart();
                var working = ContextWorkingSet.Restore(session, active, 1000);
                var restoreMs = timer.Elapsed.TotalMilliseconds;
                AssertEqual(history + 1, working.VisitedMessages, "fixed active set still visits the full archive");
                Console.WriteLine("STORAGE_BASELINE " + JsonConvert.SerializeObject(new { history,
                    materializedMessages = materialized, appendLogicalBytes = appendBytes,
                    allocatedBytes = allocated, appendMs = elapsed, pageMs,
                    workingSetVisited = working.VisitedMessages, workingSetMs = restoreMs, activeMessages = active.Length,
                    indexRebuilds = store.SqliteProjectionFullBuildCount, suffixApplications = store.SqliteProjectionSuffixCount }));
            });
        }
    }
}
