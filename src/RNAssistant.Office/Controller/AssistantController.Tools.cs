using RNAssistant.Core.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        private readonly ToolLibraryTestSessionService
            _toolLibraryTestSessions = new ToolLibraryTestSessionService();

        public ToolLibraryResponse GetTools()
        {
            return ToolLibraryResponse.From(
                _toolCatalog.GetVisibleTools());
        }

        public Task<ToolSourceReadResponse> ReadToolSourceAsync(ToolSourceReadRequest payload, CancellationToken token)
        {
            if (payload == null || string.IsNullOrWhiteSpace(payload.ChatId))
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: an explicit chat is required.");
            var session = LoadAddressedSession(payload.ChatId);
            var source = new ChatSession { Id = session.Id, Host = session.Host, DocumentKey = session.DocumentKey,
                DocumentAuthorityId = session.DocumentAuthorityId };
            return Task.Run(() => new ToolEditorResourceService(_toolExecutor.ResourceGateway, _resourceData, _toolCatalog)
                .Open(source, payload, token), token);
        }

        public ToolLibraryDocumentationResponse GetToolDocumentation(
            ToolLibraryDocumentationRequest request)
        {
            if (request == null || !string.Equals(request.Type,
                    ToolLibraryDocumentationRequest.ContractType,
                    StringComparison.Ordinal) ||
                request.ContractVersion !=
                    ToolLibraryResponse.CurrentContractVersion ||
                string.IsNullOrWhiteSpace(request.ToolId) ||
                string.IsNullOrWhiteSpace(request.ExpectedRevision))
            {
                throw new InvalidOperationException(
                    "Unsupported or incomplete Tool Library documentation contract.");
            }
            var matches = _toolCatalog.GetVisibleTools()
                .Where(item => item != null && item.BuiltIn &&
                    string.Equals(item.Id, request.ToolId,
                        StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1 || !matches[0].BuiltIn)
                throw new InvalidOperationException(
                    "Built-in tool documentation was not found for the exact id.");
            var tool = matches[0];
            var revision = ToolAuthoringService.LibraryRevision(tool);
            if (!string.Equals(revision, request.ExpectedRevision,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Tool documentation revision is stale. Refresh Tool Library.");
            return new ToolLibraryDocumentationResponse
            {
                Type = ToolLibraryDocumentationResponse.ContractType,
                ContractVersion =
                    ToolLibraryResponse.CurrentContractVersion,
                ToolId = tool.Id,
                Revision = revision,
                Markdown = ToolLibraryDocumentationService.Build(tool)
            };
        }

        public ResourceUploadOpenResponse BeginToolMutationUpload(ToolMutationUploadRequest request, CancellationToken token)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ChatId))
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: an explicit chat is required.");
            return WithReservedSession(LoadAddressedSession(request.ChatId), session =>
                new ToolEditorResourceService(_toolExecutor.ResourceGateway, _resourceData, _toolCatalog).BeginUpload(session, request, token));
        }

        public ResourceDataCloseResponse CancelToolMutationUpload(ResourceUploadLeaseRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ChatId))
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: an explicit chat is required.");
            _resourceData.CloseUpload(request.ChatId, request.LeaseId, ToolEditorResourceService.Owner);
            return new ResourceDataCloseResponse { Closed = true };
        }

        public async Task<ToolLibraryMutationResponse> SaveToolsAsync(ToolMutationWriteRequest payload, CancellationToken token)
        {
            if (payload == null || string.IsNullOrWhiteSpace(payload.ChatId))
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: an explicit chat is required.");
            try
            {
                using (_chatRuns.ReserveMaintenance())
                {
                    EnsureNoActiveRuns();
                    var session = LoadAddressedSession(payload.ChatId);
                    return await Task.Run(() =>
                    {
                        var mutations = new ToolEditorResourceService(_toolExecutor.ResourceGateway, _resourceData, _toolCatalog).PrepareMutations(session, payload, token);
                        var results = new List<ToolMutationResultDto>();
                        try
                        {
                            foreach (var mutation in mutations)
                            {
                                token.ThrowIfCancellationRequested();
                                var result = _toolExecutor.ExecuteToolLibraryMutation(mutation);
                                results.Add(ToolMutationResultDto.From(result));
                                if (result.Outcome.Status != ToolAuthoringOutcomeStatus.Ok) break;
                            }
                        }
                        finally { _toolCatalog.InvalidateDocumentVbaTools(); }
                        return new ToolLibraryMutationResponse { Type = ToolLibraryMutationResponse.ContractType,
                            ContractVersion = ToolLibraryResponse.CurrentContractVersion, Results = results, Library = GetTools() };
                    }, token).ConfigureAwait(false);
                }
            }
            finally { _resourceData.CloseUpload(payload.ChatId, payload.UploadLeaseId, ToolEditorResourceService.Owner); }
        }

        public SkillLibraryResponse GetSkills()
        {
            return SkillLibraryResponse.From(
                _skillCatalog.GetVisibleSkills());
        }

        public ResourceUploadOpenResponse BeginSkillMutationUpload(SkillMutationUploadRequest request, CancellationToken token)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ChatId))
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: an explicit chat is required.");
            return WithReservedSession(LoadAddressedSession(request.ChatId), session =>
                new SkillEditorResourceService(_toolExecutor.ResourceGateway, _resourceData, _skillCatalog).BeginUpload(session, request, token));
        }

        public ResourceDataCloseResponse CancelSkillMutationUpload(ResourceUploadLeaseRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ChatId))
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: an explicit chat is required.");
            _resourceData.CloseUpload(request.ChatId, request.LeaseId, SkillEditorResourceService.Owner);
            return new ResourceDataCloseResponse { Closed = true };
        }

        public async Task<SkillLibraryMutationResponse> SaveSkillsAsync(SkillMutationWriteRequest payload, CancellationToken token)
        {
            if (payload == null || string.IsNullOrWhiteSpace(payload.ChatId))
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: an explicit chat is required.");
            try
            {
                using (_chatRuns.ReserveMaintenance())
                {
                    EnsureNoActiveRuns();
                    var session = LoadAddressedSession(payload.ChatId);
                    return await Task.Run(() =>
                    {
                        var mutations = new SkillEditorResourceService(_toolExecutor.ResourceGateway, _resourceData, _skillCatalog)
                            .PrepareCoreMutations(session, payload, token);
                        var results = new List<SkillMutationResultDto>();
                        foreach (var mutation in mutations)
                        {
                            token.ThrowIfCancellationRequested();
                            var result = _toolExecutor.ExecuteSkillLibraryMutation(mutation);
                            results.Add(SkillMutationResultDto.From(result));
                            if (result.Outcome.Status != SkillAuthoringOutcomeStatus.Ok) break;
                        }
                        return new SkillLibraryMutationResponse { Type = SkillLibraryMutationResponse.ContractType,
                            ContractVersion = SkillLibraryResponse.CurrentContractVersion, Results = results, Library = GetSkills() };
                    }, token).ConfigureAwait(false);
                }
            }
            finally { _resourceData.CloseUpload(payload.ChatId, payload.UploadLeaseId, SkillEditorResourceService.Owner); }
        }

        public Task<SkillSourceReadResponse> ReadSkillSourceAsync(
            SkillSourceReadRequest payload, CancellationToken token)
        {
            if (payload == null || string.IsNullOrWhiteSpace(payload.ChatId))
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: an explicit chat is required.");
            var session = LoadAddressedSession(payload.ChatId);
            var source = new ChatSession { Id = session.Id, Host = session.Host, DocumentKey = session.DocumentKey,
                DocumentAuthorityId = session.DocumentAuthorityId };
            return Task.Run(() => new SkillEditorResourceService(_toolExecutor.ResourceGateway, _resourceData, _skillCatalog)
                .Open(source, payload, token), token);
        }

        public async Task<SkillReferenceResponse> SaveSkillReferenceAsync(SkillMutationWriteRequest payload, CancellationToken token)
        {
            if (payload == null || string.IsNullOrWhiteSpace(payload.ChatId))
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: an explicit chat is required.");
            try
            {
                using (_chatRuns.ReserveMaintenance())
                {
                    EnsureNoActiveRuns();
                    var session = LoadAddressedSession(payload.ChatId);
                    return await Task.Run(() =>
                    {
                        var body = new SkillEditorResourceService(_toolExecutor.ResourceGateway, _resourceData, _skillCatalog)
                            .PrepareReferenceMutation(session, payload, token);
                        token.ThrowIfCancellationRequested();
                        var result = _toolExecutor.ExecuteSkillLibraryReferenceMutation("upsert", body.SkillId, body.Path,
                            body.Content, body.ExpectedPackageRevision);
                        return SkillReferenceMutationResult(result, body.Path, false);
                    }, token).ConfigureAwait(false);
                }
            }
            finally { _resourceData.CloseUpload(payload.ChatId, payload.UploadLeaseId, SkillEditorResourceService.Owner); }
        }

        public SkillReferenceResponse DeleteSkillReference(
            SkillReferencePayload payload)
        {
            using (_chatRuns.ReserveMaintenance())
            {
                EnsureNoActiveRuns();
                ValidateSkillReferencePayload(payload);
                var result = _toolExecutor
                    .ExecuteSkillLibraryReferenceMutation(
                        "delete", payload.SkillId, payload.Path,
                        null, payload.ExpectedPackageRevision);
                return SkillReferenceMutationResult(
                    result, payload.Path, true);
            }
        }

        private static void ValidateSkillReferencePayload(
            SkillReferencePayload payload)
        {
            if (payload == null || !string.Equals(payload.Type,
                    SkillReferencePayload.ContractType,
                    StringComparison.Ordinal) ||
                payload.ContractVersion !=
                    SkillLibraryResponse.CurrentContractVersion ||
                string.IsNullOrWhiteSpace(payload.SkillId) ||
                string.IsNullOrWhiteSpace(
                    payload.ExpectedPackageRevision))
            {
                throw new InvalidOperationException(
                    "Unsupported or incomplete skill reference contract.");
            }
        }

        private static SkillReferenceResponse SkillReferenceMutationResult(
            SkillManualMutationResult result,
            string path,
            bool deleted)
        {
            var package = result == null ? null : result.Package;
            var reference = package == null ? null :
                package.References.FirstOrDefault(item => item != null &&
                    string.Equals(item.Path, path,
                        StringComparison.OrdinalIgnoreCase));
            return new SkillReferenceResponse
            {
                Type = SkillReferenceResponse.ContractType,
                ContractVersion =
                    SkillLibraryResponse.CurrentContractVersion,
                Result = SkillMutationResultDto.From(result),
                Skill = SkillPackageDto.From(package),
                Path = reference == null ? path : reference.Path,
                Deleted = deleted && result != null &&
                    result.Outcome.Status == SkillAuthoringOutcomeStatus.Ok,
                Reference = SkillReferenceDto.From(reference)
            };
        }

        public ToolRunResult RunTool(
            string toolId,
            IDictionary<string, object> arguments,
            bool dryRun,
            Action<string, string> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var settings = _settingsService.Load();
            var session = LoadSession(null);
            var tools = _toolCatalog.GetVisibleTools().Where(s => s.Enabled).ToList();
            var command = new ToolInvocation { ToolId = toolId };
            foreach (var pair in arguments ?? new Dictionary<string, object>())
            {
                command.Arguments[pair.Key] = pair.Value;
            }

            ReportProgress(progress, dryRun ? "checking" : "executing", (dryRun ? "Проверяю tool: " : "Исполняю tool: ") + toolId);
            if (dryRun)
            {
                if (string.Equals(toolId,
                        CapabilityToolCatalog.ReadToolId,
                        StringComparison.Ordinal))
                {
                    return _toolLibraryTestSessions.Execute(
                        session,
                        command,
                        manualSnapshot => _toolExecutor.ExecuteManual(
                            command, tools, settings, true, true,
                            manualSnapshot, cancellationToken));
                }
                return _toolExecutor.ExecuteManual(
                    command,
                    tools,
                    settings,
                    true,
                    true,
                    OfficeToolExecutor.CreateIsolatedManualSession(session),
                    cancellationToken);
            }

            if (!_toolExecutor.RequiresSessionLeaseForManualRun(toolId, tools))
            {
                // Read-only library checks do not modify chat state and may safely use an
                // isolated snapshot while the active chat is waiting on the model/tools.
                return _toolLibraryTestSessions.Execute(
                    session,
                    command,
                    manualSnapshot => _toolExecutor.ExecuteManual(
                        command, tools, settings, false, true,
                        manualSnapshot, cancellationToken));
            }

            if (_chatRuns.IsExternallyRunning(session.Id))
            {
                return ToolRunResult.Error(
                    "A mutating library tool cannot run while this chat is active. Read-only tools can still be tested; stop the chat before testing document or local-state mutations.",
                    null,
                    "manual_tool_chat_busy",
                    true);
            }

            return WithReservedSession(session, current =>
            {
                var result = _toolExecutor.ExecuteManual(command, tools,
                    settings, false, true, current, cancellationToken);
                if (IsSessionArtifactTool(toolId))
                {
                    SaveSessionChanges(current);
                }
                return result;
            });
        }

        private static void ReportProgress(Action<string, string> progress, string phase, string message)
        {
            if (progress != null)
            {
                progress(phase, message);
            }
        }

        private static bool IsSessionArtifactTool(string toolId)
        {
            return string.Equals(toolId, HtmlWorkspaceToolCatalog.WriteFileToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, HtmlWorkspaceToolCatalog.WriteDataToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, HtmlWorkspaceToolCatalog.ApplyPatchToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, HtmlWorkspaceToolCatalog.DeleteToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, HtmlWorkspaceToolCatalog.BindDataToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, HtmlWorkspaceToolCatalog.RefreshDataToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, HtmlWorkspaceToolCatalog.FreezeDataToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, TaskListToolCatalog.SetToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, PlanDocumentToolCatalog.SaveToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, PlanDocumentToolCatalog.RestoreToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, PlanDocumentToolCatalog.DeleteToolId, StringComparison.OrdinalIgnoreCase);
        }
    }
}
