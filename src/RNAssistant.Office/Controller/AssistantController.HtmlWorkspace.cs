using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        public ResourceDataOpenResponse OpenResourceData(ResourceDataOpenRequest request,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ChatId) ||
                string.IsNullOrWhiteSpace(request.WorkspaceId) || string.IsNullOrWhiteSpace(request.BindingName))
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: explicit workspace binding required.");
            var session = LoadSession(request.ChatId);
            if (!string.Equals(session.ActiveHtmlArtifactId, request.WorkspaceId, StringComparison.Ordinal))
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: the workspace revision is no longer active.");
            var matches = session.HtmlWorkspace.DataSources.Where(item => item.Name == request.BindingName).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: binding is unknown or ambiguous.");
            var binding = matches[0].Binding;
            HtmlWorkspaceToolService.NormalizeBinding(binding, matches[0]);
            _toolExecutor.BindResourceAuthority(session);
            return _resourceData.Open(session, request.WorkspaceId,
                binding.Policy == "head" ? new ResourceRef(binding.Resource.Identity.Uri) : binding.Resource.Copy(), binding.View, binding.ViewPath, cancellationToken);
        }

        public ResourceDataCloseResponse CloseResourceData(ResourceDataCloseRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ChatId) || string.IsNullOrWhiteSpace(request.WorkspaceId))
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: explicit workspace owner required.");
            if (string.IsNullOrWhiteSpace(request.LeaseId)) _resourceData.CloseWorkspace(request.ChatId, request.WorkspaceId);
            else _resourceData.Close(request.ChatId, request.WorkspaceId, request.LeaseId);
            return new ResourceDataCloseResponse { Closed = true };
        }

        internal ResourceStreamResponse ReadResourceData(string method, string url, CancellationToken cancellationToken)
        { return _resourceDataRouter.Handle(method, url, cancellationToken); }

        public Task<HtmlFetchResponse> HtmlFetchAsync(HtmlFetchRequest request, CancellationToken cancellationToken)
        {
            return _htmlNetwork.FetchAsync(request, cancellationToken);
        }

        public HtmlOriginPermissionResponse AllowHtmlNetworkOrigin(string origin)
        {
            return new HtmlOriginPermissionResponse
            {
                Origin = _htmlNetwork.AllowOrigin(origin),
                Allowed = true
            };
        }

        public HtmlWorkspaceResponse GetHtmlWorkspace(string chatId = null)
        {
            var session = LoadSession(chatId);
            return HtmlWorkspaceState(session);
        }

        public HtmlWorkspaceResponse SaveHtmlWorkspaceFile(string chatId, string path, string kind, string content, bool setActive)
        {
            return WithReservedSession(LoadSession(chatId), session =>
            {
                _toolExecutor.MutateLocalResources(session, "common.html_workspace_write_file", new Dictionary<string, object> { ["path"] = path, ["kind"] = kind, ["content"] = content, ["setActive"] = setActive },
                    () => HtmlWorkspaceToolService.UpsertFile(session, path, kind, content, setActive));
                SaveSessionChanges(session);
                return HtmlWorkspaceState(session);
            });
        }

        public HtmlWorkspaceResponse SaveHtmlWorkspaceData(string chatId, string name, string json)
        {
            return WithReservedSession(LoadSession(chatId), session =>
            {
                _toolExecutor.MutateLocalResources(session, "common.html_data_write", new Dictionary<string, object> { ["name"] = name, ["json"] = json },
                    () => HtmlWorkspaceToolService.UpsertDataSource(session, name, json));
                SaveSessionChanges(session);
                return HtmlWorkspaceState(session);
            });
        }

        public UploadedHtmlSourcePreviewDto GetUploadedHtmlSourcePreview(
            string chatId,
            string sourceResourceUri)
        {
            return _uploadedHtmlResources.Preview(LoadSession(chatId), sourceResourceUri);
        }

        public HtmlWorkspaceResponse ImportUploadedHtmlToWorkspace(
            string chatId,
            string sourceResourceUri,
            string expectedActiveHtmlArtifactId,
            string targetPath)
        {
            return WithReservedSession(LoadSession(chatId), session =>
            {
                var imported = _toolExecutor.MutateLocalResources(session, "common.html_workspace_import",
                    new Dictionary<string, object> { ["source"] = sourceResourceUri, ["expected"] = expectedActiveHtmlArtifactId, ["path"] = targetPath },
                    () => _uploadedHtmlResources.Import(session, sourceResourceUri, expectedActiveHtmlArtifactId, targetPath));
                SaveSessionChanges(session);
                var response = HtmlWorkspaceState(session);
                response.ImportedPath = imported.ImportedPath;
                response.ImportedFromResourceUri = imported.ImportedFromResourceUri;
                return response;
            });
        }

        public HtmlWorkspaceResponse PrepareHtmlWorkspaceExport(
            string chatId,
            string expectedActiveHtmlArtifactId,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return WithReservedSession(LoadSession(chatId), session =>
            {
                var previousArtifactId = session.ActiveHtmlArtifactId;
                var exportArtifactId = _toolExecutor.MutateLocalResources(session, "common.html_workspace_export",
                    new Dictionary<string, object> { ["expected"] = expectedActiveHtmlArtifactId },
                    () => HtmlWorkspaceArtifactService.PrepareExport(session, expectedActiveHtmlArtifactId));
                if (!string.Equals(previousArtifactId, exportArtifactId, System.StringComparison.OrdinalIgnoreCase))
                {
                    _chatSessions.NotifySaved(session); // The mutation barrier already persisted the checkpoint.
                }
                var artifact = (session.Artifacts ?? new System.Collections.Generic.List<ChatArtifact>()).Single(item =>
                    item != null &&
                    string.Equals(item.Id, exportArtifactId, System.StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Kind, ChatArtifactKinds.HtmlWorkspace, System.StringComparison.OrdinalIgnoreCase));
                var response = HtmlWorkspaceState(session);
                response.ExportRevisionArtifactId = artifact.Id;
                response.ExportResourceUri = ChatResourceUri.CreateArtifactRevision(session, artifact).Uri;
                response.ExportContentSha256 = artifact.ContentSha256;
                response.ResourceExport = new HtmlWorkspaceExportService(_toolExecutor.ResourceGateway, _resourceData)
                    .Open(session, artifact.Id, cancellationToken);
                return response;
            });
        }

        public HtmlWorkspaceResponse DeleteHtmlWorkspaceFile(string chatId, string path)
        {
            return WithReservedSession(LoadSession(chatId), session =>
            {
                _toolExecutor.MutateLocalResources(session, "common.html_workspace_delete", new Dictionary<string, object> { ["target"] = path },
                    () => HtmlWorkspaceToolService.DeleteFile(session, path));
                SaveSessionChanges(session);
                return HtmlWorkspaceState(session);
            });
        }

        public HtmlWorkspaceResponse DeleteHtmlWorkspaceData(string chatId, string name)
        {
            return WithReservedSession(LoadSession(chatId), session =>
            {
                _toolExecutor.MutateLocalResources(session, "common.html_workspace_delete", new Dictionary<string, object> { ["target"] = name },
                    () => HtmlWorkspaceToolService.DeleteDataSource(session, name));
                SaveSessionChanges(session);
                return HtmlWorkspaceState(session);
            });
        }

        public HtmlWorkspaceResponse SetActiveHtmlWorkspaceFile(string chatId, string path)
        {
            return WithReservedSession(LoadSession(chatId), session =>
            {
                _toolExecutor.MutateLocalResources(session, "common.html_workspace_select", new Dictionary<string, object> { ["path"] = path },
                    () => HtmlWorkspaceToolService.SetActiveFile(session, path));
                SaveSessionChanges(session);
                return HtmlWorkspaceState(session);
            });
        }

        public HtmlWorkspaceResponse RestoreHtmlWorkspaceSnapshot(string chatId, string snapshotId)
        {
            return WithReservedSession(LoadSession(chatId), session =>
            {
                var recovery = session.HtmlWorkspaceRecovery ?? new HtmlWorkspaceRecoveryState();
                var degraded = string.Equals(recovery.Status, HtmlWorkspaceRecoveryStatuses.Degraded, System.StringComparison.OrdinalIgnoreCase);
                if (!recovery.CanMutate && string.IsNullOrWhiteSpace(snapshotId))
                {
                    throw new System.InvalidOperationException("Select an explicit healthy HTML workspace revision to recover editing.");
                }
                var targetId = degraded && !string.IsNullOrWhiteSpace(snapshotId)
                    ? snapshotId
                    : string.IsNullOrWhiteSpace(snapshotId)
                        ? session.HtmlWorkspace.History.Select(item => item == null ? null : item.Id)
                            .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id))
                        : session.HtmlWorkspace.History
                            .Where(item => item != null && string.Equals(item.Id, snapshotId, System.StringComparison.OrdinalIgnoreCase))
                            .Select(item => item.Id)
                            .FirstOrDefault();
                if (string.IsNullOrWhiteSpace(targetId))
                {
                    throw new System.InvalidOperationException("HTML workspace snapshot was not found.");
                }
                _toolExecutor.MutateLocalResources(session, "common.html_workspace_restore",
                    new Dictionary<string, object> { ["snapshotId"] = targetId }, () => {
                        string error;
                        if (!_chatStore.TryActivateHtmlWorkspaceRevision(session, targetId, out error))
                            throw new InvalidOperationException(error ?? "HTML workspace artifact body is missing or corrupt.");
                        return true;
                    });
                SaveSessionChanges(session);
                return HtmlWorkspaceState(session);
            });
        }

        public HtmlWorkspaceResponse RedoHtmlWorkspaceSnapshot(string chatId, string snapshotId)
        {
            return WithReservedSession(LoadSession(chatId), session =>
            {
                var branches = HtmlWorkspaceNavigationService.GetRedoBranches(session);
                if (string.IsNullOrWhiteSpace(snapshotId) && branches.Count > 1)
                {
                    return HtmlWorkspaceState(session, true);
                }
                var branch = string.IsNullOrWhiteSpace(snapshotId)
                    ? branches.SingleOrDefault()
                    : branches.FirstOrDefault(item => string.Equals(item.Id, snapshotId, System.StringComparison.OrdinalIgnoreCase));
                if (branch == null)
                {
                    throw new System.InvalidOperationException("HTML workspace redo target must be a direct child revision.");
                }
                var targetId = branch.Id;
                _toolExecutor.MutateLocalResources(session, "common.html_workspace_restore",
                    new Dictionary<string, object> { ["snapshotId"] = targetId }, () => {
                        string error;
                        if (!_chatStore.TryActivateHtmlWorkspaceRevision(session, targetId, out error))
                            throw new InvalidOperationException(error ?? "HTML workspace artifact body is missing or corrupt.");
                        return true;
                    });
                SaveSessionChanges(session);
                return HtmlWorkspaceState(session);
            });
        }

        private static HtmlWorkspaceResponse HtmlWorkspaceState(ChatSession session, bool redoChoiceRequired = false)
        {
            var preflight = HtmlWorkspaceToolService.InspectForPreview(
                session, CancellationToken.None);
            HtmlWorkspacePreflightDto preflightDto;
            try
            {
                preflightDto = string.IsNullOrWhiteSpace(preflight.DataJson)
                    ? new HtmlWorkspacePreflightDto()
                    : JsonConvert.DeserializeObject<HtmlWorkspacePreflightDto>(
                        preflight.DataJson) ?? new HtmlWorkspacePreflightDto();
            }
            catch (JsonException)
            {
                preflightDto = new HtmlWorkspacePreflightDto();
            }
            preflightDto.Status = preflight.Status ==
                HtmlWorkspaceOutcomeStatus.Ok ? "ok" : "error";
            preflightDto.Message = preflight.Message;
            preflightDto.Issues = preflightDto.Issues ??
                new List<HtmlWorkspacePreflightIssueDto>();
            return new HtmlWorkspaceResponse
            {
                SessionRevision = session == null ? 0 : session.Revision,
                ActiveChatId = session.Id,
                ActiveHtmlArtifactId = session == null ? string.Empty : session.ActiveHtmlArtifactId,
                Artifacts = ChatArtifactDto.From(session),
                ArtifactLibrary = ArtifactLibraryProjectionService.Project(session),
                Workspace = HtmlWorkspaceDto.From(
                    session == null ? null : HtmlWorkspaceToolService.NormalizeWorkspace(session.HtmlWorkspace),
                    session == null ? null : session.HtmlWorkspaceRecovery),
                StaticPreflight = preflightDto,
                RedoChoiceRequired = redoChoiceRequired
            };
        }
    }
}
