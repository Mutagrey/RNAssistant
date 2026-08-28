using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.ModelProtocol
{
    public sealed class ConversationResponseParser
    {
        public ConversationResponseParseResult Parse(string content, IEnumerable<ToolDefinition> callableTools,
            IEnumerable<ToolDefinition> runnableCatalog, ModelProtocolCallContext context)
        {
            if (context == null || !context.IsComplete)
                return ConversationResponseParseResult.Fail("V4 requires a complete local batch-safety context: " +
                    (context == null ? "missing context" : context.Error));
            if (callableTools == null || runnableCatalog == null)
                return ConversationResponseParseResult.Fail("V4 parsing requires explicit callable/catalog and batch-safe read-only context.");
            var parsed = ConversationResponseJson.Read(content);
            if (!parsed.Success) return parsed;

            var knownTools = callableTools.Where(tool => tool != null && !string.IsNullOrWhiteSpace(tool.Id))
                .GroupBy(tool => tool.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var catalogIds = new HashSet<string>(runnableCatalog.Where(tool => tool != null).Select(tool => tool.Id), StringComparer.Ordinal);
            // Batching is opt-in from local authority. External or unresolved
            // effects must not be in this set; call identity belongs to runtime.
            var batchSafeIds = new HashSet<string>(context.BatchSafeReadOnlyToolIds, StringComparer.Ordinal);
            foreach (var call in parsed.Response.ToolCalls)
            {
                ToolDefinition tool;
                if (!knownTools.TryGetValue(call.Name, out tool))
                {
                    return ConversationResponseParseResult.Fail(catalogIds.Contains(call.Name)
                        ? "Tool schema is not loaded: " + call.Name + ". Call common.capabilities_read with arguments " +
                            new JObject { ["id"] = call.Name }.ToString(Formatting.None) + ", wait for its complete TOOL_RESULT, then call the tool in a later response."
                        : "Unknown tool: " + call.Name + ". Use an exact name from the current callable tools.");
                }
                if (parsed.Response.ToolCalls.Count > 1 && (tool.MutatesDocument || tool.MutatesLocalState ||
                    tool.RequiresConfirmation || !batchSafeIds.Contains(tool.Id)))
                    return ConversationResponseParseResult.Fail("Write, external, confirmation-required or unclassified calls must be returned one at a time. " +
                        "Return exactly one call and wait for its TOOL_RESULT.");

                JObject schema;
                string error;
                if (!ToolSchemaSupport.TryParse(tool, out schema, out error))
                    return ConversationResponseParseResult.Fail("Invalid callable tool schema for " + tool.Id + ": " + error);
                var arguments = JObject.FromObject(call.Arguments);
                ToolSchemaSupport.RemoveOptionalNulls(arguments, schema);
                if (!ToolSchemaSupport.ValidateArguments(arguments, schema, false, out error))
                    return ConversationResponseParseResult.Fail("Invalid arguments for " + tool.Id + ": " + error);
                call.Arguments.Clear();
                ToolArgumentNormalizer.AddProperties(arguments, call.Arguments);
            }
            return parsed;
        }
    }
}
