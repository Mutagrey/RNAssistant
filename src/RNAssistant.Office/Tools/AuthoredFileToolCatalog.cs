using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Tools
{
    internal static class AuthoredFileToolCatalog
    {
        internal const string CreateToolId = "common.file_create";
        internal const string CopyToolId = "common.file_copy";
        internal const string PatchToolId = "common.file_patch";
        internal static bool Owns(string id)
        { return id == CreateToolId || id == CopyToolId || id == PatchToolId; }
        internal static ToolBinding BindingFor(string id)
        { return Owns(id) ? new ToolBinding(id + ".intent.v1") : null; }

        internal static IEnumerable<ToolCatalogEntry> GetTools()
        {
            foreach (var id in new[] { CreateToolId, CopyToolId, PatchToolId })
            {
                var properties = new JObject();
                JArray required;
                string description;
                if (id == CreateToolId)
                {
                    properties["name"] = new JObject { ["type"] = "string", ["minLength"] = 3, ["maxLength"] = 200,
                        ["description"] = "File name with the desired code/text extension, not a path." };
                    properties["content"] = new JObject { ["type"] = "string", ["maxLength"] = AuthoredFileService.MaximumInlineCharacters,
                        ["description"] = "Complete small-file content. Copy an uploaded original to create a large file." };
                    required = new JArray("name", "content");
                    description = "Create a small document-owned text/code file with its real extension. This is a downloadable file, not an HTML workspace page or managed Markdown document artifact; use html_workspace_write_file or markdown_save for those destinations. For a large uploaded file, use file_copy.";
                }
                else if (id == CopyToolId)
                {
                    properties["source"] = new JObject { ["type"] = "string", ["minLength"] = 1,
                        ["description"] = "Exact semantic target from resources_find for a complete uploaded text/code original or authored file. Never use a URI." };
                    properties["name"] = new JObject { ["type"] = "string", ["minLength"] = 3, ["maxLength"] = 200,
                        ["description"] = "Optional output file name with desired extension, for example large.md. Omit to preserve the source name. Bytes are copied exactly." };
                    required = new JArray("source");
                    description = "Create an independent editable document-owned file from a complete text/code original, not an HTML workspace page. Optionally choose an output name and extension. Encoding, BOM, line endings and bytes remain exact; the original stays immutable.";
                }
                else
                {
                    properties["target"] = new JObject { ["type"] = "string", ["minLength"] = 1,
                        ["description"] = "Current authored file semantic target from resources_find. Never use a URI or an uploaded original." };
                    properties["patches"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 16,
                        ["description"] = "One to sixteen exact unique replacements; combined find and replace text must be at most 32,000 characters.",
                        ["items"] = new JObject { ["type"] = "object", ["properties"] = new JObject {
                            ["find"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = AuthoredFileService.MaximumInlineCharacters },
                            ["replace"] = new JObject { ["type"] = "string", ["maxLength"] = AuthoredFileService.MaximumInlineCharacters } },
                            ["required"] = new JArray("find", "replace"), ["additionalProperties"] = false } };
                    required = new JArray("target", "patches");
                    description = "Apply bounded exact, unique, non-overlapping text replacements to one pinned document-owned authored file, not an HTML workspace member. Read the relevant parts first. Stale or ambiguous edits fail without replacing the file.";
                }
                var schema = new JObject { ["type"] = "object", ["properties"] = properties,
                    ["required"] = required, ["additionalProperties"] = false };
                yield return ControllerToolCatalogEntry.CreateTypedProjection(
                    new ToolDescriptor(id, description, schema.ToString(Newtonsoft.Json.Formatting.None)),
                    new ToolPolicy(ToolEffect.Write, ToolVerification.Tool, false, false, new[] { "agent" }, 0),
                    BindingFor(id), name: id.Substring("common.".Length), scope: "session", mutatesLocalState: true);
            }
        }
    }
}
