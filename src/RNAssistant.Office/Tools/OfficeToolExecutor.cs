using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Tools
{
    public sealed class OfficeToolExecutor
    {
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly PipelineToolExecutor _pipelineExecutor;
        private readonly VbaToolExecutor _vbaExecutor;

        public OfficeToolExecutor(IOfficeApplicationAdapter adapter, VbaBackupStore vbaBackupStore)
        {
            _adapter = adapter;
            _pipelineExecutor = new PipelineToolExecutor();
            _vbaExecutor = new VbaToolExecutor(adapter, vbaBackupStore);
        }

        public IEnumerable<SkillDefinition> GetControllerTools()
        {
            return _vbaExecutor.GetControllerTools();
        }

        public SkillResult Execute(SkillCommand command, IReadOnlyList<SkillDefinition> skills, AppSettings settings, bool dryRun, bool manualRun)
        {
            return ExecuteCommand(command, skills, settings, 0, dryRun, manualRun);
        }

        public string VbaToolId(string suffix)
        {
            return _vbaExecutor.ToolId(suffix);
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
                return _pipelineExecutor.Execute(tool, command, skills, settings, depth + 1, dryRun, manualRun, ExecuteCommand);
            }

            if (tool != null && string.Equals(tool.Executor, "vba", StringComparison.OrdinalIgnoreCase))
            {
                return _vbaExecutor.ExecuteCustomTool(tool, command, settings, dryRun, manualRun);
            }

            if (tool != null)
            {
                return SkillResult.Fail("Tool executor is not runnable yet: " + tool.Executor);
            }

            if (_vbaExecutor.IsControllerTool(command.SkillId))
            {
                return _vbaExecutor.ExecuteControllerTool(command, skills, settings, dryRun, manualRun, ExecuteCommand);
            }

            if (dryRun)
            {
                return SkillResult.Ok("Dry run: would execute " + command.SkillId, JsonConvert.SerializeObject(command.Arguments));
            }

            if (string.Equals(command.SkillId, VbaToolId("vba_replace_module"), StringComparison.OrdinalIgnoreCase))
            {
                _vbaExecutor.BackupModuleBeforeReplace(command, settings);
            }

            return _adapter.ExecuteSkill(command);
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
    }
}
