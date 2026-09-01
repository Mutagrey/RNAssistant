using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Domains.Vba;

namespace RNAssistant.Harness
{
    internal sealed partial class FakeOfficeAdapter
    {
        public VbaProjectSnapshot ListProjectComponents()
        {
            var result = ExecuteVba("vba_list_project_components_internal");
            EnsureVbaSuccess(result);
            try
            {
                var data = JObject.Parse(result.DataJson ?? "{}");
                var modules = data["modules"] as JArray;
                if (modules == null)
                    throw new FormatException(
                        "VBA project snapshot has no modules array.");
                return new VbaProjectSnapshot
                {
                    Title = (string)data["title"],
                    Modules = modules.Select(token =>
                    {
                        var item = token as JObject;
                        if (item == null) return null;
                        return new VbaProjectComponentSnapshot
                        {
                            Name = (string)item["name"],
                            ComponentType = (string)item["type"],
                            LineCount = (int?)item["lineCount"] ?? 0,
                            CodeOnlyUserForm =
                                (bool?)item["codeOnlyUserForm"],
                            HasToolManifest =
                                (bool?)item["hasToolManifest"]
                        };
                    }).ToArray()
                };
            }
            catch (Exception ex) when (ex is JsonException ||
                ex is FormatException || ex is InvalidCastException)
            {
                throw InvalidVbaSnapshot(ex);
            }
        }

        public VbaModuleSnapshot ReadModule(VbaReadModuleRequest request)
        {
            var command = VbaCommand("vba_read_module");
            command.Arguments["moduleName"] = request == null
                ? string.Empty : request.ModuleName;
            command.Arguments["maxChars"] = request == null
                ? 30000 : request.MaxChars;
            var result = ExecuteTool(command);
            EnsureVbaSuccess(result);
            try
            {
                var data = JObject.Parse(result.DataJson ?? "{}");
                return new VbaModuleSnapshot
                {
                    Name = (string)data["name"],
                    ComponentType = (string)data["type"],
                    CodeOnlyUserForm = (bool?)data["codeOnlyUserForm"],
                    LineCount = (int?)data["lineCount"] ?? 0,
                    Code = data["code"] == null
                        ? null : (string)data["code"],
                    CodeSha256 = (string)data["codeSha256"],
                    Truncated = (bool?)data["truncated"] == true
                };
            }
            catch (Exception ex) when (ex is JsonException ||
                ex is FormatException || ex is InvalidCastException)
            {
                throw InvalidVbaSnapshot(ex);
            }
        }

        public VbaBackendActionResult ReplaceModule(
            VbaReplaceModuleRequest request)
        {
            var command = VbaCommand("vba_replace_module");
            command.Arguments["moduleName"] = request.ModuleName;
            command.Arguments["code"] = request.Code;
            command.Arguments["createIfMissing"] = request.CreateIfMissing;
            if (!string.IsNullOrWhiteSpace(request.ExpectedCodeSha256))
                command.Arguments["expectedCodeSha256"] =
                    request.ExpectedCodeSha256;
            return ToVbaAction(ExecuteTool(command));
        }

        public VbaBackendActionResult CreateModule(VbaCreateModuleRequest request)
        {
            var command = VbaCommand("vba_create_module_internal");
            command.Arguments["moduleName"] = request.ModuleName;
            command.Arguments["componentType"] = request.ComponentType;
            command.Arguments["code"] = request.Code;
            return ToVbaAction(ExecuteTool(command));
        }

        public VbaBackendActionResult RenameModule(VbaRenameModuleRequest request)
        {
            var command = VbaCommand("vba_rename_module_internal");
            command.Arguments["moduleName"] = request.ModuleName;
            command.Arguments["newModuleName"] = request.NewModuleName;
            command.Arguments["expectedCodeSha256"] =
                request.ExpectedCodeSha256;
            command.Arguments["expectedComponentType"] =
                request.ExpectedComponentType;
            return ToVbaAction(ExecuteTool(command));
        }

