using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.ModelProtocol
{
    public sealed class ConversationResponseParser
    {
        public ConversationResponseParseResult Parse(string content, IEnumerable<ToolCatalogEntry> callableTools,
            IEnumerable<ToolCatalogEntry> runnableCatalog, ModelProtocolCallContext context, bool schemaTransport = false)
        {
            if (context == null || !context.IsComplete)
                return ConversationResponseParseResult.Fail(ConversationResponseError.InvalidContext, "v8 requires a complete local sequential-batch context: " +
                    (context == null ? "missing context" : context.Error));
            if (callableTools == null || runnableCatalog == null)
                return ConversationResponseParseResult.Fail(ConversationResponseError.InvalidContext, "v8 parsing requires explicit callable/catalog and sequential-batch context.");
            var parsed = ConversationResponseJson.Read(content, schemaTransport, context.ReflectionEnabled);
            if (!parsed.Success) return parsed;

            var knownTools = callableTools.Where(tool => tool != null && !string.IsNullOrWhiteSpace(tool.Id))
                .GroupBy(tool => tool.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var catalog = runnableCatalog.Where(tool => tool != null && !string.IsNullOrWhiteSpace(tool.Id))
                .GroupBy(tool => tool.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            // Batching is opt-in from local authority. External or unresolved
            // effects must not be in this set; call identity belongs to runtime.
            var calls = parsed.Response.ToolCalls;
            var validatedCalls = new List<ConversationToolCall>();
            foreach (var call in calls)
            {
                ToolCatalogEntry tool;
                if (!knownTools.TryGetValue(call.Name, out tool))
                {
                    ToolCatalogEntry entry;
                    var known = catalog.TryGetValue(call.Name, out entry);
                    return ConversationResponseParseResult.Fail(known ? ConversationResponseError.SchemaNotLoaded : ConversationResponseError.UnknownTool, known
                        ? entry.Exposure == ToolExposure.Skill
                            ? "Tool schema is not loaded: " + call.Name + ". Select its published skill with core.skills.load, then wait for the next request before using its tools. If that load was refused, resolve its reported blocker before retrying; the rejected selection grants no tools."
                            : "Tool schema is not loaded: " + call.Name + ". Call core.tools.read with arguments " +
                            new JObject { ["id"] = call.Name }.ToString(Formatting.None) + ", wait for its complete TOOL_RESULT, then call the tool in a later response."
                        : "Unknown tool: " + call.Name + ". Use an exact name from the current callable tools.");
                }
                if (calls.Count > 1 && !context.AllowsSequentialBatch(tool))
                    return ConversationResponseParseResult.Fail(ConversationResponseError.InvalidBatch,
                        "Only independent local reads may be returned in a sequential batch. " +
                        tool.Id + " must be returned one at a time. No calls from this response were executed and no skills were loaded. " +
                        "Return one required standalone call, then wait for its result before the next call.");

                JObject schema;
                string error;
                if (!ToolSchemaSupport.TryParse(tool, out schema, out error))
                    return ConversationResponseParseResult.Fail(ConversationResponseError.InvalidToolSchema, "Invalid callable tool schema for " + tool.Id + ": " + error);
                var arguments = call.Arguments;
                ToolSchemaSupport.RemoveOptionalNulls(arguments, schema);
                if (!ToolSchemaSupport.ValidateArguments(arguments, schema, false, out error))
                    return ConversationResponseParseResult.Fail(ConversationResponseError.InvalidArguments, "Invalid arguments for " + tool.Id + ": " + error);
                validatedCalls.Add(call);
            }
            return parsed.Response.Action == ConversationResponse.ExecuteAction
                ? ConversationResponseParseResult.Ok(new ConversationResponse(ConversationResponse.ExecuteAction,
                    parsed.Response.Reason, parsed.Response.Message, calls: validatedCalls))
                : parsed;
        }
    }
}
