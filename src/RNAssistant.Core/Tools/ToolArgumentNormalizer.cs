using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Core.Tools
{
    public static class ToolArgumentNormalizer
    {
        public static Dictionary<string, object> ParseObject(string argumentsJson)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(argumentsJson))
            {
                return result;
            }

            try
            {
                AddProperties(JObject.Parse(argumentsJson), result);
            }
            catch (JsonException)
            {
            }

            return result;
        }

        public static Dictionary<string, object> NormalizeDictionary(IDictionary<string, object> arguments)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (arguments == null)
            {
                return result;
            }

            foreach (var pair in arguments)
            {
                result[pair.Key] = NormalizeValue(pair.Value);
            }

            return result;
        }

        public static void AddProperties(JObject obj, IDictionary<string, object> target)
        {
            if (obj == null || target == null)
            {
                return;
            }

            foreach (var property in obj.Properties())
            {
                target[property.Name] = NormalizeToken(property.Value);
            }
        }

        public static object NormalizeValue(object value)
        {
            var token = value as JToken;
            return token == null ? value : NormalizeToken(token);
        }

        public static object NormalizeToken(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
            {
                return null;
            }

            if (token.Type == JTokenType.String)
            {
                return token.Value<string>();
            }

            if (token.Type == JTokenType.Boolean)
            {
                return token.Value<bool>();
            }

            if (token.Type == JTokenType.Integer)
            {
                return token.Value<long>();
            }

            if (token.Type == JTokenType.Float)
            {
                return token.Value<double>();
            }

            if (token.Type == JTokenType.Object || token.Type == JTokenType.Array)
            {
                return token.ToString(Formatting.None);
            }

            var value = token as JValue;
            return value == null ? token.ToString(Formatting.None) : value.Value;
        }
    }
}
