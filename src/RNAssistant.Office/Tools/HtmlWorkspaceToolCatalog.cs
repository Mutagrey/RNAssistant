using System;
using System.Collections.Generic;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    internal static class HtmlWorkspaceToolCatalog
    {
        internal const string WriteFileToolId =
            "common.html_workspace_write_file";
        internal const string WriteDataToolId =
            "common.html_data_write";
        internal const string ApplyPatchToolId =
            "common.html_workspace_apply_patch";
        internal const string DeleteToolId =
            "common.html_workspace_delete";
        internal const string BindDataToolId = "common.html_data_bind";
        internal const string RefreshDataToolId = "common.html_data_refresh";
        internal const string FreezeDataToolId = "common.html_data_freeze";

        internal static bool Owns(string toolId)
        {
            return string.Equals(toolId, WriteFileToolId,
                    StringComparison.Ordinal) ||
                string.Equals(toolId, WriteDataToolId, StringComparison.Ordinal) ||
                string.Equals(toolId, ApplyPatchToolId,
                    StringComparison.Ordinal) ||
                string.Equals(toolId, DeleteToolId, StringComparison.Ordinal) ||
                string.Equals(toolId, BindDataToolId, StringComparison.Ordinal) ||
                string.Equals(toolId, RefreshDataToolId,
                    StringComparison.Ordinal) ||
                string.Equals(toolId, FreezeDataToolId,
                    StringComparison.Ordinal);
        }

        internal static bool IsMutation(string toolId)
        {
            return Owns(toolId);
        }

        internal static bool RequiresOfficeDocument(string toolId)
        {
            return false; // The gateway negotiates and guards each bound provider.
        }

        internal static IEnumerable<ToolCatalogEntry> GetTools(
            HtmlWorkspaceToolService service)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));
            yield return Projection(WriteFileToolId,
                "Workspace: Create or replace one complete HTML, CSS, or classic JavaScript file using root arguments path and content only. Runtime infers the kind, auto-injects workspace CSS/JS into the selected HTML entry, provides bundled ECharts when global echarts is referenced, and runs bounded static preflight. Do not add local link/script src tags or copy vendor bundles.",
                HtmlWorkspaceToolService.WriteFileSchema(),
                "html_workspace_write_file", true, 0);
            yield return Projection(WriteDataToolId,
                "Workspace: Create or replace one named JSON data source and run bounded static preflight automatically.",
                HtmlWorkspaceToolService.WriteDataSchema(),
                "html_data_write", true, 0);
            yield return Projection(ApplyPatchToolId,
                "Workspace: Apply ordered structured text edits atomically to one existing HTML/CSS/JavaScript file. Runtime reads current source and records one recoverable workspace revision.",
                HtmlWorkspaceToolService.ApplyPatchSchema(),
                "html_workspace_apply_patch", true, 0);
            yield return Projection(DeleteToolId,
                "Workspace: Delete one exact file path or named JSON data source. The runtime resolves its kind and rejects ambiguous targets; workspace history keeps the operation recoverable.",
                DeleteSchema(),
                "html_workspace_delete", true, 1);
            if (service.HasDataSourceTools)
            {
                yield return Projection(BindDataToolId,
                    service.BuildBindDescription(), HtmlWorkspaceToolService.BindSchema(),
                    "html_data_bind", true, 0);
            }
            yield return Projection(RefreshDataToolId,
                "Workspace: Resolve resource bindings through canonical authority. Omit name to resolve all; reopen RN.resources handles to consume current head-bound revisions.",
                RefreshSchema(),
                "html_data_refresh", true, 0);
            yield return Projection(FreezeDataToolId,
                "Workspace: Change a head binding to an exact canonical revision. Future source changes do not change this snapshot binding.",
                FreezeSchema(),
                "html_data_freeze", true, 0);
        }

        internal static string DeleteSchema()
        {
            return "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\",\"description\":\"Exact workspace-relative file path or data-source name.\",\"minLength\":1,\"maxLength\":260}},\"required\":[\"target\"],\"additionalProperties\":false}";
        }

        internal static string RefreshSchema()
        {
            return "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"Optional exact bound data-source name; omit to refresh all bound sources.\",\"minLength\":1,\"maxLength\":128}},\"required\":[],\"additionalProperties\":false}";
        }

        internal static string FreezeSchema()
        {
            return "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"Exact bound data-source name.\",\"minLength\":1,\"maxLength\":128}},\"required\":[\"name\"],\"additionalProperties\":false}";
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
