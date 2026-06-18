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

            var steps = (obj["steps"] as JArray) ?? (obj["commands"] as JArray) ?? (obj["actions"] as JArray) ?? (obj["tools"] as JArray);
            var explicitId = (string)(obj["skillId"] ?? obj["skill_id"] ?? obj["toolId"] ?? obj["tool_id"]);
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

            var command = new SkillCommand { SkillId = id };
            var args = (obj["arguments"] as JObject) ?? (obj["args"] as JObject) ?? (obj["input"] as JObject);
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
    }
}
