using RNAssistant.Core.Llm;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;
using TerminalResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Services
{
    internal sealed partial class ConversationKernelAdapter
    {
        public Task<ToolExecutionProgress> ReadReplayGuardAsync(ToolCall call,
            long expectedRevision, CancellationToken cancellationToken)
        {
            if (expectedRevision != _cursor) throw new InvalidOperationException("Stale replay-guard cursor.");
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_eventStore.RetainedReplayGuards
                ? _eventStore.ReadReplayGuard(ConversationMetadata.From(_session), call) : null);
        }

        public Task<FinishAdmission> EvaluateFinishAsync(string runId, string turnId,
            IReadOnlyList<string> acceptedCallIds, IReadOnlyList<ConversationResultClaim> results,
            long expectedRevision, CancellationToken cancellationToken)
        {
            if (expectedRevision != _cursor || runId != _session.LastRun.RunId ||
                turnId != _session.LastRun.TurnId)
                throw new InvalidOperationException("Stale finish admission cursor or run/session identity.");
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ConversationFinishEvidenceAdmission.Evaluate(_eventStore,
                _session, runId, turnId, acceptedCallIds, results, _executor.Payloads,
                _lastModel?.DeliveredEvidenceReferences ?? new string[0]));
        }

        public async Task<long> AppendAsync(AgentRunEvent fact, long expectedRevision, CancellationToken cancellationToken)
        {
            if (expectedRevision != _cursor) throw new InvalidOperationException("Stale kernel continuation cursor.");
            var firstMessage = _session.Messages.Count;
            var run = _session.LastRun;
            ConversationRunProjection.Apply(run, fact);
            ChatMessage activity = null;
            switch (fact.Kind)
            {
                case AgentRunEventKind.ResponseAccepted:
                    _stepMessage = fact.Response.Message;
                    var acceptedMessages = ConversationRunProjection.CreateAcceptedResponse(fact,
                        _lastModel?.Completion, _executor.Payloads);
                    if (_modelSession != null) _modelSession.AppendAcceptedResponse(acceptedMessages);
                    else _session.Messages.AddRange(acceptedMessages);
                    if (fact.Response.ToolCalls.Count == 0)
                    {
                        var decision = acceptedMessages[0];
                        if (fact.Response.Action == ConversationResponse.ReflectionAction)
                        {
                            var visibleDecision = string.IsNullOrWhiteSpace(fact.Response.Message)
                                ? fact.Response.Decision : fact.Response.Message;
                            activity = new ChatMessage
                            {
                                Role = "assistant", Content = string.Empty,
                                RuntimeProjection = true, ExcludeFromModelContext = true,
                                ReasoningContent = decision.ReasoningContent,
                                ReasoningTokens = decision.ReasoningTokens,
                                ReasoningTruncated = decision.ReasoningTruncated,
                                Activity = new ChatActivity
                                {
                                    Kind = "step", StepId = fact.StepId, StepMessage = visibleDecision,
                                    StepReason = fact.Response.Reason,
                                    Title = "Промежуточное решение", Status = "completed"
                                }
                            };
                            _session.Messages.Add(activity);
                        }
                    }
                    break;
                case AgentRunEventKind.ToolStarted:
                    var context = fact.ToolContext;
                    var command = Command(context.Call, context.StepId, context.IsConfirmed);
                    activity = FindActivity(context.Call.Id) ?? AgentTranscript.CreateRunningToolMessage(
                        _session, command, fact.StepId, _stepMessage);
                    if (!_session.Messages.Contains(activity)) _session.Messages.Add(activity);
                    activity.Activity = AgentTranscript.CreateRunningToolActivity(command, fact.StepId, _stepMessage);
                    activity.RunId = run.RunId;
                    activity.Activity.RunId = run.RunId;
                    break;
                case AgentRunEventKind.ToolWaiting:
                    activity = FindActivity(fact.Execution.Context.Call.Id);
                    if (activity == null) throw new InvalidOperationException("Waiting tool has no started activity.");
                    activity.Activity.Status = "waiting";
                    activity.Activity.ExecutionStatus = "waiting";
                    activity.Activity.ResultMessage = "Ожидаю сообщения агентов";
                    break;
                case AgentRunEventKind.ToolCompleted:
                    activity = ProjectToolCompletion(fact);
                    break;
                case AgentRunEventKind.SummaryChanged:
                    AppendTerminalMessage(fact.Summary);
                    break;
            }
            if (activity?.Activity?.ToolCallId != null)
            {
                // Protocol records stay hidden. Every call retains its step reason;
                // only the first carries reasoning, including resumed/non-dispatched calls.
                var accepted = _session.Messages.FirstOrDefault(message => message.ProtocolMessage &&
                    message.Role == "assistant" && message.ToolCallId == activity.Activity.ToolCallId &&
                    message.RunId == run.RunId);
                if (accepted != null)
                {
                    var decision = ConversationResponseHistoryReader.Read(accepted);
                    if (decision.Success) activity.Activity.StepReason = decision.Response.Reason;
                    if (accepted.AcceptedCallOrigin?.CallIndex == 0)
                    {
                        activity.ReasoningContent = accepted.ReasoningContent;
                        activity.ReasoningTokens = accepted.ReasoningTokens;
                        activity.ReasoningTruncated = accepted.ReasoningTruncated;
                    }
                }
            }
            var appended = _session.Messages.GetRange(firstMessage, _session.Messages.Count - firstMessage);
            StampMessages(appended);
            Save(fact, appended, fact.Kind == AgentRunEventKind.ResponseAccepted ? null : activity);
            if (fact.Kind == AgentRunEventKind.ToolCompleted)
            {
                // Counts and the completed activity are already durable. Context/media
                // preparation cannot rewrite a known effect or cause a tool retry.
                firstMessage = _session.Messages.Count;
                await MaterializeResultAsync(fact.Execution).ConfigureAwait(false);
                appended = _session.Messages.GetRange(firstMessage, _session.Messages.Count - firstMessage);
                StampMessages(appended);
                Save(fact, appended, activity, true);
            }
            _cursor = _session.Revision;
            Report(fact, activity);
            return _cursor;
        }

        private void Save(AgentRunEvent fact = null, IEnumerable<ChatMessage> appended = null,
            ChatMessage activity = null, bool resultProjection = false)
        {
            // LLM causal trace/progress may advance this same session's global CAS
            // revision between kernel appends. The private cursor guards this port;
            // The conversation store guards against other sessions/processes with
            // the canonical backend revision CAS behind its adapter.
            // Native control and checkpoint construction share the existing Inbox
            // lock, including publication of the live CAS cursor. No wait/callback
            // runs under it; without an Inbox this port is the session's sole writer.
            lock (_eventStore.AtomicAgentDelivery && _inbox != null ? _inbox.Get(_session).Sync : _session)
            {
                var view = RunViewStateProjector.StampCurrentRun(_session);
                var projected = _session.Messages.LastOrDefault(message => view != null && ReferenceEquals(message.RunViewState, view));
                if (projected != null) _pendingMessageUpdates.Add(projected);
                if (fact == null) _conversations.Commit(_session, ConversationCommit.UserInput(appended.Single(), _pendingMessageUpdates));
                else new ConversationRunCommit(_session, fact, appended, activity,
                    resultProjection, _pendingMessageUpdates).Persist(_conversations.CommitRun, _session);
                _pendingMessageUpdates.Clear();
            }
            if (_saved != null) _saved(_session);
        }

        private ChatMessage ProjectToolCompletion(AgentRunEvent fact)
        {
            var record = fact.Execution;
            var command = Command(record.Context.Call, record.Context.StepId, record.Context.IsConfirmed);
            var materialized = TerminalMaterialization(record);
            var result = ToolRunResultFactory.Create(record, materialized);
            var activity = FindActivity(record.Context.Call.Id) ?? AgentTranscript.CreateRunningToolMessage(
                _session, command, record.Context.StepId, _stepMessage);
            if (!_session.Messages.Contains(activity)) _session.Messages.Add(activity);
            activity.RunId = _session.LastRun.RunId;
            AgentTranscript.CompleteToolActivityMessage(_session, activity, command, result, record.Context.StepId, _stepMessage);
            activity.Activity.ExecutionEvidence = record.Evidence;
            activity.Activity.RunId = _session.LastRun.RunId;
            activity.ExecutionProgress = ToolExecutionProgress.Capture(record);
            return activity;
        }

        private async Task MaterializeResultAsync(ToolExecutionRecord record)
        {
            var command = Command(record.Context.Call, record.Context.StepId, record.Context.IsConfirmed);
            var result = TerminalMaterialization(record);
            if (result != null)
            {
                try
                {
                    await EnsureModelSessionAsync(_runCancellation).ConfigureAwait(false);
                    if (record.Context.IsConfirmed)
                        _modelSession.AppendConfirmedResult(command, result);
                    else
                    {
                        var prepared = await _modelSession.PrepareToolResultAsync(
                            command, result, _runCancellation).ConfigureAwait(false);
                        result = prepared.Result;
                        _modelSession.AppendToolResult(command, prepared);
                    }
                }
                catch (Exception ex)
                {
                    _preparationFailure = AgentModelResult.Failed(
                        ex is OperationCanceledException
                            ? ModelProtocolFailureKind.Cancelled
                            : ex is PromptBudgetExceededException
                                ? ModelProtocolFailureKind.PromptBudgetExceeded
                                : ModelProtocolFailureKind.Infrastructure,
                        ex.Message);
                    // Close the exchange with its known outcome/effect and exact
                    // semantic result, even when auxiliary delivery failed.
                    if (!_session.Messages.Any(message => message.ProtocolMessage && message.Role != "assistant" && message.ToolCallId == command.ToolCallId))
                    {
                        var fallback = AgentJsonProtocol.CreateToolResultMessage(command,
                            ConversationModelSession.ProjectionFailure(command, result,
                                "Result materialization failed: " + ex.Message, "result_materialization_failed"),
                            int.MaxValue, _input.Settings.ToolResultRole, _input.Settings);
                        fallback.RunId = _session.LastRun.RunId;
                        _session.Messages.Add(fallback);
                    }
                }
            }
            var uiResult = ToolRunResultFactory.Create(record, result);
            var activity = FindActivity(record.Context.Call.Id);
            AgentTranscript.CompleteToolActivityMessage(_session, activity, command, uiResult, record.Context.StepId, _stepMessage);
            activity.Activity.ExecutionEvidence = record.Evidence;
            activity.Activity.RunId = _session.LastRun.RunId;
            _projectedResults.Add(AgentTranscript.DescribeResult(command, uiResult));
        }

        private ToolResultMaterialization TerminalMaterialization(ToolExecutionRecord record)
        {
            // Confirmation is still a pending call. A question set is already
            // recorded: close its call/result frame before waiting for an answer.
            if (record.Outcome == ToolExecutionOutcome.AwaitingConfirmation || record.Outcome == ToolExecutionOutcome.Waiting) return null;
            ToolResultMaterialization result;
            if (_results.TryGetValue(record.Context.Call.Id, out result))
            {
                result.ExecutionProgress = ToolExecutionProgress.Capture(record);
                return result;
            }
            var terminal = ToolResultPersistence.Terminal(record);
            result = new ToolResultMaterialization(terminal,
                resourceEvidence: record.ResourceEvidence, resourceEffect: record.ResourceEffect,
                authorityCommitId: record.AuthorityCommitId);
            result.ExecutionProgress = ToolExecutionProgress.Capture(record);
            _results[record.Context.Call.Id] = result;
            return result;
        }

        private ChatMessage FindActivity(string callId)
        {
            return _session.Messages.LastOrDefault(message => message.Activity != null && message.Activity.ToolCallId == callId);
        }

        private void AppendTerminalMessage(RunSummary summary)
        {
            if (summary.Lifecycle == RunLifecycle.Running || summary.Lifecycle == RunLifecycle.Waiting || summary.Lifecycle == RunLifecycle.AwaitingConfirmation ||
                summary.Reason == "awaiting_user") return;
            var diagnostic = RunViewStateProjector.TerminalDiagnostic(summary);
            string responseStatus = null;
            if (summary.Reason == "provider_refused") responseStatus = AgentResponseStatuses.Refused;
            else if (summary.Lifecycle == RunLifecycle.Completed)
                responseStatus = AgentResponseStatuses.Completed;
            var message = AgentTranscript.CreateAssistantMessage(
                ConversationRunProjection.AssistantMessage(summary),
                diagnostic != null || _lastModel == null ? null : _lastModel.Completion, diagnostic, responseStatus);
            message.RuntimeProjection = true;
            message.ExcludeFromModelContext = true;
            if (_modelSession != null) _modelSession.AttachResponseEvidence(message);
            _session.Messages.Add(message);
        }

        private void StampMessages(IEnumerable<ChatMessage> messages)
        {
            foreach (var message in messages)
            {
                message.RunId = _session.LastRun.RunId;
                if (message.Activity != null) message.Activity.RunId = _session.LastRun.RunId;
            }
        }

        private void Report(AgentRunEvent fact, ChatMessage activity)
        {
            if (_progress == null) return;
            if (fact.Kind == AgentRunEventKind.ExecutionQueued)
                _progress("queued_execution", "Ожидаю свободный слот выполнения…", null);
            else if (fact.Kind == AgentRunEventKind.ExecutionAdmitted)
                _progress("preparing_context", "Продолжаю выполнение…", null);
            else if (fact.Kind == AgentRunEventKind.ModelStepStarted)
                _progress("preparing_context", "Подготавливаю контекст…", null);
            else if (fact.Kind == AgentRunEventKind.ResponseAccepted &&
                fact.Response.Action == ConversationResponse.ReflectionAction)
                _progress("reflection", string.IsNullOrWhiteSpace(_stepMessage)
                    ? fact.Response.Decision : _stepMessage, activity.Activity);
            else if (fact.Kind == AgentRunEventKind.ResponseAccepted && !fact.Response.Final)
                _progress("acting", _stepMessage, new ChatActivity { StepId = fact.StepId, StepMessage = _stepMessage,
                    StepReason = fact.Response.Reason,
                    Kind = "step", Title = string.IsNullOrWhiteSpace(_stepMessage) ? "Выполняю действие" : _stepMessage, Status = "running" });
            else if (activity != null)
                _progress(fact.Kind == AgentRunEventKind.ToolWaiting ? "waiting_agents" : fact.Kind == AgentRunEventKind.ToolStarted ? "tool_running" : "tool_result",
                    activity.Activity.ResultMessage ?? _stepMessage ?? "Выполняю действие", activity.Activity);
        }
    }
}
