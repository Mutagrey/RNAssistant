using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools.Contracts;
using TerminalResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Core.ModelProtocol
{
    public sealed class ToolResultWireReadResult
    {
        private readonly ResourceRef _resultResource;
        public bool Success { get { return Result != null; } }
        public string Error { get; private set; }
        public string ToolCallId { get; private set; }
        public string Name { get; private set; }
        public TerminalResult Result { get; private set; }
        public ResourceRef ResultResource
        {
            get { return _resultResource == null ? null : new ResourceRef(_resultResource.Uri, _resultResource.Revision); }
        }

        internal ToolResultWireReadResult(string error, string toolCallId = null, string name = null,
            TerminalResult result = null, ResourceRef resultResource = null)
        {
            Error = error;
            ToolCallId = toolCallId;
            Name = name;
            Result = result;
            _resultResource = resultResource == null ? null : new ResourceRef(resultResource.Uri, resultResource.Revision);
        }
    }

    // Only terminal model wire. Call authority, history pairing, runtime controls
    // and bounded result materialization belong to the callers of this boundary.
    public static class ToolResultWire
    {
        public const int CurrentVersion = 1;

        private static readonly Regex JsonLiteral = new Regex(
            @"\A(?:true|false|null|-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?)\z",
            RegexOptions.CultureInvariant);

        public static string Write(string toolCallId, string toolName, TerminalResult result, ResourceRef resultResource = null)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            return WriteParsed(toolCallId, toolName, result,
                ParseData(result.DataJson), resultResource);
        }

        public static JToken ParseData(string dataJson)
        {
            try
            {
                return dataJson == null ? JValue.CreateNull() : ReadJson(dataJson);
            }
            catch (JsonException ex)
            {
                throw new ArgumentException("Tool result data must be one strict JSON value.",
                    nameof(dataJson), ex);
            }
        }

        public static string WriteParsed(string toolCallId, string toolName,
            TerminalResult result, JToken data, ResourceRef resultResource = null)
        {
            if (string.IsNullOrWhiteSpace(toolCallId)) throw new ArgumentException("A tool call ID is required.", nameof(toolCallId));
            if (string.IsNullOrWhiteSpace(toolName)) throw new ArgumentException("An exact tool name is required.", nameof(toolName));
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (data == null || data.Type == JTokenType.Property || ContainsNonJsonValue(data))
                throw new ArgumentException("Parsed tool result data must be one strict JSON value.", nameof(data));

            var root = new JObject
            {
                ["tool_call_id"] = toolCallId,
                ["name"] = toolName,
                ["status"] = StatusName(result.Status),
                ["message"] = result.Message,
                ["data"] = data.DeepClone()
            };
            var references = result.Resources;
            var markedIndex = -1;
            for (var i = 0; i < references.Count; i++)
            {
                if (!IsResourceReference(references[i]))
                    throw new ArgumentException("Resources must be exact rna:// references with an optional string revision.", nameof(result));
                if (markedIndex < 0 && SameReference(references[i], resultResource)) markedIndex = i;
            }
            if (resultResource != null && markedIndex < 0)
                throw new ArgumentException("The full result resource must be an exact member of result.Resources.", nameof(resultResource));
            if (references.Count > 0)
            {
                var resources = new JArray();
                for (var i = 0; i < references.Count; i++)
                {
                    var reference = references[i];
                    var entry = new JObject { ["uri"] = reference.Uri };
                    if (reference.Revision != null) entry["revision"] = reference.Revision;
                    if (i == markedIndex) entry["relation"] = "result";
                    resources.Add(entry);
                }
                root["resources"] = resources;
            }
            return root.ToString(Formatting.None);
        }

        public static ToolResultWireReadResult Read(string json)
        {
            JObject root;
            try
            {
                root = ReadJson(json) as JObject;
            }
            catch (JsonException ex)
            {
                return Fail("Tool result is invalid JSON: " + ex.Message);
            }
            if (root == null) return Fail("Tool result must be one JSON object.");
            if (root.Properties().Any(property => property.Name != "tool_call_id" && property.Name != "name" &&
                property.Name != "status" && property.Name != "message" && property.Name != "data" && property.Name != "resources"))
                return Fail("Tool result contains an unsupported root field.");
            if (!IsNonemptyString(root["tool_call_id"]) || !IsNonemptyString(root["name"]))
                return Fail("Tool result requires string tool_call_id and name fields.");
            if (root["message"] == null || root["message"].Type != JTokenType.String || root.Property("data") == null)
                return Fail("Tool result requires a string message and a data field.");

            ToolResultStatus status;
            if (!TryStatus(root["status"], out status))
                return Fail("Tool result status must be exactly ok, error or unknown.");
            var references = new List<ResourceRef>();
            ResourceRef resultResource = null;
            var resourceProperty = root.Property("resources");
            if (resourceProperty != null)
            {
                var resources = resourceProperty.Value as JArray;
                if (resources == null) return Fail("Tool result resources must be an array.");
                foreach (var token in resources)
                {
                    var entry = token as JObject;
                    if (entry == null || entry.Properties().Any(property => property.Name != "uri" &&
                        property.Name != "revision" && property.Name != "relation") || !IsNonemptyString(entry["uri"]))
                        return Fail("Each resource must contain only uri, optional revision and optional result relation.");
                    var revision = entry.Property("revision");
                    if (revision != null && !IsNonemptyString(revision.Value))
                        return Fail("A present resource revision must be a non-empty string.");
                    var reference = new ResourceRef((string)entry["uri"], revision == null ? null : (string)revision.Value);
                    if (!IsResourceReference(reference)) return Fail("Tool result resources must use exact rna:// references.");
                    references.Add(reference);
                    var relation = entry.Property("relation");
                    if (relation != null)
                    {
                        if (relation.Value.Type != JTokenType.String || (string)relation.Value != "result" || resultResource != null)
                            return Fail("At most one resource may have relation result; no other relation is supported.");
                        resultResource = reference;
                    }
                }
            }
            var result = new TerminalResult(status, (string)root["message"], root["data"].ToString(Formatting.None), references);
            return new ToolResultWireReadResult(null, (string)root["tool_call_id"], (string)root["name"], result, resultResource);
        }

        private static ToolResultWireReadResult Fail(string error)
        {
            return new ToolResultWireReadResult(error);
        }

        private static string StatusName(ToolResultStatus status)
        {
            switch (status)
            {
                case ToolResultStatus.Ok: return "ok";
                case ToolResultStatus.Error: return "error";
                case ToolResultStatus.Unknown: return "unknown";
                default: throw new ArgumentOutOfRangeException(nameof(status));
            }
        }

        private static bool TryStatus(JToken token, out ToolResultStatus status)
        {
            status = ToolResultStatus.Error;
            if (token == null || token.Type != JTokenType.String) return false;
            switch ((string)token)
            {
                case "ok": status = ToolResultStatus.Ok; return true;
                case "error": status = ToolResultStatus.Error; return true;
                case "unknown": status = ToolResultStatus.Unknown; return true;
                default: return false;
            }
        }

        private static bool IsNonemptyString(JToken token)
        {
            return token != null && token.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)token);
        }

        private static bool IsResourceReference(ResourceRef reference)
        {
            if (reference == null || reference.Uri == null ||
                !string.Equals(reference.Uri, reference.Uri.Trim(), StringComparison.Ordinal) ||
                (reference.Revision != null && string.IsNullOrWhiteSpace(reference.Revision))) return false;
            try
            {
                ResourceAddress address;
                return ResourceUri.TryParse(reference.Uri, out address);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is FormatException)
            {
                return false;
            }
        }

        private static bool SameReference(ResourceRef first, ResourceRef second)
        {
            return second != null && string.Equals(first.Uri, second.Uri, StringComparison.Ordinal) &&
                string.Equals(first.Revision, second.Revision, StringComparison.Ordinal);
        }

        private static JToken ReadJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new JsonReaderException("A JSON value is required.");
            RejectJsonExtensions(json);
            JToken token;
            using (var reader = new JsonTextReader(new StringReader(json))
            {
                DateParseHandling = DateParseHandling.None,
                MaxDepth = 64
            })
            {
                token = JToken.ReadFrom(reader, new JsonLoadSettings
                {
                    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
                });
                if (reader.Read()) throw new JsonReaderException("More than one JSON value.");
            }
            if (ContainsNonJsonValue(token)) throw new JsonReaderException("Only JSON values and finite numbers are supported.");
            return token;
        }

        private static bool ContainsNonJsonValue(JToken token)
        {
            switch (token.Type)
            {
                case JTokenType.Object:
                case JTokenType.Array:
                case JTokenType.Property:
                    return token.Children().Any(ContainsNonJsonValue);
                case JTokenType.String:
                case JTokenType.Integer:
                case JTokenType.Boolean:
                case JTokenType.Null:
                    return false;
                case JTokenType.Float:
                    var value = ((JValue)token).Value;
                    return value is double && (double.IsNaN((double)value) || double.IsInfinity((double)value));
                default:
                    return true;
            }
        }

        // Json.NET accepts JavaScript extensions. This lexical guard precedes
        // its structural, depth and duplicate checks without interpreting strings.
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
