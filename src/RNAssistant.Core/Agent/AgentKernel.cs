using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Agent
{
    public sealed class AgentKernel
    {
        private readonly IModelProtocol _model;
        private readonly IToolRuntime _tools;
        private readonly IRunStore _store;
        private readonly Func<DateTime> _utcNow;

        public AgentKernel(IModelProtocol model, IToolRuntime tools, IRunStore store, Func<DateTime> utcNow = null)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _tools = tools ?? throw new ArgumentNullException(nameof(tools));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public async Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var state = new State(request);
            await AppendAsync(state, new AgentRunEvent(AgentRunEventKind.Started, state.Summary(),
                limits: state.Limits, userMessage: AgentMessage.User(request.UserMessage))).ConfigureAwait(false);
            return await LoopAsync(state, cancellationToken).ConfigureAwait(false);
        }

        public async Task<AgentRunResult> ResumeAsync(string runId, string pendingId,
            AgentRunContinuation continuation, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(runId)) throw new ArgumentException("Run id is required.", nameof(runId));
            var pending = continuation == null ? null : continuation.Summary.PendingConfirmation;
            if (pending == null || continuation.Summary.Lifecycle != RunLifecycle.AwaitingConfirmation ||
                !string.Equals(pending.PendingId, pendingId, StringComparison.Ordinal) ||
                !continuation.AcceptedCallIds.Contains(pending.Call.Id, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("A complete matching pending continuation is required.");
            var state = new State(runId, continuation);
            // Claim the continuation cursor before either executing or consuming
            // it. A duplicate resume cannot reach the tool runtime.
            await AppendAsync(state, new AgentRunEvent(AgentRunEventKind.SummaryChanged, state.Summary())).ConfigureAwait(false);
            var execution = await ExecuteOneAsync(state, pending.Call, pending.Policy, pending.StepId,
                true, pending.ChargedToolSteps, cancellationToken).ConfigureAwait(false);
            if (execution != null)
                return await FinishAsync(state, execution.Lifecycle, execution.Reason, execution.AssistantMessage).ConfigureAwait(false);
            return await LoopAsync(state, cancellationToken).ConfigureAwait(false);
        }

        private async Task<AgentRunResult> LoopAsync(State state, CancellationToken cancellationToken)
        {
            while (state.Iterations < state.Limits.MaxIterations)
            {
                if (cancellationToken.IsCancellationRequested)
                    return await FinishAsync(state, RunLifecycle.Cancelled, "cancelled", "Run cancelled.").ConfigureAwait(false);
                state.Iterations++;
                var stepId = state.RunId + ":" + state.Iterations;
                await AppendAsync(state, new AgentRunEvent(AgentRunEventKind.ModelStepStarted, state.Summary(), stepId)).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                    return await FinishAsync(state, RunLifecycle.Cancelled, "cancelled", "Run cancelled before model dispatch.").ConfigureAwait(false);
                AgentModelResult model;
                try
                {
                    model = await _model.SendAsync(new AgentModelRequest(state.RunId, state.TurnId, stepId,
                        state.Messages, state.AcceptedIds), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return await FinishAsync(state, RunLifecycle.Cancelled, "cancelled", "Model request cancelled.").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return await FinishAsync(state, RunLifecycle.Failed, "model_infrastructure", ex.Message).ConfigureAwait(false);
                }
                if (cancellationToken.IsCancellationRequested)
                    return await FinishAsync(state, RunLifecycle.Cancelled, "cancelled", "Run cancelled.").ConfigureAwait(false);
                if (model == null)
                    return await FinishAsync(state, RunLifecycle.Failed, "missing_model_result", "Model returned no result.").ConfigureAwait(false);
                if (model.FailureKind.HasValue)
                    return await FinishAsync(state,
                        model.FailureKind == ModelProtocolFailureKind.Cancelled ? RunLifecycle.Cancelled : RunLifecycle.Failed,
                        model.FailureKind.Value.ToString(), model.Message).ConfigureAwait(false);
                if (model.ProviderRefusal)
                    return await FinishAsync(state, RunLifecycle.Failed, "provider_refused", model.Message).ConfigureAwait(false);

                var response = model.Response;
                ToolPolicySnapshot[] policies;
                try
                {
                    policies = ValidateResponse(state, response);
                }
                catch (Exception ex)
                {
                    return await FinishAsync(state, RunLifecycle.Failed, "invalid_accepted_response", ex.Message).ConfigureAwait(false);
                }
                foreach (var call in response.ToolCalls) state.AcceptedIds.Add(call.Id);
                state.Messages.Add(AgentMessage.Assistant(response));
                await AppendAsync(state, new AgentRunEvent(AgentRunEventKind.ResponseAccepted,
                    state.Summary(), stepId, response: response)).ConfigureAwait(false);
                if (response.ToolCalls.Count == 0)
                    return await FinishAsync(state, RunLifecycle.Completed, "model_loop_ended", response.Message).ConfigureAwait(false);

                for (var index = 0; index < response.ToolCalls.Count; index++)
                {
                    var result = await ExecuteOneAsync(state, response.ToolCalls[index], policies[index], stepId,
                        false, 0, cancellationToken).ConfigureAwait(false);
                    if (result != null)
                    {
                        // Keep accepted exchanges closed when cancellation or a
                        // technical failure prevents the rest of an accepted batch.
                        for (var rest = index + 1; rest < response.ToolCalls.Count; rest++)
                            await RecordNotDispatchedAsync(state, response.ToolCalls[rest], policies[rest], stepId,
                                "Run ended before dispatch.").ConfigureAwait(false);
                        return await FinishAsync(state, result.Lifecycle, result.Reason, result.AssistantMessage).ConfigureAwait(false);
                    }
                }
            }
            return await FinishAsync(state, RunLifecycle.Failed, "iteration_limit", "Model iteration limit reached.").ConfigureAwait(false);
        }

        private ToolPolicySnapshot[] ValidateResponse(State state, AgentResponse response)
        {
            if (response == null) throw new InvalidOperationException("Accepted response is missing.");
            var ids = new HashSet<string>(state.AcceptedIds, StringComparer.OrdinalIgnoreCase);
            var policies = new List<ToolPolicySnapshot>();
            foreach (var call in response.ToolCalls)
            {
                if (!ids.Add(call.Id)) throw new InvalidOperationException("Duplicate accepted call id: " + call.Id);
                var policy = _tools.Describe(call);
                if (policy == null || !string.Equals(policy.ToolId, call.Name, StringComparison.Ordinal))
                    throw new InvalidOperationException("Exact execution policy is unavailable: " + call.Name);
                policies.Add(policy);
            }
            if (policies.Count > 1 && policies.Any(policy => !policy.IndependentLocalRead))
                throw new InvalidOperationException("Only independent local reads can be batched.");
            return policies.ToArray();
        }

        private async Task<RunSummary> ExecuteOneAsync(State state, ToolCall call, ToolPolicySnapshot policy,
            string stepId, bool confirmed, int chargedSteps, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                await RecordNotDispatchedAsync(state, call, policy, stepId, "Cancelled before dispatch.", confirmed).ConfigureAwait(false);
                return state.Summary(RunLifecycle.Cancelled, "cancelled", "Run cancelled.");
            }
            var remaining = state.Limits.MaxToolSteps - state.ToolSteps + chargedSteps;
            if (remaining <= 0)
            {
                await RecordNotDispatchedAsync(state, call, policy, stepId, "Tool step limit reached.", confirmed).ConfigureAwait(false);
                return state.Summary(RunLifecycle.Failed, "tool_step_limit", "Tool step limit reached.");
            }
            var context = new ToolExecutionContext(call, policy, state.RunId, state.TurnId, stepId, _utcNow(), confirmed, remaining);
            await AppendAsync(state, new AgentRunEvent(AgentRunEventKind.ToolStarted, state.Summary(),
                stepId, toolContext: context)).ConfigureAwait(false);
            ToolExecutionRecord record;
            RunLifecycle? stop = null;
            var enteredRuntime = false;
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    record = new ToolExecutionRecord(context, ToolExecutionOutcome.NotDispatched, CompletionTime(context),
                        "Cancelled before dispatch.", mayHaveDispatched: false);
                    stop = RunLifecycle.Cancelled;
                }
                else if (!policy.Matches(_tools.Describe(call)))
                {
                    record = new ToolExecutionRecord(context, ToolExecutionOutcome.Error, CompletionTime(context),
                        "Tool policy changed; request a new call.", mayHaveDispatched: false);
                }
                else
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    enteredRuntime = true;
                    record = await _tools.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
                    if (record == null || !ReferenceEquals(record.Context, context))
                        throw new InvalidOperationException("Tool runtime returned missing or mismatched execution evidence.");
                }
            }
            catch (Exception ex)
            {
                // Crossing the runtime entry without terminal evidence cannot
                // certify a side effect. No automatic replay/retry follows.
                var cancelled = ex is OperationCanceledException;
                record = new ToolExecutionRecord(context,
                    cancelled && !enteredRuntime ? ToolExecutionOutcome.NotDispatched
                        : enteredRuntime && policy.MayHaveSideEffects ? ToolExecutionOutcome.Unknown : ToolExecutionOutcome.Error,
                    CompletionTime(context), ex.Message, mayHaveDispatched: enteredRuntime);
                stop = cancelled ? RunLifecycle.Cancelled : RunLifecycle.Failed;
            }
            if (record.ToolStepsConsumed > remaining) stop = RunLifecycle.Failed;
            state.ToolSteps = (int)Math.Min(int.MaxValue, (long)state.ToolSteps + Math.Max(0, (long)record.ToolStepsConsumed - chargedSteps));
            state.Counts = state.Counts.Add(record);
            if (record.Outcome == ToolExecutionOutcome.AwaitingConfirmation)
                state.Pending = new PendingConfirmation(record);
            else
                state.Messages.Add(AgentMessage.ToolResult(record));
            await AppendAsync(state, new AgentRunEvent(AgentRunEventKind.ToolCompleted, state.Summary(),
                stepId, execution: record)).ConfigureAwait(false);
            if (stop.HasValue || cancellationToken.IsCancellationRequested)
            {
                if (state.Pending != null)
                {
                    state.Pending = null;
                    await RecordNotDispatchedAsync(state, call, policy, stepId,
                        "Confirmation cancelled before dispatch.").ConfigureAwait(false);
                }
                return state.Summary(stop ?? RunLifecycle.Cancelled, "execution_interrupted", record.Message);
            }
            if (state.Pending != null)
            {
                var narrative = state.Messages.LastOrDefault(message => message.Kind == AgentMessageKind.Assistant);
                return state.Summary(RunLifecycle.AwaitingConfirmation, "confirmation_required",
                    narrative == null || string.IsNullOrWhiteSpace(narrative.Text) ? record.Message : narrative.Text);
            }
            if (record.AwaitingUser)
                return state.Summary(RunLifecycle.Completed, "awaiting_user", record.Message);
            return null;
        }

        private async Task RecordNotDispatchedAsync(State state, ToolCall call, ToolPolicySnapshot policy,
            string stepId, string message, bool confirmed = false)
        {
            var context = new ToolExecutionContext(call, policy, state.RunId, state.TurnId, stepId, _utcNow(), confirmed, 0);
            var record = new ToolExecutionRecord(context, ToolExecutionOutcome.NotDispatched, CompletionTime(context), message, mayHaveDispatched: false);
            state.Messages.Add(AgentMessage.ToolResult(record));
            await AppendAsync(state, new AgentRunEvent(AgentRunEventKind.ToolCompleted, state.Summary(),
                stepId, execution: record)).ConfigureAwait(false);
        }

        private async Task<AgentRunResult> FinishAsync(State state, RunLifecycle lifecycle, string reason, string message)
        {
            if (lifecycle != RunLifecycle.AwaitingConfirmation) state.Pending = null;
            var summary = state.Summary(lifecycle, reason, message);
            await AppendAsync(state, new AgentRunEvent(AgentRunEventKind.SummaryChanged, summary)).ConfigureAwait(false);
            return new AgentRunResult(summary, state.Limits, state.Revision, state.Messages, state.AcceptedIds);
        }

        private DateTime CompletionTime(ToolExecutionContext context)
        {
            var now = _utcNow();
            return now < context.StartedUtc ? context.StartedUtc : now;
        }

        private async Task AppendAsync(State state, AgentRunEvent fact)
        {
            try
            {
                // Finish the mandatory append, then observe cancellation at the
                // next dispatch boundary. Cancellation cannot discard evidence.
                var revision = await _store.AppendAsync(fact, state.Revision, CancellationToken.None).ConfigureAwait(false);
                if (revision <= state.Revision) throw new InvalidOperationException("Run store did not advance its cursor.");
                state.Revision = revision;
            }
            catch (Exception ex)
            {
                throw new RunStoreException(state.Summary(RunLifecycle.Failed, "run_store_failure", ex.Message), ex);
            }
        }

        private sealed class State
        {
            internal readonly string RunId;
            internal readonly string TurnId;
            internal readonly AgentRunLimits Limits;
            internal readonly List<AgentMessage> Messages;
            internal readonly HashSet<string> AcceptedIds;
            internal ToolCounts Counts;
            internal int Iterations;
            internal int ToolSteps;
            internal long Revision;
            internal PendingConfirmation Pending;

            internal State(AgentRunRequest request)
            {
                RunId = request.RunId;
                TurnId = request.TurnId;
                Limits = request.Limits;
                Messages = request.PreviousMessages.Concat(new[] { AgentMessage.User(request.UserMessage) }).ToList();
                AcceptedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Counts = new ToolCounts();
            }

            internal State(string runId, AgentRunContinuation continuation)
            {
                RunId = runId;
                TurnId = continuation.Summary.TurnId;
                Limits = continuation.Limits;
                Messages = continuation.AcceptedMessages.ToList();
                AcceptedIds = new HashSet<string>(continuation.AcceptedCallIds, StringComparer.OrdinalIgnoreCase);
                Counts = continuation.Summary.ToolCounts;
                Iterations = continuation.Summary.IterationsUsed;
                ToolSteps = continuation.Summary.ToolStepsUsed;
                Revision = continuation.Revision;
            }

            internal RunSummary Summary(RunLifecycle lifecycle = RunLifecycle.Running, string reason = null, string message = null)
            {
                return new RunSummary(RunId, TurnId, lifecycle, Counts, Iterations, ToolSteps, message, reason, Pending);
            }
        }
    }
}
