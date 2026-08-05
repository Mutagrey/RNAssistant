using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Tools
{
    public static class ToolSchemaSupport
    {
        public static bool TryNormalize(ToolDefinition tool, out JObject schema, out string error)
        {
            schema = null;
            error = null;
            if (tool == null)
            {
                error = "Tool definition is null.";
                return false;
            }

            JObject parsed;
            try
            {
                parsed = JObject.Parse(string.IsNullOrWhiteSpace(tool.ArgumentSchemaJson) ? "{}" : tool.ArgumentSchemaJson);
            }
            catch (JsonException ex)
            {
                error = "Invalid argumentSchemaJson: " + ex.Message;
                return false;
            }

            if (!parsed.Properties().Any())
            {
                error = "argumentSchemaJson must be a formal JSON Schema object with type=object and properties.";
                return false;
            }

            if (string.Equals((string)parsed["type"], "object", StringComparison.OrdinalIgnoreCase) && parsed["properties"] is JObject)
            {
                schema = (JObject)parsed.DeepClone();
                schema["type"] = "object";
                if (schema["additionalProperties"] == null)
                {
                    schema["additionalProperties"] = false;
                }
                if (!(schema["required"] is JArray))
                {
                    schema["required"] = new JArray();
                }
                return true;
            }

            error = "argumentSchemaJson must be a formal JSON Schema object with type=object and properties.";
            return false;
        }

        public static string FromPropertySamples(string json)
        {
            var parsed = JObject.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (string.Equals((string)parsed["type"], "object", StringComparison.OrdinalIgnoreCase) && parsed["properties"] is JObject)
            {
                return parsed.ToString(Formatting.None);
            }
            var properties = new JObject();
            foreach (var property in parsed.Properties())
            {
                properties[property.Name] = InferSchema(property.Value);
            }
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JArray(),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }

        public static JObject ForStructuredOutput(JObject schema)
        {
            var clone = schema == null ? EmptyObjectSchema() : (JObject)schema.DeepClone();
            MakeObjectSchemasStrict(clone);
            return clone;
        }

        public static bool ValidateArguments(JObject arguments, JObject schema, bool applyDefaults, out string error)
        {
            arguments = arguments ?? new JObject();
            schema = schema ?? EmptyObjectSchema();
            return ValidateValue(arguments, schema, "$", applyDefaults, out error);
        }

        public static IReadOnlyList<LlmToolDefinition> BuildApiTools(IEnumerable<ToolDefinition> tools)
        {
            var result = new List<LlmToolDefinition>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tool in tools ?? new ToolDefinition[0])
            {
                JObject schema;
                string error;
                if (tool == null || !ToolIsAvailable(tool) || !TryNormalize(tool, out schema, out error))
                {
                    continue;
                }
                var apiName = ApiName(tool.Id, names);
                names.Add(apiName);
                result.Add(new LlmToolDefinition
                {
                    ToolId = tool.Id,
                    ApiName = apiName,
                    Description = Trim(tool.Description, 900),
                    ParametersSchemaJson = ForStructuredOutput(schema).ToString(Formatting.None)
                });
            }
            return result;
        }

        public static IReadOnlyList<LlmToolDefinition> BuildApiToolNames(IEnumerable<ToolDefinition> tools)
        {
            var result = new List<LlmToolDefinition>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tool in tools ?? new ToolDefinition[0])
            {
                if (tool == null || !ToolIsAvailable(tool)) continue;
                var apiName = ApiName(tool.Id, names);
                names.Add(apiName);
                result.Add(new LlmToolDefinition { ToolId = tool.Id, ApiName = apiName });
            }
            return result;
        }

        public static string ResolveToolId(string apiName, IEnumerable<LlmToolDefinition> tools)
        {
            var match = (tools ?? new LlmToolDefinition[0]).FirstOrDefault(tool =>
                tool != null && string.Equals(tool.ApiName, apiName, StringComparison.OrdinalIgnoreCase));
            return match == null ? null : match.ToolId;
        }

        private static bool ToolIsAvailable(ToolDefinition tool)
        {
            return tool.Enabled &&
                (string.IsNullOrWhiteSpace(tool.CapabilityStatus) ||
                 string.Equals(tool.CapabilityStatus, "available", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(tool.CapabilityStatus, "partial", StringComparison.OrdinalIgnoreCase));
        }

        private static JObject EmptyObjectSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject(),
                ["required"] = new JArray(),
                ["additionalProperties"] = false
            };
        }

        private static JObject InferSchema(JToken sample)
        {
            if (sample == null || sample.Type == JTokenType.Null)
            {
                return new JObject { ["type"] = new JArray("string", "null") };
            }
            switch (sample.Type)
            {
                case JTokenType.Boolean:
                    return new JObject { ["type"] = "boolean" };
                case JTokenType.Integer:
                    return new JObject { ["type"] = "integer" };
                case JTokenType.Float:
                    return new JObject { ["type"] = "number" };
                case JTokenType.Array:
                    return new JObject { ["type"] = "array", ["items"] = new JObject() };
                case JTokenType.Object:
                    return new JObject { ["type"] = "object", ["additionalProperties"] = true };
                default:
                    return new JObject { ["type"] = "string" };
            }
        }

        private static void MakeObjectSchemasStrict(JToken token)
        {
            var obj = token as JObject;
            if (obj != null)
            {
                if (string.Equals((string)obj["type"], "object", StringComparison.OrdinalIgnoreCase))
                {
                    var properties = obj["properties"] as JObject ?? new JObject();
                    obj["properties"] = properties;
                    obj["required"] = new JArray(properties.Properties().Select(property => property.Name));
                    obj["additionalProperties"] = false;
                }
                foreach (var property in obj.Properties().ToList())
                {
                    MakeObjectSchemasStrict(property.Value);
                }
                return;
            }
            var array = token as JArray;
            if (array != null)
            {
                foreach (var item in array) MakeObjectSchemasStrict(item);
            }
        }

        private static bool ValidateValue(JToken value, JObject schema, string path, bool applyDefaults, out string error)
        {
            error = null;
            var anyOf = schema["anyOf"] as JArray;
            if (anyOf != null)
            {
                foreach (var candidate in anyOf.OfType<JObject>())
                {
                    string ignored;
                    var clone = value == null ? null : value.DeepClone();
                    if (ValidateValue(clone, candidate, path, false, out ignored)) return true;
                }
                error = path + " does not match any allowed schema.";
                return false;
            }

            if (!MatchesType(value, schema["type"]))
            {
                error = path + " has the wrong JSON type.";
                return false;
            }

            var enumValues = schema["enum"] as JArray;
            if (enumValues != null && !enumValues.Any(item => JToken.DeepEquals(item, value)))
            {
                error = path + " is not one of the allowed values.";
                return false;
            }

            var obj = value as JObject;
            if (obj != null)
            {
                var properties = schema["properties"] as JObject ?? new JObject();
                if (applyDefaults)
                {
                    foreach (var property in properties.Properties())
                    {
                        var propertySchema = property.Value as JObject;
                        if (obj[property.Name] == null && propertySchema != null && propertySchema["default"] != null)
                        {
                            obj[property.Name] = propertySchema["default"].DeepClone();
                        }
                    }
                }
                foreach (var required in (schema["required"] as JArray ?? new JArray()).Values<string>())
                {
                    if (obj[required] == null)
                    {
                        error = path + "." + required + " is required.";
                        return false;
                    }
                }
                if (schema["additionalProperties"] != null && schema["additionalProperties"].Type == JTokenType.Boolean && !schema["additionalProperties"].Value<bool>())
                {
                    var unknown = obj.Properties().FirstOrDefault(property => properties[property.Name] == null);
                    if (unknown != null)
                    {
                        error = path + " contains unsupported property " + unknown.Name + ".";
                        return false;
                    }
                }
                foreach (var property in obj.Properties())
                {
                    var childSchema = properties[property.Name] as JObject;
                    if (childSchema == null) continue;
                    if (!ValidateValue(property.Value, childSchema, path + "." + property.Name, applyDefaults, out error)) return false;
                }
            }
            return true;
        }

        private static bool MatchesType(JToken value, JToken typeToken)
        {
            if (typeToken == null) return true;
            var types = typeToken.Type == JTokenType.Array
                ? ((JArray)typeToken).Values<string>()
                : new[] { (string)typeToken };
            foreach (var type in types)
            {
                if (string.Equals(type, "null", StringComparison.OrdinalIgnoreCase) && (value == null || value.Type == JTokenType.Null)) return true;
                if (value == null) continue;
                if (string.Equals(type, "object", StringComparison.OrdinalIgnoreCase) && value.Type == JTokenType.Object) return true;
                if (string.Equals(type, "array", StringComparison.OrdinalIgnoreCase) && value.Type == JTokenType.Array) return true;
                if (string.Equals(type, "string", StringComparison.OrdinalIgnoreCase) && value.Type == JTokenType.String) return true;
                if (string.Equals(type, "boolean", StringComparison.OrdinalIgnoreCase) && value.Type == JTokenType.Boolean) return true;
                if (string.Equals(type, "integer", StringComparison.OrdinalIgnoreCase) && value.Type == JTokenType.Integer) return true;
                if (string.Equals(type, "number", StringComparison.OrdinalIgnoreCase) && (value.Type == JTokenType.Integer || value.Type == JTokenType.Float)) return true;
            }
            return false;
        }

        private static string ApiName(string toolId, ISet<string> existing)
        {
            var builder = new StringBuilder("rna_");
            foreach (var character in toolId ?? "tool")
            {
                builder.Append(char.IsLetterOrDigit(character) || character == '_' || character == '-' ? character : '_');
            }
            var value = builder.ToString();
            if (value.Length > 64) value = value.Substring(0, 55) + "_" + Hash(toolId).Substring(0, 8);
            if (existing.Contains(value)) value = value.Substring(0, Math.Min(55, value.Length)) + "_" + Hash(toolId).Substring(0, 8);
            return value;
        }

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty))).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static string Trim(string value, int max)
        {
            value = value ?? string.Empty;
            return value.Length <= max ? value : value.Substring(0, max);
        }
    }
}
