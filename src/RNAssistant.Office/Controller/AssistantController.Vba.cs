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

        public ResourceUploadOpenResponse BeginVbaModuleUpload(VbaEditorUploadRequest request, CancellationToken token)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ChatId))
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: an explicit chat is required.");
            return WithReservedSession(LoadAddressedSession(request.ChatId), session =>
                new VbaEditorResourceService(_toolExecutor.ResourceGateway, _resourceData).BeginUpload(session, request, token));
        }

        public ResourceDataCloseResponse CancelVbaModuleUpload(ResourceUploadLeaseRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ChatId))
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: an explicit chat is required.");
            _resourceData.CloseUpload(request.ChatId, request.LeaseId, VbaEditorResourceService.Owner);
            return new ResourceDataCloseResponse { Closed = true };
        }

        public Task<ToolRunResult> SaveVbaModuleAsync(VbaModulePayload request, CancellationToken token)
        {
            return WriteVbaModuleAsync(request, "updateOnly", null, request?.ExpectedCodeSha256, token);
        }

        public Task<ToolRunResult> CreateVbaModuleAsync(VbaCreateModulePayload request, CancellationToken token)
        {
            return WriteVbaModuleAsync(request, "createOnly", request?.ComponentType, null, token);
        }

        private async Task<ToolRunResult> WriteVbaModuleAsync(VbaEditorWriteRequest request, string mode,
            string componentType, string expectedCodeSha256, CancellationToken token)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ChatId))
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: an explicit chat is required.");
            try
            {
                var session = LoadAddressedSession(request.ChatId);
                using (ReserveChatOperation(session))
                {
                    session = ReloadReservedSession(session);
                    var settings = _settingsService.Load();
                    var tools = _toolCatalog.GetVisibleTools().Where(s => s.Enabled).ToList();
                    var result = await Task.Run(() =>
                    {
                        var code = new VbaEditorResourceService(_toolExecutor.ResourceGateway, _resourceData)
                            .ReadUploadedSource(session, request, token);
                        var command = new ToolInvocation { ToolId = _toolExecutor.VbaToolId("vba_write_module"),
                            ExpectedContentSha256 = expectedCodeSha256 };
                        command.Arguments["moduleName"] = request.ModuleName;
                        command.Arguments["code"] = code;
                        command.Arguments["mode"] = mode;
                        if (componentType != null) command.Arguments["componentType"] = componentType;
                        return _toolExecutor.ExecuteManual(command, tools, settings, false, true, session, token);
                    }, token).ConfigureAwait(false);
                    _toolCatalog.InvalidateDocumentVbaTools();
                    return result;
                }
            }
            finally { _resourceData.CloseUpload(request.ChatId, request.UploadLeaseId, VbaEditorResourceService.Owner); }
        }

        public ToolRunResult RunVbaMacro(string macroName, CancellationToken cancellationToken)
        {
            return WithReservedSession(LoadSession(null), session =>
                _toolExecutor.RunVbaMacro(macroName, session, cancellationToken));
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
