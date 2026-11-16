using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Storage;

namespace RNAssistant.Core.Services
{
    // The existing bounded overview projection, shared by its two storage readers.
    internal static class ChatOverviewProjection
    {
        internal static bool SameScope(ChatSession left, ChatSession right)
        {
            return left != null && right != null &&
                string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(left.DocumentKey, right.DocumentKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(left.WorkspaceId, right.WorkspaceId, StringComparison.Ordinal) &&
                string.Equals(left.DocumentAuthorityId, right.DocumentAuthorityId, StringComparison.Ordinal);
        }

        internal static ResourceRequestException Unavailable()
        { return new ResourceRequestException("The requested chat is unavailable in this scope.", "chat_unavailable", false); }

        internal static ChatOverviewExcerpt Excerpt(ChatMessage message)
        {
            if (!IsVisible(message)) return null;
            string text = null;
            if (message.Role == "user" && !message.ProtocolMessage) text = message.Content;
            else if (message.Role == "assistant" && message.ProtocolMessage)
            {
                var accepted = ConversationResponseHistoryReader.Read(message);
                if (accepted.Success && accepted.Response.Action == ConversationResponse.FinishAction) text = accepted.Response.Message;
            }
            else if (message.Role == "assistant" && !message.ProtocolMessage) text = message.Content;
            return string.IsNullOrWhiteSpace(text) ? null : new ChatOverviewExcerpt { Role = message.Role, CreatedUtc = message.CreatedUtc,
                FromAgent = message.AgentCommunication?.Sender?.AgentId, Text = Clip(text, 600), Truncated = text.Length > 600 };
        }

        internal static bool IsVisible(ChatMessage message)
        {
            return !message.ExcludeFromModelContext && !message.RuntimeProjection && message.Activity == null &&
                message.ToolCallId == null && message.ToolName == null && !(message.ToolCalls?.Count > 0) &&
                (message.Role == "user" && !message.ProtocolMessage || message.Role == "assistant");
        }

        internal static void SelectExcerpts(ChatOverview overview, List<ChatOverviewExcerpt> excerpts)
        { overview.Excerpts.AddRange(excerpts.Count <= 6 ? excerpts : excerpts.Take(2).Concat(excerpts.Skip(excerpts.Count - 4))); }

        internal static void ReadSummary(ChatOverview overview, DocumentArtifactStore artifacts)
        {
            try
            {
                // ContextCheckpoints is a disposable message-derived projection, not
                // scalar chat metadata. Only the existing published resource head is read.
                var reference = artifacts.PublishedContext(overview.Source);
                if (reference == null) return;
                var artifact = artifacts.Read(ConversationMetadata.From(overview.Source), reference, includeBody: false);
                if (!artifact.ContentByteLength.HasValue || artifact.ContentByteLength > 2097152)
                { overview.SummaryStatus = "size_unavailable_or_too_large"; return; }
                artifact = artifacts.Read(ConversationMetadata.From(overview.Source), reference, includeBody: true);
                var published = JsonConvert.DeserializeObject<SharedContextDocument>(artifact.InlineText);
                if (published?.SourceChatId != overview.Source.Id || published.Version != ContextCheckpoint.CurrentPromptVersion)
                { overview.SummaryStatus = "unavailable"; return; }
                overview.SharedContextReference = reference;
                overview.SummaryUtc = artifact.CreatedUtc;
                overview.SummaryClaims.AddRange((published.Claims ?? new List<StructuredContextClaim>())
                    .Where(claim => claim?.SourceRoles?.Count > 0 && claim.SourceRoles.All(role => role == "user" || role == "assistant") &&
                        !string.IsNullOrWhiteSpace(claim.Text)).Take(4).Select(claim => Clip(claim.Text, 400)));
                overview.SummaryStatus = "published_excerpt";
            }
            catch (Exception error) when (error is IOException || error is InvalidOperationException || error is JsonException || error is ArgumentException)
            { overview.SummaryStatus = "unavailable"; }
        }

        private static string Clip(string text, int length)
        { return text.Length <= length ? text : text.Substring(0, char.IsHighSurrogate(text[length - 1]) ? length - 1 : length) + "…"; }

    }
}
