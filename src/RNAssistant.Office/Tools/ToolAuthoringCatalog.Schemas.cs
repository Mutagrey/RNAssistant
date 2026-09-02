using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Office.Tools
{
    internal static partial class ToolAuthoringCatalog
    {
        private static string ExactIdSchema()
        {
            return "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\",\"description\":\"Exact stable custom tool id.\",\"minLength\":1,\"maxLength\":128}},\"required\":[\"id\"],\"additionalProperties\":false}";
        }

        private static string ToolUpsertSchema()
        {
            var properties = new JObject
            {
                ["id"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Exact stable custom tool id; it cannot shadow a built-in id.",
                    ["minLength"] = 1,
                    ["maxLength"] = 128
                },
                ["mode"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Existence policy; upsert is normally sufficient.",
                    ["enum"] = new JArray("upsert", "createOnly", "updateOnly"),
                    ["default"] = "upsert"
                },
                ["components"] = new JObject
                {
                    ["type"] = "array",
                    ["description"] = "Complete ordered VBA package sources. Required for create and when implementation changes; the first component is the StdModule containing the manifest and entry function.",
                    ["maxItems"] = 50,
                    ["items"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["name"] = Property("string", "Exact VBA component name."),
                            ["type"] = EnumProperty("VBA component type.", "StdModule", "ClassModule", "MSForm"),
                            ["code"] = BoundedStringProperty("Complete VBA source code for this component.", 1000000)
                        },
                        ["required"] = new JArray("name", "type", "code"),
                        ["additionalProperties"] = false
                    }
                },
                ["readme"] = BoundedStringProperty("Markdown documentation stored with the custom tool.", 500000),
                ["useWhen"] = BoundedStringProperty("Positive selection guidance for the model.", 4000),
                ["doNotUseWhen"] = BoundedStringProperty("Cases where the model should not select this tool.", 4000),
                ["limitations"] = BoundedStringProperty("Known limitations presented to the model.", 4000)
            };
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JArray("id"),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }

        private static JObject Property(string type, string description)
        {
            return new JObject { ["type"] = type, ["description"] = description };
        }

        private static JObject BoundedStringProperty(string description, int maxLength)
        {
            return new JObject
            {
                ["type"] = "string",
                ["description"] = description,
                ["maxLength"] = maxLength
            };
        }

        private static JObject EnumProperty(string description, params string[] values)
        {
            return new JObject
            {
                ["type"] = "string",
                ["description"] = description,
                ["enum"] = new JArray(values ?? new string[0])
            };
        }
    }
}
