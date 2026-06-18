using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Office.Skills
{
    public static class SkillArgumentReader
    {
        public static string String(IDictionary<string, object> args, string name, string fallback = "")
        {
            object value;
            if (args == null || !args.TryGetValue(name, out value) || value == null)
            {
                return fallback;
            }

            return Convert.ToString(value);
        }

        public static int Int32(IDictionary<string, object> args, string name, int fallback = 0)
        {
            var raw = String(args, name, null);
            int parsed;
            return int.TryParse(raw, out parsed) ? parsed : fallback;
        }

        public static bool Boolean(IDictionary<string, object> args, string name, bool fallback = false)
        {
            var raw = String(args, name, null);
            bool parsed;
            return bool.TryParse(raw, out parsed) ? parsed : fallback;
        }

        public static Dictionary<string, object> ParseObject(string argumentsJson)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(argumentsJson))
            {
                return result;
            }

            try
            {
                var args = JObject.Parse(argumentsJson);
                foreach (var property in args.Properties())
                {
                    result[property.Name] = property.Value.Type == JTokenType.String
                        ? (object)property.Value.Value<string>()
                        : property.Value.ToString(Formatting.None);
                }
            }
            catch (JsonException)
            {
            }

            return result;
        }
    }
}
