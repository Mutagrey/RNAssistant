using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Tools
{
    internal static class PlanDocumentToolCatalog
    {
        internal const string SaveToolId = "common.plan_doc_save";
        internal const string RestoreToolId = "common.plan_doc_restore";
        internal const string DeleteToolId = "common.plan_doc_delete";

        internal static bool Owns(string toolId)
        {
            return string.Equals(toolId, SaveToolId, StringComparison.Ordinal) ||
                string.Equals(toolId, RestoreToolId, StringComparison.Ordinal) ||
                string.Equals(toolId, DeleteToolId, StringComparison.Ordinal);
        }

        internal static IEnumerable<ToolCatalogEntry> GetTools()
        {
            yield return Projection(SaveToolId,
                "Plan document: Save the complete active Markdown plan. Runtime creates it when absent and otherwise appends an exactly guarded linear revision.",
                SaveSchema(), "plan_doc_save", 0);
            yield return Projection(RestoreToolId,
                "Plan document: On explicit request, restore one user-visible historical version as a new exactly guarded linear head without modifying history.",
                RestoreSchema(), "plan_doc_restore", 0);
            yield return Projection(DeleteToolId,
                "Plan document: Append an exactly guarded removal tombstone only when the user explicitly asks to remove the active plan.",
                DeleteSchema(), "plan_doc_delete", 1);
        }

        private static ToolCatalogEntry Projection(string id, string description,
            string schema, string name, int riskLevel)
        {
            return ControllerToolCatalogEntry.CreateTypedProjection(
                new ToolDescriptor(id, description, schema),
                new ToolPolicy(ToolEffect.Write, ToolVerification.Tool,
                    false, false, new[] { "plan" }, riskLevel),
                name: name, scope: "session", mutatesLocalState: true);
        }

        internal static string SchemaFor(string toolId)
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
                    ["status"] = new JObject { ["type"] = "string", ["enum"] = new JArray("draft", "ready"), ["description"] = "Draft while decisions remain; ready only when the plan is decision-complete." }
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
