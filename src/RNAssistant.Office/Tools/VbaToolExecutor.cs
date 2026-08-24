using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class VbaToolExecutor
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
            yield return ControllerToolDefinition.Create(ToolId("vba_list_modules"), _adapter.HostName, "Read-only: List all VBA components with name, type, and line count. Read small components with vba_read_module or exact ranges with vba_read_lines.", "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}");
            yield return ControllerToolDefinition.Create(ToolId("vba_search_code"), _adapter.HostName, "Read-only: Search literal or regex patterns across VBA component code.", "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"description\":\"Non-empty literal or regular-expression search query.\",\"minLength\":1,\"maxLength\":2048},\"moduleName\":{\"type\":\"string\",\"description\":\"Exact VBA component name.\"},\"mode\":{\"type\":\"string\",\"description\":\"Text matching mode: literal or regex.\",\"default\":\"literal\",\"enum\":[\"literal\",\"regex\"]},\"matchCase\":{\"type\":\"boolean\",\"description\":\"Whether matching is case-sensitive.\",\"default\":false},\"wholeWord\":{\"type\":\"boolean\",\"description\":\"Whether only whole-word matches are accepted.\",\"default\":false},\"maxResults\":{\"type\":\"integer\",\"description\":\"Maximum number of matches returned.\",\"default\":100,\"minimum\":1,\"maximum\":500},\"contextChars\":{\"type\":\"integer\",\"description\":\"Maximum context characters returned around each match.\",\"default\":80,\"minimum\":0,\"maximum\":1000}},\"required\":[\"query\"],\"additionalProperties\":false}");
            yield return ControllerToolDefinition.Create(ToolId("vba_restore_backup"), _adapter.HostName, "Mutates document: Restore a VBA module from an exact backupId, or restore the latest backup for moduleName when backupId is omitted.", "{\"type\":\"object\",\"properties\":{\"backupId\":{\"type\":\"string\",\"description\":\"Exact rollback backup identifier from vba_list_backups.\"},\"moduleName\":{\"type\":\"string\",\"description\":\"Exact VBA component name; used only to select its latest backup when backupId is omitted.\"}},\"required\":[],\"additionalProperties\":false}", mutatesDocument: true, agentCanRun: true, requiresConfirmation: true, riskLevel: 3);
            yield return ControllerToolDefinition.Create(ToolId("vba_replace_text"), _adapter.HostName, "Mutates document: Replace one exact text fragment, or all exact occurrences when replaceAll=true. Requires the current module hash and creates a rollback backup.", ReplaceTextSchema(), mutatesDocument: true, agentCanRun: true, requiresConfirmation: true, riskLevel: 3);
            yield return ControllerToolDefinition.Create(ToolId("vba_apply_patch"), _adapter.HostName, "Mutates document: Apply ordered structured literal, regex, insertion, or line patches. Combine all known edits for one module and current hash into this single patch array; never emit several patch calls for the same snapshot. patch must be a native JSON array, not a JSON string. Use text (or replace) for replacement content; never misuse insertBefore/insertAfter as replacement. Requires the current module hash and creates a rollback backup.", ApplyPatchSchema(), mutatesDocument: true, agentCanRun: true, requiresConfirmation: true, riskLevel: 3);
            yield return ControllerToolDefinition.Create(ToolId("vba_create_module"), _adapter.HostName, "Mutates document: Create a new StdModule, ClassModule, or blank MSForm/UserForm and return its code hash. For MSForm, code is code-behind only; visual controls, layout, and FRX assets are not edited.", "{\"type\":\"object\",\"properties\":{\"moduleName\":{\"type\":\"string\",\"description\":\"Exact new VBA component name.\"},\"componentType\":{\"type\":\"string\",\"description\":\"VBA component type. MSForm creates a blank UserForm with editable code-behind.\",\"default\":\"StdModule\",\"enum\":[\"StdModule\",\"ClassModule\",\"MSForm\"]},\"code\":{\"type\":\"string\",\"description\":\"Complete VBA source code or MSForm code-behind, normally beginning with Option Explicit.\",\"minLength\":1}},\"required\":[\"moduleName\",\"code\"],\"additionalProperties\":false}", mutatesDocument: true, agentCanRun: true, requiresConfirmation: true, riskLevel: 3);
            yield return ControllerToolDefinition.Create(ToolId("vba_delete_module"), _adapter.HostName, "Mutates document: Delete a StdModule or ClassModule after current-hash validation and backup. Document modules and UserForms cannot be deleted.", "{\"type\":\"object\",\"properties\":{\"moduleName\":{\"type\":\"string\",\"description\":\"Exact VBA component name.\"},\"expectedCodeSha256\":{\"type\":\"string\",\"description\":\"Exact current codeSha256 returned by vba_read_module, vba_read_lines, or vba_search_code.\"}},\"required\":[\"moduleName\",\"expectedCodeSha256\"],\"additionalProperties\":false}", mutatesDocument: true, agentCanRun: true, requiresConfirmation: true, riskLevel: 3);
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
                var observedMatches = 0;
                var truncated = false;
                var matchedModule = string.IsNullOrWhiteSpace(moduleFilter);
                var modules = (JObject.Parse(project.DataJson ?? "{}")["modules"] as JArray ?? new JArray()).OfType<JObject>().ToList();
                foreach (var module in modules)
                {
                    var name = (string)module["name"] ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(moduleFilter) && !string.Equals(name, moduleFilter, StringComparison.OrdinalIgnoreCase)) continue;
                    matchedModule = true;
                    VbaModuleState moduleState;
                    ToolResult readError;
                    if (!TryReadVbaModule(name, 1000000, out moduleState, out readError))
                    {
                        return readError ?? ToolResult.Fail("VBA module could not be read: " + name, null, "vba_read_invalid", true);
                    }
                    var code = moduleState.Code;
                    var moduleHash = CodeSha256(code);
                    var found = TextPatternEngine.Find(code, query, new TextPatternOptions { Mode = ToolArgumentReader.String(command.Arguments, "mode", "literal"), MatchCase = ToolArgumentReader.Boolean(command.Arguments, "matchCase", false), WholeWord = ToolArgumentReader.Boolean(command.Arguments, "wholeWord", false) }, Math.Max(1, maxResults - rows.Count), contextChars);
                    observedMatches += found.MatchCount;
                    var scannedIndex = 0;
                    var currentLine = 1;
                    foreach (var match in found.Matches)
                    {
                        if (rows.Count >= maxResults) break;
                        while (scannedIndex < match.Index && scannedIndex < code.Length)
                        {
                            if (code[scannedIndex] == '\n' ||
                                (code[scannedIndex] == '\r' && (scannedIndex + 1 >= code.Length || code[scannedIndex + 1] != '\n'))) currentLine++;
                            scannedIndex++;
                        }
                        rows.Add(new { moduleName = name, componentType = moduleState.ComponentType, line = currentLine, start = match.Index, end = match.Index + match.Length, preview = match.Preview, codeSha256 = moduleHash });
                    }
                }
                if (!matchedModule) return ToolResult.Fail("VBA module not found: " + moduleFilter, null, "vba_module_not_found", true);
                truncated = observedMatches > rows.Count;
                return ToolResult.Ok(
                    "VBA code matches returned: " + rows.Count + (truncated ? " (results truncated)." : "."),
                    JsonConvert.SerializeObject(new { matchCount = observedMatches, matchCountIsExact = true, returnedCount = rows.Count, truncated = truncated, matches = rows }));
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
            return VerifyModuleWrite(
                moduleName,
                code,
                "VBA module created: " + moduleName,
                JsonConvert.SerializeObject(new
                {
                    moduleName = moduleName,
                    componentType = componentType,
                    codeSha256 = CodeSha256(code)
                }),
                "vba_create",
                componentType);
        }

        private ToolResult DeleteModule(ToolCommand command, bool dryRun)
        {
            var moduleName = ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty);
            var expectedHash = ToolArgumentReader.String(command.Arguments, "expectedCodeSha256", string.Empty);
            VbaModuleState module;
            ToolResult error;
            if (!TryReadVbaModule(moduleName, 1000000, out module, out error)) return error;
            if (!string.Equals(module.ComponentType, "StdModule", StringComparison.OrdinalIgnoreCase) && !string.Equals(module.ComponentType, "ClassModule", StringComparison.OrdinalIgnoreCase))
                return ToolResult.Fail("Document modules and UserForms cannot be deleted through RNAssistant.", null, "vba_component_type_read_only", false);
            var currentHash = CodeSha256(module.Code);
            if (string.IsNullOrWhiteSpace(expectedHash) || !string.Equals(expectedHash, currentHash, StringComparison.OrdinalIgnoreCase))
                return ToolResult.Fail("VBA module hash is missing or stale. Call vba_read_module, vba_read_lines, or vba_search_code first.", JsonConvert.SerializeObject(new { moduleName = moduleName, actualCodeSha256 = currentHash }), "stale_vba_module", true);
            if (dryRun) return ToolResult.Ok("Dry run: would delete VBA module " + moduleName + ".");
            ToolResult backupError;
            if (!TrySaveBackup(moduleName, module, "delete", out backupError)) return backupError;
            var delete = new ToolCommand { ToolId = ToolId("vba_delete_module_internal") };
            delete.Arguments["moduleName"] = moduleName;
            var deleted = _adapter.ExecuteTool(delete);
            if (deleted == null || !deleted.Success)
            {
                return deleted ?? ToolResult.Fail("VBA delete returned no result.", null, "vba_delete_failed", false);
            }
            VbaModuleState remaining;
            ToolResult verifyError;
            if (TryReadVbaModule(moduleName, 1000000, out remaining, out verifyError))
            {
                return ToolResult.PartialFailure(
                    "VBA delete returned success but the module is still present: " + moduleName + ".",
                    VerificationData(moduleName, null, CodeSha256(remaining.Code), deleted.DataJson),
                    "vba_delete_verify_failed");
            }
            if (!IsModuleNotFound(verifyError))
            {
                return ToolResult.PartialFailure(
                    "VBA module deletion completed but could not be verified: " + (verifyError == null ? moduleName : verifyError.Message),
                    VerificationData(moduleName, null, null, deleted.DataJson),
                    "vba_delete_verify_failed");
            }
            return ToolResult.Ok("VBA module deleted: " + moduleName, deleted.DataJson ?? JsonConvert.SerializeObject(new { moduleName = moduleName }));
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
            var moduleExists = false;
            if (TryReadVbaModule(backup.ModuleName, 1000000, out current, out readError))
            {
                moduleExists = true;
                if (!string.IsNullOrWhiteSpace(backup.ComponentType) &&
                    !string.Equals(backup.ComponentType, current.ComponentType, StringComparison.OrdinalIgnoreCase))
                {
                    return ToolResult.Fail(
                        "VBA restore was blocked because the current component type differs from the backup.",
                        JsonConvert.SerializeObject(new { moduleName = backup.ModuleName, backupType = backup.ComponentType, currentType = current.ComponentType }),
                        "vba_restore_component_type_mismatch",
                        false);
                }
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

            ToolResult result;
            var componentType = string.IsNullOrWhiteSpace(backup.ComponentType)
                ? (moduleExists ? current.ComponentType : "StdModule")
                : backup.ComponentType;
            if (moduleExists)
            {
                result = WriteModule(backup.ModuleName, backup.Code, false);
            }
            else
            {
                var create = new ToolCommand { ToolId = ToolId("vba_create_module_internal") };
                create.Arguments["moduleName"] = backup.ModuleName;
                create.Arguments["componentType"] = componentType;
                create.Arguments["code"] = backup.Code ?? string.Empty;
                result = _adapter.ExecuteTool(create);
            }
            if (result == null || !result.Success)
            {
                return result ?? ToolResult.Fail("VBA restore write returned no result.", null, "vba_restore_failed", false);
            }

            return VerifyModuleWrite(
                backup.ModuleName,
                backup.Code,
                "VBA backup restored: " + backup.BackupId,
                JsonConvert.SerializeObject(new
                {
                    backupId = backup.BackupId,
                    moduleName = backup.ModuleName,
                    codeSha256 = CodeSha256(backup.Code),
                    restore = result
                }),
                "vba_restore",
                componentType);
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
            find = MatchLineEndings(find, code);
            replace = MatchLineEndings(replace, code);
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
            if (result == null || !result.Success)
            {
                return result ?? ToolResult.Fail("VBA replacement write returned no result.", null, "vba_replace_failed", false);
            }

            return VerifyModuleWrite(
                moduleName,
                updated,
                "VBA text replaced in " + moduleName + ": " + replacements + " replacement(s).",
                preview,
                "vba_replace");
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
            if (writeResult == null || !writeResult.Success)
            {
                return writeResult ?? ToolResult.Fail("VBA patch write returned no result.", null, "vba_patch_failed", false);
            }

            return VerifyModuleWrite(
                moduleName,
                updated,
                "VBA patch applied to " + moduleName + ".",
                preview,
                "vba_patch");
        }

        private bool TryReadVbaModule(string moduleName, int maxChars, out VbaModuleState module, out ToolResult error)
        {
            module = null;
            error = null;
            var read = new ToolCommand { ToolId = ToolId("vba_read_module") };
            read.Arguments["moduleName"] = moduleName;
            read.Arguments["maxChars"] = maxChars;
            var current = _adapter.ExecuteTool(read);
            if (current == null || !current.Success || string.IsNullOrWhiteSpace(current.DataJson))
            {
                error = current == null
                    ? ToolResult.Fail("VBA module read returned no result.", null, "vba_read_missing_result", true)
                    : current.Success ? ToolResult.Fail("VBA module returned no code.") : current;
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
                    ComponentType = (string)data["type"] ?? string.Empty,
                    Truncated = (bool?)data["truncated"] ?? false,
                    LineCount = (int?)data["lineCount"] ?? VbaToolManifestParser.LiveCodeLineCount((string)data["code"] ?? string.Empty)
                };
            }
            catch (JsonException ex)
            {
                error = ToolResult.Fail("Could not parse VBA module data: " + ex.Message);
                return false;
            }

            if (module.Truncated || module.Code.EndsWith("\n...[truncated]", StringComparison.Ordinal))
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

        private ToolResult VerifyModuleWrite(
            string moduleName,
            string expectedCode,
            string successMessage,
            string successDataJson,
            string errorPrefix,
            string expectedComponentType = null)
        {
            var expectedHash = CodeSha256(expectedCode);
            var expectedComparableHash = VbaToolManifestParser.VbeComparableCodeSha256(expectedCode);
            var expectedLineCount = VbaToolManifestParser.LiveCodeLineCount(expectedCode);
            VbaModuleState actual;
            ToolResult readError;
            if (!TryReadVbaModule(moduleName, 1000000, out actual, out readError))
            {
                return ToolResult.PartialFailure(
                    "VBA write completed but final state could not be read back: " +
                    (readError == null ? moduleName : readError.Message),
                    VerificationData(moduleName, expectedHash, null, successDataJson, expectedComponentType, null, expectedLineCount, null),
                    (errorPrefix ?? "vba_write") + "_verify_failed");
            }

            var actualHash = CodeSha256(actual.Code);
            var actualComparableHash = VbaToolManifestParser.VbeComparableCodeSha256(actual.Code);
            var codeMatches = string.Equals(expectedComparableHash, actualComparableHash, StringComparison.OrdinalIgnoreCase);
            var componentTypeMatches = string.IsNullOrWhiteSpace(expectedComponentType) ||
                string.Equals(expectedComponentType, actual.ComponentType, StringComparison.OrdinalIgnoreCase);
            if (!codeMatches || !componentTypeMatches)
            {
                return ToolResult.PartialFailure(
                    "VBA write verification failed for " + moduleName +
                    ": final code or component type differs from the requested state.",
                    VerificationData(moduleName, expectedHash, actualHash, successDataJson, expectedComponentType, actual.ComponentType, expectedLineCount, actual.LineCount),
                    (errorPrefix ?? "vba_write") + "_verify_mismatch");
            }

            return ToolResult.Ok(successMessage, SuccessfulVerificationData(
                moduleName,
                expectedHash,
                actualHash,
                successDataJson,
                actual.ComponentType,
                actual.LineCount));
        }

        private static string SuccessfulVerificationData(
            string moduleName,
            string requestedHash,
            string actualHash,
            string operationDataJson,
            string actualComponentType,
            int actualLineCount)
        {
            JObject data;
            try { data = string.IsNullOrWhiteSpace(operationDataJson) ? new JObject() : JObject.Parse(operationDataJson); }
            catch (JsonException) { data = new JObject { ["operationData"] = operationDataJson ?? string.Empty }; }
            data["moduleName"] = moduleName ?? string.Empty;
            data["codeSha256"] = actualHash;
            data["lineCount"] = actualLineCount;
            data["componentType"] = actualComponentType ?? string.Empty;
            data["vbeNormalized"] = !string.Equals(requestedHash, actualHash, StringComparison.OrdinalIgnoreCase);
            if (!string.Equals(requestedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                data["requestedCodeSha256"] = requestedHash;
            }
            return data.ToString(Formatting.None);
        }

        private static string VerificationData(
            string moduleName,
            string expectedHash,
            string actualHash,
            string operationDataJson,
            string expectedComponentType = null,
            string actualComponentType = null,
            int? expectedLineCount = null,
            int? actualLineCount = null)
        {
            JToken operationData = null;
            if (!string.IsNullOrWhiteSpace(operationDataJson))
            {
                try { operationData = JToken.Parse(operationDataJson); }
                catch (JsonException) { operationData = new JValue(operationDataJson); }
            }
            return new JObject
            {
                ["moduleName"] = moduleName ?? string.Empty,
                ["expectedCodeSha256"] = string.IsNullOrWhiteSpace(expectedHash) ? null : expectedHash,
                ["actualCodeSha256"] = string.IsNullOrWhiteSpace(actualHash) ? null : actualHash,
                ["expectedComponentType"] = string.IsNullOrWhiteSpace(expectedComponentType) ? null : expectedComponentType,
                ["actualComponentType"] = string.IsNullOrWhiteSpace(actualComponentType) ? null : actualComponentType,
                ["expectedLineCount"] = expectedLineCount,
                ["actualLineCount"] = actualLineCount,
                ["operationData"] = operationData
            }.ToString(Formatting.None);
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
            return VbaToolManifestParser.LiveCodeSha256(code);
        }

        private static bool IsModuleNotFound(ToolResult result)
        {
            return result != null &&
                (string.Equals(result.ErrorCode, "vba_module_not_found", StringComparison.OrdinalIgnoreCase) ||
                 (result.Message ?? string.Empty).IndexOf("VBA module not found", StringComparison.OrdinalIgnoreCase) >= 0);
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
                "\"expectedCodeSha256\":{\"type\":\"string\",\"description\":\"Exact current codeSha256 returned by vba_read_module, vba_read_lines, or vba_search_code.\"}," +
                "\"find\":{\"type\":\"string\",\"description\":\"Exact non-empty code fragment to replace.\",\"minLength\":1}," +
                "\"replace\":{\"type\":\"string\",\"description\":\"Replacement code fragment.\"}," +
                "\"replaceAll\":{\"type\":\"boolean\",\"description\":\"Whether every exact occurrence may be replaced; false rejects an ambiguous multi-match edit.\",\"default\":false}" +
                "},\"required\":[\"moduleName\",\"expectedCodeSha256\",\"find\"],\"additionalProperties\":false}";
        }

        private static string ApplyPatchSchema()
        {
            return "{\"type\":\"object\",\"properties\":{" +
                "\"moduleName\":{\"type\":\"string\",\"description\":\"Exact VBA component name.\"}," +
                "\"expectedCodeSha256\":{\"type\":\"string\",\"description\":\"Exact current codeSha256 returned by vba_read_module, vba_read_lines, or vba_search_code.\"}," +
                "\"patch\":{\"type\":\"array\",\"description\":\"Native JSON array of ordered patch operations applied to one current module snapshot; never encode this array as a string.\",\"minItems\":1,\"maxItems\":100,\"items\":{" +
                    "\"type\":\"object\",\"properties\":{" +
                        "\"op\":{\"type\":\"string\",\"description\":\"Operation: replace requires one exact match; replaceAll is explicit; replaceFirst changes the first; insertBefore/insertAfter insert a line-safe block around one unique anchor; replaceLines uses current sequential line coordinates; regexReplace uses pattern.\",\"enum\":[\"replace\",\"replaceAll\",\"replaceFirst\",\"insertBefore\",\"insertAfter\",\"replaceLines\",\"regexReplace\"]}," +
                        "\"find\":{\"type\":\"string\",\"description\":\"Non-empty exact text or unique insertion anchor for literal operations.\",\"minLength\":1}," +
                        "\"pattern\":{\"type\":\"string\",\"description\":\"Non-empty regular expression for regexReplace.\",\"minLength\":1}," +
                        "\"text\":{\"type\":\"string\",\"description\":\"Preferred field for replacement or inserted VBA code; regex capture groups such as $1 are supported.\"}," +
                        "\"replace\":{\"type\":\"string\",\"description\":\"Alias for text, accepted as replacement content. Do not supply both text and replace.\"}," +
                        "\"startLine\":{\"type\":\"integer\",\"description\":\"One-based first line for replaceLines, evaluated after all preceding patch operations.\",\"minimum\":1}," +
                        "\"deleteCount\":{\"type\":\"integer\",\"description\":\"Number of existing lines removed by replaceLines.\",\"minimum\":0}," +
                        "\"matchCase\":{\"type\":\"boolean\",\"description\":\"Whether regexReplace is case-sensitive.\",\"default\":true}," +
                        "\"wholeWord\":{\"type\":\"boolean\",\"description\":\"Whether regexReplace accepts only whole-word matches.\",\"default\":false}," +
                        "\"replaceAll\":{\"type\":\"boolean\",\"description\":\"Whether regexReplace replaces every match.\",\"default\":true}," +
                        "\"maxReplacements\":{\"type\":\"integer\",\"description\":\"Maximum regex replacements allowed.\",\"default\":500,\"minimum\":1,\"maximum\":10000}" +
                    "},\"required\":[\"op\"],\"additionalProperties\":false}" +
                "}},\"required\":[\"moduleName\",\"expectedCodeSha256\",\"patch\"],\"additionalProperties\":false}";
        }

        private sealed class VbaModuleState
        {
            public string Code { get; set; }
            public string ComponentType { get; set; }
            public bool Truncated { get; set; }
            public int LineCount { get; set; }
        }

    }
}