        public VbaBackendActionResult DeleteModule(VbaDeleteModuleRequest request)
        {
            var command = VbaCommand("vba_delete_module_internal");
            command.Arguments["moduleName"] = request.ModuleName;
            if (!string.IsNullOrWhiteSpace(request.ExpectedCodeSha256))
                command.Arguments["expectedCodeSha256"] =
                    request.ExpectedCodeSha256;
            return ToVbaAction(ExecuteTool(command));
        }

        public VbaBackendActionResult InstallPackage(
            VbaInstallPackageRequest request)
        {
            var command = VbaCommand("vba_install_package_internal");
            command.Arguments["componentsJson"] = JsonConvert.SerializeObject(
                (request.Components ?? new VbaInstallPackageComponent[0])
                    .Select(component => new
                    {
                        name = component.Name,
                        type = component.ComponentType,
                        code = component.Code,
                        expectedBeforeExists = component.ExpectedBeforeExists,
                        expectedBeforeType =
                            component.ExpectedBeforeComponentType,
                        expectedBeforeComparableCodeSha256 =
                            component.ExpectedBeforeComparableCodeSha256,
                        expectedBeforeOwnershipMarkerPresent =
                            component.ExpectedBeforeOwnershipMarkerPresent,
                        expectedBeforeOwnershipMarker =
                            component.ExpectedBeforeOwnershipMarker
                    }).ToArray());
            command.Arguments["marker"] = request.Marker;
            return ToVbaAction(ExecuteTool(command));
        }

        public VbaBackendActionResult RemovePackage(
            VbaRemovePackageRequest request)
        {
            var command = VbaCommand("vba_remove_package_internal");
            command.Arguments["expectedComponentsJson"] =
                JsonConvert.SerializeObject(
                    request.ExpectedComparableHashes ??
                    new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase));
            command.Arguments["expectedMarker"] = request.ExpectedMarker;
            return ToVbaAction(ExecuteTool(command));
        }

        public VbaBackendActionResult RunMacro(VbaRunMacroRequest request)
        {
            var command = VbaCommand("run_macro");
            command.Arguments["macroName"] = request.MacroName;
            command.Arguments["argumentsJson"] = JsonConvert.SerializeObject(
                request.Arguments ?? new object[0]);
            return ToVbaAction(ExecuteTool(command));
        }

        private ToolResult ExecuteVba(string suffix)
        {
            return ExecuteTool(VbaCommand(suffix));
        }

        private ToolCommand VbaCommand(string suffix)
        {
            return new ToolCommand
            {
                ToolId = (_hostName ?? string.Empty).ToLowerInvariant() +
                    "." + suffix
            };
        }

        private static void EnsureVbaSuccess(ToolResult result)
        {
            if (result != null && result.Success) return;
            throw new VbaBackendException(
                result == null ? "VBA backend returned no result." : result.Message,
                result == null || string.IsNullOrWhiteSpace(result.ErrorCode)
                    ? "vba_read_missing_result" : result.ErrorCode,
                result == null || result.Retryable == true,
                ParseData(result == null ? null : result.DataJson));
        }

        private static VbaBackendActionResult ToVbaAction(ToolResult result)
        {
            if (result == null)
                return VbaBackendActionResult.Unknown(
                    "VBA backend returned no result.",
                    null,
                    "vba_backend_missing_result");
            var data = ParseData(result.DataJson);
            if (result.Success)
                return VbaBackendActionResult.Ok(result.Message, data);
            if (string.Equals(
                result.Status, "partial_failure",
                StringComparison.OrdinalIgnoreCase))
                return VbaBackendActionResult.Unknown(
                    result.Message, data, result.ErrorCode);
            return VbaBackendActionResult.Error(
                result.Message,
                data,
                result.ErrorCode,
                result.Retryable == true);
        }

        private static JObject ParseData(string dataJson)
        {
            if (string.IsNullOrWhiteSpace(dataJson)) return null;
            try { return JObject.Parse(dataJson); }
            catch (JsonException)
            {
                return new JObject { ["operationData"] = dataJson };
            }
        }

        private static VbaBackendException InvalidVbaSnapshot(Exception error)
        {
            return new VbaBackendException(
                "Invalid scripted VBA snapshot: " + error.Message,
                "vba_read_invalid",
                true,
                null,
                error);
        }
    }
}
