using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Skills
{
    public sealed class SkillCommandParser
    {
        private const string Fence = "```rnassistant-skill";
        private const string XmlStart = "<rnassistant-skill>";
        private const string XmlEnd = "</rnassistant-skill>";

        public IReadOnlyList<SkillCommand> Parse(string assistantText)
        {
            var commands = new List<SkillCommand>();
            if (string.IsNullOrWhiteSpace(assistantText))
            {
                return commands;
            }

            ExtractFenced(assistantText, commands);
            ExtractXml(assistantText, commands);
            return commands;
        }

        private static void ExtractFenced(string text, ICollection<SkillCommand> commands)
        {
            var index = 0;
            while ((index = text.IndexOf(Fence, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var jsonStart = index + Fence.Length;
                var end = text.IndexOf("```", jsonStart, StringComparison.OrdinalIgnoreCase);
                if (end < 0)
                {
                    break;
                }

                TryAdd(text.Substring(jsonStart, end - jsonStart), commands);
                index = end + 3;
            }
        }

        private static void ExtractXml(string text, ICollection<SkillCommand> commands)
        {
            var index = 0;
            while ((index = text.IndexOf(XmlStart, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var jsonStart = index + XmlStart.Length;
                var end = text.IndexOf(XmlEnd, jsonStart, StringComparison.OrdinalIgnoreCase);
                if (end < 0)
                {
                    break;
                }

                TryAdd(text.Substring(jsonStart, end - jsonStart), commands);
                index = end + XmlEnd.Length;
            }
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

            var id = (string)(obj["skillId"] ?? obj["skill_id"] ?? obj["id"]);
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            var command = new SkillCommand { SkillId = id };
            var args = obj["arguments"] as JObject;
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

