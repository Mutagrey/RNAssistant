using System;
using System.Collections.Generic;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    internal static class HtmlWorkspaceToolCatalog
    {
        internal const string InspectWorkspaceToolId =
            "common.html_workspace_inspect";
        internal const string UpsertToolId =
            "common.html_workspace_upsert";
        internal const string ApplyPatchToolId =
            "common.html_workspace_apply_patch";
        internal const string DeleteToolId =
            "common.html_workspace_delete";
        internal const string SetActiveToolId =
            "common.html_workspace_set_active";
        internal const string BindDataToolId = "common.html_data_bind";
        internal const string RefreshDataToolId = "common.html_data_refresh";
        internal const string FreezeDataToolId = "common.html_data_freeze";

        internal static bool Owns(string toolId)
        {
            return string.Equals(toolId, InspectWorkspaceToolId,
                    StringComparison.Ordinal) ||
                string.Equals(toolId, UpsertToolId, StringComparison.Ordinal) ||
                string.Equals(toolId, ApplyPatchToolId,
                    StringComparison.Ordinal) ||
                string.Equals(toolId, DeleteToolId, StringComparison.Ordinal) ||
                string.Equals(toolId, SetActiveToolId,
                    StringComparison.Ordinal) ||
                string.Equals(toolId, BindDataToolId, StringComparison.Ordinal) ||
                string.Equals(toolId, RefreshDataToolId,
                    StringComparison.Ordinal) ||
                string.Equals(toolId, FreezeDataToolId,
                    StringComparison.Ordinal);
        }

        internal static bool IsMutation(string toolId)
        {
            return Owns(toolId) && !string.Equals(
                toolId, InspectWorkspaceToolId, StringComparison.Ordinal);
        }

        internal static bool RequiresOfficeDocument(string toolId)
        {
            return string.Equals(toolId, BindDataToolId,
                    StringComparison.Ordinal) ||
                string.Equals(toolId, RefreshDataToolId,
                    StringComparison.Ordinal);
        }

        internal static IEnumerable<ToolCatalogEntry> GetTools(
            HtmlWorkspaceToolService service)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));
            yield return Projection(InspectWorkspaceToolId,
                "Read-only: Run bounded static preflight diagnostics for one HTML entry and the CSS, classic scripts, and data injected into it. Does not execute JavaScript or render WebView.",
                HtmlWorkspaceToolService.InspectWorkspaceSchema(),
                "html_workspace_inspect", false, 0);
            yield return Projection(UpsertToolId,
                "Workspace: Write the complete content of one file or JSON data source. File kind is inferred from its extension; default upsert creates or updates, while strict modes can require one state.",
                HtmlWorkspaceToolService.UpsertWorkspaceSchema(),
                "html_workspace_upsert", true, 0);
            yield return Projection(ApplyPatchToolId,
                "Workspace: Apply ordered structured text edits atomically to one existing HTML/CSS/JavaScript file. Runtime reads current source and records one recoverable workspace revision.",
                HtmlWorkspaceToolService.ApplyPatchSchema(),
                "html_workspace_apply_patch", true, 0);
            yield return Projection(DeleteToolId,
                "Workspace: Delete one exact file or JSON data source. Workspace history keeps the operation recoverable.",
                "{\"type\":\"object\",\"properties\":{\"resourceType\":{\"type\":\"string\",\"enum\":[\"file\",\"data\"],\"description\":\"Resource to delete: file or data.\"},\"name\":{\"type\":\"string\",\"description\":\"Exact workspace-relative file path or data-source name.\",\"maxLength\":260}},\"required\":[\"resourceType\",\"name\"],\"additionalProperties\":false}",
                "html_workspace_delete", true, 1);
            yield return Projection(SetActiveToolId,
                "Workspace: Select the active HTML file displayed on the HTML tab for the active chat.",
                "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"Exact workspace-relative HTML file path.\",\"default\":\"index.html\",\"maxLength\":260}},\"required\":[],\"additionalProperties\":false}",
                "html_workspace_set_active", true, 0);
            if (service.HasDataSourceTools)
            {
                yield return Projection(BindDataToolId,
                    service.BuildBindDescription(), service.BuildBindSchema(),
                    "html_data_bind", true, 0);
            }
            yield return Projection(RefreshDataToolId,
                "Workspace: Re-run a bound read-only Office source and replace its JSON without another model request. Omit name to refresh all matching bound sources.",
                "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"Optional exact bound data-source name; omit to refresh all matching sources.\",\"maxLength\":128},\"policy\":{\"type\":\"string\",\"enum\":[\"all\",\"on_preview\"],\"description\":\"Refresh all bound sources or only sources configured for preview refresh.\",\"default\":\"all\"}},\"required\":[],\"additionalProperties\":false}",
                "html_data_refresh", true, 0);
            yield return Projection(FreezeDataToolId,
                "Workspace: Keep the current JSON of one bound data source but remove its Office binding so future refreshes cannot overwrite it.",
                "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"Exact bound data-source name.\",\"maxLength\":128}},\"required\":[\"name\"],\"additionalProperties\":false}",
                "html_data_freeze", true, 0);
        }

        private static ToolCatalogEntry Projection(
            string id, string description, string schema, string name,
            bool mutation, int riskLevel)
        {
            var policy = mutation
                ? new ToolPolicy(ToolEffect.Write, ToolVerification.Tool,
                    false, false, new[] { "agent" }, riskLevel)
                : new ToolPolicy(ToolEffect.Read, ToolVerification.None,
                    false, true, new[] { "agent" }, riskLevel);
            return ControllerToolCatalogEntry.CreateTypedProjection(
                new ToolDescriptor(id, description, schema), policy,
                name: name, scope: "session",
                mutatesLocalState: mutation);
        }
    }
}
