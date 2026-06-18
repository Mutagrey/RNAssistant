using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Skills;

namespace RNAssistant.Office.Tools
{
    public sealed class OfficeToolExecutor
    {
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly VbaBackupStore _vbaBackupStore;

        public OfficeToolExecutor(IOfficeApplicationAdapter adapter, VbaBackupStore vbaBackupStore)
        {
            _adapter = adapter;
            _vbaBackupStore = vbaBackupStore;
        }

        public IEnumerable<SkillDefinition> GetControllerTools()
        {
            if (!HostSupportsVba())
            {
                yield break;
            }

            yield return ControllerTool(VbaToolId("vba_list_backups"), "List RNAssistant VBA rollback backups for the current document.", "{}");
            yield return ControllerTool(VbaToolId("vba_restore_backup"), "Restore a VBA module from a prior RNAssistant backup by backupId, or latest backup for moduleName.", "{\"backupId\":\"optional\",\"moduleName\":\"Module1\"}");
            yield return ControllerTool(VbaToolId("vba_replace_text"), "Replace an exact text fragment inside one VBA module; safer than replacing the whole module and creates a rollback backup.", "{\"moduleName\":\"Module1\",\"find\":\"old code\",\"replace\":\"new code\"}");
            yield return ControllerTool(VbaToolId("vba_apply_patch"), "Apply structured VBA code patches: replace exact text, insert before/after exact text, or replace line ranges; creates rollback backup.", "{\"moduleName\":\"Module1\",\"patch\":[{\"op\":\"replace\",\"find\":\"old\",\"text\":\"new\"},{\"op\":\"replaceLines\",\"startLine\":10,\"deleteCount\":2,\"text\":\"new code\"}]}");
        }

        public SkillResult Execute(SkillCommand command, IReadOnlyList<SkillDefinition> skills, AppSettings settings, bool dryRun, bool manualRun)
        {
            return ExecuteCommand(command, skills, settings, 0, dryRun, manualRun);
        }

        public string VbaToolId(string suffix)
        {
            return HostToolPrefix() + "." + suffix;
        }

        private SkillDefinition ControllerTool(string id, string description, string schema)
        {
            return new SkillDefinition
            {
                Id = id,
                Host = _adapter.HostName,
                Name = id,
                Description = description,
                ArgumentSchemaJson = schema,
                BuiltIn = true,
                Enabled = true
            };
        }

        private SkillResult ExecuteCommand(SkillCommand command, IReadOnlyList<SkillDefinition> skills, AppSettings settings, int depth, bool dryRun, bool manualRun)
        {
            if (command == null || string.IsNullOrWhiteSpace(command.SkillId))
            {
                return SkillResult.Fail("Tool command is empty.");
            }

            if (depth > 8)
            {
                return SkillResult.Fail("Pipeline nesting limit exceeded.");
            }

            var tool = skills.FirstOrDefault(s =>
                !s.BuiltIn &&
                s.Enabled &&
                string.Equals(s.Id, command.SkillId, StringComparison.OrdinalIgnoreCase));

            if (IsMutationTool(command.SkillId) && !settings.AutoConfirmToolActions && !manualRun && !dryRun &&
                !CanAgentRunBuiltInMutation(command.SkillId, tool, settings))
            {
                return SkillResult.Fail("Tool requires confirmation before execution: " + command.SkillId);
            }

            if (tool != null && tool.RequiresConfirmation && !settings.AutoConfirmToolActions && !manualRun)
            {
                return SkillResult.Fail("Tool requires confirmation before execution: " + tool.Id);
            }

            if (tool != null && string.Equals(tool.Executor, "pipeline", StringComparison.OrdinalIgnoreCase))
            {
                return ExecutePipeline(tool, command, skills, settings, depth + 1, dryRun, manualRun);
            }

            if (tool != null && string.Equals(tool.Executor, "vba", StringComparison.OrdinalIgnoreCase))
            {
                return ExecuteVbaTool(tool, command, skills, settings, dryRun, manualRun);
            }

            if (tool != null)
            {
                return SkillResult.Fail("Tool executor is not runnable yet: " + tool.Executor);
            }

            if (string.Equals(command.SkillId, VbaToolId("vba_list_backups"), StringComparison.OrdinalIgnoreCase))
            {
                return SkillResult.Ok("VBA backups listed.", JsonConvert.SerializeObject(_vbaBackupStore.List(_adapter.HostName, _adapter.DocumentKey)));
            }

            if (string.Equals(command.SkillId, VbaToolId("vba_restore_backup"), StringComparison.OrdinalIgnoreCase))
            {
                return RestoreVbaBackup(command, skills, settings, dryRun, manualRun);
            }

            if (string.Equals(command.SkillId, VbaToolId("vba_replace_text"), StringComparison.OrdinalIgnoreCase))
            {
                return ReplaceVbaText(command, skills, settings, dryRun, manualRun);
            }

            if (string.Equals(command.SkillId, VbaToolId("vba_apply_patch"), StringComparison.OrdinalIgnoreCase))
            {
                return ApplyVbaPatch(command, skills, settings, dryRun, manualRun);
            }

            if (dryRun)
            {
                return SkillResult.Ok("Dry run: would execute " + command.SkillId, JsonConvert.SerializeObject(command.Arguments));
            }

            if (string.Equals(command.SkillId, VbaToolId("vba_replace_module"), StringComparison.OrdinalIgnoreCase))
            {
                BackupVbaModuleBeforeReplace(command, settings);
            }

            return _adapter.ExecuteSkill(command);
        }

