using RNAssistant.Core.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.ModelProtocol
{
    // One instance per conversation run. Only endpoint-format compatibility is
    // retained between steps; rejected responses and repair prompts are never retained.
    public interface IMaterializedModelProtocol
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
        public IReadOnlyList<ToolCatalogEntry> CallableTools { get; set; }
        public IReadOnlyList<ToolCatalogEntry> RunnableCatalog { get; set; }
        // Required before raw dispatch, supplied by local execution authority.
        // ModelProtocol validates batching; runtime alone owns accepted call IDs.
        public ModelProtocolCallContext CallContext { get; set; }
        public LlmRequestOptions Options { get; set; }
    }

    public sealed class ModelProtocolCallContext
    {
        public IReadOnlyList<string> BatchSafeReadOnlyToolIds { get; private set; }
        public string Error { get; private set; }
        public bool IsComplete { get { return string.IsNullOrEmpty(Error); } }

        public ModelProtocolCallContext(IEnumerable<string> batchSafeIds, string error = null)
        {
            Error = !string.IsNullOrWhiteSpace(error) ? error
                : batchSafeIds == null ? "Model protocol batch-safety context is incomplete." : null;
            BatchSafeReadOnlyToolIds = batchSafeIds == null ? null : Snapshot(batchSafeIds);
        }

        private static IReadOnlyList<string> Snapshot(IEnumerable<string> values)
        {
            return Array.AsReadOnly(values.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray());
        }
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

        internal ModelProtocolFailure(ModelProtocolFailureKind kind, string message, Exception cause = null)
        {
            Kind = kind;
            Message = message;
            var provider = cause as LlmRequestException;
            ProviderKind = provider == null ? (LlmFailureKind?)null : provider.Kind;
            StatusCode = provider == null ? null : provider.StatusCode;
        }
    }

    public sealed class ModelProtocolResult
    {
        public ConversationResponse Response { get; private set; }
        // Provider-native refusal is not a model-authored conversation envelope.
        public string ProviderRefusal { get; private set; }
        // Only an accepted completion may cross the protocol boundary.
        public LlmCompletionResult Completion { get; private set; }
        // Immutable origin of this accepted draft, copied from its successful raw
        // dispatch. Later requests may reuse options but cannot change this value.
        public string SourceModelAttemptId { get; private set; }
        public ModelProtocolFailure Failure { get; private set; }
        // Existing ContextUsageEstimator projection; no raw response/repair prompt.
        public object ContextUsage { get; private set; }

        private ModelProtocolResult() { }

        internal static ModelProtocolResult Accepted(ConversationResponse response, LlmCompletionResult completion,
            object contextUsage, string sourceModelAttemptId)
        {
            if (string.IsNullOrWhiteSpace(sourceModelAttemptId))
                throw new ArgumentException("Accepted response requires its source model attempt ID.", nameof(sourceModelAttemptId));
            return new ModelProtocolResult
            {
                Response = response, Completion = completion, ContextUsage = contextUsage,
                SourceModelAttemptId = sourceModelAttemptId
            };
        }

        internal static ModelProtocolResult Refused(LlmCompletionResult completion, object contextUsage)
        {
            return new ModelProtocolResult { ProviderRefusal = completion.RefusalContent, Completion = completion, ContextUsage = contextUsage };
        }

        internal static ModelProtocolResult Failed(ModelProtocolFailure failure, object contextUsage)
        {
            return new ModelProtocolResult { Failure = failure, ContextUsage = contextUsage };
        }
    }
}
