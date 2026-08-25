using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Office.Tools
{
    public static class ToolArgumentReader
    {
        public static string String(IDictionary<string, object> args, string name, string fallback = "")
        {
            object value;
            if (args == null || !args.TryGetValue(name, out value) || value == null)
            {
                return fallback;
            }

            var token = value as JToken;
            if (token == null)
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            }
            if (token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
            {
                return fallback;
            }
            if (token.Type == JTokenType.Object || token.Type == JTokenType.Array)
            {
                return token.ToString(Formatting.None);
            }

            var scalar = token as JValue;
            return Convert.ToString(scalar == null ? token.ToString(Formatting.None) : scalar.Value, CultureInfo.InvariantCulture);
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
    }
}
