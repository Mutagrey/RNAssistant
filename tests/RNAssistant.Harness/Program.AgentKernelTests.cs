using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Agent;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static AgentResponseDraft KernelResponse(params ToolCallDraft[] calls)
        {
            return new AgentResponseDraft("Done; all changes applied.", calls);
        }

        private static ToolCallDraft KernelCall(string name = "write", string argumentsJson = "{}")
        {
            return new ToolCallDraft(name, argumentsJson);
        }

        private static ToolExecutionRecord KernelRecord(ToolExecutionContext context,
            ToolExecutionOutcome outcome = ToolExecutionOutcome.Ok, bool awaitingUser = false)
        {
            var pending = outcome == ToolExecutionOutcome.AwaitingConfirmation;
            return new ToolExecutionRecord(context, outcome, context.StartedUtc.AddMilliseconds(1),
                "Runtime evidence.", "{\"source\":\"runtime\"}", mayHaveDispatched: !pending,
                pendingId: pending ? "pending-" + context.Call.Id : null, awaitingUser: awaitingUser);
        }

        private static string KernelCounts(RunSummary summary)
        {
            var c = summary.ToolCounts;
            return string.Join(",", c.ReadOk, c.ReadError, c.WriteOk, c.WriteError, c.WriteUnknown);
        }

        private static async Task KernelAggregatesOutcome(string tool, ToolExecutionOutcome outcome,
            ExecutionHealth health, string counts)
        {
            var f = new KernelFixture(KernelResponse(KernelCall(tool)), KernelResponse());
            f.Tools.OnExecute = (context, token) => Task.FromResult(KernelRecord(context, outcome));
            var result = await f.RunAsync();
            AssertEqual(RunLifecycle.Completed, result.Summary.Lifecycle, "empty calls finish the loop");
            AssertEqual(health, result.Summary.ExecutionHealth, "runtime evidence owns health");
            AssertEqual(counts, KernelCounts(result.Summary), "actual tool counts");
            AssertEqual("Done; all changes applied.", result.Summary.AssistantMessage, "narrative remains separate");
            AssertEqual(1, f.Tools.Calls.Count, "no tool retry");
            AssertEqual(AgentRunEventKind.SummaryChanged, f.Store.Events.Last().Kind, "terminal summary is last");
            AssertEqual(counts, KernelCounts(f.Store.Events.Last().Summary), "append receives authoritative summary");
        }

        private static async Task KernelPreservesCumulativeHealth(ToolExecutionOutcome first, ToolExecutionOutcome second,
            ExecutionHealth health, string counts)
        {
            var f = new KernelFixture(KernelResponse(KernelCall()), KernelResponse(KernelCall()), KernelResponse());
            var outcomes = new Queue<ToolExecutionOutcome>(new[] { first, second });
            f.Tools.OnExecute = (context, token) => Task.FromResult(KernelRecord(context, outcomes.Dequeue()));
            var result = await f.RunAsync();
            AssertEqual(health, result.Summary.ExecutionHealth, "later successes cannot clear earlier evidence");
            AssertEqual(counts, KernelCounts(result.Summary), "both executions counted once");
            AssertEqual(3, f.Model.Requests.Count, "ordinary next step without automatic retry");
            var firstFact = f.Store.Events.First(e => e.Kind == AgentRunEventKind.ToolCompleted);
            AssertEqual(1, firstFact.Summary.ToolStepsUsed, "earlier summary snapshot is unchanged");
        }

        private static async Task KernelNarrativeCannotClaimEffects()
        {
            foreach (var message in new[] { "completed", "blocked", "refused" })
            {
                var f = new KernelFixture(new AgentResponseDraft(message, new ToolCallDraft[0]));
                var result = await f.RunAsync();
                AssertEqual(RunLifecycle.Completed, result.Summary.Lifecycle, "wording is not runtime lifecycle");
                AssertEqual(ExecutionHealth.Clean, result.Summary.ExecutionHealth, "no effects is not an error");
                AssertEqual("0,0,0,0,0", KernelCounts(result.Summary), "no invented writes");
                AssertEqual(message, result.Summary.AssistantMessage, "narrative preserved");
            }
        }

        private static async Task KernelContinuesAfterNoToolCheckpoint()
        {
            var f = new KernelFixture(
                new AgentResponseDraft("Составляю итог.", new ToolCallDraft[0], false),
                KernelResponse());
            var result = await f.RunAsync();
            AssertEqual(RunLifecycle.Completed, result.Summary.Lifecycle, "non-final empty calls do not finish the loop");
            AssertEqual(2, f.Model.Requests.Count, "kernel asks the model for the final response after a no-tool checkpoint");
            AssertEqual(2, f.Store.Events.Count(e => e.Kind == AgentRunEventKind.ResponseAccepted),
                "checkpoint and final response are both accepted");
            AssertEqual("Done; all changes applied.", result.Summary.AssistantMessage,
                "final response owns the terminal message");
        }

        private static async Task KernelFailsRepeatedNoToolCheckpoints()
        {
            var f = new KernelFixture(
                new AgentResponseDraft("One.", new ToolCallDraft[0], false),
                new AgentResponseDraft("Two.", new ToolCallDraft[0], false),
                new AgentResponseDraft("Three.", new ToolCallDraft[0], false));
            var result = await f.RunAsync();
            AssertEqual(RunLifecycle.Failed, result.Summary.Lifecycle, "repeated no-tool checkpoints fail closed");
            AssertEqual("model_loop_stalled", result.Summary.Reason, "stalled no-tool loop has explicit reason");
            AssertEqual(3, f.Model.Requests.Count, "stall is bounded");
            AssertEqual(0, f.Tools.Calls.Count, "stalled checkpoint loop dispatches no tools");
        }

        private static async Task KernelReadsAreSequentialAndBounded()
        {
            var f = new KernelFixture(KernelResponse(KernelCall("read"), KernelCall("read")), KernelResponse());
            var result = await f.RunAsync();
            AssertEqual("call_1,call_2", string.Join(",", f.Tools.Calls.Select(c => c.Call.Id)), "allocated batch order");
            AssertEqual("2,0,0,0,0", KernelCounts(result.Summary), "read-only batch accounting");
            AssertEqual("call_1,call_2", string.Join(",", f.Model.Requests[1].AcceptedMessages
                .Where(m => m.Kind == AgentMessageKind.ToolResult).Select(m => m.ToolCallId)), "closed exchange before next request");
            AssertEqual("call_1,call_2", string.Join(",", f.Model.Requests[1].AcceptedMessages.SelectMany(m => m.ToolCalls)
                .Select(call => call.Id)), "accepted history retains the runtime correlation ids");
        }

        private static async Task KernelRejectsUnsafeBatches()
        {
            foreach (var tool in new[] { "write", "external", "confirm", "unclassified" })
            {
                var f = new KernelFixture(KernelResponse(KernelCall("read"), KernelCall(tool)));
                var result = await f.RunAsync();
                AssertEqual(RunLifecycle.Failed, result.Summary.Lifecycle, "unsafe accepted response fails closed");
                AssertEqual(0, f.Tools.Calls.Count, "whole response rejected before any dispatch");
                AssertEqual(1, result.AcceptedMessages.Count, "rejected response absent from history");
                AssertTrue(!f.Store.Events.Any(e => e.Kind == AgentRunEventKind.ResponseAccepted), "no rejected durable response");
            }
        }

        private static async Task KernelRejectsAllocationCollisions(bool acrossSteps)
        {
            var f = acrossSteps
                ? new KernelFixture(KernelResponse(KernelCall("read")),
                    KernelResponse(KernelCall("read"), KernelCall("read")))
                : new KernelFixture(KernelResponse(KernelCall("read"), KernelCall("read")));
            var allocated = new Queue<string>(acrossSteps
                ? new[] { "call_1", "call_2", "CALL_1" } : new[] { "call_1", "CALL_1" });
            f.NewCallId = () => allocated.Dequeue();
            var result = await f.RunAsync();
            AssertEqual(RunLifecycle.Failed, result.Summary.Lifecycle, "runtime id collision fails closed");
            AssertEqual("call_id_allocation_failed", result.Summary.Reason, "collision is an infrastructure failure, not invalid model output");
            AssertEqual(acrossSteps ? 1 : 0, f.Tools.Calls.Count, "rejected batch has no effects");
            AssertEqual(acrossSteps ? 1 : 0, f.Store.Events.Count(e => e.Kind == AgentRunEventKind.ResponseAccepted),
                "only accepted responses persist");
            AssertEqual(acrossSteps ? 2 : 1, f.Model.Requests.Count, "allocator collision does not request model repair");
            AssertEqual(acrossSteps ? 3 : 2, f.AllocationCount, "colliding allocation is not retried");
            AssertTrue(!result.AcceptedMessages.SelectMany(m => m.ToolCalls).Any(c => c.Id == "call_2"),
                "partially allocated response is not accepted");
        }

        private static async Task KernelAllocatesUniqueIdsForRepeatedCalls()
        {
            const string arguments = "{\"query\":\"same input\",\"note\":\"kept exactly\"}";
            var repeated = KernelCall("read", arguments);
            var f = new KernelFixture(KernelResponse(repeated, repeated), KernelResponse(repeated), KernelResponse());
            // Exercise the production generator, not the fixture's deterministic allocator.
            var kernel = new AgentKernel(f.Model, f.Tools, f.Store);
            var result = await kernel.RunAsync(new AgentRunRequest("run", "turn", "Read the same input.",
                new AgentRunLimits(5, 5)), CancellationToken.None);
            var accepted = f.Store.Events.Where(e => e.Kind == AgentRunEventKind.ResponseAccepted)
                .SelectMany(e => e.Response.ToolCalls).ToArray();
            AssertEqual(RunLifecycle.Completed, result.Summary.Lifecycle, "identical payloads remain ordinary calls");
            AssertEqual(3, accepted.Length, "each draft occurrence receives its own accepted call");
            AssertEqual(3, accepted.Select(call => call.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                "same draft in a batch and a later step receives distinct runtime ids");
            AssertTrue(accepted.All(call => call.Id.StartsWith("call_", StringComparison.Ordinal) && call.Id.Length <= 64 &&
                call.Id.All(character => character >= 'A' && character <= 'Z' || character >= 'a' && character <= 'z' ||
                    character >= '0' && character <= '9' || character == '_' || character == '-')),
                "default ids are safe for native tool history");
            AssertTrue(accepted.All(call => call.Name == repeated.Name && call.ArgumentsJson == arguments),
                "allocation preserves the exact model payload");
            AssertTrue(accepted.Select(call => call.Id).SequenceEqual(f.Tools.Calls.Select(context => context.Call.Id)),
                "execution retains ids and ordering from accepted persistence");
            AssertEqual("3,0,0,0,0", KernelCounts(result.Summary), "identical calls are not silently deduplicated");
            AssertEqual(3, f.Model.Requests.Count, "allocation adds no model request or repair");
        }

        private static async Task KernelRejectsInvalidAllocatorOutput()
        {
            foreach (var allocate in new Func<string>[]
            {
                () => null, () => string.Empty, () => " ", () => "unsafe.id", () => new string('a', 65),
                () => { throw new InvalidOperationException("Allocator unavailable."); }
            })
            {
                var f = new KernelFixture(KernelResponse(KernelCall()));
                f.NewCallId = allocate;
                var result = await f.RunAsync();
                AssertEqual(RunLifecycle.Failed, result.Summary.Lifecycle, "allocator failure stops the invocation");
                AssertEqual("call_id_allocation_failed", result.Summary.Reason, "allocator faults remain infrastructure failures");
                AssertEqual(ExecutionHealth.Clean, result.Summary.ExecutionHealth, "allocation cannot invent an execution effect");
                AssertEqual(0, f.Tools.Calls.Count, "invalid runtime id never reaches dispatch");
                AssertEqual(1, result.AcceptedMessages.Count, "failed allocation is absent from accepted history");
                AssertTrue(!f.Store.Events.Any(e => e.Kind == AgentRunEventKind.ResponseAccepted), "no partial accepted response is persisted");
                AssertEqual(1, f.Model.Requests.Count, "allocator faults do not trigger model repair");
                AssertEqual(1, f.AllocationCount, "allocator faults are not retried");
            }
        }

        private static async Task KernelRequestsAreDetachedSnapshots()
        {
            var oldCall = new ToolCall("prior_read", "read", "{}");
            var history = new List<AgentMessage> { AgentMessage.User("old turn"),
                AgentMessage.Assistant(new AgentResponse("Prior accepted read.", new[] { oldCall })),
                AgentMessage.AcceptedToolResult(oldCall.Id, "old result", "{}") };
            var f = new KernelFixture(KernelResponse(KernelCall("read")), KernelResponse());
            var request = new AgentRunRequest("run", "turn", "new turn", new AgentRunLimits(5, 5), history);
            history.Clear();
            var result = await f.Kernel.RunAsync(request, CancellationToken.None);
            AssertEqual(RunLifecycle.Completed, result.Summary.Lifecycle, "prior accepted history remains usable");
            AssertEqual(4, f.Model.Requests[0].AcceptedMessages.Count, "first request is not mutated by later steps");
            AssertEqual("prior_read", string.Join(",", f.Model.Requests[0].AcceptedMessages.SelectMany(m => m.ToolCalls)
                .Select(call => call.Id)), "first request keeps original accepted history snapshot");
            AssertEqual("prior_read,call_1", string.Join(",", f.Model.Requests[1].AcceptedMessages.SelectMany(m => m.ToolCalls)
                .Select(call => call.Id)), "next request gains only the accepted runtime call");
            AssertEqual(0, f.Store.Events[0].Summary.IterationsUsed, "initial summary remains unchanged");
        }

        private static async Task KernelClassifiesModelFailures()
        {
            foreach (ModelProtocolFailureKind kind in Enum.GetValues(typeof(ModelProtocolFailureKind)))
            {
                var f = new KernelFixture();
                f.Model.OnSend = (request, token) => Task.FromResult(AgentModelResult.Failed(kind, "boundary failure"));
                var result = await f.RunAsync();
                AssertEqual(kind == ModelProtocolFailureKind.Cancelled ? RunLifecycle.Cancelled : RunLifecycle.Failed,
                    result.Summary.Lifecycle, "typed failure classification");
                AssertEqual(ExecutionHealth.Clean, result.Summary.ExecutionHealth, "model failure does not invent tool errors");
                AssertEqual(1, f.Model.Requests.Count, "kernel has no protocol retries");
                AssertEqual(1, result.AcceptedMessages.Count, "failure is not accepted model history");
                AssertEqual(0, f.AllocationCount, "failed model response allocates no call ids");
            }
            var refusal = new KernelFixture();
            refusal.Model.OnSend = (request, token) => Task.FromResult(AgentModelResult.Refused("Provider refusal."));
            var refused = await refusal.RunAsync();
            AssertEqual("provider_refused", refused.Summary.Reason, "native refusal locally classified");
            AssertEqual(RunLifecycle.Failed, refused.Summary.Lifecycle, "refusal is not model-owned status");
            AssertEqual(1, refused.AcceptedMessages.Count, "native refusal stays out of conversation JSON");
            AssertEqual(0, refusal.AllocationCount, "native refusal allocates no call ids");
        }

        private static async Task KernelCancellationBeforeDispatch(AgentRunEventKind boundary, bool duringPolicyLookup = false)
        {
            using (var cancellation = new CancellationTokenSource())
            {
                var f = new KernelFixture(KernelResponse(KernelCall()));
                f.Store.OnAppended = fact =>
                {
                    if (fact.Kind != boundary) return;
                    if (duringPolicyLookup) f.Tools.BeforeDescribe = () => cancellation.Cancel();
                    else cancellation.Cancel();
                };
                var result = await f.RunAsync(cancellation.Token);
                AssertEqual(RunLifecycle.Cancelled, result.Summary.Lifecycle, "cancelled at mandatory append boundary");
                var beforeModel = boundary == AgentRunEventKind.Started || boundary == AgentRunEventKind.ModelStepStarted;
                AssertEqual(beforeModel ? 0 : 1, f.Model.Requests.Count, "no cancelled model dispatch");
                AssertEqual(0, f.Tools.Calls.Count, "no cancelled tool dispatch");
                AssertEqual("0,0,0,0,0", KernelCounts(result.Summary), "non-dispatch never invents effects");
                if (!beforeModel)
                    AssertEqual(1, result.AcceptedMessages.Count(m => m.Kind == AgentMessageKind.ToolResult), "accepted call closed");
            }
        }

        private static async Task KernelIgnoresLateCancelledResponse()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                var f = new KernelFixture();
                f.Model.OnSend = (request, token) =>
                {
                    cancellation.Cancel();
                    return Task.FromResult(AgentModelResult.Accepted(KernelResponse(KernelCall())));
                };
                var result = await f.RunAsync(cancellation.Token);
                AssertEqual(RunLifecycle.Cancelled, result.Summary.Lifecycle, "late response cannot revive cancelled run");
                AssertEqual(0, f.Tools.Calls.Count, "no late dispatch");
                AssertEqual(1, result.AcceptedMessages.Count, "late response not accepted");
                AssertEqual(0, f.AllocationCount, "late cancelled draft never receives an id");
            }
        }

        private static async Task KernelCancellationAfterPossibleDispatch(bool write)
        {
            var f = new KernelFixture(KernelResponse(KernelCall(write ? "write" : "read")));
            f.Tools.OnExecute = (context, token) => { throw new OperationCanceledException("Executor interrupted."); };
            var result = await f.RunAsync();
            AssertEqual(RunLifecycle.Cancelled, result.Summary.Lifecycle, "executor cancellation");
            AssertEqual(write ? ExecutionHealth.Unknown : ExecutionHealth.Errors, result.Summary.ExecutionHealth,
                "possible effects never become clean");
            AssertEqual(write ? "0,0,0,0,1" : "0,1,0,0,0", KernelCounts(result.Summary), "conservative missing terminal evidence");
            AssertEqual(1, f.Tools.Calls.Count, "no retry after ambiguous execution");
            AssertTrue(f.Store.Events.Single(e => e.Execution != null).Execution.MayHaveDispatched, "runtime entry is only possible dispatch");
            AssertEqual(write ? ToolExecutionOutcome.Unknown : ToolExecutionOutcome.Error,
                result.AcceptedMessages.Last().Execution.Outcome, "model adapter receives typed synthetic evidence, not message heuristics");
        }

        private static async Task KernelCancellationPreservesTerminalEvidence()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                var f = new KernelFixture(KernelResponse(KernelCall()));
                f.Tools.OnExecute = (context, token) =>
                {
                    cancellation.Cancel();
                    return Task.FromResult(KernelRecord(context));
                };
                var result = await f.RunAsync(cancellation.Token);
                AssertEqual(RunLifecycle.Cancelled, result.Summary.Lifecycle, "cancel stops next model step");
                AssertEqual("0,0,1,0,0", KernelCounts(result.Summary), "known terminal success is not overwritten by cancellation");
                AssertEqual(1, f.Model.Requests.Count, "no model step after cancellation");
            }
        }

        private static async Task KernelCancelledBatchClosesAllCalls()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                var f = new KernelFixture(KernelResponse(KernelCall("read"), KernelCall("read")));
                f.Tools.OnExecute = (context, token) =>
                {
                    cancellation.Cancel();
                    return Task.FromResult(KernelRecord(context));
                };
                var result = await f.RunAsync(cancellation.Token);
                AssertEqual(1, f.Tools.Calls.Count, "rest of batch never dispatched");
                AssertEqual("1,0,0,0,0", KernelCounts(result.Summary), "only actual result counted");
                AssertEqual("call_1,call_2", string.Join(",", result.AcceptedMessages.Where(m => m.Kind == AgentMessageKind.ToolResult)
                    .Select(m => m.ToolCallId)), "all accepted calls closed in returned history");
                AssertEqual(AgentRunEventKind.SummaryChanged, f.Store.Events.Last().Kind, "summary follows closure");
                AssertEqual(ToolExecutionOutcome.NotDispatched, f.Store.Events.Last(e => e.Execution != null).Execution.Outcome,
                    "unexecuted batch tail is explicit");
                AssertEqual(ToolExecutionOutcome.NotDispatched, result.AcceptedMessages.Last().Execution.Outcome,
                    "synthetic non-dispatch remains typed for the model adapter");
            }
        }

        private static async Task KernelHonorsLimits(bool iterations)
        {
            var f = iterations
                ? new KernelFixture(KernelResponse(KernelCall("read")))
                : new KernelFixture(KernelResponse(KernelCall("read"), KernelCall("read")));
            var result = await f.RunAsync(limits: new AgentRunLimits(iterations ? 1 : 5, iterations ? 5 : 1));
            AssertEqual(RunLifecycle.Failed, result.Summary.Lifecycle, "limit is runtime failure");
            AssertEqual(iterations ? "iteration_limit" : "tool_step_limit", result.Summary.Reason, "specific limit");
            AssertEqual(1, f.Tools.Calls.Count, "no execution over budget");
            AssertEqual(1, result.Summary.ToolStepsUsed, "bounded tool accounting");
            AssertEqual("1,0,0,0,0", KernelCounts(result.Summary), "limit cannot erase successful read");
        }

        private static async Task KernelConfirmationSharesAccounting(ToolExecutionOutcome before, ToolExecutionOutcome after,
            ExecutionHealth health, string counts)
        {
            var f = new KernelFixture(KernelResponse(KernelCall()), KernelResponse(KernelCall("confirm")), KernelResponse());
            f.Tools.OnExecute = (context, token) => Task.FromResult(KernelRecord(context, context.Call.Name == "write"
                ? before : context.IsConfirmed ? after : ToolExecutionOutcome.AwaitingConfirmation));
            var paused = await f.RunAsync(limits: new AgentRunLimits(3, 2));
            AssertEqual(RunLifecycle.AwaitingConfirmation, paused.Summary.Lifecycle, "runtime owns pause");
            AssertEqual(2, paused.Summary.ToolStepsUsed, "pending step precharged once");
            var pending = paused.Summary.PendingConfirmation;
            AssertEqual(0, paused.AcceptedMessages.Count(m => m.Kind == AgentMessageKind.ToolResult && m.ToolCallId == pending.Call.Id),
                "pending is not a fabricated execution result");
            var resumed = await f.Kernel.ResumeAsync("resume", pending.PendingId, paused.Continuation, CancellationToken.None);
            AssertEqual(RunLifecycle.Completed, resumed.Summary.Lifecycle, "same loop resumes");
            AssertEqual(health, resumed.Summary.ExecutionHealth, "cumulative health preserved across confirmation");
            AssertEqual(counts, KernelCounts(resumed.Summary), "confirmed call counted exactly once");
            AssertEqual(2, resumed.Summary.ToolStepsUsed, "resume does not double-charge tool budget");
            AssertEqual(3, resumed.Summary.IterationsUsed, "resume shares model budget");
            AssertEqual("resume", f.Tools.Calls.Last().RunId, "new execution run");
            AssertEqual("turn", f.Tools.Calls.Last().TurnId, "same user turn");
            AssertEqual(f.Tools.Calls[1].StepId, f.Tools.Calls[2].StepId, "original model step retained");
            AssertEqual(1, f.Tools.Calls.Last().RemainingToolSteps, "pending reservation reusable only for the confirmed call");
            AssertEqual("call_1,call_2", string.Join(",", f.Model.Requests.Last().AcceptedMessages.SelectMany(m => m.ToolCalls)
                .Select(call => call.Id)), "resume retains runtime ids in accepted history");
            AssertEqual(pending.Call.Id, f.Tools.Calls.Last().Call.Id, "confirmation reuses the accepted call id");
            AssertEqual(2, f.AllocationCount, "confirmation and final response allocate no new ids");
            AssertEqual(RunLifecycle.AwaitingConfirmation, paused.Summary.Lifecycle, "pause snapshot immutable");
        }

        private static async Task KernelRejectsAllocationCollisionAfterConfirmation()
        {
            var f = new KernelFixture(KernelResponse(KernelCall("confirm")), KernelResponse(KernelCall("read")));
            var allocated = new Queue<string>(new[] { "call_1", "CALL_1" });
            f.NewCallId = () => allocated.Dequeue();
            f.Tools.OnExecute = (context, token) => Task.FromResult(KernelRecord(context,
                context.IsConfirmed ? ToolExecutionOutcome.Ok : ToolExecutionOutcome.AwaitingConfirmation));
            var paused = await f.RunAsync();
            var pending = paused.Summary.PendingConfirmation;
            var resumed = await f.Kernel.ResumeAsync("resume", pending.PendingId, paused.Continuation, CancellationToken.None);
            AssertEqual(RunLifecycle.Failed, resumed.Summary.Lifecycle, "allocation checks retained ids after confirmation");
            AssertEqual("call_id_allocation_failed", resumed.Summary.Reason, "post-confirmation collision remains an infrastructure failure");
            AssertEqual(2, f.Tools.Calls.Count, "only pending and confirmed dispatches enter runtime");
            AssertEqual(2, f.AllocationCount, "resume reuses the pending id and does not retry a later collision");
            AssertEqual(2, f.Model.Requests.Count, "allocation failure does not start model repair");
            AssertEqual(1, f.Store.Events.Count(e => e.Kind == AgentRunEventKind.ResponseAccepted),
                "colliding response is absent from durable history");
            AssertEqual(pending.Call.Id, resumed.AcceptedMessages.Single(m => m.Kind == AgentMessageKind.ToolResult).ToolCallId,
                "confirmed result keeps its original correlation id");
            AssertEqual("0,0,1,0,0", KernelCounts(resumed.Summary), "confirmed effect preserved despite allocation failure");
        }

        private static async Task KernelRestoredContinuationDoesNotAllocatePendingId()
        {
            const string arguments = "{\"source\":\"original accepted input\"}";
            var f = new KernelFixture(KernelResponse(KernelCall("confirm", arguments)), KernelResponse());
            f.Tools.OnExecute = (context, token) => Task.FromResult(KernelRecord(context,
                context.IsConfirmed ? ToolExecutionOutcome.Ok : ToolExecutionOutcome.AwaitingConfirmation));
            var paused = await f.RunAsync();
            var pending = paused.Summary.PendingConfirmation;
            var restored = AgentRunContinuation.Restore(paused.Summary, paused.Continuation.Limits,
                paused.Continuation.Revision, paused.AcceptedMessages);
            var resumeAllocations = 0;
            var kernel = new AgentKernel(f.Model, f.Tools, f.Store, newCallId: () =>
            {
                resumeAllocations++;
                throw new InvalidOperationException("Pending call must already have an id.");
            });
            var resumed = await kernel.ResumeAsync("new-runtime-run", pending.PendingId, restored, CancellationToken.None);
            AssertEqual(RunLifecycle.Completed, resumed.Summary.Lifecycle, "restored continuation resumes in a new kernel");
            AssertEqual(0, resumeAllocations, "restoring and executing a pending call does not generate a replacement id");
            AssertEqual(1, f.AllocationCount, "one id was allocated before the original acceptance");
            AssertEqual(pending.Call.Id, restored.AcceptedCallIds.Single(), "restored run retains its accepted id scope");
            AssertEqual(2, f.Tools.Calls.Count, "one pending entry and one confirmed execution");
            AssertTrue(f.Tools.Calls.All(context => context.Call.Id == pending.Call.Id && context.Call.ArgumentsJson == arguments),
                "pending and confirmed execution retain the original id and payload");
            AssertEqual(f.Tools.Calls[0].StepId, f.Tools.Calls[1].StepId, "confirmation preserves the original model step");
            AssertEqual("new-runtime-run", f.Tools.Calls.Last().RunId, "runtime run changes on resume");
            AssertEqual(paused.Summary.TurnId, f.Tools.Calls.Last().TurnId, "logical turn remains unchanged");
            AssertEqual(pending.Call.Id, resumed.AcceptedMessages.Single(m => m.Kind == AgentMessageKind.ToolResult).ToolCallId,
                "confirmed result pairs with the persisted accepted call");
            AssertEqual("0,0,1,0,0", KernelCounts(resumed.Summary), "confirmation records exactly one effect");
        }

        private static async Task KernelRejectsStaleConfirmation()
        {
            var f = new KernelFixture(KernelResponse(KernelCall("confirm")), KernelResponse());
            f.Tools.OnExecute = (context, token) => Task.FromResult(KernelRecord(context,
                context.IsConfirmed ? ToolExecutionOutcome.Ok : ToolExecutionOutcome.AwaitingConfirmation));
            var paused = await f.RunAsync();
            await KernelThrowsAsync<InvalidOperationException>(() =>
                f.Kernel.ResumeAsync("wrong", "different", paused.Continuation, CancellationToken.None));
            AssertEqual(1, f.Tools.Calls.Count, "wrong pending id cannot execute");
            var pendingId = paused.Summary.PendingConfirmation.PendingId;
            await f.Kernel.ResumeAsync("resume", pendingId, paused.Continuation, CancellationToken.None);
            await KernelThrowsAsync<RunStoreException>(() =>
                f.Kernel.ResumeAsync("again", pendingId, paused.Continuation, CancellationToken.None));
            AssertEqual(2, f.Tools.Calls.Count, "stale cursor rejected before a second confirmed dispatch");
        }

        private static async Task KernelCancelsPendingWithoutDanglingCall()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                var f = new KernelFixture(KernelResponse(KernelCall("confirm")));
                f.Tools.OnExecute = (context, token) =>
                {
                    cancellation.Cancel();
                    return Task.FromResult(KernelRecord(context, ToolExecutionOutcome.AwaitingConfirmation));
                };
                var result = await f.RunAsync(cancellation.Token);
                AssertEqual(RunLifecycle.Cancelled, result.Summary.Lifecycle, "cancellation beats unexecuted pending");
                AssertTrue(result.Continuation == null && result.Summary.PendingConfirmation == null, "no resumable cancelled confirmation");
                AssertEqual("0,0,0,0,0", KernelCounts(result.Summary), "pending never counted as effect");
                AssertEqual(1, result.AcceptedMessages.Count(m => m.Kind == AgentMessageKind.ToolResult), "cancelled call closed");
            }
        }

        private static async Task KernelPolicyChangeStopsDispatch(bool confirmation)
        {
            var f = new KernelFixture(KernelResponse(KernelCall(confirmation ? "confirm" : "write")), KernelResponse());
            if (confirmation)
            {
                f.Tools.OnExecute = (context, token) => Task.FromResult(KernelRecord(context, ToolExecutionOutcome.AwaitingConfirmation));
                var paused = await f.RunAsync();
                f.Tools.Policies["confirm"] = new ToolPolicySnapshot("confirm", "changed", true, true);
                var result = await f.Kernel.ResumeAsync("resume", paused.Summary.PendingConfirmation.PendingId,
                    paused.Continuation, CancellationToken.None);
                AssertEqual(1, f.Tools.Calls.Count, "changed confirmed policy never executes");
                AssertEqual("0,0,0,1,0", KernelCounts(result.Summary), "known rejection is error, not unknown effect");
            }
            else
            {
                f.Store.OnAppended = fact =>
                {
                    if (fact.Kind == AgentRunEventKind.ToolStarted)
                        f.Tools.Policies["write"] = new ToolPolicySnapshot("write", "changed", true);
                };
                var result = await f.RunAsync();
                AssertEqual(0, f.Tools.Calls.Count, "changed accepted policy never executes");
                AssertEqual("0,0,0,1,0", KernelCounts(result.Summary), "known pre-dispatch rejection");
            }
            AssertTrue(!f.Store.Events.Single(e => e.Execution != null && e.Execution.Outcome == ToolExecutionOutcome.Error)
                .Execution.MayHaveDispatched, "policy rejection has no ambiguous dispatch");
        }

        private static async Task KernelStoreFailureStopsBeforeDispatch()
        {
            foreach (var boundary in new[] { AgentRunEventKind.Started, AgentRunEventKind.ModelStepStarted,
                AgentRunEventKind.ResponseAccepted, AgentRunEventKind.ToolStarted })
            {
                var f = new KernelFixture(KernelResponse(KernelCall()));
                f.Store.FailOn = fact => fact.Kind == boundary;
                var failure = await KernelThrowsAsync<RunStoreException>(() => f.RunAsync());
                AssertEqual(0, f.Tools.Calls.Count, "mandatory evidence failure prevents tool dispatch");
                AssertEqual("0,0,0,0,0", KernelCounts(failure.UnpersistedSummary), "no invented persisted effects");
                AssertEqual(1, f.Store.FailedAppends, "mandatory append is never retried automatically");
                AssertTrue(!f.Store.Events.Any(e => e.Kind == AgentRunEventKind.SummaryChanged), "no false durable terminal");
                if (boundary == AgentRunEventKind.Started || boundary == AgentRunEventKind.ModelStepStarted)
                    AssertEqual(0, f.Model.Requests.Count, "mandatory model boundary precedes network");
            }
        }

        private static async Task KernelStoreFailurePreservesUnpersistedEvidence()
        {
            foreach (var boundary in new[] { AgentRunEventKind.ToolCompleted, AgentRunEventKind.SummaryChanged })
            {
                var f = new KernelFixture(KernelResponse(KernelCall()), KernelResponse());
                f.Tools.OnExecute = (context, token) => Task.FromResult(KernelRecord(context, ToolExecutionOutcome.Unknown));
                f.Store.FailOn = fact => fact.Kind == boundary;
                var failure = await KernelThrowsAsync<RunStoreException>(() => f.RunAsync());
                AssertEqual(RunLifecycle.Failed, failure.UnpersistedSummary.Lifecycle, "store failure stops execution");
                AssertEqual(ExecutionHealth.Unknown, failure.UnpersistedSummary.ExecutionHealth, "possible effect survives append failure in memory");
                AssertEqual("0,0,0,0,1", KernelCounts(failure.UnpersistedSummary), "unpersisted evidence explicitly labelled");
                AssertEqual(1, f.Tools.Calls.Count, "no effect replay after failed append");
                AssertEqual(1, f.Store.FailedAppends, "no automatic append retry");
                AssertTrue(!f.Store.Events.Any(e => e.Kind == AgentRunEventKind.SummaryChanged), "failed terminal was not persisted");
            }
            var noCursor = new KernelFixture(KernelResponse());
            noCursor.Store.ReturnUnchangedCursor = true;
            await KernelThrowsAsync<RunStoreException>(() => noCursor.RunAsync());
            AssertEqual(0, noCursor.Model.Requests.Count, "unchanged cursor cannot authorize dispatch");
        }

        private static async Task KernelRejectsMissingExecutionEvidence()
        {
            var f = new KernelFixture(KernelResponse(KernelCall()));
            f.Tools.OnExecute = (context, token) => Task.FromResult<ToolExecutionRecord>(null);
            var result = await f.RunAsync();
            AssertEqual(RunLifecycle.Failed, result.Summary.Lifecycle, "missing runtime record is technical failure");
            AssertEqual(ExecutionHealth.Unknown, result.Summary.ExecutionHealth, "missing terminal cannot certify write effect");
            AssertEqual(1, f.Tools.Calls.Count, "no retry for missing evidence");
        }

        private static async Task KernelLocalInteractionEndsWithoutExtraModelStep()
        {
            var f = new KernelFixture(KernelResponse(KernelCall("unclassified")));
            f.Tools.OnExecute = (context, token) => Task.FromResult(KernelRecord(context, awaitingUser: true));
            var result = await f.RunAsync();
            AssertEqual(RunLifecycle.Completed, result.Summary.Lifecycle, "bounded local interaction ends this invocation");
            AssertEqual("awaiting_user", result.Summary.Reason, "reason is separate from lifecycle");
            AssertEqual(1, f.Model.Requests.Count, "no unsolicited next step");
        }

        private static async Task<TException> KernelThrowsAsync<TException>(Func<Task> action) where TException : Exception
        {
            try { await action(); }
            catch (TException ex) { return ex; }
            throw new InvalidOperationException("Expected " + typeof(TException).Name);
        }

        private sealed class KernelFixture
        {
            internal readonly KernelModelFake Model;
            internal readonly KernelToolFake Tools;
            internal readonly KernelStoreFake Store = new KernelStoreFake();
            internal readonly AgentKernel Kernel;
            internal Func<string> NewCallId;
            internal int AllocationCount;

            internal KernelFixture(params AgentResponseDraft[] responses)
            {
                Model = new KernelModelFake(Store, responses);
                Tools = new KernelToolFake(Store);
                Kernel = new AgentKernel(Model, Tools, Store,
                    () => new DateTime(2026, 8, 28, 0, 0, 0, DateTimeKind.Utc), AllocateCallId);
            }

            private string AllocateCallId()
            {
                AllocationCount++;
                return NewCallId == null ? "call_" + AllocationCount : NewCallId();
            }

            internal Task<AgentRunResult> RunAsync(CancellationToken token = default(CancellationToken), AgentRunLimits limits = null)
            {
                return Kernel.RunAsync(new AgentRunRequest("run", "turn", "Change requested.", limits ?? new AgentRunLimits(10, 10)), token);
            }
        }

        private sealed class KernelModelFake : IModelProtocol
        {
            internal readonly List<AgentModelRequest> Requests = new List<AgentModelRequest>();
            internal Func<AgentModelRequest, CancellationToken, Task<AgentModelResult>> OnSend;
            private readonly KernelStoreFake _store;
            private readonly Queue<AgentResponseDraft> _responses;

            internal KernelModelFake(KernelStoreFake store, IEnumerable<AgentResponseDraft> responses)
            {
                _store = store;
                _responses = new Queue<AgentResponseDraft>(responses);
            }

            public Task<AgentModelResult> SendAsync(AgentModelRequest request, CancellationToken token)
            {
                Requests.Add(request);
                AssertEqual(AgentRunEventKind.ModelStepStarted, _store.Events.Last().Kind, "model boundary persisted before dispatch");
                return OnSend != null ? OnSend(request, token) : Task.FromResult(AgentModelResult.Accepted(_responses.Dequeue()));
            }
        }

        private sealed class KernelToolFake : IToolRuntime
        {
            internal readonly List<ToolExecutionContext> Calls = new List<ToolExecutionContext>();
            internal readonly Dictionary<string, ToolPolicySnapshot> Policies = new Dictionary<string, ToolPolicySnapshot>
            {
                ["read"] = new ToolPolicySnapshot("read", "r1", false, independentLocalRead: true),
                ["write"] = new ToolPolicySnapshot("write", "r1", true),
                ["external"] = new ToolPolicySnapshot("external", "r1", true),
                ["confirm"] = new ToolPolicySnapshot("confirm", "r1", true, true),
                ["unclassified"] = new ToolPolicySnapshot("unclassified", "r1", false)
            };
            internal Func<ToolExecutionContext, CancellationToken, Task<ToolExecutionRecord>> OnExecute;
            internal Action BeforeDescribe;
            private readonly KernelStoreFake _store;
            internal KernelToolFake(KernelStoreFake store) { _store = store; }

            public ToolPolicySnapshot Describe(ToolCall call)
            {
                if (BeforeDescribe != null) BeforeDescribe();
                return Policies[call.Name];
            }

            public Task<ToolExecutionRecord> ExecuteAsync(ToolExecutionContext context, CancellationToken token)
            {
                Calls.Add(context);
                AssertEqual(AgentRunEventKind.ToolStarted, _store.Events.Last().Kind, "tool boundary persisted before dispatch");
                AssertTrue(_store.Events.Any(e => e.Kind == AgentRunEventKind.ResponseAccepted && e.Response.ToolCalls.Any(call =>
                    call.Id == context.Call.Id && call.Name == context.Call.Name && call.ArgumentsJson == context.Call.ArgumentsJson)),
                    "execution uses the exact call and runtime id from durable acceptance");
                return OnExecute != null ? OnExecute(context, token) : Task.FromResult(KernelRecord(context));
            }
        }

        // Only a port fake. This is not existing ChatStore replay or crash recovery coverage.
        private sealed class KernelStoreFake : IRunStore
        {
            internal readonly List<AgentRunEvent> Events = new List<AgentRunEvent>();
            internal Action<AgentRunEvent> OnAppended;
            internal Func<AgentRunEvent, bool> FailOn;
            internal bool ReturnUnchangedCursor;
            internal int FailedAppends;
            private long _revision;

            public Task<long> AppendAsync(AgentRunEvent fact, long expectedRevision, CancellationToken token)
            {
                AssertTrue(!token.CanBeCanceled, "mandatory terminal evidence cannot be dropped by cancellation");
                if (expectedRevision != _revision) throw new InvalidOperationException("Stale continuation cursor.");
                if (FailOn != null && FailOn(fact))
                {
                    FailedAppends++;
                    throw new IOException("Injected append failure.");
                }
                Events.Add(fact);
                _revision++;
                if (OnAppended != null) OnAppended(fact);
                return Task.FromResult(ReturnUnchangedCursor ? expectedRevision : _revision);
            }
        }
    }
}
