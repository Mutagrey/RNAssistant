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
            settings = settings ?? new AppSettings();
            if (command == null || string.IsNullOrWhiteSpace(command.SkillId))
            {
                return SkillResult.Fail("Tool command is empty.");
            }

            if (depth > 8)
            {
                return SkillResult.Fail("Pipeline nesting limit exceeded.");
            }

            var tool = FindEnabledTool(skills, command.SkillId) ??
                FindEnabledTool(_adapter.GetBuiltInSkills(), command.SkillId);
            if (tool == null)
            {
                tool = _vbaExecutor.GetControllerTool(command.SkillId);
            }
            var customTool = tool != null && !tool.BuiltIn ? tool : null;

            if (ToolSafetyPolicy.RequiresConfirmation(tool, settings, dryRun, manualRun))
            {
                return SkillResult.Fail("Tool requires confirmation before execution: " + command.SkillId);
            }

            if (customTool != null && customTool.RequiresConfirmation && !settings.AutoConfirmToolActions && !manualRun)
            {
                return SkillResult.Fail("Tool requires confirmation before execution: " + customTool.Id);
            }

            if (customTool != null && string.Equals(customTool.Executor, "pipeline", StringComparison.OrdinalIgnoreCase))
            {
                return _pipelineExecutor.Execute(customTool, command, skills, settings, depth + 1, dryRun, manualRun, ExecuteCommand);
            }

            if (customTool != null && string.Equals(customTool.Executor, "vba", StringComparison.OrdinalIgnoreCase))
            {
                return _vbaExecutor.ExecuteCustomTool(customTool, command, settings, dryRun, manualRun);
            }

            if (customTool != null)
            {
                return SkillResult.Fail("Tool executor is not runnable yet: " + customTool.Executor);
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

        private static SkillDefinition FindEnabledTool(IEnumerable<SkillDefinition> tools, string toolId)
        {
            return (tools ?? new SkillDefinition[0]).FirstOrDefault(s =>
                s.Enabled &&
                string.Equals(s.Id, toolId, StringComparison.OrdinalIgnoreCase));
        }
    }
}
