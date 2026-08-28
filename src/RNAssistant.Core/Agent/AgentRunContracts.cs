using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Agent
{
    public sealed class AgentRunLimits
    {
        public int MaxIterations { get; private set; }
        public int MaxToolSteps { get; private set; }

        public AgentRunLimits(int maxIterations, int maxToolSteps)
        {
            if (maxIterations < 1) throw new ArgumentOutOfRangeException(nameof(maxIterations));
            if (maxToolSteps < 1) throw new ArgumentOutOfRangeException(nameof(maxToolSteps));
            MaxIterations = maxIterations;
            MaxToolSteps = maxToolSteps;
        }
    }

    public sealed class AgentRunRequest
    {
        public string RunId { get; private set; }
        public string TurnId { get; private set; }
        public string UserMessage { get; private set; }
        public AgentRunLimits Limits { get; private set; }
        public IReadOnlyList<AgentMessage> PreviousMessages { get; private set; }

        public AgentRunRequest(string runId, string turnId, string userMessage, AgentRunLimits limits,
            IEnumerable<AgentMessage> previousMessages = null)
        {
            if (string.IsNullOrWhiteSpace(runId)) throw new ArgumentException("Run id is required.", nameof(runId));
            if (string.IsNullOrWhiteSpace(turnId)) throw new ArgumentException("Turn id is required.", nameof(turnId));
            var previous = (previousMessages ?? new AgentMessage[0]).ToArray();
            if (previous.Any(message => message == null)) throw new ArgumentException("History cannot contain null.", nameof(previousMessages));
            RunId = runId;
            TurnId = turnId;
            UserMessage = userMessage ?? string.Empty;
            Limits = limits ?? throw new ArgumentNullException(nameof(limits));
            PreviousMessages = Array.AsReadOnly(previous);
        }
    }

    public enum AgentRunEventKind { Started, ModelStepStarted, ResponseAccepted, ToolStarted, ToolCompleted, SummaryChanged }

    public sealed class AgentRunEvent
    {
        public AgentRunEventKind Kind { get; private set; }
        public RunSummary Summary { get; private set; }
        public AgentRunLimits Limits { get; private set; }
        public string StepId { get; private set; }
        public AgentMessage UserMessage { get; private set; }
        public AgentResponse Response { get; private set; }
        public ToolExecutionContext ToolContext { get; private set; }
        public ToolExecutionRecord Execution { get; private set; }

        internal AgentRunEvent(AgentRunEventKind kind, RunSummary summary, string stepId = null,
            AgentRunLimits limits = null, AgentMessage userMessage = null, AgentResponse response = null,
            ToolExecutionContext toolContext = null, ToolExecutionRecord execution = null)
        {
            Kind = kind;
            Summary = summary;
            Limits = limits;
            StepId = stepId;
            UserMessage = userMessage;
            Response = response;
            ToolContext = toolContext;
            Execution = execution;
        }
    }

    // An immutable in-memory continuation, not a second durable authority.
    // A storage adapter must reconstruct it only from a validated typed event stream.
    public sealed class AgentRunContinuation
    {
        public RunSummary Summary { get; private set; }
        public AgentRunLimits Limits { get; private set; }
        public long Revision { get; private set; }
        public IReadOnlyList<AgentMessage> AcceptedMessages { get; private set; }
        public IReadOnlyList<string> AcceptedCallIds { get; private set; }

        internal AgentRunContinuation(RunSummary summary, AgentRunLimits limits, long revision,
            IEnumerable<AgentMessage> messages, IEnumerable<string> ids)
        {
            Summary = summary;
            Limits = limits;
            Revision = revision;
            AcceptedMessages = Array.AsReadOnly(messages.ToArray());
            AcceptedCallIds = Array.AsReadOnly(ids.OrderBy(id => id, StringComparer.Ordinal).ToArray());
        }

        public static AgentRunContinuation Restore(RunSummary summary, AgentRunLimits limits, long revision,
            IEnumerable<AgentMessage> acceptedMessages)
        {
            if (summary == null || summary.Lifecycle != RunLifecycle.AwaitingConfirmation || summary.PendingConfirmation == null ||
                limits == null || revision < 0 || summary.IterationsUsed > limits.MaxIterations || summary.ToolStepsUsed > limits.MaxToolSteps)
                throw new InvalidOperationException("A validated pending run summary is required; open a new chat or cancel the pending action.");
            var messages = (acceptedMessages ?? new AgentMessage[0]).ToArray();
            if (messages.Length == 0 || messages.Any(message => message == null))
                throw new InvalidOperationException("Complete accepted history is required.");
            var calls = messages.SelectMany(message => message.ToolCalls).ToArray();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var call in calls)
                if (!ids.Add(call.Id)) throw new InvalidOperationException("Duplicate call in accepted continuation history.");
            var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var message in messages.Where(message => message.Kind == AgentMessageKind.ToolResult))
                if (!ids.Contains(message.ToolCallId) || !completed.Add(message.ToolCallId))
                    throw new InvalidOperationException("Orphan or duplicate accepted tool result in continuation history.");
            var pending = summary.PendingConfirmation.Call;
            var original = calls.SingleOrDefault(call => string.Equals(call.Id, pending.Id, StringComparison.OrdinalIgnoreCase));
            if (original == null || original.Name != pending.Name || original.ArgumentsJson != pending.ArgumentsJson ||
                completed.Contains(pending.Id) || calls.Any(call => call.Id != pending.Id && !completed.Contains(call.Id)))
                throw new InvalidOperationException("Pending call differs from accepted history or is already resolved.");
            return new AgentRunContinuation(summary, limits, revision, messages, ids);
        }
    }

    public sealed class AgentRunResult
    {
        public RunSummary Summary { get; private set; }
        public IReadOnlyList<AgentMessage> AcceptedMessages { get; private set; }
        public AgentRunContinuation Continuation { get; private set; }

        internal AgentRunResult(RunSummary summary, AgentRunLimits limits, long revision,
            IEnumerable<AgentMessage> messages, IEnumerable<string> ids)
        {
            Summary = summary;
            AcceptedMessages = Array.AsReadOnly(messages.ToArray());
            if (summary.Lifecycle == RunLifecycle.AwaitingConfirmation)
                Continuation = new AgentRunContinuation(summary, limits, revision, AcceptedMessages, ids);
        }
    }
}
