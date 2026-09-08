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
            var session = LoadAddressedSession(request.ChatId);
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

        internal ResourceStreamResponse HandleResourceData(string method, string url, CancellationToken cancellationToken,
            System.IO.Stream body = null)
        { return _resourceDataRouter.Handle(method, url, cancellationToken, body); }

        private bool ResourceOwnerIsActive(string chatId, string workspaceId)
        {
            if (Volatile.Read(ref _disposed) != 0 || string.IsNullOrWhiteSpace(chatId)) return false;
            try
            {
                // Lease checks never select a different chat or fall back to the active one.
                var session = LoadAddressedSession(chatId);
                return session != null && (workspaceId == "viewer" || workspaceId == ResourceDataPlaneService.UploadOwner ||
                    workspaceId == TrajectoryExportDownloadService.Owner ||
                    workspaceId == TrajectoryPayloadService.Owner ||
                    workspaceId == PromptContextInspectorDownloadService.Owner ||
                    workspaceId == VbaEditorResourceService.Owner ||
                    workspaceId == SkillEditorResourceService.Owner ||
                    workspaceId == ToolEditorResourceService.Owner ||
                    workspaceId == PromptEditorResourceService.Owner ||
                    workspaceId == HtmlWorkspaceEditorResourceService.Owner ||
                    string.Equals(session.ActiveHtmlArtifactId, workspaceId, StringComparison.Ordinal));
            }
            catch (InvalidOperationException) { return false; }
        }

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
            var session = LoadAddressedSession(chatId);
            return HtmlWorkspaceState(session);
        }

        public ResourceUploadOpenResponse BeginHtmlWorkspaceMutationUpload(HtmlWorkspaceMutationUploadRequest request, CancellationToken token)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ChatId))
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: an explicit chat is required.");
            return WithReservedSession(LoadAddressedSession(request.ChatId), session =>
                new HtmlWorkspaceEditorResourceService(_toolExecutor, _resourceData).BeginUpload(session, request, token));
        }

        public Task<HtmlWorkspaceSourceResponse> ReadHtmlWorkspaceSourceAsync(HtmlWorkspaceSourceRequest request, CancellationToken token)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ChatId))
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: an explicit chat is required.");
            var session = HtmlWorkspaceEditorResourceService.CaptureSourceSession(LoadAddressedSession(request.ChatId), request);
            return Task.Run(() => new HtmlWorkspaceEditorResourceService(_toolExecutor, _resourceData).OpenSource(session, request, token), token);
        }

        public ResourceDataCloseResponse CancelHtmlWorkspaceMutationUpload(ResourceUploadLeaseRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ChatId))
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: an explicit chat is required.");
            _resourceData.CloseUpload(request.ChatId, request.LeaseId, HtmlWorkspaceEditorResourceService.Owner);
            return new ResourceDataCloseResponse { Closed = true };
        }

        public HtmlWorkspaceResponse SaveHtmlWorkspaceFile(HtmlWorkspaceFilePayload request, CancellationToken token)
        {
            return SaveUploadedHtmlWorkspace(request, session =>
                new HtmlWorkspaceEditorResourceService(_toolExecutor, _resourceData).SaveFile(session, request, token));
        }

        public HtmlWorkspaceResponse SaveHtmlWorkspaceData(HtmlWorkspaceDataPayload request, CancellationToken token)
        {
            return SaveUploadedHtmlWorkspace(request, session =>
                new HtmlWorkspaceEditorResourceService(_toolExecutor, _resourceData).SaveData(session, request, token));
        }

        private HtmlWorkspaceResponse SaveUploadedHtmlWorkspace(HtmlWorkspaceMutationPayload request, Action<ChatSession> save)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ChatId))
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: an explicit chat is required.");
            try
            {
                return WithReservedSession(LoadAddressedSession(request.ChatId), session =>
                {
                    save(session);
                    SaveSessionChanges(session);
                    return HtmlWorkspaceState(session);
                });
            }
            finally { _resourceData.CloseUpload(request.ChatId, request.UploadLeaseId, HtmlWorkspaceEditorResourceService.Owner); }
        }

        public HtmlWorkspaceResponse ImportUploadedHtmlToWorkspace(HtmlWorkspaceImportPayload request)
        {
            return WithHtmlWorkspaceAction(request, session =>
            {
                var imported = MutateHtmlWorkspaceAction(session, request, "common.html_workspace_import",
                    new Dictionary<string, object> { ["source"] = request.SourceResourceUri, ["expected"] = request.ExpectedActiveHtmlArtifactId, ["path"] = request.TargetPath },
                    () => _uploadedHtmlResources.Import(session, request.SourceResourceUri, request.ExpectedActiveHtmlArtifactId, request.TargetPath));
                SaveSessionChanges(session);
                var response = HtmlWorkspaceState(session);
                response.ImportedPath = imported.ImportedPath;
                response.ImportedFromResourceUri = imported.ImportedFromResourceUri;
                return response;
            });
        }

        public HtmlWorkspaceResponse PrepareHtmlWorkspaceExport(HtmlWorkspaceExportPayload request,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return WithHtmlWorkspaceAction(request, session =>
            {
                var previousArtifactId = session.ActiveHtmlArtifactId;
                var exportArtifactId = MutateHtmlWorkspaceAction(session, request, "common.html_workspace_export",
                    new Dictionary<string, object> { ["expected"] = request.ExpectedActiveHtmlArtifactId },
                    () => HtmlWorkspaceArtifactService.PrepareExport(session, request.ExpectedActiveHtmlArtifactId));
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

        public HtmlWorkspaceResponse DeleteHtmlWorkspaceFile(HtmlWorkspaceDeleteFilePayload request)
        {
            return WithHtmlWorkspaceAction(request, session =>
            {
                MutateHtmlWorkspaceAction(session, request, "common.html_workspace_delete", new Dictionary<string, object> { ["target"] = request.Path },
                    () => HtmlWorkspaceToolService.DeleteFile(session, request.Path));
                SaveSessionChanges(session);
                return HtmlWorkspaceState(session);
            });
        }

        public HtmlWorkspaceResponse DeleteHtmlWorkspaceData(HtmlWorkspaceDeleteDataPayload request)
        {
            return WithHtmlWorkspaceAction(request, session =>
            {
                MutateHtmlWorkspaceAction(session, request, "common.html_workspace_delete", new Dictionary<string, object> { ["target"] = request.Name },
                    () => HtmlWorkspaceToolService.DeleteDataSource(session, request.Name));
                SaveSessionChanges(session);
                return HtmlWorkspaceState(session);
            });
        }

        public HtmlWorkspaceResponse SetActiveHtmlWorkspaceFile(HtmlWorkspaceActiveFilePayload request)
        {
            return WithHtmlWorkspaceAction(request, session =>
            {
                MutateHtmlWorkspaceAction(session, request, "common.html_workspace_select", new Dictionary<string, object> { ["path"] = request.Path },
                    () => HtmlWorkspaceToolService.SetActiveFile(session, request.Path));
                SaveSessionChanges(session);
                return HtmlWorkspaceState(session);
            });
        }

        public HtmlWorkspaceResponse RestoreHtmlWorkspaceSnapshot(HtmlWorkspaceRestorePayload request)
        {
            return WithHtmlWorkspaceAction(request, session =>
            {
                var recovery = session.HtmlWorkspaceRecovery ?? new HtmlWorkspaceRecoveryState();
                var degraded = string.Equals(recovery.Status, HtmlWorkspaceRecoveryStatuses.Degraded, System.StringComparison.OrdinalIgnoreCase);
                if (!recovery.CanMutate && string.IsNullOrWhiteSpace(request.SnapshotId))
                {
                    throw new System.InvalidOperationException("Select an explicit healthy HTML workspace revision to recover editing.");
                }
                var targetId = degraded && !string.IsNullOrWhiteSpace(request.SnapshotId)
                    ? request.SnapshotId
                    : string.IsNullOrWhiteSpace(request.SnapshotId)
                        ? session.HtmlWorkspace.History.Select(item => item == null ? null : item.Id)
                            .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id))
                        : session.HtmlWorkspace.History
                            .Where(item => item != null && string.Equals(item.Id, request.SnapshotId, System.StringComparison.OrdinalIgnoreCase))
                            .Select(item => item.Id)
                            .FirstOrDefault();
                if (string.IsNullOrWhiteSpace(targetId))
                {
                    throw new System.InvalidOperationException("HTML workspace snapshot was not found.");
                }
                MutateHtmlWorkspaceAction(session, request, "common.html_workspace_restore",
                    new Dictionary<string, object> { ["snapshotId"] = targetId }, () => {
                        HtmlWorkspaceArtifactService.RestoreAsRevision(session, targetId);
                        return true;
                    });
                SaveSessionChanges(session);
                return HtmlWorkspaceState(session);
            });
        }

        public HtmlWorkspaceResponse RedoHtmlWorkspaceSnapshot(HtmlWorkspaceRestorePayload request)
        {
            return WithHtmlWorkspaceAction(request, session =>
            {
                var branches = HtmlWorkspaceNavigationService.GetRedoBranches(session);
                if (string.IsNullOrWhiteSpace(request.SnapshotId) && branches.Count > 1)
                {
                    return HtmlWorkspaceState(session, true);
                }
                var branch = string.IsNullOrWhiteSpace(request.SnapshotId)
                    ? branches.SingleOrDefault()
                    : branches.FirstOrDefault(item => string.Equals(item.Id, request.SnapshotId, System.StringComparison.OrdinalIgnoreCase));
                if (branch == null)
                {
                    throw new System.InvalidOperationException("HTML workspace redo target must be a direct child revision.");
                }
                var targetId = branch.Id;
                MutateHtmlWorkspaceAction(session, request, "common.html_workspace_redo",
                    new Dictionary<string, object> { ["snapshotId"] = targetId }, () => {
                        HtmlWorkspaceArtifactService.RestoreAsRevision(session, targetId, true);
                        return true;
                    });
                SaveSessionChanges(session);
                return HtmlWorkspaceState(session);
            });
        }

        private T WithHtmlWorkspaceAction<T>(HtmlWorkspaceActionPayload request, Func<ChatSession, T> action)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ChatId))
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: an explicit HTML action chat is required.");
            return WithReservedSession(LoadAddressedSession(request.ChatId), session =>
            {
                ValidateHtmlWorkspaceAction(session, request);
                return action(session);
            });
        }

        private void ValidateHtmlWorkspaceAction(ChatSession session, HtmlWorkspaceActionPayload request)
        {
            if (!_chatSessions.IsCurrentDocument(session))
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: open the document of this chat before editing HTML.");
            HtmlWorkspaceActionGuard.Validate(session, request);
        }

        private T MutateHtmlWorkspaceAction<T>(ChatSession session, HtmlWorkspaceActionPayload request,
            string operation, IDictionary<string, object> arguments, Func<T> action)
        {
            arguments = new Dictionary<string, object>(arguments) {
                ["expectedActiveHtmlArtifactId"] = request.ExpectedActiveHtmlArtifactId,
                ["expectedSessionRevision"] = request.ExpectedSessionRevision.Value
            };
            return _toolExecutor.MutateLocalResources(session, operation, arguments, action,
                validateBeforeDispatch: () => ValidateHtmlWorkspaceAction(session, request));
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
                Workspace = HtmlWorkspaceEditorResourceService.Metadata(session),
                StaticPreflight = preflightDto,
                RedoChoiceRequired = redoChoiceRequired
            };
        }
    }
}
