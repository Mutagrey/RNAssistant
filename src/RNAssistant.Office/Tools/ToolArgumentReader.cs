using System;
using System.Collections.Generic;

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
    }
}
