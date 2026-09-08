using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Tools
{
    internal static class MarkdownDocumentToolCatalog
    {
        internal const string SaveToolId = "common.markdown_save";
        internal const string RestoreToolId = "common.markdown_restore";
        internal static bool Owns(string id) { return id == SaveToolId || id == RestoreToolId; }
        internal static ToolBinding BindingFor(string id) { return Owns(id) ? new ToolBinding(id + ".intent.v1") : null; }
        internal static IEnumerable<ToolCatalogEntry> GetTools()
        {
            foreach (var id in new[] { SaveToolId, RestoreToolId })
            {
                var save = id == SaveToolId;
                var properties = new JObject {
                    ["target"] = new JObject { ["type"] = "string", ["minLength"] = 1,
                        ["description"] = "Copy the complete Markdown target from common.resources_find or RUNTIME_CONTEXT. Omit only to create an independent document; never use a URI or invent a target." } };
                if (save)
                {
                    properties["title"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 200, ["description"] = "User-visible document title, including a .md suffix if useful." };
                    properties["description"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 1200,
                        ["description"] = "Concise purpose, covered topics and intended use, grounded in this document. Discovery aid, not evidence of its contents." };
                    properties["markdown"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = MarkdownDocumentService.MaximumCharacters,
                        ["description"] = "Complete Markdown body. Preserve all text and formatting; this replaces the selected revision, not an append or patch." };
                }
                else properties["version"] = new JObject { ["type"] = "integer", ["minimum"] = 1,
                    ["description"] = "Historical version to restore as a new revision of the explicit target." };
                var schema = new JObject { ["type"] = "object", ["properties"] = properties,
                    ["required"] = save ? new JArray("title", "description", "markdown") : new JArray("target", "version"), ["additionalProperties"] = false };
                yield return ControllerToolCatalogEntry.CreateTypedProjection(
                    new ToolDescriptor(id, save
                        ? "Create or revise a standalone document-owned Markdown artifact (documentation, report or specification) only when the user requests a document. Omit target to create; supply an exact discovered target to edit. Multiple documents coexist across chats. Ordinary Markdown answers remain messages. Read an existing document before replacing its complete body; stale targets are rejected."
                        : "On explicit user request, restore a historical Markdown version as a new causal revision. Other chats and historical messages retain exact references.", schema.ToString(Formatting.None)),
                    new ToolPolicy(ToolEffect.Write, ToolVerification.Tool, false, false, new[] { "agent" }, 0),
                    name: save ? "markdown_save" : "markdown_restore", scope: "session", mutatesLocalState: true);
            }
        }
    }
}
