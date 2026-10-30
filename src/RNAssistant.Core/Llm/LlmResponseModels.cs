using System.Collections.Generic;
using Newtonsoft.Json;

namespace RNAssistant.Core.Llm
{
    public sealed class LlmToolCall
    {
        public string Id { get; set; }
        public string Type { get; set; }
        public string Name { get; set; }
        public string ArgumentsJson { get; set; }
    }

    public enum LlmCompletionTermination { Unspecified, Completed, OutputLimit, Filtered, Other }

    public sealed class LlmCompletionResult
    {
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public List<LlmToolCall> NativeToolCalls { get; set; }
        // Presence only, even when diagnostic native-call assembly is disabled.
        // This never makes provider-native generation an accepted v8 proposal.
        public bool NativeToolCallsObserved { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool IsEventStream { get; set; }
        public LlmCompletionTermination Termination { get; set; }
        public string Content { get; set; }
        public string RefusalContent { get; set; }
        public string ReasoningContent { get; set; }
        public int? ReasoningTokens { get; set; }
        public bool ReasoningTruncated { get; set; }
        public int? PromptTokens { get; set; }
        public int? CompletionTokens { get; set; }
        public int? TotalTokens { get; set; }
        public string UsageJson { get; set; }
        public int? BaseEstimatedPromptTokens { get; set; }
        public int? EstimatedPromptTokens { get; set; }
        public bool TokenEstimateCalibrationEligible { get; set; }
    }

    public sealed class LlmStreamUpdate
    {
        public string ContentDelta { get; set; }
        public string ReasoningDelta { get; set; }
        public bool Completed { get; set; }
    }
}
