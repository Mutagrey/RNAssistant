using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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

        public static bool ValidateArguments(JObject arguments, JObject schema, bool applyDefaults, out string error)
        {
            arguments = arguments ?? new JObject();
            schema = schema ?? EmptyObjectSchema();
            return ValidateValue(arguments, schema, "$", applyDefaults, out error);
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

    }
}
