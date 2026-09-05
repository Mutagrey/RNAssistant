using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
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
            var source = ToolPackageSource.Capture(tool);
            var result = WithReservedSession(LoadSession(null), session =>
            {
                return _toolExecutor.InstallVbaTool(source, dryRun, session);
            });
            if (!dryRun) _toolCatalog.InvalidateDocumentVbaTools();
            return new VbaToolPackageResponse
            {
                Result = VbaPackageResultDto.From(result),
                Tools = GetTools()
            };
        }

        public VbaToolPackageResponse UninstallVbaTool(string id)
        {
            var tool = _toolStore.Load().FirstOrDefault(item => item != null &&
                string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Executor, "vba", StringComparison.OrdinalIgnoreCase));
            if (tool == null) throw new InvalidOperationException("Global VBA tool not found: " + id);
            var source = ToolPackageSource.Capture(tool);
            var result = WithReservedSession(LoadSession(null), session =>
            {
                return _toolExecutor.RemoveVbaTool(source, session);
            });
            _toolCatalog.InvalidateDocumentVbaTools();
            return new VbaToolPackageResponse
            {
                Result = VbaPackageResultDto.From(result),
                Tools = GetTools()
            };
        }

        public VbaProjectResponse GetVbaProject()
        {
            var session = OfficeToolExecutor.CreateIsolatedManualSession(LoadSession(null));
            var result = _toolExecutor.ReadVbaProjectForEditor(session);
            return new VbaProjectResponse
            {
                Result = result,
                Backups = _vbaJournalStore.List(session.Host, session.DocumentKey)
            };
        }

        public Task<VbaEditorReadResponse> GetVbaModuleAsync(VbaEditorReadRequest request, CancellationToken token)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ChatId))
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: an explicit chat is required.");
            var session = LoadAddressedSession(request.ChatId);
            var source = new ChatSession { Id = session.Id, Host = session.Host, DocumentKey = session.DocumentKey,
                DocumentPath = session.DocumentPath, DocumentAuthorityId = session.DocumentAuthorityId,
                LastRun = session.LastRun == null ? null : new ChatRunRecord { DocumentRuntimeKey = session.LastRun.DocumentRuntimeKey } };
            _toolExecutor.BindResourceAuthority(source);
            return Task.Run(() => new VbaEditorResourceService(_toolExecutor.ResourceGateway, _resourceData)
                .Open(source, request.ModuleName, token), token);
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

        public ToolRunResult SaveVbaModule(string moduleName, string code, string expectedCodeSha256 = null)
        {
            var settings = _settingsService.Load();
            var tools = _toolCatalog.GetVisibleTools().Where(s => s.Enabled).ToList();
            var command = new ToolInvocation { ToolId = _toolExecutor.VbaToolId("vba_write_module") };
            command.Arguments["moduleName"] = moduleName;
            command.Arguments["code"] = code;
            command.Arguments["mode"] = "updateOnly";
            command.ExpectedContentSha256 = expectedCodeSha256;
            return WithReservedSession(LoadSession(null), session =>
            {
                var result = _toolExecutor.ExecuteManual(command, tools,
                    settings, false, true, session);
                _toolCatalog.InvalidateDocumentVbaTools();
                return result;
            });
        }

        public ToolRunResult RunVbaMacro(string macroName, CancellationToken cancellationToken)
        {
            return WithReservedSession(LoadSession(null), session =>
                _toolExecutor.RunVbaMacro(macroName, session, cancellationToken));
        }

        public ToolRunResult CreateVbaModule(string moduleName, string componentType, string code)
        {
            var settings = _settingsService.Load();
            var tools = _toolCatalog.GetVisibleTools().Where(s => s.Enabled).ToList();
            var command = new ToolInvocation { ToolId = _toolExecutor.VbaToolId("vba_write_module") };
            command.Arguments["moduleName"] = moduleName;
            command.Arguments["componentType"] = componentType;
            command.Arguments["code"] = code;
            command.Arguments["mode"] = "createOnly";
            return WithReservedSession(LoadSession(null), session =>
            {
                var result = _toolExecutor.ExecuteManual(command, tools,
                    settings, false, true, session);
                _toolCatalog.InvalidateDocumentVbaTools();
                return result;
            });
        }

        public ToolRunResult DeleteVbaModule(string moduleName)
        {
            var settings = _settingsService.Load();
            var tools = _toolCatalog.GetVisibleTools().Where(s => s.Enabled).ToList();
            var command = new ToolInvocation { ToolId = _toolExecutor.VbaToolId("vba_delete_module") };
            command.Arguments["moduleName"] = moduleName;
            return WithReservedSession(LoadSession(null), session =>
            {
                var result = _toolExecutor.ExecuteManual(command, tools,
                    settings, false, true, session);
                _toolCatalog.InvalidateDocumentVbaTools();
                return result;
            });
        }

        public ToolRunResult RestoreVbaBackup(string backupId, string moduleName)
        {
            var settings = _settingsService.Load();
            var tools = _toolCatalog.GetVisibleTools().Where(s => s.Enabled).ToList();
            var command = new ToolInvocation { ToolId = _toolExecutor.VbaToolId("vba_restore_backup") };
            return WithReservedSession(LoadSession(null), session =>
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(backupId))
                        command.Arguments["target"] =
                            _toolExecutor.VbaBackupSemanticTarget(backupId);
                    else if (!string.IsNullOrWhiteSpace(moduleName))
                        command.Arguments["moduleName"] = moduleName;
                }
                catch (Exception ex)
                {
                    return ToolRunResult.Error(
                        ex.Message,
                        null,
                        "vba_backup_target_not_found",
                        true);
                }
                var result = _toolExecutor.ExecuteManual(command, tools,
                    settings, false, true, session);
                _toolCatalog.InvalidateDocumentVbaTools();
                return result;
            });
        }
    }
}
