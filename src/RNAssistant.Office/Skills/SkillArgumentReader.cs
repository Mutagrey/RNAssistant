using System;
using System.Collections.Generic;

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
    }
}

