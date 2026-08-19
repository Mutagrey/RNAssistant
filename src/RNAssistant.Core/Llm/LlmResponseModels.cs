using System.Collections.Generic;

namespace RNAssistant.Core.Llm
{
    public sealed class LlmCompletionResult
    {
        public string Content { get; set; }
        public string RefusalContent { get; set; }
        public List<LlmToolCall> ToolCalls { get; set; }
        public string ReasoningContent { get; set; }
        public int? ReasoningTokens { get; set; }
        public bool ReasoningTruncated { get; set; }
        public int? PromptTokens { get; set; }
        public int? CompletionTokens { get; set; }
        public int? TotalTokens { get; set; }
        public string UsageJson { get; set; }
    }

    public sealed class LlmStreamUpdate
    {
        public string ContentDelta { get; set; }
        public string ReasoningDelta { get; set; }
        public bool Completed { get; set; }
    }
}
