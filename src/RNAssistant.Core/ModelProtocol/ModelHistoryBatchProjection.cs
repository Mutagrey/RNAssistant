using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.ModelProtocol
{
    // Final wire projection only. Durable per-call facts retain exact runtime IDs.
    internal static class ModelHistoryBatchProjection
    {
        internal static IReadOnlyList<ChatMessage> Project(IReadOnlyList<ChatMessage> messages)
        {
            var calls = messages.Where(m => m.ProtocolMessage && !m.ExcludeFromModelContext && m.AcceptedCallOrigin != null).ToArray();
            var results = messages.Where(m => m.ProtocolMessage && !m.ExcludeFromModelContext && m.Activity == null &&
                m.AcceptedCallOrigin == null && m.CompletedOperation == null && m.ToolResultProtocolVersion == ToolResultWire.CurrentVersion)
                .GroupBy(m => m.ToolCallId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
            var replacements = new Dictionary<ChatMessage, List<ChatMessage>>();
            var consumed = new HashSet<ChatMessage>();
            foreach (var batch in calls.GroupBy(m => m.AcceptedCallOrigin.ModelAttemptId, StringComparer.Ordinal))
            {
                var ordered = batch.OrderBy(m => m.AcceptedCallOrigin.CallIndex).ToArray();
                var count = ordered[0].AcceptedCallOrigin.CallCount;
                if (!count.HasValue || ordered.Any(m => m.AcceptedCallOrigin.CallCount != count) ||
                    ordered.Select(m => m.AcceptedCallOrigin.CallIndex).Distinct().Count() != ordered.Length)
                    throw new InvalidOperationException("Accepted batch origin is inconsistent.");
                var template = JObject.Parse(ordered[0].Content);
                foreach (var call in ordered)
                {
                    var proposal = JObject.Parse(call.Content);
                    if (call.RunId != ordered[0].RunId || call.AcceptedCallOrigin.StepId != ordered[0].AcceptedCallOrigin.StepId ||
                        !JToken.DeepEquals(proposal["reason"], template["reason"]) ||
                        !JToken.DeepEquals(proposal["message"], template["message"]) &&
                        !string.IsNullOrEmpty((string)proposal["message"]))
                        throw new InvalidOperationException("Accepted batch decisions disagree.");
                }
                var complete = ordered.Length == count && ordered.Select((m, i) => m.AcceptedCallOrigin.CallIndex == i).All(v => v);
                var bodies = new JArray();
                var returned = new List<ChatMessage>();
                var next = new List<ChatMessage>();
                foreach (var call in ordered)
                {
                    ChatMessage[] matches;
                    if (!results.TryGetValue(call.ToolCallId, out matches) || matches.Length != 1)
                        throw new InvalidOperationException("Accepted call requires exactly one terminal result in this request.");
                    var result = matches[0];
                    var body = JObject.Parse(result.Content);
                    body.Remove("type");
                    body["call_index"] = call.AcceptedCallOrigin.CallIndex;
                    if ((string)body["name"] != call.ToolName)
                        throw new InvalidOperationException("Result name disagrees with the accepted call.");
                    bodies.Add(body);
                    returned.Add(result);
                    consumed.Add(call); consumed.Add(result);
                    if (!complete)
                    {
                        // Older siblings may have been compacted/evicted. Do not
                        // manufacture a smaller assistant decision with new indices.
                        var original = JObject.Parse(call.Content);
                        var receipt = Copy(result);
                        receipt.Content = new JObject {
                            ["type"] = "completed_operation", ["reason"] = original["reason"],
                            ["call"] = original["tool_calls"][0], ["result"] = body,
                            ["batch_compacted"] = true }.ToString(Formatting.None);
                        receipt.ProjectedSourceMessageIds = new List<string> { call.Id, result.Id };
                        next.Add(receipt);
                    }
                }
                if (complete)
                {
                    var root = JObject.Parse(ordered[0].Content);
                    root["tool_calls"] = new JArray(ordered.Select(m => JObject.Parse(m.Content)["tool_calls"][0]));
                    var decision = Copy(ordered[0]);
                    decision.Content = root.ToString(Formatting.None);
                    decision.ProjectedSourceMessageIds = ordered.Select(m => m.Id).ToList();
                    var output = Copy(returned[0]);
                    output.Role = "user";
                    output.Content = new JObject { ["type"] = "tool_results", ["results"] = bodies }.ToString(Formatting.None);
                    output.ProjectedSourceMessageIds = returned.Select(m => m.Id).ToList();
                    output.ProjectedEvidenceReferences = returned.Where(m => m.EvidenceReference != null)
                        .Select(m => m.EvidenceReference.Alias).Distinct(StringComparer.Ordinal).ToList();
                    next.Add(decision); next.Add(output);
                }
                replacements[batch.First()] = next;
            }
            if (results.Values.SelectMany(values => values).Any(result => !consumed.Contains(result)))
                throw new InvalidOperationException("A tool result has no retained accepted call or completed-operation receipt.");
            var projected = new List<ChatMessage>();
            foreach (var message in messages)
            {
                List<ChatMessage> replacement;
                if (replacements.TryGetValue(message, out replacement)) projected.AddRange(replacement);
                else if (!consumed.Contains(message)) projected.Add(message);
            }
            return projected;
        }

        private static ChatMessage Copy(ChatMessage source)
        {
            var copy = JsonConvert.DeserializeObject<ChatMessage>(JsonConvert.SerializeObject(source));
            copy.ToolCallId = null; copy.ToolName = null; copy.ToolCallReference = null;
            copy.ToolResultRole = null; copy.ToolResultProtocolVersion = 0; copy.AcceptedCallOrigin = null;
            if (source.EvidenceReference != null)
                copy.ProjectedEvidenceReferences = new List<string> { source.EvidenceReference.Alias };
            return copy;
        }
    }
}
