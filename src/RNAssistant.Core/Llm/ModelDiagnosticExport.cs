using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Core.Llm
{
    // Shared redaction for diagnostic CLI exports and the UI; never writes headers.
    public static class ModelDiagnosticExport
    {
        private static string Redact(string text, string secret)
        {
            if (string.IsNullOrEmpty(secret)) return text;
            var escaped = JsonConvert.ToString(secret);
            return text.Replace(escaped.Substring(1, escaped.Length - 2), "[REDACTED]").Replace(secret, "[REDACTED]");
        }
        public static string RedactJson(string text, string secret)
        {
            if (string.IsNullOrEmpty(secret)) return text;
            JToken root;
            try { root = JToken.Parse(text); }
            catch (JsonException) { return Redact(text, secret); }
            var changed = false;
            var tokens = root is JContainer ? ((JContainer)root).DescendantsAndSelf().ToArray() : new[] { root };
            foreach (var token in tokens)
            {
                var property = token as JProperty;
                if (property != null && Redact(property.Name, secret) != property.Name)
                    return "\"[REDACTED: sensitive property name]\"";
                var value = token as JValue;
                if (value == null || value.Type != JTokenType.String) continue;
                var original = (string)value;
                var safe = Redact(original, secret);
                if (safe == original) continue;
                value.Value = safe; changed = true;
            }
            return changed ? root.ToString(Formatting.Indented) : text;
        }
    }
}
