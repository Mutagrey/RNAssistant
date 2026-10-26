using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Tools;
using RNAssistant.Core.ModelProtocol;

namespace RNAssistant.Core.Agent
{
    // Arguments and result bodies are opaque to the kernel. Their validation and
    // materialization belong to the model/tool ports, not to the loop.
    public sealed class ToolCallDraft
    {
        public string Name { get; private set; }
        public string ArgumentsJson { get; private set; }

        public ToolCallDraft(string name, string argumentsJson)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Tool name is required.", nameof(name));
            Name = name;
            ArgumentsJson = argumentsJson ?? throw new ArgumentNullException(nameof(argumentsJson));
        }
    }

    // Validated model proposal has no call identity. Only kernel assigns IDs.
    public sealed class AgentResponseDraft
    {
        public string Action { get; private set; }
        public string Reason { get; private set; }
        public string Message { get; private set; }
        public string Decision { get; private set; }
        public ConversationOutcome? Outcome { get; private set; }
        public IReadOnlyList<ConversationResultClaim> Results { get; private set; }
        public IReadOnlyList<ToolCallDraft> ToolCalls { get; private set; }

        public AgentResponseDraft(string action, string reason, string message,
            IEnumerable<ToolCallDraft> calls = null, string decision = null,
            IEnumerable<ConversationResultClaim> results = null, ConversationOutcome? outcome = null)
        {
            var snapshot = (calls ?? new ToolCallDraft[0]).ToArray();
            if (!ConversationResponse.IsValidAction(action, snapshot.Length) ||
                action == ConversationResponse.ExecuteAction && (decision != null || results != null) ||
                action == ConversationResponse.ReflectionAction && (calls != null || results != null || string.IsNullOrWhiteSpace(decision)) ||
                action == ConversationResponse.FinishAction && (calls != null || decision != null || results == null) ||
                snapshot.Any(call => call == null))
                throw new ArgumentException("Invalid v8 model proposal.");
            if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A reason is required.", nameof(reason));
            if (message == null) throw new ArgumentNullException(nameof(message));
            var claims = results == null ? null : results.ToArray();
            if (claims != null && claims.Any(claim => claim == null)) throw new ArgumentException("Null result claim.", nameof(results));
            ConversationFinishContract.Validate(action, message, outcome, claims);
            Action = action; Reason = reason; Message = message; Decision = decision;
            Outcome = outcome;
            Results = claims == null ? null : Array.AsReadOnly(claims);
            ToolCalls = Array.AsReadOnly(snapshot);
        }
    }

    // Accepted execution identity; restored from durable history, never from a
    // model-generated identifier or by generating a new id during replay.
    public sealed class ToolCall
    {
        public string Id { get; private set; }
        public string Name { get; private set; }
        public string ArgumentsJson { get; private set; }

        public ToolCall(string id, string name, string argumentsJson)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Call id is required.", nameof(id));
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Tool name is required.", nameof(name));
            Id = id;
            Name = name;
            ArgumentsJson = argumentsJson ?? throw new ArgumentNullException(nameof(argumentsJson));
        }
    }

    public sealed class AgentResponse
    {
        public string Action { get; private set; }
        public string Reason { get; private set; }
        public string Message { get; private set; }
        public string Decision { get; private set; }
        public ConversationOutcome? Outcome { get; private set; }
        public string SourceModelAttemptId { get; private set; }
        public bool Final { get { return Action == ConversationResponse.FinishAction; } }
        public IReadOnlyList<ConversationResultClaim> Results { get; private set; }
        public IReadOnlyList<ToolCall> ToolCalls { get; private set; }

        public AgentResponse(string action, string reason, string message,
            IEnumerable<ToolCall> calls = null, string decision = null,
            IEnumerable<ConversationResultClaim> results = null, string sourceModelAttemptId = null,
            ConversationOutcome? outcome = null)
        {
            var snapshot = (calls ?? new ToolCall[0]).ToArray();
            if (!ConversationResponse.IsValidAction(action, snapshot.Length) ||
                action == ConversationResponse.ExecuteAction && (decision != null || results != null) ||
                action == ConversationResponse.ReflectionAction && (calls != null || results != null || string.IsNullOrWhiteSpace(decision)) ||
                action == ConversationResponse.FinishAction && (calls != null || decision != null || results == null) ||
                snapshot.Any(call => call == null))
                throw new ArgumentException("Invalid accepted v8 response.");
            if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A reason is required.", nameof(reason));
            if (message == null) throw new ArgumentNullException(nameof(message));
            var claims = results == null ? null : results.ToArray();
            if (claims != null && claims.Any(claim => claim == null)) throw new ArgumentException("Null result claim.", nameof(results));
            ConversationFinishContract.Validate(action, message, outcome, claims);
            Action = action; Reason = reason; Message = message; Decision = decision;
            Outcome = outcome;
            SourceModelAttemptId = sourceModelAttemptId;
            Results = claims == null ? null : Array.AsReadOnly(claims);
            ToolCalls = Array.AsReadOnly(snapshot);
        }
    }

    public enum AgentMessageKind { User, Assistant, ToolResult }

    public sealed class AgentMessage
    {
        public AgentMessageKind Kind { get; private set; }
        public string Text { get; private set; }
        public string Action { get; private set; }
        public string Reason { get; private set; }
        public string Decision { get; private set; }
        public ConversationOutcome? Outcome { get; private set; }
        public string SourceModelAttemptId { get; private set; }
        public IReadOnlyList<ConversationResultClaim> Results { get; private set; }
        public IReadOnlyList<ToolCall> ToolCalls { get; private set; }
        public string ToolCallId { get; private set; }
        public string ResultJson { get; private set; }
        public ToolExecutionRecord Execution { get; private set; }
        public ToolExecutionProgress Progress { get; private set; }
        public IReadOnlyList<RNAssistant.Core.Models.ResourceEvidence> ResourceEvidence { get; private set; }
        public RNAssistant.Core.Models.ResourceEffect ResourceEffect { get; private set; }

        private AgentMessage(AgentMessageKind kind, string text, IReadOnlyList<ToolCall> calls,
            string toolCallId = null, string resultJson = null, ToolExecutionRecord execution = null,
            string action = null)
        {
            Kind = kind;
            Text = text ?? string.Empty;
            ToolCalls = calls ?? Array.AsReadOnly(new ToolCall[0]);
            Action = action;
            ToolCallId = toolCallId;
            ResultJson = resultJson;
            Execution = execution;
        }

        public static AgentMessage User(string text)
        {
            return new AgentMessage(AgentMessageKind.User, text, null);
        }

        public static AgentMessage Assistant(AgentResponse response)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));
            return new AgentMessage(AgentMessageKind.Assistant, response.Message, response.ToolCalls,
                action: response.Action) { Reason = response.Reason, Decision = response.Decision,
                    Results = response.Results, Outcome = response.Outcome, SourceModelAttemptId = response.SourceModelAttemptId };
        }

        // Already validated, materialized history from an earlier user turn.
        // It cannot seed current execution counts or authorize a dispatch.
        public static AgentMessage AcceptedToolResult(string callId, string message, string resultJson,
            ToolExecutionProgress progress = null,
            IEnumerable<RNAssistant.Core.Models.ResourceEvidence> evidence = null,
            RNAssistant.Core.Models.ResourceEffect effect = null)
        {
            if (string.IsNullOrWhiteSpace(callId)) throw new ArgumentException("Call id is required.", nameof(callId));
            return new AgentMessage(AgentMessageKind.ToolResult, message, null, callId, resultJson) {
                Progress = progress, ResourceEvidence = (evidence ?? new RNAssistant.Core.Models.ResourceEvidence[0]).ToArray(),
                ResourceEffect = effect };
        }

        public static AgentMessage ToolResult(ToolExecutionRecord execution)
        {
            if (execution == null) throw new ArgumentNullException(nameof(execution));
            return new AgentMessage(AgentMessageKind.ToolResult, execution.Message, null,
                execution.Context.Call.Id, execution.ModelResultJson, execution) {
                    Progress = ToolExecutionProgress.Capture(execution), ResourceEvidence = execution.ResourceEvidence,
                    ResourceEffect = execution.ResourceEffect };
        }
    }
}
