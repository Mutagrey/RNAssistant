using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
        private static void RunViewStateProjectsRuntimeEvidence()
        {
            var session = RunViewSession(new RunSummary("run-view", "turn-view", RunLifecycle.Completed,
                new ToolCounts(readOk: 1, writeOk: 2, writeError: 1, writeUnknown: 1),
                2, 4, "Model says everything succeeded.", "done", null));
            session.Messages.Add(EffectMessage("run-view", "changed", ToolEffectEvidence.VerifiedChange));
            session.Messages.Add(EffectMessage("run-view", "no-change", ToolEffectEvidence.VerifiedNoChange));
            session.Messages.Add(EffectMessage("run-view", "unknown", ToolEffectEvidence.Unknown));

            var view = RunViewStateProjector.Create(session);
            AssertEqual(RunViewLifecycles.Completed, view.Lifecycle, "kernel owns lifecycle");
            AssertEqual(RunViewHealth.Unknown, view.ExecutionHealth, "unknown effect dominates model narrative");
            AssertEqual(1, view.SuccessfulReads, "read count projected");
            AssertEqual(1, view.VerifiedWrites, "verified change projected");
            AssertEqual(1, view.NoChangeWrites, "verified no-change projected");
            AssertEqual(0, view.UnverifiedWrites, "verified writes are not marked unverified");
            AssertEqual(1, view.FailedCalls, "failed calls projected without prose parsing");
            AssertEqual(1, view.UnknownEffects, "unknown effects projected");
            AssertEqual("Model says everything succeeded.", view.Narrative, "narrative is preserved as data");

            var unverified = RunViewSession(new RunSummary("run-unverified", "turn-unverified", RunLifecycle.Completed,
                new ToolCounts(writeOk: 1), 1, 1, "Changed.", null, null));
            unverified.Messages.Add(EffectMessage("run-unverified", "legacy-write", ToolEffectEvidence.Unreported));
            var conservative = RunViewStateProjector.Create(unverified);
            AssertEqual(1, conservative.UnverifiedWrites, "successful write without effect proof stays unverified");
            AssertEqual(1, conservative.UnknownEffects, "unverified write remains an unknown effect");
            AssertEqual(RunViewHealth.Unknown, conservative.ExecutionHealth, "unverified write cannot render clean");

            var inconsistent = RunViewSession(new RunSummary("run-inconsistent", "turn-inconsistent", RunLifecycle.Completed,
                new ToolCounts(writeOk: 1), 1, 1, "Changed.", null, null));
            inconsistent.Messages.Add(EffectMessage("run-inconsistent", "change", ToolEffectEvidence.VerifiedChange));
            inconsistent.Messages.Add(EffectMessage("run-inconsistent", "extra", ToolEffectEvidence.VerifiedNoChange));
            var bounded = RunViewStateProjector.Create(inconsistent);
            AssertEqual(1, bounded.VerifiedWrites, "effect evidence cannot overstate successful writes");
            AssertEqual(0, bounded.NoChangeWrites, "overlapping effect evidence is capped by runtime count");
            AssertEqual(1, bounded.UnknownEffects, "inconsistent source evidence is visible as unknown");

            var failedNoChange = RunViewSession(new RunSummary(
                "run-failed-no-change", "turn-failed-no-change", RunLifecycle.Completed,
                new ToolCounts(writeError: 1), 1, 1, "Write failed.", null, null));
            failedNoChange.Messages.Add(EffectMessage(
                "run-failed-no-change", "failed-write", ToolEffectEvidence.VerifiedNoChange));
            var failedView = RunViewStateProjector.Create(failedNoChange);
            AssertEqual(RunViewHealth.Errors, failedView.ExecutionHealth,
                "verified no-change on a failed write remains a definite error");
            AssertEqual(1, failedView.FailedCalls, "failed write remains counted");
            AssertEqual(0, failedView.UnknownEffects,
                "failed verified-no-change evidence is not misclassified as unknown");
            RuntimeThrows<ArgumentException>(() => new RunViewState("invalid", "turn-invalid", "",
                RunViewLifecycles.Completed, RunViewHealth.Clean, 0, 0, 0, 0, 0, 1,
                null, null, "", DateTime.UtcNow));
        }

        private static void RunViewStateProjectsPendingConfirmation()
        {
            var call = new ToolCall("call-pending", "excel.write_range", "{\"address\":\"A1\"}");
            var policy = new ToolPolicySnapshot(call.Name, "policy-rev", true, true);
            var pending = new PendingConfirmation("pending-1", call, policy, "step-1", 1);
            var session = RunViewSession(new RunSummary("run-pending", "turn-pending",
                RunLifecycle.AwaitingConfirmation, new ToolCounts(writeOk: 1), 1, 1, "Please confirm.", null, pending));
            var prior = EffectMessage("run-before-confirmation", "call-before", ToolEffectEvidence.VerifiedChange);
            prior.RunViewState = new RunViewState("run-before-confirmation", "turn-pending", "Earlier step.",
                RunViewLifecycles.Completed, RunViewHealth.Clean, 0, 1, 0, 0, 0, 0,
                null, null, "Earlier step.", DateTime.UtcNow);
            session.Messages.Add(prior);

            var view = RunViewStateProjector.Create(session);
            AssertEqual(RunViewLifecycles.AwaitingConfirmation, view.Lifecycle, "pending lifecycle is typed");
            AssertEqual("pending-1", view.PendingConfirmation.PendingId, "pending id projected from kernel");
            AssertEqual("call-pending", view.PendingConfirmation.ToolCallId, "call id projected from kernel");
            AssertEqual("excel.write_range", view.PendingConfirmation.ToolName, "tool name projected from kernel");
            AssertEqual(1, view.VerifiedWrites,
                "confirmation continuation retains source evidence from the same logical turn");
        }

        private static void RunViewStateReplayEqualityAndImmutableWire()
        {
            WithTempPaths(paths =>
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var session = NewSession(adapter);
                session.LastRun = new ChatRunRecord
                {
                    RunId = "run-replay-view",
                    TurnId = "turn-replay-view",
                    RuntimeId = "runtime",
                    KernelState = new AgentRunState(new RunSummary("run-replay-view", "turn-replay-view",
                        RunLifecycle.Completed, new ToolCounts(readOk: 1, writeOk: 1), 1, 1,
                        "Saved narrative.", null, null), new AgentRunLimits(4, 4)),
                    CurrentAction = "Saved narrative.",
                    StartedUtc = DateTime.UtcNow
                };
                session.Messages.Add(EffectMessage("run-replay-view", "write", ToolEffectEvidence.VerifiedChange));
                RunViewStateProjector.StampCurrentRun(session);
                var before = RunViewStateProjector.Create(session);
                var store = new ChatStore(paths);
                store.Save(session);

                var loaded = store.Load(session.Host, session.DocumentKey, session.Id);
                var after = RunViewStateProjector.Create(loaded);
                AssertEqual(JsonConvert.SerializeObject(before), JsonConvert.SerializeObject(after),
                    "restart replay produces equal run view state");
                AssertEqual(JsonConvert.SerializeObject(after),
                    JsonConvert.SerializeObject(store.ListHeaders().Single(item => item.Id == session.Id).RunViewState),
                    "chat header replays the same run view state");
                AssertTrue(typeof(RunViewState).GetProperties().All(property =>
                    property.SetMethod == null || !property.SetMethod.IsPublic), "run view state has no public mutation surface");
                AssertTrue(JObject.FromObject(loaded.LastRun)["ExecutionSummary"] == null &&
                    loaded.Messages.All(message => JObject.FromObject(message)["ExecutionSummary"] == null),
                    "new run projection persists no flat execution summary");
                var legacyMessage = JsonConvert.DeserializeObject<ChatMessage>(
                    "{\"Role\":\"assistant\",\"ExecutionSummary\":{\"ExecutionHealth\":\"clean\",\"WriteOk\":9}}");
                AssertTrue(JObject.FromObject(legacyMessage)["ExecutionSummary"] == null,
                    "obsolete flat fields are ignored instead of becoming compatibility authority");
            });
        }

        private static ChatSession RunViewSession(RunSummary summary)
        {
            return new ChatSession
            {
                Messages = new System.Collections.Generic.List<ChatMessage>(),
                LastRun = new ChatRunRecord
                {
                    RunId = summary.RunId,
                    TurnId = summary.TurnId,
                    KernelState = new AgentRunState(summary, new AgentRunLimits(8, 8)),
                    CurrentAction = summary.AssistantMessage,
                    StartedUtc = DateTime.UtcNow
                }
            };
        }

        private static ChatMessage EffectMessage(string runId, string callId, ToolEffectEvidence effect)
        {
            return new ChatMessage
            {
                Role = "assistant",
                RunId = runId,
                Activity = new ChatActivity
                {
                    RunId = runId,
                    Kind = "tool",
                    ToolCallId = callId,
                    ExecutionEvidence = new ToolExecutionEvidence(
                        ToolDispatchEvidence.MayHaveDispatched, effect)
                }
            };
        }
    }
}
