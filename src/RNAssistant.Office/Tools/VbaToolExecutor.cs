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
using RNAssistant.Office.Tools;

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

            yield return ControllerToolDefinition.Create(ToolId("vba_list_backups"), _adapter.HostName, "Read-only: List RNAssistant VBA rollback backups for the current document.", "{}");
            yield return ControllerToolDefinition.Create(ToolId("vba_restore_backup"), _adapter.HostName, "Mutates document: Restore a VBA module from a backupId or from the latest backup for moduleName.", "{\"backupId\":\"\",\"moduleName\":\"Module1\"}", mutatesDocument: true, agentCanRun: false, riskLevel: 3);
            yield return ControllerToolDefinition.Create(ToolId("vba_replace_text"), _adapter.HostName, "Mutates document: Replace an exact text fragment inside one VBA module and create a rollback backup.", "{\"moduleName\":\"Module1\",\"find\":\"old code\",\"replace\":\"new code\"}", mutatesDocument: true, agentCanRun: false, riskLevel: 3);
            yield return ControllerToolDefinition.Create(ToolId("vba_apply_patch"), _adapter.HostName, "Mutates document: Apply structured VBA code patches and create a rollback backup.", "{\"moduleName\":\"Module1\",\"patch\":[{\"op\":\"replace\",\"find\":\"old\",\"text\":\"new\"},{\"op\":\"replaceLines\",\"startLine\":10,\"deleteCount\":2,\"text\":\"new code\"}]}", mutatesDocument: true, agentCanRun: false, riskLevel: 3);
        }

        public string ToolId(string suffix)
        {
            return HostToolPrefix() + "." + suffix;
        }

        public ToolResult ExecuteControllerTool(ToolCommand command, bool dryRun, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(command.ToolId, ToolId("vba_list_backups"), StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Ok("VBA backups listed.", JsonConvert.SerializeObject(_vbaBackupStore.List(_adapter.HostName, _adapter.DocumentKey)));
            }

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

            return ToolResult.Fail("Unknown VBA controller tool: " + command.ToolId);
        }

        public ToolResult ExecuteCustomTool(ToolDefinition tool, ToolCommand command, AppSettings settings, bool dryRun, bool manualRun)
        {
            if (string.IsNullOrWhiteSpace(tool.Code))
            {
                return ToolResult.Fail("VBA tool has no code: " + tool.Id);
            }
            if (!dryRun && !manualRun && !settings.AutoConfirmToolActions)
            {
                return ToolResult.WaitingConfirmation("VBA tool requires confirmation before execution: " + tool.Id);
            }

            var moduleName = ToolArgumentReader.String(command.Arguments, "moduleName", ToolModuleName(tool.Id));
            var macroName = ToolArgumentReader.String(command.Arguments, "macroName", string.Empty);
            if (dryRun)
            {
                return ToolResult.Ok("Dry run: would insert VBA module " + moduleName + (string.IsNullOrWhiteSpace(macroName) ? string.Empty : " and run " + macroName), JsonConvert.SerializeObject(new { moduleName = moduleName, macroName = macroName, code = tool.Code }));
            }

            var insert = new ToolCommand { ToolId = ToolId("insert_vba_module") };
            insert.Arguments["moduleName"] = moduleName;
            insert.Arguments["code"] = tool.Code;
            var insertResult = _adapter.ExecuteTool(insert);
            if (!insertResult.Success)
            {
                return insertResult;
            }

            var verification = CreateVerification(moduleName, tool.Code);
            if (string.IsNullOrWhiteSpace(macroName))
            {
                var inserted = ToolResult.Ok("VBA tool inserted: " + tool.Id, JsonConvert.SerializeObject(new { insert = insertResult }));
                inserted.Verification = verification;
                return inserted;
            }

            var run = new ToolCommand { ToolId = ToolId("run_macro") };
            run.Arguments["macroName"] = macroName;
            var runResult = _adapter.ExecuteTool(run);
            var dataJson = JsonConvert.SerializeObject(new { insert = insertResult, run = runResult });
            if (!runResult.Success)
            {
                var partial = ToolResult.PartialFailure(
                    "VBA module was inserted, but the macro failed: " + (runResult.Message ?? macroName),
                    dataJson,
                    "vba_macro_failed_after_insert");
                partial.Verification = verification;
                return partial;
            }

            var executed = ToolResult.Ok("VBA tool executed: " + tool.Id, dataJson);
            executed.Verification = verification;
            return executed;
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

            var restored = ToolResult.Ok("VBA backup restored: " + backup.BackupId, JsonConvert.SerializeObject(new { backupId = backup.BackupId, moduleName = backup.ModuleName, restore = result }));
            restored.Verification = CreateVerification(backup.ModuleName, backup.Code);
            return restored;
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
            var replacements = CountOccurrences(code, find);
            if (replacements == 0)
            {
                return ToolResult.Fail("Text fragment was not found in VBA module: " + moduleName);
            }

            var updated = code.Replace(find, replace ?? string.Empty);
            var preview = JsonConvert.SerializeObject(new
            {
                moduleName = moduleName,
                replacements = replacements,
                oldLength = code.Length,
                newLength = updated.Length
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

            var replaced = ToolResult.Ok("VBA text replaced in " + moduleName + ": " + replacements + " replacement(s).", preview);
            replaced.Verification = CreateVerification(moduleName, updated);
            return replaced;
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
                newLength = updated.Length
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

            var patched = ToolResult.Ok("VBA patch applied to " + moduleName + ".", preview);
            patched.Verification = CreateVerification(moduleName, updated);
            return patched;
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

        private ToolVerification CreateVerification(string moduleName, string code)
        {
            var verification = new ToolVerification
            {
                ToolId = ToolId("vba_read_module"),
                ExpectedCodeSha256 = CodeSha256(code)
            };
            verification.Arguments["moduleName"] = moduleName;
            return verification;
        }

        internal static string CodeSha256(string code)
        {
            using (var sha256 = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(NormalizeCode(code));
                return BitConverter.ToString(sha256.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static string NormalizeCode(string code)
        {
            return (code ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
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
    }
}
