using RNAssistant.Core.Tools;
using System.Collections.Generic;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Tools
{
    // Catalog projection only. Execution belongs to the four typed handlers.
    internal static class ResourceToolCatalog
    {
        public const string ListToolId = "common.resources_list";
        public const string ResolveToolId = "common.resources_resolve";
        public const string SearchToolId = "common.resources_search";
        public const string ReadToolId = "common.resources_read";

        internal static IEnumerable<ToolCatalogEntry> GetControllerTools()
        {
            yield return ControllerToolCatalogEntry.CreateReadProjection(
                ResourceListToolHandler.Descriptor, ResourceListToolHandler.Policy, "resources_list");
            yield return ControllerToolCatalogEntry.CreateReadProjection(
                ResourceResolveToolHandler.Descriptor, ResourceResolveToolHandler.Policy, "resources_resolve");
            yield return ControllerToolCatalogEntry.CreateReadProjection(
                ResourceSearchToolHandler.Descriptor, ResourceSearchToolHandler.Policy, "resources_search");
            yield return ControllerToolCatalogEntry.CreateReadProjection(
                ResourceReadToolHandler.Descriptor, ResourceReadToolHandler.Policy, "resources_read");
        }
    }
}