        private SkillResult ExecutePipeline(SkillDefinition tool, SkillCommand command, IReadOnlyList<SkillDefinition> skills, AppSettings settings, int depth, bool dryRun, bool manualRun)
        {
            if (string.IsNullOrWhiteSpace(tool.PipelineJson))
            {
                return SkillResult.Fail("Tool has no pipeline: " + tool.Id);
            }

            JObject pipeline;
            try
            {
                pipeline = JObject.Parse(tool.PipelineJson);
            }
            catch (JsonException ex)
            {
                return SkillResult.Fail("Invalid pipeline JSON for " + tool.Id + ": " + ex.Message);
            }

            var steps = pipeline["steps"] as JArray;
            if (steps == null || steps.Count == 0)
            {
                return SkillResult.Fail("Pipeline has no steps: " + tool.Id);
            }

            var stepResults = new Dictionary<string, SkillResult>(StringComparer.OrdinalIgnoreCase);
            var output = new List<object>();
            foreach (var stepToken in steps)
            {
                var step = stepToken as JObject;
                if (step == null)
                {
                    continue;
                }

                var toolId = (string)(step["toolId"] ?? step["skillId"] ?? step["id"]);
                if (string.IsNullOrWhiteSpace(toolId))
                {
                    return SkillResult.Fail("Pipeline step has no toolId.");
                }

                var stepId = (string)step["id"];
                if (string.IsNullOrWhiteSpace(stepId))
                {
                    stepId = toolId;
                }

                var nested = new SkillCommand { SkillId = toolId };
                var args = step["arguments"] as JObject;
                if (args != null)
                {
                    foreach (var property in args.Properties())
                    {
                        nested.Arguments[property.Name] = ResolvePipelineValue(property.Value, command.Arguments, stepResults);
                    }
                }

                var result = ExecuteCommand(nested, skills, settings, depth + 1, dryRun, manualRun);
                stepResults[stepId] = result;
                output.Add(new { id = stepId, toolId = toolId, success = result.Success, message = result.Message, dataJson = result.DataJson });

                if (!result.Success)
                {
                    return SkillResult.Fail("Pipeline step failed: " + stepId + ". " + result.Message);
                }
            }

            return SkillResult.Ok((dryRun ? "Dry run completed: " : "Pipeline executed: ") + tool.Id, JsonConvert.SerializeObject(new { toolId = tool.Id, dryRun = dryRun, steps = output }));
        }

        private SkillResult ExecuteVbaTool(SkillDefinition tool, SkillCommand command, IReadOnlyList<SkillDefinition> skills, AppSettings settings, bool dryRun, bool manualRun)
        {
            if (string.IsNullOrWhiteSpace(tool.Code))
            {
                return SkillResult.Fail("VBA tool has no code: " + tool.Id);
            }
            if (!dryRun && !manualRun && !settings.AutoConfirmToolActions)
            {
                return SkillResult.Fail("VBA tool requires confirmation before execution: " + tool.Id);
            }

            var moduleName = SkillArgumentReader.String(command.Arguments, "moduleName", ToolModuleName(tool.Id));
            var macroName = SkillArgumentReader.String(command.Arguments, "macroName", string.Empty);
            if (dryRun)
            {
                return SkillResult.Ok("Dry run: would insert VBA module " + moduleName + (string.IsNullOrWhiteSpace(macroName) ? string.Empty : " and run " + macroName), JsonConvert.SerializeObject(new { moduleName = moduleName, macroName = macroName, code = tool.Code }));
            }

            var insert = new SkillCommand { SkillId = VbaToolId("insert_vba_module") };
            insert.Arguments["moduleName"] = moduleName;
            insert.Arguments["code"] = tool.Code;
            var insertResult = _adapter.ExecuteSkill(insert);
            if (!insertResult.Success ||
                string.IsNullOrWhiteSpace(macroName) ||
                (insertResult.Message ?? string.Empty).StartsWith("VBA insert was blocked", StringComparison.OrdinalIgnoreCase))
            {
                return insertResult;
            }

            var run = new SkillCommand { SkillId = VbaToolId("run_macro") };
            run.Arguments["macroName"] = macroName;
            var runResult = _adapter.ExecuteSkill(run);
            return SkillResult.Ok("VBA tool executed: " + tool.Id, JsonConvert.SerializeObject(new { insert = insertResult, run = runResult }));
        }

