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
            var reads = new JArray();
            var singleCalls = new JArray();
            foreach (var tool in callableTools.Where(item => item != null && !string.IsNullOrWhiteSpace(item.Id))
                .GroupBy(item => item.Id, System.StringComparer.Ordinal).Select(group => group.First()))
            {
                JObject parameters;
                string error;
                if (!ToolSchemaSupport.TryParse(tool, out parameters, out error)) continue;
                var options = context.AllowsSequentialBatch(tool) ? reads : singleCalls;
                options.Add(ClosedObject(new JObject
                {
                    ["name"] = new JObject { ["type"] = "string", ["const"] = tool.Id },
                    ["arguments"] = ToolSchemaSupport.ForStructuredOutput(parameters, keepDescriptions: true)
                }));
            }
            var branches = new JArray();
            // Disjoint catalogs keep each argument contract once, and exclude mixed batches.
            if (reads.Count > 0 || singleCalls.Count > 0)
            {
                var calls = reads.Count > 0 && singleCalls.Count > 0
                    ? new JObject { ["anyOf"] = new JArray(CallArray(singleCalls, 1), CallArray(reads, MaximumToolCalls)) }
                    : reads.Count > 0 ? CallArray(reads, MaximumToolCalls) : CallArray(singleCalls, 1);
                branches.Add(Branch(ConversationResponse.ExecuteAction, "tool_calls", calls));
            }
            branches.Add(Branch(ConversationResponse.ReflectionAction, "decision", NonblankString()));
            branches.Add(Branch(ConversationResponse.FinishAction, "results", new JObject
            {
                ["type"] = "array",
                ["items"] = ClosedObject(new JObject
                {
                    ["summary"] = NonblankString(),
                    ["evidence"] = new JObject
                    {
                        ["type"] = "array", ["items"] = NonblankString(),
                        ["description"] = "Exact runtime tool_call_id references to terminal results of this run; never invented IDs."
                    },
                    ["assessment"] = new JObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JArray("satisfied", "partially_satisfied", "unverified", "not_satisfied")
                    }
                })
            }));
            // Structured Outputs requires an object root; the closed action union
            // lives inside an explicit transport envelope, never a permissive root.
            return ClosedObject(new JObject
            {
                ["response"] = new JObject { ["anyOf"] = branches }
            }).ToString(Formatting.None);
        }

        private static JObject Branch(string action, string field, JObject schema)
        {
            return ClosedObject(new JObject
            {
                ["action"] = new JObject { ["type"] = "string", ["const"] = action },
                ["reason"] = new JObject(NonblankString()) { ["description"] = "Brief decision basis, not private reasoning." },
                [field] = schema,
                ["message"] = new JObject { ["type"] = "string", ["description"] = ModelProtocolWire.MessageGuidance }
            });
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
