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
        public ConversationResponseParseResult Parse(string content, IEnumerable<ToolCatalogEntry> callableTools,
            IEnumerable<ToolCatalogEntry> runnableCatalog, ModelProtocolCallContext context)
        {
            if (context == null || !context.IsComplete)
                return ConversationResponseParseResult.Fail("v7 requires a complete local sequential-batch context: " +
                    (context == null ? "missing context" : context.Error));
            if (callableTools == null || runnableCatalog == null)
                return ConversationResponseParseResult.Fail("v7 parsing requires explicit callable/catalog and sequential-batch context.");
            var parsed = ConversationResponseJson.Read(content);
            if (!parsed.Success) return parsed;

            var knownTools = callableTools.Where(tool => tool != null && !string.IsNullOrWhiteSpace(tool.Id))
                .GroupBy(tool => tool.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var catalog = runnableCatalog.Where(tool => tool != null && !string.IsNullOrWhiteSpace(tool.Id))
                .GroupBy(tool => tool.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            // Batching is opt-in from local authority. External or unresolved
            // effects must not be in this set; call identity belongs to runtime.
            foreach (var call in parsed.Response.ToolCalls)
            {
                ToolCatalogEntry tool;
                if (!knownTools.TryGetValue(call.Name, out tool))
                {
                    ToolCatalogEntry entry;
                    return ConversationResponseParseResult.Fail(catalog.TryGetValue(call.Name, out entry)
                        ? entry.Exposure == ToolExposure.Skill
                            ? "Tool schema is not loaded: " + call.Name + ". Select its published skill with common.skills_load, then wait for the next request before using its tools."
                            : "Tool schema is not loaded: " + call.Name + ". Call common.capabilities_read with arguments " +
                            new JObject { ["id"] = call.Name }.ToString(Formatting.None) + ", wait for its complete TOOL_RESULT, then call the tool in a later response."
                        : "Unknown tool: " + call.Name + ". Use an exact name from the current callable tools.");
                }
                if (parsed.Response.ToolCalls.Count > 1 && !context.AllowsSequentialBatch(tool))
                    return ConversationResponseParseResult.Fail("Only independent local reads may be returned in a sequential batch. " +
                        "Every mutation, confirmation-required, external, opaque or unclassified call must be returned one at a time; wait for its result before the next mutation.");

                JObject schema;
                string error;
                if (!ToolSchemaSupport.TryParse(tool, out schema, out error))
                    return ConversationResponseParseResult.Fail("Invalid callable tool schema for " + tool.Id + ": " + error);
                var arguments = call.Arguments;
                ToolSchemaSupport.RemoveOptionalNulls(arguments, schema);
                if (!ToolSchemaSupport.ValidateArguments(arguments, schema, false, out error))
                    return ConversationResponseParseResult.Fail("Invalid arguments for " + tool.Id + ": " + error);
            }
            return parsed;
        }
    }
}
