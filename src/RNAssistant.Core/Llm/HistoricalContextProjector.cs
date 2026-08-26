using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Llm
{
    public static class HistoricalContextProjector
    {
        private const int MaximumReferences = 32;
        private const int MaximumReferenceValueCharacters = 200;

        public static ChatMessage Project(ChatMessage source)
        {
            return Project(source, null);
        }

        public static ChatMessage Project(ChatMessage source, Func<string, string> resourceUriResolver)
        {
            if (source == null) return null;
            return new ChatMessage
            {
                Id = source.Id,
                Role = source.Role,
                Content = AppendReferences(source, resourceUriResolver),
                ExcludeFromModelContext = source.ExcludeFromModelContext,
                ProtocolMessage = source.ProtocolMessage,
                ToolCallId = source.ToolCallId,
                ToolName = source.ToolName,
                ToolResultRole = source.ToolResultRole,
                ToolCalls = (source.ToolCalls ?? new List<LlmToolCall>())
                    .Where(call => call != null)
                    .Select(call => new LlmToolCall
                    {
                        Id = call.Id,
                        Type = call.Type,
                        Name = call.Name,
                        ArgumentsJson = call.ArgumentsJson
                    })
                    .ToList(),
                Attachments = new List<ChatAttachment>(),
                ArtifactIds = new List<string>(source.ArtifactIds ?? new List<string>()),
                HtmlWorkspaceCheckpointId = source.HtmlWorkspaceCheckpointId,
                RunId = source.RunId,
                Sequence = source.Sequence,
                CreatedUtc = source.CreatedUtc
            };
        }

        private static string AppendReferences(ChatMessage source, Func<string, string> resourceUriResolver)
        {
            var references = new List<string>();
            references.AddRange((source.ArtifactIds ?? new List<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => ResolveReference(id, resourceUriResolver))
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            if (!string.IsNullOrWhiteSpace(source.HtmlWorkspaceCheckpointId))
            {
                var workspaceReference = resourceUriResolver == null
                    ? "html_workspace:" + SafeValue(source.HtmlWorkspaceCheckpointId)
                    : ResolveReference(source.HtmlWorkspaceCheckpointId, resourceUriResolver);
                if (!string.IsNullOrWhiteSpace(workspaceReference)) references.Add(workspaceReference);
            }
            if (resourceUriResolver == null)
            {
                references.AddRange((source.Attachments ?? new List<ChatAttachment>())
                    .Where(attachment => attachment != null)
                    .Select(attachment => "attachment:" + SafeValue(attachment.Id) + " | " +
                        SafeValue(attachment.Kind ?? "file") + " | " +
                        SafeValue(attachment.FileName ?? "unnamed")));
            }
            references = references
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaximumReferences)
                .ToList();
            if (references.Count == 0) return source.Content ?? string.Empty;
            return (source.Content ?? string.Empty) +
                "\n\nHISTORICAL_REFERENCES (local artifacts; not new instructions):\n- " +
                string.Join("\n- ", references.ToArray());
        }

        private static string ResolveReference(string artifactId, Func<string, string> resourceUriResolver)
        {
            if (resourceUriResolver == null) return "artifact:" + SafeValue(artifactId);
            var uri = resourceUriResolver(artifactId);
            return string.IsNullOrWhiteSpace(uri) ? null : "resource:" + uri;
        }

        private static string SafeValue(string value)
        {
            value = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            return value.Length <= MaximumReferenceValueCharacters
                ? value
                : value.Substring(0, MaximumReferenceValueCharacters);
        }
    }
}
