using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Storage;

namespace RNAssistant.Core.Services
{
    public static class ConversationRunProjection
    {
        // Both hosts project the same accepted kernel facts; host presentation
        // and actual request-delivery evidence stay with their existing owners.
        public static void Apply(ChatRunRecord run, AgentRunEvent fact)
        {
            run.KernelState = AgentRunState.Apply(run.KernelState, fact);
            Apply(run);
            switch (fact.Kind)
            {
                case AgentRunEventKind.ExecutionQueued: run.Phase = "queued_execution"; break;
                case AgentRunEventKind.ExecutionAdmitted: run.Phase = "running"; break;
                case AgentRunEventKind.ModelStepStarted: run.Phase = "thinking"; break;
                case AgentRunEventKind.ToolStarted: run.Phase = "executing"; break;
                case AgentRunEventKind.ToolWaiting: run.Phase = "waiting_agents"; break;
            }
        }

        public static IReadOnlyList<ChatMessage> CreateAcceptedResponse(AgentRunEvent fact,
            LlmCompletionResult completion, ChatBlobStore payloads)
        {
            if (fact?.Kind != AgentRunEventKind.ResponseAccepted || fact.Response == null ||
                string.IsNullOrWhiteSpace(fact.StepId) || string.IsNullOrWhiteSpace(fact.SourceModelAttemptId))
                throw new InvalidOperationException("Accepted response requires its exact step and model-attempt origin.");
            var messages = new List<ChatMessage>();
            var response = fact.Response;
            if (response.ToolCalls.Count == 0)
            {
                var message = new ChatMessage { Role = "assistant", ProtocolMessage = true,
                    RunId = fact.Summary.RunId, ResponseProtocolVersion = ConversationResponse.ProtocolVersion,
                    ResponseModelAttemptId = fact.SourceModelAttemptId,
                    ResponseStatus = response.Final ? AgentResponseStatuses.Completed : AgentResponseStatuses.InProgress,
                    Content = ModelProtocolWire.WriteAcceptedDecision(response.Action, response.Reason,
                        response.Message, response.Decision, response.Results, response.Outcome) };
                ApplyCompletion(message, completion);
                messages.Add(message);
            }
            for (var index = 0; index < response.ToolCalls.Count; index++)
            {
                var call = response.ToolCalls[index];
                var arguments = JsonConvert.DeserializeObject<Dictionary<string, object>>(call.ArgumentsJson,
                    new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });
                var message = CreateToolCallMessage(new AgentToolCall { Id = call.Id, Name = call.Name,
                    Arguments = arguments }, index == 0 ? response.Message : string.Empty, response.Reason,
                    index == 0 ? completion : null, ToolResultRoles.User,
                    new AcceptedToolCallOrigin(fact.StepId, fact.SourceModelAttemptId, index, response.ToolCalls.Count));
                message.RunId = fact.Summary.RunId;
                var encoded = JsonConvert.SerializeObject(arguments);
                if (payloads != null && encoded.Length > 8192)
                {
                    message.ArgumentPayload = PayloadRef.FromBlob(payloads.StoreText(encoded, "application/json"));
                    AcceptedCallPayloadService.Externalize(message, payloads);
                }
                messages.Add(message);
            }
            return messages;
        }

        public static void ApplyCompletion(ChatMessage message, LlmCompletionResult completion)
        {
            if (completion == null) return;
            const int limit = 24000;
            var reasoning = completion.ReasoningContent;
            var truncated = reasoning != null && reasoning.Length > limit;
            message.ReasoningContent = truncated ? reasoning.Substring(0, limit) : reasoning;
            message.ReasoningTokens = completion.ReasoningTokens;
            message.ReasoningTruncated = completion.ReasoningTruncated || truncated;
            message.PromptTokens = completion.PromptTokens;
            message.CompletionTokens = completion.CompletionTokens;
            message.TotalTokens = completion.TotalTokens;
            message.UsageJson = completion.UsageJson;
        }

        public static ChatMessage CreateToolCallMessage(
            AgentToolCall call,
            string message,
            string reason,
            RNAssistant.Core.Llm.LlmCompletionResult completion,
            string toolResultRole,
            AcceptedToolCallOrigin origin)
        {
            if (call == null || string.IsNullOrWhiteSpace(call.Id) || string.IsNullOrWhiteSpace(call.Name) ||
                string.IsNullOrWhiteSpace(reason) || origin == null)
                throw new ArgumentException("Accepted runtime call identity and origin are required.");
            var normalizedRole = ToolResultRoles.User;
            var content = ModelProtocolWire.WriteAcceptedCall(reason, message, new ConversationToolCall
                {
                    Name = call.Name,
                    Arguments = JObject.FromObject(call.Arguments ?? new Dictionary<string, object>())
                });
            var protocolMessage = new ChatMessage { Role = "assistant", Content = content };
            ApplyCompletion(protocolMessage, completion);
            protocolMessage.ResponseProtocolVersion = AgentResponseProtocol.CurrentVersion;
            protocolMessage.ToolResultProtocolVersion = ToolResultWire.CurrentVersion;
            protocolMessage.ResponseStatus = AgentResponseStatuses.InProgress;
            protocolMessage.ProtocolMessage = true;
            protocolMessage.ToolResultRole = normalizedRole;
            protocolMessage.ToolCallId = call.Id;
            protocolMessage.AcceptedCallOrigin = origin;
            protocolMessage.ToolName = call.Name;
            return protocolMessage;
        }

        public static string AssistantMessage(RunSummary summary)
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

        public static string Status(RunSummary summary)
        {
            if (summary.Lifecycle == RunLifecycle.Waiting) return "running";
            if (summary.Lifecycle == RunLifecycle.AwaitingConfirmation) return "waiting_confirmation";
            if (summary.Lifecycle == RunLifecycle.Completed &&
                summary.Reason == "awaiting_user") return "awaiting_user";
            return summary.Lifecycle.ToString().ToLowerInvariant();
        }

        public static void Apply(ChatRunRecord run)
        {
            var summary = run.KernelState.Summary;
            run.RunId = summary.RunId;
            run.TurnId = summary.TurnId;
            run.Status = Status(summary);
            run.Phase = summary.Lifecycle == RunLifecycle.Waiting ? (summary.PauseId == null ? "waiting_agents" : "paused") : run.Status;
            run.CurrentAction = AssistantMessage(summary);
            run.ResponseProtocolVersion = AgentResponseProtocol.CurrentVersion;
            run.IterationsUsed = summary.IterationsUsed;
            run.ToolStepsUsed = summary.ToolStepsUsed;
        }
    }
}
