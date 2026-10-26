using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;

namespace RNAssistant.Core.Storage
{
    public sealed partial class ChatStore
    {
        private static void ApplyExecutionReference(JObject root, JToken message)
        {
            var reference = message?["ExecutionReference"]?.ToObject<ModelReference>();
            if (reference == null) return;
            if (reference.Kind != ModelReferenceKind.Execution ||
                reference.Ordinal <= ((long?)root["ExecutionReferenceSequence"] ?? 0))
                throw new JsonException("Duplicate or out-of-order execution reference.");
            root["ExecutionReferenceSequence"] = reference.Ordinal;
        }

        private static void ApplyResourceReferences(JObject root, JToken message)
        {
            if (message?.Type != JTokenType.Object) return;
            var additions = message?["ResourceModelReferences"]?.ToObject<List<ModelResourceReference>>();
            if (additions == null || additions.Count == 0) return;
            var index = root["ResourceReferenceIndex"]?.ToObject<List<ModelResourceReference>>() ?? new List<ModelResourceReference>();
            foreach (var reference in additions)
            {
                var existing = index.FirstOrDefault(item => item.ExactKey == reference.ExactKey);
                if (existing != null)
                {
                    if (existing.Alias != reference.Alias) throw new JsonException("Resource reference was renamed.");
                    continue;
                }
                var logical = index.Where(item => item.LogicalKey == reference.LogicalKey).ToArray();
                if (logical.Length == 0)
                {
                    if (reference.Reference.Ordinal != index.Select(item => item.Reference.Ordinal).DefaultIfEmpty(0).Max() + 1 || reference.Version != 1)
                        throw new JsonException("Invalid new resource reference ordinal.");
                }
                else if (logical[0].Reference.Alias != reference.Reference.Alias || reference.Version != logical.Max(item => item.Version) + 1)
                    throw new JsonException("Invalid resource revision reference.");
                index.Add(reference);
            }
            root["ResourceReferenceIndex"] = JArray.FromObject(index);
        }

        // One allocator for message observations and saved request observations.
        // The index is reconstructed from their existing append-only events.
        private sealed class ResourceReferenceAllocator
        {
            internal readonly List<ModelResourceReference> Index;
            private readonly Dictionary<string, ModelResourceReference> _exact;
            private readonly Dictionary<string, ModelResourceReference> _logical;
            private long _ordinal;
            internal ResourceReferenceAllocator(IEnumerable<ModelResourceReference> index)
            {
                Index = index.ToList();
                _exact = Index.ToDictionary(item => item.ExactKey, StringComparer.Ordinal);
                _logical = Index.GroupBy(item => item.LogicalKey).ToDictionary(group => group.Key,
                    group => group.OrderByDescending(item => item.Version).First(), StringComparer.Ordinal);
                _ordinal = Index.Select(item => item.Reference.Ordinal).DefaultIfEmpty(0).Max();
            }
            internal ModelResourceReference Observe(ResourceAuthorityScopeId scope, ResourceRef resource)
            {
                var candidate = new ModelResourceReference(scope, resource,
                    ModelReference.Create(ModelReferenceKind.Resource, 1), 1);
                ModelResourceReference reference;
                if (_exact.TryGetValue(candidate.ExactKey, out reference)) return reference;
                ModelResourceReference previous;
                var known = _logical.TryGetValue(candidate.LogicalKey, out previous);
                reference = new ModelResourceReference(scope, resource,
                    known ? previous.Reference : ModelReference.Create(ModelReferenceKind.Resource, checked(++_ordinal)),
                    known ? checked(previous.Version + 1) : 1);
                Index.Add(reference); _exact.Add(reference.ExactKey, reference); _logical[candidate.LogicalKey] = reference;
                return reference;
            }
            internal ModelResourceReference Retain(ModelResourceReference reference)
            {
                ModelResourceReference canonical;
                if (!_exact.TryGetValue(reference.ExactKey, out canonical) || canonical.Alias != reference.Alias)
                    throw new InvalidOperationException("Resource reference is immutable and must be allocated from an observation.");
                return canonical;
            }
        }

