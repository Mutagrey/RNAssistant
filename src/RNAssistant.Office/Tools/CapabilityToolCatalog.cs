using System;
using System.Collections.Generic;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    internal static class CapabilityToolCatalog
    {
        internal const string SearchToolId = "common.capabilities_search";
        internal const string ReadToolId = "common.capabilities_read";

        internal static bool Owns(string toolId)
        {
            return string.Equals(toolId, SearchToolId, StringComparison.Ordinal) ||
                string.Equals(toolId, ReadToolId, StringComparison.Ordinal);
        }

        internal static IEnumerable<ToolCatalogEntry> GetTools()
        {
            yield return Projection(
                SearchToolId,
                "Read-only: Filter the complete compact RUNTIME_CONTEXT.capabilities catalog by id or metadata. Results identify tools and skills but load neither; use the exact id with common.capabilities_read.",
                CapabilityCatalogService.SearchSchema(),
                "capabilities_search");
            yield return Projection(
                ReadToolId,
                "Read-only: Read one exact capability id from RUNTIME_CONTEXT.capabilities or capabilities_search. A tool result loads its exact callable schema; a skill result loads its complete Markdown body. Never invent or derive an id.",
                CapabilityCatalogService.ReadSchema(null, null),
                "capabilities_read");
        }

        private static ToolCatalogEntry Projection(
            string id, string description, string schema, string name)
        {
            return ControllerToolCatalogEntry.CreateTypedProjection(
                new ToolDescriptor(id, description, schema),
                new ToolPolicy(ToolEffect.Read, ToolVerification.None,
                    false, true, new[] { "agent", "plan" }),
                name: name, scope: "session");
        }
    }
}
