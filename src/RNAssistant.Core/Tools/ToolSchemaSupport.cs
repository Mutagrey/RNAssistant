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
        private static readonly HashSet<string> SupportedSchemaKeywords = new HashSet<string>(
            new[]
            {
                "type", "description", "properties", "required", "additionalProperties", "items", "anyOf",
                "enum", "const", "default", "minimum", "maximum", "minLength", "maxLength", "minItems", "maxItems"
            },
            StringComparer.Ordinal);

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
                parsed = JObject.Parse(
                    string.IsNullOrWhiteSpace(tool.ArgumentSchemaJson) ? "{}" : tool.ArgumentSchemaJson,
                    new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
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
            var ambiguousProperty = properties.Properties()
                .GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (ambiguousProperty != null)
            {
                error = "argumentSchemaJson.properties contains names that differ only by case: " + ambiguousProperty.Key + ".";
                return false;
            }
            var requiredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

            if (!ValidateSchemaNode(parsed, "argumentSchemaJson", out error))
            {
                return false;
            }

            schema = (JObject)parsed.DeepClone();
            schema["type"] = "object";
            return true;
        }

        public static JObject ForStructuredOutput(JObject schema)
        {
            var clone = schema == null ? EmptyObjectSchema() : (JObject)schema.DeepClone();
            CollapseObjectAnyOfConstraints(clone);
            MakeOptionalPropertiesNullable(clone);
            MakeObjectSchemasStrict(clone);
            return clone;
        }

        public static JObject ForPrompt(JObject schema)
        {
            var clone = schema == null ? EmptyObjectSchema() : (JObject)schema.DeepClone();
            CollapseObjectAnyOfConstraints(clone);
            return clone;
        }

        public static void RemoveOptionalNulls(JToken value, JObject schema)
        {
            if (value == null || schema == null) return;

            var alternatives = schema["anyOf"] as JArray;
            if (alternatives != null)
            {
                JObject discriminatorMatch = null;
                foreach (var candidate in alternatives.OfType<JObject>())
                {
                    if (discriminatorMatch == null && MatchesDiscriminator(value, candidate)) discriminatorMatch = candidate;
                    var clone = value.DeepClone();
                    RemoveOptionalNulls(clone, candidate);
                    string ignored;
                    if (!ValidateValue(clone, candidate, "$", false, out ignored)) continue;
                    CopyValidatedValue(value, clone);
                    return;
                }
                if (discriminatorMatch != null)
                {
                    RemoveOptionalNulls(value, discriminatorMatch);
                    return;
                }
            }

            var array = value as JArray;
            if (array != null)
            {
                var itemSchema = schema["items"] as JObject;
                if (itemSchema == null) return;
                foreach (var item in array) RemoveOptionalNulls(item, itemSchema);
                return;
            }

            var obj = value as JObject;
            if (obj == null) return;
            var properties = schema["properties"] as JObject ?? new JObject();
            var required = new HashSet<string>(
                (schema["required"] as JArray ?? new JArray()).Values<string>(),
                StringComparer.Ordinal);
            foreach (var property in obj.Properties().ToList())
            {
                var childSchema = properties[property.Name] as JObject;
                if (childSchema == null) continue;
                if ((property.Value.Type == JTokenType.Null || property.Value.Type == JTokenType.Undefined) &&
                    !required.Contains(property.Name) &&
                    !ContainsType(childSchema["type"], "null"))
                {
                    property.Remove();
                    continue;
                }
                RemoveOptionalNulls(property.Value, childSchema);
            }
        }

        public static bool ValidateArguments(JObject arguments, JObject schema, bool applyDefaults, out string error)
        {
            arguments = arguments ?? new JObject();
            schema = schema ?? EmptyObjectSchema();
            try
            {
                return ValidateValue(arguments, schema, "$", applyDefaults, out error);
            }
            catch (Exception ex) when (ex is FormatException || ex is OverflowException || ex is InvalidCastException)
            {
                error = "Tool schema contains an invalid constraint: " + ex.Message;
                return false;
            }
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
                string firstError = null;
                string discriminatorError = null;
                string closestError = null;
                var closestScore = int.MinValue;
                foreach (var candidate in anyOf.OfType<JObject>())
                {
                    string candidateError;
                    var clone = value == null ? null : value.DeepClone();
                    if (ValidateValue(clone, candidate, path, applyDefaults, out candidateError))
                    {
                        CopyValidatedValue(value, clone);
                        return true;
                    }
                    if (firstError == null) firstError = candidateError;
                    if (MatchesDiscriminator(value, candidate)) discriminatorError = candidateError;
                    var score = AlternativeMatchScore(value, candidate);
                    if (score > closestScore)
                    {
                        closestScore = score;
                        closestError = candidateError;
                    }
                }
                error = discriminatorError ?? closestError ?? firstError ?? path + " does not match any allowed schema.";
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
            var constant = schema["const"];
            if (constant != null && !JToken.DeepEquals(constant, value))
            {
                error = path + " does not match the required constant value.";
                return false;
            }

            if (value != null && (value.Type == JTokenType.Integer || value.Type == JTokenType.Float))
            {
                var number = value.Value<double>();
                if (double.IsNaN(number) || double.IsInfinity(number))
                {
                    error = path + " must be a finite JSON number.";
                    return false;
                }
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

        private static bool MatchesDiscriminator(JToken value, JObject schema)
        {
            var obj = value as JObject;
            var properties = schema == null ? null : schema["properties"] as JObject;
            if (obj == null || properties == null) return false;
            foreach (var property in properties.Properties())
            {
                var actual = obj[property.Name];
                var propertySchema = property.Value as JObject;
                if (actual == null || propertySchema == null) continue;
                var constant = propertySchema["const"];
                if (constant != null && JToken.DeepEquals(actual, constant)) return true;
                var allowed = propertySchema["enum"] as JArray;
                if (allowed != null && allowed.Count == 1 && JToken.DeepEquals(actual, allowed[0])) return true;
            }
            return false;
        }

        private static int AlternativeMatchScore(JToken value, JObject schema)
        {
            var obj = value as JObject;
            var properties = schema == null ? null : schema["properties"] as JObject;
            if (obj == null || properties == null) return 0;
            var score = 0;
            foreach (var actual in obj.Properties())
            {
                var propertySchema = properties[actual.Name] as JObject;
                if (propertySchema == null)
                {
                    score--;
                    continue;
                }
                score += 2;
                var constant = propertySchema["const"];
                if (constant != null) score += JToken.DeepEquals(actual.Value, constant) ? 20 : -20;
                var allowed = propertySchema["enum"] as JArray;
                if (allowed != null && allowed.Count == 1) score += JToken.DeepEquals(actual.Value, allowed[0]) ? 20 : -20;
            }
            return score;
        }

        private static void CopyValidatedValue(JToken target, JToken source)
        {
            var targetObject = target as JObject;
            var sourceObject = source as JObject;
            if (targetObject != null && sourceObject != null)
            {
                targetObject.RemoveAll();
                foreach (var property in sourceObject.Properties()) targetObject.Add(property.Name, property.Value.DeepClone());
                return;
            }

            var targetArray = target as JArray;
            var sourceArray = source as JArray;
            if (targetArray != null && sourceArray != null)
            {
                targetArray.RemoveAll();
                foreach (var item in sourceArray) targetArray.Add(item.DeepClone());
            }
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
            return types.Length > 0 &&
                types.Distinct(StringComparer.OrdinalIgnoreCase).Count() == types.Length &&
                types.All(type =>
                string.Equals(type, "null", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "object", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "array", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "string", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "boolean", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "integer", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "number", StringComparison.OrdinalIgnoreCase));
        }

        private static bool ValidateSchemaNode(JObject node, string path, out string error)
        {
            error = null;
            if (node == null)
            {
                error = path + " must be a schema object.";
                return false;
            }

            var unsupported = node.Properties().FirstOrDefault(property => !SupportedSchemaKeywords.Contains(property.Name));
            if (unsupported != null)
            {
                error = path + " contains unsupported schema keyword " + unsupported.Name + ".";
                return false;
            }
            var description = node["description"];
            if (description != null && description.Type != JTokenType.String)
            {
                error = path + ".description must be a JSON string.";
                return false;
            }

            var type = node["type"];
            if (type != null && !HasValidType(type))
            {
                error = path + ".type is invalid.";
                return false;
            }

            var anyOfToken = node["anyOf"];
            if (anyOfToken != null)
            {
                var alternatives = anyOfToken as JArray;
                if (alternatives == null || alternatives.Count == 0 || alternatives.Any(item => !(item is JObject)))
                {
                    error = path + ".anyOf must be a non-empty array of schema objects.";
                    return false;
                }
                for (var i = 0; i < alternatives.Count; i++)
                {
                    if (!ValidateSchemaNode((JObject)alternatives[i], path + ".anyOf[" + i + "]", out error)) return false;
                }
            }

            var propertiesToken = node["properties"];
            if (propertiesToken != null && !(propertiesToken is JObject))
            {
                error = path + ".properties must be an object.";
                return false;
            }
            var properties = propertiesToken as JObject;
            if (properties != null)
            {
                var ambiguousProperty = properties.Properties()
                    .GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(group => group.Count() > 1);
                if (ambiguousProperty != null)
                {
                    error = path + ".properties contains names that differ only by case: " + ambiguousProperty.Key + ".";
                    return false;
                }
                foreach (var property in properties.Properties())
                {
                    var propertySchema = property.Value as JObject;
                    if (propertySchema == null || !ValidateSchemaNode(propertySchema, path + ".properties." + property.Name, out error))
                    {
                        if (error == null) error = path + ".properties." + property.Name + " must be a schema object.";
                        return false;
                    }
                }
            }

            var requiredToken = node["required"];
            if (requiredToken != null)
            {
                var required = requiredToken as JArray;
                if (required == null || required.Any(item => item.Type != JTokenType.String))
                {
                    error = path + ".required must be an array of property names.";
                    return false;
                }
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in required.Values<string>())
                {
                    if (!names.Add(name))
                    {
                        error = path + ".required contains duplicate property " + name + ".";
                        return false;
                    }
                    if (properties != null && properties[name] == null)
                    {
                        error = path + ".required references unknown property " + name + ".";
                        return false;
                    }
                }
            }

            var additionalProperties = node["additionalProperties"];
            if (additionalProperties != null &&
                (additionalProperties.Type != JTokenType.Boolean || additionalProperties.Value<bool>()))
            {
                error = path + ".additionalProperties must be false when declared.";
                return false;
            }

            if (ContainsType(type, "array"))
            {
                var items = node["items"] as JObject;
                if (items == null)
                {
                    error = path + ".items is required for arrays.";
                    return false;
                }
                if (!ValidateSchemaNode(items, path + ".items", out error)) return false;
            }

            var enumToken = node["enum"];
            if (enumToken != null)
            {
                var values = enumToken as JArray;
                if (values == null || values.Count == 0)
                {
                    error = path + ".enum must be a non-empty array.";
                    return false;
                }
                if (type != null && values.Any(value => !MatchesType(value, type)))
                {
                    error = path + ".enum contains a value with the wrong JSON type.";
                    return false;
                }
                for (var i = 0; i < values.Count; i++)
                {
                    if (values.Take(i).Any(value => JToken.DeepEquals(value, values[i])))
                    {
                        error = path + ".enum contains duplicate values.";
                        return false;
                    }
                }
            }

            var constant = node["const"];
            if (constant != null && type != null && !MatchesType(constant, type))
            {
                error = path + ".const has the wrong JSON type.";
                return false;
            }
            if (constant != null && enumToken is JArray && !((JArray)enumToken).Any(value => JToken.DeepEquals(value, constant)))
            {
                error = path + ".const is not present in enum.";
                return false;
            }

            double minimum;
            double maximum;
            if (!TryReadFiniteNumber(node["minimum"], out minimum))
            {
                error = path + ".minimum must be a finite JSON number.";
                return false;
            }
            if (!TryReadFiniteNumber(node["maximum"], out maximum))
            {
                error = path + ".maximum must be a finite JSON number.";
                return false;
            }
            if (node["minimum"] != null && node["maximum"] != null && minimum > maximum)
            {
                error = path + ".minimum must not exceed maximum.";
                return false;
            }

            int minLength;
            int maxLength;
            int minItems;
            int maxItems;
            if (!TryReadNonNegativeInt(node["minLength"], out minLength) ||
                !TryReadNonNegativeInt(node["maxLength"], out maxLength) ||
                !TryReadNonNegativeInt(node["minItems"], out minItems) ||
                !TryReadNonNegativeInt(node["maxItems"], out maxItems))
            {
                error = path + " length and item limits must be non-negative JSON integers.";
                return false;
            }
            if (node["minLength"] != null && node["maxLength"] != null && minLength > maxLength)
            {
                error = path + ".minLength must not exceed maxLength.";
                return false;
            }
            if (node["minItems"] != null && node["maxItems"] != null && minItems > maxItems)
            {
                error = path + ".minItems must not exceed maxItems.";
                return false;
            }

            var defaultValue = node["default"];
            if (defaultValue != null)
            {
                string defaultError;
                if (!ValidateValue(defaultValue.DeepClone(), node, path + ".default", false, out defaultError))
                {
                    error = path + ".default is invalid: " + defaultError;
                    return false;
                }
            }
            return true;
        }

        private static bool TryReadFiniteNumber(JToken token, out double value)
        {
            value = 0;
            if (token == null) return true;
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float) return false;
            try
            {
                value = token.Value<double>();
                return !double.IsNaN(value) && !double.IsInfinity(value);
            }
            catch (Exception ex) when (ex is FormatException || ex is OverflowException || ex is InvalidCastException)
            {
                return false;
            }
        }

        private static bool TryReadNonNegativeInt(JToken token, out int value)
        {
            value = 0;
            if (token == null) return true;
            if (token.Type != JTokenType.Integer) return false;
            try
            {
                value = token.Value<int>();
                return value >= 0;
            }
            catch (Exception ex) when (ex is FormatException || ex is OverflowException || ex is InvalidCastException)
            {
                return false;
            }
        }

        private static bool ContainsType(JToken typeToken, string expected)
        {
            if (typeToken == null) return false;
            if (typeToken.Type == JTokenType.String)
            {
                return string.Equals((string)typeToken, expected, StringComparison.OrdinalIgnoreCase);
            }
            var types = typeToken as JArray;
            return types != null && types.Any(type =>
                type.Type == JTokenType.String &&
                string.Equals((string)type, expected, StringComparison.OrdinalIgnoreCase));
        }

        private static void MakeObjectSchemasStrict(JToken token)
        {
            var obj = token as JObject;
            if (obj != null)
            {
                if (ContainsType(obj["type"], "object"))
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

        private static void CollapseObjectAnyOfConstraints(JToken token)
        {
            var obj = token as JObject;
            if (obj != null)
            {
                foreach (var property in obj.Properties().ToList())
                {
                    CollapseObjectAnyOfConstraints(property.Value);
                }

                if (ContainsType(obj["type"], "object") && obj["anyOf"] is JArray)
                {
                    // Runtime validation treats anyOf branches as complete alternatives. Keep the
                    // structured-output schema equivalent: a strict parent object with the union of
                    // branch properties would otherwise require fields that every branch forbids.
                    obj.Remove("type");
                    obj.Remove("properties");
                    obj.Remove("required");
                    obj.Remove("additionalProperties");
                }
                return;
            }

            var array = token as JArray;
            if (array != null)
            {
                foreach (var item in array) CollapseObjectAnyOfConstraints(item);
            }
        }

        private static void MakeOptionalPropertiesNullable(JToken token)
        {
            var obj = token as JObject;
            if (obj != null)
            {
                if (ContainsType(obj["type"], "object"))
                {
                    var properties = obj["properties"] as JObject ?? new JObject();
                    var required = new HashSet<string>(
                        (obj["required"] as JArray ?? new JArray()).Values<string>(),
                        StringComparer.Ordinal);
                    foreach (var property in properties.Properties())
                    {
                        MakeOptionalPropertiesNullable(property.Value);
                        var propertySchema = property.Value as JObject;
                        if (propertySchema != null && !required.Contains(property.Name)) MakeNullable(propertySchema);
                    }
                    foreach (var keyword in obj.Properties().Where(property =>
                        !string.Equals(property.Name, "properties", StringComparison.Ordinal)).ToList())
                    {
                        MakeOptionalPropertiesNullable(keyword.Value);
                    }
                    return;
                }
                foreach (var property in obj.Properties().ToList()) MakeOptionalPropertiesNullable(property.Value);
                return;
            }

            var array = token as JArray;
            if (array != null)
            {
                foreach (var item in array) MakeOptionalPropertiesNullable(item);
            }
        }

        private static void MakeNullable(JObject schema)
        {
            if (schema == null) return;
            var type = schema["type"];
            if (type != null && !ContainsType(type, "null"))
            {
                schema["type"] = type.Type == JTokenType.Array
                    ? new JArray(((JArray)type).Select(item => item.DeepClone()).Concat(new[] { new JValue("null") }))
                    : new JArray(type.DeepClone(), "null");
            }
            else if (type == null && schema["anyOf"] is JArray)
            {
                var alternatives = (JArray)schema["anyOf"];
                if (!alternatives.OfType<JObject>().Any(item => ContainsType(item["type"], "null")))
                {
                    alternatives.Add(new JObject { ["type"] = "null" });
                }
            }

            var enumValues = schema["enum"] as JArray;
            if (enumValues != null && !enumValues.Any(item => item.Type == JTokenType.Null))
            {
                enumValues.Add(JValue.CreateNull());
            }
        }

    }
}
