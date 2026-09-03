using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Tools;

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

    // Validated model content has no execution identity. Only the kernel can
    // turn this draft into an accepted response before persistence/dispatch.
    public sealed class AgentResponseDraft
    {
        public string Message { get; private set; }
        public bool Final { get; private set; }
        public IReadOnlyList<ToolCallDraft> ToolCalls { get; private set; }

        public AgentResponseDraft(string message, IEnumerable<ToolCallDraft> calls, bool? final = null)
        {
            var snapshot = (calls ?? throw new ArgumentNullException(nameof(calls))).ToArray();
            if (snapshot.Any(call => call == null)) throw new ArgumentException("Calls cannot contain null.", nameof(calls));
            var isFinal = final ?? snapshot.Length == 0;
            if (isFinal && snapshot.Length > 0) throw new ArgumentException("A final response cannot contain tool calls.", nameof(final));
            Message = message ?? string.Empty;
            Final = isFinal;
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
        public string Message { get; private set; }
        public bool Final { get; private set; }
        public IReadOnlyList<ToolCall> ToolCalls { get; private set; }

        public AgentResponse(string message, IEnumerable<ToolCall> calls, bool? final = null)
        {
            var snapshot = (calls ?? throw new ArgumentNullException(nameof(calls))).ToArray();
            if (snapshot.Any(call => call == null)) throw new ArgumentException("Calls cannot contain null.", nameof(calls));
            var isFinal = final ?? snapshot.Length == 0;
            if (isFinal && snapshot.Length > 0) throw new ArgumentException("A final response cannot contain tool calls.", nameof(final));
            Message = message ?? string.Empty;
            Final = isFinal;
            ToolCalls = Array.AsReadOnly(snapshot);
        }
    }

    public enum AgentMessageKind { User, Assistant, ToolResult }

    public sealed class AgentMessage
    {
        public AgentMessageKind Kind { get; private set; }
        public string Text { get; private set; }
        public IReadOnlyList<ToolCall> ToolCalls { get; private set; }
        public string ToolCallId { get; private set; }
        public string ResultJson { get; private set; }
        public ToolExecutionRecord Execution { get; private set; }

        private AgentMessage(AgentMessageKind kind, string text, IReadOnlyList<ToolCall> calls,
            string toolCallId = null, string resultJson = null, ToolExecutionRecord execution = null)
        {
            Kind = kind;
            Text = text ?? string.Empty;
            ToolCalls = calls ?? Array.AsReadOnly(new ToolCall[0]);
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
            return new AgentMessage(AgentMessageKind.Assistant, response.Message, response.ToolCalls);
        }

        // Already validated, materialized history from an earlier user turn.
        // It cannot seed current execution counts or authorize a dispatch.
        public static AgentMessage AcceptedToolResult(string callId, string message, string resultJson)
        {
            if (string.IsNullOrWhiteSpace(callId)) throw new ArgumentException("Call id is required.", nameof(callId));
            return new AgentMessage(AgentMessageKind.ToolResult, message, null, callId, resultJson);
        }

        public static AgentMessage ToolResult(ToolExecutionRecord execution)
        {
            if (execution == null) throw new ArgumentNullException(nameof(execution));
            return new AgentMessage(AgentMessageKind.ToolResult, execution.Message, null,
                execution.Context.Call.Id, execution.ModelResultJson, execution);
        }
    }
}
