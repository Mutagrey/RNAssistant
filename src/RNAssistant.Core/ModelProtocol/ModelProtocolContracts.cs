using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.ModelProtocol
{
    // One instance per conversation run. Only endpoint-format compatibility is
    // retained between steps; rejected responses and repair prompts are never retained.
    public interface IModelProtocol
    {
        Task<ModelProtocolResult> GetResponseAsync(
            ModelProtocolRequest request,
            ModelProtocolProgress progress,
            CancellationToken cancellationToken);
    }

    public sealed class ModelProtocolRequest
    {
        public AppSettings Settings { get; set; }
        public IReadOnlyList<ChatMessage> AcceptedMessages { get; set; }
        public IReadOnlyList<ToolDefinition> CallableTools { get; set; }
        public IReadOnlyList<ToolDefinition> RunnableCatalog { get; set; }
        public LlmRequestOptions Options { get; set; }
    }

    // Presentation controls carry no repair instruction or attempt count. StreamUpdate
    // preserves the existing provisional preview; it is never accepted history.
    public sealed class ModelProtocolProgress
    {
        public Action<bool> AttemptStarted { get; set; }
        public Action<LlmStreamUpdate> StreamUpdate { get; set; }
        public Action AttemptCompleted { get; set; }
        public Action JsonObjectFallback { get; set; }
        public Action OptionalTraceFailed { get; set; }
    }

    public enum ModelProtocolFailureKind
    {
        ProtocolExhausted,
        PromptBudgetExceeded,
        Provider,
        Cancelled,
        Infrastructure
    }

    public sealed class ModelProtocolFailure
    {
        public ModelProtocolFailureKind Kind { get; private set; }
        public string Message { get; private set; }
        public LlmFailureKind? ProviderKind { get; private set; }
        public int? StatusCode { get; private set; }

        // Temporary adapter for the existing controller's exception/cancellation path.
        // Not a durable payload; AgentKernel replaces this consumer in Phase 3.
        [JsonIgnore]
        public Exception Cause { get; private set; }

        internal ModelProtocolFailure(ModelProtocolFailureKind kind, string message, Exception cause = null)
        {
            Kind = kind;
            Message = message;
            Cause = cause;
            var provider = cause as LlmRequestException;
            ProviderKind = provider == null ? (LlmFailureKind?)null : provider.Kind;
            StatusCode = provider == null ? null : provider.StatusCode;
        }
    }

    public sealed class ModelProtocolResult
    {
        public AgentResponse Response { get; private set; }
        // Only an accepted completion may cross the protocol boundary.
        public LlmCompletionResult Completion { get; private set; }
        public ModelProtocolFailure Failure { get; private set; }
        // Existing ContextUsageEstimator projection; no raw response/repair prompt.
        public object ContextUsage { get; private set; }

        private ModelProtocolResult() { }

        internal static ModelProtocolResult Accepted(AgentResponse response, LlmCompletionResult completion, object contextUsage)
        {
            return new ModelProtocolResult { Response = response, Completion = completion, ContextUsage = contextUsage };
        }

        internal static ModelProtocolResult Failed(ModelProtocolFailure failure, object contextUsage)
        {
            return new ModelProtocolResult { Failure = failure, ContextUsage = contextUsage };
        }
    }
}
