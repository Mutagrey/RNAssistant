using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.ModelProtocol;

namespace RNAssistant.Core.Agent
{
    public interface IRunInputChannel
    {
        System.Threading.CancellationToken InterruptToken { get; }
        bool HasPendingInput { get; }
        System.Threading.Tasks.Task<IReadOnlyList<AgentMessage>> TakeInputAsync(System.Threading.CancellationToken token);
        bool TryCloseInput();
    }

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
        public string SourceModelAttemptId { get; private set; }
        public ToolExecutionContext ToolContext { get; private set; }
        public ToolExecutionRecord Execution { get; private set; }
        public int ConsecutiveReflections { get; private set; }
        public bool ReflectionFeedbackIssued { get; private set; }

        internal AgentRunEvent(AgentRunEventKind kind, RunSummary summary, string stepId = null,
            AgentRunLimits limits = null, AgentMessage userMessage = null, AgentResponse response = null,
            ToolExecutionContext toolContext = null, ToolExecutionRecord execution = null,
            string sourceModelAttemptId = null)
        {
            Kind = kind;
            Summary = summary;
            Limits = limits;
            StepId = stepId;
            UserMessage = userMessage;
            Response = response;
            SourceModelAttemptId = sourceModelAttemptId;
            ToolContext = toolContext;
            Execution = execution;
        }

        internal void SetReflectionState(int consecutive, bool feedbackIssued)
        {
            if (consecutive < 0 || consecutive > 2) throw new ArgumentOutOfRangeException(nameof(consecutive));
            ConsecutiveReflections = consecutive;
            ReflectionFeedbackIssued = feedbackIssued;
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
        public int ConsecutiveReflections { get; private set; }
        public bool ReflectionFeedbackIssued { get; private set; }

        internal AgentRunContinuation(RunSummary summary, AgentRunLimits limits, long revision,
            IEnumerable<AgentMessage> messages, IEnumerable<string> ids,
            int consecutiveReflections = 0, bool reflectionFeedbackIssued = false)
        {
            Summary = summary;
            Limits = limits;
            Revision = revision;
            AcceptedMessages = Array.AsReadOnly(messages.ToArray());
            AcceptedCallIds = Array.AsReadOnly(ids.OrderBy(id => id, StringComparer.Ordinal).ToArray());
            ConsecutiveReflections = consecutiveReflections;
            ReflectionFeedbackIssued = reflectionFeedbackIssued;
        }

        public static AgentRunContinuation Restore(AgentRunState checkpoint, long revision,
            IEnumerable<AgentMessage> acceptedMessages)
        {
            var summary = checkpoint?.Summary;
            var limits = checkpoint?.Limits;
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
            return new AgentRunContinuation(summary, limits, revision, messages, ids,
                checkpoint.ConsecutiveReflections, checkpoint.ReflectionFeedbackIssued);
        }

        // The application must own the exact session lease. A Running checkpoint
        // is eligible only after that lease proves its previous owner has exited.
        public static AgentRunContinuation RestoreCheckpoint(AgentRunState checkpoint, long revision,
            IEnumerable<AgentMessage> acceptedMessages)
        {
            var summary = checkpoint?.Summary;
            var limits = checkpoint?.Limits;
            var stopped = summary != null && (summary.Lifecycle == RunLifecycle.Running ||
                summary.Lifecycle == RunLifecycle.Cancelled && (summary.Reason == "cancelled" || summary.Reason == "Cancelled" ||
                    summary.Reason == "execution_interrupted") ||
                summary.Lifecycle == RunLifecycle.Failed && summary.Reason == "runtime_interrupted");
            if (!stopped || checkpoint.InFlightTool != null || summary.PendingConfirmation != null ||
                summary.ExecutionHealth == ExecutionHealth.Unknown || revision < 0 ||
                summary.IterationsUsed >= limits.MaxIterations || summary.ToolStepsUsed > limits.MaxToolSteps)
                throw new InvalidOperationException("Continuation requires a stopped checkpoint, remaining iterations and no pending or uncertain effect.");
            var messages = (acceptedMessages ?? new AgentMessage[0]).ToArray();
            if (messages.Length == 0 || messages.Any(message => message == null))
                throw new InvalidOperationException("Complete accepted history is required for continuation.");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var open = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var message in messages)
            {
                if (message.Kind == AgentMessageKind.ToolResult)
                {
                    if (!open.Remove(message.ToolCallId))
                        throw new InvalidOperationException("Continuation contains an orphan or duplicate tool result.");
                }
                else
                {
                    if (open.Count != 0) throw new InvalidOperationException("Continuation contains an incomplete tool exchange.");
                    if (message.Kind == AgentMessageKind.Assistant &&
                        !ConversationResponse.IsValidAction(message.Action, message.ToolCalls.Count))
                        throw new InvalidOperationException("Continuation contains an unsupported model action.");
                    foreach (var call in message.ToolCalls)
                    {
                        if (!ids.Add(call.Id)) throw new InvalidOperationException("Duplicate accepted call in continuation.");
                        open.Add(call.Id);
                    }
                }
            }
            if (open.Count != 0) throw new InvalidOperationException("An accepted tool call is unresolved; it cannot be replayed.");
            var last = messages.Last();
            if (last.Kind == AgentMessageKind.Assistant && last.Action != ConversationResponse.ReflectionAction)
                throw new InvalidOperationException("The accepted model decision cannot be continued.");
            return new AgentRunContinuation(summary, limits, revision, messages, ids,
                checkpoint.ConsecutiveReflections, checkpoint.ReflectionFeedbackIssued);
        }
    }

    public sealed class AgentRunResult
    {
        public RunSummary Summary { get; private set; }
        public IReadOnlyList<AgentMessage> AcceptedMessages { get; private set; }
        public AgentRunContinuation Continuation { get; private set; }

        internal AgentRunResult(RunSummary summary, AgentRunLimits limits, long revision,
            IEnumerable<AgentMessage> messages, IEnumerable<string> ids,
            int consecutiveReflections = 0, bool reflectionFeedbackIssued = false)
        {
            Summary = summary;
            AcceptedMessages = Array.AsReadOnly(messages.ToArray());
            if (summary.Lifecycle == RunLifecycle.AwaitingConfirmation)
                Continuation = new AgentRunContinuation(summary, limits, revision, AcceptedMessages, ids,
                    consecutiveReflections, reflectionFeedbackIssued);
        }
    }
}
