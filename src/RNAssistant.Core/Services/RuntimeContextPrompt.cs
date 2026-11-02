using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Services
{
    // Rendering only: callers own the frozen metadata and admission. No parsing of
    // model prose, authority lookup, selection or second context store lives here.
    public static class RuntimeContextPrompt
    {
        public const string AgentHeader = "# RNAssistant Agent Contract\n\n";
        public const string SchemaReuseGuidance =
            "Entries in tools are already callable; use them directly without reading their schemas again. " +
            "Their exact arguments are in tools[].function.parameters or response_format.json_schema. " +
            "schemaInRequest=true is a compact receipt: the schema is supplied there, not missing. ";

        public static string Render(JObject context)
        {
            var value = (JObject)context.DeepClone();
            value.AddFirst(new JProperty("type", "runtime_context"));
            value["tool_guidance"] = SchemaReuseGuidance +
                "Use the tools listed in tools directly. Other capabilities must be loaded before use.";
            return value.ToString(Formatting.None);
        }

        public static bool IsRuntimeContext(ChatMessage message)
        {
            if (message == null || message.ToolCallId != null || message.AttachmentAnalysis != null) return false;
            try { var value = JObject.Parse(message.Content ?? ""); return value["type"]?.Type == JTokenType.String && (string)value["type"] == "runtime_context"; }
            catch (JsonException) { return false; }
        }

        public static bool UsesResponseSchema(ChatMessage message)
        {
            return IsRuntimeContext(message) && (bool?)JObject.Parse(message.Content)["schemaInRequest"] == true;
        }

        public static JArray Tools(IEnumerable<ToolCatalogEntry> tools)
        { return new JArray((tools ?? new ToolCatalogEntry[0]).Select(CapabilityCatalogService.BuildTool).Where(t => t != null)); }

        public static IReadOnlyList<ChatMessage> ProjectJsonSchemaMessages(IReadOnlyList<ChatMessage> messages,
            IEnumerable<ToolCatalogEntry> tools, string responseSchemaJson)
        {
            if (messages == null || messages.Count < 2 || string.IsNullOrWhiteSpace(responseSchemaJson) ||
                !(messages[0]?.Content ?? "").StartsWith(AgentHeader, StringComparison.Ordinal)) return messages;
            var expected = Tools(tools).OfType<JObject>().ToArray();
            JObject schema;
            try { schema = JObject.Parse(responseSchemaJson); }
            catch (JsonException) { return messages; }
            var execute = (schema.SelectToken("properties.response.anyOf") as JArray)?.OfType<JObject>()
                .FirstOrDefault(branch => (string)branch.SelectToken("properties.action.const") == "execute");
            var calls = execute?.SelectToken("properties.tool_calls") as JObject;
            if ((string)calls?["$ref"] == "#/$defs/calls") calls = schema["$defs"]?["calls"] as JObject;
            if (calls == null || expected.Length == 0) return messages;
            var alternatives = calls["anyOf"] as JArray;
            var arrays = alternatives == null ? new[] { calls } : alternatives.OfType<JObject>().ToArray();
            var options = arrays.SelectMany(array => (array.SelectToken("items.anyOf") as JArray ?? new JArray()).OfType<JObject>()).ToArray();
            if (options.Length != expected.Length) return messages;
            foreach (var tool in expected)
            {
                var matching = options.Where(option => (string)option.SelectToken("properties.name.const") ==
                    (string)tool.SelectToken("function.name")).ToArray();
                if (matching.Length != 1 || !JToken.DeepEquals(ToolSchemaSupport.ForStructuredOutput(
                    (JObject)tool.SelectToken("function.parameters"), keepDescriptions: true), matching[0].SelectToken("properties.arguments"))) return messages;
            }
            // Transform only the owned second context message beneath our instruction.
            var source = messages[1];
            if (source == null || source.ProtocolMessage || source.Attachments?.Count > 0 || !IsRuntimeContext(source)) return messages;
            var body = JObject.Parse(source.Content);
            if (!JToken.DeepEquals(body["tools"], new JArray(expected))) return messages;
            foreach (var tool in ((JArray)body["tools"]).OfType<JObject>())
                ((JObject)tool["function"]).Remove("parameters");
            body["schemaInRequest"] = true;
            var copy = JsonConvert.DeserializeObject<ChatMessage>(JsonConvert.SerializeObject(source));
            copy.Content = body.ToString(Formatting.None);
            var result = messages.ToArray();
            result[1] = copy;
            return result;
        }

    }
}
