using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
        private const int MaxListedBackups = 100;
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly VbaBackupStore _vbaBackupStore;
        private readonly object _observedModulesSync = new object();
        private readonly Dictionary<string, string> _observedModuleHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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

            yield return ControllerToolDefinition.Create(ToolId("vba_list_backups"), "Common", "Read-only: List up to 100 latest RNAssistant VBA rollback backups for the active document as metadata without duplicating source code.", "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}");
            yield return ControllerToolDefinition.Create(ToolId("vba_read_module"), "Common", "Read-only: List VBA component metadata when moduleName is omitted, or read one component when it is supplied. Omit startLine/lineCount for the whole source. Runtime resolves case and safely normalizable names.", ReadModuleSchema());
            yield return ControllerToolDefinition.Create(ToolId("vba_search_code"), "Common", "Read-only: Search literal or regex patterns across VBA component code. Use moduleName only to limit the search to one component.", "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"description\":\"Non-empty literal or regular-expression search query.\",\"minLength\":1,\"maxLength\":2048},\"moduleName\":{\"type\":\"string\",\"description\":\"Optional VBA component name; safely normalizable names are resolved by runtime.\",\"maxLength\":255},\"mode\":{\"type\":\"string\",\"description\":\"Text matching mode: literal or regex.\",\"default\":\"literal\",\"enum\":[\"literal\",\"regex\"]},\"matchCase\":{\"type\":\"boolean\",\"description\":\"Whether matching is case-sensitive.\",\"default\":false},\"wholeWord\":{\"type\":\"boolean\",\"description\":\"Whether only whole-word matches are accepted.\",\"default\":false},\"maxResults\":{\"type\":\"integer\",\"description\":\"Maximum number of matches returned.\",\"default\":100,\"minimum\":1,\"maximum\":500},\"contextChars\":{\"type\":\"integer\",\"description\":\"Maximum context characters returned around each match.\",\"default\":80,\"minimum\":0,\"maximum\":1000}},\"required\":[\"query\"],\"additionalProperties\":false}");
            yield return ControllerToolDefinition.Create(ToolId("vba_restore_backup"), "Common", "Mutates document: Restore a VBA module from an exact backupId, or restore the latest backup for moduleName when backupId is omitted. Runtime snapshots current state before confirmation.", RestoreBackupSchema(), mutatesDocument: true, agentCanRun: true, requiresConfirmation: true, riskLevel: 3);
            yield return ControllerToolDefinition.Create(ToolId("vba_write_module"), "Common", "Mutates document: Write the complete source of a VBA component. Updates it when present and creates it when missing. Runtime normalizes invalid new names, snapshots existing code, creates a rollback backup, and verifies read-back. componentType is used only when creating; MSForm code means code-behind only.", WriteModuleSchema(), mutatesDocument: true, agentCanRun: true, requiresConfirmation: true, riskLevel: 3);
            yield return ControllerToolDefinition.Create(ToolId("vba_apply_patch"), "Common", "Mutates document: Apply ordered structured literal, regex, insertion, or line patches to an existing component. Runtime reads and snapshots the target itself, so a separate read is optional and only needed to discover code. Combine known edits for one module into one native JSON patch array. Creates a rollback backup and verifies read-back.", ApplyPatchSchema(), mutatesDocument: true, agentCanRun: true, requiresConfirmation: true, riskLevel: 3);
            yield return ControllerToolDefinition.Create(ToolId("vba_delete_module"), "Common", "Mutates document: Delete an existing StdModule or ClassModule. Runtime reads it, validates the type, and creates a rollback backup; no separate read call is required. Document modules and UserForms are not deleted.", ModuleNameSchema(), mutatesDocument: true, agentCanRun: true, requiresConfirmation: true, riskLevel: 3);
        }

        public string ToolId(string suffix)
        {
            return "common." + suffix;
        }

        public string BackendToolId(string suffix)
        {
            return HostToolPrefix() + "." + suffix;
        }

        internal bool IsInternalToolId(string id)
        {
            return !string.IsNullOrWhiteSpace(id) &&
                (string.Equals(id, BackendToolId("vba_install_package_internal"), StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(id, BackendToolId("vba_remove_package_internal"), StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(id, BackendToolId("vba_list_project_components_internal"), StringComparison.OrdinalIgnoreCase));
        }

        public ToolResult ExecuteControllerTool(ToolCommand command, bool dryRun, ChatSession session, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(command.ToolId, ToolId("vba_list_backups"), StringComparison.OrdinalIgnoreCase))
            {
                return ListBackups();
            }

            if (string.Equals(command.ToolId, ToolId("vba_read_module"), StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty)))
                {
                    if (command.Arguments.ContainsKey("startLine") || command.Arguments.ContainsKey("lineCount"))
                    {
                        return ToolResult.Fail("moduleName is required when startLine or lineCount is supplied.", null, "invalid_arguments", true);
                    }
                    return ListModules();
                }
                return ReadModule(command, session);
            }
            if (string.Equals(command.ToolId, ToolId("vba_search_code"), StringComparison.OrdinalIgnoreCase)) return SearchCode(command, session);

            if (string.Equals(command.ToolId, ToolId("vba_restore_backup"), StringComparison.OrdinalIgnoreCase))
            {
                return RestoreVbaBackup(command, dryRun, session, cancellationToken);
            }

            if (string.Equals(command.ToolId, ToolId("vba_apply_patch"), StringComparison.OrdinalIgnoreCase))
            {
                return ApplyVbaPatch(command, dryRun, session, cancellationToken);
            }

            if (string.Equals(command.ToolId, ToolId("vba_write_module"), StringComparison.OrdinalIgnoreCase)) return WriteVbaModule(command, dryRun, session);
            if (string.Equals(command.ToolId, ToolId("vba_delete_module"), StringComparison.OrdinalIgnoreCase)) return DeleteModule(command, dryRun, session);

            return ToolResult.Fail("Unknown VBA controller tool: " + command.ToolId);
        }

        private ToolResult ListBackups()
        {
            var all = _vbaBackupStore.List(_adapter.HostName, _adapter.DocumentKey);
            var backups = all.Take(MaxListedBackups).Select(backup => new
            {
                backupId = backup.BackupId,
                moduleName = backup.ModuleName,
                componentType = backup.ComponentType,
                createdUtc = backup.CreatedUtc,
                codeLength = (backup.Code ?? string.Empty).Length,
                codeSha256 = CodeSha256(backup.Code)
            }).ToList();
            return ToolResult.Ok(
                "VBA backup metadata listed: " + backups.Count + (all.Count > backups.Count ? " (truncated)." : "."),
                JsonConvert.SerializeObject(new
                {
                    totalCount = all.Count,
                    returnedCount = backups.Count,
                    truncated = all.Count > backups.Count,
                    backups = backups
                }));
        }

        public void NormalizeLegacyArguments(ToolCommand command, string requestedToolId)
        {
            if (command == null || command.Arguments == null) return;
            if (IsPublicMutation(command.ToolId)) command.Arguments.Remove("expectedCodeSha256");
            if (string.Equals(command.ToolId, ToolId("vba_apply_patch"), StringComparison.OrdinalIgnoreCase) &&
                !command.Arguments.ContainsKey("patch") && command.Arguments.ContainsKey("find"))
            {
                var operation = new JObject
                {
                    ["op"] = ToolArgumentReader.Boolean(command.Arguments, "replaceAll", false) ? "replaceAll" : "replace",
                    ["find"] = ToolArgumentReader.String(command.Arguments, "find", string.Empty),
                    ["text"] = ToolArgumentReader.String(command.Arguments, "replace", string.Empty)
                };
                command.Arguments.Remove("find");
                command.Arguments.Remove("replace");
                command.Arguments.Remove("replaceAll");
                command.Arguments["patch"] = new JArray(operation);
            }
            if (string.Equals(command.ToolId, ToolId("vba_apply_patch"), StringComparison.OrdinalIgnoreCase))
            {
                NormalizePatchArguments(command);
            }
            if (VbaPublicToolIds.IsLegacyReadLines(requestedToolId) &&
                string.Equals(command.ToolId, ToolId("vba_read_module"), StringComparison.OrdinalIgnoreCase))
            {
                if (!command.Arguments.ContainsKey("startLine")) command.Arguments["startLine"] = 1;
                if (!command.Arguments.ContainsKey("lineCount")) command.Arguments["lineCount"] = 200;
            }
            if (VbaPublicToolIds.IsLegacyCreate(requestedToolId) &&
                string.Equals(command.ToolId, ToolId("vba_write_module"), StringComparison.OrdinalIgnoreCase) &&
                !command.Arguments.ContainsKey("mode"))
            {
                command.Arguments["mode"] = "createOnly";
            }
        }

        public ToolResult PrepareControllerTool(ToolCommand command, ChatSession session)
        {
            if (command == null || !string.IsNullOrWhiteSpace(command.RuntimeGuardJson)) return null;
            var moduleName = ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty);
            if (IsExistingModuleMutation(command.ToolId))
            {
                return PrepareExistingModuleGuard(command, session, moduleName);
            }
            if (string.Equals(command.ToolId, ToolId("vba_write_module"), StringComparison.OrdinalIgnoreCase))
            {
                return PrepareWriteGuard(command, session, moduleName);
            }
            if (string.Equals(command.ToolId, ToolId("vba_restore_backup"), StringComparison.OrdinalIgnoreCase))
            {
                var backup = _vbaBackupStore.Find(
                    _adapter.HostName,
                    _adapter.DocumentKey,
                    ToolArgumentReader.String(command.Arguments, "backupId", string.Empty),
                    moduleName);
                if (backup == null) return ToolResult.Fail("VBA backup not found.", null, "vba_backup_not_found", false);
                command.Arguments["backupId"] = backup.BackupId;
                command.Arguments["moduleName"] = backup.ModuleName;
                return PrepareCurrentModuleGuard(command, session, backup.ModuleName, backup.ComponentType);
            }
            return null;
        }

        public ToolResult PreviewPreparedControllerTool(ToolCommand command, ChatSession session, CancellationToken cancellationToken)
        {
            if (command == null || !IsPreflightMutation(command.ToolId)) return null;
            return ExecuteControllerTool(command, true, session, cancellationToken) ??
                ToolResult.Fail("VBA preflight returned no result.");
        }

        public void ObserveExpectedHash(ChatSession session, string moduleName, string codeSha256)
        {
            if (!string.IsNullOrWhiteSpace(moduleName) && !string.IsNullOrWhiteSpace(codeSha256))
            {
                RecordObservation(session, moduleName, codeSha256);
            }
        }

        public ToolResult PrepareBackupBeforeReplace(ToolCommand command, ChatSession session)
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

            var guardError = ValidateExistingModuleGuard(command, session, moduleName, existing);
            if (guardError != null)
            {
                return guardError;
            }
            ToolResult backupError;
            if (!TrySaveBackup(moduleName, existing, "replacement", out backupError))
            {
                return backupError;
            }
            return null;
        }

        private ToolResult ReadModule(ToolCommand command, ChatSession session)
        {
            var requestedModuleName = ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty);
            var moduleName = (requestedModuleName ?? string.Empty).Trim();
            var exactLines = ToolArgumentReader.Int32(command.Arguments, "startLine", 0) > 0 ||
                command.Arguments.ContainsKey("lineCount");
            var result = ExecuteModuleRead(command, moduleName, exactLines);
            var normalizedName = NormalizeModuleName(moduleName);
            if (IsModuleNotFound(result) && !string.Equals(moduleName, normalizedName, StringComparison.OrdinalIgnoreCase))
            {
                moduleName = normalizedName;
                command.Arguments["moduleName"] = moduleName;
                result = ExecuteModuleRead(command, moduleName, exactLines);
            }
            RecordObservationFromRead(session, moduleName, result);
            return result ?? ToolResult.Fail("VBA module read returned no result.", null, "vba_read_missing_result", true);
        }

        private ToolResult ExecuteModuleRead(ToolCommand command, string moduleName, bool exactLines)
        {
            var read = new ToolCommand
            {
                ToolId = BackendToolId(exactLines ? "vba_read_lines" : "vba_read_module")
            };
            read.Arguments["moduleName"] = moduleName;
            if (exactLines)
            {
                read.Arguments["startLine"] = Math.Max(1, ToolArgumentReader.Int32(command.Arguments, "startLine", 1));
                read.Arguments["lineCount"] = ToolArgumentReader.Int32(command.Arguments, "lineCount", 200);
            }
            else
            {
                read.Arguments["maxChars"] = ToolArgumentReader.Int32(command.Arguments, "maxChars", 30000);
            }
            return _adapter.ExecuteTool(read);
        }

        private ToolResult ListModules()
        {
            var read = new ToolCommand { ToolId = BackendToolId("vba_list_project_components_internal") };
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

        private ToolResult SearchCode(ToolCommand command, ChatSession session)
        {
            var query = ToolArgumentReader.String(command.Arguments, "query", string.Empty);
            if (string.IsNullOrWhiteSpace(query)) return ToolResult.Fail("query is required.");
            var requestedModuleFilter = ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty).Trim();
            var moduleFilter = requestedModuleFilter;
            var maxResults = Math.Max(1, Math.Min(500, ToolArgumentReader.Int32(command.Arguments, "maxResults", 100)));
            var contextChars = Math.Max(0, Math.Min(1000, ToolArgumentReader.Int32(command.Arguments, "contextChars", 80)));
            var read = new ToolCommand { ToolId = BackendToolId("vba_list_project_components_internal") };
            var project = _adapter.ExecuteTool(read);
            if (project == null || !project.Success) return project ?? ToolResult.Fail("VBA project returned no result.");
            try
            {
                var rows = new List<object>();
                var observedMatches = 0;
                var truncated = false;
                var modules = (JObject.Parse(project.DataJson ?? "{}")["modules"] as JArray ?? new JArray()).OfType<JObject>().ToList();
                if (!string.IsNullOrWhiteSpace(moduleFilter) && !modules.Any(module =>
                    string.Equals((string)module["name"], moduleFilter, StringComparison.OrdinalIgnoreCase)))
                {
                    var normalizedFilter = NormalizeModuleName(moduleFilter);
                    var normalizedMatch = modules.FirstOrDefault(module =>
                        string.Equals((string)module["name"], normalizedFilter, StringComparison.OrdinalIgnoreCase));
                    if (normalizedMatch != null)
                    {
                        moduleFilter = (string)normalizedMatch["name"] ?? normalizedFilter;
                        command.Arguments["moduleName"] = moduleFilter;
                    }
                }
                var matchedModule = string.IsNullOrWhiteSpace(moduleFilter);
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
                    var returnedForModule = false;
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
                        returnedForModule = true;
                    }
                    if (returnedForModule) RecordObservation(session, name, moduleHash);
                }
                if (!matchedModule)
                {
                    return ToolResult.Fail(
                        "VBA module not found: " + requestedModuleFilter + ".",
                        JsonConvert.SerializeObject(new
                        {
                            requestedModuleName = requestedModuleFilter,
                            normalizedModuleName = NormalizeModuleName(requestedModuleFilter),
                            discoveryTool = ToolId("vba_read_module")
                        }),
                        "vba_module_not_found",
                        true);
                }
                truncated = observedMatches > rows.Count;
                return ToolResult.Ok(
                    "VBA code matches returned: " + rows.Count + (truncated ? " (results truncated)." : "."),
                    JsonConvert.SerializeObject(new { matchCount = observedMatches, matchCountIsExact = true, returnedCount = rows.Count, truncated = truncated, matches = rows }));
            }
            catch (TextPatternException ex) { return ToolResult.Fail(ex.Message, null, ex.ErrorCode, false); }
            catch (JsonException ex) { return ToolResult.Fail("Could not parse VBA project: " + ex.Message, null, "vba_read_invalid", true); }
        }

        private ToolResult WriteVbaModule(ToolCommand command, bool dryRun, ChatSession session)
        {
            var moduleName = ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty);
            var componentType = ToolArgumentReader.String(command.Arguments, "componentType", "StdModule");
            var code = ToolArgumentReader.String(command.Arguments, "code", string.Empty);
            var mode = ToolArgumentReader.String(command.Arguments, "mode", "upsert");
            VbaModuleState existing;
            ToolResult readError;
            var exists = TryReadVbaModule(moduleName, 1000000, out existing, out readError);
            if (!exists && !IsModuleNotFound(readError)) return readError;
            var guardError = ValidateModuleGuard(command, session, moduleName, exists, existing);
            if (guardError != null) return guardError;
            if (exists && string.Equals(mode, "createOnly", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Fail(
                    "VBA module already exists: " + moduleName + ". Use mode=upsert to replace its complete source, or common.vba_apply_patch for a targeted edit.",
                    JsonConvert.SerializeObject(new { moduleName = moduleName, suggestedMode = "upsert", patchTool = ToolId("vba_apply_patch") }),
                    "vba_module_exists",
                    true);
            }
            if (!exists && string.Equals(mode, "updateOnly", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Fail(
                    "VBA module does not exist: " + moduleName + ". Use mode=upsert to create it automatically.",
                    JsonConvert.SerializeObject(new { moduleName = moduleName, suggestedMode = "upsert" }),
                    "vba_module_not_found",
                    true);
            }
            var guard = ReadGuard(command);
            var operationData = JsonConvert.SerializeObject(new
            {
                requestedModuleName = guard == null ? moduleName : guard.RequestedModuleName,
                moduleName = moduleName,
                nameNormalized = guard != null && !string.Equals(guard.RequestedModuleName, moduleName, StringComparison.Ordinal),
                componentType = exists ? existing.ComponentType : componentType,
                mode = mode,
                created = !exists,
                codeSha256 = CodeSha256(code)
            });
            if (dryRun)
            {
                return ToolResult.Ok(
                    "Dry run: would " + (exists ? "update" : "create") + " VBA " +
                    (exists ? existing.ComponentType : componentType) + " " + moduleName + ".",
                    operationData);
            }

            ToolResult written;
            var expectedComponentType = exists ? existing.ComponentType : componentType;
            if (exists)
            {
                ToolResult backupError;
                if (!TrySaveBackup(moduleName, existing, "write", out backupError)) return backupError;
                written = WriteModule(moduleName, code, false);
            }
            else
            {
                var create = new ToolCommand { ToolId = BackendToolId("vba_create_module_internal") };
                create.Arguments["moduleName"] = moduleName;
                create.Arguments["componentType"] = componentType;
                create.Arguments["code"] = code;
                written = _adapter.ExecuteTool(create);
            }
            if (written == null || !written.Success)
            {
                return written ?? ToolResult.Fail("VBA module write returned no result.", null, "vba_write_failed", false);
            }
            return VerifyModuleWrite(
                moduleName,
                code,
                "VBA module " + (exists ? "updated: " : "created: ") + moduleName,
                operationData,
                "vba_write",
                expectedComponentType,
                session);
        }

        private ToolResult DeleteModule(ToolCommand command, bool dryRun, ChatSession session)
        {
            var moduleName = ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty);
            VbaModuleState module;
            ToolResult error;
            if (!TryReadVbaModule(moduleName, 1000000, out module, out error)) return error;
            if (!string.Equals(module.ComponentType, "StdModule", StringComparison.OrdinalIgnoreCase) && !string.Equals(module.ComponentType, "ClassModule", StringComparison.OrdinalIgnoreCase))
                return ToolResult.Fail("Document modules and UserForms cannot be deleted through RNAssistant.", null, "vba_component_type_read_only", false);
            var guardError = ValidateExistingModuleGuard(command, session, moduleName, module);
            if (guardError != null) return guardError;
            if (dryRun)
            {
                return ToolResult.Ok(
                    "Dry run: would delete VBA " + module.ComponentType + " " + moduleName + ".",
                    JsonConvert.SerializeObject(new { moduleName = moduleName, componentType = module.ComponentType }));
            }
            ToolResult backupError;
            if (!TrySaveBackup(moduleName, module, "delete", out backupError)) return backupError;
            var delete = new ToolCommand { ToolId = BackendToolId("vba_delete_module_internal") };
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
            RemoveObservation(session, moduleName);
            return ToolResult.Ok("VBA module deleted: " + moduleName, deleted.DataJson ?? JsonConvert.SerializeObject(new { moduleName = moduleName }));
        }

        private ToolResult RestoreVbaBackup(ToolCommand command, bool dryRun, ChatSession session, CancellationToken cancellationToken)
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
                return ToolResult.Ok(
                    "Dry run: would restore VBA backup " + backup.BackupId + " to " + backup.ModuleName + ".",
                    new JObject
                    {
                        ["backupId"] = backup.BackupId,
                        ["moduleName"] = backup.ModuleName,
                        ["componentType"] = backup.ComponentType,
                        ["createdUtc"] = backup.CreatedUtc,
                        ["codeLength"] = (backup.Code ?? string.Empty).Length,
                        ["codeSha256"] = CodeSha256(backup.Code)
                    }.ToString(Formatting.None));
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

            var guardError = ValidateModuleGuard(command, session, backup.ModuleName, moduleExists, current);
            if (guardError != null) return guardError;
            if (moduleExists)
            {
                ToolResult backupError;
                if (!TrySaveBackup(backup.ModuleName, current, "restore", out backupError))
                {
                    return backupError;
                }
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
                var create = new ToolCommand { ToolId = BackendToolId("vba_create_module_internal") };
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
                componentType,
                session);
        }

        private ToolResult ApplyVbaPatch(ToolCommand command, bool dryRun, ChatSession session, CancellationToken cancellationToken)
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
            var currentHash = CodeSha256(code);
            var guardError = ValidateExistingModuleGuard(command, session, moduleName, module);
            if (guardError != null) return guardError;
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
                "vba_patch",
                null,
                session);
        }

        private bool TryReadVbaModule(string moduleName, int maxChars, out VbaModuleState module, out ToolResult error)
        {
            module = null;
            error = null;
            var read = new ToolCommand { ToolId = BackendToolId("vba_read_module") };
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
                    Name = (string)data["name"] ?? moduleName,
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

        private bool TryReadExistingModule(
            string requestedModuleName,
            out string resolvedModuleName,
            out VbaModuleState module,
            out ToolResult error)
        {
            requestedModuleName = (requestedModuleName ?? string.Empty).Trim();
            resolvedModuleName = requestedModuleName;
            if (TryReadVbaModule(requestedModuleName, 1000000, out module, out error))
            {
                resolvedModuleName = string.IsNullOrWhiteSpace(module.Name) ? requestedModuleName : module.Name;
                return true;
            }
            if (!IsModuleNotFound(error)) return false;

            var normalizedName = NormalizeModuleName(requestedModuleName);
            if (!string.Equals(requestedModuleName, normalizedName, StringComparison.OrdinalIgnoreCase) &&
                TryReadVbaModule(normalizedName, 1000000, out module, out error))
            {
                resolvedModuleName = string.IsNullOrWhiteSpace(module.Name) ? normalizedName : module.Name;
                return true;
            }
            if (!IsModuleNotFound(error)) return false;

            resolvedModuleName = normalizedName;
            error = ToolResult.Fail(
                "VBA module not found: " + requestedModuleName +
                (string.Equals(requestedModuleName, normalizedName, StringComparison.Ordinal)
                    ? "."
                    : ". Runtime also tried the normalized name " + normalizedName + ".") +
                " Call common.vba_read_module without moduleName only if the target name is unknown.",
                JsonConvert.SerializeObject(new
                {
                    requestedModuleName = requestedModuleName,
                    normalizedModuleName = normalizedName,
                    discoveryTool = ToolId("vba_read_module")
                }),
                "vba_module_not_found",
                true);
            module = null;
            return false;
        }

        private ToolResult WriteModule(string moduleName, string code, bool createIfMissing)
        {
            var write = new ToolCommand { ToolId = BackendToolId("vba_replace_module") };
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
            string expectedComponentType = null,
            ChatSession session = null)
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

            RecordObservation(session, moduleName, actualHash);
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

        private bool IsPublicMutation(string toolId)
        {
            return string.Equals(toolId, ToolId("vba_write_module"), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, ToolId("vba_apply_patch"), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, ToolId("vba_delete_module"), StringComparison.OrdinalIgnoreCase);
        }

        private bool IsExistingModuleMutation(string toolId)
        {
            return string.Equals(toolId, ToolId("vba_apply_patch"), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, ToolId("vba_delete_module"), StringComparison.OrdinalIgnoreCase);
        }

        private bool IsPreflightMutation(string toolId)
        {
            return IsPublicMutation(toolId) ||
                string.Equals(toolId, ToolId("vba_restore_backup"), StringComparison.OrdinalIgnoreCase);
        }

        private ToolResult PrepareExistingModuleGuard(ToolCommand command, ChatSession session, string moduleName)
        {
            if (string.IsNullOrWhiteSpace(moduleName)) return ToolResult.Fail("moduleName is required.", null, "vba_module_name_required", true);
            string resolvedName;
            VbaModuleState current;
            ToolResult readError;
            if (!TryReadExistingModule(moduleName, out resolvedName, out current, out readError)) return readError;
            command.Arguments["moduleName"] = resolvedName;
            var currentHash = CodeSha256(current.Code);
            string observedHash;
            if (TryGetObservation(session, resolvedName, out observedHash) &&
                !string.Equals(observedHash, currentHash, StringComparison.OrdinalIgnoreCase))
            {
                RemoveObservation(session, resolvedName);
                var operation = string.Equals(command.ToolId, ToolId("vba_apply_patch"), StringComparison.OrdinalIgnoreCase)
                    ? "patch"
                    : "mutation";
                return StaleSnapshot(resolvedName, true, observedHash, true, currentHash, operation);
            }
            BindGuard(command, session, resolvedName, true, currentHash, moduleName);
            return null;
        }

        private ToolResult PrepareWriteGuard(ToolCommand command, ChatSession session, string moduleName)
        {
            if (string.IsNullOrWhiteSpace(moduleName)) return ToolResult.Fail("moduleName is required.", null, "vba_module_name_required", true);
            var requestedName = moduleName.Trim();
            VbaModuleState existing;
            ToolResult readError;
            if (TryReadVbaModule(requestedName, 1000000, out existing, out readError))
            {
                var existingName = string.IsNullOrWhiteSpace(existing.Name) ? requestedName : existing.Name;
                command.Arguments["moduleName"] = existingName;
                return BindWriteGuard(command, session, existingName, existing, requestedName);
            }
            if (!IsModuleNotFound(readError)) return readError;

            var normalizedName = NormalizeModuleName(requestedName);
            if (!string.Equals(requestedName, normalizedName, StringComparison.OrdinalIgnoreCase) &&
                TryReadVbaModule(normalizedName, 1000000, out existing, out readError))
            {
                var existingName = string.IsNullOrWhiteSpace(existing.Name) ? normalizedName : existing.Name;
                command.Arguments["moduleName"] = existingName;
                return BindWriteGuard(command, session, existingName, existing, requestedName);
            }
            if (!IsModuleNotFound(readError)) return readError;

            command.Arguments["moduleName"] = normalizedName;
            BindGuard(command, session, normalizedName, false, null, requestedName);
            return null;
        }

        private ToolResult BindWriteGuard(
            ToolCommand command,
            ChatSession session,
            string moduleName,
            VbaModuleState existing,
            string requestedName)
        {
            var currentHash = CodeSha256(existing == null ? string.Empty : existing.Code);
            string observedHash;
            if (TryGetObservation(session, moduleName, out observedHash) &&
                !string.Equals(observedHash, currentHash, StringComparison.OrdinalIgnoreCase))
            {
                RemoveObservation(session, moduleName);
                return StaleSnapshot(moduleName, true, observedHash, true, currentHash, "write");
            }
            BindGuard(command, session, moduleName, true, currentHash, requestedName);
            return null;
        }

        private ToolResult PrepareCurrentModuleGuard(ToolCommand command, ChatSession session, string moduleName, string expectedComponentType)
        {
            VbaModuleState current;
            ToolResult readError;
            if (TryReadVbaModule(moduleName, 1000000, out current, out readError))
            {
                if (!string.IsNullOrWhiteSpace(expectedComponentType) &&
                    !string.Equals(expectedComponentType, current.ComponentType, StringComparison.OrdinalIgnoreCase))
                {
                    return ToolResult.Fail(
                        "VBA restore was blocked because the current component type differs from the backup.",
                        JsonConvert.SerializeObject(new { moduleName = moduleName, backupType = expectedComponentType, currentType = current.ComponentType }),
                        "vba_restore_component_type_mismatch",
                        false);
                }
                BindGuard(command, session, moduleName, true, CodeSha256(current.Code), moduleName);
                return null;
            }
            if (!IsModuleNotFound(readError)) return readError;
            BindGuard(command, session, moduleName, false, null, moduleName);
            return null;
        }

        private ToolResult ValidateExistingModuleGuard(ToolCommand command, ChatSession session, string moduleName, VbaModuleState current)
        {
            if (current == null) return ToolResult.Fail("VBA module state is unavailable.", null, "vba_read_invalid", true);
            if (string.IsNullOrWhiteSpace(command == null ? null : command.RuntimeGuardJson))
            {
                string observedHash;
                if (!TryGetObservation(session, moduleName, out observedHash)) return SnapshotRequired(moduleName);
                var actualHash = CodeSha256(current.Code);
                if (!string.Equals(observedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                {
                    RemoveObservation(session, moduleName);
                    return StaleSnapshot(moduleName, true, observedHash, true, actualHash, "editor");
                }
                BindGuard(command, session, moduleName, true, observedHash);
                return null;
            }
            return ValidateModuleGuard(command, session, moduleName, true, current);
        }

        private ToolResult ValidateModuleGuard(
            ToolCommand command,
            ChatSession session,
            string moduleName,
            bool moduleExists,
            VbaModuleState current)
        {
            var guard = ReadGuard(command);
            if (guard == null || guard.Version != 1 || string.IsNullOrWhiteSpace(guard.ModuleName))
            {
                return SnapshotRequired(moduleName);
            }
            if (!GuardContextMatches(guard, session, moduleName))
            {
                return ToolResult.Fail(
                    "The prepared VBA action belongs to another document, chat, or module. Retry the same tool in the current document.",
                    JsonConvert.SerializeObject(new { moduleName = moduleName, retrySameTool = true }),
                    "vba_snapshot_context_changed",
                    true);
            }
            var actualHash = moduleExists && current != null ? CodeSha256(current.Code) : null;
            if (guard.ModuleExists != moduleExists ||
                moduleExists && !string.Equals(guard.CodeSha256, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                RemoveObservation(session, moduleName);
                var operation = string.Equals(command.ToolId, ToolId("vba_write_module"), StringComparison.OrdinalIgnoreCase)
                    ? "write"
                    : string.Equals(command.ToolId, ToolId("vba_apply_patch"), StringComparison.OrdinalIgnoreCase)
                        ? "patch"
                        : "mutation";
                return StaleSnapshot(moduleName, guard.ModuleExists, guard.CodeSha256, moduleExists, actualHash, operation);
            }
            return null;
        }

        private bool GuardContextMatches(VbaMutationGuard guard, ChatSession session, string moduleName)
        {
            if (!string.Equals(guard.Host, _adapter.HostName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(guard.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase)) return false;
            var sessionId = session == null ? string.Empty : session.Id ?? string.Empty;
            if (!string.Equals(guard.SessionId ?? string.Empty, sessionId, StringComparison.OrdinalIgnoreCase)) return false;
            var documentKey = _adapter.DocumentKey ?? string.Empty;
            if (OfficeDocumentExecutionGuardState.IdentityMatches(
                guard.DocumentKey,
                string.Empty,
                documentKey,
                string.Empty)) return true;
            var runtimeKey = _adapter.RuntimeDocumentKey ?? string.Empty;
            return OfficeDocumentExecutionGuardState.IdentityMatches(
                string.Empty,
                guard.RuntimeDocumentKey,
                string.Empty,
                runtimeKey);
        }

        private static VbaMutationGuard ReadGuard(ToolCommand command)
        {
            try
            {
                return JsonConvert.DeserializeObject<VbaMutationGuard>(command == null ? null : command.RuntimeGuardJson);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private void BindGuard(
            ToolCommand command,
            ChatSession session,
            string moduleName,
            bool moduleExists,
            string hash,
            string requestedModuleName = null)
        {
            if (command == null) return;
            command.RuntimeGuardJson = JsonConvert.SerializeObject(new VbaMutationGuard
            {
                Version = 1,
                Host = _adapter.HostName ?? string.Empty,
                DocumentKey = _adapter.DocumentKey ?? string.Empty,
                RuntimeDocumentKey = _adapter.RuntimeDocumentKey ?? string.Empty,
                SessionId = session == null ? string.Empty : session.Id ?? string.Empty,
                ModuleName = moduleName ?? string.Empty,
                RequestedModuleName = string.IsNullOrWhiteSpace(requestedModuleName) ? moduleName ?? string.Empty : requestedModuleName,
                ModuleExists = moduleExists,
                CodeSha256 = moduleExists ? hash ?? string.Empty : string.Empty
            });
        }

        private ToolResult SnapshotRequired(string moduleName)
        {
            return ToolResult.Fail(
                "The internal VBA snapshot is missing. Retry the same public VBA tool, or reload the VBA editor before retrying an editor save.",
                JsonConvert.SerializeObject(new
                {
                    moduleName = moduleName ?? string.Empty,
                    retrySameTool = true,
                    reloadEditor = true
                }),
                "vba_internal_snapshot_missing",
                true);
        }

        private ToolResult StaleSnapshot(
            string moduleName,
            bool observedExists,
            string observedHash,
            bool actualExists,
            string actualHash,
            string operation)
        {
            var editor = string.Equals(operation, "editor", StringComparison.OrdinalIgnoreCase);
            var wholeWrite = string.Equals(operation, "write", StringComparison.OrdinalIgnoreCase);
            var message = editor
                ? "The VBA module changed after it was loaded in the editor. Reload it and reconcile the changes before saving."
                : wholeWrite
                    ? "The VBA module changed after the source was inspected or this write was prepared. Re-read and reconcile if the complete source was derived from that version; retry the same write only for an intentional complete overwrite."
                    : "The VBA module changed after this action was prepared. Retry the same tool so runtime can bind the current state; read it only if the intended action may no longer match.";
            return ToolResult.Fail(
                message,
                JsonConvert.SerializeObject(new
                {
                    moduleName = moduleName ?? string.Empty,
                    observedExists = observedExists,
                    observedCodeSha256 = string.IsNullOrWhiteSpace(observedHash) ? null : observedHash,
                    actualExists = actualExists,
                    actualCodeSha256 = string.IsNullOrWhiteSpace(actualHash) ? null : actualHash,
                    retrySameTool = !editor,
                    reloadEditor = editor,
                    reconcileBeforeOverwrite = wholeWrite,
                    inspectTool = ToolId("vba_read_module")
                }),
                "stale_vba_module",
                true);
        }

        private void RecordObservationFromRead(ChatSession session, string moduleName, ToolResult result)
        {
            if (result == null || !result.Success || string.IsNullOrWhiteSpace(result.DataJson)) return;
            try
            {
                var data = JObject.Parse(result.DataJson);
                var hash = (string)data["codeSha256"];
                var actualName = (string)data["name"] ?? moduleName;
                if (!string.IsNullOrWhiteSpace(hash)) RecordObservation(session, actualName, hash);
            }
            catch (JsonException) { }
        }

        private void RecordObservation(ChatSession session, string moduleName, string hash)
        {
            if (string.IsNullOrWhiteSpace(moduleName) || string.IsNullOrWhiteSpace(hash)) return;
            var key = ObservationKey(session, moduleName);
            lock (_observedModulesSync)
            {
                if (_observedModuleHashes.Count >= 1024 && !_observedModuleHashes.ContainsKey(key))
                {
                    _observedModuleHashes.Clear();
                }
                _observedModuleHashes[key] = hash;
            }
        }

        private bool TryGetObservation(ChatSession session, string moduleName, out string hash)
        {
            lock (_observedModulesSync)
            {
                return _observedModuleHashes.TryGetValue(ObservationKey(session, moduleName), out hash);
            }
        }

        private void RemoveObservation(ChatSession session, string moduleName)
        {
            lock (_observedModulesSync)
            {
                _observedModuleHashes.Remove(ObservationKey(session, moduleName));
            }
        }

        private string ObservationKey(ChatSession session, string moduleName)
        {
            var runtimeKey = _adapter.RuntimeDocumentKey ?? string.Empty;
            var documentIdentity = string.IsNullOrWhiteSpace(runtimeKey)
                ? "document:" + (_adapter.DocumentKey ?? string.Empty)
                : "runtime:" + runtimeKey;
            return (session == null ? string.Empty : session.Id ?? string.Empty) + "|" +
                (_adapter.HostName ?? string.Empty) + "|" + documentIdentity + "|" + (moduleName ?? string.Empty);
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

        private static string NormalizeModuleName(string value)
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

        private static string ModuleNameSchema()
        {
            return "{\"type\":\"object\",\"properties\":{" +
                "\"moduleName\":{\"type\":\"string\",\"description\":\"VBA component name. Case and safely normalizable punctuation/length are resolved by runtime.\",\"minLength\":1,\"maxLength\":255}" +
                "},\"required\":[\"moduleName\"],\"additionalProperties\":false}";
        }

        private static string ReadModuleSchema()
        {
            return "{\"type\":\"object\",\"properties\":{" +
                "\"moduleName\":{\"type\":\"string\",\"description\":\"Optional VBA component name; omit to list component metadata.\",\"minLength\":1,\"maxLength\":255}," +
                "\"startLine\":{\"type\":\"integer\",\"description\":\"Optional one-based first line for range mode; omit for the whole module.\",\"minimum\":1}," +
                "\"lineCount\":{\"type\":\"integer\",\"description\":\"Optional maximum consecutive lines; when supplied alone, range mode starts at line 1. Runtime uses 200 when only startLine is supplied.\",\"minimum\":1,\"maximum\":500}," +
                "\"maxChars\":{\"type\":\"integer\",\"description\":\"Maximum source characters in whole-module mode.\",\"default\":30000,\"minimum\":1,\"maximum\":1000000}" +
                "},\"required\":[],\"additionalProperties\":false}";
        }

        private static string WriteModuleSchema()
        {
            return "{\"type\":\"object\",\"properties\":{" +
                "\"moduleName\":{\"type\":\"string\",\"description\":\"Desired VBA component name. Invalid punctuation, a non-letter prefix, and names over the VBE limit of 31 characters are normalized deterministically when creating; the result returns the actual name.\",\"minLength\":1,\"maxLength\":255}," +
                "\"code\":{\"type\":\"string\",\"description\":\"Complete VBA source or MSForm code-behind. Empty text intentionally clears an existing component or creates an empty one.\"}," +
                "\"componentType\":{\"type\":\"string\",\"description\":\"Type used only if the component must be created.\",\"default\":\"StdModule\",\"enum\":[\"StdModule\",\"ClassModule\",\"MSForm\"]}," +
                "\"mode\":{\"type\":\"string\",\"description\":\"upsert updates or creates automatically; createOnly/updateOnly are optional strict modes.\",\"default\":\"upsert\",\"enum\":[\"upsert\",\"createOnly\",\"updateOnly\"]}" +
                "},\"required\":[\"moduleName\",\"code\"],\"additionalProperties\":false}";
        }

        private static string RestoreBackupSchema()
        {
            Func<JObject> properties = () => new JObject
            {
                ["backupId"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Exact rollback backup identifier from common.vba_list_backups.",
                    ["minLength"] = 1
                },
                ["moduleName"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "VBA component whose latest backup is selected when backupId is omitted.",
                    ["minLength"] = 1
                }
            };
            Func<string, JObject> variant = required => new JObject
            {
                ["type"] = "object",
                ["properties"] = properties(),
                ["required"] = new JArray(required),
                ["additionalProperties"] = false
            };
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties(),
                ["required"] = new JArray(),
                ["additionalProperties"] = false,
                ["anyOf"] = new JArray(variant("backupId"), variant("moduleName"))
            }.ToString(Formatting.None);
        }

        private static string ApplyPatchSchema()
        {
            var find = new JObject
            {
                ["type"] = "string",
                ["description"] = "Non-empty exact text or unique insertion anchor.",
                ["minLength"] = 1
            };
            var text = new JObject
            {
                ["type"] = "string",
                ["description"] = "Replacement or inserted VBA code; empty text is valid for replacement/deletion."
            };
            var operations = new JArray
            {
                PatchOperationSchema("replace", "Replace exactly one occurrence; ambiguity is rejected.",
                    new JObject { ["find"] = find.DeepClone(), ["text"] = text.DeepClone() }, "find", "text"),
                PatchOperationSchema("replaceAll", "Replace every exact occurrence explicitly.",
                    new JObject { ["find"] = find.DeepClone(), ["text"] = text.DeepClone() }, "find", "text"),
                PatchOperationSchema("replaceFirst", "Replace the first exact occurrence.",
                    new JObject { ["find"] = find.DeepClone(), ["text"] = text.DeepClone() }, "find", "text"),
                PatchOperationSchema("insertBefore", "Insert a non-empty line-safe block before one unique anchor.",
                    new JObject
                    {
                        ["find"] = find.DeepClone(),
                        ["text"] = new JObject { ["type"] = "string", ["description"] = "Non-empty VBA block to insert.", ["minLength"] = 1 }
                    }, "find", "text"),
                PatchOperationSchema("insertAfter", "Insert a non-empty line-safe block after one unique anchor.",
                    new JObject
                    {
                        ["find"] = find.DeepClone(),
                        ["text"] = new JObject { ["type"] = "string", ["description"] = "Non-empty VBA block to insert.", ["minLength"] = 1 }
                    }, "find", "text"),
                PatchOperationSchema("replaceLines", "Replace or delete a current one-based line range after preceding operations.",
                    new JObject
                    {
                        ["startLine"] = new JObject { ["type"] = "integer", ["description"] = "One-based first line.", ["minimum"] = 1 },
                        ["deleteCount"] = new JObject { ["type"] = "integer", ["description"] = "Number of existing lines to delete.", ["minimum"] = 0 },
                        ["text"] = text.DeepClone()
                    }, "startLine", "deleteCount", "text"),
                PatchOperationSchema("regexReplace", "Replace a bounded literal or capture-group regex match set.",
                    new JObject
                    {
                        ["pattern"] = new JObject { ["type"] = "string", ["description"] = "Non-empty regular expression.", ["minLength"] = 1 },
                        ["text"] = new JObject { ["type"] = "string", ["description"] = "Replacement text; capture groups such as $1 are supported." },
                        ["matchCase"] = new JObject { ["type"] = "boolean", ["description"] = "Whether matching is case-sensitive.", ["default"] = true },
                        ["wholeWord"] = new JObject { ["type"] = "boolean", ["description"] = "Whether only whole-word matches are accepted.", ["default"] = false },
                        ["replaceAll"] = new JObject { ["type"] = "boolean", ["description"] = "Whether every match is replaced.", ["default"] = true },
                        ["maxReplacements"] = new JObject { ["type"] = "integer", ["description"] = "Maximum replacements allowed.", ["default"] = 500, ["minimum"] = 1, ["maximum"] = 10000 }
                    }, "pattern", "text")
            };
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["moduleName"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Existing VBA component name. Case and safely normalizable punctuation/length are resolved by runtime.",
                        ["minLength"] = 1,
                        ["maxLength"] = 255
                    },
                    ["patch"] = new JObject
                    {
                        ["type"] = "array",
                        ["description"] = "Native JSON array of ordered patch operations applied to one current module snapshot; never encode this array as a string.",
                        ["minItems"] = 1,
                        ["maxItems"] = 100,
                        ["items"] = new JObject { ["anyOf"] = operations }
                    }
                },
                ["required"] = new JArray("moduleName", "patch"),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }

        private static JObject PatchOperationSchema(string operation, string description, JObject properties, params string[] required)
        {
            properties = properties ?? new JObject();
            properties.AddFirst(new JProperty("op", new JObject
            {
                ["type"] = "string",
                ["description"] = description,
                ["enum"] = new JArray(operation)
            }));
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JArray(new[] { "op" }.Concat(required ?? new string[0])),
                ["additionalProperties"] = false
            };
        }

        private static void NormalizePatchArguments(ToolCommand command)
        {
            object raw;
            if (command == null || command.Arguments == null || !command.Arguments.TryGetValue("patch", out raw) || raw == null) return;
            JToken patch;
            try
            {
                var token = raw as JToken;
                patch = token != null
                    ? token.DeepClone()
                    : raw is string ? JToken.Parse((string)raw) : JToken.FromObject(raw);
            }
            catch (JsonException)
            {
                return;
            }
            var operations = patch as JArray;
            if (operations == null) return;
            foreach (var operation in operations.OfType<JObject>())
            {
                foreach (var property in operation.Properties().Where(item =>
                    item.Value.Type == JTokenType.Null || item.Value.Type == JTokenType.Undefined).ToList())
                {
                    property.Remove();
                }
                if (operation["text"] == null && operation["replace"] != null)
                {
                    operation["text"] = operation["replace"];
                }
                operation.Remove("replace");
            }
            command.Arguments["patch"] = operations;
        }

        private sealed class VbaModuleState
        {
            public string Name { get; set; }
            public string Code { get; set; }
            public string ComponentType { get; set; }
            public bool Truncated { get; set; }
            public int LineCount { get; set; }
        }

        private sealed class VbaMutationGuard
        {
            public int Version { get; set; }
            public string Host { get; set; }
            public string DocumentKey { get; set; }
            public string RuntimeDocumentKey { get; set; }
            public string SessionId { get; set; }
            public string ModuleName { get; set; }
            public string RequestedModuleName { get; set; }
            public bool ModuleExists { get; set; }
            public string CodeSha256 { get; set; }
        }

    }

    internal static class VbaPublicToolIds
    {
        public static bool IsLegacyReadLines(string id)
        {
            return !string.IsNullOrWhiteSpace(id) && id.EndsWith(".vba_read_lines", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsLegacyCreate(string id)
        {
            return !string.IsNullOrWhiteSpace(id) && id.EndsWith(".vba_create_module", StringComparison.OrdinalIgnoreCase);
        }

        public static string Canonicalize(string id)
        {
            if (string.Equals(id, "common.vba_list_modules", StringComparison.OrdinalIgnoreCase)) return "common.vba_read_module";
            if (string.Equals(id, "common.vba_read_lines", StringComparison.OrdinalIgnoreCase)) return "common.vba_read_module";
            if (string.Equals(id, "common.vba_replace_text", StringComparison.OrdinalIgnoreCase)) return "common.vba_apply_patch";
            if (string.Equals(id, "common.vba_create_module", StringComparison.OrdinalIgnoreCase)) return "common.vba_write_module";
            return id;
        }

        public static IEnumerable<KeyValuePair<string, string>> LegacyAliases()
        {
            yield return new KeyValuePair<string, string>("common.vba_list_modules", "common.vba_read_module");
            yield return new KeyValuePair<string, string>("common.vba_read_lines", "common.vba_read_module");
            yield return new KeyValuePair<string, string>("common.vba_replace_text", "common.vba_apply_patch");
            yield return new KeyValuePair<string, string>("common.vba_create_module", "common.vba_write_module");
        }
    }
}
