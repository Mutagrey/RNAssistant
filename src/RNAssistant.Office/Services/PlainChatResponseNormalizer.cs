using System;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Office.Services
{
    internal static class PlainChatResponseNormalizer
    {
        public static bool TryGetUserFacingText(string text, out string answer)
        {
            answer = null;
            var trimmed = UnwrapJsonFence(text);
            JObject obj = null;
            if (trimmed.StartsWith("{", StringComparison.Ordinal) &&
                trimmed.EndsWith("}", StringComparison.Ordinal))
            {
                try
                {
                    obj = JObject.Parse(trimmed);
                }
                catch
                {
                    obj = null;
                }
            }

            var suspiciousText = trimmed.IndexOf("{", StringComparison.Ordinal) >= 0 &&
                (trimmed.IndexOf("\"thought\"", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 trimmed.IndexOf("\"reasoning\"", StringComparison.OrdinalIgnoreCase) >= 0);
            if (obj == null)
            {
                return suspiciousText;
            }

            var hasInternalField = obj.GetValue("thought", StringComparison.OrdinalIgnoreCase) != null ||
                obj.GetValue("reasoning", StringComparison.OrdinalIgnoreCase) != null;
            var kind = obj.GetValue("kind", StringComparison.OrdinalIgnoreCase);
            var hasPlannerKind = kind != null &&
                (string.Equals(kind.ToString(), "tool", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(kind.ToString(), "plan", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(kind.ToString(), "final", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(kind.ToString(), "clarify", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(kind.ToString(), "cannot_complete", StringComparison.OrdinalIgnoreCase));
            if (!hasInternalField && !hasPlannerKind)
            {
                return false;
            }

            foreach (var field in new[] { "answer", "final", "response", "content", "message" })
            {
                var token = obj.GetValue(field, StringComparison.OrdinalIgnoreCase);
                if (token != null && token.Type == JTokenType.String && !string.IsNullOrWhiteSpace(token.Value<string>()))
                {
                    answer = token.Value<string>().Trim();
                    break;
                }
            }
            return true;
        }

        private static string UnwrapJsonFence(string text)
        {
            var value = (text ?? string.Empty).TrimStart('\uFEFF').Trim();
            if (!value.StartsWith("```", StringComparison.Ordinal) ||
                !value.EndsWith("```", StringComparison.Ordinal))
            {
                return value;
            }

            var newline = value.IndexOf('\n');
            if (newline < 0)
            {
                return value;
            }
            var language = value.Substring(3, newline - 3).Trim();
            if (language.Length > 0 && !string.Equals(language, "json", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
            return value.Substring(newline + 1, value.Length - newline - 4).Trim();
        }
    }
}
