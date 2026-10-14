using System;
using System.Collections.Generic;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    internal static partial class ToolAuthoringCatalog
    {
        internal const string UpsertToolId = "common.tools_upsert";
        internal const string DeleteToolId = "common.tools_delete";

        internal static bool Owns(string toolId)
        {
            return string.Equals(toolId, UpsertToolId,
                    StringComparison.Ordinal) ||
                string.Equals(toolId, DeleteToolId,
                    StringComparison.Ordinal);
        }

        internal static IEnumerable<ToolCatalogEntry> GetTools(
            ToolAuthoringService service, bool enableJavaScript = true)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));
            if (!service.CanUse) yield break;

            yield return Projection(
                UpsertToolId,
                enableJavaScript
                    ? "Mutates settings: Create or update a VBA package or read-only JavaScript tool. VBA implementation and metadata belong in the complete components package and entry manifest. JS-specific fields require executor=js; creation requires code and a strict parameters schema with a required resources array. Do not mix VBA components and JS fields. Omitted update fields are preserved."
                    : "Mutates settings: Create or update a VBA package. Supply the complete components package when creating or changing source, name, description or parameters; VBA metadata belongs in the entry manifest. Supplied components replace the entire package, not one component. Omitted update fields are preserved.",
                SchemaFor(UpsertToolId, enableJavaScript), "tools_upsert");
            yield return Projection(
                DeleteToolId,
                "Mutates settings: Delete a custom RNAssistant tool by id.",
                SchemaFor(DeleteToolId),
                "tools_delete");
        }

        internal static string SchemaFor(string toolId, bool enableJavaScript = true)
        {
            if (string.Equals(toolId, UpsertToolId,
                    StringComparison.Ordinal)) return ToolUpsertSchema(enableJavaScript);
            if (string.Equals(toolId, DeleteToolId,
                    StringComparison.Ordinal)) return ExactIdSchema();
            throw new ArgumentException("Unknown tool authoring id: " + toolId,
                nameof(toolId));
        }

        private static ToolCatalogEntry Projection(
            string id, string description, string schema, string name)
        {
            var policy = new ToolPolicy(ToolEffect.Write, ToolVerification.Tool,
                true, false, new[] { "agent" }, 1);
            return ControllerToolCatalogEntry.CreateTypedProjection(
                new ToolDescriptor(id, description, schema), policy,
                ToolAuthoringMutationToolHandler.BindingFor(id),
                name: name, scope: "global",
                mutatesLocalState: true);
        }
    }
}
