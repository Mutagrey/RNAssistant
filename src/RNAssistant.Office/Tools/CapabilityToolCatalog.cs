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
                "Read-only: Filter the complete compact RUNTIME_CONTEXT.capabilities catalog by id or metadata. Results identify tools and skills but load neither. Paging and limits are runtime-owned; refine the semantic query when complete=false.",
                CapabilityCatalogService.SearchSchema(),
                "capabilities_search");
            yield return Projection(
                ReadToolId,
                "Read-only: Read one exact public capability id from RUNTIME_CONTEXT.capabilities or capabilities_search. For a listed skill reference, use its semantic referencePath and action=next after hasMore=true; offsets, page sizes, catalog revisions, and admission state are runtime-owned.",
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
