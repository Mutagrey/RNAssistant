using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Vba
{
    // Typed live snapshot shared by mutation guards, verification and document-tool discovery.
    // Access serialization and target binding remain the caller's HostRuntime responsibility.
    internal sealed class VbaModuleState
    {
        public string Name { get; set; }
        public string Code { get; set; }
        public string CodeSha256 { get; set; }
        public string ComponentType { get; set; }
        public bool? CodeOnlyUserForm { get; set; }
        public bool? HasToolManifest { get; set; }
        public bool HasCode { get; set; }
        public bool Truncated { get; set; }
        public int LineCount { get; set; }
    }

    // Owns VBA read command construction, payload validation and read-name normalization.
    // It does not own document gates, reconciliation, observations, mutations or Tool Result v1.
    internal sealed class VbaReader
    {
        private const string TruncatedMarker = "\n...[truncated]";
        private const int MaximumModuleCharacters = 1000000;
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly Func<string, string> _backendToolId;

        public VbaReader(IOfficeApplicationAdapter adapter, Func<string, string> backendToolId)
        {
            _adapter = adapter ?? throw new ArgumentNullException("adapter");
            _backendToolId = backendToolId ?? throw new ArgumentNullException("backendToolId");
        }

        public bool TryReadResourceModule(
            string requestedModuleName,
            int maxChars,
            out VbaModuleState module,
            out ToolResult result)
        {
            module = null;
            var moduleName = (requestedModuleName ?? string.Empty).Trim();
            result = ExecuteModuleRead(moduleName, maxChars);
            var normalizedName = NormalizeModuleName(moduleName);
            if (IsModuleNotFound(result) &&
                !string.Equals(moduleName, normalizedName, StringComparison.OrdinalIgnoreCase))
            {
                moduleName = normalizedName;
                result = ExecuteModuleRead(moduleName, maxChars);
            }
            ToolResult error;
            if (!TryParseModuleResult(result, moduleName, false, out module, out error))
            {
                result = error;
                return false;
            }
            return true;
        }

        public bool TryReadProject(out IReadOnlyList<VbaModuleState> modules, out ToolResult error)
        {
            modules = null;
            error = null;
            var result = _adapter.ExecuteTool(new ToolCommand
            {
                ToolId = _backendToolId("vba_list_project_components_internal")
            });
            if (result == null || !result.Success)
            {
                error = result ?? ToolResult.Fail(
                    "VBA project returned no result.",
                    null,
                    "vba_read_missing_result",
                    true);
                return false;
            }
            if (string.IsNullOrWhiteSpace(result.DataJson))
            {
                error = InvalidProject("VBA project returned no data.");
                return false;
            }

            try
            {
                var data = JObject.Parse(result.DataJson);
                var source = data["modules"] as JArray;
                if (source == null)
                {
                    error = InvalidProject("VBA project data has no modules array.");
                    return false;
                }
                var parsed = new List<VbaModuleState>();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var token in source)
                {
                    var item = token as JObject;
                    if (item == null)
                    {
                        error = InvalidProject("VBA project modules contain a non-object entry.");
                        return false;
                    }
                    var name = ReadRequiredString(item, "name");
                    if (!names.Add(name))
                    {
                        error = InvalidProject("VBA project data contains a duplicate module name: " + name + ".");
                        return false;
                    }
                    parsed.Add(new VbaModuleState
                    {
                        Name = name,
                        ComponentType = ReadRequiredString(item, "type"),
                        LineCount = Math.Max(0, ReadInt(item, "lineCount", 0)),
                        CodeOnlyUserForm = ReadNullableBool(item, "codeOnlyUserForm"),
                        HasToolManifest = ReadNullableBool(item, "hasToolManifest"),
                        Code = null,
                        HasCode = false
                    });
                }
                modules = parsed;
                return true;
            }
            catch (Exception ex) when (IsPayloadException(ex))
            {
                error = InvalidProject("Could not parse VBA project: " + ex.Message);
                return false;
            }
        }

        public bool TryReadModule(string moduleName, int maxChars, out VbaModuleState module, out ToolResult error)
        {
            var current = ExecuteModuleRead(moduleName, maxChars);
            return TryParseModuleResult(current, moduleName, true, out module, out error);
        }

        private static bool TryParseModuleResult(
            ToolResult current,
            string moduleName,
            bool requireComplete,
            out VbaModuleState module,
            out ToolResult error)
        {
            module = null;
            error = null;
            if (current == null || !current.Success || string.IsNullOrWhiteSpace(current.DataJson))
            {
                error = current == null
                    ? ToolResult.Fail("VBA module read returned no result.", null, "vba_read_missing_result", true)
                    : current.Success
                        ? ToolResult.Fail("VBA module returned no data.", current.DataJson, "vba_read_invalid", true)
                        : current;
                return false;
            }

            try
            {
                var data = JObject.Parse(current.DataJson);
                if (data["code"] == null || data["code"].Type == JTokenType.Null)
                {
                    error = ToolResult.Fail(
                        "VBA module data has no code field.",
                        current.DataJson,
                        "vba_read_invalid",
                        true);
                    return false;
                }
                var code = ReadOptionalString(data, "code", string.Empty);
                var resolvedName = ReadOptionalString(data, "name", moduleName);
                if (!string.Equals(resolvedName, moduleName, StringComparison.OrdinalIgnoreCase))
                {
                    error = ToolResult.Fail(
                        "VBA module read returned a different component: " + resolvedName + ".",
                        current.DataJson,
                        "vba_read_invalid",
                        true);
                    return false;
                }
                var codeSha256 = ReadOptionalSha256(data, "codeSha256");
                var truncated = ReadNullableBool(data, "truncated") == true;
                var markerPresent = code.EndsWith(TruncatedMarker, StringComparison.Ordinal);
                if (truncated != markerPresent)
                {
                    error = ToolResult.Fail(
                        "VBA module truncation metadata does not match its source payload.",
                        current.DataJson,
                        "vba_read_invalid",
                        true);
                    return false;
                }
                if (!truncated && !string.IsNullOrWhiteSpace(codeSha256) &&
                    !string.Equals(codeSha256, VbaTextCanonicalizer.LiveCodeSha256(code), StringComparison.OrdinalIgnoreCase))
                {
                    error = ToolResult.Fail(
                        "VBA module hash does not match its complete source payload.",
                        current.DataJson,
                        "vba_read_invalid",
                        true);
                    return false;
                }
                module = new VbaModuleState
                {
                    Name = resolvedName,
                    Code = code,
                    CodeSha256 = codeSha256,
                    ComponentType = ReadOptionalString(data, "type", string.Empty),
                    CodeOnlyUserForm = ReadNullableBool(data, "codeOnlyUserForm"),
                    HasCode = true,
                    Truncated = truncated,
                    LineCount = Math.Max(0, ReadInt(data, "lineCount", VbaTextCanonicalizer.LiveCodeLineCount(code)))
                };
            }
            catch (Exception ex) when (IsPayloadException(ex))
            {
                error = ToolResult.Fail(
                    "Could not parse VBA module data: " + ex.Message,
                    current.DataJson,
                    "vba_read_invalid",
                    true);
                return false;
            }

            if (requireComplete && module.Truncated)
            {
                error = ToolResult.Fail("VBA module is too large for a safe patch.");
                module = null;
                return false;
            }
            return true;
        }

        private ToolResult ExecuteModuleRead(string moduleName, int maxChars)
        {
            var read = new ToolCommand { ToolId = _backendToolId("vba_read_module") };
            read.Arguments["moduleName"] = moduleName;
            read.Arguments["maxChars"] = Math.Max(1, Math.Min(MaximumModuleCharacters, maxChars));
            return _adapter.ExecuteTool(read);
        }

        private static ToolResult InvalidProject(string message)
        {
            return ToolResult.Fail(message, null, "vba_read_invalid", true);
        }

        private static int ReadInt(JObject data, string name, int fallback)
        {
            var token = data == null ? null : data[name];
            if (token == null || token.Type == JTokenType.Null) return fallback;
            if (token.Type != JTokenType.Integer)
            {
                throw new FormatException("VBA snapshot field '" + name + "' must be an integer.");
            }
            return token.Value<int>();
        }

        private static bool? ReadNullableBool(JObject data, string name)
        {
            var token = data == null ? null : data[name];
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.Boolean)
            {
                throw new FormatException("VBA snapshot field '" + name + "' must be a boolean.");
            }
            return token.Value<bool>();
        }

        private static string ReadRequiredString(JObject data, string name)
        {
            var value = ReadOptionalString(data, name, null);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new FormatException("VBA snapshot field '" + name + "' is missing or empty.");
            }
            return value;
        }

        private static string ReadOptionalString(JObject data, string name, string fallback)
        {
            var token = data == null ? null : data[name];
            if (token == null || token.Type == JTokenType.Null) return fallback;
            if (token.Type != JTokenType.String)
            {
                throw new FormatException("VBA snapshot field '" + name + "' must be a string.");
            }
            return token.Value<string>();
        }

        private static string ReadOptionalSha256(JObject data, string name)
        {
            var value = ReadOptionalString(data, name, null);
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new FormatException("VBA snapshot field '" + name + "' must be a SHA-256 hex value.");
            }
            return value;
        }

        private static bool IsPayloadException(Exception exception)
        {
            return exception is JsonException || exception is FormatException ||
                   exception is InvalidCastException || exception is OverflowException ||
                   exception is ArgumentException;
        }

        public static bool IsModuleNotFound(ToolResult result)
        {
            return result != null &&
                (string.Equals(result.ErrorCode, "vba_module_not_found", StringComparison.OrdinalIgnoreCase) ||
                 (result.Message ?? string.Empty).IndexOf("VBA module not found", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static string NormalizeModuleName(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (VbaToolManifestParser.ValidComponentName(value)) return value;

            var normalized = new StringBuilder();
            foreach (var character in value)
            {
                var valid = character >= 'A' && character <= 'Z' ||
                            character >= 'a' && character <= 'z' ||
                            character >= '0' && character <= '9' ||
                            character == '_';
                if (valid)
                {
                    normalized.Append(character);
                }
                else if (normalized.Length > 0 && normalized[normalized.Length - 1] != '_')
                {
                    normalized.Append('_');
                }
            }

            var candidate = normalized.ToString().Trim('_');
            if (string.IsNullOrWhiteSpace(candidate)) candidate = "Module";
            if (!IsAsciiLetter(candidate[0])) candidate = "Module_" + candidate;
            if (string.IsNullOrWhiteSpace(candidate) || !IsAsciiLetter(candidate[0])) candidate = "Module";
            var suffix = "_" + TextPatternEngine.Sha256(value).Substring(0, 8);
            var maxBaseLength = 31 - suffix.Length;
            if (candidate.Length > maxBaseLength) candidate = candidate.Substring(0, maxBaseLength).TrimEnd('_');
            if (string.IsNullOrWhiteSpace(candidate)) candidate = "Module";
            return candidate + suffix;
        }

        private static bool IsAsciiLetter(char value)
        {
            return value >= 'A' && value <= 'Z' || value >= 'a' && value <= 'z';
        }
    }
}
