using Newtonsoft.Json.Linq;

namespace RNAssistant.Core.Services
{
    // One Office discovery contract; the workspace route narrows it to the
    // document pinned to that run and adds only its type discriminator.
    public static class ResourceModelFindSchema
    {
        public static JObject Office()
        {
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["query"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Optional literal text from the resource name, metadata, or readable body. Omit to browse the selected semantic scope.",
                        ["minLength"] = 1,
                        ["maxLength"] = 500
                    },
                    ["scope"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Optional user-level resource category. Catalog bodies are historical resources, not callable admission.",
                        ["enum"] = new JArray("all", "conversation", "document", "selection", "html", "vba", "backups", "catalogs"),
                        ["default"] = "all"
                    }
                },
                ["required"] = new JArray(),
                ["additionalProperties"] = false
            };
        }

        public static JObject BoundOffice()
        {
            var schema = Office();
            var properties = (JObject)schema["properties"];
            var scope = (JObject)properties["scope"];
            scope["enum"] = new JArray("document", "selection", "vba");
            scope["default"] = "document";
            properties["type"] = new JObject { ["type"] = "string", ["enum"] = new JArray("office"),
                ["description"] = "Find resources in this run's exact Office document." };
            ((JArray)schema["required"]).Add("type");
            return schema;
        }
    }
}
