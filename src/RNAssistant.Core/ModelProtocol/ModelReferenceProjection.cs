using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.ModelProtocol
{
    // Wire-only history DTO. Canonical envelopes continue through the strict raw
    // history reader unchanged. Only owned correlation fields are rewritten.
    public static class ModelReferenceProjection
    {
        public static IReadOnlyList<ChatMessage> Project(IReadOnlyList<ChatMessage> messages)
        {
            var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
            var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var message in messages)
            {
                if (!message.ProtocolMessage || message.ExcludeFromModelContext || message.ExecutionReference == null) continue;
                var reference = message.ExecutionReference;
                string previous;
                if (reference.Kind != ModelReferenceKind.Execution || string.IsNullOrEmpty(message.ToolCallId) ||
                    bindings.TryGetValue(message.ToolCallId, out previous) && previous != reference.Alias ||
                    aliases.TryGetValue(reference.Alias, out previous) && previous != message.ToolCallId)
                    throw new InvalidOperationException("Ambiguous execution reference in model history.");
                bindings[message.ToolCallId] = reference.Alias;
                aliases[reference.Alias] = message.ToolCallId;
            }
            return messages.Select(source => ProjectResources(ProjectMessage(source, bindings))).ToArray();
        }

        private static ChatMessage ProjectResources(ChatMessage source)
        {
            if (!source.ProtocolMessage || source.ExcludeFromModelContext || source.ResourceModelReferences.Count == 0 ||
                source.Activity != null || source.AcceptedCallOrigin != null) return source;
            var content = source.Content ?? string.Empty;
            var view = source.SyntheticResourceObservation && content.StartsWith("CURRENT_RESOURCE_VIEW (", StringComparison.Ordinal);
            var receipt = source.CompletedOperation != null;
            var result = source.ToolResultProtocolVersion == ToolResultWire.CurrentVersion;
            if (!view && !receipt && !result) return source;
            var start = view || receipt ? content.IndexOf('\n') + 1 :
                content.StartsWith("TOOL_RESULT:\n", StringComparison.Ordinal) ? "TOOL_RESULT:\n".Length : 0;
            var end = view ? content.IndexOf('\n', start) : content.Length;
            if (end < start) throw new InvalidOperationException("Current resource view lacks its structured header.");
            var root = JObject.Parse(content.Substring(start, end - start));
            root["resource_refs"] = new JArray(source.ResourceModelReferences.Select(item => item.Alias).Distinct(StringComparer.Ordinal));
            var projected = JsonConvert.DeserializeObject<ChatMessage>(JsonConvert.SerializeObject(source));
            projected.Content = content.Substring(0, start) + root.ToString(Formatting.None) + content.Substring(end);
            return projected;
        }

        public static IReadOnlyList<string> Delivered(IReadOnlyList<ChatMessage> messages)
        {
            return Array.AsReadOnly(messages.Where(message => message.ProtocolMessage && !message.ExcludeFromModelContext &&
                message.ExecutionReference != null && (message.CompletedOperation != null ||
                message.AcceptedCallOrigin != null || message.ToolResultProtocolVersion != 0))
                .Select(message => message.ExecutionReference.Alias).Distinct(StringComparer.Ordinal).ToArray());
        }

        private static ChatMessage ProjectMessage(ChatMessage source, IDictionary<string, string> bindings)
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
                if (source.ToolCalls?.Count > 0)
                {
                    // Preserve native transport pairing; expose the same semantic alias
                    // in narration and the result body, without a second operation.
                    projected.Content = "EXECUTION_REFERENCE: " + alias + "\n" + (source.Content ?? string.Empty);
                }
                else
                {
                    var body = JObject.Parse(source.Content);
                    ((JObject)body["tool_calls"][0]).AddFirst(new JProperty("id", alias));
                    projected.Content = body.ToString(Formatting.None);
                }
                return projected;
            }
            var content = source.Content ?? string.Empty;
            var start = source.CompletedOperation != null ? content.IndexOf('\n') + 1 :
                content.StartsWith("TOOL_RESULT:\n", StringComparison.Ordinal) ? "TOOL_RESULT:\n".Length : 0;
            var root = JObject.Parse(content.Substring(start));
            if ((string)root["tool_call_id"] != source.ToolCallId)
                throw new InvalidOperationException("Model result correlation disagrees with canonical metadata.");
            root["tool_call_id"] = alias;
            var retained = root["retained_result"] as JObject;
            if (retained != null)
            {
                string retainedAlias;
                if (!bindings.TryGetValue((string)retained["tool_call_id"] ?? "", out retainedAlias))
                    throw new InvalidOperationException("Retained result reference is absent from this request.");
                retained["tool_call_id"] = retainedAlias;
            }
            projected.Content = content.Substring(0, start) + root.ToString(Formatting.None);
            return projected;
        }
    }
}
