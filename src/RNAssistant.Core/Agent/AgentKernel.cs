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
    public sealed partial class AgentKernel
    {
        private readonly IModelProtocol _model;
        private readonly IToolRuntime _tools;
        private readonly IRunStore _store;
        private readonly Func<DateTime> _utcNow;
        private readonly Func<string> _newCallId;
        private readonly IRunInputChannel _input;
        private readonly Func<bool> _suspendOnCancellation;
        private readonly IRunExecutionAdmission _admission;

        public AgentKernel(IModelProtocol model, IToolRuntime tools, IRunStore store, Func<DateTime> utcNow = null,
            Func<string> newCallId = null, IRunInputChannel input = null, Func<bool> suspendOnCancellation = null,
            IRunExecutionAdmission admission = null)
        {
            _admission = admission;
            _input = input;
            _suspendOnCancellation = suspendOnCancellation;
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _tools = tools ?? throw new ArgumentNullException(nameof(tools));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _newCallId = newCallId ?? (() => "call_" + Guid.NewGuid().ToString("N"));
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
            AgentRunContinuation continuation, CancellationToken cancellationToken, bool supersedePending = false,
            bool rejectPending = false)
        {
            if (string.IsNullOrWhiteSpace(runId)) throw new ArgumentException("Run id is required.", nameof(runId));
            var pending = continuation == null ? null : continuation.Summary.PendingConfirmation;
            if (continuation != null && !string.Equals(runId, continuation.Summary.RunId, StringComparison.Ordinal))
                throw new InvalidOperationException("Pending confirmation must resume in its original run.");
            if (pending == null || continuation.Summary.Lifecycle != RunLifecycle.AwaitingConfirmation ||
                !string.Equals(pending.PendingId, pendingId, StringComparison.Ordinal) ||
                !continuation.AcceptedCallIds.Contains(pending.Call.Id, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("A complete matching pending continuation is required.");
            var state = new State(runId, continuation);
            // Claim the continuation cursor before either executing or consuming
            // it. A duplicate resume cannot reach the tool runtime.
            await AppendAsync(state, new AgentRunEvent(AgentRunEventKind.SummaryChanged, state.Summary())).ConfigureAwait(false);
            if (rejectPending)
            {
                await RecordNotDispatchedAsync(state, pending.Call, pending.Policy, pending.StepId,
                    "User rejected the pending confirmation.", cancelled: true).ConfigureAwait(false);
                return await FinishAsync(state, RunLifecycle.Cancelled, "confirmation_denied",
                    "Pending tool call was rejected without dispatch.").ConfigureAwait(false);
            }
            if (supersedePending || (_input != null && _input.HasPendingInput))
            {
                await RecordNotDispatchedAsync(state, pending.Call, pending.Policy, pending.StepId,
                    "Pending approval superseded by a new user message.", cancelled: true).ConfigureAwait(false);
                return await LoopAsync(state, cancellationToken).ConfigureAwait(false);
            }
            var execution = await ExecuteOneAsync(state, pending.Call, pending.Policy, pending.StepId,
                true, pending.ChargedToolSteps, pending.PreparedStateJson, cancellationToken).ConfigureAwait(false);
            if (execution != null)
                return await FinishAsync(state, execution.Lifecycle, execution.Reason, execution.AssistantMessage).ConfigureAwait(false);
            return await LoopAsync(state, cancellationToken).ConfigureAwait(false);
        }

        public async Task<AgentRunResult> ContinueAsync(AgentRunContinuation continuation, CancellationToken cancellationToken)
        {
            if (continuation == null) throw new ArgumentNullException(nameof(continuation));
            // Revalidate even when called without the application factory. The
            // first append claims the supplied invocation cursor before any model call.
            var checkedContinuation = AgentRunContinuation.RestoreCheckpoint(
                new AgentRunState(continuation.Summary, continuation.Limits, null,
                    continuation.ConsecutiveReflections, continuation.ReflectionFeedbackIssued, continuation.FinishCorrection),
                continuation.Revision, continuation.AcceptedMessages);
            var state = new State(checkedContinuation.Summary.RunId, checkedContinuation);
            await AppendAsync(state, new AgentRunEvent(AgentRunEventKind.SummaryChanged, state.Summary())).ConfigureAwait(false);
            return await LoopAsync(state, cancellationToken).ConfigureAwait(false);
        }

        public async Task<AgentRunResult> ResumeWaitAsync(AgentRunContinuation continuation, CancellationToken cancellationToken)
        {
            if (continuation == null) throw new ArgumentNullException(nameof(continuation));
            // Workspace may carry earlier turns in model context. They are not
            // accepted calls of this Run and cannot become its finish evidence.
            var currentIds = new HashSet<string>(continuation.AcceptedCallIds, StringComparer.OrdinalIgnoreCase);
            var currentHistory = continuation.AcceptedMessages.Where(message => message.Kind == AgentMessageKind.ToolResult
                ? currentIds.Contains(message.ToolCallId)
                : message.ToolCalls.Count == 0 || message.ToolCalls.Any(call => currentIds.Contains(call.Id)));
            var restored = AgentRunContinuation.RestoreWait(new AgentRunState(continuation.Summary, continuation.Limits, null,
                continuation.ConsecutiveReflections, continuation.ReflectionFeedbackIssued, continuation.FinishCorrection),
                continuation.Revision, currentHistory);
            if (!currentIds.SetEquals(restored.AcceptedCallIds))
                throw new InvalidOperationException("Saved wait history differs from this run's accepted call identities.");
            var state = new State(restored.Summary.RunId, continuation);
            var pending = restored.Summary.PendingWait;
            if (restored.Summary.PauseId != null)
                return await ResumePauseAsync(state, restored.Summary.PauseId, cancellationToken).ConfigureAwait(false);
            var context = pending.Context;
            state.PendingWait = pending;
            // Claim under CAS while retaining a recoverable wait if the process exits here.
            await AppendAsync(state, new AgentRunEvent(AgentRunEventKind.SummaryChanged,
                state.Summary(RunLifecycle.Waiting, "waiting_agents"))).ConfigureAwait(false);
            ToolExecutionRecord record;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!context.Policy.Matches(_tools.Describe(context.Call)) || !(_tools is IToolWaitRuntime))
                    throw new InvalidOperationException("The saved wait binding changed; request a new observation.");
                record = await ((IToolWaitRuntime)_tools).CompleteWaitAsync(pending, cancellationToken).ConfigureAwait(false);
                if (record == null || !ReferenceEquals(record.Context, context) ||
                    record.Outcome == ToolExecutionOutcome.Waiting || record.Outcome == ToolExecutionOutcome.AwaitingConfirmation ||
                    record.MayHaveDispatched || record.AwaitingUser)
                    throw new InvalidOperationException("Invalid terminal evidence for a saved wait.");
            }
            catch (Exception ex)
            {
                var cancelled = ex is OperationCanceledException;
                record = new ToolExecutionRecord(context, cancelled ? ToolExecutionOutcome.NotDispatched : ToolExecutionOutcome.Error,
                    CompletionTime(context), ex.Message, mayHaveDispatched: false, wasCancelled: cancelled);
            }
            if (record.WasCancelled && _suspendOnCancellation?.Invoke() == true)
                return await FinishAsync(state, RunLifecycle.Waiting, "waiting_agents",
                    "Runtime closed; explicitly continue the saved wait.").ConfigureAwait(false);
            state.PendingWait = null;
            state.Counts = state.Counts.Add(record);
            state.Messages.Add(AgentMessage.ToolResult(record));
            state.ConsecutiveReflections = 0; state.ReflectionFeedbackIssued = false;
            if (record.Outcome != ToolExecutionOutcome.NotDispatched) state.FinishCorrection = null;
            await AppendAsync(state, new AgentRunEvent(AgentRunEventKind.ToolCompleted, state.Summary(),
                context.StepId, execution: record)).ConfigureAwait(false);
            state.Progress.Observe(context.Call, ToolExecutionProgress.Capture(record), record.Message, record.ResourceEvidence, record.ResourceEffect);
            if (record.WasCancelled || cancellationToken.IsCancellationRequested)
                return await FinishAsync(state, RunLifecycle.Cancelled, "cancelled", "Run cancelled while waiting.").ConfigureAwait(false);
            return await LoopAsync(state, cancellationToken).ConfigureAwait(false);
        }

        private async Task<AgentRunResult> ResumePauseAsync(State state, string pauseId, CancellationToken token)
        {
            state.PauseId = pauseId;
            var control = _input as IRunPauseChannel;
            if (control == null) throw new InvalidOperationException("Paused run requires its input control owner.");
            // Claim the checkpoint under CAS, preserving it through runtime shutdown.
            await AppendAsync(state, new AgentRunEvent(AgentRunEventKind.SummaryChanged,
                state.Summary(RunLifecycle.Waiting, "paused"))).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                await control.WaitForResumeAsync(pauseId, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                var suspend = _suspendOnCancellation?.Invoke() == true;
                return await FinishAsync(state, suspend ? RunLifecycle.Waiting : RunLifecycle.Cancelled,
                    suspend ? "paused" : "cancelled", "Run paused or stopped.").ConfigureAwait(false);
            }
            state.PauseId = null;
            await AppendAsync(state, new AgentRunEvent(AgentRunEventKind.SummaryChanged, state.Summary())).ConfigureAwait(false);
            return await LoopAsync(state, token).ConfigureAwait(false);
        }

        private async Task<AgentRunResult> PauseAsync(State state, string pauseId)
        {
            state.PauseId = pauseId;
            var result = await FinishAsync(state, RunLifecycle.Waiting, "paused", "Execution paused at a safe boundary.").ConfigureAwait(false);
            // Acknowledge only after the safe checkpoint commits. The coordinator's
            // next checkpoint publication then includes the acknowledged Inbox state.
            (_input as IRunPauseChannel)?.AcknowledgePause(pauseId);
            return result;
        }

        // A pure terminal transition for a dormant, effect-free wait. The application
        // must own the exact conversation lease and commit this fact under storage CAS.
        // No continuation, model request or tool dispatch is needed to cancel it.
        public static AgentRunEvent CancelWait(AgentRunState checkpoint)
        {
            var summary = checkpoint?.Summary;
            if (summary?.Lifecycle != RunLifecycle.Waiting || checkpoint.InFlightTool != null)
                throw new InvalidOperationException("Only a saved, unresolved wait can be cancelled without its live owner.");
            if (summary.PauseId != null)
            {
                var cancelled = new RunSummary(summary.RunId, summary.TurnId, RunLifecycle.Cancelled,
                    summary.ToolCounts, summary.IterationsUsed, summary.ToolStepsUsed, "Приостановленный запуск отменён.", "cancelled", null);
                var closed = new AgentRunEvent(AgentRunEventKind.SummaryChanged, cancelled);
                closed.SetReflectionState(checkpoint.ConsecutiveReflections, checkpoint.ReflectionFeedbackIssued);
                closed.SetFinishCorrection(checkpoint.FinishCorrection);
                return closed;
            }
            var context = summary.PendingWait.Context;
            var now = DateTime.UtcNow;
            const string message = "Ожидание отменено. Выполнение остановлено.";
            var record = new ToolExecutionRecord(context, ToolExecutionOutcome.NotDispatched,
                now < context.StartedUtc ? context.StartedUtc : now, message, mayHaveDispatched: false,
                result: RNAssistant.Core.Tools.Contracts.ToolResult.Error(message,
                    "{\"code\":\"wait_cancelled\",\"dispatched\":false}"), wasCancelled: true);
            var terminal = new RunSummary(summary.RunId, summary.TurnId, RunLifecycle.Cancelled,
                summary.ToolCounts, summary.IterationsUsed, summary.ToolStepsUsed, message, "cancelled", null);
            var fact = new AgentRunEvent(AgentRunEventKind.ToolCompleted, terminal, context.StepId, execution: record);
            fact.SetReflectionState(checkpoint.ConsecutiveReflections, checkpoint.ReflectionFeedbackIssued);
            fact.SetFinishCorrection(checkpoint.FinishCorrection);
            return fact;
        }

        private async Task<AgentRunResult> LoopAsync(State state, CancellationToken cancellationToken)
        {
            if (state.Progress.RecoveryIgnored)
                return await FinishAsync(state, RunLifecycle.Failed, "restored_no_progress",
                    "The checkpoint already exhausted its no-progress allowance.").ConfigureAwait(false);
            if (state.FinishCorrection?.Kind == ModelRequestCauseKind.CompletionRejected &&
                state.FinishCorrection.Rejections >= FinishCorrection.MaximumRejections)
                return await FinishRejectionLimitAsync(state).ConfigureAwait(false);
            string completionFeedback = state.ReflectionFeedbackIssued ? ReflectionLimitFeedback(state) : null;
            ModelRequestCause requestCause = completionFeedback == null ? null :
                new ModelRequestCause(ModelRequestCauseKind.ReflectionLimit, null, completionFeedback);
            var partialFinishRechecked = false;
            while (state.Iterations < state.Limits.MaxIterations)
            using (var permit = await EnterExecutionAsync(state, cancellationToken).ConfigureAwait(false))
            {
                if (cancellationToken.IsCancellationRequested)
                    return await FinishAsync(state, RunLifecycle.Cancelled, "cancelled", "Run cancelled.").ConfigureAwait(false);
                var pauseId = (_input as IRunPauseChannel)?.PauseId;
                if (pauseId != null) return await PauseAsync(state, pauseId).ConfigureAwait(false);
                if (permit.Error != null)
                    return await FinishAsync(state, RunLifecycle.Failed, "execution_admission", permit.Error).ConfigureAwait(false);
                if (!permit.Acquired) continue;
                if (await TakeInputAsync(state, cancellationToken).ConfigureAwait(false))
                {
                    completionFeedback = null;
                    requestCause = new ModelRequestCause(ModelRequestCauseKind.InputReceived,
                        requestCause?.Kind == ModelRequestCauseKind.InputReceived ? requestCause.SourceModelAttemptId : null,
                        "New input arrived. Reconsider the next action using the received messages and completed tool results.");
                    partialFinishRechecked = false;
                }
                state.Iterations++;
                var stepId = state.RunId + ":" + state.Iterations;
                await AppendAsync(state, new AgentRunEvent(AgentRunEventKind.ModelStepStarted, state.Summary(), stepId)).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                    return await FinishAsync(state, RunLifecycle.Cancelled, "cancelled", "Run cancelled before model dispatch.").ConfigureAwait(false);
                AgentModelResult model;
                try
                {
                    using (var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                        _input == null ? CancellationToken.None : _input.InterruptToken))
                        model = await _model.SendAsync(new AgentModelRequest(state.RunId, state.TurnId, stepId,
                            state.Messages, completionFeedback ?? state.FinishCorrection?.Message ?? (state.Progress.InspectionRequired
                                ? ModelProtocolWire.UnknownEffectGuidance : null), state.Limits.MaxConsecutiveReflections,
                            state.ConsecutiveReflections, requestCause ?? state.FinishCorrection?.RequestCause(),
                            state.FinishCorrection), requestCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (!cancellationToken.IsCancellationRequested && _input != null && _input.HasPendingInput)
                        continue;
                    return await FinishAsync(state, RunLifecycle.Cancelled, "cancelled", "Model request cancelled.").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return await FinishAsync(state, RunLifecycle.Failed, "model_infrastructure", ex.Message).ConfigureAwait(false);
                }
                if (cancellationToken.IsCancellationRequested)
                    return await FinishAsync(state, RunLifecycle.Cancelled, "cancelled", "Run cancelled.").ConfigureAwait(false);
                pauseId = (_input as IRunPauseChannel)?.PauseId;
                if (pauseId != null) return await PauseAsync(state, pauseId).ConfigureAwait(false);
                if (_input != null && _input.HasPendingInput)
                {
                    requestCause = new ModelRequestCause(ModelRequestCauseKind.InputReceived,
                        model?.SourceModelAttemptId, "New input arrived before this response was accepted; no tools from this response were dispatched.");
                    continue;
                }
                if (model == null)
                    return await FinishAsync(state, RunLifecycle.Failed, "missing_model_result", "Model returned no result.").ConfigureAwait(false);
                if (model.FailureKind.HasValue)
                    return await FinishAsync(state,
                        model.FailureKind == ModelProtocolFailureKind.Cancelled ? RunLifecycle.Cancelled : RunLifecycle.Failed,
                        model.FailureKind.Value.ToString(), model.Message).ConfigureAwait(false);
                if (model.ProviderRefusal)
                    return await FinishAsync(state, RunLifecycle.Failed, "provider_refused", model.Message).ConfigureAwait(false);

                if (model.CompletionRejected)
                {
                    completionFeedback = null; requestCause = null;
                    var stopped = await RejectFinishAsync(state, model.Response, model.CompletionRejection,
                        model.SourceModelAttemptId, stepId).ConfigureAwait(false);
                    if (stopped != null) return stopped;
                    continue;
                }
                completionFeedback = null;
                requestCause = null;
                if (model.Response == null)
                    return await FinishAsync(state, RunLifecycle.Failed, "invalid_accepted_response", "Accepted response is missing.").ConfigureAwait(false);
                AgentResponse response;
                try
                {
                    response = AssignCallIds(state, model.Response, model.SourceModelAttemptId);
                }
                catch (Exception ex)
                {
                    // Allocation is local infrastructure, never a reason to ask
                    // the model to regenerate its useful payload.
                    return await FinishAsync(state, RunLifecycle.Failed, "call_id_allocation_failed", ex.Message).ConfigureAwait(false);
                }
                ToolPolicySnapshot[] policies;
                try
                {
                    policies = ValidateResponse(response);
                }
                catch (Exception ex)
                {
                    return await FinishAsync(state, RunLifecycle.Failed, "invalid_accepted_response", ex.Message).ConfigureAwait(false);
                }
                if (response.Action == ConversationResponse.ReflectionAction)
                {
                    if (state.Limits.MaxConsecutiveReflections == 0)
                        return await FinishAsync(state, RunLifecycle.Failed, "ReflectionDisabled",
                            "Рефлексия отключена для этого запуска, но модель вернула reflection. Ответ отклонён без запроса на корректировку.").ConfigureAwait(false);
                    if (state.ReflectionFeedbackIssued)
                        return await FinishAsync(state, RunLifecycle.Failed, "reflection_limit",
                            "Reflection limit reached without a new tool result or user input.").ConfigureAwait(false);
                    if (state.ConsecutiveReflections >= state.Limits.MaxConsecutiveReflections)
                    {
                        state.ReflectionFeedbackIssued = true;
                        completionFeedback = ReflectionLimitFeedback(state);
                        requestCause = new ModelRequestCause(ModelRequestCauseKind.ReflectionLimit,
                            model.SourceModelAttemptId, completionFeedback);
                        await AppendAsync(state, new AgentRunEvent(AgentRunEventKind.SummaryChanged,
                            state.Summary(reason: "reflection_limit_feedback", message: completionFeedback), stepId)).ConfigureAwait(false);
                        continue;
                    }
                }
                if (response.Final && state.Progress.InspectionRequired)
                {
                    var stopped = await RejectFinishAsync(state, model.Response,
                        FinishAdmission.Reject("inspection_required", ModelProtocolWire.UnknownEffectGuidance),
                        model.SourceModelAttemptId, stepId).ConfigureAwait(false);
                    if (stopped != null) return stopped;
                    continue;
                }
                if (response.Final)
                {
                    FinishAdmission admission;
                    try
                    {
                        admission = await _store.EvaluateFinishAsync(state.RunId, state.TurnId,
                            state.AcceptedIds.ToArray(), response.Results, state.Revision, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        return await FinishAsync(state, RunLifecycle.Failed, "finish_evidence_unavailable", ex.Message).ConfigureAwait(false);
                    }
                    if (admission == null)
                        return await FinishAsync(state, RunLifecycle.Failed, "finish_evidence_unavailable",
                            "Finish evidence resolver returned no decision.").ConfigureAwait(false);
                    if (!admission.Accepted)
                    {
                        var stopped = await RejectFinishAsync(state, model.Response, admission,
                            model.SourceModelAttemptId, stepId).ConfigureAwait(false);
                        if (stopped != null) return stopped;
                        continue;
                    }
                    // Reconsider a partial claim once without interpreting free-form reason text.
                    // Keep the available terminal answer when another step cannot fit.
                    if (response.Outcome == ConversationOutcome.Partial && state.AcceptedIds.Count > 0 &&
                        !partialFinishRechecked && state.Iterations < state.Limits.MaxIterations &&
                        state.ToolSteps < state.Limits.MaxToolSteps)
                    {
                        partialFinishRechecked = true;
                        state.FinishCorrection = FinishCorrection.Create(model.Response, "partial_finish_recheck",
                            ModelProtocolWire.PartialFinishRecheckGuidance, null, model.SourceModelAttemptId,
                            ModelRequestCauseKind.PartialFinishRecheck, null);
                        await AppendAsync(state, new AgentRunEvent(AgentRunEventKind.SummaryChanged,
                            state.Summary(reason: "partial_finish_recheck", message: state.FinishCorrection.Message), stepId)).ConfigureAwait(false);
                        continue;
                    }
                    if (_input != null && !_input.TryCloseInput())
                    {
                        requestCause = new ModelRequestCause(ModelRequestCauseKind.InputReceived,
                            model.SourceModelAttemptId, "New input arrived before finish was accepted.");
                        continue;
                    }
                }
                if (response.Final) state.FinishCorrection = null;
                if (response.Action == ConversationResponse.ReflectionAction) state.ConsecutiveReflections++;
                foreach (var call in response.ToolCalls) state.AcceptedIds.Add(call.Id);
                state.Messages.Add(AgentMessage.Assistant(response));
                await AppendAsync(state, new AgentRunEvent(AgentRunEventKind.ResponseAccepted,
                    state.Summary(), stepId, response: response,
                    sourceModelAttemptId: model.SourceModelAttemptId)).ConfigureAwait(false);
                if (response.Action != ConversationResponse.ReflectionAction) state.Progress.BeginStep();
                if (response.Final)
                {
                    return await FinishAsync(state, RunLifecycle.Completed, "model_finish", response.Message).ConfigureAwait(false);
                }
                if (response.Action == ConversationResponse.ReflectionAction) continue;

                for (var index = 0; index < response.ToolCalls.Count; index++)
                {
                    if (_input != null && (_input.HasPendingInput || (_input as IRunPauseChannel)?.PauseId != null))
                    {
                        for (var rest = index; rest < response.ToolCalls.Count; rest++)
                            await RecordNotDispatchedAsync(state, response.ToolCalls[rest], policies[rest], stepId,
                                "Incoming control or message prevented dispatch.").ConfigureAwait(false);
                        break;
                    }
                    var result = await ExecuteOneAsync(state, response.ToolCalls[index], policies[index], stepId,
                        false, 0, null, cancellationToken).ConfigureAwait(false);
                    if (result != null)
                    {
                        // Keep accepted exchanges closed when cancellation or a
                        // technical failure prevents the rest of an accepted batch.
                        for (var rest = index + 1; rest < response.ToolCalls.Count; rest++)
                            await RecordNotDispatchedAsync(state, response.ToolCalls[rest], policies[rest], stepId,
                                "Run ended before dispatch.", cancelled: result.Lifecycle == RunLifecycle.Cancelled).ConfigureAwait(false);
                        return await FinishAsync(state, result.Lifecycle, result.Reason, result.AssistantMessage).ConfigureAwait(false);
                    }
                }
                if (state.Progress.RecoveryIgnored)
                    return await FinishAsync(state, RunLifecycle.Failed, "repeated_tool_no_progress",
                        "Three consecutive model responses repeated only rejected calls. No repeated operation was dispatched; change the approach or reconcile the earlier result.").ConfigureAwait(false);
            }
            return await FinishAsync(state, RunLifecycle.Failed, "iteration_limit", "Model iteration limit reached.").ConfigureAwait(false);
        }

        private static string ReflectionLimitFeedback(State state)
        {
            return "reflection_limit_feedback: The allowance of " + state.Limits.MaxConsecutiveReflections +
                " consecutive reflections is exhausted. The last reflection was not accepted. Choose execute or finish now; another reflection ends the run. " +
                "Format repairs and provider retries do not consume reflection depth. Do not invent a tool call or report unverified work as complete.";
        }

        private async Task<AgentRunResult> RejectFinishAsync(State state, AgentResponseDraft proposal,
            FinishAdmission rejection, string sourceModelAttemptId, string stepId)
        {
            state.FinishCorrection = FinishCorrection.Create(proposal, rejection.Code, rejection.Message,
                rejection.ClaimIndex, sourceModelAttemptId, ModelRequestCauseKind.CompletionRejected, state.FinishCorrection);
            await AppendAsync(state, new AgentRunEvent(AgentRunEventKind.SummaryChanged,
                state.Summary(reason: "completion_requirements_unmet", message: state.FinishCorrection.RequestCause().Feedback), stepId)).ConfigureAwait(false);
            return state.FinishCorrection.Rejections >= FinishCorrection.MaximumRejections
                ? await FinishRejectionLimitAsync(state).ConfigureAwait(false) : null;
        }

        private Task<AgentRunResult> FinishRejectionLimitAsync(State state)
        {
            return FinishAsync(state, RunLifecycle.Failed, "repeated_finish_no_progress",
                "Завершение трижды отклонено по одной причине без новых результатов инструментов или ввода. " +
                "Запуск остановлен. Последний ответ не принят. Причина: " + state.FinishCorrection.RequestCause().Feedback);
        }

        private async Task<bool> TakeInputAsync(State state, CancellationToken token)
        {
            if (_input == null) return false;
            var messages = await _input.TakeInputAsync(token).ConfigureAwait(false);
            if (messages.Count > 0)
            {
                state.ConsecutiveReflections = 0; state.ReflectionFeedbackIssued = false;
                state.FinishCorrection = null;
                state.Progress.ObserveUserInput();
            }
            foreach (var message in messages)
                state.Messages.Add(message);
            return messages.Count > 0;
        }

        private AgentResponse AssignCallIds(State state, AgentResponseDraft response, string sourceModelAttemptId)
        {
            var ids = new HashSet<string>(state.AcceptedIds, StringComparer.OrdinalIgnoreCase);
            var calls = new List<ToolCall>();
            foreach (var draft in response.ToolCalls)
            {
                var id = _newCallId();
                if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || id.Any(character =>
                    !(character >= 'a' && character <= 'z' || character >= 'A' && character <= 'Z' ||
                      character >= '0' && character <= '9' || character == '_' || character == '-')) || !ids.Add(id))
                    throw new InvalidOperationException("Runtime call id allocation returned an invalid or duplicate identity.");
                calls.Add(new ToolCall(id, draft.Name, draft.ArgumentsJson));
            }
            return new AgentResponse(response.Action, response.Reason, response.Message,
                response.Action == ConversationResponse.ExecuteAction ? calls : null,
                response.Decision, response.Results, sourceModelAttemptId, response.Outcome);
        }

        private ToolPolicySnapshot[] ValidateResponse(AgentResponse response)
        {
            if (!ConversationResponse.IsValidAction(response.Action, response.ToolCalls.Count))
                throw new InvalidOperationException("Accepted action and tool calls disagree.");
            if (string.IsNullOrWhiteSpace(response.Reason))
                throw new InvalidOperationException("Accepted response requires a reason.");
            var policies = new List<ToolPolicySnapshot>();
            foreach (var call in response.ToolCalls)
            {
                var policy = _tools.Describe(call);
                if (policy == null || !string.Equals(policy.ToolId, call.Name, StringComparison.Ordinal))
                    throw new InvalidOperationException("Exact execution policy is unavailable: " + call.Name);
                policies.Add(policy);
            }
            if (policies.Count > 1 && policies.Any(policy => policy == null || !policy.IndependentLocalRead))
                throw new InvalidOperationException("Only independent local reads can be batched; every mutation and other call must be returned alone.");
            return policies.ToArray();
        }

        private async Task<RunSummary> ExecuteOneAsync(State state, ToolCall call, ToolPolicySnapshot policy,
            string stepId, bool confirmed, int chargedSteps, string preparedStateJson,
            CancellationToken cancellationToken)
        {
            if (!confirmed)
                return await ExecuteAdmittedAsync(state, call, policy, stepId, false, chargedSteps,
                    preparedStateJson, cancellationToken).ConfigureAwait(false);
            using (var permit = await EnterExecutionAsync(state, cancellationToken).ConfigureAwait(false))
            {
                if (permit.Error != null || !permit.Acquired || (_input as IRunPauseChannel)?.PauseId != null ||
                    (_input != null && _input.HasPendingInput))
                {
                    await RecordNotDispatchedAsync(state, call, policy, stepId,
                        permit.Error ?? "Incoming control prevented confirmed dispatch.", confirmed,
                        cancelled: cancellationToken.IsCancellationRequested).ConfigureAwait(false);
                    if (cancellationToken.IsCancellationRequested) return state.Summary(RunLifecycle.Cancelled, "cancelled");
                    return permit.Error == null ? null : state.Summary(RunLifecycle.Failed, "execution_admission", permit.Error);
                }
                return await ExecuteAdmittedAsync(state, call, policy, stepId, true, chargedSteps,
                    preparedStateJson, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<RunSummary> ExecuteAdmittedAsync(State state, ToolCall call, ToolPolicySnapshot policy,
            string stepId, bool confirmed, int chargedSteps, string preparedStateJson,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                await RecordNotDispatchedAsync(state, call, policy, stepId, "Cancelled before dispatch.", confirmed, cancelled: true).ConfigureAwait(false);
                return state.Summary(RunLifecycle.Cancelled, "cancelled", "Run cancelled.");
            }
            var remaining = state.Limits.MaxToolSteps - state.ToolSteps + chargedSteps;
            if (remaining <= 0)
            {
                await RecordNotDispatchedAsync(state, call, policy, stepId, "Tool step limit reached.", confirmed).ConfigureAwait(false);
                return state.Summary(RunLifecycle.Failed, "tool_step_limit", "Tool step limit reached.");
            }
            string progressReason, progressMessage;
            int retryDelay = 0;
            if (!confirmed && !state.Progress.CanDispatch(call, out progressReason, out progressMessage, out retryDelay, policy))
            {
                await RecordNotDispatchedAsync(state, call, policy, stepId, progressMessage,
                    code: progressReason).ConfigureAwait(false);
                return null;
            }
            if (retryDelay > 0)
            {
                try { await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    await RecordNotDispatchedAsync(state, call, policy, stepId, "Cancelled before retry dispatch.", cancelled: true).ConfigureAwait(false);
                    return state.Summary(RunLifecycle.Cancelled, "cancelled", "Run cancelled.");
                }
            }
            var context = new ToolExecutionContext(call, policy, state.RunId, state.TurnId, stepId,
                _utcNow(), confirmed, remaining, preparedStateJson);
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
                        "Cancelled before dispatch.", mayHaveDispatched: false, wasCancelled: true);
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
                    CompletionTime(context), ex.Message, mayHaveDispatched: enteredRuntime,
                    wasCancelled: cancelled && !enteredRuntime);
                stop = cancelled ? RunLifecycle.Cancelled : RunLifecycle.Failed;
            }
            if (record.ToolStepsConsumed > remaining) stop = RunLifecycle.Failed;
            state.ToolSteps = (int)Math.Min(int.MaxValue, (long)state.ToolSteps + Math.Max(0, (long)record.ToolStepsConsumed - chargedSteps));
            state.Counts = state.Counts.Add(record);
            if (record.Outcome == ToolExecutionOutcome.Waiting)
            {
                state.PendingWait = new PendingToolWait(context, record.WaitCondition);
                await AppendAsync(state, new AgentRunEvent(AgentRunEventKind.ToolWaiting,
                    state.Summary(RunLifecycle.Waiting, "waiting_agents"), stepId, execution: record)).ConfigureAwait(false);
                return state.Summary(RunLifecycle.Waiting, "waiting_agents", record.Message);
            }
            if (record.Outcome == ToolExecutionOutcome.AwaitingConfirmation)
                state.Pending = new PendingConfirmation(record);
            else
                state.Messages.Add(AgentMessage.ToolResult(record));
            if (record.Outcome != ToolExecutionOutcome.AwaitingConfirmation)
            { state.ConsecutiveReflections = 0; state.ReflectionFeedbackIssued = false; }
            // A fresh terminal fact gives the model new information. Rejected
            // dispatches and reflection do not erase an outstanding correction.
            if (record.Outcome != ToolExecutionOutcome.AwaitingConfirmation && record.Outcome != ToolExecutionOutcome.NotDispatched)
                state.FinishCorrection = null;
            await AppendAsync(state, new AgentRunEvent(AgentRunEventKind.ToolCompleted, state.Summary(),
                stepId, execution: record)).ConfigureAwait(false);
            state.Progress.Observe(call, ToolExecutionProgress.Capture(record), record.Message,
                record.ResourceEvidence, record.ResourceEffect);
            if (stop.HasValue || cancellationToken.IsCancellationRequested)
            {
                if (state.Pending != null)
                {
                    state.Pending = null;
                    await RecordNotDispatchedAsync(state, call, policy, stepId,
                        "Confirmation cancelled before dispatch.", cancelled: true).ConfigureAwait(false);
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
            string stepId, string message, bool confirmed = false, string code = null, bool cancelled = false)
        {
            var context = new ToolExecutionContext(call, policy, state.RunId, state.TurnId, stepId, _utcNow(), confirmed, 0);
            var record = new ToolExecutionRecord(context, ToolExecutionOutcome.NotDispatched, CompletionTime(context), message, mayHaveDispatched: false,
                result: code == null ? null : RNAssistant.Core.Tools.Contracts.ToolResult.Error(message,
                    Newtonsoft.Json.JsonConvert.SerializeObject(new { code, dispatched = false })), wasCancelled: cancelled);
            state.Messages.Add(AgentMessage.ToolResult(record));
            state.ConsecutiveReflections = 0;
            state.ReflectionFeedbackIssued = false;
            await AppendAsync(state, new AgentRunEvent(AgentRunEventKind.ToolCompleted, state.Summary(),
                stepId, execution: record)).ConfigureAwait(false);
            state.Progress.Observe(call, ToolExecutionProgress.Capture(record), message, null, null);
        }

        private async Task<AgentRunResult> FinishAsync(State state, RunLifecycle lifecycle, string reason, string message)
        {
            if (lifecycle != RunLifecycle.AwaitingConfirmation) state.Pending = null;
            if (lifecycle != RunLifecycle.Waiting) { state.PendingWait = null; state.PauseId = null; }
            var summary = state.Summary(lifecycle, reason, message);
            await AppendAsync(state, new AgentRunEvent(AgentRunEventKind.SummaryChanged, summary)).ConfigureAwait(false);
            return new AgentRunResult(summary, state.Limits, state.Revision, state.Messages, state.AcceptedIds,
                state.ConsecutiveReflections, state.ReflectionFeedbackIssued, state.FinishCorrection);
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
                fact.SetReflectionState(state.ConsecutiveReflections, state.ReflectionFeedbackIssued);
                fact.SetFinishCorrection(state.FinishCorrection);
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
            internal PendingToolWait PendingWait;
            internal string PauseId;
            internal int ConsecutiveReflections;
            internal bool ReflectionFeedbackIssued;
            internal FinishCorrection FinishCorrection;
            internal readonly AgentProgressTracker Progress = new AgentProgressTracker();

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
                Progress.Restore(Messages);
                AcceptedIds = new HashSet<string>(continuation.AcceptedCallIds, StringComparer.OrdinalIgnoreCase);
                Counts = continuation.Summary.ToolCounts;
                Iterations = continuation.Summary.IterationsUsed;
                ToolSteps = continuation.Summary.ToolStepsUsed;
                Revision = continuation.Revision;
                ConsecutiveReflections = continuation.ConsecutiveReflections;
                ReflectionFeedbackIssued = continuation.ReflectionFeedbackIssued;
                FinishCorrection = continuation.FinishCorrection;
            }

            internal RunSummary Summary(RunLifecycle lifecycle = RunLifecycle.Running, string reason = null, string message = null)
            {
                return new RunSummary(RunId, TurnId, lifecycle, Counts, Iterations, ToolSteps, message, reason, Pending, PendingWait, lifecycle == RunLifecycle.Waiting ? PauseId : null);
            }
        }
    }
}
