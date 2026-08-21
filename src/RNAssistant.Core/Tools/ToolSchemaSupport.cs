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
        public static bool TryParse(ToolDefinition tool, out JObject schema, out string error)
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

            if (!string.Equals((string)parsed["type"], "object", StringComparison.OrdinalIgnoreCase) || !(parsed["properties"] is JObject))
            {
                error = "argumentSchemaJson must be a formal JSON Schema object with type=object and properties.";
                return false;
            }

            var required = parsed["required"] as JArray;
            if (required == null || required.Any(item => item.Type != JTokenType.String))
            {
                error = "argumentSchemaJson.required must be an array of property names.";
                return false;
            }

            var additionalProperties = parsed["additionalProperties"];
            if (additionalProperties == null || additionalProperties.Type != JTokenType.Boolean || additionalProperties.Value<bool>())
            {
                error = "argumentSchemaJson.additionalProperties must be false.";
                return false;
            }

            var properties = (JObject)parsed["properties"];
            var requiredNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in required.Values<string>())
            {
                if (!requiredNames.Add(name))
                {
                    error = "argumentSchemaJson.required contains duplicate property " + name + ".";
                    return false;
                }
                if (properties[name] == null)
                {
                    error = "argumentSchemaJson.required references unknown property " + name + ".";
                    return false;
                }
            }

            foreach (var property in properties.Properties())
            {
                var propertySchema = property.Value as JObject;
                if (propertySchema == null)
                {
                    error = "argumentSchemaJson.properties." + property.Name + " must be an object.";
                    return false;
                }
                if (string.IsNullOrWhiteSpace((string)propertySchema["description"]))
                {
                    error = "argumentSchemaJson.properties." + property.Name + ".description is required.";
                    return false;
                }
                if (!HasValidType(propertySchema["type"]))
                {
                    error = "argumentSchemaJson.properties." + property.Name + ".type is invalid.";
                    return false;
                }
                if (ContainsType(propertySchema["type"], "array") && !(propertySchema["items"] is JObject))
                {
                    error = "argumentSchemaJson.properties." + property.Name + ".items is required for arrays.";
                    return false;
                }
                if (propertySchema["default"] != null && !MatchesType(propertySchema["default"], propertySchema["type"]))
                {
                    error = "argumentSchemaJson.properties." + property.Name + ".default has the wrong JSON type.";
                    return false;
                }
            }

            schema = (JObject)parsed.DeepClone();
            schema["type"] = "object";
            return true;
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

            if (value != null && (value.Type == JTokenType.Integer || value.Type == JTokenType.Float))
            {
                var number = value.Value<double>();
                if (schema["minimum"] != null && number < schema["minimum"].Value<double>())
                {
                    error = path + " is below the minimum value.";
                    return false;
                }
                if (schema["maximum"] != null && number > schema["maximum"].Value<double>())
                {
                    error = path + " is above the maximum value.";
                    return false;
                }
            }

            var text = value == null ? null : value.Type == JTokenType.String ? value.Value<string>() : null;
            if (text != null)
            {
                if (schema["minLength"] != null && text.Length < schema["minLength"].Value<int>())
                {
                    error = path + " is shorter than minLength.";
                    return false;
                }
                if (schema["maxLength"] != null && text.Length > schema["maxLength"].Value<int>())
                {
                    error = path + " is longer than maxLength.";
                    return false;
                }
            }

            var array = value as JArray;
            if (array != null)
            {
                if (schema["minItems"] != null && array.Count < schema["minItems"].Value<int>())
                {
                    error = path + " has fewer items than minItems.";
                    return false;
                }
                if (schema["maxItems"] != null && array.Count > schema["maxItems"].Value<int>())
                {
                    error = path + " has more items than maxItems.";
                    return false;
                }
                var itemSchema = schema["items"] as JObject;
                if (itemSchema != null)
                {
                    for (var i = 0; i < array.Count; i++)
                    {
                        if (!ValidateValue(array[i], itemSchema, path + "[" + i + "]", applyDefaults, out error)) return false;
                    }
                }
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

        private static bool HasValidType(JToken typeToken)
        {
            if (typeToken == null) return false;
            var types = typeToken.Type == JTokenType.Array
                ? ((JArray)typeToken).Values<string>().ToArray()
                : new[] { typeToken.Type == JTokenType.String ? (string)typeToken : null };
            return types.Length > 0 && types.All(type =>
                string.Equals(type, "null", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "object", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "array", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "string", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "boolean", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "integer", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "number", StringComparison.OrdinalIgnoreCase));
        }

        private static bool ContainsType(JToken typeToken, string expected)
        {
            if (typeToken == null) return false;
            return typeToken.Type == JTokenType.Array
                ? ((JArray)typeToken).Values<string>().Any(type => string.Equals(type, expected, StringComparison.OrdinalIgnoreCase))
                : string.Equals((string)typeToken, expected, StringComparison.OrdinalIgnoreCase);
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

    }
}
