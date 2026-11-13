using Newtonsoft.Json.Linq;

namespace RNAssistant.Runtime
{
    internal static class BrowserResourceSchema
    {
        internal static JObject Create(bool find)
        {
            var properties = new JObject { ["type"] = new JObject { ["type"] = "string", ["enum"] = new JArray("browser") } };
            var required = new JArray("type");
            properties[find ? "query" : "target"] = new JObject { ["type"] = "string", ["minLength"] = find ? 0 : 1, ["maxLength"] = find ? 500 : 1000 };
            if (!find)
            {
                required.Add("target");
                properties["representation"] = new JObject { ["type"] = "string", ["enum"] = new JArray("text", "media") };
            }
            return new JObject { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false };
        }
    }
}
