using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Diagnostics;

namespace RNAssistant.Office.WebView
{
    public sealed class AssistantWebBridge : IDisposable
    {
        private readonly AssistantController _controller;
        private readonly Action<string> _postMessageJson;
        private readonly BridgeRequestCancellationRegistry _cancellations;
        private readonly string _bridgeToken;
        private readonly object _resourceChangesSync = new object();
        private readonly Dictionary<string, ResourceChangedMessage> _resourceChanges = new Dictionary<string, ResourceChangedMessage>();
        private readonly Timer _resourceChangesTimer;
        private bool _resourceChangesDisposed;

        public AssistantWebBridge(AssistantController controller, Action<string> postMessageJson)
        {
            _controller = controller;
            _postMessageJson = postMessageJson;
            _cancellations = new BridgeRequestCancellationRegistry();
            _bridgeToken = Guid.NewGuid().ToString("N");
            _resourceChangesTimer = new Timer(ignored => FlushResourceChanges(), null, Timeout.Infinite, Timeout.Infinite);
            _controller.ModelRequestDiagnostics += ReportModelRequestDiagnostics;
            _controller.ResourceAuthorityChanged += ReportResourceChanged;
        }

        public async Task<string> HandleMessageAsync(string requestJson)
        {
            string id = null;
            CancellationTokenSource cancellationSource = null;
            try
            {
                _cancellations.ThrowIfDisposed();
                var request = JsonConvert.DeserializeObject<BridgeRequest>(requestJson) ?? new BridgeRequest();
                id = request.Id;
                var type = (request.Type ?? string.Empty).Trim();
                var payload = request.Payload ?? JValue.CreateNull();
                if (RequiresBridgeToken(type) && !string.Equals(request.BridgeToken, _bridgeToken, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Invalid WebView bridge token.");
                }
                object responsePayload;
                if (string.Equals(type, "cancelRequest", StringComparison.OrdinalIgnoreCase))
                {
                    responsePayload = new CancellationResponse
                    {
                        Cancelled = _cancellations.Cancel(Payload<CancelRequestPayload>(payload).RequestId)
                    };
                    return Success(id, responsePayload);
                }
                if (string.Equals(type, "cancelChatRun", StringComparison.OrdinalIgnoreCase))
                {
                    var cancelRun = Payload<CancelChatRunPayload>(payload);
                    responsePayload = new CancellationResponse
                    {
                        Cancelled = _controller.CancelChatRun(cancelRun.ChatId, cancelRun.RunId)
                    };
                    return Success(id, responsePayload);
                }

                cancellationSource = _cancellations.Create(id, type);
                var cancellationToken = cancellationSource == null ? CancellationToken.None : cancellationSource.Token;

                switch (type)
                {
                    case "init":
                        responsePayload = WithBridgeToken(_controller.Initialize());
                        break;
                    case "listChats":
                        responsePayload = _controller.ListChats();
                        break;
                    case "getChatState":
                        responsePayload = _controller.GetChatState(Payload<ChatPayload>(payload).ChatId);
                        break;
                    case "getChatTrajectory":
                        responsePayload = _controller.GetChatTrajectory(Payload<ChatTrajectoryRequest>(payload));
                        break;
                    case "exportChatTrajectory":
                        responsePayload = await _controller.ExportChatTrajectoryAsync(Payload<ChatTrajectoryExportRequest>(payload), cancellationToken).ConfigureAwait(false);
                        break;
                    case "getChatEventPayload":
                        var chatEventPayload = Payload<ChatEventPayloadRequest>(payload);
                        responsePayload = await _controller.GetChatEventPayloadAsync(chatEventPayload.ChatId, chatEventPayload.EventId, cancellationToken).ConfigureAwait(false);
                        break;
                    case "getQualificationCatalog":
                        var qualificationCatalog = Payload<QualificationCatalogPayload>(payload);
                        responsePayload = _controller.GetQualificationCatalog(
                            qualificationCatalog.ChatId, qualificationCatalog.Suite);
                        break;
                    case "getQualificationRun":
                        var qualificationRun = Payload<QualificationRunPayload>(payload);
                        responsePayload = _controller.GetQualificationRun(
                            qualificationRun.ChatId, qualificationRun.RunId);
                        break;
                    case "startQualification":
                        var qualificationStart = Payload<QualificationStartPayload>(payload);
                        responsePayload = await _controller.StartQualificationAsync(
                            qualificationStart.ChatId,
                            qualificationStart.PackId,
                            qualificationStart.PreviousRunId,
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case "advanceQualification":
                        var qualificationAdvance = Payload<QualificationAdvancePayload>(payload);
                        responsePayload = await _controller.AdvanceQualificationAsync(
                            qualificationAdvance.ChatId,
                            qualificationAdvance.RunId,
                            qualificationAdvance.StepId,
                            qualificationAdvance.Acknowledged,
                            qualificationAdvance.Cancel,
                            qualificationAdvance.Note,
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case "createChat":
                        var createChat = Payload<CreateChatPayload>(payload);
                        responsePayload = _controller.CreateChat(createChat.Title);
                        break;
                    case "createDocumentChat":
                        var createDocumentChat = Payload<CreateDocumentChatPayload>(payload);
                        responsePayload = _controller.CreateDocumentChat(
                            createDocumentChat.Title,
                            createDocumentChat.Host,
                            createDocumentChat.DocumentKey,
                            createDocumentChat.DocumentTitle,
                            createDocumentChat.DocumentPath);
                        break;
                    case "selectChat":
                        responsePayload = _controller.SelectChat(Payload<ChatPayload>(payload).ChatId);
                        break;
                    case "openDocument":
                        responsePayload = _controller.OpenDocument(Payload<ChatPayload>(payload).ChatId);
                        break;
                    case "activateDocument":
                        responsePayload = _controller.ActivateDocument(Payload<DocumentPayload>(payload).DocumentKey);
                        break;
                    case "deleteDocument":
                        var deleteDocument = Payload<DocumentPayload>(payload);
                        responsePayload = _controller.DeleteDocument(deleteDocument.Host, deleteDocument.DocumentKey);
                        break;
                    case "renameChat":
                        var renameChat = Payload<RenameChatPayload>(payload);
                        responsePayload = _controller.RenameChat(renameChat.ChatId, renameChat.Title);
                        break;
                    case "setChatModel":
                        var setChatModel = Payload<SetChatModelPayload>(payload);
                        responsePayload = _controller.SetChatModel(setChatModel.ChatId, setChatModel.Model);
                        break;
                    case "setChatMode":
                        var setChatMode = Payload<SetChatModePayload>(payload);
                        responsePayload = _controller.SetChatMode(setChatMode.ChatId, setChatMode.Mode);
                        break;
                    case "setChatReasoning":
                        var setChatReasoning = Payload<SetChatReasoningPayload>(payload);
                        responsePayload = _controller.SetChatReasoning(setChatReasoning.ChatId, setChatReasoning.Enabled == true);
                        break;
                    case "allowHtmlNetworkOrigin":
                        responsePayload = _controller.AllowHtmlNetworkOrigin(Payload<HtmlOriginPayload>(payload).Origin);
                        break;
                    case "htmlFetch":
                        responsePayload = await _controller.HtmlFetchAsync(Payload<HtmlFetchRequest>(payload), cancellationToken);
                        break;
                    case "clearChat":
                        responsePayload = _controller.ClearChat(Payload<ChatPayload>(payload).ChatId);
                        break;
                    case "compactChatContext":
                        var compactChat = Payload<ChatPayload>(payload);
                        responsePayload = await _controller.CompactChatContextAsync(
                            compactChat.ChatId,
                            (phase, message, activity) => ReportProgress(id, compactChat.ChatId, string.Empty, phase, message, activity),
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case "deleteChat":
                        responsePayload = _controller.DeleteChat(Payload<ChatPayload>(payload).ChatId);
                        break;
                    case "sendChat":
                        var sendChat = Payload<SendChatPayload>(payload);
                        var runId = Guid.NewGuid().ToString("N");
                        responsePayload = await _controller.SendChatAsync(
                            sendChat.Text,
                            sendChat.ChatId,
                            sendChat.ResourceDraftIds,
                            (phase, message, activity) => ReportProgress(id, sendChat.ChatId, runId, phase, message, activity),
                            ReportChatState,
                            cancellationToken,
                            runId);
                        break;
                    case "beginChatResourceUpload":
                        responsePayload = _controller.BeginChatResourceUpload(Payload<ResourceUploadOpenRequest>(payload), cancellationToken);
                        break;
                    case "completeChatResourceUpload":
                        responsePayload = await _controller.CompleteChatResourceUploadAsync(Payload<ResourceUploadLeaseRequest>(payload), cancellationToken).ConfigureAwait(false);
                        break;
                    case "cancelChatResourceUpload":
                        responsePayload = _controller.CancelChatResourceUpload(Payload<ResourceUploadLeaseRequest>(payload));
                        break;
                    case "discardChatResourceDraft":
                        var discardResource = Payload<DiscardChatResourceDraftPayload>(payload);
                        responsePayload = _controller.DiscardChatResourceDraft(discardResource.ChatId, discardResource.Id);
                        break;
                    case "deleteMessage":
                        var deleteMessage = Payload<MessageActionPayload>(payload);
                        responsePayload = _controller.DeleteMessage(deleteMessage.Id, deleteMessage.Index ?? -1, deleteMessage.ChatId);
                        break;
                    case "forkChat":
                        var forkChat = Payload<MessageActionPayload>(payload);
                        responsePayload = _controller.ForkChat(forkChat.Id, forkChat.Index ?? -1, forkChat.ChatId);
                        break;
                    case "editMessage":
                        var editMessage = Payload<EditMessagePayload>(payload);
                        var editRunId = Guid.NewGuid().ToString("N");
                        responsePayload = await _controller.EditMessageAsync(
                            editMessage.Text,
                            editMessage.Id,
                            editMessage.Index ?? -1,
                            editMessage.ChatId,
                            (phase, message, activity) => ReportProgress(id, editMessage.ChatId, editRunId, phase, message, activity),
                            ReportChatState,
                            cancellationToken,
                            editRunId);
                        break;
                    case "updateMessageActivityData":
                        var updateActivityData = Payload<UpdateMessageActivityDataPayload>(payload);
                        responsePayload = _controller.UpdateMessageActivityData(
                            updateActivityData.MessageId,
                            updateActivityData.DataJson,
                            updateActivityData.ChatId);
                        break;
                    case "getSettings":
                        responsePayload = _controller.GetSettings();
                        break;
                    case "getRuntimeLog":
                        responsePayload = _controller.GetRuntimeLog();
                        break;
                    case "clearRuntimeLog":
                        responsePayload = _controller.ClearRuntimeLog();
                        break;
                    case "getCasHealth":
                        responsePayload = _controller.GetCasHealth();
                        break;
                    case "collectCasGarbage":
                        responsePayload = _controller.CollectCasGarbage();
                        break;
                    case "getModelCatalog":
                        var modelCatalog = Payload<ModelCatalogPayload>(payload);
                        responsePayload = await _controller.GetModelCatalogAsync(
                            modelCatalog.Settings,
                            modelCatalog.ApiKey);
                        break;
                    case "saveSettings":
                        responsePayload = _controller.SaveSettings(payload.ToObject<SaveSettingsPayload>(JsonSerializer.Create(
                            new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error })), cancellationToken);
                        break;
                    case "readPromptSource":
                        responsePayload = await _controller.ReadPromptSourceAsync(Payload<PromptSourceReadRequest>(payload), cancellationToken).ConfigureAwait(false);
                        break;
                    case "beginPromptMutationUpload":
                        responsePayload = _controller.BeginPromptMutationUpload(Payload<PromptMutationUploadRequest>(payload), cancellationToken);
                        break;
                    case "cancelPromptMutationUpload":
                        responsePayload = _controller.CancelPromptMutationUpload(Payload<ResourceUploadLeaseRequest>(payload));
                        break;
                    case "testModelCompatibility":
                        responsePayload = await _controller.TestModelCompatibilityAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case "testModelConnection":
                        responsePayload = await _controller.TestModelConnectionAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case "clearRuntimeData":
                        responsePayload = WithBridgeToken(_controller.ClearRuntimeData());
                        break;
                    case "getTools":
                        responsePayload = _controller.GetTools();
                        break;
                    case "getToolDocumentation":
                        responsePayload = await _controller.GetToolDocumentationAsync(
                            Payload<ToolLibraryDocumentationRequest>(payload), cancellationToken).ConfigureAwait(false);
                        break;
                    case "saveTools":
                        responsePayload = await _controller.SaveToolsAsync(Payload<ToolMutationWriteRequest>(payload), cancellationToken).ConfigureAwait(false);
                        break;
                    case "readToolSource":
                        responsePayload = await _controller.ReadToolSourceAsync(Payload<ToolSourceReadRequest>(payload), cancellationToken).ConfigureAwait(false);
                        break;
                    case "beginToolMutationUpload":
                        responsePayload = _controller.BeginToolMutationUpload(Payload<ToolMutationUploadRequest>(payload), cancellationToken);
                        break;
                    case "cancelToolMutationUpload":
                        responsePayload = _controller.CancelToolMutationUpload(Payload<ResourceUploadLeaseRequest>(payload));
                        break;
                    case "installVbaTool":
                        var installVbaTool = Payload<VbaToolPackagePayload>(payload);
                        responsePayload = _controller.InstallVbaTool(installVbaTool.Id, installVbaTool.DryRun);
                        break;
                    case "uninstallVbaTool":
                        responsePayload = _controller.UninstallVbaTool(Payload<VbaToolPackagePayload>(payload).Id);
                        break;
                    case "getSkills":
                        responsePayload = _controller.GetSkills();
                        break;
                    case "saveSkills":
                        responsePayload = await _controller.SaveSkillsAsync(Payload<SkillMutationWriteRequest>(payload), cancellationToken).ConfigureAwait(false);
                        break;
                    case "beginSkillMutationUpload":
                        responsePayload = _controller.BeginSkillMutationUpload(Payload<SkillMutationUploadRequest>(payload), cancellationToken);
                        break;
                    case "cancelSkillMutationUpload":
                        responsePayload = _controller.CancelSkillMutationUpload(Payload<ResourceUploadLeaseRequest>(payload));
                        break;
                    case "readSkillSource":
                        responsePayload = await _controller.ReadSkillSourceAsync(Payload<SkillSourceReadRequest>(payload), cancellationToken).ConfigureAwait(false);
                        break;
                    case "saveSkillReference":
                        responsePayload = await _controller.SaveSkillReferenceAsync(Payload<SkillMutationWriteRequest>(payload), cancellationToken).ConfigureAwait(false);
                        break;
                    case "deleteSkillReference":
                        var deleteSkillReference = Payload<SkillReferencePayload>(payload);
                        responsePayload = _controller.DeleteSkillReference(
                            deleteSkillReference);
                        break;
                    case "runTool":
                        var runTool = Payload<RunToolPayload>(payload);
                        responsePayload = _controller.RunTool(
                            runTool.ToolId,
                            ToArguments(runTool.Arguments),
                            runTool.DryRun,
                            (phase, message) => ReportProgress(id, phase, message),
                            cancellationToken);
                        break;
                    case "confirmAgentTool":
                        var confirmAgentTool = Payload<PendingAgentToolPayload>(payload);
                        var confirmRunId = Guid.NewGuid().ToString("N");
                        responsePayload = await _controller.ConfirmAgentToolAsync(
                            confirmAgentTool.PendingId,
                            confirmAgentTool.ChatId,
                            (phase, message, activity) => ReportProgress(id, confirmAgentTool.ChatId, confirmRunId, phase, message, activity),
                            cancellationToken,
                            confirmRunId,
                            ReportChatState);
                        break;
                    case "cancelAgentTool":
                        var cancelAgentTool = Payload<PendingAgentToolPayload>(payload);
                        responsePayload = _controller.CancelAgentTool(cancelAgentTool.PendingId, cancelAgentTool.ChatId);
                        break;
                    case "getVbaProject":
                        responsePayload = _controller.GetVbaProject();
                        break;
                    case "getVbaModule":
                        responsePayload = await _controller.GetVbaModuleAsync(Payload<VbaEditorReadRequest>(payload), cancellationToken).ConfigureAwait(false);
                        break;
                    case "getVbaMutations":
                        responsePayload = _controller.GetVbaMutations(Payload<VbaMutationQueryPayload>(payload));
                        break;
                    case "getVbaMutationDetail":
                        responsePayload = _controller.GetVbaMutationDetail(
                            Payload<VbaMutationDetailPayload>(payload).MutationId);
                        break;
                    case "saveVbaModule":
                        responsePayload = await _controller.SaveVbaModuleAsync(Payload<VbaModulePayload>(payload), cancellationToken).ConfigureAwait(false);
                        break;
                    case "createVbaModule":
                        responsePayload = await _controller.CreateVbaModuleAsync(Payload<VbaCreateModulePayload>(payload), cancellationToken).ConfigureAwait(false);
                        break;
                    case "beginVbaModuleUpload":
                        responsePayload = _controller.BeginVbaModuleUpload(Payload<VbaEditorUploadRequest>(payload), cancellationToken);
                        break;
                    case "cancelVbaModuleUpload":
                        responsePayload = _controller.CancelVbaModuleUpload(Payload<ResourceUploadLeaseRequest>(payload));
                        break;
                    case "deleteVbaModule":
                        var deleteVbaModule = Payload<VbaDeleteModulePayload>(payload);
                        responsePayload = _controller.DeleteVbaModule(deleteVbaModule.ModuleName);
                        break;
                    case "restoreVbaBackup":
                        var restoreVba = Payload<RestoreVbaBackupPayload>(payload);
                        responsePayload = _controller.RestoreVbaBackup(restoreVba.BackupId, restoreVba.ModuleName);
                        break;
                    case "runVbaMacro":
                        responsePayload = _controller.RunVbaMacro(
                            Payload<RunVbaMacroPayload>(payload).MacroName,
                            cancellationToken);
                        break;
                    case "resourceDataOpen":
                        responsePayload = _controller.OpenResourceData(Payload<ResourceDataOpenRequest>(payload), cancellationToken);
                        break;
                    case "resourceDataClose":
                        responsePayload = _controller.CloseResourceData(Payload<ResourceDataCloseRequest>(payload));
                        break;
                    case "getHtmlWorkspace":
                        responsePayload = _controller.GetHtmlWorkspace(Payload<ChatPayload>(payload).ChatId);
                        break;
                    case "readHtmlWorkspaceSource":
                        responsePayload = await _controller.ReadHtmlWorkspaceSourceAsync(Payload<HtmlWorkspaceSourceRequest>(payload), cancellationToken).ConfigureAwait(false);
                        break;
                    case "saveHtmlWorkspaceFile":
                        responsePayload = _controller.SaveHtmlWorkspaceFile(
                            payload.ToObject<HtmlWorkspaceFilePayload>(JsonSerializer.Create(
                                new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error })), cancellationToken);
                        break;
                    case "saveHtmlWorkspaceData":
                        responsePayload = _controller.SaveHtmlWorkspaceData(
                            payload.ToObject<HtmlWorkspaceDataPayload>(JsonSerializer.Create(
                                new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error })), cancellationToken);
                        break;
                    case "beginHtmlWorkspaceMutationUpload":
                        responsePayload = _controller.BeginHtmlWorkspaceMutationUpload(Payload<HtmlWorkspaceMutationUploadRequest>(payload), cancellationToken);
                        break;
                    case "cancelHtmlWorkspaceMutationUpload":
                        responsePayload = _controller.CancelHtmlWorkspaceMutationUpload(Payload<ResourceUploadLeaseRequest>(payload));
                        break;
                    case "readArtifactViewerPage":
                        var artifactViewerPage = Payload<ArtifactViewerPagePayload>(payload);
                        responsePayload = _controller.ReadArtifactViewerPage(
                            artifactViewerPage.ChatId,
                            artifactViewerPage.ResourceUri,
                            artifactViewerPage.Cursor, cancellationToken);
                        break;
                    case "readArtifactImage":
                        var artifactImage = Payload<ArtifactImageViewerPayload>(payload);
                        responsePayload = _controller.ReadArtifactImage(
                            artifactImage.ChatId,
                            artifactImage.ResourceUri, cancellationToken);
                        break;
                    case "readArtifactImageThumbnail":
                        var artifactImageThumbnail = Payload<ArtifactImageThumbnailPayload>(payload);
                        responsePayload = _controller.ReadArtifactImageThumbnail(
                            artifactImageThumbnail.ChatId,
                            artifactImageThumbnail.ResourceUri, cancellationToken);
                        break;
                    case "readArtifactPdfInfo":
                        var artifactPdf = Payload<ArtifactPdfViewerPayload>(payload);
                        responsePayload = _controller.ReadArtifactPdfInfo(
                            artifactPdf.ChatId,
                            artifactPdf.ResourceUri);
                        break;
                    case "readArtifactPdfPage":
                        var artifactPdfPage = Payload<ArtifactPdfPagePayload>(payload);
                        responsePayload = _controller.ReadArtifactPdfPage(
                            artifactPdfPage.ChatId,
                            artifactPdfPage.ResourceUri,
                            artifactPdfPage.PageIndex, cancellationToken);
                        break;
                    case "readArtifactPdfThumbnail":
                        var artifactPdfThumbnail = Payload<ArtifactPdfPagePayload>(payload);
                        responsePayload = _controller.ReadArtifactPdfThumbnail(
                            artifactPdfThumbnail.ChatId,
                            artifactPdfThumbnail.ResourceUri,
                            artifactPdfThumbnail.PageIndex, cancellationToken);
                        break;
                    case "importUploadedHtmlToWorkspace":
                        var htmlImport = Payload<HtmlWorkspaceImportPayload>(payload);
                        responsePayload = _controller.ImportUploadedHtmlToWorkspace(
                            htmlImport.ChatId,
                            htmlImport.SourceResourceUri,
                            htmlImport.ExpectedActiveHtmlArtifactId,
                            htmlImport.TargetPath);
                        break;
                    case "prepareHtmlWorkspaceExport":
                        var htmlExport = Payload<HtmlWorkspaceExportPayload>(payload);
                        responsePayload = _controller.PrepareHtmlWorkspaceExport(
                            htmlExport.ChatId,
                            htmlExport.ExpectedActiveHtmlArtifactId, cancellationToken);
                        break;
                    case "deleteHtmlWorkspaceFile":
                        var deleteHtmlFile = Payload<HtmlWorkspaceDeleteFilePayload>(payload);
                        responsePayload = _controller.DeleteHtmlWorkspaceFile(
                            deleteHtmlFile.ChatId,
                            deleteHtmlFile.Path);
                        break;
                    case "deleteHtmlWorkspaceData":
                        var deleteHtmlData = Payload<HtmlWorkspaceDeleteDataPayload>(payload);
                        responsePayload = _controller.DeleteHtmlWorkspaceData(
                            deleteHtmlData.ChatId,
                            deleteHtmlData.Name);
                        break;
                    case "setActiveHtmlWorkspaceFile":
                        var htmlActive = Payload<HtmlWorkspaceActiveFilePayload>(payload);
                        responsePayload = _controller.SetActiveHtmlWorkspaceFile(
                            htmlActive.ChatId,
                            htmlActive.Path);
                        break;
                    case "restoreHtmlWorkspaceSnapshot":
                        var htmlRestore = Payload<HtmlWorkspaceRestorePayload>(payload);
                        responsePayload = _controller.RestoreHtmlWorkspaceSnapshot(
                            htmlRestore.ChatId,
                            htmlRestore.SnapshotId);
                        break;
                    case "redoHtmlWorkspaceSnapshot":
                        var htmlRedo = Payload<HtmlWorkspaceRestorePayload>(payload);
                        responsePayload = _controller.RedoHtmlWorkspaceSnapshot(
                            htmlRedo.ChatId,
                            htmlRedo.SnapshotId);
                        break;
                    case "getContext":
                        responsePayload = _controller.GetContext(Payload<ChatPayload>(payload).ChatId);
                        break;
                    case "inspectPromptContext":
                        var inspectContext = Payload<PromptContextInspectorPayload>(payload);
                        responsePayload = _controller.InspectPromptContext(
                            inspectContext.ChatId,
                            inspectContext.Text,
                            inspectContext.ResourceDraftIds,
                            inspectContext.IncludeRaw);
                        break;
                    case "addSelectionContext":
                        var selectionContext = Payload<SelectionContextPayload>(payload);
                        responsePayload = _controller.AddSelectionContextFromBridge(selectionContext.Mode, selectionContext.ChatId);
                        break;
                    case "addTextContext":
                        var textContext = Payload<TextContextPayload>(payload);
                        responsePayload = _controller.AddTextContext(
                            textContext.Role,
                            textContext.Kind,
                            textContext.Title,
                            textContext.Reference,
                            textContext.Text,
                            textContext.DetailsJson,
                            textContext.ChatId);
                        break;
                    case "removeContextItem":
                        var removeContextItem = Payload<RemoveContextItemPayload>(payload);
                        responsePayload = _controller.RemoveContextItem(removeContextItem.Id, removeContextItem.ChatId);
                        break;
                    case "clearContext":
                        responsePayload = _controller.ClearContext(Payload<ChatPayload>(payload).ChatId);
                        break;
                    case "quickAction":
                        responsePayload = await _controller.RunQuickActionAsync(Payload<QuickActionPayload>(payload).Action);
                        break;
                    default:
                        throw new InvalidOperationException("Unknown bridge message: " + type);
                }

                return Success(id, responsePayload);
            }
            catch (OperationCanceledException ex)
            {
                return Serialize(new BridgeResponse
                {
                    Id = id,
                    Ok = false,
                    Error = "Request cancelled.",
                    ErrorDetail = string.IsNullOrWhiteSpace(ex.Message) ? "Request cancelled." : ex.Message,
                    Cancelled = true
                });
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("WebView bridge request failed.", ex);
                return Serialize(new BridgeResponse
                {
                    Id = id,
                    Ok = false,
                    Error = ex.Message,
                    ErrorDetail = "Request failed. See the local runtime log for details."
                });
            }
            finally
            {
                _cancellations.Release(id, cancellationSource);
            }
        }

        private static bool RequiresBridgeToken(string type)
        {
            return !string.Equals(type, "init", StringComparison.OrdinalIgnoreCase);
        }

        private InitResponse WithBridgeToken(InitResponse response)
        {
            if (response != null)
            {
                response.BridgeToken = _bridgeToken;
            }

            return response;
        }

        public void Dispose()
        {
            _controller.ModelRequestDiagnostics -= ReportModelRequestDiagnostics;
            _controller.ResourceAuthorityChanged -= ReportResourceChanged;
            lock (_resourceChangesSync)
            {
                _resourceChangesDisposed = true;
                _resourceChanges.Clear();
                _resourceChangesTimer.Dispose();
            }
            _cancellations.Dispose();
        }

        private static string Success(string id, object payload)
        {
            return Serialize(new BridgeResponse
            {
                Id = id,
                Ok = true,
                Payload = ToPayloadToken(payload)
            });
        }

        private static string Serialize(BridgeResponse response)
        {
            return JsonConvert.SerializeObject(response);
        }

        private void ReportProgress(string id, string phase, string message)
        {
            ReportProgress(id, null, null, phase, message, null);
        }

        private void ReportProgress(string id, string phase, string message, ChatActivity activity)
        {
            ReportProgress(id, null, activity == null ? null : activity.RunId, phase, message, activity);
        }

        private void ReportProgress(string id, string chatId, string runId, string phase, string message, ChatActivity activity)
        {
            if (_postMessageJson == null)
            {
                return;
            }

            var streaming = string.Equals(phase, "streaming", StringComparison.OrdinalIgnoreCase);
            var streamReset = streaming && string.IsNullOrEmpty(message);
            var reasoning = activity != null && string.Equals(activity.Kind, "reasoning", StringComparison.OrdinalIgnoreCase);
            _postMessageJson(JsonConvert.SerializeObject(new ProgressMessage
            {
                Type = "progress",
                Id = id,
                Payload = new ProgressPayload
                {
                    ChatId = chatId,
                    RunId = runId,
                    Phase = phase,
                    Message = streaming ? null : message,
                    Activity = reasoning ? null : activity,
                    ContentDelta = streaming && !streamReset ? message : null,
                    ContentReset = streamReset ? (bool?)true : null,
                    ReasoningReset = streamReset ? (bool?)true : null,
                    ReasoningDelta = reasoning ? activity.ResultMessage : null,
                    ReasoningComplete = reasoning
                        ? (bool?)(string.Equals(activity.Status, "completed", StringComparison.OrdinalIgnoreCase))
                        : null
                }
            }));
        }

        private void ReportResourceChanged(object sender, ResourceAuthorityChangedEventArgs change)
        {
            if (_postMessageJson == null || change == null) return;
            ResourceChangedMessage[] overflow = null;
            lock (_resourceChangesSync)
            {
                if (_resourceChangesDisposed) return;
                var scope = change.ScopeId.ToString();
                ResourceChangedMessage pending;
                if (!_resourceChanges.TryGetValue(scope, out pending))
                {
                    if (_resourceChanges.Count >= 16) { overflow = _resourceChanges.Values.ToArray(); _resourceChanges.Clear(); }
                    pending = new ResourceChangedMessage { Scope = scope, Resources = new string[0] };
                    if (_resourceChanges.Count == 0) _resourceChangesTimer.Change(100, Timeout.Infinite);
                    _resourceChanges.Add(scope, pending);
                }
                var resources = pending.Resources.Concat(change.AffectedResources.Select(item => item.Uri)).Distinct().Take(65).ToArray();
                pending.AllInScope |= resources.Length > 64;
                pending.Resources = pending.AllInScope ? new string[0] : resources;
                if (change.Generation >= pending.Generation) { pending.Generation = change.Generation; pending.CommitId = change.CommitId; }
            }
            if (overflow != null) PostResourceChanges(overflow);
        }

        private void FlushResourceChanges()
        {
            ResourceChangedMessage[] messages;
            lock (_resourceChangesSync)
            {
                if (_resourceChangesDisposed) return;
                messages = _resourceChanges.Values.ToArray(); _resourceChanges.Clear();
            }
            PostResourceChanges(messages);
        }

        private void PostResourceChanges(IEnumerable<ResourceChangedMessage> messages)
        {
            foreach (var message in messages)
                try { _postMessageJson(JsonConvert.SerializeObject(message)); }
                catch { /* Advisory only; fresh requests still capture shared authority. */ }
        }

        private void ReportModelRequestDiagnostics(LlmRequestDiagnosticUpdate update)
        {
            if (_postMessageJson == null || update == null)
            {
                return;
            }

            _postMessageJson(JsonConvert.SerializeObject(new ModelRequestDiagnosticsMessage
            {
                Type = "modelDiagnostics",
                Payload = ModelRequestDiagnosticsDto.From(update)
            }));
        }

        private void ReportChatState(ChatStateResponse state)
        {
            if (_postMessageJson == null)
            {
                return;
            }

            _postMessageJson(JsonConvert.SerializeObject(new ChatStateMessage
            {
                Type = "chatState",
                Scope = state != null && state.Messages != null ? "full" : "catalog",
                Payload = state
            }));
        }

        private static T Payload<T>(JToken payload) where T : class, new()
        {
            return payload == null ? new T() : (payload.ToObject<T>() ?? new T());
        }

        private static JToken ToPayloadToken(object payload)
        {
            if (payload == null)
            {
                return JValue.CreateNull();
            }

            var token = payload as JToken;
            return token ?? JToken.FromObject(payload);
        }

        private static Dictionary<string, object> ToArguments(IDictionary<string, object> arguments)
        {
            return ToolArgumentNormalizer.NormalizeDictionary(arguments);
        }
    }
}
