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
        internal const string CreateToolId = "common.plan_doc_create";
        internal const string UpdateToolId = "common.plan_doc_update";
        internal const string RestoreToolId = "common.plan_doc_restore";
        internal const string DeleteToolId = "common.plan_doc_delete";

        internal static bool Owns(string toolId)
        {
            return string.Equals(toolId, CreateToolId, StringComparison.Ordinal) ||
                string.Equals(toolId, UpdateToolId, StringComparison.Ordinal) ||
                string.Equals(toolId, RestoreToolId, StringComparison.Ordinal) ||
                string.Equals(toolId, DeleteToolId, StringComparison.Ordinal);
        }

        internal static IEnumerable<ToolDefinition> GetTools()
        {
            yield return Projection(CreateToolId,
                "Plan document: Create the single revisioned Markdown plan for this chat after discovery and alignment.",
                CreateSchema(), "plan_doc_create", 0);
            yield return Projection(UpdateToolId,
                "Plan document: Replace the active Markdown plan body using an exact current revision guard.",
                UpdateSchema(), "plan_doc_update", 0);
            yield return Projection(RestoreToolId,
                "Plan document: Restore one exact historical revision as a new linear head without modifying history.",
                RestoreSchema(), "plan_doc_restore", 0);
            yield return Projection(DeleteToolId,
                "Plan document: Append a removal tombstone only when the user explicitly asks to remove the logical plan.",
                DeleteSchema(), "plan_doc_delete", 1);
        }

        private static ToolDefinition Projection(string id, string description,
            string schema, string name, int riskLevel)
        {
            return ControllerToolDefinition.CreateTypedProjection(
                new ToolDescriptor(id, description, schema),
                new ToolPolicy(ToolEffect.Write, ToolVerification.Tool,
                    false, false, new[] { "plan" }, riskLevel),
                name: name, scope: "session", mutatesLocalState: true);
        }

        private static string CreateSchema()
        {
            return Schema(false).ToString(Formatting.None);
        }

        private static string UpdateSchema()
        {
            return Schema(true).ToString(Formatting.None);
        }

        private static JObject Schema(bool update)
        {
            var properties = new JObject
            {
                ["id"] = new JObject { ["type"] = "string", ["description"] = "Stable plan id from create or metadata." },
                ["expectedRevisionArtifactId"] = new JObject { ["type"] = "string", ["description"] = "Exact active revision artifact id used as an optimistic concurrency guard." },
                ["title"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 200, ["description"] = "User-visible plan title." },
                ["markdown"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = PlanDocumentService.MaximumMarkdownCharacters, ["description"] = "Complete free-form Markdown plan body; preserve it exactly without trimming or partial patch semantics." },
                ["status"] = new JObject { ["type"] = "string", ["enum"] = new JArray("draft", "ready"), ["default"] = "draft", ["description"] = "Draft while being refined; ready only after requirements are decision-complete." }
            };
            if (!update)
            {
                properties.Remove("id");
                properties.Remove("expectedRevisionArtifactId");
            }
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = update
                    ? new JArray("id", "expectedRevisionArtifactId", "markdown")
                    : new JArray("title", "markdown"),
                ["additionalProperties"] = false
            };
        }

        private static string DeleteSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["id"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["description"] = "Stable logical Plan id." },
                    ["expectedRevisionArtifactId"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["description"] = "Exact active revision artifact id guarded by the removal." }
                },
                ["required"] = new JArray("id", "expectedRevisionArtifactId"),
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
                    ["id"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["description"] = "Stable logical Plan id." },
                    ["expectedRevisionArtifactId"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["description"] = "Exact active revision artifact id used as the optimistic concurrency guard." },
                    ["sourceRevisionArtifactId"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["description"] = "Exact historical revision whose complete body/title/status become the new head." }
                },
                ["required"] = new JArray("id", "expectedRevisionArtifactId", "sourceRevisionArtifactId"),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }
    }
}
