using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Llm
{
    [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public enum LlmRequestCompositionGroup
    {
        Unattributed, InstructionsAndCatalog, Conversation, ToolActivity, Resources,
        MessagesWithAttachments, ResponseFormat, ResourceReferences
    }

    public sealed class LlmRequestCompositionItem
    {
        public int? MessageIndex { get; set; }
        public LlmRequestCompositionGroup Group { get; set; }
        public int EstimatedTokens { get; set; }
    }

    // Diagnostic estimate of whole final messages, not source-body cost or API usage.
    public sealed class LlmRequestComposition
    {
        public int Version { get; set; } = 1;
        public string Method { get; set; } = "base_text_media_v1";
        public long TotalEstimatedTokens { get; set; }
        public bool HasMedia { get; set; }
        public List<LlmRequestCompositionItem> Items { get; set; } = new List<LlmRequestCompositionItem>();
    }

    internal static class LlmRequestCompositionBuilder
    {
        internal static LlmRequestComposition Describe(LlmApiMessageBuildResult build,
            IList<ChatMessage> messages, ContextReceipt receipt, LlmRequestOptions options)
        {
            var visible = messages.Where(m => m != null && !m.ExcludeFromModelContext && !string.IsNullOrWhiteSpace(m.Role)).ToList();
            if (build.Messages.Count != visible.Count || build.MessageBaseTokenEstimates.Count != visible.Count ||
                build.MessageParts.Count != visible.Count || build.MessageBaseTokenEstimates.Any(cost => cost < 0)) return null;
            var sources = LlmContextMessageAttribution.Match(visible, receipt);
            var result = new LlmRequestComposition { HasMedia = build.HasAudio || build.HasImages };
            for (var index = 0; index < visible.Count; index++)
            {
                var source = sources[index];
                var group = Classify(source?.Kind);
                if (build.MessageParts[index].Any(part => part != null && !string.IsNullOrEmpty(part.SourceAttachmentId) &&
                    part.Presentation != ContextPresentationKind.Excluded)) group = LlmRequestCompositionGroup.MessagesWithAttachments;
                result.Items.Add(new LlmRequestCompositionItem { MessageIndex = index, Group = group,
                    EstimatedTokens = build.MessageBaseTokenEstimates[index] });
            }
            var formatTokens = ModelContextBudget.EstimateRequestOptionsTokens(options);
            if (formatTokens > 0) result.Items.Add(new LlmRequestCompositionItem {
                Group = LlmRequestCompositionGroup.ResponseFormat, EstimatedTokens = formatTokens });
            result.TotalEstimatedTokens = result.Items.Sum(item => (long)item.EstimatedTokens);
            return result;
        }

        private static LlmRequestCompositionGroup Classify(string kind)
        {
            switch (kind)
            {
                case "system-invariant": case "user-instruction": return LlmRequestCompositionGroup.InstructionsAndCatalog;
                case "dialogue": case "assistant-update": return LlmRequestCompositionGroup.Conversation;
                case "tool-interaction": case "terminal-mutation": case "archived-operation":
                case "completed-operation": case "historical-diagnostic": return LlmRequestCompositionGroup.ToolActivity;
                case "resource-evidence": case "current-source": return LlmRequestCompositionGroup.Resources;
                case "resource-change":
                case "observation-notice": case "repeated-read": case "working-set":
                case "context-unavailable": return LlmRequestCompositionGroup.ResourceReferences;
                default: return LlmRequestCompositionGroup.Unattributed;
            }
        }
    }

    // Both saved presentation and cost attribution require one exact source at
    // one final wire position. An ambiguous receipt must never claim delivery.
    internal static class LlmContextMessageAttribution
    {
        internal static ContextMessagePresentation[] Match(IList<ChatMessage> visible, ContextReceipt receipt)
        {
            var sourceCounts = visible.Where(m => !string.IsNullOrEmpty(m.Id)).GroupBy(m => m.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            var entries = (receipt?.Messages ?? new List<ContextMessagePresentation>())
                .Where(m => m != null && m.MessageIndex.HasValue).ToArray();
            var sources = entries.Where(m => !string.IsNullOrEmpty(m.SourceMessageId))
                .GroupBy(m => m.SourceMessageId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var positions = entries.GroupBy(m => m.MessageIndex.Value).ToDictionary(group => group.Key, group => group.Count());
            var result = new ContextMessagePresentation[visible.Count];
            for (var index = 0; index < visible.Count; index++)
            {
                var id = visible[index].Id;
                ContextMessagePresentation[] candidates;
                if (!string.IsNullOrEmpty(id) && sourceCounts[id] == 1 && sources.TryGetValue(id, out candidates) &&
                    candidates.Length == 1 && candidates[0].MessageIndex == index && positions[index] == 1 &&
                    candidates[0].Presentation != ContextPresentationKind.Excluded)
                    result[index] = candidates[0];
            }
            return result;
        }
    }
}