        private void BackupVbaModuleBeforeReplace(SkillCommand command, AppSettings settings)
        {
            var moduleName = SkillArgumentReader.String(command.Arguments, "moduleName", string.Empty);
            if (string.IsNullOrWhiteSpace(moduleName) || SkillArgumentReader.String(command.Arguments, "skipBackup", "false").Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var read = new SkillCommand { SkillId = VbaToolId("vba_read_module") };
            read.Arguments["moduleName"] = moduleName;
            read.Arguments["maxChars"] = Math.Max(settings.VbaContextCharLimit, 30000);
            var existing = _adapter.ExecuteSkill(read);
            if (!existing.Success || string.IsNullOrWhiteSpace(existing.DataJson))
            {
                return;
            }

            try
            {
                var data = JObject.Parse(existing.DataJson);
                var code = (string)data["code"];
                var componentType = (string)data["type"];
                if (code != null)
                {
                    _vbaBackupStore.Save(_adapter.HostName, _adapter.DocumentKey, _adapter.DocumentTitle, moduleName, componentType, code);
                }
            }
            catch (JsonException)
            {
            }
        }

        private SkillResult RestoreVbaBackup(SkillCommand command, IReadOnlyList<SkillDefinition> tools, AppSettings settings, bool dryRun, bool manualRun)
        {
            var backupId = SkillArgumentReader.String(command.Arguments, "backupId", string.Empty);
            var moduleName = SkillArgumentReader.String(command.Arguments, "moduleName", string.Empty);
            var backup = _vbaBackupStore.Find(_adapter.HostName, _adapter.DocumentKey, backupId, moduleName);
            if (backup == null)
            {
                return SkillResult.Fail("VBA backup not found.");
            }

            if (dryRun)
            {
                return SkillResult.Ok("Dry run: would restore VBA backup " + backup.BackupId, JsonConvert.SerializeObject(backup));
            }

            var replace = new SkillCommand { SkillId = VbaToolId("vba_replace_module") };
            replace.Arguments["moduleName"] = backup.ModuleName;
            replace.Arguments["code"] = backup.Code;
            replace.Arguments["createIfMissing"] = "true";
            var result = ExecuteCommand(replace, tools, settings, 0, false, manualRun);
            return result.Success
                ? SkillResult.Ok("VBA backup restored: " + backup.BackupId, JsonConvert.SerializeObject(new { backup = backup, restore = result }))
                : result;
        }

        private SkillResult ReplaceVbaText(SkillCommand command, IReadOnlyList<SkillDefinition> tools, AppSettings settings, bool dryRun, bool manualRun)
        {
            var moduleName = SkillArgumentReader.String(command.Arguments, "moduleName", string.Empty);
            var find = SkillArgumentReader.String(command.Arguments, "find", string.Empty);
            var replace = SkillArgumentReader.String(command.Arguments, "replace", string.Empty);
            if (string.IsNullOrWhiteSpace(moduleName) || string.IsNullOrEmpty(find))
            {
                return SkillResult.Fail("moduleName and find are required.");
            }

            string code;
            SkillResult error;
            if (!TryReadVbaModuleCode(moduleName, out code, out error))
            {
                return error;
            }

            var replacements = CountOccurrences(code, find);
            if (replacements == 0)
            {
                return SkillResult.Fail("Text fragment was not found in VBA module: " + moduleName);
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
                return SkillResult.Ok("Dry run: would patch VBA module " + moduleName + " (" + replacements + " replacement(s)).", preview);
            }

            var write = new SkillCommand { SkillId = VbaToolId("vba_replace_module") };
            write.Arguments["moduleName"] = moduleName;
            write.Arguments["code"] = updated;
            write.Arguments["createIfMissing"] = "true";
            var result = ExecuteCommand(write, tools, settings, 0, false, manualRun);
            return result.Success
                ? SkillResult.Ok("VBA text replaced in " + moduleName + ": " + replacements + " replacement(s).", preview)
                : result;
        }

        private SkillResult ApplyVbaPatch(SkillCommand command, IReadOnlyList<SkillDefinition> tools, AppSettings settings, bool dryRun, bool manualRun)
        {
            var moduleName = SkillArgumentReader.String(command.Arguments, "moduleName", string.Empty);
            if (string.IsNullOrWhiteSpace(moduleName))
            {
                return SkillResult.Fail("moduleName is required.");
            }

            JArray operations;
            try
            {
                operations = ParsePatchOperations(SkillArgumentReader.String(command.Arguments, "patch", string.Empty));
            }
            catch (JsonException ex)
            {
                return SkillResult.Fail("Invalid patch JSON: " + ex.Message);
            }

            if (operations.Count == 0)
            {
                return SkillResult.Fail("Patch has no operations.");
            }

            string code;
            SkillResult error;
            if (!TryReadVbaModuleCode(moduleName, out code, out error))
            {
                return error;
            }

            var updated = code;
            var summary = new List<object>();
            foreach (JObject operation in operations.OfType<JObject>())
            {
                var result = ApplyPatchOperation(updated, operation, out updated);
                if (!result.Success)
                {
                    return result;
                }

                summary.Add(new { op = (string)(operation["op"] ?? operation["type"]), message = result.Message });
            }
            if (summary.Count != operations.Count)
            {
                return SkillResult.Fail("Each patch operation must be a JSON object.");
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
                return SkillResult.Ok("Dry run: would apply VBA patch to " + moduleName + ".", preview);
            }

            var write = new SkillCommand { SkillId = VbaToolId("vba_replace_module") };
            write.Arguments["moduleName"] = moduleName;
            write.Arguments["code"] = updated;
            write.Arguments["createIfMissing"] = "true";
            var writeResult = ExecuteCommand(write, tools, settings, 0, false, manualRun);
            return writeResult.Success
                ? SkillResult.Ok("VBA patch applied to " + moduleName + ".", preview)
                : writeResult;
        }

        private bool TryReadVbaModuleCode(string moduleName, out string code, out SkillResult error)
        {
            code = string.Empty;
            error = null;
            var read = new SkillCommand { SkillId = VbaToolId("vba_read_module") };
            read.Arguments["moduleName"] = moduleName;
            read.Arguments["maxChars"] = 1000000;
            var current = _adapter.ExecuteSkill(read);
            if (!current.Success || string.IsNullOrWhiteSpace(current.DataJson))
            {
                error = current.Success ? SkillResult.Fail("VBA module returned no code.") : current;
                return false;
            }

            try
            {
                code = (string)JObject.Parse(current.DataJson)["code"] ?? string.Empty;
            }
            catch (JsonException ex)
            {
                error = SkillResult.Fail("Could not parse VBA module data: " + ex.Message);
                return false;
            }

            if (code.EndsWith("\n...[truncated]", StringComparison.Ordinal))
            {
                error = SkillResult.Fail("VBA module is too large for a safe patch.");
                return false;
            }

            return true;
        }

        private static object ResolvePipelineValue(JToken token, IDictionary<string, object> inputArgs, IDictionary<string, SkillResult> stepResults)
        {
            var value = token.Type == JTokenType.String
                ? token.Value<string>()
                : token.ToString(Formatting.None);

            return ReplacePlaceholders(value, inputArgs, stepResults);
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

        private static SkillResult ApplyPatchOperation(string current, JObject operation, out string updated)
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
                        return SkillResult.Fail("Patch replace requires find.");
                    }

                    var count = CountOccurrences(current, find);
                    if (count == 0)
                    {
                        return SkillResult.Fail("Patch find text was not found.");
                    }

                    updated = current.Replace(find, text);
                    return SkillResult.Ok("Replaced " + count + " occurrence(s).");
                case "replacefirst":
                    return ReplaceAtMatch(current, find, text, out updated);
                case "insertbefore":
                    return ReplaceAtMatch(current, find, text + find, out updated);
                case "insertafter":
                    return ReplaceAtMatch(current, find, find + text, out updated);
                case "replacelines":
                    return ReplaceLines(current, operation, text, out updated);
                default:
                    return SkillResult.Fail("Unsupported patch op: " + op);
            }
        }

