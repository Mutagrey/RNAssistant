using System;
using System.Collections.Generic;
using System.Linq;
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

        internal static IEnumerable<ToolDefinition> GetTools(
            PromptSettingsService service)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));
            if (!service.CanRead) yield break;
            yield return Projection(
                ReadToolId,
                "Read-only: Read current RNAssistant Markdown prompts, optionally including built-in defaults in the same result.",
                ReadSchema(), "prompts_read", false);
            yield return Projection(
                SaveToolId,
                "Mutates settings: Update any editable RNAssistant model prompt after the user asks to edit it. Agent general, tool-use, and skill-loading policies are separate fields but are composed into one instruction message at runtime. Compatibility probes remain fixed so their diagnostics stay trustworthy.",
                SaveSchema(), "prompts_save", true);
        }

        private static ToolDefinition Projection(
            string id, string description, string schema, string name,
            bool mutation)
        {
            var policy = mutation
                ? new ToolPolicy(ToolEffect.Write, ToolVerification.Tool,
                    true, false, new[] { "agent" }, 1)
                : new ToolPolicy(ToolEffect.Read, ToolVerification.None,
                    false, true, new[] { "agent" });
            return ControllerToolDefinition.CreateTypedProjection(
                new ToolDescriptor(id, description, schema), policy,
                name: name, scope: "global",
                mutatesLocalState: mutation);
        }

        private static string ReadSchema()
        {
            return "{\"type\":\"object\",\"properties\":{\"includeDefaults\":{\"type\":\"boolean\",\"description\":\"Whether to include built-in defaults beside current prompts.\",\"default\":false}},\"required\":[],\"additionalProperties\":false}";
        }

        private static string SaveSchema()
        {
            var properties = new JObject
            {
                ["systemPrompt"] = PromptProperty(
                    "General Agent-mode Markdown: role, runtime context, response contract, and completion rules."),
                ["agentToolsPrompt"] = PromptProperty(
                    "Agent-wide tool selection and execution policy; tool-specific input details remain in each tool schema."),
                ["agentSkillsPrompt"] = PromptProperty(
                    "Agent skill discovery, mandatory loading evidence, reference reading, and precedence policy."),
                ["chatSystemPrompt"] = PromptProperty(
                    "Complete tool-free Chat-mode Markdown prompt."),
                ["planSystemPrompt"] = PromptProperty(
                    "Complete read-only Plan-mode Markdown prompt."),
                ["systemPromptRole"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Message role used for prompt instructions.",
                    ["enum"] = new JArray("developer", "system", "user")
                },
                ["contextCompactionPrompt"] = PromptProperty(
                    "Markdown prompt used to compact completed history."),
                ["chatTitlePrompt"] = PromptProperty(
                    "Markdown prompt used to generate chat titles."),
                ["attachmentAnalysisPrompt"] = PromptProperty(
                    "Markdown prompt used by the auxiliary image/audio attachment analysis worker.")
            };
            var variants = new JArray(properties.Properties().Select(property =>
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = properties.DeepClone(),
                    ["required"] = new JArray(property.Name),
                    ["additionalProperties"] = false
                }));
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JArray(),
                ["additionalProperties"] = false,
                ["anyOf"] = variants
            }.ToString(Formatting.None);
        }

        private static JObject PromptProperty(string description)
        {
            return new JObject
            {
                ["type"] = "string",
                ["description"] = description,
                ["maxLength"] = PromptSettingsService.MaximumPromptCharacters
            };
        }
    }
}
