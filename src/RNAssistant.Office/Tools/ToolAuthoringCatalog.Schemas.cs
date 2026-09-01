using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    internal static partial class ToolAuthoringCatalog
    {
        private static string OptionalIdSchema()
        {
            return "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\",\"description\":\"Exact custom tool id; omit to list compact metadata.\"}},\"required\":[],\"additionalProperties\":false}";
        }

        private static string ToolPayloadSchema(bool update)
        {
            var properties = new JObject
            {
                ["id"] = BoundedStringProperty("Exact stable custom tool id; it cannot shadow a built-in id.", 128),
                ["host"] = EnumProperty("Office host where the tool is available.", "Common", "Excel", "Word", "PowerPoint", "Outlook"),
                ["name"] = BoundedStringProperty("Human-readable tool name.", 200),
                ["description"] = BoundedStringProperty("Clear model-facing description of what the tool does.", 8000),
                ["parameters"] = ParametersProperty(),
                ["parameterDefinitions"] = ParameterDefinitionsProperty(),
                ["executor"] = EnumProperty("Execution type.", "vba"),
                ["components"] = new JObject
                {
                    ["type"] = "array",
                    ["description"] = "Ordered VBA package source components; the first component is the StdModule containing the manifest and entry function. MSForm means a blank code-only UserForm, never an exported Designer form.",
                    ["maxItems"] = 50,
                    ["items"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["name"] = Property("string", "Exact VBA component name."),
                            ["type"] = EnumProperty("VBA component type.", "StdModule", "ClassModule", "MSForm"),
                            ["fileName"] = Property("string", "Optional display source name; storage derives .bas, .cls, or .form.vba from the component type."),
                            ["code"] = BoundedStringProperty("Complete VBA source code for this component.", 1000000)
                        },
                        ["required"] = new JArray("name", "type", "code"),
                        ["additionalProperties"] = false
                    }
                },
                ["readme"] = BoundedStringProperty("Markdown documentation stored with the custom tool.", 500000),
                ["enabled"] = Property("boolean", "Whether the tool is enabled."),
                ["requiresConfirmation"] = Property("boolean", "Whether execution requires explicit user confirmation."),
                ["mutatesDocument"] = Property("boolean", "Whether execution may change the Office document."),
                ["mutatesLocalState"] = Property("boolean", "Whether execution may change RNAssistant local state."),
                ["agentCanRun"] = Property("boolean", "Whether Agent mode may select this tool."),
                ["riskLevel"] = new JObject
                {
                    ["type"] = "integer",
                    ["description"] = "Risk level from 0 through 3.",
                    ["minimum"] = 0,
                    ["maximum"] = 3
                },
                ["useWhen"] = BoundedStringProperty("Positive selection guidance for the model.", 4000),
                ["doNotUseWhen"] = BoundedStringProperty("Cases where the model should not select this tool.", 4000),
                ["capabilityStatus"] = EnumProperty("Current capability status.", "available", "partial", "unavailable"),
                ["limitations"] = BoundedStringProperty("Known limitations presented to the model.", 4000)
            };
            if (!update)
            {
                properties["enabled"]["default"] = true;
                properties["requiresConfirmation"]["default"] = false;
                properties["mutatesDocument"]["default"] = false;
                properties["mutatesLocalState"]["default"] = false;
                properties["agentCanRun"]["default"] = true;
                properties["riskLevel"]["default"] = 0;
                properties["capabilityStatus"]["default"] = "available";
            }
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = update
                    ? new JArray("id")
                    : new JArray("id", "host", "description", "executor"),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }

        private static string ToolUpsertSchema()
        {
            var schema = JObject.Parse(ToolPayloadSchema(true));
            ((JObject)schema["properties"])["mode"] = new JObject
            {
                ["type"] = "string",
                ["description"] = "Existence policy; upsert is normally sufficient.",
                ["enum"] = new JArray("upsert", "createOnly", "updateOnly"),
                ["default"] = "upsert"
            };
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

        private static JObject ParametersProperty()
        {
            return new JObject
            {
                ["type"] = "object",
                ["description"] = "Advanced native strict object JSON Schema. In strict Agent output arbitrary property names cannot be generated here, so prefer parameterDefinitions; never pass this object as a JSON string.",
                ["properties"] = new JObject
                {
                    ["type"] = new JObject { ["type"] = "string", ["description"] = "Root schema type; must be object.", ["enum"] = new JArray("object") },
                    ["properties"] = Property("object", "Named argument schemas with types and useful descriptions."),
                    ["required"] = new JObject { ["type"] = "array", ["description"] = "Names of required arguments.", ["items"] = new JObject { ["type"] = "string" } },
                    ["additionalProperties"] = new JObject { ["type"] = "boolean", ["description"] = "Must be false." }
                },
                ["required"] = new JArray("type", "properties", "required", "additionalProperties")
            };
        }

        private static JObject ParameterDefinitionsProperty()
        {
            return new JObject
            {
                ["type"] = "array",
                ["description"] = "Agent-friendly native array that builds parameters without dynamic JSON keys. Use one unique entry per scalar or scalar-array argument; omit for a no-argument tool or when advanced parameters is supplied.",
                ["maxItems"] = 100,
                ["items"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["name"] = BoundedStringProperty("Exact argument name.", 128),
                        ["type"] = EnumProperty("Argument JSON type.", "string", "integer", "number", "boolean", "array"),
                        ["description"] = BoundedStringProperty("Useful model-facing description of this argument.", 4000),
                        ["required"] = new JObject { ["type"] = "boolean", ["description"] = "Whether callers must supply this argument.", ["default"] = false },
                        ["nullable"] = new JObject { ["type"] = "boolean", ["description"] = "Whether an explicit JSON null is meaningful and must be preserved.", ["default"] = false },
                        ["itemsType"] = EnumProperty("Required scalar item type when type=array.", "string", "integer", "number", "boolean"),
                        ["enumValues"] = new JObject { ["type"] = "array", ["description"] = "Optional allowed string values when type=string.", ["items"] = new JObject { ["type"] = "string" }, ["minItems"] = 1, ["maxItems"] = 100 },
                        ["defaultString"] = BoundedStringProperty("Optional string default used only when type=string.", 100000),
                        ["defaultInteger"] = new JObject { ["type"] = "integer", ["description"] = "Optional integer default used only when type=integer." },
                        ["defaultNumber"] = new JObject { ["type"] = "number", ["description"] = "Optional numeric default used only when type=number." },
                        ["defaultBoolean"] = new JObject { ["type"] = "boolean", ["description"] = "Optional boolean default used only when type=boolean." },
                        ["minimum"] = new JObject { ["type"] = "number", ["description"] = "Optional inclusive numeric minimum." },
                        ["maximum"] = new JObject { ["type"] = "number", ["description"] = "Optional inclusive numeric maximum." },
                        ["minLength"] = new JObject { ["type"] = "integer", ["description"] = "Optional string minimum length.", ["minimum"] = 0 },
                        ["maxLength"] = new JObject { ["type"] = "integer", ["description"] = "Optional string maximum length.", ["minimum"] = 0 },
                        ["minItems"] = new JObject { ["type"] = "integer", ["description"] = "Optional array minimum item count.", ["minimum"] = 0 },
                        ["maxItems"] = new JObject { ["type"] = "integer", ["description"] = "Optional array maximum item count.", ["minimum"] = 0 }
                    },
                    ["required"] = new JArray("name", "type", "description"),
                    ["additionalProperties"] = false
                }
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
