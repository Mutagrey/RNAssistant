using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Llm
{
    public static class HistoricalContextProjector
    {
        public static ChatMessage Project(ChatMessage source)
        {
            if (source == null) return null;
            return new ChatMessage
            {
                Id = source.Id,
                Role = source.Role,
                // Exact ResourceRefs remain runtime metadata and are never copied
                // into model-facing text. Models address resources only by the
                // semantic targets published in runtime context or resources_find.
                Content = source.Content,
                ExcludeFromModelContext = source.ExcludeFromModelContext,
                ProtocolMessage = source.ProtocolMessage,
                ResponseProtocolVersion = source.ResponseProtocolVersion,
                ToolResultProtocolVersion = source.ToolResultProtocolVersion,
                ToolCallId = source.ToolCallId,
                AcceptedCallOrigin = source.AcceptedCallOrigin,
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
                ResourceEvidence = (source.ResourceEvidence ?? new List<ResourceEvidence>()).ToList(),
                ContextClaims = (source.ContextClaims ?? new List<StructuredContextClaim>()).ToList(),
                ArgumentPayload = source.ArgumentPayload,
                AcceptedCallPayload = source.AcceptedCallPayload,
                ResultPayload = source.ResultPayload,
                ResourceEffect = source.ResourceEffect,
                AuthorityCommitId = source.AuthorityCommitId,
                HtmlWorkspaceCheckpoint = CloneReference(source.HtmlWorkspaceCheckpoint),
                RunId = source.RunId,
                Sequence = source.Sequence,
                CreatedUtc = source.CreatedUtc
            };
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

    }
}
