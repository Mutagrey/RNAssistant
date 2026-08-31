using System;
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
using LegacyResult = RNAssistant.Core.Models.ToolResult;
using TerminalResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Services
{
    internal sealed partial class ConversationKernelAdapter
    {
        public async Task<long> AppendAsync(AgentRunEvent fact, long expectedRevision, CancellationToken cancellationToken)
        {
            if (expectedRevision != _cursor) throw new InvalidOperationException("Stale kernel continuation cursor.");
            var firstMessage = _session.Messages.Count;
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
                    // Persist the entire accepted batch before any dispatch. Callable
                    // pack membership changes only at the next model-step boundary.
                    for (var index = 0; index < fact.Response.ToolCalls.Count; index++)
                        _modelSession.AppendToolCall(new AgentToolCall
                            { Id = fact.Response.ToolCalls[index].Id, Name = fact.Response.ToolCalls[index].Name,
                                Arguments = ReadArguments(fact.Response.ToolCalls[index]) },
                            index == 0 ? fact.Response.Message : string.Empty,
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
                    break;
                case AgentRunEventKind.ToolCompleted:
                    activity = ProjectToolCompletion(fact);
                    break;
                case AgentRunEventKind.SummaryChanged:
                    AppendTerminalMessage(fact.Summary);
                    break;
            }
            StampMessages(firstMessage);
            Save();
            if (fact.Kind == AgentRunEventKind.ToolCompleted)
            {
                // Counts and the completed activity are already durable. Context/media
                // preparation cannot rewrite a known effect or cause a tool retry.
                await MaterializeResultAsync(fact.Execution).ConfigureAwait(false);
                StampMessages(firstMessage);
                Save();
            }
            _cursor = _session.Revision;
            Report(fact, activity);
            return _cursor;
        }

        private void Save()
        {
            // LLM causal trace/progress may advance this same session's global CAS
            // revision between kernel appends. The private cursor guards this port;
            // The conversation store guards against other sessions/processes with
            // the canonical backend revision CAS behind its adapter.
            RunViewStateProjector.StampCurrentRun(_session);
            _conversations.Save(_session);
            if (_saved != null) _saved(_session);
        }

        private ChatMessage ProjectToolCompletion(AgentRunEvent fact)
        {
            var record = fact.Execution;
            var command = Command(record.Context.Call, record.Context.StepId, record.Context.IsConfirmed);
            var materialized = TerminalMaterialization(record);
            LegacyResult result;
            if (!_uiResults.TryGetValue(record.Context.Call.Id, out result) ||
                LegacyToolOutcomeAdapter.Map(record.Context.Policy, result) != record.Outcome)
            {
                result = ToolResultUiProjection.Create(record);
                _uiResults[record.Context.Call.Id] = result;
            }
            ToolResultUiProjection.IncludeResources(result, materialized);
            var activity = FindActivity(record.Context.Call.Id) ?? AgentTranscript.CreateRunningToolMessage(
                _session, command, record.Context.StepId, _stepMessage);
            if (!_session.Messages.Contains(activity)) _session.Messages.Add(activity);
            activity.RunId = _session.LastRun.RunId;
            AgentTranscript.CompleteToolActivityMessage(_session, activity, command, result, record.Context.StepId, _stepMessage);
            activity.Activity.ExecutionEvidence = record.Evidence;
            activity.Activity.RunId = _session.LastRun.RunId;
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
                    // Close the accepted exchange without copying a large/unprepared
                    // payload. This is projection failure, not new execution evidence.
                    if (!_session.Messages.Any(message => message.ProtocolMessage && message.Role != "assistant" && message.ToolCallId == command.ToolCallId))
                    {
                        var fallback = AgentJsonProtocol.CreateToolResultMessage(command,
                            new TerminalResult(
                                ToolResultResourceService.ProjectionFailureStatus(command, result.Result.Status),
                                "Result materialization failed: " + ex.Message,
                                new JObject { ["code"] = "result_materialization_failed", ["loaded"] = false,
                                    ["complete"] = false }.ToString(Formatting.None)), _input.Settings.ToolResultRole);
                        fallback.RunId = _session.LastRun.RunId;
                        ConversationModelSession.AppendPairedResult(_session.Messages, fallback);
                    }
                }
            }
            var uiResult = _uiResults[record.Context.Call.Id];
            ToolResultUiProjection.IncludeResources(uiResult, result);
            var activity = FindActivity(record.Context.Call.Id);
            AgentTranscript.CompleteToolActivityMessage(_session, activity, command, uiResult, record.Context.StepId, _stepMessage);
            activity.Activity.ExecutionEvidence = record.Evidence;
            activity.Activity.RunId = _session.LastRun.RunId;
            _projectedResults.Add(AgentTranscript.DescribeResult(command, uiResult));
        }

        private ToolResultMaterialization TerminalMaterialization(ToolExecutionRecord record)
        {
            // Confirmation/user-input pauses have no terminal wire result. Proven
            // non-dispatch is kept in the record, independently of the error payload.
            if (record.Outcome == ToolExecutionOutcome.AwaitingConfirmation || record.AwaitingUser) return null;
            ToolResultMaterialization result;
            if (_results.TryGetValue(record.Context.Call.Id, out result)) return result;
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
            result = new ToolResultMaterialization(terminal);
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
            else if (summary.Lifecycle == RunLifecycle.Completed) responseStatus = AgentResponseStatuses.Completed;
            else diagnostic = new ChatActivity
            {
                Kind = "diagnostic", Title = "Выполнение остановлено", Status = ConversationRunProjection.Status(summary),
                ExecutionStatus = summary.Reason == "ProtocolExhausted" ? "invalid_model_response"
                    : summary.Reason == "PromptBudgetExceeded" ? "prompt_budget_exceeded"
                    : summary.Reason == "iteration_limit" || summary.Reason == "tool_step_limit" ? "step_limit_reached" : summary.Reason,
                ResultMessage = summary.AssistantMessage
            };
            var message = AgentTranscript.CreateAssistantMessage(summary.AssistantMessage,
                diagnostic != null || _lastModel == null ? null : _lastModel.Completion, diagnostic, responseStatus);
            _session.Messages.Add(message);
        }

        private void StampMessages(int first)
        {
            for (var index = first; index < _session.Messages.Count; index++)
            {
                var message = _session.Messages[index];
                message.RunId = _session.LastRun.RunId;
                if (message.Activity != null) message.Activity.RunId = _session.LastRun.RunId;
            }
        }

        private void Report(AgentRunEvent fact, ChatMessage activity)
        {
            if (_progress == null) return;
            if (fact.Kind == AgentRunEventKind.ModelStepStarted)
                _progress("thinking", "Модель выбирает следующий шаг...", null);
            else if (fact.Kind == AgentRunEventKind.ResponseAccepted && fact.Response.ToolCalls.Count > 0 && !string.IsNullOrWhiteSpace(_stepMessage))
                _progress("acting", _stepMessage, new ChatActivity { StepId = fact.StepId, StepMessage = _stepMessage,
                    Kind = "step", Title = _stepMessage, Status = "running" });
            else if (activity != null)
                _progress(fact.Kind == AgentRunEventKind.ToolStarted ? "tool_running" : "tool_result",
                    activity.Activity.ResultMessage ?? _stepMessage ?? "Выполняю действие", activity.Activity);
        }
    }

    internal static class ConversationRunProjection
    {
        internal static string Status(RunSummary summary)
        {
            if (summary.Lifecycle == RunLifecycle.AwaitingConfirmation) return "waiting_confirmation";
            if (summary.Lifecycle == RunLifecycle.Completed && summary.Reason == "awaiting_user") return "awaiting_user";
            return summary.Lifecycle.ToString().ToLowerInvariant();
        }

        internal static void Apply(ChatRunRecord run)
        {
            var summary = run.KernelState.Summary;
            run.RunId = summary.RunId;
            run.TurnId = summary.TurnId;
            run.Status = Status(summary);
            run.Phase = run.Status;
            run.CurrentAction = summary.AssistantMessage;
            run.ResponseProtocolVersion = AgentResponseProtocol.CurrentVersion;
            run.IterationsUsed = summary.IterationsUsed;
            run.ToolStepsUsed = summary.ToolStepsUsed;
        }
    }
}
