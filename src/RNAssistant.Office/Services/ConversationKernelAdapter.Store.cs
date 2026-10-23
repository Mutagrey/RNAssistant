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
        public Task<FinishAdmission> EvaluateFinishAsync(string runId, string turnId,
            IReadOnlyList<string> acceptedCallIds, IReadOnlyList<ConversationResultClaim> results,
            long expectedRevision, CancellationToken cancellationToken)
        {
            if (expectedRevision != _cursor || runId != _session.LastRun.RunId ||
                turnId != _session.LastRun.TurnId)
                throw new InvalidOperationException("Stale finish admission cursor or run/session identity.");
            cancellationToken.ThrowIfCancellationRequested();
            var events = _eventStore.Read(_session, SessionEventReadMode.RequireComplete);
            if (events.Count == 0 || events[events.Count - 1].Sequence != _session.Revision)
                throw new InvalidOperationException("Canonical finish evidence is not current.");
            return Task.FromResult(ConversationFinishEvidenceAdmission.Evaluate(events,
                _session.Id, runId, turnId, acceptedCallIds, results, _executor.Payloads));
        }

        public async Task<long> AppendAsync(AgentRunEvent fact, long expectedRevision, CancellationToken cancellationToken)
        {
            if (expectedRevision != _cursor) throw new InvalidOperationException("Stale kernel continuation cursor.");
            var previousMessages = new HashSet<string>(_session.Messages.Select(message => message.Id), StringComparer.Ordinal);
            var run = _session.LastRun;
            run.KernelState = AgentRunState.Apply(run.KernelState, fact);
            ConversationRunProjection.Apply(run);
            ChatMessage activity = null;
            switch (fact.Kind)
            {
                case AgentRunEventKind.ModelStepStarted:
                    run.Phase = "thinking";
                    break;
                case AgentRunEventKind.ResponseAccepted:
                    _stepMessage = fact.Response.Message;
                    if (fact.Response.ToolCalls.Count == 0)
                    {
                        if (string.IsNullOrWhiteSpace(fact.SourceModelAttemptId))
                            throw new InvalidOperationException("Accepted v8 decision lacks its model attempt origin.");
                        var decision = AgentTranscript.CreateAssistantMessage(
                            ModelProtocolWire.WriteAcceptedDecision(fact.Response.Action, fact.Response.Reason,
                                fact.Response.Message, fact.Response.Decision, fact.Response.Results),
                            _lastModel == null ? null : _lastModel.Completion,
                            responseStatus: fact.Response.Final ? AgentResponseStatuses.Completed : AgentResponseStatuses.InProgress);
                        decision.ProtocolMessage = true;
                        decision.ResponseProtocolVersion = AgentResponseProtocol.CurrentVersion;
                        decision.ResponseModelAttemptId = fact.SourceModelAttemptId;
                        if (_modelSession != null) _modelSession.AttachResponseEvidence(decision);
                        _session.Messages.Add(decision);
                        if (fact.Response.Action == ConversationResponse.ReflectionAction)
                        {
                            var visibleDecision = string.IsNullOrWhiteSpace(fact.Response.Message)
                                ? fact.Response.Decision : fact.Response.Message;
                            _session.Messages.Add(new ChatMessage
                            {
                                Role = "assistant", Content = string.Empty,
                                RuntimeProjection = true, ExcludeFromModelContext = true,
                                Activity = new ChatActivity
                                {
                                    Kind = "step", StepId = fact.StepId, StepMessage = visibleDecision,
                                    Title = "Промежуточное решение", Status = "completed"
                                }
                            });
                        }
                    }
                    // Persist the entire accepted batch before any dispatch. Callable
                    // pack membership changes only at the next model-step boundary.
                    for (var index = 0; index < fact.Response.ToolCalls.Count; index++)
                        _modelSession.AppendToolCall(new AgentToolCall
                            { Id = fact.Response.ToolCalls[index].Id, Name = fact.Response.ToolCalls[index].Name,
                                Arguments = ReadArguments(fact.Response.ToolCalls[index]) },
                            index == 0 ? fact.Response.Message : string.Empty,
                            fact.Response.Reason,
                            index == 0 ? _lastModel.Completion : null,
                            new AcceptedToolCallOrigin(fact.StepId, _lastModel.SourceModelAttemptId, index));
                    break;
                case AgentRunEventKind.ToolStarted:
                    run.Phase = "executing";
                    var context = fact.ToolContext;
                    var command = Command(context.Call, context.StepId, context.IsConfirmed);
                    activity = FindActivity(context.Call.Id) ?? AgentTranscript.CreateRunningToolMessage(
                        _session, command, fact.StepId, _stepMessage);
                    if (!_session.Messages.Contains(activity)) _session.Messages.Add(activity);
                    activity.Activity = AgentTranscript.CreateRunningToolActivity(command, fact.StepId, _stepMessage);
                    activity.RunId = run.RunId;
                    activity.Activity.RunId = run.RunId;
                    break;
                case AgentRunEventKind.ToolCompleted:
                    activity = ProjectToolCompletion(fact);
                    break;
                case AgentRunEventKind.SummaryChanged:
                    AppendTerminalMessage(fact.Summary);
                    break;
            }
            var appended = _session.Messages.Where(message => !previousMessages.Contains(message.Id)).ToArray();
            StampMessages(appended);
            Save(fact, appended, activity);
            if (fact.Kind == AgentRunEventKind.ToolCompleted)
            {
                // Counts and the completed activity are already durable. Context/media
                // preparation cannot rewrite a known effect or cause a tool retry.
                previousMessages = new HashSet<string>(_session.Messages.Select(message => message.Id), StringComparer.Ordinal);
                await MaterializeResultAsync(fact.Execution).ConfigureAwait(false);
                appended = _session.Messages.Where(message => !previousMessages.Contains(message.Id)).ToArray();
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
            var view = RunViewStateProjector.StampCurrentRun(_session);
            var projected = _session.Messages.LastOrDefault(message => view != null && ReferenceEquals(message.RunViewState, view));
            if (projected != null) _pendingMessageUpdates.Add(projected);
            if (fact == null) _conversations.Commit(_session, ConversationCommit.UserInput(appended.Single(), _pendingMessageUpdates));
            else _conversations.CommitRun(_session, new ConversationRunCommit(fact, appended, activity,
                resultProjection, _pendingMessageUpdates));
            _pendingMessageUpdates.Clear();
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
                        ConversationModelSession.AppendPairedResult(_session.Messages, fallback);
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
            if (record.Outcome == ToolExecutionOutcome.AwaitingConfirmation) return null;
            ToolResultMaterialization result;
            if (_results.TryGetValue(record.Context.Call.Id, out result))
            {
                result.ExecutionProgress = ToolExecutionProgress.Capture(record);
                return result;
            }
            var terminal = record.Result;
            if (terminal == null)
            {
                var code = record.Context.IsConfirmed && !record.MayHaveDispatched && record.Outcome == ToolExecutionOutcome.Error
                    ? "pending_tool_catalog_changed" : record.Outcome == ToolExecutionOutcome.NotDispatched
                        ? "tool_not_dispatched" : "execution_interrupted";
                terminal = new TerminalResult(record.Outcome == ToolExecutionOutcome.Ok ? ToolResultStatus.Ok :
                    record.Outcome == ToolExecutionOutcome.Unknown ? ToolResultStatus.Unknown : ToolResultStatus.Error,
                    record.Message, new JObject { ["code"] = code }.ToString(Formatting.None));
            }
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
            if (summary.Lifecycle == RunLifecycle.Running || summary.Lifecycle == RunLifecycle.AwaitingConfirmation ||
                summary.Reason == "awaiting_user") return;
            ChatActivity diagnostic = null;
            string responseStatus = null;
            if (summary.Reason == "provider_refused") responseStatus = AgentResponseStatuses.Refused;
            else if (summary.Lifecycle == RunLifecycle.Completed)
                responseStatus = AgentResponseStatuses.Completed;
            else diagnostic = new ChatActivity
            {
                Kind = "diagnostic", Title = "Выполнение остановлено", Status = ConversationRunProjection.Status(summary),
                ExecutionStatus = summary.Reason == "ProtocolExhausted" ? "invalid_model_response"
                    : summary.Reason == "PromptBudgetExceeded" ? "prompt_budget_exceeded"
                    : summary.Reason == "iteration_limit" || summary.Reason == "tool_step_limit" ? "step_limit_reached" : summary.Reason,
                ResultMessage = summary.AssistantMessage
            };
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
            if (fact.Kind == AgentRunEventKind.ModelStepStarted)
                _progress("thinking", "Модель выбирает следующий шаг...", null);
            else if (fact.Kind == AgentRunEventKind.ResponseAccepted &&
                fact.Response.Action == ConversationResponse.ReflectionAction)
                _progress("thinking", string.IsNullOrWhiteSpace(_stepMessage)
                    ? fact.Response.Decision : _stepMessage, null);
            else if (fact.Kind == AgentRunEventKind.ResponseAccepted && !fact.Response.Final && !string.IsNullOrWhiteSpace(_stepMessage))
                _progress("acting", _stepMessage, new ChatActivity { StepId = fact.StepId, StepMessage = _stepMessage,
                    Kind = "step", Title = _stepMessage, Status = "running" });
            else if (activity != null)
                _progress(fact.Kind == AgentRunEventKind.ToolStarted ? "tool_running" : "tool_result",
                    activity.Activity.ResultMessage ?? _stepMessage ?? "Выполняю действие", activity.Activity);
        }
    }

    internal static class ConversationRunProjection
    {
        internal static string AssistantMessage(RunSummary summary)
        {
            if (summary == null) return string.Empty;
            var message = summary.AssistantMessage ?? string.Empty;
            if (summary.Lifecycle != RunLifecycle.Completed || summary.ToolCounts == null)
                return message;
            var errors = summary.ToolCounts.WriteError;
            var unknown = summary.ToolCounts.WriteUnknown;
            if (errors <= 0 && unknown <= 0) return message;
            var notice = unknown > 0
                ? "История попыток: состояние " + unknown +
                    " операций записи осталось неизвестным. Успешное применение всех изменений не подтверждено."
                : "История попыток: " + errors + " операций записи завершились ошибкой.";
            notice += " Это счётчики попыток, а не число нерешённых проблем; " +
                "последующее исправление конкретной операции ими не определяется.";
            return string.IsNullOrWhiteSpace(message)
                ? notice
                : message.TrimEnd() + "\n\n" + notice;
        }

        internal static string Status(RunSummary summary)
        {
            if (summary.Lifecycle == RunLifecycle.AwaitingConfirmation) return "waiting_confirmation";
            if (summary.Lifecycle == RunLifecycle.Completed &&
                summary.Reason == "awaiting_user") return "awaiting_user";
            return summary.Lifecycle.ToString().ToLowerInvariant();
        }

        internal static void Apply(ChatRunRecord run)
        {
            var summary = run.KernelState.Summary;
            run.RunId = summary.RunId;
            run.TurnId = summary.TurnId;
            run.Status = Status(summary);
            run.Phase = run.Status;
            run.CurrentAction = AssistantMessage(summary);
            run.ResponseProtocolVersion = AgentResponseProtocol.CurrentVersion;
            run.IterationsUsed = summary.IterationsUsed;
            run.ToolStepsUsed = summary.ToolStepsUsed;
        }
    }
}
