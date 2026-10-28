using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Tools
{
    public static class PlanDocumentToolCatalog
    {
        public const string SaveToolId = "plan.document.save";
        public const string RestoreToolId = "plan.document.restore";
        public const string DeleteToolId = "plan.document.delete";

        public static bool Owns(string toolId)
        {
            return string.Equals(toolId, SaveToolId, StringComparison.Ordinal) ||
                string.Equals(toolId, RestoreToolId, StringComparison.Ordinal) ||
                string.Equals(toolId, DeleteToolId, StringComparison.Ordinal);
        }

        public static IEnumerable<ToolCatalogEntry> GetTools()
        {
            yield return Projection(SaveToolId,
                "Plan document: Save a complete Markdown plan. Update the selected plan, or set startNew=true to create an independent plan for a different task without removing the previous one.",
                SaveSchema(), "plan_doc_save", 0, true);
            yield return Projection(RestoreToolId,
                "Plan document: On explicit request, restore one user-visible historical version as a new exactly guarded linear head without modifying history.",
                RestoreSchema(), "plan_doc_restore", 0);
            yield return Projection(DeleteToolId,
                "Plan document: Append an exactly guarded removal tombstone only when the user explicitly asks to remove the active plan.",
                DeleteSchema(), "plan_doc_delete", 1);
        }

        private static ToolCatalogEntry Projection(string id, string description,
            string schema, string name, int riskLevel, bool agentAllowed = false)
        {
            var policy = new ToolPolicy(ToolEffect.Write, ToolVerification.Tool,
                false, false, agentAllowed ? new[] { "agent", "plan" } : new[] { "agent" }, riskLevel);
            return new ToolCatalogEntry
            {
                Id = id, Host = "Common", Name = name, Description = description,
                ArgumentSchemaJson = schema, BuiltIn = true, Enabled = true, Scope = "session",
                AgentCanRun = true, MutatesLocalState = true, RiskLevel = riskLevel,
                Policy = policy, Binding = PlanDocumentToolHandler.BindingFor(id)
            };
        }

        public static string SchemaFor(string toolId)
        {
            if (string.Equals(toolId, SaveToolId, StringComparison.Ordinal))
                return SaveSchema();
            if (string.Equals(toolId, RestoreToolId, StringComparison.Ordinal))
                return RestoreSchema();
            if (string.Equals(toolId, DeleteToolId, StringComparison.Ordinal))
                return DeleteSchema();
            return null;
        }

        private static string SaveSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["title"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 200, ["description"] = "User-visible plan title." },
                    ["markdown"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = PlanDocumentService.MaximumMarkdownCharacters, ["description"] = "Complete free-form Markdown plan body; preserve it exactly without trimming or partial patch semantics." },
                    ["status"] = new JObject { ["type"] = "string", ["enum"] = new JArray("draft", "ready"), ["description"] = "Draft while decisions remain; ready only when the plan is decision-complete." },
                    ["startNew"] = new JObject { ["type"] = "boolean", ["description"] = "True only when the selected Plan belongs to a different task; keep its history and create a separate selected Plan." }
                },
                ["required"] = new JArray("title", "markdown", "status"),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }

        private static string DeleteSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject(),
                ["required"] = new JArray(),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }

        private static string RestoreSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["version"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["description"] = "User-visible historical Plan version to restore as the new head." }
                },
                ["required"] = new JArray("version"),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }
    }
}
