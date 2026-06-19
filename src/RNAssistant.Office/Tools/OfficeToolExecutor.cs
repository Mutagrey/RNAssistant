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
        private readonly SkillToolExecutor _skillExecutor;

        public OfficeToolExecutor(IOfficeApplicationAdapter adapter, VbaBackupStore vbaBackupStore, SkillStore skillStore)
        {
            _adapter = adapter;
            _pipelineExecutor = new PipelineToolExecutor();
            _vbaExecutor = new VbaToolExecutor(adapter, vbaBackupStore);
            _skillExecutor = new SkillToolExecutor(adapter, skillStore);
        }

        public IEnumerable<ToolDefinition> GetControllerTools()
        {
            return _vbaExecutor.GetControllerTools().Concat(_skillExecutor.GetControllerTools());
        }

        public ToolResult Execute(ToolCommand command, IReadOnlyList<ToolDefinition> skills, AppSettings settings, bool dryRun, bool manualRun)
        {
            return ExecuteCommand(command, skills, settings, 0, dryRun, manualRun);
        }

        public string VbaToolId(string suffix)
        {
            return _vbaExecutor.ToolId(suffix);
        }

        private ToolResult ExecuteCommand(ToolCommand command, IReadOnlyList<ToolDefinition> skills, AppSettings settings, int depth, bool dryRun, bool manualRun)
        {
            settings = settings ?? new AppSettings();
            if (command == null || string.IsNullOrWhiteSpace(command.ToolId))
            {
                return ToolResult.Fail("Tool command is empty.");
            }

            if (depth > 8)
            {
                return ToolResult.Fail("Pipeline nesting limit exceeded.");
            }

            var tool = FindEnabledTool(skills, command.ToolId) ??
                FindEnabledTool(_adapter.GetBuiltInTools(), command.ToolId);
            if (tool == null)
            {
                tool = _vbaExecutor.GetControllerTool(command.ToolId) ?? _skillExecutor.GetControllerTool(command.ToolId);
            }
            var customTool = tool != null && !tool.BuiltIn ? tool : null;

            if (ToolSafetyPolicy.RequiresConfirmation(tool, settings, dryRun, manualRun))
            {
                return ToolResult.WaitingConfirmation("Tool requires confirmation before execution: " + command.ToolId);
            }

            if (customTool != null && customTool.RequiresConfirmation && !settings.AutoConfirmToolActions && !dryRun && !manualRun)
            {
                return ToolResult.WaitingConfirmation("Tool requires confirmation before execution: " + customTool.Id);
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
                return ToolResult.Fail("Tool executor is not runnable yet: " + customTool.Executor);
            }

            if (_vbaExecutor.IsControllerTool(command.ToolId))
            {
                return _vbaExecutor.ExecuteControllerTool(command, skills, settings, dryRun, manualRun, ExecuteCommand);
            }

            if (_skillExecutor.IsControllerTool(command.ToolId))
            {
                return _skillExecutor.ExecuteControllerTool(command, settings, dryRun, manualRun);
            }

            if (dryRun)
            {
                return ToolResult.Ok("Dry run: would execute " + command.ToolId, JsonConvert.SerializeObject(command.Arguments));
            }

            if (string.Equals(command.ToolId, VbaToolId("vba_replace_module"), StringComparison.OrdinalIgnoreCase))
            {
                _vbaExecutor.BackupModuleBeforeReplace(command, settings);
            }

            return _adapter.ExecuteTool(command);
        }

        private static ToolDefinition FindEnabledTool(IEnumerable<ToolDefinition> tools, string toolId)
        {
            return (tools ?? new ToolDefinition[0]).FirstOrDefault(s =>
                s.Enabled &&
                string.Equals(s.Id, toolId, StringComparison.OrdinalIgnoreCase));
        }
    }
}
