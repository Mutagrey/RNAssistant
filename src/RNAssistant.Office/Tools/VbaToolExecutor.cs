using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    internal sealed class VbaToolExecutor
    {
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly VbaBackupStore _vbaBackupStore;

        public VbaToolExecutor(IOfficeApplicationAdapter adapter, VbaBackupStore vbaBackupStore)
        {
            _adapter = adapter;
            _vbaBackupStore = vbaBackupStore;
        }

        public IEnumerable<ToolDefinition> GetControllerTools()
        {
            if (!HostSupportsVba())
            {
                yield break;
            }

            yield return ControllerToolDefinition.Create(ToolId("vba_list_backups"), _adapter.HostName, "Read-only: List RNAssistant VBA rollback backups for the current document.", "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}");
            yield return ControllerToolDefinition.Create(ToolId("vba_list_modules"), _adapter.HostName, "Read-only: List all VBA components with name, type, and line count. Read only the needed component with vba_read_module.", "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}");
            yield return ControllerToolDefinition.Create(ToolId("vba_search_code"), _adapter.HostName, "Read-only: Search literal or regex patterns across VBA component code.", "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"description\":\"Non-empty literal or regular-expression search query.\",\"minLength\":1},\"moduleName\":{\"type\":\"string\",\"description\":\"Exact VBA component name.\"},\"mode\":{\"type\":\"string\",\"description\":\"Text matching mode: literal or regex.\",\"default\":\"literal\",\"enum\":[\"literal\",\"regex\"]},\"matchCase\":{\"type\":\"boolean\",\"description\":\"Whether matching is case-sensitive.\",\"default\":false},\"wholeWord\":{\"type\":\"boolean\",\"description\":\"Whether only whole-word matches are accepted.\",\"default\":false},\"maxResults\":{\"type\":\"integer\",\"description\":\"Maximum number of matches returned.\",\"default\":100},\"contextChars\":{\"type\":\"integer\",\"description\":\"Maximum context characters returned around each match.\",\"default\":80}},\"required\":[\"query\"],\"additionalProperties\":false}");
            yield return ControllerToolDefinition.Create(ToolId("vba_restore_backup"), _adapter.HostName, "Mutates document: Restore a VBA module from an exact backupId, or restore the latest backup for moduleName when backupId is omitted.", "{\"type\":\"object\",\"properties\":{\"backupId\":{\"type\":\"string\",\"description\":\"Exact rollback backup identifier from vba_list_backups.\"},\"moduleName\":{\"type\":\"string\",\"description\":\"Exact VBA component name; used only to select its latest backup when backupId is omitted.\"}},\"required\":[],\"additionalProperties\":false}", mutatesDocument: true, agentCanRun: true, requiresConfirmation: true, riskLevel: 3);
            yield return ControllerToolDefinition.Create(ToolId("vba_replace_text"), _adapter.HostName, "Mutates document: Replace one exact text fragment, or all exact occurrences when replaceAll=true. Requires the current module hash and creates a rollback backup.", ReplaceTextSchema(), mutatesDocument: true, agentCanRun: true, requiresConfirmation: true, riskLevel: 3);
            yield return ControllerToolDefinition.Create(ToolId("vba_apply_patch"), _adapter.HostName, "Mutates document: Apply ordered structured literal, regex, insertion, or line patches. Requires the current module hash and creates a rollback backup.", ApplyPatchSchema(), mutatesDocument: true, agentCanRun: true, requiresConfirmation: true, riskLevel: 3);
            yield return ControllerToolDefinition.Create(ToolId("vba_create_module"), _adapter.HostName, "Mutates document: Create a new StdModule or ClassModule and return its code hash. Document modules and UserForms cannot be created.", "{\"type\":\"object\",\"properties\":{\"moduleName\":{\"type\":\"string\",\"description\":\"Exact new VBA component name.\"},\"componentType\":{\"type\":\"string\",\"description\":\"VBA component type.\",\"default\":\"StdModule\",\"enum\":[\"StdModule\",\"ClassModule\"]},\"code\":{\"type\":\"string\",\"description\":\"Complete VBA source code, normally beginning with Option Explicit.\",\"minLength\":1}},\"required\":[\"moduleName\",\"code\"],\"additionalProperties\":false}", mutatesDocument: true, agentCanRun: true, requiresConfirmation: true, riskLevel: 3);
            yield return ControllerToolDefinition.Create(ToolId("vba_delete_module"), _adapter.HostName, "Mutates document: Delete a StdModule or ClassModule after current-hash validation and backup. Document modules and UserForms cannot be deleted.", "{\"type\":\"object\",\"properties\":{\"moduleName\":{\"type\":\"string\",\"description\":\"Exact VBA component name.\"},\"expectedCodeSha256\":{\"type\":\"string\",\"description\":\"Exact current codeSha256 returned by vba_read_module or vba_search_code.\"}},\"required\":[\"moduleName\",\"expectedCodeSha256\"],\"additionalProperties\":false}", mutatesDocument: true, agentCanRun: true, requiresConfirmation: true, riskLevel: 3);
        }

        public string ToolId(string suffix)
        {
            return HostToolPrefix() + "." + suffix;
        }

        internal bool IsInternalToolId(string id)
        {
            return !string.IsNullOrWhiteSpace(id) &&
                (string.Equals(id, ToolId("vba_install_package_internal"), StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(id, ToolId("vba_remove_package_internal"), StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(id, ToolId("vba_list_project_components_internal"), StringComparison.OrdinalIgnoreCase));
        }

        public ToolResult ExecuteControllerTool(ToolCommand command, bool dryRun, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(command.ToolId, ToolId("vba_list_backups"), StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Ok("VBA backups listed.", JsonConvert.SerializeObject(_vbaBackupStore.List(_adapter.HostName, _adapter.DocumentKey)));
            }

            if (string.Equals(command.ToolId, ToolId("vba_list_modules"), StringComparison.OrdinalIgnoreCase)) return ListModules();
            if (string.Equals(command.ToolId, ToolId("vba_search_code"), StringComparison.OrdinalIgnoreCase)) return SearchCode(command);

            if (string.Equals(command.ToolId, ToolId("vba_restore_backup"), StringComparison.OrdinalIgnoreCase))
            {
                return RestoreVbaBackup(command, dryRun, cancellationToken);
            }

            if (string.Equals(command.ToolId, ToolId("vba_replace_text"), StringComparison.OrdinalIgnoreCase))
            {
                return ReplaceVbaText(command, dryRun, cancellationToken);
            }

            if (string.Equals(command.ToolId, ToolId("vba_apply_patch"), StringComparison.OrdinalIgnoreCase))
            {
                return ApplyVbaPatch(command, dryRun, cancellationToken);
            }

            if (string.Equals(command.ToolId, ToolId("vba_create_module"), StringComparison.OrdinalIgnoreCase)) return CreateModule(command, dryRun);
            if (string.Equals(command.ToolId, ToolId("vba_delete_module"), StringComparison.OrdinalIgnoreCase)) return DeleteModule(command, dryRun);

            return ToolResult.Fail("Unknown VBA controller tool: " + command.ToolId);
        }

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
            return _adapter.ExecuteTool(install);
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
            return _adapter.ExecuteTool(remove);
        }

        public string GetInstallationStatus(ToolDefinition tool)
        {
            ToolDefinition package;
            ToolResult error;
            return TryPreparePackage(tool, out package, out error) ? ProbeInstallation(package).Status : "invalid";
        }

        public ToolResult PrepareBackupBeforeReplace(ToolCommand command)
        {
            var moduleName = ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty);
            if (string.IsNullOrWhiteSpace(moduleName))
            {
                return ToolResult.Fail("moduleName is required.", null, "vba_module_name_required", true);
            }

            VbaModuleState existing;
            ToolResult readError;
            if (!TryReadVbaModule(moduleName, 1000000, out existing, out readError))
            {
                if (ToolArgumentReader.Boolean(command.Arguments, "createIfMissing", true) && IsModuleNotFound(readError))
                {
                    return null;
                }

                return ToolResult.Fail(
                    "VBA replacement was blocked because a rollback backup could not be created. " +
                    (readError == null ? string.Empty : readError.Message),
                    readError == null ? null : readError.DataJson,
                    "vba_backup_failed",
                    false);
            }

            ToolResult backupError;
            if (!TrySaveBackup(moduleName, existing, "replacement", out backupError))
            {
                return backupError;
            }
            return null;
        }

        private ToolResult ListModules()
        {
            var read = new ToolCommand { ToolId = ToolId("vba_list_project_components_internal") };
            var result = _adapter.ExecuteTool(read);
            if (result == null || !result.Success) return result ?? ToolResult.Fail("VBA project returned no result.");
            try
            {
                var data = JObject.Parse(result.DataJson ?? "{}");
                var modules = new JArray();
                foreach (var module in (data["modules"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    modules.Add(new JObject
                    {
                        ["name"] = module["name"], ["type"] = module["type"], ["lineCount"] = module["lineCount"]
                    });
                }
                return ToolResult.Ok("VBA modules listed: " + modules.Count + ".", JsonConvert.SerializeObject(new { modules = modules }));
            }
            catch (JsonException ex) { return ToolResult.Fail("Could not parse VBA project: " + ex.Message, null, "vba_read_invalid", true); }
        }

        private ToolResult SearchCode(ToolCommand command)
        {
            var query = ToolArgumentReader.String(command.Arguments, "query", string.Empty);
            if (string.IsNullOrWhiteSpace(query)) return ToolResult.Fail("query is required.");
            var moduleFilter = ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty);
            var maxResults = Math.Max(1, Math.Min(500, ToolArgumentReader.Int32(command.Arguments, "maxResults", 100)));
            var contextChars = Math.Max(0, Math.Min(1000, ToolArgumentReader.Int32(command.Arguments, "contextChars", 80)));
            var read = new ToolCommand { ToolId = ToolId("vba_list_project_components_internal") };
            var project = _adapter.ExecuteTool(read);
            if (project == null || !project.Success) return project ?? ToolResult.Fail("VBA project returned no result.");
            try
            {
                var rows = new List<object>();
                var total = 0;
                foreach (var module in (JObject.Parse(project.DataJson ?? "{}")["modules"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    var name = (string)module["name"] ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(moduleFilter) && !string.Equals(name, moduleFilter, StringComparison.OrdinalIgnoreCase)) continue;
                    VbaModuleState moduleState;
                    ToolResult readError;
                    if (!TryReadVbaModule(name, 1000000, out moduleState, out readError))
                    {
                        return readError ?? ToolResult.Fail("VBA module could not be read: " + name, null, "vba_read_invalid", true);
                    }
                    var code = moduleState.Code;
                    var found = TextPatternEngine.Find(code, query, new TextPatternOptions { Mode = ToolArgumentReader.String(command.Arguments, "mode", "literal"), MatchCase = ToolArgumentReader.Boolean(command.Arguments, "matchCase", false), WholeWord = ToolArgumentReader.Boolean(command.Arguments, "wholeWord", false) }, Math.Max(1, maxResults - rows.Count), contextChars);
                    total += found.MatchCount;
                    foreach (var match in found.Matches)
                    {
                        if (rows.Count >= maxResults) break;
                        var line = 1;
                        for (var i = 0; i < match.Index && i < code.Length; i++) if (code[i] == '\n') line++;
                        rows.Add(new { moduleName = name, componentType = moduleState.ComponentType, line = line, start = match.Index, end = match.Index + match.Length, preview = match.Preview, codeSha256 = CodeSha256(code) });
                    }
                }
                return ToolResult.Ok("VBA code matches: " + total + ".", JsonConvert.SerializeObject(new { matchCount = total, returnedCount = rows.Count, truncated = total > rows.Count, matches = rows }));
            }
            catch (TextPatternException ex) { return ToolResult.Fail(ex.Message, null, ex.ErrorCode, false); }
            catch (JsonException ex) { return ToolResult.Fail("Could not parse VBA project: " + ex.Message, null, "vba_read_invalid", true); }
        }

        private ToolResult CreateModule(ToolCommand command, bool dryRun)
        {
            var moduleName = ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty);
            var componentType = ToolArgumentReader.String(command.Arguments, "componentType", "StdModule");
            var code = ToolArgumentReader.String(command.Arguments, "code", string.Empty);
            if (dryRun) return ToolResult.Ok("Dry run: would create VBA " + componentType + " " + moduleName + ".");
            var create = new ToolCommand { ToolId = ToolId("vba_create_module_internal") };
            create.Arguments["moduleName"] = moduleName; create.Arguments["componentType"] = componentType; create.Arguments["code"] = code;
            var created = _adapter.ExecuteTool(create);
            if (created == null || !created.Success) return created ?? ToolResult.Fail("VBA create returned no result.");
            VbaModuleState module;
            ToolResult readError;
            return TryReadVbaModule(moduleName, 1000000, out module, out readError)
                ? ToolResult.Ok("VBA module created: " + moduleName, JsonConvert.SerializeObject(new
                {
                    moduleName = moduleName,
                    componentType = module.ComponentType,
                    codeSha256 = CodeSha256(module.Code)
                }))
                : ToolResult.PartialFailure("VBA module was created but could not be read back: " + (readError == null ? moduleName : readError.Message), created.DataJson, "vba_create_verify_failed");
        }

        private ToolResult DeleteModule(ToolCommand command, bool dryRun)
        {
            var moduleName = ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty);
            var expectedHash = ToolArgumentReader.String(command.Arguments, "expectedCodeSha256", string.Empty);
            VbaModuleState module;
            ToolResult error;
            if (!TryReadVbaModule(moduleName, 1000000, out module, out error)) return error;
            if (!string.Equals(module.ComponentType, "StdModule", StringComparison.OrdinalIgnoreCase) && !string.Equals(module.ComponentType, "ClassModule", StringComparison.OrdinalIgnoreCase))
                return ToolResult.Fail("Document modules and UserForms are read/search/patch only and cannot be deleted.", null, "vba_component_type_read_only", false);
            var currentHash = CodeSha256(module.Code);
            if (string.IsNullOrWhiteSpace(expectedHash) || !string.Equals(expectedHash, currentHash, StringComparison.OrdinalIgnoreCase))
                return ToolResult.Fail("VBA module hash is missing or stale. Call vba_read_module or vba_search_code first.", JsonConvert.SerializeObject(new { moduleName = moduleName, actualCodeSha256 = currentHash }), "stale_vba_module", true);
            if (dryRun) return ToolResult.Ok("Dry run: would delete VBA module " + moduleName + ".");
            ToolResult backupError;
            if (!TrySaveBackup(moduleName, module, "delete", out backupError)) return backupError;
            var delete = new ToolCommand { ToolId = ToolId("vba_delete_module_internal") };
            delete.Arguments["moduleName"] = moduleName;
            return _adapter.ExecuteTool(delete);
        }

        private ToolResult RestoreVbaBackup(ToolCommand command, bool dryRun, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var backupId = ToolArgumentReader.String(command.Arguments, "backupId", string.Empty);
            var moduleName = ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty);
            var backup = _vbaBackupStore.Find(_adapter.HostName, _adapter.DocumentKey, backupId, moduleName);
            if (backup == null)
            {
                return ToolResult.Fail("VBA backup not found.");
            }

            if (dryRun)
            {
                return ToolResult.Ok("Dry run: would restore VBA backup " + backup.BackupId, JsonConvert.SerializeObject(backup));
            }

            VbaModuleState current;
            ToolResult readError;
            if (TryReadVbaModule(backup.ModuleName, 1000000, out current, out readError))
            {
                ToolResult backupError;
                if (!TrySaveBackup(backup.ModuleName, current, "restore", out backupError))
                {
                    return backupError;
                }
            }
            else if (!IsModuleNotFound(readError))
            {
                return ToolResult.Fail(
                    "VBA restore was blocked because the current module could not be read. " +
                    (readError == null ? string.Empty : readError.Message),
                    readError == null ? null : readError.DataJson,
                    "vba_backup_failed",
                    false);
            }

            var result = WriteModule(backup.ModuleName, backup.Code, true);
            if (!result.Success)
            {
                return result;
            }

            return ToolResult.Ok("VBA backup restored: " + backup.BackupId, JsonConvert.SerializeObject(new { backupId = backup.BackupId, moduleName = backup.ModuleName, restore = result }));
        }

        private ToolResult ReplaceVbaText(ToolCommand command, bool dryRun, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var moduleName = ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty);
            var find = ToolArgumentReader.String(command.Arguments, "find", string.Empty);
            var replace = ToolArgumentReader.String(command.Arguments, "replace", string.Empty);
            if (string.IsNullOrWhiteSpace(moduleName) || string.IsNullOrEmpty(find))
            {
                return ToolResult.Fail("moduleName and find are required.");
            }

            VbaModuleState module;
            ToolResult error;
            if (!TryReadVbaModule(moduleName, 1000000, out module, out error))
            {
                return error;
            }

            var code = module.Code;
            var expectedHash = ToolArgumentReader.String(command.Arguments, "expectedCodeSha256", string.Empty);
            var currentHash = CodeSha256(code);
            if (string.IsNullOrWhiteSpace(expectedHash) || !string.Equals(expectedHash, currentHash, StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Fail("VBA module changed after it was read. Read or search it again before editing.", JsonConvert.SerializeObject(new { moduleName = moduleName, actualCodeSha256 = currentHash }), "stale_vba_module", true);
            }
            var replacements = CountOccurrences(code, find);
            if (replacements == 0)
            {
                return ToolResult.Fail("Text fragment was not found in VBA module: " + moduleName);
            }

            var replaceAll = ToolArgumentReader.Boolean(command.Arguments, "replaceAll", false);
            if (replacements > 1 && !replaceAll)
            {
                return ToolResult.Fail("The exact text occurs " + replacements + " times. Narrow find or set replaceAll=true explicitly.", JsonConvert.SerializeObject(new { moduleName = moduleName, matchCount = replacements, codeSha256 = currentHash }), "vba_patch_ambiguous", true);
            }
            var updated = replaceAll ? code.Replace(find, replace ?? string.Empty) : ReplaceFirst(code, find, replace ?? string.Empty);
            var preview = JsonConvert.SerializeObject(new
            {
                moduleName = moduleName,
                replacements = replacements,
                oldLength = code.Length,
                newLength = updated.Length,
                previousCodeSha256 = currentHash,
                codeSha256 = CodeSha256(updated)
            });
            if (dryRun)
            {
                return ToolResult.Ok("Dry run: would patch VBA module " + moduleName + " (" + replacements + " replacement(s)).", preview);
            }

            ToolResult backupError;
            if (!TrySaveBackup(moduleName, module, "patch", out backupError))
            {
                return backupError;
            }

            var result = WriteModule(moduleName, updated, false);
            if (!result.Success)
            {
                return result;
            }

            return ToolResult.Ok("VBA text replaced in " + moduleName + ": " + replacements + " replacement(s).", preview);
        }

        private ToolResult ApplyVbaPatch(ToolCommand command, bool dryRun, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var moduleName = ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty);
            if (string.IsNullOrWhiteSpace(moduleName))
            {
                return ToolResult.Fail("moduleName is required.");
            }

            JArray operations;
            try
            {
                operations = ParsePatchOperations(ToolArgumentReader.String(command.Arguments, "patch", string.Empty));
            }
            catch (JsonException ex)
            {
                return ToolResult.Fail("Invalid patch JSON: " + ex.Message, null, "vba_patch_invalid", true);
            }

            if (operations.Count == 0)
            {
                return ToolResult.Fail("Patch has no operations.");
            }

            VbaModuleState module;
            ToolResult error;
            if (!TryReadVbaModule(moduleName, 1000000, out module, out error))
            {
                return error;
            }

            var code = module.Code;
            var expectedHash = ToolArgumentReader.String(command.Arguments, "expectedCodeSha256", string.Empty);
            var currentHash = CodeSha256(code);
            if (string.IsNullOrWhiteSpace(expectedHash) || !string.Equals(expectedHash, currentHash, StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Fail("VBA module changed after it was read. Read or search it again before editing.", JsonConvert.SerializeObject(new { moduleName = moduleName, actualCodeSha256 = currentHash }), "stale_vba_module", true);
            }
            var updated = code;
            var summary = new List<object>();
            foreach (JObject operation in operations.OfType<JObject>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = ApplyPatchOperation(updated, operation, out updated);
                if (!result.Success)
                {
                    return result;
                }

                summary.Add(new { op = (string)(operation["op"] ?? operation["type"]), message = result.Message });
            }
            if (summary.Count != operations.Count)
            {
                return ToolResult.Fail("Each patch operation must be a JSON object.");
            }

            var preview = JsonConvert.SerializeObject(new
            {
                moduleName = moduleName,
                operations = summary,
                oldLength = code.Length,
                newLength = updated.Length,
                previousCodeSha256 = currentHash,
                codeSha256 = CodeSha256(updated)
            });
            if (dryRun)
            {
                return ToolResult.Ok("Dry run: would apply VBA patch to " + moduleName + ".", preview);
            }

            ToolResult backupError;
            if (!TrySaveBackup(moduleName, module, "patch", out backupError))
            {
                return backupError;
            }

            var writeResult = WriteModule(moduleName, updated, false);
            if (!writeResult.Success)
            {
                return writeResult;
            }

            return ToolResult.Ok("VBA patch applied to " + moduleName + ".", preview);
        }

        private bool TryReadVbaModule(string moduleName, int maxChars, out VbaModuleState module, out ToolResult error)
        {
            module = null;
            error = null;
            var read = new ToolCommand { ToolId = ToolId("vba_read_module") };
            read.Arguments["moduleName"] = moduleName;
            read.Arguments["maxChars"] = maxChars;
            var current = _adapter.ExecuteTool(read);
            if (!current.Success || string.IsNullOrWhiteSpace(current.DataJson))
            {
                error = current.Success ? ToolResult.Fail("VBA module returned no code.") : current;
                return false;
            }

            try
            {
                var data = JObject.Parse(current.DataJson);
                if (data["code"] == null || data["code"].Type == JTokenType.Null)
                {
                    error = ToolResult.Fail("VBA module data has no code field.", current.DataJson, "vba_read_invalid", true);
                    return false;
                }
                module = new VbaModuleState
                {
                    Code = (string)data["code"] ?? string.Empty,
                    ComponentType = (string)data["type"] ?? string.Empty
                };
            }
            catch (JsonException ex)
            {
                error = ToolResult.Fail("Could not parse VBA module data: " + ex.Message);
                return false;
            }

            if (module.Code.EndsWith("\n...[truncated]", StringComparison.Ordinal))
            {
                error = ToolResult.Fail("VBA module is too large for a safe patch.");
                module = null;
                return false;
            }

            return true;
        }

        private ToolResult WriteModule(string moduleName, string code, bool createIfMissing)
        {
            var write = new ToolCommand { ToolId = ToolId("vba_replace_module") };
            write.Arguments["moduleName"] = moduleName;
            write.Arguments["code"] = code;
            write.Arguments["createIfMissing"] = createIfMissing;
            return _adapter.ExecuteTool(write);
        }

        private void SaveBackup(string moduleName, VbaModuleState module)
        {
            _vbaBackupStore.Save(
                _adapter.HostName,
                _adapter.DocumentKey,
                _adapter.DocumentTitle,
                moduleName,
                module == null ? string.Empty : module.ComponentType,
                module == null ? string.Empty : module.Code);
        }

        private bool TrySaveBackup(string moduleName, VbaModuleState module, string operation, out ToolResult error)
        {
            try
            {
                SaveBackup(moduleName, module);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ToolResult.Fail(
                    "VBA " + operation + " was blocked because the rollback backup could not be saved. " + ex.Message,
                    null,
                    "vba_backup_failed",
                    false);
                return false;
            }
        }

        internal static string CodeSha256(string code)
        {
            return VbaToolManifestParser.CodeSha256(code);
        }

        private static string NormalizeCode(string code)
        {
            return VbaToolManifestParser.NormalizeCode(code);
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
                var equals = string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
                if (equals) matching++;
                details.Add(new { name = component.Name, status = equals ? "matching" : "modified", expected = expected, actual = actual });
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

        private static bool IsModuleNotFound(ToolResult result)
        {
            return result != null &&
                (string.Equals(result.ErrorCode, "vba_module_not_found", StringComparison.OrdinalIgnoreCase) ||
                 (result.Message ?? string.Empty).IndexOf("VBA module not found", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static JArray ParsePatchOperations(string patchJson)
        {
            if (string.IsNullOrWhiteSpace(patchJson))
            {
                return new JArray();
            }

            var token = JToken.Parse(patchJson);
            if (token.Type == JTokenType.Array)
            {
                return (JArray)token;
            }

            return new JArray(token);
        }

        private static ToolResult ApplyPatchOperation(string current, JObject operation, out string updated)
        {
            updated = current;
            var op = ((string)(operation["op"] ?? operation["type"]) ?? string.Empty).Trim();
            var find = (string)(operation["find"] ?? operation["anchor"]);
            var text = (string)(operation["text"] ?? operation["replace"] ?? operation["code"]) ?? string.Empty;
            switch (op.ToLowerInvariant())
            {
                case "replace":
                case "replaceall":
                    if (string.IsNullOrEmpty(find))
                    {
                        return ToolResult.Fail("Patch replace requires find.");
                    }

                    var count = CountOccurrences(current, find);
                    if (count == 0)
                    {
                        return ToolResult.Fail("Patch find text was not found.");
                    }

                    updated = current.Replace(find, text);
                    return ToolResult.Ok("Replaced " + count + " occurrence(s).");
                case "replacefirst":
                    return ReplaceAtMatch(current, find, text, out updated);
                case "insertbefore":
                    return ReplaceAtMatch(current, find, text + find, out updated);
                case "insertafter":
                    return ReplaceAtMatch(current, find, find + text, out updated);
                case "replacelines":
                    return ReplaceLines(current, operation, text, out updated);
                case "regexreplace":
                    var pattern = (string)(operation["pattern"] ?? operation["find"]);
                    if (string.IsNullOrEmpty(pattern)) return ToolResult.Fail("regexReplace requires pattern.", null, "vba_patch_invalid", true);
                    try
                    {
                        var planned = TextPatternEngine.Replace(
                            current,
                            pattern,
                            text,
                            new TextPatternOptions { Mode = "regex", MatchCase = (bool?)(operation["matchCase"]) ?? true, WholeWord = (bool?)(operation["wholeWord"]) ?? false },
                            (bool?)(operation["replaceAll"]) ?? true,
                            Math.Max(1, Math.Min(10000, (int?)(operation["maxReplacements"]) ?? 500)));
                        if (planned.MatchCount == 0) return ToolResult.Fail("Patch regex was not found.");
                        updated = planned.Text;
                        return ToolResult.Ok("Regex replaced " + planned.MatchCount + " occurrence(s).");
                    }
                    catch (TextPatternException ex) { return ToolResult.Fail(ex.Message, null, ex.ErrorCode, false); }
                default:
                    return ToolResult.Fail("Unsupported patch op: " + op);
            }
        }

        private static ToolResult ReplaceAtMatch(string current, string find, string replacement, out string updated)
        {
            updated = current;
            if (string.IsNullOrEmpty(find))
            {
                return ToolResult.Fail("Patch operation requires find.");
            }

            var index = current.IndexOf(find, StringComparison.Ordinal);
            if (index < 0)
            {
                return ToolResult.Fail("Patch find text was not found.");
            }

            updated = current.Substring(0, index) + replacement + current.Substring(index + find.Length);
            return ToolResult.Ok("Patched first occurrence.");
        }

        private static string ReplaceFirst(string current, string find, string replacement)
        {
            var index = current.IndexOf(find, StringComparison.Ordinal);
            return index < 0
                ? current
                : current.Substring(0, index) + replacement + current.Substring(index + find.Length);
        }

        private static ToolResult ReplaceLines(string current, JObject operation, string text, out string updated)
        {
            updated = current;
            int startLine;
            int deleteCount;
            if (!int.TryParse(Convert.ToString(operation["startLine"]), out startLine) ||
                !int.TryParse(Convert.ToString(operation["deleteCount"] ?? 0), out deleteCount) ||
                startLine <= 0 || deleteCount < 0)
            {
                return ToolResult.Fail("replaceLines requires startLine >= 1 and deleteCount >= 0.");
            }

            var newline = current.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            var lines = current.Replace("\r\n", "\n").Split('\n').ToList();
            var index = startLine - 1;
            if (index > lines.Count)
            {
                return ToolResult.Fail("replaceLines startLine is outside the module.");
            }

            if (deleteCount > lines.Count - index)
            {
                return ToolResult.Fail("replaceLines deleteCount extends past the end of the module.");
            }
            if (deleteCount > 0)
            {
                lines.RemoveRange(index, deleteCount);
            }

            if (!string.IsNullOrEmpty(text))
            {
                lines.InsertRange(index, text.Replace("\r\n", "\n").Split('\n'));
            }

            updated = string.Join(newline, lines.ToArray());
            return ToolResult.Ok("Replaced lines at " + startLine + " deleting " + deleteCount + ".");
        }

        private static string ToolModuleName(string toolId)
        {
            return "RNAssistant_" + Regex.Replace(toolId ?? "Tool", "[^A-Za-z0-9_]", "_");
        }

        private string HostToolPrefix()
        {
            return (_adapter.HostName ?? string.Empty).ToLowerInvariant();
        }

        private bool HostSupportsVba()
        {
            return string.Equals(_adapter.HostName, "Excel", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_adapter.HostName, "Word", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_adapter.HostName, "PowerPoint", StringComparison.OrdinalIgnoreCase);
        }

        private static string ReplaceTextSchema()
        {
            return "{\"type\":\"object\",\"properties\":{" +
                "\"moduleName\":{\"type\":\"string\",\"description\":\"Exact VBA component name.\"}," +
                "\"expectedCodeSha256\":{\"type\":\"string\",\"description\":\"Exact current codeSha256 returned by vba_read_module or vba_search_code.\"}," +
                "\"find\":{\"type\":\"string\",\"description\":\"Exact non-empty code fragment to replace.\",\"minLength\":1}," +
                "\"replace\":{\"type\":\"string\",\"description\":\"Replacement code fragment.\"}," +
                "\"replaceAll\":{\"type\":\"boolean\",\"description\":\"Whether every exact occurrence may be replaced; false rejects an ambiguous multi-match edit.\",\"default\":false}" +
                "},\"required\":[\"moduleName\",\"expectedCodeSha256\",\"find\"],\"additionalProperties\":false}";
        }

        private static string ApplyPatchSchema()
        {
            return "{\"type\":\"object\",\"properties\":{" +
                "\"moduleName\":{\"type\":\"string\",\"description\":\"Exact VBA component name.\"}," +
                "\"expectedCodeSha256\":{\"type\":\"string\",\"description\":\"Exact current codeSha256 returned by vba_read_module or vba_search_code.\"}," +
                "\"patch\":{\"type\":\"array\",\"description\":\"Ordered patch operations applied to one current module snapshot.\",\"minItems\":1,\"maxItems\":100,\"items\":{" +
                    "\"type\":\"object\",\"properties\":{" +
                        "\"op\":{\"type\":\"string\",\"description\":\"Operation: replace/replaceAll replace every exact match; replaceFirst changes the first; insertBefore/insertAfter use find as anchor; replaceLines uses startLine/deleteCount; regexReplace uses pattern.\",\"enum\":[\"replace\",\"replaceAll\",\"replaceFirst\",\"insertBefore\",\"insertAfter\",\"replaceLines\",\"regexReplace\"]}," +
                        "\"find\":{\"type\":\"string\",\"description\":\"Exact text or insertion anchor for literal operations.\"}," +
                        "\"pattern\":{\"type\":\"string\",\"description\":\"Regular expression for regexReplace.\"}," +
                        "\"text\":{\"type\":\"string\",\"description\":\"Replacement or inserted VBA code; regex capture groups such as $1 are supported.\"}," +
                        "\"startLine\":{\"type\":\"integer\",\"description\":\"One-based first line for replaceLines.\",\"minimum\":1}," +
                        "\"deleteCount\":{\"type\":\"integer\",\"description\":\"Number of existing lines removed by replaceLines.\",\"minimum\":0}," +
                        "\"matchCase\":{\"type\":\"boolean\",\"description\":\"Whether regexReplace is case-sensitive.\",\"default\":true}," +
                        "\"wholeWord\":{\"type\":\"boolean\",\"description\":\"Whether regexReplace accepts only whole-word matches.\",\"default\":false}," +
                        "\"replaceAll\":{\"type\":\"boolean\",\"description\":\"Whether regexReplace replaces every match.\",\"default\":true}," +
                        "\"maxReplacements\":{\"type\":\"integer\",\"description\":\"Maximum regex replacements allowed.\",\"default\":500,\"minimum\":1,\"maximum\":10000}" +
                    "},\"required\":[\"op\"],\"additionalProperties\":false}" +
                "}},\"required\":[\"moduleName\",\"expectedCodeSha256\",\"patch\"],\"additionalProperties\":false}";
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

        private static int CountOccurrences(string value, string find)
        {
            var count = 0;
            var index = 0;
            while ((index = value.IndexOf(find, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += find.Length;
            }

            return count;
        }

        private sealed class VbaModuleState
        {
            public string Code { get; set; }
            public string ComponentType { get; set; }
        }

        private sealed class InstallationProbe
        {
            public string Status { get; set; }
            public string DataJson { get; set; }
        }
    }
}
