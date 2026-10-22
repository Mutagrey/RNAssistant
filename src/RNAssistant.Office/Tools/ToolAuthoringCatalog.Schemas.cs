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

        private static string ToolUpsertSchema(bool enableJavaScript)
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
                ["components"] = new JObject
                {
                    ["type"] = "array",
                    ["description"] = "Complete ordered VBA package sources. Required for create and when implementation changes; the first component is the StdModule containing the manifest and entry function. Manifest parameters must be a JSON Schema object shaped as {type:'object',properties:{...},required:[...],additionalProperties:false}. Do not duplicate the component list inside the manifest: runtime derives it from this exact order.",
                    ["maxItems"] = 50,
                    ["items"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["name"] = Property("string", "Exact VBA component name."),
                            ["type"] = EnumProperty("VBA component type.", "StdModule", "ClassModule", "MSForm"),
                            ["code"] = BoundedStringProperty("Complete VBA source code for this component.", int.MaxValue)
                        },
                        ["required"] = new JArray("name", "type", "code"),
                        ["additionalProperties"] = false
                    }
                },
                ["display"] = new JObject
                {
                    ["type"] = "object",
                    ["description"] = "Optional user-facing Russian action captions and semantic target selectors. UI only: never status, verification, runtime guards or model result text. Omit on update to preserve; use an empty targetArguments list to clear selectors.",
                    ["properties"] = new JObject
                    {
                        ["action"] = BoundedStringProperty("Russian action label, e.g. Пересчёт отчёта.", 200),
                        ["runningAction"] = BoundedStringProperty("Russian in-progress label, e.g. Пересчитываю отчёт.", 200),
                        ["operation"] = EnumProperty("Icon category only; never execution policy.", "Command", "Read", "Search", "Write", "Delete", "Export", "Learn", "Question", "Plan", "Chart", "Package", "Check"),
                        ["targetArguments"] = new JObject { ["type"] = "array", ["maxItems"] = 8,
                            ["description"] = "Ordered exact top-level scalar argument names from this tool's schema. Select only the semantic target, not full content, secrets or protocol metadata.",
                            ["items"] = BoundedStringProperty("Exact scalar argument name.", 64) }
                    },
                    ["required"] = new JArray("action"),
                    ["additionalProperties"] = false
                },
                ["readme"] = BoundedStringProperty("Markdown documentation stored with the custom tool.", int.MaxValue),
                ["useWhen"] = BoundedStringProperty("Positive selection guidance for the model.", 4000),
                ["doNotUseWhen"] = BoundedStringProperty("Cases where the model should not select this tool.", 4000),
                ["limitations"] = BoundedStringProperty("Known limitations presented to the model.", 4000)
            };
            var vbaProperties = (JObject)properties.DeepClone();
            if (enableJavaScript)
            {
                properties["name"] = BoundedStringProperty("JavaScript tool name. VBA name belongs in the entry component manifest.", 200);
                properties["description"] = BoundedStringProperty("JavaScript tool discovery description. VBA description belongs in the entry component manifest.", 8000);
                properties["executor"] = EnumProperty("Custom tool runtime.", "vba", "js");
                properties["host"] = EnumProperty("Tool host for JavaScript packages; VBA host comes from its manifest.", "Common", "Excel", "Word", "PowerPoint", "Outlook");
                properties["parameters"] = BoundedStringProperty("Strict object JSON Schema serialized as a string for saved JS tool arguments.", 64000);
                properties["code"] = BoundedStringProperty("Complete JS async function body when executor=js.", int.MaxValue);
            }
            var schema = new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JArray("id"),
                ["additionalProperties"] = false
            };
            if (enableJavaScript)
            {
                vbaProperties["executor"] = EnumProperty("VBA package runtime; optional for metadata-only updates.", "vba");
                var jsProperties = (JObject)properties.DeepClone();
                jsProperties.Remove("components");
                jsProperties["executor"] = EnumProperty("Required when supplying JavaScript-specific fields.", "js");
                schema["anyOf"] = new JArray(
                    new JObject { ["type"] = "object", ["properties"] = vbaProperties,
                        ["required"] = new JArray("id"), ["additionalProperties"] = false },
                    new JObject { ["type"] = "object", ["properties"] = jsProperties,
                        ["required"] = new JArray("id", "executor"), ["additionalProperties"] = false });
            }
            return schema.ToString(Formatting.None);
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
