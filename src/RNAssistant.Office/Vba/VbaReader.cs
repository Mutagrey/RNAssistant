using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.Vba;

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

    // Owns typed VBA snapshot validation and read-name normalization.
    // It does not own document gates, reconciliation, observations, mutations or Tool Result v1.
    internal sealed class VbaReader
    {
        private const string TruncatedMarker = "\n...[truncated]";
        private const int MaximumModuleCharacters = 1000000;
        private readonly IVbaHostBackend _backend;

        public VbaReader(IVbaHostBackend backend)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        public bool TryReadResourceModule(
            string requestedModuleName,
            int maxChars,
            out VbaModuleState module,
            out ToolResult result)
        {
            module = null;
            var moduleName = (requestedModuleName ?? string.Empty).Trim();
            VbaModuleSnapshot snapshot;
            VbaBackendException backendError;
            if (!TryExecuteModuleRead(
                moduleName, maxChars, out snapshot, out backendError))
            {
                var normalized = NormalizeModuleName(moduleName);
                if (IsModuleNotFound(backendError) &&
                    !string.Equals(
                        moduleName,
                        normalized,
                        StringComparison.OrdinalIgnoreCase))
                {
                    moduleName = normalized;
                    if (!TryExecuteModuleRead(
                        moduleName,
                        maxChars,
                        out snapshot,
                        out backendError))
                    {
                        result = BackendError(backendError);
                        return false;
                    }
                }
                else
                {
                    result = BackendError(backendError);
                    return false;
                }
            }
            ToolResult error;
            if (!TryValidateModuleSnapshot(
                snapshot, moduleName, false, out module, out error))
            {
                result = error;
                return false;
            }
            result = ModuleToolResult(module);
            return true;
        }

        public bool TryReadProject(out IReadOnlyList<VbaModuleState> modules, out ToolResult error)
        {
            modules = null;
            error = null;
            VbaProjectSnapshot snapshot;
            try
            {
                snapshot = _backend.ListProjectComponents();
            }
            catch (VbaBackendException ex)
            {
                error = BackendError(ex);
                return false;
            }
            catch (Exception ex)
            {
                error = InvalidProject(
                    "Could not read VBA project: " + ex.Message);
                return false;
            }
            if (snapshot == null || snapshot.Modules == null)
            {
                error = InvalidProject("VBA project data has no modules collection.");
                return false;
            }

            try
            {
                var parsed = new List<VbaModuleState>();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in snapshot.Modules)
                {
                    if (item == null)
                    {
                        error = InvalidProject(
                            "VBA project modules contain a null entry.");
                        return false;
                    }
                    var name = RequireSnapshotString(item.Name, "name");
                    if (!names.Add(name))
                    {
                        error = InvalidProject("VBA project data contains a duplicate module name: " + name + ".");
                        return false;
                    }
                    parsed.Add(new VbaModuleState
                    {
                        Name = name,
                        ComponentType = RequireSnapshotString(
                            item.ComponentType, "type"),
                        LineCount = Math.Max(0, item.LineCount),
                        CodeOnlyUserForm = item.CodeOnlyUserForm,
                        HasToolManifest = item.HasToolManifest,
                        Code = null,
                        HasCode = false
                    });
                }
                modules = parsed;
                return true;
            }
            catch (FormatException ex)
            {
                error = InvalidProject("Could not parse VBA project: " + ex.Message);
                return false;
            }
        }

        public bool TryReadModule(string moduleName, int maxChars, out VbaModuleState module, out ToolResult error)
        {
            VbaModuleSnapshot snapshot;
            VbaBackendException backendError;
            if (!TryExecuteModuleRead(
                moduleName, maxChars, out snapshot, out backendError))
            {
                module = null;
                error = BackendError(backendError);
                return false;
            }
            return TryValidateModuleSnapshot(
                snapshot, moduleName, true, out module, out error);
        }

        private static bool TryValidateModuleSnapshot(
            VbaModuleSnapshot snapshot,
            string moduleName,
            bool requireComplete,
            out VbaModuleState module,
            out ToolResult error)
        {
            module = null;
            error = null;
            if (snapshot == null)
            {
                error = ToolResult.Fail(
                    "VBA module read returned no snapshot.",
                    null,
                    "vba_read_missing_result",
                    true);
                return false;
            }

            try
            {
                if (snapshot.Code == null)
                {
                    error = ToolResult.Fail(
                        "VBA module data has no code field.",
                        SnapshotJson(snapshot),
                        "vba_read_invalid",
                        true);
                    return false;
                }
                var code = snapshot.Code;
                var resolvedName = string.IsNullOrWhiteSpace(snapshot.Name)
                    ? moduleName : snapshot.Name;
                if (!string.Equals(resolvedName, moduleName, StringComparison.OrdinalIgnoreCase))
                {
                    error = ToolResult.Fail(
                        "VBA module read returned a different component: " + resolvedName + ".",
                        SnapshotJson(snapshot),
                        "vba_read_invalid",
                        true);
                    return false;
                }
                var codeSha256 = ValidateOptionalSha256(snapshot.CodeSha256);
                var truncated = snapshot.Truncated;
                var markerPresent = code.EndsWith(TruncatedMarker, StringComparison.Ordinal);
                if (truncated != markerPresent)
                {
                    error = ToolResult.Fail(
                        "VBA module truncation metadata does not match its source payload.",
                        SnapshotJson(snapshot),
                        "vba_read_invalid",
                        true);
                    return false;
                }
                if (!truncated && !string.IsNullOrWhiteSpace(codeSha256) &&
                    !string.Equals(codeSha256, VbaTextCanonicalizer.LiveCodeSha256(code), StringComparison.OrdinalIgnoreCase))
                {
                    error = ToolResult.Fail(
                        "VBA module hash does not match its complete source payload.",
                        SnapshotJson(snapshot),
                        "vba_read_invalid",
                        true);
                    return false;
                }
                module = new VbaModuleState
                {
                    Name = resolvedName,
                    Code = code,
                    CodeSha256 = codeSha256,
                    ComponentType = snapshot.ComponentType ?? string.Empty,
                    CodeOnlyUserForm = snapshot.CodeOnlyUserForm,
                    HasCode = true,
                    Truncated = truncated,
                    LineCount = Math.Max(0, snapshot.LineCount)
                };
            }
            catch (FormatException ex)
            {
                error = ToolResult.Fail(
                    "Could not parse VBA module data: " + ex.Message,
                    SnapshotJson(snapshot),
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

        private bool TryExecuteModuleRead(
            string moduleName,
            int maxChars,
            out VbaModuleSnapshot snapshot,
            out VbaBackendException error)
        {
            try
            {
                snapshot = _backend.ReadModule(new VbaReadModuleRequest
                {
                    ModuleName = moduleName,
                    MaxChars = Math.Max(
                        1,
                        Math.Min(MaximumModuleCharacters, maxChars))
                });
                error = null;
                return true;
            }
            catch (VbaBackendException ex)
            {
                snapshot = null;
                error = ex;
                return false;
            }
            catch (Exception ex)
            {
                snapshot = null;
                error = new VbaBackendException(
                    ex.Message,
                    "vba_access_error",
                    false,
                    null,
                    ex);
                return false;
            }
        }

        private static ToolResult InvalidProject(string message)
        {
            return ToolResult.Fail(message, null, "vba_read_invalid", true);
        }

        private static string RequireSnapshotString(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new FormatException(
                    "VBA snapshot field '" + name + "' is missing or empty.");
            return value;
        }

        private static string ValidateOptionalSha256(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new FormatException(
                    "VBA snapshot field 'codeSha256' must be a SHA-256 hex value.");
            }
            return value;
        }

        private static ToolResult BackendError(VbaBackendException error)
        {
            if (error == null)
                return ToolResult.Fail(
                    "VBA backend returned no error.",
                    null,
                    "vba_read_missing_result",
                    true);
            return ToolResult.Fail(
                error.Message,
                error.Details == null
                    ? null : error.Details.ToString(Formatting.None),
                error.ErrorCode,
                error.Retryable);
        }

        private static ToolResult ModuleToolResult(VbaModuleState module)
        {
            return ToolResult.Ok(
                "VBA module read: " + module.Name,
                JsonConvert.SerializeObject(new
                {
                    name = module.Name,
                    type = module.ComponentType,
                    codeOnlyUserForm = module.CodeOnlyUserForm,
                    lineCount = module.LineCount,
                    code = module.Code,
                    codeSha256 = module.CodeSha256,
                    truncated = module.Truncated
                }));
        }

        private static string SnapshotJson(VbaModuleSnapshot snapshot)
        {
            return snapshot == null ? null : JsonConvert.SerializeObject(snapshot);
        }

        private static bool IsModuleNotFound(VbaBackendException error)
        {
            return error != null &&
                (string.Equals(
                    error.ErrorCode,
                    "vba_module_not_found",
                    StringComparison.OrdinalIgnoreCase) ||
                 (error.Message ?? string.Empty).IndexOf(
                    "VBA module not found",
                    StringComparison.OrdinalIgnoreCase) >= 0);
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
