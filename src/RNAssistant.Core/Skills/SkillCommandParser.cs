using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Skills
{
    public sealed class SkillCommandParser
    {
        private static readonly string[] Fences = { "```rnassistant-skill", "```rnassistant-agent" };
        private const string XmlStart = "<rnassistant-skill>";
        private const string XmlEnd = "</rnassistant-skill>";
        private const string AgentXmlStart = "<rnassistant-agent>";
        private const string AgentXmlEnd = "</rnassistant-agent>";

        public IReadOnlyList<SkillCommand> Parse(string assistantText)
        {
            var commands = new List<SkillCommand>();
            if (string.IsNullOrWhiteSpace(assistantText))
            {
                return commands;
            }

            ExtractFenced(assistantText, commands);
            ExtractXml(assistantText, commands);
            ExtractBareJson(assistantText, commands);
            if (commands.Count == 0)
            {
                ExtractGenericJsonFences(assistantText, commands);
            }
            if (commands.Count == 0)
            {
                ExtractEmbeddedJson(assistantText, commands);
            }
            return commands;
        }

        private static void ExtractFenced(string text, ICollection<SkillCommand> commands)
        {
            foreach (var fence in Fences)
            {
                var index = 0;
                while ((index = text.IndexOf(fence, index, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    var jsonStart = index + fence.Length;
                    var end = text.IndexOf("```", jsonStart, StringComparison.OrdinalIgnoreCase);
                    if (end < 0)
                    {
                        break;
                    }

                    TryAdd(text.Substring(jsonStart, end - jsonStart), commands);
                    index = end + 3;
                }
            }
        }

        private static void ExtractXml(string text, ICollection<SkillCommand> commands)
        {
            ExtractXml(text, XmlStart, XmlEnd, commands);
            ExtractXml(text, AgentXmlStart, AgentXmlEnd, commands);
        }

        private static void ExtractXml(string text, string startTag, string endTag, ICollection<SkillCommand> commands)
        {
            var index = 0;
            while ((index = text.IndexOf(startTag, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var jsonStart = index + startTag.Length;
                var end = text.IndexOf(endTag, jsonStart, StringComparison.OrdinalIgnoreCase);
                if (end < 0)
                {
                    break;
                }

                TryAdd(text.Substring(jsonStart, end - jsonStart), commands);
                index = end + endTag.Length;
            }
        }

        private static void ExtractBareJson(string text, ICollection<SkillCommand> commands)
        {
            var trimmed = (text ?? string.Empty).Trim();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal) && !trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                return;
            }

            TryAdd(trimmed, commands);
        }

        private static void ExtractGenericJsonFences(string text, ICollection<SkillCommand> commands)
        {
            var index = 0;
            while ((index = text.IndexOf("```", index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var contentStart = index + 3;
                var lineEnd = text.IndexOf('\n', contentStart);
                if (lineEnd < 0)
                {
                    break;
                }

                var language = text.Substring(contentStart, lineEnd - contentStart).Trim();
                var end = text.IndexOf("```", lineEnd + 1, StringComparison.OrdinalIgnoreCase);
                if (end < 0)
                {
                    break;
                }

                if (string.Equals(language, "json", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(language))
                {
                    TryAdd(text.Substring(lineEnd + 1, end - lineEnd - 1), commands);
                }
                index = end + 3;
            }
        }

        private static void ExtractEmbeddedJson(string text, ICollection<SkillCommand> commands)
        {
            foreach (var start in FindJsonStarts(text))
            {
                var candidate = ReadBalancedJson(text, start);
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                TryAdd(candidate, commands);
                if (commands.Count > 0)
                {
                    return;
                }
            }
        }

        private static IEnumerable<int> FindJsonStarts(string text)
        {
            for (var i = 0; i < (text == null ? 0 : text.Length); i++)
            {
                if (text[i] == '{' || text[i] == '[')
                {
                    yield return i;
                }
            }
        }

        private static string ReadBalancedJson(string text, int start)
        {
            if (string.IsNullOrEmpty(text) || start < 0 || start >= text.Length)
            {
                return null;
            }

            var open = text[start];
            var close = open == '{' ? '}' : ']';
            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var i = start; i < text.Length; i++)
            {
                var c = text[i];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (c == '\\')
                    {
                        escaped = true;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }
                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                }
                else if (c == open)
                {
                    depth++;
                }
                else if (c == close)
                {
                    depth--;
                    if (depth == 0)
                    {
                        return text.Substring(start, i - start + 1);
                    }
                }
            }

            return null;
        }

        private static void TryAdd(string json, ICollection<SkillCommand> commands)
        {
            try
            {
                var token = JToken.Parse(json.Trim());
                if (token.Type == JTokenType.Array)
                {
                    foreach (var item in token.Children())
                    {
                        AddObject(item, commands);
                    }
                }
                else
                {
                    AddObject(token, commands);
                }
            }
            catch (JsonException)
            {
            }
        }

        private static void AddObject(JToken token, ICollection<SkillCommand> commands)
        {
            var obj = token as JObject;
            if (obj == null)
            {
                return;
            }

            var steps = (obj["steps"] as JArray) ?? (obj["commands"] as JArray) ?? (obj["actions"] as JArray) ?? (obj["tools"] as JArray) ?? (obj["tool_calls"] as JArray);
            var argumentToken = obj["arguments"] ?? obj["args"] ?? obj["parameters"] ?? obj["input"];
            var explicitId = (string)(obj["skillId"] ?? obj["skill_id"] ?? obj["toolId"] ?? obj["tool_id"] ?? obj["tool"]);
            var function = obj["function"] as JObject;
            if (string.IsNullOrWhiteSpace(explicitId) && function != null)
            {
                explicitId = (string)function["name"];
                argumentToken = argumentToken ?? function["arguments"];
            }
            if (string.IsNullOrWhiteSpace(explicitId) && argumentToken != null)
            {
                explicitId = (string)(obj["action"] ?? obj["name"]);
            }
            if (string.IsNullOrWhiteSpace(explicitId) && steps != null)
            {
                foreach (var step in steps.Children())
                {
                    AddObject(step, commands);
                }
                return;
            }

            var id = string.IsNullOrWhiteSpace(explicitId) ? (string)obj["id"] : explicitId;
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            var command = new SkillCommand
            {
                SkillId = id,
                Description = (string)(obj["description"] ?? obj["title"] ?? obj["reason"])
            };
            var args = ReadObject(argumentToken);
            if (args != null)
            {
                foreach (var property in args.Properties())
                {
                    command.Arguments[property.Name] = property.Value.Type == JTokenType.String
                        ? (object)property.Value.Value<string>()
                        : property.Value.ToString(Formatting.None);
                }
            }

            commands.Add(command);
        }

        private static JObject ReadObject(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            var obj = token as JObject;
            if (obj != null)
            {
                return obj;
            }

            if (token.Type == JTokenType.String)
            {
                try
                {
                    return JObject.Parse(token.Value<string>() ?? "{}");
                }
                catch (JsonException)
                {
                    return null;
                }
            }

            return null;
        }
    }
}
