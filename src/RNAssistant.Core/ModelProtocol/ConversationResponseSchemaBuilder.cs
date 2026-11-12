using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.ModelProtocol
{
    public static class ConversationResponseSchemaBuilder
    {
        public const string SchemaName = "rnassistant_conversation_response_v8";
        public const int MaximumToolCalls = 32;

        public static string Build(IEnumerable<ToolCatalogEntry> callableTools, ModelProtocolCallContext context)
        {
            if (context == null || !context.IsComplete)
                throw new System.ArgumentException("V8 schema requires a complete local sequential-batch context.", nameof(context));
            if (callableTools == null) throw new System.ArgumentNullException(nameof(callableTools));
            var batches = new JArray();
            var singleCalls = new JArray();
            foreach (var tool in callableTools.Where(item => item != null && !string.IsNullOrWhiteSpace(item.Id))
                .GroupBy(item => item.Id, System.StringComparer.Ordinal).Select(group => group.First()))
            {
                JObject parameters;
                string error;
                if (!ToolSchemaSupport.TryParse(tool, out parameters, out error)) continue;
                var options = !context.AllowsSequentialBatch(tool) ? singleCalls : batches;
                options.Add(ClosedObject(new JObject
                {
                    ["name"] = new JObject { ["type"] = "string", ["const"] = tool.Id },
                    ["arguments"] = ToolSchemaSupport.ForStructuredOutput(parameters, keepDescriptions: true)
                }));
            }
            var branches = new JArray();
            var definitions = new JObject();
            // Each argument contract occurs once; only locally authorized calls can share a batch.
            if (batches.Count > 0 || singleCalls.Count > 0)
            {
                var arrays = new JArray();
                if (singleCalls.Count > 0) arrays.Add(CallArray(singleCalls, 1));
                if (batches.Count > 0) arrays.Add(CallArray(batches, MaximumToolCalls));
                var calls = arrays.Count == 1 ? (JObject)arrays[0] : new JObject { ["anyOf"] = arrays };
                calls["description"] = ModelProtocolWire.ExecuteGuidance;
                definitions["calls"] = calls;
                AddOptionalMessageBranches(branches, ConversationResponse.ExecuteAction, "tool_calls",
                    new JObject { ["$ref"] = "#/$defs/calls" }, context.ReflectionEnabled);
            }
            if (context.ReflectionEnabled)
                AddOptionalMessageBranches(branches, ConversationResponse.ReflectionAction, "decision",
                    new JObject(NonblankString()) { ["description"] = ModelProtocolWire.DecisionGuidance }, true);
            branches.Add(Branch(ConversationResponse.FinishAction, "results", new JObject
            {
                ["type"] = "array",
                ["items"] = ClosedObject(new JObject
                {
                    ["criterion"] = new JObject(NonblankString()) { ["description"] = ModelProtocolWire.CriterionGuidance },
                    ["evidence"] = new JObject
                    {
                        ["type"] = "array", ["items"] = NonblankString(),
                        ["description"] = "Exact short evidence reference (E1, E2) references to terminal results of this chat delivered in this request; never invented IDs."
                    },
                    ["assessment"] = new JObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JArray("satisfied", "partially_satisfied", "unverified", "not_satisfied")
                    }
                })
            }, context.ReflectionEnabled));
            // Structured Outputs requires an object root; the closed action union
            // lives inside an explicit transport envelope, never a permissive root.
            var root = ClosedObject(new JObject
            {
                ["response"] = new JObject { ["anyOf"] = branches }
            });
            if (definitions.Count > 0) root["$defs"] = definitions;
            return root.ToString(Formatting.None);
        }

        private static void AddOptionalMessageBranches(JArray branches, string action, string field, JObject schema, bool reflectionEnabled)
        {
            var withMessage = Branch(action, field, schema, reflectionEnabled);
            var withoutMessage = (JObject)withMessage.DeepClone();
            ((JObject)withoutMessage["properties"]).Remove("message");
            withoutMessage["required"] = new JArray(((JObject)withoutMessage["properties"]).Properties().Select(item => item.Name));
            branches.Add(withoutMessage);
            branches.Add(withMessage);
        }

        private static JObject Branch(string action, string field, JObject schema, bool reflectionEnabled)
        {
            var properties = new JObject
            {
                ["action"] = new JObject { ["type"] = "string", ["const"] = action },
                ["reason"] = new JObject(NonblankString()) { ["description"] = ModelProtocolWire.ReasonGuidance }
            };
            if (action == ConversationResponse.FinishAction)
                properties["outcome"] = new JObject { ["type"] = "string",
                    ["enum"] = new JArray("completed", "partial", "unable_to_complete"),
                    ["description"] = ModelProtocolWire.FinishGuidance };
            properties[field] = schema;
            var message = action == ConversationResponse.FinishAction ? NonblankString() : new JObject { ["type"] = "string" };
            message["description"] = ModelProtocolWire.MessageGuidanceFor(reflectionEnabled);
            properties["message"] = message;
            return ClosedObject(properties);
        }

        private static JObject ClosedObject(JObject properties)
        {
            return new JObject
            {
                ["type"] = "object", ["properties"] = properties,
                ["required"] = new JArray(properties.Properties().Select(property => property.Name)),
                ["additionalProperties"] = false
            };
        }

        private static JObject NonblankString()
        {
            // Spell out Char.IsWhiteSpace's set; regex engines disagree on \\s (e.g. U+FEFF).
            return new JObject { ["type"] = "string", ["pattern"] = @"[^\t\n\v\f\r \u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]" };
        }

        private static JObject CallArray(JArray options, int maxItems)
        {
            return new JObject
            {
                ["type"] = "array", ["items"] = new JObject { ["anyOf"] = options },
                ["minItems"] = 1, ["maxItems"] = maxItems
            };
        }
    }
}