        private static SkillResult ReplaceAtMatch(string current, string find, string replacement, out string updated)
        {
            updated = current;
            if (string.IsNullOrEmpty(find))
            {
                return SkillResult.Fail("Patch operation requires find.");
            }

            var index = current.IndexOf(find, StringComparison.Ordinal);
            if (index < 0)
            {
                return SkillResult.Fail("Patch find text was not found.");
            }

            updated = current.Substring(0, index) + replacement + current.Substring(index + find.Length);
            return SkillResult.Ok("Patched first occurrence.");
        }

        private static SkillResult ReplaceLines(string current, JObject operation, string text, out string updated)
        {
            updated = current;
            var startLine = (int?)operation["startLine"] ?? 0;
            var deleteCount = (int?)operation["deleteCount"] ?? 0;
            if (startLine <= 0 || deleteCount < 0)
            {
                return SkillResult.Fail("replaceLines requires startLine >= 1 and deleteCount >= 0.");
            }

            var newline = current.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            var lines = current.Replace("\r\n", "\n").Split('\n').ToList();
            var index = startLine - 1;
            if (index > lines.Count)
            {
                return SkillResult.Fail("replaceLines startLine is outside the module.");
            }

            var remove = Math.Min(deleteCount, lines.Count - index);
            if (remove > 0)
            {
                lines.RemoveRange(index, remove);
            }

            if (!string.IsNullOrEmpty(text))
            {
                lines.InsertRange(index, text.Replace("\r\n", "\n").Split('\n'));
            }

            updated = string.Join(newline, lines.ToArray());
            return SkillResult.Ok("Replaced lines at " + startLine + " deleting " + deleteCount + ".");
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

        private static bool IsMutationTool(string toolId)
        {
            return EndsWithTool(toolId, ".write_range") ||
                EndsWithTool(toolId, ".write_table") ||
                EndsWithTool(toolId, ".add_chart") ||
                EndsWithTool(toolId, ".add_sheet") ||
                EndsWithTool(toolId, ".insert_text") ||
                EndsWithTool(toolId, ".replace_selection") ||
                EndsWithTool(toolId, ".add_comment") ||
                EndsWithTool(toolId, ".add_slide") ||
                EndsWithTool(toolId, ".replace_selection_text") ||
                EndsWithTool(toolId, ".draft_reply") ||
                EndsWithTool(toolId, ".vba_replace_module") ||
                EndsWithTool(toolId, ".vba_replace_text") ||
                EndsWithTool(toolId, ".vba_apply_patch") ||
                EndsWithTool(toolId, ".vba_restore_backup") ||
                EndsWithTool(toolId, ".insert_vba_module") ||
                EndsWithTool(toolId, ".run_macro");
        }

        private static bool CanAgentRunBuiltInMutation(string toolId, SkillDefinition customTool, AppSettings settings)
        {
            return settings != null &&
                settings.AgentModeEnabled != false &&
                customTool == null &&
                !IsVbaMutationTool(toolId);
        }

        private static bool IsVbaMutationTool(string toolId)
        {
            return EndsWithTool(toolId, ".vba_replace_module") ||
                EndsWithTool(toolId, ".vba_replace_text") ||
                EndsWithTool(toolId, ".vba_apply_patch") ||
                EndsWithTool(toolId, ".vba_restore_backup") ||
                EndsWithTool(toolId, ".insert_vba_module") ||
                EndsWithTool(toolId, ".run_macro");
        }

        private static bool EndsWithTool(string toolId, string suffix)
        {
            return (toolId ?? string.Empty).EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
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

        private static string ReplacePlaceholders(string value, IDictionary<string, object> inputArgs, IDictionary<string, SkillResult> stepResults)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            return Regex.Replace(value, "\\{\\{\\s*([^}]+)\\s*\\}\\}", match =>
            {
                var key = match.Groups[1].Value.Trim();
                if (key.StartsWith("args.", StringComparison.OrdinalIgnoreCase))
                {
                    object arg;
                    return inputArgs != null && inputArgs.TryGetValue(key.Substring(5), out arg) && arg != null
                        ? Convert.ToString(arg)
                        : string.Empty;
                }

                if (key.StartsWith("steps.", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = key.Split('.');
                    SkillResult step;
                    if (parts.Length >= 3 && stepResults != null && stepResults.TryGetValue(parts[1], out step))
                    {
                        if (string.Equals(parts[2], "message", StringComparison.OrdinalIgnoreCase))
                        {
                            return step.Message ?? string.Empty;
                        }

                        if (string.Equals(parts[2], "dataJson", StringComparison.OrdinalIgnoreCase))
                        {
                            return step.DataJson ?? string.Empty;
                        }

                        if (string.Equals(parts[2], "success", StringComparison.OrdinalIgnoreCase))
                        {
                            return step.Success ? "true" : "false";
                        }
                    }
                }

                return match.Value;
            });
        }
    }
}
