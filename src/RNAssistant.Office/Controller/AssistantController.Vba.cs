using System;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        public VbaToolPackageResponse InstallVbaTool(string id, bool dryRun)
        {
            var tool = _toolStore.Load().FirstOrDefault(item => item != null &&
                string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Executor, "vba", StringComparison.OrdinalIgnoreCase));
            if (tool == null) throw new InvalidOperationException("Global VBA tool not found: " + id);
            var result = _toolExecutor.InstallVbaTool(tool, dryRun);
            return new VbaToolPackageResponse { Result = result, Tools = GetTools() };
        }

        public VbaToolPackageResponse UninstallVbaTool(string id)
        {
            var tool = _toolStore.Load().FirstOrDefault(item => item != null &&
                string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Executor, "vba", StringComparison.OrdinalIgnoreCase));
            if (tool == null) throw new InvalidOperationException("Global VBA tool not found: " + id);
            var result = _toolExecutor.RemoveVbaTool(tool);
            return new VbaToolPackageResponse { Result = result, Tools = GetTools() };
        }

        public VbaProjectResponse GetVbaProject()
        {
            var settings = _settingsService.Load();
            var command = new ToolCommand { ToolId = _toolExecutor.VbaToolId("vba_list_modules") };
            var result = _toolExecutor.Execute(command, new ToolDefinition[0], settings, false, true);
            return new VbaProjectResponse
            {
                Result = result,
                Backups = _vbaBackupStore.List(_adapter.HostName, _adapter.DocumentKey)
            };
        }

        public ToolResult GetVbaModule(string moduleName)
        {
            var settings = _settingsService.Load();
            var command = new ToolCommand { ToolId = _toolExecutor.VbaToolId("vba_read_module") };
            command.Arguments["moduleName"] = moduleName;
            command.Arguments["maxChars"] = 1000000;
            return _toolExecutor.Execute(command, new ToolDefinition[0], settings, false, true);
        }

        public ToolResult SaveVbaModule(string moduleName, string code)
        {
            var settings = _settingsService.Load();
            var tools = _toolCatalog.GetVisibleTools().Where(s => s.Enabled).ToList();
            var command = new ToolCommand { ToolId = _toolExecutor.VbaToolId("vba_replace_module") };
            command.Arguments["moduleName"] = moduleName;
            command.Arguments["code"] = code;
            command.Arguments["createIfMissing"] = "true";
            return _toolExecutor.Execute(command, tools, settings, false, true);
        }

        public ToolResult RestoreVbaBackup(string backupId, string moduleName)
        {
            var settings = _settingsService.Load();
            var tools = _toolCatalog.GetVisibleTools().Where(s => s.Enabled).ToList();
            return _toolExecutor.Execute(new ToolCommand
            {
                ToolId = _toolExecutor.VbaToolId("vba_restore_backup"),
                Arguments =
                {
                    ["backupId"] = backupId ?? string.Empty,
                    ["moduleName"] = moduleName ?? string.Empty
                }
            }, tools, settings, false, true);
        }
    }
}