        private static ResourceReferenceAllocator AllocateTraceResourceReferences(ChatSession session, JToken data)
        {
            if (session.FormatVersion != ChatSession.CurrentFormatVersion || data?.Type != JTokenType.Object) return null;
            var allocator = new ResourceReferenceAllocator(session.ResourceReferenceIndex);
            var links = data.ToObject<RNAssistant.Core.Llm.LlmTraceRecord>().ContextMessages;
            var bindings = (links ?? new List<ContextMessagePresentation>()).Where(item => item != null)
                .SelectMany(item => item.EvidenceLinks ?? new List<ContextEvidenceLink>())
                .Where(link => link?.ScopeId != null && link.Resource?.IsExact == true)
                .Select(link => allocator.Observe(link.ScopeId, link.Resource))
                .GroupBy(reference => reference.ExactKey).Select(group => group.First()).ToList();
            // Never accept caller-supplied names; only canonical scope/resource identity.
            data["ResourceModelReferences"] = JArray.FromObject(bindings);
            return allocator;
        }

        private static void AllocateResourceReferences(ChatSession session, ChatSession stored, ConversationCommit commit)
        {
            if (session.FormatVersion != ChatSession.CurrentFormatVersion) return;
            var allocator = new ResourceReferenceAllocator(stored?.ResourceReferenceIndex ?? session.ResourceReferenceIndex);
            foreach (var write in commit.Messages)
            {
                var assigned = (write.Message.ResourceEvidence ?? new List<ResourceEvidence>())
                    .Select(evidence => allocator.Observe(evidence.ScopeId, evidence.Resource)).ToList();
                // Metadata-only updates retain immutable bindings from earlier observations.
                assigned.AddRange(write.Message.ResourceModelReferences.Select(allocator.Retain));
                write.Message.ResourceModelReferences = assigned.GroupBy(item => item.ExactKey).Select(group => group.First()).ToList();
            }
            session.ResourceReferenceIndex = allocator.Index;
        }

        // Called under the existing document lock, after the revision check and
        // before serializing the single accepted-batch commit. No side journal.
        private static void AllocateExecutionReferences(ChatSession session, ChatSession stored, ConversationCommit commit)
        {
            if (session.FormatVersion != ChatSession.CurrentFormatVersion) return;
            var accepted = (stored?.Messages ?? new List<ChatMessage>())
                .Where(message => message.AcceptedCallOrigin != null)
                .ToDictionary(message => message.ToolCallId, StringComparer.Ordinal);
            var highWater = stored?.ExecutionReferenceSequence ?? session.ExecutionReferenceSequence;
            foreach (var write in commit.Messages.Where(write => write.Operation == SessionOperationTypes.ToolCallRecorded))
            {
                var message = write.Message;
                if (message.AcceptedCallOrigin == null || string.IsNullOrEmpty(message.ToolCallId) || accepted.ContainsKey(message.ToolCallId))
                    throw new InvalidOperationException("An accepted operation needs a new canonical call and origin.");
                var reference = ModelReference.Create(ModelReferenceKind.Execution, checked(++highWater));
                if (message.ExecutionReference != null && message.ExecutionReference.Alias != reference.Alias)
                    throw new InvalidOperationException("Accepted execution reference cannot be replaced.");
                message.ExecutionReference = reference;
                accepted.Add(message.ToolCallId, message);
            }
            foreach (var write in commit.Messages)
            {
                var message = write.Message;
                ChatMessage call;
                var callId = message.ToolCallId ?? message.Activity?.ToolCallId;
                if (callId == null || !accepted.TryGetValue(callId, out call)) continue;
                if (call.ExecutionReference == null || call.ExecutionReference.Kind != ModelReferenceKind.Execution)
                    throw new InvalidOperationException("Accepted operation has no execution reference. Start a new chat.");
                if (message.ExecutionReference != null && message.ExecutionReference.Alias != call.ExecutionReference.Alias)
                    throw new InvalidOperationException("Execution reference is immutable.");
                message.ExecutionReference = call.ExecutionReference;
            }
            // This is a projection of accepted records, not a separately written counter.
            session.ExecutionReferenceSequence = highWater;
        }
    }
}
