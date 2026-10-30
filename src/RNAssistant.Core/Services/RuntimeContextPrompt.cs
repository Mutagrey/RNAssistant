using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
        public const string Header = "# RUNTIME_CONTEXT\n";
        public const string AgentHeader = "# RNAssistant Agent Contract\n\n";
        public const string DefinitionsHeader = "## Callable tool definitions\n";
        public const string SchemaDefinitions = DefinitionsHeader + "Exact argument contracts are in response_format.json_schema.\n";

        public static string Render(JObject context)
        {
            var text = new StringBuilder(Header);
            var metadata = new JObject(context.Properties().Where(p => p.Name != "tools" && p.Name != "capabilities")
                .Select(p => new JProperty(p.Name, p.Value.DeepClone())));
            text.Append("## Environment\n").Append(Metadata(metadata));
            var tools = (context["tools"] as JArray ?? new JArray()).OfType<JObject>().ToArray();
            var callable = new HashSet<string>(tools.Select(t => (string)t.SelectToken("function.name")), StringComparer.Ordinal);
            text.Append("\n## Capabilities — available now\n");
            foreach (var tool in tools)
            {
                text.Append("- `").Append((string)tool.SelectToken("function.name")).Append("` — ")
                    .Append(Line((string)tool.SelectToken("function.description"))).Append('\n');
                if (tool["safety"] is JObject safety)
                    text.Append("  policy: ").Append(safety.ToString(Formatting.None)).Append('\n');
            }
            if (tools.Length == 0) text.Append("- None.\n");
            text.Append("\n## Capabilities — load on demand\n");
            var loaded = new HashSet<string>((context["loaded_skills"] as JArray ?? new JArray()).Values<string>(), StringComparer.Ordinal);
            var items = ((context["capabilities"] as JObject)?["items"] as JArray ?? new JArray()).OfType<JObject>()
                .Where(item => (string)item["kind"] == "tool" ? !callable.Contains((string)item["id"]) : !loaded.Contains((string)item["id"])).ToArray();
            foreach (var item in items)
            {
                text.Append("- `").Append((string)item["id"]).Append("` (").Append((string)item["kind"])
                    .Append(") — ").Append(Line((string)item["summary"])).Append('\n');
                if (item["loadSkill"] is JArray skills)
                    text.Append("  loadSkill: ").Append(skills.ToString(Formatting.None)).Append('\n');
            }
            if (items.Length == 0) text.Append("- None.\n");
            text.Append("Only available tools are callable. Load a direct tool with core.tools.read; select a skill with core.skills.load. A listed loadSkill is required; an empty loadSkill list means unavailable.\n\n");
            text.Append(Definitions(tools));
            return text.ToString();
        }

        // JSON values are a YAML subset. One key per line avoids wrapping/escaping
        // a whole runtime object and preserves arbitrary strings exactly.
        public static string Metadata(JObject fields)
        {
            var text = new StringBuilder("```yaml\n");
            foreach (var field in fields.Properties())
                text.Append(field.Name).Append(": ").Append(field.Value.ToString(Formatting.None)).Append('\n');
            return text.Append("```\n").ToString();
        }

        public static JArray Tools(IEnumerable<ToolCatalogEntry> tools)
        { return new JArray((tools ?? new ToolCatalogEntry[0]).Select(CapabilityCatalogService.BuildTool).Where(t => t != null)); }

        public static string Definitions(IEnumerable<JObject> tools)
        {
            var text = new StringBuilder(DefinitionsHeader);
            foreach (var tool in tools)
                text.Append("### ").Append((string)tool.SelectToken("function.name")).Append("\n```json\n")
                    .Append(tool.SelectToken("function.parameters").ToString(Formatting.None)).Append("\n```\n");
            return text.ToString();
        }

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
                .SingleOrDefault(branch => (string)branch.SelectToken("properties.action.const") == "execute");
            var calls = execute?.SelectToken("properties.tool_calls") as JObject;
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
            var full = Definitions(expected);
            // Only the owned second message is runtime context. A pasted header in
            // user/history content must never trigger this wire transformation.
            var source = messages[1];
            if (source == null || source.ProtocolMessage || source.ToolCallId != null || source.Attachments?.Count > 0 ||
                !(source.Content ?? "").StartsWith(Header, StringComparison.Ordinal) || !source.Content.EndsWith(full, StringComparison.Ordinal)) return messages;
            var copy = JsonConvert.DeserializeObject<ChatMessage>(JsonConvert.SerializeObject(source));
            copy.Content = source.Content.Substring(0, source.Content.Length - full.Length) + SchemaDefinitions;
            var result = messages.ToArray();
            result[1] = copy;
            return result;
        }

        private static string Line(string value) { return (value ?? "").Replace("\r", " ").Replace("\n", " "); }
    }
}
