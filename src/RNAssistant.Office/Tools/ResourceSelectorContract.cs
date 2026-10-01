using System;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Tools
{
    // Shared model-facing selection contract; provider references stay in the gateway.
    internal static class ResourceSelectorContract
    {
        internal static JObject Target()
        { return new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 1024,
            ["description"] = "Copy a semantic target verbatim from runtime context, discovery, a workspace member or binding sourceTarget. Never construct it from a title or pass a URI. HTML data names describe bindings; sourceTarget identifies their values." }; }

        internal static JObject RecordPath()
        { return new JObject { ["type"] = "string", ["maxLength"] = 256, ["pattern"] = @"^\$(?:\.[A-Za-z_][A-Za-z0-9_]*)*$",
            ["description"] = "Optional JSON record-array path. Omit for Office targets: runtime selects the canonical view. For JSON use $ or a declared property such as $.records. This is not a page index, file path or worksheet address." }; }

        internal static JObject NamedInput(bool binding)
        {
            var properties = new JObject {
                ["name"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 128,
                    ["description"] = "Local name passed to RN.resources.open(name)." },
                ["target"] = Target(),
                ["view"] = new JObject { ["type"] = "string",
                    ["description"] = "Choose an advertised source view explicitly. records/table both return rows and columns; text/source return text chunks. raw is inert binary. Page views require pageIndex.",
                    ["enum"] = new JArray("text", "source", "table", "records", "raw", "image", "thumbnail", "render-page", "page-thumbnail") },
                ["path"] = RecordPath(),
                ["pageIndex"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 999999,
                    ["description"] = "Zero-based page number, only for render-page/page-thumbnail." }
            };
            if (binding) properties["policy"] = new JObject { ["type"] = "string", ["enum"] = new JArray("head", "exact"),
                ["default"] = "head", ["description"] = "head opens current data (default); exact freezes the observed revision." };
            Func<string[], string, JObject> branch = (views, selector) => {
                var allowed = new JObject();
                foreach (var key in new[] { "name", "target", "view", "policy", selector })
                    if (key != null && properties[key] != null) allowed[key] = properties[key].DeepClone();
                allowed["view"]["enum"] = new JArray(views);
                var required = new JArray("name", "target", "view");
                if (selector == "pageIndex") required.Add(selector);
                return new JObject { ["type"] = "object", ["properties"] = allowed,
                    ["required"] = required, ["additionalProperties"] = false };
            };
            return new JObject { ["type"] = "object", ["properties"] = properties,
                ["required"] = new JArray("name", "target", "view"), ["additionalProperties"] = false,
                ["anyOf"] = new JArray(branch(new[] { "text", "source", "raw", "image", "thumbnail" }, null),
                    branch(new[] { "table", "records" }, "path"), branch(new[] { "render-page", "page-thumbnail" }, "pageIndex")) };
        }

        internal static string ResolvePath(ResourceIntentTarget target, string view, string path, int? pageIndex = null)
        {
            if (view == "table" || view == "records")
            {
                if (pageIndex.HasValue) throw Invalid("pageIndex is only valid for page views.");
                return ResourceGatewayService.ResolveStructuralViewPath(target, path);
            }
            if (view == "render-page" || view == "page-thumbnail")
            {
                if (path != null || !pageIndex.HasValue || pageIndex < 0 || pageIndex > 999999)
                    throw Invalid("Page views require pageIndex, a zero-based integer. Omit path.");
                return pageIndex.Value.ToString(CultureInfo.InvariantCulture);
            }
            if (path != null || pageIndex.HasValue) throw Invalid("This view has no path or pageIndex selector.");
            return null;
        }

        private static ResourceRequestException Invalid(string message)
        { return new ResourceRequestException(message, "RESOURCE_VIEW_PATH_MISMATCH", false); }
    }
}
