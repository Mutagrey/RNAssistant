using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    internal static class PromptToolCatalog
    {
        internal const string ReadToolId = "common.prompts_read";
        internal const string SaveToolId = "common.prompts_save";

        internal static bool Owns(string toolId)
        {
            return string.Equals(toolId, ReadToolId, StringComparison.Ordinal) ||
                string.Equals(toolId, SaveToolId, StringComparison.Ordinal);
        }

        internal static bool IsMutation(string toolId)
        {
            return string.Equals(toolId, SaveToolId, StringComparison.Ordinal);
        }

        internal static IEnumerable<ToolCatalogEntry> GetTools(
            PromptSettingsService service)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));
            if (!service.CanRead) yield break;
            yield return Projection(
                ReadToolId,
                "Read-only: Read current RNAssistant Markdown prompts, optionally including built-in defaults in the same result.",
                SchemaFor(ReadToolId), "prompts_read", false);
            yield return Projection(
                SaveToolId,
                "Mutates settings: Replace one exact editable RNAssistant model prompt after the user asks to edit it. Agent general, tool-use, and skill-loading policies are separate fields but are composed into one instruction message at runtime. Compatibility probes remain fixed so their diagnostics stay trustworthy.",
                SchemaFor(SaveToolId), "prompts_save", true);
        }

        private static ToolCatalogEntry Projection(
            string id, string description, string schema, string name,
            bool mutation)
        {
            var policy = mutation
                ? new ToolPolicy(ToolEffect.Write, ToolVerification.Tool,
                    true, false, new[] { "agent" }, 1)
                : new ToolPolicy(ToolEffect.Read, ToolVerification.None,
                    false, true, new[] { "agent" });
            return ControllerToolCatalogEntry.CreateTypedProjection(
                new ToolDescriptor(id, description, schema), policy,
                name: name, scope: "global",
                mutatesLocalState: mutation);
        }

        internal static string SchemaFor(string toolId)
        {
            if (string.Equals(toolId, ReadToolId, StringComparison.Ordinal))
                return ReadSchema();
            if (string.Equals(toolId, SaveToolId, StringComparison.Ordinal))
                return SaveSchema();
            throw new ArgumentException("Unknown prompt tool id: " + toolId,
                nameof(toolId));
        }

        private static string ReadSchema()
        {
            return "{\"type\":\"object\",\"properties\":{\"includeDefaults\":{\"type\":\"boolean\",\"description\":\"Whether to include built-in defaults beside current prompts.\",\"default\":false}},\"required\":[],\"additionalProperties\":false}";
        }

        private static string SaveSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["promptKey"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Exact editable prompt setting to replace.",
                        ["enum"] = new JArray(
                            "systemPrompt", "agentToolsPrompt",
                            "agentSkillsPrompt", "chatSystemPrompt",
                            "planSystemPrompt", "systemPromptRole",
                            "contextCompactionPrompt", "chatTitlePrompt",
                            "attachmentAnalysisPrompt")
                    },
                    ["value"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Complete replacement value. For systemPromptRole use developer, system, or user; all other keys accept Markdown.",
                        ["maxLength"] = PromptSettingsService.MaximumPromptCharacters
                    }
                },
                ["required"] = new JArray("promptKey", "value"),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }
    }
}
