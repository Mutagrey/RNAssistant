using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Agent;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Storage;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void RuntimeProjectionPreservesAcceptedBatch()
        {
            WithTempPaths(paths => {
                var store = new ChatStore(paths);
                var session = store.Create("Workspace", "accepted-projection", "Workspace", "Accepted");
                session.LastRun = new ChatRunRecord { RunId = "run", TurnId = "turn" }; store.Save(session);
                var text = new string('x', 9000) + "\\literal\\n";
                var arguments = JsonConvert.SerializeObject(new { text, stamp = "2026-11-11T12:34:56.000Z" });
                var response = new AgentResponse(ConversationResponse.ExecuteAction, "Read exact sources.", "Read both.",
                    new[] { new ToolCall("one", "core.read", arguments), new ToolCall("two", "core.read", "{}") });
                var fact = new AgentRunEvent(AgentRunEventKind.ResponseAccepted, RuntimeBoundaryFact(session).Summary,
                    stepId: "step", response: response, sourceModelAttemptId: "attempt");
                var payloads = new ChatBlobStore(paths);
                var messages = ConversationRunProjection.CreateAcceptedResponse(fact, new RNAssistant.Core.Llm.LlmCompletionResult {
                    ReasoningContent = new string('r', 24001), CompletionTokens = 10 }, payloads);
                session.Messages.AddRange(messages);
                new ConversationRunCommit(session, fact, messages).Persist(store.CommitRun, session);
                var loaded = new ChatStore(paths).Load(session.Id).Messages;
                AssertEqual(2, loaded.Count, "one durable record per accepted batch position");
                AssertTrue(AcceptedCallPayloadService.IsExternalizedCall(loaded[0]), "large accepted arguments use the existing CAS owner");
                AssertTrue(loaded[0].Content.Length < 200, "large source stays out of accepted metadata");
                AssertEqual(24000, loaded[0].ReasoningContent.Length, "reasoning has the shared transcript bound");
                AssertTrue(loaded[0].ReasoningTruncated && loaded[1].ReasoningContent == null && loaded[1].CompletionTokens == null,
                    "only the first call carries bounded reasoning and usage");
                AssertEqual(1, loaded[1].AcceptedCallOrigin.CallIndex, "batch position survives restart");
                AssertEqual("attempt", loaded[1].AcceptedCallOrigin.ModelAttemptId, "origin comes from the kernel fact");
                var hydrated = AcceptedCallPayloadService.Hydrate(loaded[0], payloads);
                var accepted = ConversationResponseHistoryReader.Read(hydrated);
                var exact = JsonConvert.DeserializeObject<Dictionary<string, string>>(accepted.Response.ToolCalls.Single().ArgumentsJson);
                AssertEqual(text, exact["text"], "literal backslashes survive shared projection and CAS hydration");
                AssertEqual("2026-11-11T12:34:56.000Z", exact["stamp"], "ISO-looking strings are not rewritten as dates");
                var missingOrigin = new AgentRunEvent(AgentRunEventKind.ResponseAccepted, fact.Summary,
                    stepId: "step", response: response);
                RuntimeThrows<InvalidOperationException>(() => ConversationRunProjection.CreateAcceptedResponse(missingOrigin, null, payloads));
            });
        }

        private static AgentRunEvent RuntimeBoundaryFact(ChatSession session)
        {
            return new AgentRunEvent(AgentRunEventKind.ModelStepStarted,
                new RunSummary(session.LastRun.RunId, session.LastRun.TurnId, RunLifecycle.Running,
                    new ToolCounts(), 1, 0, "", null, null));
        }

        private static void RuntimeCommitWithoutCallerHistory()
        {
            WithTempPaths(paths => {
                var store = new ChatStore(paths);
                var session = store.Create("Workspace", "runtime-state", "Workspace", "Before");
                session.LastRun = new ChatRunRecord { RunId = "run", TurnId = "turn" };
                for (var i = 0; i < 20; i++) session.Messages.Add(new ChatMessage { Role = "user", Content = "history " + i });
                SetContinuityCheckpoint(session, session.Messages.Last());
                store.Save(session);
                var historyIds = session.Messages.Select(item => item.Id).ToArray();
                var checkpoint = session.ActiveContextCheckpointId;
                // The caller can carry current state and a scalar count, without
                // loading, enumerating or sending canonical history to CommitRun.
                var state = session.CopyForRunCommit();
                state.Title = "After";
                var update = new ChatMessage { Id = historyIds[3], Role = "user", Content = "explicit edit" };
                var commit = new ConversationRunCommit(state, RuntimeBoundaryFact(state), new ChatMessage[0],
                    updated: new[] { update }, expectedMessageCount: historyIds.Length);
                AssertEqual(0, commit.CreateStateProjection().Messages.Count, "command never captures caller history");
                commit.Persist(new ChatConversationStoreAdapter(store).CommitRun, state);
                AssertEqual(0, state.Messages.Count, "receipt does not hydrate caller history");
                AssertEqual(state.Revision, commit.Position.Sequence, "receipt identifies the durable cursor");
                var loaded = new ChatStore(paths).Load(session.Id);
                AssertTrue(historyIds.SequenceEqual(loaded.Messages.Select(item => item.Id)), "canonical history and order survive state-only submission");
                AssertEqual("explicit edit", loaded.Messages[3].Content, "only the addressed message changes");
                AssertEqual("After", loaded.Title, "existing metadata change remains explicit commit state");
                AssertEqual(checkpoint, loaded.ActiveContextCheckpointId, "active checkpoint cannot be lost because the caller omitted history");
                AssertEqual(checkpoint, state.ActiveContextCheckpointId, "receipt preserves active selection");
                RuntimeThrows<InvalidOperationException>(() => commit.Persist(store.CommitRun, state));
                var before = loaded.Revision;
                state.Revision = before - 1;
                var stale = new ConversationRunCommit(state, RuntimeBoundaryFact(state), new ChatMessage[0], expectedMessageCount: historyIds.Length);
                RuntimeThrows<ChatConcurrencyException>(() => stale.Persist(store.CommitRun, state));
                AssertEqual(before - 1, state.Revision, "conflict never adopts a foreign tail");
                AssertEqual(before, new ChatStore(paths).Load(session.Id).Revision, "stale command cannot write");
                var wrong = loaded.CopyForRunCommit(); wrong.DocumentKey = "foreign";
                var exact = new ConversationRunCommit(loaded, RuntimeBoundaryFact(loaded), new ChatMessage[0]);
                var called = false;
                RuntimeThrows<InvalidOperationException>(() => exact.Persist(value => called = true, wrong));
                AssertTrue(!called, "projection scope mismatch is rejected before storage");
                RuntimeThrows<InvalidOperationException>(() => store.Commit(loaded, exact));
                var unconfirmed = new ConversationRunCommit(loaded, RuntimeBoundaryFact(loaded), new ChatMessage[0]);
                RuntimeThrows<InvalidOperationException>(() => unconfirmed.Persist(value => { }, loaded));
                AssertTrue(unconfirmed.Position == null, "a returning handler without a receipt cannot claim success");
            });
        }

        private static void RuntimeCommitKeepsDeltaOrder()
        {
            WithTempPaths(paths => {
                var store = new ChatStore(paths);
                var session = store.Create("Workspace", "runtime-order", "Workspace", "Order");
                session.LastRun = new ChatRunRecord { RunId = "run", TurnId = "run" }; store.Save(session);
                var context = PersistenceContext("run", "call");
                var summary = RuntimeBoundaryFact(session).Summary;
                var accepted = ConversationRunProjection.CreateToolCallMessage(new AgentToolCall { Id = context.Call.Id,
                    Name = context.Call.Name, Arguments = new Dictionary<string, object> { ["range"] = "A1" } },
                    "Read", "Read the requested source.", null, ToolResultRoles.User,
                    new AcceptedToolCallOrigin(context.StepId, "attempt", 0, 1));
                accepted.RunId = "run"; session.Messages.Add(accepted);
                new ConversationRunCommit(session, new AgentRunEvent(AgentRunEventKind.ResponseAccepted, summary,
                    response: new AgentResponse(ConversationResponse.ExecuteAction, "Read", "Read", new[] { context.Call })),
                    new[] { accepted }).Persist(store.CommitRun, session);
                var record = KernelRecord(context);
                var activity = new ChatMessage { Role = "assistant", RunId = "run", RuntimeProjection = true,
                    Activity = new ChatActivity { Kind = "tool", ToolCallId = context.Call.Id, ToolId = context.Call.Name,
                        RunId = "run", Status = "completed" } };
                var result = ToolResultPersistence.Create(context.Call.Id, context.Call.Name, context.StepId,
                    new ToolResultMaterialization(RNAssistant.Core.Tools.Contracts.ToolResult.Ok("Done")) {
                        ExecutionProgress = ToolExecutionProgress.Capture(record) }, new ChatBlobStore(paths), "run");
                var media = new ChatMessage { Role = "user", RunId = "run", Content = "media helper" };
                session.Messages.AddRange(new[] { activity, result, media });
                new ConversationRunCommit(session, new AgentRunEvent(AgentRunEventKind.ToolCompleted, summary, execution: record),
                    new[] { activity, result, media }, activity).Persist(store.CommitRun, session);
                var loaded = new ChatStore(paths).Load(session.Id);
                AssertTrue(session.Messages.Select(item => item.Id).SequenceEqual(loaded.Messages.Select(item => item.Id)),
                    "new activity/result/media preserve presentation order independently of lifecycle operation order");
                AssertEqual(accepted.ToolCallReference.Alias, loaded.Messages[2].ToolCallReference.Alias, "result retains exact accepted-call binding");
            });
        }

        private static void RuntimeCommitOwnAppendRecovery()
        {
            foreach (var boundary in new[] { "before-write", "after-flush" })
            WithTempPaths(paths => {
                var store = new ChatStore(paths);
                var session = store.Create("Workspace", "runtime-fault", "Workspace", "Fault");
                session.LastRun = new ChatRunRecord { RunId = "run", TurnId = "turn" };
                session.Messages.Add(new ChatMessage { Role = "user", Content = "preserved" }); store.Save(session);
                var before = session.Revision;
                var count = session.Messages.Count;
                var workspace = session.HtmlWorkspace;
                var commit = new ConversationRunCommit(session, RuntimeBoundaryFact(session), new ChatMessage[0]);
                store.AppendFaultPoint = point => { if (point == boundary) throw new IOException("runtime boundary fault"); };
                RuntimeThrows<IOException>(() => commit.Persist(store.CommitRun, session));
                store.AppendFaultPoint = null;
                var loaded = new ChatStore(paths).Load(session.Id);
                AssertEqual(loaded.Revision, session.Revision, "only own durably recovered append advances the caller cursor");
                AssertEqual(boundary == "after-flush", session.Revision > before, "failure is not turned into acknowledged success");
                AssertEqual(count, loaded.Messages.Count, "failed state write neither removes nor duplicates history");
                AssertTrue(ReferenceEquals(workspace, session.HtmlWorkspace), "exception receipt does not clear the current view");
                AssertEqual(boundary == "after-flush", commit.Position != null, "typed position is available only for the retained own append");
                RuntimeThrows<InvalidOperationException>(() => commit.Persist(store.CommitRun, session));
            });
        }

        private static void RuntimeCommitStoreCost()
        {
            foreach (var count in new[] { 100, 1000, 10000 })
            WithTempPaths(paths => {
                var store = new ChatStore(paths);
                var session = store.Create("Workspace", "runtime-store", "Workspace", "Cost");
                session.LastRun = new ChatRunRecord { RunId = "run", TurnId = "turn" };
                for (var i = 0; i < count; i++) session.Messages.Add(new ChatMessage { Role = "user", Content = "history " + i });
                store.Save(session);
                // A state-only generic commit uses the previous aggregate path;
                // no lifecycle/message semantic differs in this fixture.
                var visits = store.MaterializedMessageCount; var before = GC.GetAllocatedBytesForCurrentThread();
                var timer = Stopwatch.StartNew();
                store.Commit(session, new ConversationCommit());
                var oldMs = timer.Elapsed.TotalMilliseconds; var oldBytes = GC.GetAllocatedBytesForCurrentThread() - before;
                var oldVisits = store.MaterializedMessageCount - visits;
                before = GC.GetAllocatedBytesForCurrentThread(); visits = store.MaterializedMessageCount; timer.Restart();
                new ConversationRunCommit(session, RuntimeBoundaryFact(session), new ChatMessage[0]).Persist(store.CommitRun, session);
                var elapsedMs = timer.Elapsed.TotalMilliseconds; var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
                Console.WriteLine("RUNTIME_COMMIT_STORE " + JsonConvert.SerializeObject(new { history = count,
                    oldVisits, visits = store.MaterializedMessageCount - visits, oldAllocatedBytes = oldBytes,
                    allocatedBytes, oldMs, elapsedMs }));
                AssertEqual(count, new ChatStore(paths).Load(session.Id).Messages.Count, "unchanged archive is preserved by both commit paths");
            });
        }

        private static void RuntimeCommitCaptureCost()
        {
            foreach (var count in new[] { 100, 1000, 10000 })
            {
                var session = new ChatSession { Host = "Workspace", DocumentKey = "runtime-cost", Revision = 1,
                    LastRun = new ChatRunRecord { RunId = "run", TurnId = "turn" } };
                for (var i = 0; i < count; i++) session.Messages.Add(new ChatMessage { Role = "user", Content = new string('x', 100) });
                var fact = RuntimeBoundaryFact(session);
                var added = new ChatMessage { Role = "assistant", Content = "visible update" };
                var firstMessage = session.Messages.Count; session.Messages.Add(added);
                // Old Office boundary captured every identity before selecting the
                // new tail. Setup and storage are deliberately outside this probe.
                var before = GC.GetAllocatedBytesForCurrentThread(); var timer = Stopwatch.StartNew();
                for (var repeat = 0; repeat < 24; repeat++)
                {
                    var previous = new HashSet<string>(session.Messages.Take(firstMessage).Select(item => item.Id), StringComparer.Ordinal);
                    var appended = session.Messages.Where(item => !previous.Contains(item.Id)).ToArray();
                    AssertEqual(1, appended.Length, "same old tail selection");
                }
                var oldMs = timer.Elapsed.TotalMilliseconds; var oldBytes = GC.GetAllocatedBytesForCurrentThread() - before;
                before = GC.GetAllocatedBytesForCurrentThread(); timer.Restart();
                for (var repeat = 0; repeat < 24; repeat++)
                {
                    var appended = session.Messages.GetRange(firstMessage, session.Messages.Count - firstMessage);
                    var command = new ConversationRunCommit(session, fact, new ChatMessage[0], updated: appended);
                    AssertEqual(1, command.Messages.Count, "same new explicit delta");
                    AssertEqual(count + 1, command.ExpectedMessageCount, "scalar guard replaces caller-wide identity inventory");
                    AssertEqual(0, command.CreateStateProjection().Messages.Count, "no message archive retained");
                }
                var elapsedMs = timer.Elapsed.TotalMilliseconds; var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
                Console.WriteLine("RUNTIME_COMMIT_CAPTURE " + JsonConvert.SerializeObject(new { history = count,
                    oldAllocatedBytes = oldBytes, allocatedBytes, oldMs, elapsedMs }));
                AssertTrue(allocatedBytes < 100000, "preparation allocation does not grow with message history");
            }
        }
    }
}
