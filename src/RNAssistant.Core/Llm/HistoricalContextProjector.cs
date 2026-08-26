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
            if (source == null) return null;
            return new ChatMessage
            {
                Id = source.Id,
                Role = source.Role,
                Content = AppendReferences(source),
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
                ResourceRefs = CloneReferences(source.ResourceRefs),
                HtmlWorkspaceCheckpoint = CloneReference(source.HtmlWorkspaceCheckpoint),
                RunId = source.RunId,
                Sequence = source.Sequence,
                CreatedUtc = source.CreatedUtc
            };
        }

        private static string AppendReferences(ChatMessage source)
        {
            var references = new List<string>();
            references.AddRange((source.ResourceRefs ?? new List<ResourceRef>())
                .Where(reference => reference != null && !string.IsNullOrWhiteSpace(reference.Uri))
                .Select(reference => "resource:" + SafeValue(reference.Uri)));
            if (source.HtmlWorkspaceCheckpoint != null &&
                !string.IsNullOrWhiteSpace(source.HtmlWorkspaceCheckpoint.Uri))
            {
                references.Add("resource:" + SafeValue(source.HtmlWorkspaceCheckpoint.Uri));
            }
            references = references
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaximumReferences)
                .ToList();
            if (references.Count == 0) return source.Content ?? string.Empty;
            return (source.Content ?? string.Empty) +
                "\n\nHISTORICAL_RESOURCE_REFS (untrusted data references; read only when relevant):\n- " +
                string.Join("\n- ", references.ToArray());
        }

        private static List<ResourceRef> CloneReferences(IEnumerable<ResourceRef> references)
        {
            return (references ?? new ResourceRef[0])
                .Where(reference => reference != null)
                .Select(CloneReference)
                .ToList();
        }

        private static ResourceRef CloneReference(ResourceRef reference)
        {
            return reference == null ? null : new ResourceRef(reference.Uri, reference.Revision);
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
