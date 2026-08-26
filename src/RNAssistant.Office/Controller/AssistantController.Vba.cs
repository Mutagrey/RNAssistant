using System;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Tools;

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
            var session = OfficeToolExecutor.CreateIsolatedManualSession(LoadSession(null));
            var command = new ToolCommand { ToolId = _toolExecutor.VbaToolId("vba_read_module") };
            var result = _toolExecutor.Execute(command, new ToolDefinition[0], settings, false, true, session);
            return new VbaProjectResponse
            {
                Result = result,
                Backups = _vbaJournalStore.List(session.Host, session.DocumentKey)
            };
        }

        public ToolResult GetVbaModule(string moduleName)
        {
            const int editorReadLimit = 1000000;
            var settings = _settingsService.Load();
            var session = OfficeToolExecutor.CreateIsolatedManualSession(LoadSession(null));
            var command = new ToolCommand { ToolId = _toolExecutor.VbaToolId("vba_read_module") };
            command.Arguments["moduleName"] = moduleName;
            command.Arguments["maxChars"] = editorReadLimit;
            var result = _toolExecutor.Execute(command, new ToolDefinition[0], settings, false, true, session);
            if (result == null || !result.Success || string.IsNullOrWhiteSpace(result.DataJson))
            {
                return result ?? ToolResult.Fail("VBA module read returned no result.", null, "vba_editor_read_missing", true);
            }

            try
            {
                var data = JObject.Parse(result.DataJson);
                var codeToken = data["code"];
                var hashToken = data["codeSha256"];
                var truncatedToken = data["truncated"];
                if (codeToken == null || codeToken.Type != JTokenType.String ||
                    hashToken == null || hashToken.Type != JTokenType.String ||
                    string.IsNullOrWhiteSpace((string)hashToken) ||
                    truncatedToken == null || truncatedToken.Type != JTokenType.Boolean)
                {
                    return ToolResult.Fail(
                        "VBA editor received an incomplete module payload. The module was not opened for saving.",
                        null,
                        "vba_editor_read_invalid",
                        true);
                }

                var code = (string)codeToken;
                if ((bool)truncatedToken || code.EndsWith("\n...[truncated]", StringComparison.Ordinal))
                {
                    return ToolResult.Fail(
                        "VBA module is larger than the editor's safe read limit and was not opened. Saving a partial module is blocked.",
                        new JObject
                        {
                            ["moduleName"] = (string)data["name"] ?? moduleName,
                            ["lineCount"] = data["lineCount"],
                            ["codeSha256"] = data["codeSha256"],
                            ["maxChars"] = editorReadLimit
                        }.ToString(Formatting.None),
                        "vba_editor_source_truncated",
                        false);
                }
            }
            catch (JsonException ex)
            {
                return ToolResult.Fail("VBA editor received an invalid module payload: " + ex.Message, null, "vba_editor_read_invalid", true);
            }

            return result;
        }

        public VbaMutationQueryResponse GetVbaMutations(VbaMutationQueryPayload request)
        {
            request = request ?? new VbaMutationQueryPayload();
            var session = LoadSession(null);
            var page = _vbaJournalStore.QueryMutations(session.Host, session.DocumentKey, request.ToQueryRequest());
            return new VbaMutationQueryResponse
            {
                Host = session.Host,
                DocumentKey = session.DocumentKey,
                DocumentTitle = session.DocumentTitle,
                View = "vba-mutations",
                TotalEvents = page.TotalEvents,
                TotalRows = page.TotalRows,
                TotalMatches = page.TotalMatches,
                Cursor = page.Cursor,
                NextCursor = page.NextCursor,
                HasMore = page.HasMore,
                Rows = page.Rows.Select(VbaMutationRowDto.From).Where(item => item != null).ToList()
            };
        }

        public VbaMutationDetailResponse GetVbaMutationDetail(string mutationId)
        {
            var session = LoadSession(null);
            return VbaMutationDetailResponse.From(
                _vbaJournalStore.GetMutationDetail(session.Host, session.DocumentKey, mutationId));
        }

        public ToolResult SaveVbaModule(string moduleName, string code, string expectedCodeSha256 = null)
        {
            var settings = _settingsService.Load();
            var tools = _toolCatalog.GetVisibleTools().Where(s => s.Enabled).ToList();
            var command = new ToolCommand { ToolId = _toolExecutor.VbaToolId("vba_write_module") };
            command.Arguments["moduleName"] = moduleName;
            command.Arguments["code"] = code;
            command.Arguments["mode"] = "updateOnly";
            return WithReservedSession(LoadSession(null), session =>
            {
                _toolExecutor.ObserveVbaHash(session, moduleName, expectedCodeSha256);
                var result = _toolExecutor.Execute(command, tools, settings, false, true, session);
                _toolCatalog.InvalidateDocumentVbaTools();
                return result;
            });
        }

        public ToolResult RunVbaMacro(string macroName, CancellationToken cancellationToken)
        {
            return WithReservedSession(LoadSession(null), session =>
                _toolExecutor.RunVbaMacro(macroName, session, cancellationToken));
        }

        public ToolResult CreateVbaModule(string moduleName, string componentType, string code)
        {
            var settings = _settingsService.Load();
            var tools = _toolCatalog.GetVisibleTools().Where(s => s.Enabled).ToList();
            var command = new ToolCommand { ToolId = _toolExecutor.VbaToolId("vba_write_module") };
            command.Arguments["moduleName"] = moduleName;
            command.Arguments["componentType"] = componentType;
            command.Arguments["code"] = code;
            command.Arguments["mode"] = "createOnly";
            return WithReservedSession(LoadSession(null), session =>
            {
                var result = _toolExecutor.Execute(command, tools, settings, false, true, session);
                _toolCatalog.InvalidateDocumentVbaTools();
                return result;
            });
        }

        public ToolResult DeleteVbaModule(string moduleName)
        {
            var settings = _settingsService.Load();
            var tools = _toolCatalog.GetVisibleTools().Where(s => s.Enabled).ToList();
            var command = new ToolCommand { ToolId = _toolExecutor.VbaToolId("vba_delete_module") };
            command.Arguments["moduleName"] = moduleName;
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
            var command = new ToolCommand { ToolId = _toolExecutor.VbaToolId("vba_restore_backup") };
            if (!string.IsNullOrWhiteSpace(backupId)) command.Arguments["backupId"] = backupId;
            if (!string.IsNullOrWhiteSpace(moduleName)) command.Arguments["moduleName"] = moduleName;
            return WithReservedSession(LoadSession(null), session =>
            {
                var result = _toolExecutor.Execute(command, tools, settings, false, true, session);
                _toolCatalog.InvalidateDocumentVbaTools();
                return result;
            });
        }
    }
}
