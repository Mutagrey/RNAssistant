using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.ModelProtocol
{
    // Wire-only history DTO. Canonical envelopes continue through the strict raw
    // history reader unchanged. JSON calls retain their original shape; final
    // batches expose only call_index and delivered evidence references.
    public static class ModelReferenceProjection
    {
        public static IReadOnlyList<ChatMessage> Project(IReadOnlyList<ChatMessage> messages)
        {
            var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
            var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var message in messages)
            {
                if (!message.ProtocolMessage || message.ExcludeFromModelContext || message.ToolCallReference == null) continue;
                var reference = message.ToolCallReference;
                string previous;
                if (reference.Kind != ModelReferenceKind.ToolCall || string.IsNullOrEmpty(message.ToolCallId) ||
                    bindings.TryGetValue(message.ToolCallId, out previous) && previous != reference.Alias ||
                    aliases.TryGetValue(reference.Alias, out previous) && previous != message.ToolCallId)
                    throw new InvalidOperationException("Ambiguous execution reference in model history.");
                bindings[message.ToolCallId] = reference.Alias;
                aliases[reference.Alias] = message.ToolCallId;
            }
            var evidence = messages.Where(m => m.ProtocolMessage && !m.ExcludeFromModelContext && m.Activity == null &&
                    m.AcceptedCallOrigin == null && !string.IsNullOrEmpty(m.ToolCallId) && m.EvidenceReference != null)
                .GroupBy(m => m.ToolCallId, StringComparer.Ordinal).ToDictionary(group => group.Key,
                    group => group.Select(m => m.EvidenceReference.Alias).Distinct(StringComparer.Ordinal).Single(), StringComparer.Ordinal);
            return ModelHistoryBatchProjection.Project(messages.Select(source => ProjectResources(ProjectMessage(source, bindings, evidence))).ToArray());
        }

        private static ChatMessage ProjectResources(ChatMessage source)
        {
            if (!source.ProtocolMessage || source.ExcludeFromModelContext || source.ResourceModelReferences.Count == 0 ||
                source.Activity != null || source.AcceptedCallOrigin != null) return source;
            var content = source.Content ?? string.Empty;
            var view = source.SyntheticResourceObservation;
            // Receipts describe an outcome, not delivery of the observed source.
            // Discovery/search have no durable per-item source binding. Never zip
            // their items with references or infer identity from semantic targets.
            var read = source.CompletedOperation == null && source.ToolName == "core.read" &&
                source.ToolResultProtocolVersion == ToolResultWire.CurrentVersion;
            if (!view && !read) return source;
            var root = JObject.Parse(content);
            if (view && (string)root["type"] != "resource_observation") return source;
            var block = view ? root : root["data"] as JObject;
            if (block == null || block["target"]?.Type != JTokenType.String) return source;
            if (view ? (bool?)block["bodyIncluded"] != true :
                (string)root["status"] != "ok" || (string)block["kind"] != "resource-read" ||
                !new[] { "text", "table", "tree" }.Any(field => block[field] != null && block[field].Type != JTokenType.Null))
                return source;
            var alias = SourceAlias(source, block, view ? "view" : "representation");
            if (alias == null) return source;
            block.AddFirst(new JProperty("ref", alias));
            var projected = JsonConvert.DeserializeObject<ChatMessage>(JsonConvert.SerializeObject(source));
            projected.Content = root.ToString(Formatting.None);
            return projected;
        }

        private static string SourceAlias(ChatMessage source, JObject block, string viewField)
        {
            try { return BoundSourceReference(source, (string)block[viewField], block["coverage"]?.ToObject<ResourceCoverage>())?.Alias; }
            catch (JsonException) { return null; }
            catch (ArgumentException) { return null; }
        }

        // Shared by wire and UI projections; neither consumer allocates references.
        public static ModelResourceReference BoundSourceReference(ChatMessage source, string view, ResourceCoverage coverage)
        {
            // Both admitted shapes describe one source. Bind via typed evidence,
            // including its scope/revision, and verify the actual displayed slice.
            // Unbound or ambiguous metadata cannot assert source delivery.
            if (source == null || coverage == null || source.ResourceEvidence.Count != 1) return null;
            var evidence = source.ResourceEvidence[0];
            if (view != evidence.View ||
                !JToken.DeepEquals(JObject.FromObject(coverage), JObject.FromObject(evidence.Coverage))) return null;
            var bindings = source.ResourceModelReferences.Where(item => item.Matches(evidence)).ToArray();
            return bindings.Length == 1 ? bindings[0] : null;
        }

        public static IReadOnlyList<string> Delivered(IReadOnlyList<ChatMessage> messages)
        {
            return Array.AsReadOnly(messages.Where(message => message.ProtocolMessage && !message.ExcludeFromModelContext && message.Activity == null)
                .SelectMany(message => message.ProjectedEvidenceReferences ??
                    (message.EvidenceReference?.Kind == ModelReferenceKind.Evidence && message.AcceptedCallOrigin == null &&
                     (message.CompletedOperation != null || message.ToolResultProtocolVersion != 0)
                        ? new List<string> { message.EvidenceReference.Alias } : new List<string>()))
                .Distinct(StringComparer.Ordinal).ToArray());
        }

        private static ChatMessage ProjectMessage(ChatMessage source, IDictionary<string, string> bindings, IReadOnlyDictionary<string, string> evidence)
        {
            if (!source.ProtocolMessage || source.ExcludeFromModelContext || source.Activity != null ||
                source.CompletedOperation == null && source.AcceptedCallOrigin == null && source.ToolResultProtocolVersion == 0)
                return source;
            string alias;
            if (source.ToolCallId == null || !bindings.TryGetValue(source.ToolCallId, out alias))
                throw new InvalidOperationException("Execution reference is missing. Start a new chat; canonical IDs are not model aliases.");
            var projected = JsonConvert.DeserializeObject<ChatMessage>(JsonConvert.SerializeObject(source));
            if (source.AcceptedCallOrigin != null)
            {
                var accepted = ConversationResponseHistoryReader.Read(source);
                if (!accepted.Success) throw new InvalidOperationException(accepted.Error);
                return projected;
            }
            var content = source.Content ?? string.Empty;
            var root = JObject.Parse(content);
            if ((string)root["tool_call_id"] != source.ToolCallId)
                throw new InvalidOperationException("Model result correlation disagrees with canonical metadata.");
            root.Remove("tool_call_id");
            root.Remove("evidence");
            if (source.EvidenceReference != null)
            {
                if (source.EvidenceReference.Kind != ModelReferenceKind.Evidence)
                    throw new InvalidOperationException("A tool result carries a mistyped evidence reference.");
                root.AddFirst(new JProperty("evidence", source.EvidenceReference.Alias));
            }
            // Only compiler-owned read receipts contain this pointer. A generic
            // tool's business data may use the same field names literally.
            var observation = source.CompletedOperation != null ? root["observation"] as JObject :
                source.ToolName == "core.read" || source.ToolName == "core.tools.read"
                    ? root["data"]?["observation"] as JObject : null;
            if (observation?["retainedAt"] != null)
            {
                string retained;
                if (observation["retainedAt"].Type == JTokenType.String && evidence.TryGetValue((string)observation["retainedAt"], out retained)) observation["retainedAt"] = retained;
                else observation.Remove("retainedAt");
            }
            projected.Content = root.ToString(Formatting.None);
            return projected;
        }
    }
}
