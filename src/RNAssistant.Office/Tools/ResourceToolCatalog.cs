using RNAssistant.Core.Tools;
using System.Collections.Generic;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Tools
{
    // Catalog projection only. Exact routing and continuation belong to the
    // runtime handlers, not to model arguments.
    internal static class ResourceToolCatalog
    {
        public const string FindToolId = "common.resources_find";
        public const string ReadToolId = "common.resources_read";

        internal static IEnumerable<ToolCatalogEntry> GetControllerTools()
        {
            yield return ControllerToolCatalogEntry.CreateReadProjection(
                ResourceFindToolHandler.Descriptor, ResourceFindToolHandler.Policy, "resources_find");
            yield return ControllerToolCatalogEntry.CreateReadProjection(
                ResourceReadToolHandler.Descriptor, ResourceReadToolHandler.Policy, "resources_read");
            foreach (var tool in ResourceDefinitionToolHandler.Catalog()) yield return tool;
        }
    }
}
