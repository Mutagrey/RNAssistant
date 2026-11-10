using System;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Application.Conversations;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ConversationJournalRunCheckpoints()
        {
            WithTempPaths(paths => {
                var owner = new ConversationJournal(paths, DeliveryTestScope);
                var policy = ConversationRunPolicy.For(ChatModes.Agent);
                owner.CreateChat("create", "root", "Run owner", policy);
                var runtime = new ConversationRunOwner(ConversationLeaseService.ForScope(paths, DeliveryTestScope));
                var secondHost = new ConversationRunOwner(ConversationLeaseService.ForScope(paths, DeliveryTestScope));
                using (var first = runtime.Start("root", "run", null, new CancellationTokenSource()))
                {
                    RuntimeThrows<InvalidOperationException>(() => secondHost.Start("root", "other-run", null, new CancellationTokenSource()));
                    var initial = CheckpointState("run", RunLifecycle.Running, 0, 0);
                    var started = owner.CommitRun("start", "root", null, first.InvocationId, initial, policy);
                    var now = DateTime.UtcNow;
                    var waitPolicy = AgentCommunicationToolCatalog.GetTools().Single(tool => tool.Id == AgentCommunicationToolCatalog.Wait).Policy;
                    var call = new ToolCall("wait", AgentCommunicationToolCatalog.Wait, "{\"timeout_seconds\":5}");
                    var context = new ToolExecutionContext(call, new ToolPolicySnapshot(call.Name, "fixture", waitPolicy), "run", "turn-run", "step-wait", now, false, 19);
                    var pending = new PendingToolWait(context, new AgentWaitCondition(now, now.AddSeconds(5)));
                    var waiting = CheckpointState("run", RunLifecycle.Waiting, 1, 1, pending: pending);
                    owner.Journal.Fault = point => { if (point == "after_flush") throw new IOException("wait acknowledgement lost"); };
                    RuntimeThrows<IOException>(() => owner.CommitRun("wait", "root", started, first.InvocationId, waiting, policy));
                    owner = new ConversationJournal(paths, DeliveryTestScope);
                    var saved = owner.CurrentRun("root");
                    AssertEqual(RunLifecycle.Waiting, saved.Fact.Lifecycle, "saved checkpoint catches up flushed wait");
                    var restoredState = owner.ReadRunState(saved);
                    AssertEqual(pending.Condition.DeadlineUtc, restoredState.Summary.PendingWait.Condition.DeadlineUtc, "absolute wait deadline survives restart");
                    var continuation = AgentRunContinuation.RestoreWait(restoredState, saved.Fact.Revision,
                        new[] { AgentMessage.User("Inspect"), AgentMessage.Assistant(new AgentResponse("execute", "Wait for peer input", null, new[] { call })) });
                    AssertEqual(1, continuation.Summary.ToolStepsUsed, "existing kernel restores charged wait without restarting budget");
                    AssertTrue(!secondHost.IsRunning("root"), "reading saved wait never activates another host");
                    RuntimeThrows<InvalidOperationException>(() => owner.CommitRun("stale", "root", started, first.InvocationId, initial, policy));
                    RuntimeThrows<InvalidOperationException>(() => owner.CommitRun("wrong-invocation", "root", saved, "foreign", CheckpointState("run", RunLifecycle.Running, 1, 1), policy));
                    runtime.Complete("root", "run", first.InvocationId);
                    using (var resumed = runtime.Start("root", "run", null, new CancellationTokenSource()))
                    {
                        AssertTrue(!first.IsActive && !first.Cancel(), "stale handle cannot cancel a later invocation of the same run");
                        first.Dispose();
                        AssertTrue(resumed.IsActive, "stale disposal cannot release a later invocation's lease");
                        RuntimeThrows<InvalidOperationException>(() => secondHost.Start("root", "competing", null, new CancellationTokenSource()));
                        var running = CheckpointState("run", RunLifecycle.Running, 1, 1);
                        var resumedReceipt = owner.CommitRun("resume", "root", saved, resumed.InvocationId, running, policy, resume: true);
                        RuntimeThrows<InvalidOperationException>(() => owner.CommitRun("reset-budget", "root", resumedReceipt, resumed.InvocationId, initial, policy));

                        // Prepared JSON contains a nested body reference. The checkpoint
                        // must reject its publication before accepting any metadata.
                        var writePolicy = new ToolPolicy(ToolEffect.Write, ToolVerification.Tool, false, false, new[] { "agent" });
                        var missing = new PayloadRef(new string('a', 64), 5, "text/plain");
                        var write = new ToolExecutionContext(new ToolCall("write", "core.write", "{}"), new ToolPolicySnapshot("core.write", "fixture", writePolicy),
                            "run", "turn-run", "step-write", now, false, 18, JsonConvert.SerializeObject(new { Payload = missing }));
                        var inFlight = new AgentRunState(running.Summary, running.Limits, write);
                        var bytes = owner.Journal.WrittenBytes;
                        RuntimeThrows<InvalidDataException>(() => owner.CommitRun("missing-prepared-body", "root", resumedReceipt, resumed.InvocationId, inFlight, policy));
                        AssertEqual(bytes, owner.Journal.WrittenBytes, "missing nested body accepts no checkpoint frame");
                        var stopped = owner.CommitRun("stop", "root", resumedReceipt, resumed.InvocationId,
                            CheckpointState("run", RunLifecycle.Cancelled, 1, 1, counts: new ToolCounts(writeUnknown: 1)), policy);
                        RuntimeThrows<InvalidOperationException>(() => owner.CommitRun("late-state", "root", stopped, resumed.InvocationId, running, policy));
                        AssertEqual(1, owner.ReadRunState(stopped).Summary.ToolCounts.WriteUnknown, "terminal unknown effect remains recorded");
                    }
                }
                var terminal = owner.CurrentRun("root");
                var reads = owner.Journal.ReadRecords;
                AssertEqual(terminal.Position.Hash, owner.CurrentRun("root").Position.Hash, "current checkpoint exact lookup");
                AssertTrue(owner.Journal.ReadRecords - reads <= 2, "current run reads only boundary and exact checkpoint metadata");
                var canonical = Directory.EnumerateFiles(paths.Root, "*.jsonl", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
                owner.Rebuild();
                foreach (var pair in canonical) AssertTrue(pair.Value.SequenceEqual(File.ReadAllBytes(pair.Key)), "run checkpoint rebuild preserves bytes");
                AssertEqual(1, owner.ReadRunState(owner.CurrentRun("root")).Summary.ToolCounts.WriteUnknown, "unknown counts survive full rebuild");
                using (var next = runtime.Start("root", "next", null, new CancellationTokenSource()))
                {
                    var started = owner.CommitRun("next-start", "root", owner.CurrentRun("root"), next.InvocationId, CheckpointState("next", RunLifecycle.Running, 0, 0), policy);
                    var done = owner.CommitRun("next-done", "root", started, next.InvocationId, CheckpointState("next", RunLifecycle.Completed, 0, 0), policy);
                    RuntimeThrows<InvalidOperationException>(() => owner.CommitRun("reuse-run", "root", done, next.InvocationId, CheckpointState("run", RunLifecycle.Running, 0, 0), policy));
                }
            });
        }

        private static void ConversationJournalQueueControls()
        {
            WithTempPaths(paths => {
                var owner = new ConversationJournal(paths, DeliveryTestScope);
                var policy = ConversationRunPolicy.For(ChatModes.Agent);
                foreach (var chat in new[] { "root", "peer" }) owner.CreateChat("create-" + chat, chat, chat, policy);
                var started = owner.CommitRun("start", "root", null, "live-invocation", CheckpointState("run", RunLifecycle.Running, 0, 0), policy);
                var requested = owner.CommitControl("request", "root", null, QueueControlAction.AgentPauseRequested, started);
                AssertEqual(RunLifecycle.Running, owner.CurrentRun("root").Fact.Lifecycle, "durable pause request is not a safe-boundary acknowledgement");
                RuntimeThrows<InvalidOperationException>(() => owner.CommitControl("early-ack", "root", requested, QueueControlAction.AgentPauseAcknowledged, started));
                owner.CommitCall("peer", "call-input", JournalAgentCall("input", "peer-run"));
                var input = owner.SendAgent("input", "peer", "peer-run", "input", AgentChatAddress.IdFor("root"), "peer", "peer-chain", 2, "queued while paused");
                RuntimeThrows<InvalidOperationException>(() => owner.ChangeInput("cannot-deliver", "root", input.Fact.MessageId, 0, ConversationInputStatus.Delivering, "run"));
                AssertEqual(input.Fact.MessageId, owner.PendingAgentInputs("root", 0, 1).Single().Fact.MessageId, "paused input remains visible/pending");
                var pausedState = CheckpointState("run", RunLifecycle.Waiting, 0, 0, pause: requested.Fact.PauseId);
                var paused = owner.CommitRun("paused", "root", started, "live-invocation", pausedState, policy);
                var acknowledged = owner.CommitControl("ack", "root", requested, QueueControlAction.AgentPauseAcknowledged, paused);
                RuntimeThrows<InvalidOperationException>(() => owner.CommitRun("blocked-resume", "root", paused, "live-invocation", CheckpointState("run", RunLifecycle.Running, 0, 0), policy));
                var user = owner.CommitControl("user-stop", "root", acknowledged, QueueControlAction.UserPause);
                RuntimeThrows<InvalidOperationException>(() => owner.CommitControl("stale-release", "root", acknowledged, QueueControlAction.AgentPauseReleased, paused));
                RuntimeThrows<ArgumentException>(() => owner.CommitControl("protected-release", "root", user, QueueControlAction.AgentPauseReleased, paused));
                owner = new ConversationJournal(paths, DeliveryTestScope);
                AssertEqual(QueueControlAction.UserPause, owner.QueueControl("root").Fact.Action, "restart keeps the user pause protected");
                var released = owner.CommitControl("user-resume", "root", owner.QueueControl("root"), QueueControlAction.UserResume);
                AssertEqual(RunLifecycle.Waiting, owner.CurrentRun("root").Fact.Lifecycle, "queue release does not fabricate kernel continuation");
                var running = owner.CommitRun("running", "root", paused, "live-invocation", CheckpointState("run", RunLifecycle.Running, 0, 0), policy);
                owner.ChangeInput("take-input", "root", input.Fact.MessageId, 0, ConversationInputStatus.Delivering, "run");
                var nextRequest = owner.CommitControl("request-2", "root", released, QueueControlAction.AgentPauseRequested, running);
                AssertTrue(nextRequest.Fact.PauseId != requested.Fact.PauseId, "later pause gets a distinct exact token");
                RuntimeThrows<InvalidOperationException>(() => owner.CommitControl("old-pause-release", "root", requested, QueueControlAction.AgentPauseReleased, running));
                var nextPaused = owner.CommitRun("paused-2", "root", running, "live-invocation",
                    CheckpointState("run", RunLifecycle.Waiting, 0, 0, pause: nextRequest.Fact.PauseId), policy);
                var nextAck = owner.CommitControl("ack-2", "root", nextRequest, QueueControlAction.AgentPauseAcknowledged, nextPaused);
                owner.Journal.Fault = point => { if (point == "after_flush") throw new IOException("release acknowledgement lost"); };
                RuntimeThrows<IOException>(() => owner.CommitControl("release-2", "root", nextAck, QueueControlAction.AgentPauseReleased, nextPaused));
                owner = new ConversationJournal(paths, DeliveryTestScope);
                AssertTrue(!owner.QueueControl("root").Fact.Paused, "lost release acknowledgement recovers its exact accepted state");
                var recovery = owner.CommitControl("recovery", "root", owner.QueueControl("root"), QueueControlAction.RecoveryPause);
                var bytes = owner.Journal.WrittenBytes;
                owner.CommitControl("release-2", "root", nextAck, QueueControlAction.AgentPauseReleased, nextPaused);
                AssertEqual(bytes, owner.Journal.WrittenBytes, "old receipt retry appends nothing");
                AssertEqual(recovery.Position.Hash, owner.QueueControl("root").Position.Hash, "old release receipt cannot undo a newer recovery pause");
                owner.Rebuild();
                AssertEqual(QueueControlAction.RecoveryPause, owner.QueueControl("root").Fact.Action, "rebuild never grants live peer resume authority");
                AssertEqual(RunLifecycle.Waiting, owner.CurrentRun("root").Fact.Lifecycle, "rebuild retains the kernel boundary");
            });
        }

        private static AgentRunState CheckpointState(string run, RunLifecycle lifecycle, int iterations, int steps,
            PendingToolWait pending = null, ToolCounts counts = null, string pause = null)
        { return new AgentRunState(new RunSummary(run, "turn-" + run, lifecycle, counts ?? new ToolCounts(), iterations, steps, "", null, null, pending, pause), new AgentRunLimits(10, 20)); }
    }
}
