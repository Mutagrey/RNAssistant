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
