using System;
using System.Globalization;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Tools
{
    // Office named-input schema and view-path resolver; target/path shapes live in Core.
    internal static class ResourceSelectorContract
    {
        internal static JObject NamedInput(bool binding)
        {
            var properties = new JObject {
                ["name"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 128,
                    ["description"] = "Local name passed to RN.resources.open(name)." },
                ["target"] = ResourceModelReadSchema.SemanticTarget(),
                ["view"] = new JObject { ["type"] = "string",
                    ["description"] = "Choose an advertised source view explicitly. table returns rows and columns; text/source return text chunks. raw is inert binary. Page views require pageIndex.",
                    ["enum"] = new JArray("text", "source", "table", "raw", "image", "thumbnail", "render-page", "page-thumbnail") },
                ["path"] = ResourceModelReadSchema.RecordPath(),
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
                    branch(new[] { "table" }, "path"), branch(new[] { "render-page", "page-thumbnail" }, "pageIndex")) };
        }

        internal static string ResolvePath(ResourceIntentTarget target, string view, string path, int? pageIndex = null)
        {
            // Historical custom JS inputs can still select records through this internal resolver.
            // Built-in model reads and new HTML bindings reject records before calling it.
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
