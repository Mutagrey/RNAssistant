using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Core.ModelProtocol
{
    internal static class ConversationResponseJson
    {
        private static readonly Regex JsonLiteral = new Regex(
            @"\A(?:true|false|null|-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?)\z",
            RegexOptions.CultureInvariant);

        internal static ConversationResponseParseResult Read(string content)
        {
            var raw = (content ?? string.Empty).Trim(' ', '\t', '\r', '\n');
            if (!raw.StartsWith("{", StringComparison.Ordinal) || !raw.EndsWith("}", StringComparison.Ordinal))
                return ConversationResponseParseResult.Fail("Conversation response must be one JSON object without markdown or surrounding prose.");

            JObject root;
            try
            {
                RejectJsonExtensions(raw);
                using (var reader = new JsonTextReader(new StringReader(raw))
                {
                    DateParseHandling = DateParseHandling.None,
                    MaxDepth = 64
                })
                {
                    root = JObject.Load(reader, new JsonLoadSettings
                    {
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
                    });
                    if (reader.Read()) throw new JsonReaderException("More than one JSON value.");
                }
                if (root.Descendants().OfType<JValue>().Any(value => value.Type == JTokenType.Float &&
                    value.Value is double && (double.IsNaN((double)value.Value) || double.IsInfinity((double)value.Value))))
                    throw new JsonReaderException("Non-finite numbers are not supported.");
            }
            catch (JsonException ex)
            {
                return ConversationResponseParseResult.Fail("Conversation response is invalid JSON: " + ex.Message);
            }

            var unsupported = root.Properties().FirstOrDefault(property => property.Name != "message" && property.Name != "tool_calls");
            if (unsupported != null)
                return ConversationResponseParseResult.Fail("Conversation response contains unsupported root field: " + unsupported.Name + ".");
            if (root["message"] == null || root["message"].Type != JTokenType.String)
                return ConversationResponseParseResult.Fail("Conversation response requires a string message field.");
            var calls = root["tool_calls"] as JArray;
            if (calls == null)
                return ConversationResponseParseResult.Fail("Conversation response requires a tool_calls array.");
            if (calls.Count > ConversationResponseSchemaBuilder.MaximumToolCalls)
                return ConversationResponseParseResult.Fail("tool_calls exceeds the maximum of " +
                    ConversationResponseSchemaBuilder.MaximumToolCalls + " calls per response.");

            var parsedCalls = new List<ConversationToolCall>();
            foreach (var token in calls)
            {
                var call = token as JObject;
                if (call == null || call.Properties().Any(property => property.Name != "name" && property.Name != "arguments"))
                    return ConversationResponseParseResult.Fail("Each tool call must contain only name and arguments; runtime assigns call IDs.");
                var name = call["name"] != null && call["name"].Type == JTokenType.String ? (string)call["name"] : null;
                var arguments = call["arguments"] as JObject;
                if (string.IsNullOrWhiteSpace(name) || arguments == null)
                    return ConversationResponseParseResult.Fail("Each tool call requires a non-empty string name and object arguments.");
                if (arguments.Properties().GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                    return ConversationResponseParseResult.Fail("Tool arguments must not contain duplicate names that differ only by case.");
                call.Property("arguments").Value = JValue.CreateNull();
                parsedCalls.Add(new ConversationToolCall { Name = name, Arguments = arguments });
            }
            return ConversationResponseParseResult.Ok(new ConversationResponse((string)root["message"], parsedCalls));
        }

        // Json.NET also accepts JavaScript syntax. Reject those extensions before its
        // structural/depth/duplicate checks, without changing strings or argument bytes.
        private static void RejectJsonExtensions(string raw)
        {
            for (var i = 0; i < raw.Length; i++)
            {
                var c = raw[i];
                if (IsWhitespace(c) || "{}[]:".IndexOf(c) >= 0) continue;
                if (c == ',')
                {
                    var next = i + 1;
                    while (next < raw.Length && IsWhitespace(raw[next])) next++;
                    if (next < raw.Length && (raw[next] == '}' || raw[next] == ']'))
                        throw new JsonReaderException("Trailing commas are not JSON.");
                    continue;
                }
                if (c == '"')
                {
                    var closed = false;
                    while (++i < raw.Length)
                    {
                        c = raw[i];
                        if (c == '"') { closed = true; break; }
                        if (c < 0x20) throw new JsonReaderException("Unescaped control character in string.");
                        if (c != '\\') continue;
                        if (++i >= raw.Length) break;
                        c = raw[i];
                        if ("\"\\/bfnrt".IndexOf(c) >= 0) continue;
                        if (c != 'u' || i + 4 >= raw.Length)
                            throw new JsonReaderException("Invalid JSON string escape.");
                        for (var digit = 0; digit < 4; digit++)
                            if (!Uri.IsHexDigit(raw[++i])) throw new JsonReaderException("Invalid Unicode escape.");
                    }
                    if (!closed) throw new JsonReaderException("Unterminated JSON string.");
                    continue;
                }
                var start = i;
                while (i < raw.Length && !IsWhitespace(raw[i]) && "{}[],:".IndexOf(raw[i]) < 0) i++;
                if (!JsonLiteral.IsMatch(raw.Substring(start, i - start)))
                    throw new JsonReaderException("Invalid JSON literal or unquoted property.");
                var after = i;
                while (after < raw.Length && IsWhitespace(raw[after])) after++;
                if (after < raw.Length && raw[after] == ':')
                    throw new JsonReaderException("JSON property names require double quotes.");
                i--;
            }
        }

        private static bool IsWhitespace(char value)
        {
            return value == ' ' || value == '\t' || value == '\r' || value == '\n';
        }
    }
}
