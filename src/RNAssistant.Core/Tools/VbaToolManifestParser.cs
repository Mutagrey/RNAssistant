using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Tools
{
    public sealed class VbaToolManifestParseResult
    {
        public ToolCatalogEntry Tool { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }

        public bool Success { get { return Tool != null && string.IsNullOrWhiteSpace(ErrorCode); } }

        public static VbaToolManifestParseResult Fail(string code, string message)
        {
            return new VbaToolManifestParseResult { ErrorCode = code, ErrorMessage = message };
        }
    }

    public sealed class VbaToolManifestParser
    {
        private const string OpenMarker = "<RNAssistantTool>";
        private const string CloseMarker = "</RNAssistantTool>";
        private static readonly Regex IdentifierPattern = new Regex("^[A-Za-z][A-Za-z0-9_]{0,39}$", RegexOptions.CultureInvariant);
        private static readonly Regex ComponentNamePattern = new Regex("^[A-Za-z][A-Za-z0-9_]{0,30}$", RegexOptions.CultureInvariant);

        public VbaToolManifestParseResult Parse(string code)
        {
            code = code ?? string.Empty;
            var start = code.IndexOf(OpenMarker, StringComparison.Ordinal);
            var end = code.IndexOf(CloseMarker, StringComparison.Ordinal);
            if (start < 0 || end < start) return VbaToolManifestParseResult.Fail("manifest_missing", "VBA tool manifest markers were not found.");
            try
            {
                var manifest = JObject.Parse(StripCommentPrefixes(code.Substring(start + OpenMarker.Length, end - start - OpenMarker.Length)));
                var components = ReadStringArray(manifest["components"]);
                if (components.Count == 0) return VbaToolManifestParseResult.Fail("manifest_components", "components must identify the entry module first.");
                return Parse(components[0], code);
            }
            catch (JsonException ex)
            {
                return VbaToolManifestParseResult.Fail("manifest_invalid_json", ex.Message);
            }
        }

        public VbaToolManifestParseResult Parse(string moduleName, string code)
        {
            code = code ?? string.Empty;
            if (!ValidComponentName(moduleName)) return VbaToolManifestParseResult.Fail("invalid_component_name", "VBA component name must start with a letter, contain only letters/numbers/underscore, and be at most 31 characters.");
            var start = code.IndexOf(OpenMarker, StringComparison.Ordinal);
            var end = code.IndexOf(CloseMarker, StringComparison.Ordinal);
            if (start < 0 || end < start) return VbaToolManifestParseResult.Fail("manifest_missing", "VBA tool manifest markers were not found.");
            if (code.IndexOf(OpenMarker, start + OpenMarker.Length, StringComparison.Ordinal) >= 0) return VbaToolManifestParseResult.Fail("multiple_manifests", "Only one VBA tool manifest is allowed per entry module.");

            var manifestText = StripCommentPrefixes(code.Substring(start + OpenMarker.Length, end - start - OpenMarker.Length));
            JObject manifest;
            try
            {
                manifest = JObject.Parse(manifestText, new JsonLoadSettings
                {
                    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
                });
            }
            catch (JsonException ex) { return VbaToolManifestParseResult.Fail("manifest_invalid_json", ex.Message); }

            var allowed = new HashSet<string>(new[]
            {
                "protocolVersion", "id", "name", "description", "host", "packageVersion", "entryPoint",
                "components", "argumentOrder", "parameters", "mutatesDocument", "agentCanRun", "requiresConfirmation"
            }, StringComparer.Ordinal);
            var extra = manifest.Properties().FirstOrDefault(property => !allowed.Contains(property.Name));
            if (extra != null) return VbaToolManifestParseResult.Fail("manifest_unexpected_field", "Unsupported manifest field: " + extra.Name);
            if ((int?)manifest["protocolVersion"] != 1) return VbaToolManifestParseResult.Fail("manifest_version", "protocolVersion must be 1.");

            var id = StringValue(manifest["id"]);
            var host = NormalizeHost(StringValue(manifest["host"]));
            var entryPoint = StringValue(manifest["entryPoint"]);
            if (string.IsNullOrWhiteSpace(id) || id.Any(char.IsWhiteSpace)) return VbaToolManifestParseResult.Fail("manifest_id", "Manifest id is required and cannot contain whitespace.");
            if (string.IsNullOrWhiteSpace(host)) return VbaToolManifestParseResult.Fail("manifest_host", "host must be Excel, Word, or PowerPoint.");
            if (!ValidIdentifier(entryPoint)) return VbaToolManifestParseResult.Fail("invalid_entry_point", "entryPoint must be a valid VBA identifier of at most 40 characters.");

            var components = ReadStringArray(manifest["components"]);
            if (components.Count == 0 || !string.Equals(components[0], moduleName, StringComparison.OrdinalIgnoreCase))
            {
                return VbaToolManifestParseResult.Fail("manifest_components", "components must list the entry module first.");
            }
            if (components.Any(name => !ValidComponentName(name)) || components.Distinct(StringComparer.OrdinalIgnoreCase).Count() != components.Count)
            {
                return VbaToolManifestParseResult.Fail("manifest_components", "components must contain unique valid VBA component names.");
            }

            var argumentOrder = ReadStringArray(manifest["argumentOrder"]);
            if (argumentOrder.Distinct(StringComparer.OrdinalIgnoreCase).Count() != argumentOrder.Count)
            {
                return VbaToolManifestParseResult.Fail("argument_order", "argumentOrder contains duplicate names.");
            }
            if (argumentOrder.Count > 30)
            {
                return VbaToolManifestParseResult.Fail("argument_limit", "VBA tool entry functions support at most 30 positional arguments.");
            }
            var parameters = manifest["parameters"] as JObject;
            if (parameters == null) return VbaToolManifestParseResult.Fail("parameters_schema", "parameters must be a formal JSON Schema object.");

            var tool = new ToolCatalogEntry
            {
                Id = id,
                Host = host,
                Name = StringValue(manifest["name"]) ?? id,
                Description = StringValue(manifest["description"]) ?? string.Empty,
                PackageVersion = StringValue(manifest["packageVersion"]) ?? "1.0.0",
                EntryPoint = entryPoint,
                ArgumentOrder = argumentOrder,
                ArgumentSchemaJson = parameters.ToString(Formatting.None),
                Executor = "vba",
                MutatesDocument = BoolValue(manifest["mutatesDocument"], true),
                AgentCanRun = BoolValue(manifest["agentCanRun"], false),
                RequiresConfirmation = BoolValue(manifest["requiresConfirmation"], true),
                RiskLevel = BoolValue(manifest["mutatesDocument"], true) ? 3 : 2,
                Enabled = true,
                BuiltIn = false,
                Code = code,
                Scope = "global"
            };
            var entryCodeSha256 = VbaTextCanonicalizer.PackageCodeSha256(code);
            tool.Components = components.Select(name => new ToolPackageComponentDefinition
            {
                Name = name,
                Type = string.Equals(name, moduleName, StringComparison.OrdinalIgnoreCase) ? "StdModule" : string.Empty,
                FileName = name + (string.Equals(name, moduleName, StringComparison.OrdinalIgnoreCase) ? ".bas" : string.Empty),
                Code = string.Equals(name, moduleName, StringComparison.OrdinalIgnoreCase) ? code : string.Empty,
                CodeSha256 = string.Equals(name, moduleName, StringComparison.OrdinalIgnoreCase) ? entryCodeSha256 : string.Empty
            }).ToList();

            JObject normalizedSchema;
            string schemaError;
            if (!ToolSchemaSupport.TryParse(tool, out normalizedSchema, out schemaError)) return VbaToolManifestParseResult.Fail("parameters_schema", schemaError);
            var signature = ParseFunctionSignature(code.Substring(end + CloseMarker.Length), entryPoint);
            if (!signature.Success) return VbaToolManifestParseResult.Fail(signature.ErrorCode, signature.ErrorMessage);
            if (signature.Parameters.Count != argumentOrder.Count) return VbaToolManifestParseResult.Fail("signature_arguments", "Function parameters must match argumentOrder exactly.");

            var properties = normalizedSchema["properties"] as JObject ?? new JObject();
            var required = new HashSet<string>((normalizedSchema["required"] as JArray ?? new JArray()).Values<string>(), StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < argumentOrder.Count; index++)
            {
                var argumentName = argumentOrder[index];
                var parameter = signature.Parameters[index];
                var propertySchema = properties[argumentName] as JObject;
                if (propertySchema == null) return VbaToolManifestParseResult.Fail("argument_schema_property", "argumentOrder property is missing from parameters: " + argumentName);
                if (!string.Equals(parameter.Name, argumentName, StringComparison.OrdinalIgnoreCase)) return VbaToolManifestParseResult.Fail("signature_arguments", "Function parameter order/name does not match argumentOrder at " + argumentName + ".");
                var expectedType = VbaType((string)propertySchema["type"]);
                if (expectedType == null || !string.Equals(parameter.Type, expectedType, StringComparison.OrdinalIgnoreCase))
                {
                    return VbaToolManifestParseResult.Fail("signature_type", argumentName + " must use VBA type " + (expectedType ?? "String/Long/Double/Boolean") + ".");
                }
                if (!required.Contains(argumentName) && propertySchema["default"] == null)
                {
                    return VbaToolManifestParseResult.Fail("optional_default", "Optional VBA argument requires a JSON Schema default: " + argumentName);
                }
            }
            var unknownProperty = properties.Properties().FirstOrDefault(property => !argumentOrder.Contains(property.Name, StringComparer.OrdinalIgnoreCase));
            if (unknownProperty != null) return VbaToolManifestParseResult.Fail("argument_order", "parameters property is missing from argumentOrder: " + unknownProperty.Name);
            return new VbaToolManifestParseResult { Tool = tool };
        }

        public static bool ValidIdentifier(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && IdentifierPattern.IsMatch(value);
        }

        public static bool ValidComponentName(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && ComponentNamePattern.IsMatch(value);
        }

        public static bool ContainsUserFormDesignerExport(string code)
        {
            var normalized = (code ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            return Regex.IsMatch(normalized, "(?im)^\\s*VERSION\\s+5\\.00\\b") ||
                Regex.IsMatch(normalized, "(?im)^\\s*Begin\\s+(?:\\{|VB\\.|MSForms\\.)") ||
                Regex.IsMatch(normalized, "(?im)^\\s*OleObjectBlob\\s*=") ||
                Regex.IsMatch(normalized, "(?im)^\\s*Attribute\\s+VB_Base\\s*=");
        }

        private static VbaSignatureResult ParseFunctionSignature(string trailingCode, string entryPoint)
        {
            var flattened = Regex.Replace(trailingCode ?? string.Empty, "_\\s*(?:\\r?\\n)", " ");
            var pattern = "^\\s*Public\\s+Function\\s+" + Regex.Escape(entryPoint) + "\\s*\\((.*?)\\)\\s+As\\s+String\\b";
            var match = Regex.Match(flattened, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
            if (!match.Success) return VbaSignatureResult.Fail("entry_signature", "The manifest must immediately precede Public Function " + entryPoint + "(...) As String.");
            var signature = new VbaSignatureResult { Success = true };
            var raw = match.Groups[1].Value.Trim();
            if (raw.Length == 0) return signature;
            foreach (var part in raw.Split(','))
            {
                var parameter = Regex.Match(part.Trim(), "^ByVal\\s+([A-Za-z][A-Za-z0-9_]*)\\s+As\\s+(String|Long|Double|Boolean)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!parameter.Success) return VbaSignatureResult.Fail("entry_signature", "Every entry parameter must be ByVal name As String/Long/Double/Boolean.");
                signature.Parameters.Add(new VbaParameter { Name = parameter.Groups[1].Value, Type = parameter.Groups[2].Value });
            }
            return signature;
        }

        private static string StripCommentPrefixes(string value)
        {
            return string.Join("\n", (value ?? string.Empty).Replace("\r\n", "\n").Split('\n').Select(line =>
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("'", StringComparison.Ordinal)) trimmed = trimmed.Substring(1);
                return trimmed.StartsWith(" ", StringComparison.Ordinal) ? trimmed.Substring(1) : trimmed;
            }).ToArray()).Trim();
        }

        private static List<string> ReadStringArray(JToken token)
        {
            var array = token as JArray;
            return array == null ? new List<string>() : array.Values<string>().Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).ToList();
        }

        private static string VbaType(string jsonType)
        {
            switch ((jsonType ?? string.Empty).ToLowerInvariant())
            {
                case "string": return "String";
                case "integer": return "Long";
                case "number": return "Double";
                case "boolean": return "Boolean";
                default: return null;
            }
        }

        private static string NormalizeHost(string host)
        {
            if (string.Equals(host, "excel", StringComparison.OrdinalIgnoreCase)) return "Excel";
            if (string.Equals(host, "word", StringComparison.OrdinalIgnoreCase)) return "Word";
            if (string.Equals(host, "powerpoint", StringComparison.OrdinalIgnoreCase)) return "PowerPoint";
            return null;
        }

        private static string StringValue(JToken token) { return token == null || token.Type != JTokenType.String ? null : token.Value<string>(); }
        private static bool BoolValue(JToken token, bool fallback) { return token == null || token.Type != JTokenType.Boolean ? fallback : token.Value<bool>(); }

        private sealed class VbaSignatureResult
        {
            public bool Success { get; set; }
            public string ErrorCode { get; set; }
            public string ErrorMessage { get; set; }
            public List<VbaParameter> Parameters { get; private set; }
            public VbaSignatureResult() { Parameters = new List<VbaParameter>(); }
            public static VbaSignatureResult Fail(string code, string message) { return new VbaSignatureResult { ErrorCode = code, ErrorMessage = message }; }
        }

        private sealed class VbaParameter
        {
            public string Name { get; set; }
            public string Type { get; set; }
        }
    }
}
