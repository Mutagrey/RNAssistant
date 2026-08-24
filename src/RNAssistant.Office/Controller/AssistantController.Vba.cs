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
            var result = WithReservedSession(LoadSession(null), session =>
            {
                return _toolExecutor.InstallVbaTool(tool, dryRun, session);
            });
            if (!dryRun) _toolCatalog.InvalidateDocumentVbaTools();
            return new VbaToolPackageResponse { Result = result, Tools = GetTools() };
        }

        public VbaToolPackageResponse UninstallVbaTool(string id)
        {
            var tool = _toolStore.Load().FirstOrDefault(item => item != null &&
                string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Executor, "vba", StringComparison.OrdinalIgnoreCase));
            if (tool == null) throw new InvalidOperationException("Global VBA tool not found: " + id);
            var result = WithReservedSession(LoadSession(null), session =>
            {
                return _toolExecutor.RemoveVbaTool(tool, session);
            });
            _toolCatalog.InvalidateDocumentVbaTools();
            return new VbaToolPackageResponse { Result = result, Tools = GetTools() };
        }

        public VbaProjectResponse GetVbaProject()
        {
            var settings = _settingsService.Load();
            var session = LoadSession(null);
            var command = new ToolCommand { ToolId = _toolExecutor.VbaToolId("vba_list_modules") };
            var result = _toolExecutor.Execute(command, new ToolDefinition[0], settings, false, true, session);
            return new VbaProjectResponse
            {
                Result = result,
                Backups = _vbaBackupStore.List(session.Host, session.DocumentKey)
            };
        }

        public ToolResult GetVbaModule(string moduleName)
        {
            var settings = _settingsService.Load();
            var session = LoadSession(null);
            var command = new ToolCommand { ToolId = _toolExecutor.VbaToolId("vba_read_module") };
            command.Arguments["moduleName"] = moduleName;
            command.Arguments["maxChars"] = 1000000;
            return _toolExecutor.Execute(command, new ToolDefinition[0], settings, false, true, session);
        }

        public ToolResult SaveVbaModule(string moduleName, string code)
        {
            var settings = _settingsService.Load();
            var tools = _toolCatalog.GetVisibleTools().Where(s => s.Enabled).ToList();
            var command = new ToolCommand { ToolId = _toolExecutor.VbaToolId("vba_replace_module") };
            command.Arguments["moduleName"] = moduleName;
            command.Arguments["code"] = code;
            command.Arguments["createIfMissing"] = "false";
            return WithReservedSession(LoadSession(null), session =>
            {
                var result = _toolExecutor.Execute(command, tools, settings, false, true, session);
                _toolCatalog.InvalidateDocumentVbaTools();
                return result;
            });
        }

        public ToolResult CreateVbaModule(string moduleName, string componentType, string code)
        {
            var settings = _settingsService.Load();
            var tools = _toolCatalog.GetVisibleTools().Where(s => s.Enabled).ToList();
            var command = new ToolCommand { ToolId = _toolExecutor.VbaToolId("vba_create_module") };
            command.Arguments["moduleName"] = moduleName;
            command.Arguments["componentType"] = componentType;
            command.Arguments["code"] = code;
            return WithReservedSession(LoadSession(null), session =>
            {
                var result = _toolExecutor.Execute(command, tools, settings, false, true, session);
                _toolCatalog.InvalidateDocumentVbaTools();
                return result;
            });
        }

        public ToolResult DeleteVbaModule(string moduleName, string expectedCodeSha256)
        {
            var settings = _settingsService.Load();
            var tools = _toolCatalog.GetVisibleTools().Where(s => s.Enabled).ToList();
            var command = new ToolCommand { ToolId = _toolExecutor.VbaToolId("vba_delete_module") };
            command.Arguments["moduleName"] = moduleName;
            command.Arguments["expectedCodeSha256"] = expectedCodeSha256;
            return WithReservedSession(LoadSession(null), session =>
            {
                var result = _toolExecutor.Execute(command, tools, settings, false, true, session);
                _toolCatalog.InvalidateDocumentVbaTools();
                return result;
            });
        }

        public ToolResult RestoreVbaBackup(string backupId, string moduleName)
        {
            var settings = _settingsService.Load();
            var tools = _toolCatalog.GetVisibleTools().Where(s => s.Enabled).ToList();
            return WithReservedSession(LoadSession(null), session =>
            {
                var result = _toolExecutor.Execute(new ToolCommand
                {
                    ToolId = _toolExecutor.VbaToolId("vba_restore_backup"),
                    Arguments =
                    {
                        ["backupId"] = backupId ?? string.Empty,
                        ["moduleName"] = moduleName ?? string.Empty
                    }
                }, tools, settings, false, true, session);
                _toolCatalog.InvalidateDocumentVbaTools();
                return result;
            });
        }
    }
}
