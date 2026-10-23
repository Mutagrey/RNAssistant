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
                return ConversationResponseParseResult.Fail(ConversationResponseError.InvalidJson, "Conversation response must be one JSON object without markdown or surrounding prose.");

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
                return ConversationResponseParseResult.Fail(ConversationResponseError.InvalidJson, "Conversation response is invalid JSON: " + ex.Message);
            }

            var action = root["action"] != null && root["action"].Type == JTokenType.String ? (string)root["action"] : null;
            string specialized;
            switch (action)
            {
                case ConversationResponse.ExecuteAction: specialized = "tool_calls"; break;
                case ConversationResponse.ReflectionAction: specialized = "decision"; break;
                case ConversationResponse.FinishAction: specialized = "results"; break;
                default: return Fail(ConversationResponseError.InvalidAction, "action must be execute, reflection or finish.");
            }
            var unsupported = root.Properties().FirstOrDefault(property =>
                property.Name != "action" && property.Name != "reason" && property.Name != "message" && property.Name != specialized);
            if (unsupported != null)
                return Fail(ConversationResponseError.WrongActionFields, "action=" + action + " forbids root field " + unsupported.Name + ".");
            if (!Nonblank(root["reason"]))
                return Fail(ConversationResponseError.MissingReason, "A nonblank string reason is required.");
            if (root["message"] == null || root["message"].Type != JTokenType.String)
                return Fail(ConversationResponseError.MissingMessage, "A string message is required; an empty string is allowed.");
            var reason = (string)root["reason"];
            var message = (string)root["message"];
            if (action == ConversationResponse.ExecuteAction) return ReadExecute(root, reason, message);
            if (action == ConversationResponse.FinishAction) return ReadFinish(root, reason, message);
            if (!Nonblank(root["decision"]))
                return Fail(ConversationResponseError.MissingDecision, "reflection requires a nonblank string decision.");
            return ConversationResponseParseResult.Ok(new ConversationResponse(action, reason, message, decision: (string)root["decision"]));
        }

        private static ConversationResponseParseResult ReadExecute(JObject root, string reason, string message)
        {
            var calls = root["tool_calls"] as JArray;
            if (calls == null || calls.Count == 0)
                return Fail(ConversationResponseError.EmptyExecuteCalls, "execute requires a nonempty tool_calls array.");
            if (calls.Count > ConversationResponseSchemaBuilder.MaximumToolCalls)
                return Fail(ConversationResponseError.TooManyCalls, "tool_calls exceeds the maximum of " + ConversationResponseSchemaBuilder.MaximumToolCalls + ".");
            var parsedCalls = new List<ConversationToolCall>();
            foreach (var token in calls)
            {
                var call = token as JObject;
                if (call == null || call.Properties().Any(property => property.Name != "name" && property.Name != "arguments"))
                    return Fail(ConversationResponseError.InvalidToolCall, "Each call contains only name and arguments; runtime assigns call IDs.");
                var arguments = call["arguments"] as JObject;
                if (!Nonblank(call["name"]) || arguments == null)
                    return Fail(ConversationResponseError.InvalidToolCall, "Each call requires a nonblank string name and object arguments.");
                if (arguments.Properties().GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                    return Fail(ConversationResponseError.InvalidToolCall, "Tool arguments must not contain duplicate names that differ only by case.");
                parsedCalls.Add(new ConversationToolCall { Name = (string)call["name"], Arguments = arguments });
            }
            return ConversationResponseParseResult.Ok(new ConversationResponse(ConversationResponse.ExecuteAction, reason, message, calls: parsedCalls));
        }

        private static ConversationResponseParseResult ReadFinish(JObject root, string reason, string message)
        {
            var results = root["results"] as JArray;
            if (results == null)
                return Fail(ConversationResponseError.MissingFinishResults, "finish requires a results array, even for a direct answer without tools.");
            var claims = new List<ConversationResultClaim>();
            foreach (var token in results)
            {
                var claim = token as JObject;
                if (claim == null || claim.Properties().Any(property => property.Name != "summary" && property.Name != "evidence" && property.Name != "assessment"))
                    return Fail(ConversationResponseError.InvalidFinishResults, "Each result contains only summary, evidence and assessment.");
                var evidence = claim["evidence"] as JArray;
                if (!Nonblank(claim["summary"]) || evidence == null || evidence.Any(item => !Nonblank(item)))
                    return Fail(ConversationResponseError.InvalidFinishResults, "Each result requires a nonblank summary and evidence array of nonblank strings.");
                if (evidence.Values<string>().Distinct(StringComparer.Ordinal).Count() != evidence.Count)
                    return Fail(ConversationResponseError.DuplicateEvidence, "Evidence references must be unique within each claim.");
                ConversationAssessment assessment;
                if (claim["assessment"] == null || claim["assessment"].Type != JTokenType.String ||
                    !ConversationResultClaim.TryReadAssessment((string)claim["assessment"], out assessment))
                    return Fail(ConversationResponseError.InvalidAssessment, "assessment must be satisfied, partially_satisfied, unverified or not_satisfied.");
                claims.Add(new ConversationResultClaim((string)claim["summary"], evidence.Values<string>(), assessment));
            }
            // Referential/run/terminal checks belong to kernel admission, before acceptance.
            return ConversationResponseParseResult.Ok(new ConversationResponse(ConversationResponse.FinishAction, reason, message, results: claims));
        }

        private static bool Nonblank(JToken token)
        {
            return token != null && token.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)token);
        }

        private static ConversationResponseParseResult Fail(ConversationResponseError kind, string message)
        {
            return ConversationResponseParseResult.Fail(kind, message);
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
