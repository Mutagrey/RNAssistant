using System;
using System.Collections.Generic;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    internal static partial class ToolAuthoringCatalog
    {
        internal const string DefinitionReadToolId =
            "common.tools_definition_read";
        internal const string ValidateToolId = "common.tools_validate";
        internal const string UpsertToolId = "common.tools_upsert";
        internal const string DeleteToolId = "common.tools_delete";

        internal static bool Owns(string toolId)
        {
            return string.Equals(toolId, DefinitionReadToolId,
                    StringComparison.Ordinal) ||
                string.Equals(toolId, ValidateToolId,
                    StringComparison.Ordinal) ||
                string.Equals(toolId, UpsertToolId,
                    StringComparison.Ordinal) ||
                string.Equals(toolId, DeleteToolId,
                    StringComparison.Ordinal);
        }

        internal static bool IsMutation(string toolId)
        {
            return string.Equals(toolId, UpsertToolId,
                    StringComparison.Ordinal) ||
                string.Equals(toolId, DeleteToolId,
                    StringComparison.Ordinal);
        }

        internal static IEnumerable<ToolCatalogEntry> GetTools(
            ToolAuthoringService service)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));
            if (!service.CanUse) yield break;

            yield return Projection(
                DefinitionReadToolId,
                "Read-only authoring inspection: Read one custom tool definition including its implementation fields; omit id to list compact custom-tool metadata. This does not load a callable schema.",
                OptionalIdSchema(), "tools_definition_read", false);
            yield return Projection(
                ValidateToolId,
                "Read-only: Validate a manifest-based VBA tool definition without saving it. Agent authoring may use compact parameterDefinitions; advanced callers may pass complete native parameters objects.",
                ToolPayloadSchema(false), "tools_validate", false);
            yield return Projection(
                UpsertToolId,
                "Mutates settings: Create or update one custom tool after validating the effective definition. In Agent mode prefer compact parameterDefinitions; parameters remains the advanced native form. Omitted update fields are preserved.",
                ToolUpsertSchema(), "tools_upsert", true);
            yield return Projection(
                DeleteToolId,
                "Mutates settings: Delete a custom RNAssistant tool by id.",
                "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\",\"description\":\"Exact stable identifier.\"}},\"required\":[\"id\"],\"additionalProperties\":false}",
                "tools_delete", true);
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
    }
}
