using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Tools
{
    public sealed class ToolCommandParseDiagnostic
    {
        public string Code { get; set; }
        public string Message { get; set; }
        public bool Recovered { get; set; }
    }

    public sealed class ToolCommandParseResult
    {
        public List<ToolCommand> Commands { get; private set; }
        public List<ToolCommandParseDiagnostic> Diagnostics { get; private set; }

        public ToolCommandParseResult()
        {
            Commands = new List<ToolCommand>();
            Diagnostics = new List<ToolCommandParseDiagnostic>();
        }

        public bool HasProtocolDiagnostics
        {
            get
            {
                return Diagnostics.Any(d => d != null && !string.IsNullOrWhiteSpace(d.Code));
            }
        }

        public bool HasRecoveredCommands
        {
            get
            {
                return Diagnostics.Any(d => d != null && d.Recovered);
            }
        }
    }

    public sealed class ToolCommandParser
    {
        private static readonly string[] Fences = { "```rnassistant-skill", "```rnassistant-agent" };
        private const string XmlStart = "<rnassistant-skill>";
        private const string XmlEnd = "</rnassistant-skill>";
        private const string AgentXmlStart = "<rnassistant-agent>";
        private const string AgentXmlEnd = "</rnassistant-agent>";

        public IReadOnlyList<ToolCommand> Parse(string assistantText)
        {
            return ParseWithDiagnostics(assistantText).Commands;
        }

        public ToolCommandParseResult ParseWithDiagnostics(string assistantText)
        {
            var result = new ToolCommandParseResult();
            if (string.IsNullOrWhiteSpace(assistantText))
            {
                return result;
            }

            ExtractFenced(assistantText, result);
            ExtractXml(assistantText, result);
            ExtractBareJson(assistantText, result);
            if (result.Commands.Count == 0)
            {
                ExtractGenericJsonFences(assistantText, result);
            }
            if (result.Commands.Count == 0)
            {
                ExtractEmbeddedJson(assistantText, result);
            }
            return result;
        }

        private static void ExtractFenced(string text, ToolCommandParseResult result)
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
                        TryAdd(text.Substring(jsonStart), result, "tool_fence_unclosed", true);
                        break;
                    }

                    TryAdd(text.Substring(jsonStart, end - jsonStart), result, "tool_fence_invalid_json", true);
                    index = end + 3;
                }
            }
        }

        private static void ExtractXml(string text, ToolCommandParseResult result)
        {
            ExtractXml(text, XmlStart, XmlEnd, result);
            ExtractXml(text, AgentXmlStart, AgentXmlEnd, result);
        }

        private static void ExtractXml(string text, string startTag, string endTag, ToolCommandParseResult result)
        {
            var index = 0;
            while ((index = text.IndexOf(startTag, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var jsonStart = index + startTag.Length;
                var end = text.IndexOf(endTag, jsonStart, StringComparison.OrdinalIgnoreCase);
                if (end < 0)
                {
                    TryAdd(text.Substring(jsonStart), result, "tool_xml_unclosed", true);
                    break;
                }

                TryAdd(text.Substring(jsonStart, end - jsonStart), result, "tool_xml_invalid_json", true);
                index = end + endTag.Length;
            }
        }

        private static void ExtractBareJson(string text, ToolCommandParseResult result)
        {
            var trimmed = (text ?? string.Empty).Trim();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal) && !trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                return;
            }

            TryAdd(trimmed, result, "bare_json_invalid", false);
        }

        private static void ExtractGenericJsonFences(string text, ToolCommandParseResult result)
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
                    TryAdd(text.Substring(lineEnd + 1, end - lineEnd - 1), result, "json_fence_invalid", false);
                }
                index = end + 3;
            }
        }

        private static void ExtractEmbeddedJson(string text, ToolCommandParseResult result)
        {
            foreach (var start in FindJsonStarts(text))
            {
                var candidate = ReadBalancedJson(text, start);
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    TryAdd(text.Substring(start), result, "embedded_json_repaired", false);
                    if (result.Commands.Count > 0)
                    {
                        return;
                    }
                    continue;
                }

                TryAdd(candidate, result, "embedded_json_invalid", false);
                if (result.Commands.Count > 0)
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

        private static void TryAdd(string json, ToolCommandParseResult result, string diagnosticCode, bool reportFailure)
        {
            var before = result.Commands.Count;
            if (TryAddToken(json, result.Commands))
            {
                return;
            }

            string repaired;
            if (TryRepairJson(json, out repaired) && TryAddToken(repaired, result.Commands))
            {
                if (result.Commands.Count > before)
                {
                    AddDiagnostic(result, diagnosticCode + "_recovered", "Recovered malformed tool JSON.", true);
                }
                return;
            }

            if (reportFailure)
            {
                AddDiagnostic(result, diagnosticCode, "Could not parse tool JSON.", false);
            }
        }

        private static bool TryAddToken(string json, ICollection<ToolCommand> commands)
        {
            if (string.IsNullOrWhiteSpace(json) || commands == null)
            {
                return false;
            }

            try
            {
                var token = JToken.Parse(json.Trim());
                var before = commands.Count;
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
                return commands.Count > before;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool TryRepairJson(string json, out string repaired)
        {
            repaired = null;
            var candidate = NormalizeJsonCandidate(json);
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            var balanced = ReadBalancedJson(candidate, 0);
            if (!string.IsNullOrWhiteSpace(balanced) && !string.Equals(balanced, candidate, StringComparison.Ordinal))
            {
                repaired = RemoveTrailingCommas(balanced);
                return true;
            }

            var completed = CompleteJson(candidate);
            completed = RemoveTrailingCommas(completed);
            if (!string.Equals(completed, candidate, StringComparison.Ordinal))
            {
                repaired = completed;
                return true;
            }

            var withoutTrailingCommas = RemoveTrailingCommas(candidate);
            if (!string.Equals(withoutTrailingCommas, candidate, StringComparison.Ordinal))
            {
                repaired = withoutTrailingCommas;
                return true;
            }

            return false;
        }

        private static string NormalizeJsonCandidate(string json)
        {
            var value = (json ?? string.Empty).Trim();
            if (value.StartsWith("```", StringComparison.Ordinal))
            {
                value = value.Substring(3).TrimStart();
            }

            var start = -1;
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] == '{' || value[i] == '[')
                {
                    start = i;
                    break;
                }
            }

            if (start < 0)
            {
                return string.Empty;
            }

            value = value.Substring(start).Trim();
            var fence = value.IndexOf("```", StringComparison.Ordinal);
            if (fence >= 0)
            {
                value = value.Substring(0, fence).Trim();
            }

            return value;
        }

        private static string CompleteJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return json;
            }

            var builder = new StringBuilder();
            var closers = new Stack<char>();
            var inString = false;
            var escaped = false;
            for (var i = 0; i < json.Length; i++)
            {
                var c = json[i];
                builder.Append(c);
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
                else if (c == '{')
                {
                    closers.Push('}');
                }
                else if (c == '[')
                {
                    closers.Push(']');
                }
                else if ((c == '}' || c == ']') && closers.Count > 0 && closers.Peek() == c)
                {
                    closers.Pop();
                }
            }

            if (inString)
            {
                builder.Append('"');
            }

            while (closers.Count > 0)
            {
                builder.Append(closers.Pop());
            }

            return builder.ToString();
        }

        private static string RemoveTrailingCommas(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return json;
            }

            var builder = new StringBuilder();
            var inString = false;
            var escaped = false;
            for (var i = 0; i < json.Length; i++)
            {
                var c = json[i];
                if (inString)
                {
                    builder.Append(c);
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
                    builder.Append(c);
                    continue;
                }

                if (c == ',')
                {
                    var next = NextNonWhitespace(json, i + 1);
                    if (next == '}' || next == ']')
                    {
                        continue;
                    }
                }

                builder.Append(c);
            }

            return builder.ToString();
        }

        private static char NextNonWhitespace(string value, int start)
        {
            for (var i = start; i < (value == null ? 0 : value.Length); i++)
            {
                if (!char.IsWhiteSpace(value[i]))
                {
                    return value[i];
                }
            }

            return '\0';
        }

        private static void AddDiagnostic(ToolCommandParseResult result, string code, string message, bool recovered)
        {
            if (result == null)
            {
                return;
            }

            result.Diagnostics.Add(new ToolCommandParseDiagnostic
            {
                Code = code,
                Message = message,
                Recovered = recovered
            });
        }

        private static void AddObject(JToken token, ICollection<ToolCommand> commands)
        {
            var obj = token as JObject;
            if (obj == null)
            {
                return;
            }

            var toolCalls = obj["tool_calls"] as JArray;
            if (toolCalls != null && toolCalls.Count > 0)
            {
                foreach (var call in toolCalls.Children())
                {
                    AddToolCall(call, commands);
                }
                return;
            }

            var steps = (obj["steps"] as JArray) ?? (obj["commands"] as JArray) ?? (obj["actions"] as JArray) ?? (obj["tools"] as JArray);
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

            var command = new ToolCommand
            {
                ToolId = id,
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

        private static void AddToolCall(JToken token, ICollection<ToolCommand> commands)
        {
            var obj = token as JObject;
            if (obj == null)
            {
                return;
            }

            var function = obj["function"] as JObject;
            if (function == null)
            {
                AddObject(token, commands);
                return;
            }

            var id = (string)function["name"];
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            AddObject(new JObject
            {
                ["toolId"] = id,
                ["arguments"] = function["arguments"] ?? new JObject()
            }, commands);
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
