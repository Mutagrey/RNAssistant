using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class VbaToolExecutor
    {
        public ToolResult ExecuteCustomTool(ToolDefinition tool, ToolCommand command, AppSettings settings, bool dryRun, bool manualRun)
        {
            ToolDefinition package;
            ToolResult validationError;
            if (!TryPreparePackage(tool, out package, out validationError)) return validationError;

            JObject argumentObject;
            try { argumentObject = JObject.FromObject(command == null ? new Dictionary<string, object>() : command.Arguments ?? new Dictionary<string, object>()); }
            catch (JsonException ex) { return ToolResult.Fail("VBA tool arguments are invalid: " + ex.Message, null, "vba_arguments_invalid", true); }
            JObject schema;
            string schemaError;
            ToolSchemaSupport.TryParse(package, out schema, out schemaError);
            string argumentError;
            if (!ToolSchemaSupport.ValidateArguments(argumentObject, schema, true, out argumentError)) return ToolResult.Fail(argumentError, null, "vba_arguments_invalid", true);
            var positional = new JArray((package.ArgumentOrder ?? new List<string>()).Select(name => argumentObject[name] == null ? JValue.CreateNull() : argumentObject[name].DeepClone()));

            var probe = ProbeInstallation(package);
            if (probe.Status == "unavailable")
            {
                return ToolResult.Fail("VBA package state could not be read. Execution was blocked.", probe.DataJson, "vba_package_probe_failed", true);
            }
            if (probe.Status == "modified_local" || probe.Status == "partial")
            {
                return ToolResult.Fail("VBA package components collide with modified or partial document code. Review and explicitly reinstall the package.", probe.DataJson, "vba_package_drift", false);
            }
            if (dryRun)
            {
                return ToolResult.Ok("Dry run: VBA tool is valid and would run " + package.EntryPoint + ".", JsonConvert.SerializeObject(new
                {
                    toolId = package.Id,
                    entryPoint = package.EntryPoint,
                    arguments = positional,
                    installationStatus = probe.Status,
                    sessionInstall = probe.Status == "not_installed"
                }));
            }

            var sessionInstalled = false;
            ToolResult installResult = null;
            if (probe.Status == "not_installed")
            {
                installResult = InstallCustomTool(package, true, false);
                if (!installResult.Success) return installResult;
                sessionInstalled = true;
            }

            var entryModule = EntryComponent(package);
            var run = new ToolCommand { ToolId = ToolId("run_macro") };
            run.Arguments["macroName"] = entryModule.Name + "." + package.EntryPoint;
            run.Arguments["argumentsJson"] = positional.ToString(Formatting.None);
            ToolResult runResult;
            ToolResult cleanupResult = null;
            try
            {
                runResult = _adapter.ExecuteTool(run) ?? ToolResult.Fail("VBA function returned no result.", null, "vba_function_failed", true);
            }
            catch (Exception ex)
            {
                runResult = ToolResult.Fail("VBA function failed: " + ex.Message, null, "vba_function_failed", true);
            }
            finally
            {
                if (sessionInstalled) cleanupResult = RemoveCustomTool(package, true);
            }

            var output = ExtractMacroOutput(runResult);
            var dataJson = JsonConvert.SerializeObject(new
            {
                protocolVersion = 1,
                output = output,
                sessionInstalled = sessionInstalled,
                install = installResult,
                cleanup = cleanupResult
            });
            if (cleanupResult != null && !cleanupResult.Success)
            {
                return ToolResult.PartialFailure("VBA tool finished, but temporary components could not be removed: " + cleanupResult.Message, dataJson, "vba_session_cleanup_failed");
            }
            if (!runResult.Success)
            {
                return ToolResult.Fail("VBA function failed: " + runResult.Message, dataJson, runResult.ErrorCode ?? "vba_function_failed", runResult.Retryable);
            }
            return ToolResult.Ok(output, dataJson);
        }

        public ToolResult InstallCustomTool(ToolDefinition tool, bool sessionOnly, bool dryRun)
        {
            ToolDefinition package;
            ToolResult validationError;
            if (!TryPreparePackage(tool, out package, out validationError)) return validationError;
            if (dryRun) return ToolResult.Ok("Dry run: would install VBA package " + package.Id, PackageData(package));
            if (!sessionOnly && !SupportsPersistentVbaDocument())
            {
                return ToolResult.Fail(
                    "Persistent VBA installation requires a macro-enabled document (.xlsm/.xlam, .docm/.dotm, or .pptm/.ppam). Use normal tool execution for temporary session injection.",
                    null,
                    "vba_macro_enabled_document_required",
                    false);
            }

            if (!sessionOnly)
            {
                foreach (var component in package.Components)
                {
                    VbaModuleState existing;
                    ToolResult readError;
                    if (TryReadVbaModule(component.Name, 1000000, out existing, out readError))
                    {
                        ToolResult backupError;
                        if (!TrySaveBackup(component.Name, existing, "package installation", out backupError)) return backupError;
                    }
                    else if (!IsModuleNotFound(readError))
                    {
                        return ToolResult.Fail("VBA package installation was blocked because component state could not be read: " + component.Name, readError == null ? null : readError.DataJson, "vba_package_probe_failed", false);
                    }
                }
            }

            var install = new ToolCommand { ToolId = ToolId("vba_install_package_internal") };
            install.Arguments["componentsJson"] = JsonConvert.SerializeObject(package.Components.Select(component => new { name = component.Name, type = component.Type, code = component.Code }).ToArray());
            install.Arguments["marker"] = (sessionOnly ? "RNAssistantSession: " : "RNAssistantPackage: ") +
                "id=" + package.Id + "; version=" + package.PackageVersion + "; hash=" + PackageHash(package);
            var installed = _adapter.ExecuteTool(install);
            if (installed == null || !installed.Success)
            {
                return installed ?? ToolResult.Fail("VBA package installation returned no result.", null, "vba_package_install_failed", false);
            }
            var verification = ProbeInstallation(package);
            if (!string.Equals(verification.Status, "installed", StringComparison.OrdinalIgnoreCase))
            {
                ToolResult cleanup = null;
                if (sessionOnly)
                {
                    try { cleanup = RemoveCustomTool(package, true); }
                    catch (Exception ex)
                    {
                        cleanup = ToolResult.Fail(
                            "Temporary VBA package cleanup failed after verification error: " + ex.Message,
                            null,
                            "vba_session_cleanup_failed",
                            false);
                    }
                }
                return ToolResult.PartialFailure(
                    "VBA package installation completed but read-back verification failed: " + verification.Status + ".",
                    JsonConvert.SerializeObject(new
                    {
                        verificationStatus = verification.Status,
                        verification = ParseJsonOrString(verification.DataJson),
                        cleanup = cleanup
                    }),
                    "vba_package_verify_failed");
            }
            return ToolResult.Ok(installed.Message, installed.DataJson ?? PackageData(package));
        }

        public ToolResult RemoveCustomTool(ToolDefinition tool, bool sessionOnly = false)
        {
            ToolDefinition package;
            ToolResult validationError;
            if (!TryPreparePackage(tool, out package, out validationError)) return validationError;
            var expected = new JObject();
            foreach (var component in package.Components) expected[component.Name] = VbaToolManifestParser.CodeSha256(component.Code);
            var remove = new ToolCommand { ToolId = ToolId("vba_remove_package_internal") };
            remove.Arguments["expectedComponentsJson"] = expected.ToString(Formatting.None);
            remove.Arguments["expectedMarker"] = (sessionOnly ? "RNAssistantSession: " : "RNAssistantPackage: ") + "id=" + package.Id + ";";
            var removed = _adapter.ExecuteTool(remove);
            if (removed == null || !removed.Success)
            {
                return removed ?? ToolResult.Fail("VBA package removal returned no result.", null, "vba_package_remove_failed", false);
            }
            var verification = ProbeInstallation(package);
            if (!string.Equals(verification.Status, "not_installed", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.PartialFailure(
                    "VBA package removal completed but components are still present: " + verification.Status + ".",
                    verification.DataJson,
                    "vba_package_remove_verify_failed");
            }
            return ToolResult.Ok(removed.Message, removed.DataJson);
        }

        public string GetInstallationStatus(ToolDefinition tool)
        {
            ToolDefinition package;
            ToolResult error;
            return TryPreparePackage(tool, out package, out error) ? ProbeInstallation(package).Status : "invalid";
        }

        private static JToken ParseJsonOrString(string dataJson)
        {
            if (string.IsNullOrWhiteSpace(dataJson)) return JValue.CreateNull();
            try { return JToken.Parse(dataJson); }
            catch (JsonException) { return new JValue(dataJson); }
        }

        private bool TryPreparePackage(ToolDefinition source, out ToolDefinition package, out ToolResult error)
        {
            package = null;
            error = null;
            if (source == null || string.IsNullOrWhiteSpace(source.Code))
            {
                error = ToolResult.Fail("VBA tool has no entry module code.", null, "vba_code_missing", false);
                return false;
            }
            var parsed = new VbaToolManifestParser().Parse(source.Code);
            if (!parsed.Success)
            {
                error = ToolResult.Fail(parsed.ErrorMessage, null, parsed.ErrorCode, false);
                return false;
            }
            package = parsed.Tool;
            if (!string.Equals(package.Id, source.Id, StringComparison.OrdinalIgnoreCase) || !string.Equals(package.Host, _adapter.HostName, StringComparison.OrdinalIgnoreCase))
            {
                error = ToolResult.Fail("VBA manifest id/host does not match the selected tool and active Office host.", null, "vba_manifest_metadata_mismatch", false);
                return false;
            }
            var supplied = new Dictionary<string, VbaToolComponent>(StringComparer.OrdinalIgnoreCase);
            foreach (var component in source.Components ?? new List<VbaToolComponent>())
            {
                if (component == null || string.IsNullOrWhiteSpace(component.Name)) continue;
                if (supplied.ContainsKey(component.Name))
                {
                    error = ToolResult.Fail("VBA package contains a duplicate component: " + component.Name, null, "vba_component_duplicate", false);
                    return false;
                }
                supplied.Add(component.Name, component);
            }
            var entryName = package.Components[0].Name;
            var resolved = new List<VbaToolComponent>();
            foreach (var declared in package.Components)
            {
                VbaToolComponent component;
                supplied.TryGetValue(declared.Name, out component);
                var code = string.Equals(declared.Name, entryName, StringComparison.OrdinalIgnoreCase)
                    ? source.Code
                    : component == null ? string.Empty : component.Code;
                var type = string.Equals(declared.Name, entryName, StringComparison.OrdinalIgnoreCase)
                    ? "StdModule"
                    : component == null ? string.Empty : component.Type;
                if (string.IsNullOrWhiteSpace(code) ||
                    (!string.Equals(type, "StdModule", StringComparison.OrdinalIgnoreCase) && !string.Equals(type, "ClassModule", StringComparison.OrdinalIgnoreCase)))
                {
                    error = ToolResult.Fail("VBA package source/type is missing for component: " + declared.Name, null, "vba_component_missing", false);
                    package = null;
                    return false;
                }
                resolved.Add(new VbaToolComponent
                {
                    Name = declared.Name,
                    Type = type,
                    FileName = component == null ? declared.FileName : component.FileName,
                    Code = code,
                    CodeSha256 = VbaToolManifestParser.CodeSha256(code)
                });
            }
            package.Components = resolved;
            var unexpected = supplied.Keys.FirstOrDefault(name => !resolved.Any(component => string.Equals(component.Name, name, StringComparison.OrdinalIgnoreCase)));
            if (!string.IsNullOrWhiteSpace(unexpected))
            {
                error = ToolResult.Fail("VBA package contains an undeclared component: " + unexpected, null, "vba_component_undeclared", false);
                package = null;
                return false;
            }
            package.StoragePath = source.StoragePath;
            package.Readme = source.Readme;
            return true;
        }

        private InstallationProbe ProbeInstallation(ToolDefinition package)
        {
            var present = 0;
            var matching = 0;
            var details = new List<object>();
            foreach (var component in package.Components)
            {
                VbaModuleState current;
                ToolResult readError;
                if (!TryReadVbaModule(component.Name, 1000000, out current, out readError))
                {
                    if (!IsModuleNotFound(readError))
                    {
                        return new InstallationProbe { Status = "unavailable", DataJson = readError == null ? null : readError.DataJson };
                    }
                    details.Add(new { name = component.Name, status = "missing" });
                    continue;
                }
                present++;
                var expected = VbaToolManifestParser.CodeSha256(component.Code);
                var actual = VbaToolManifestParser.CodeSha256(current.Code);
                var equals = string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(component.Type, current.ComponentType, StringComparison.OrdinalIgnoreCase);
                if (equals) matching++;
                details.Add(new
                {
                    name = component.Name,
                    status = equals ? "matching" : "modified",
                    expected = expected,
                    actual = actual,
                    expectedType = component.Type,
                    actualType = current.ComponentType
                });
            }
            var status = present == 0 ? "not_installed" : present == package.Components.Count && matching == present ? "installed" : matching == present ? "partial" : "modified_local";
            return new InstallationProbe { Status = status, DataJson = JsonConvert.SerializeObject(details) };
        }

        private static VbaToolComponent EntryComponent(ToolDefinition package)
        {
            return package.Components.First(component => string.Equals(component.Type, "StdModule", StringComparison.OrdinalIgnoreCase) &&
                (component.Code ?? string.Empty).IndexOf("<RNAssistantTool>", StringComparison.Ordinal) >= 0);
        }

        private static string ExtractMacroOutput(ToolResult result)
        {
            if (result == null || string.IsNullOrWhiteSpace(result.DataJson)) return result == null ? string.Empty : result.Message ?? string.Empty;
            try
            {
                var data = JObject.Parse(result.DataJson);
                return data["output"] == null || data["output"].Type == JTokenType.Null ? string.Empty : Convert.ToString(((JValue)data["output"]).Value);
            }
            catch (JsonException) { return result.Message ?? string.Empty; }
        }

        private static string PackageHash(ToolDefinition package)
        {
            return CodeSha256(string.Join("\n", package.Components.OrderBy(component => component.Name).Select(component => component.Name + ":" + VbaToolManifestParser.CodeSha256(component.Code)).ToArray()));
        }

        private static string PackageData(ToolDefinition package)
        {
            return JsonConvert.SerializeObject(new
            {
                id = package.Id,
                version = package.PackageVersion,
                entryPoint = package.EntryPoint,
                components = package.Components.Select(component => new { name = component.Name, type = component.Type, codeSha256 = component.CodeSha256 }).ToArray()
            });
        }

        private bool SupportsPersistentVbaDocument()
        {
            var extension = System.IO.Path.GetExtension(_adapter.DocumentTitle ?? string.Empty);
            if (string.Equals(_adapter.HostName, "Excel", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(extension, ".xlsm", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".xlam", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".xlsb", StringComparison.OrdinalIgnoreCase);
            }
            if (string.Equals(_adapter.HostName, "Word", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(extension, ".docm", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".dotm", StringComparison.OrdinalIgnoreCase);
            }
            if (string.Equals(_adapter.HostName, "PowerPoint", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(extension, ".pptm", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".ppam", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".potm", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".ppsm", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private sealed class InstallationProbe
        {
            public string Status { get; set; }
            public string DataJson { get; set; }
        }
    }
}
