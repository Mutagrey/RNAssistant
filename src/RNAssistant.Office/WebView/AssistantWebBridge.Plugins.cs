using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Runtime;
namespace RNAssistant.Office.WebView
{
    public sealed partial class AssistantWebBridge
    {
        public PluginApplicationService Plugins { get; set; }
        private Task<object> HandlePluginAsync(string id, BridgeOperationId operation, JToken payload, CancellationToken token)
        {
            if(Plugins==null)throw new PluginException("plugins_unavailable","Plugin host is unavailable.");
            return Task.Run(async ()=> {
                token.ThrowIfCancellationRequested();
                var settings=new SettingsService(_controller.StoragePaths);
                switch(operation)
                {
                    case BridgeOperationId.GetPlugins: return (object)Plugins.List();
                    case BridgeOperationId.GetRemovedPlugins: return Plugins.ListRemoved();
                    case BridgeOperationId.GetPluginIcons: return Plugins.ListIcons();
                    case BridgeOperationId.SavePluginIcon: return Plugins.SaveIcon(Payload<PluginIconSaveRequest>(payload));
                    case BridgeOperationId.DeletePluginIcon: return Plugins.DeleteIcon(Payload<PluginIconDeleteRequest>(payload));
                    case BridgeOperationId.UpdatePluginAppearance: return Plugins.UpdateAppearance(Payload<PluginAppearanceRequest>(payload));
                    case BridgeOperationId.DrawPluginIcon: return await Plugins.DrawIconAsync(Payload<PluginIconDrawRequest>(payload),settings.Load(),settings.LoadApiKey,token);
                    case BridgeOperationId.CreatePlugin: return Plugins.Create(Payload<PluginCreateRequest>(payload));
                    case BridgeOperationId.ConnectPlugin: return Plugins.Connect(Payload<PluginConnectRequest>(payload));
                    case BridgeOperationId.GetPluginSourceState: return Plugins.SourceState(Payload<PluginIdRequest>(payload).PluginId);
                    case BridgeOperationId.CheckPlugin: return Plugins.Check(Payload<PluginIdRequest>(payload).PluginId);
                    case BridgeOperationId.ApplyPlugin: return Plugins.Apply(Payload<PluginApplyRequest>(payload));
                    case BridgeOperationId.GetPluginVersions: return Plugins.ReleaseHistory(Payload<PluginReleaseHistoryRequest>(payload));
                    case BridgeOperationId.RestorePluginVersion: return Plugins.RestoreVersion(Payload<PluginRestoreVersionRequest>(payload));
                    case BridgeOperationId.OpenPlugin:
                        if(Plugins.Executor==null)throw new PluginException("plugins_unavailable",Plugins.StartupError ?? "Plugin browser host is unavailable.");
                        return Plugins.Open(Payload<PluginOpenRequest>(payload));
                    case BridgeOperationId.ClosePlugin: await Plugins.CloseInstanceAsync(Payload<PluginCloseRequest>(payload).InstanceId); return new PluginFrameResult();
                    case BridgeOperationId.StagePluginVisualSelection:
                        return Plugins.StageVisualSelection(Payload<PluginVisualSelectionRequest>(payload));
                    case BridgeOperationId.DiscardPluginVisualSelection:
                        return Plugins.DiscardVisualSelection(Payload<PluginVisualDraftRequest>(payload));
                    case BridgeOperationId.DisablePlugin: return await Plugins.DisableAsync(Payload<PluginIdRequest>(payload).PluginId);
                    case BridgeOperationId.RemovePlugin: return await Plugins.RemoveAsync(Payload<PluginIdRequest>(payload).PluginId);
                    case BridgeOperationId.RestorePlugin: return Plugins.Restore(Payload<PluginIdRequest>(payload).PluginId);
                    case BridgeOperationId.GetPluginChat: return Plugins.ChatState(Payload<PluginIdRequest>(payload).PluginId);
                    case BridgeOperationId.GetPluginVendors: return new PluginVendorCatalogDto {Items=Plugins.Vendors.List()};
                    case BridgeOperationId.ImportPluginVendor: return Plugins.Vendors.Import(Payload<PluginConnectRequest>(payload).RootPath);
                    case BridgeOperationId.ResolveHtmlVendor: return Plugins.ResolveHtmlVendor(Payload<PluginVendorRequest>(payload));
                    case BridgeOperationId.GetProjectChats: return Plugins.ProjectChats();
                    case BridgeOperationId.ConnectProjectWorkspace: return Plugins.ConnectProjectWorkspace(Payload<ProjectConnectionRequest>(payload));
                    case BridgeOperationId.CreateProjectWorkspace: return Plugins.CreateProjectWorkspace(Payload<ProjectConnectionRequest>(payload));
                    case BridgeOperationId.CreateProjectChat: return Plugins.CreateProjectChat(Payload<ProjectChatRequest>(payload));
                    case BridgeOperationId.ForkProjectChat: return Plugins.ForkProjectChat(Payload<ProjectChatRequest>(payload));
                    case BridgeOperationId.DeleteProjectChatMessage: return Plugins.DeleteProjectChatMessage(Payload<ProjectChatRequest>(payload));
                    case BridgeOperationId.GetProjectChat: return Plugins.ProjectChat(Payload<ProjectChatRequest>(payload));
                    case BridgeOperationId.GetPreviousProjectChatMessages: return Plugins.ProjectChatMessages(Payload<ProjectChatHistoryRequest>(payload));
                    case BridgeOperationId.GetProjectToolResultPresentation:
                        var presentationRequest = Payload<ProjectToolResultPresentationRequest>(payload);
                        var presentationSession = Plugins.ProjectChatSessionForPresentation(
                            presentationRequest.WorkspaceId, presentationRequest.SessionId);
                        var presentation = new ToolResultPresentationService(null, new ChatBlobStore(_controller.StoragePaths))
                            .Read(presentationSession, presentationRequest.RunId, presentationRequest.ToolCallId);
                        presentation.WorkspaceId = presentationRequest.WorkspaceId;
                        return presentation;
                    case BridgeOperationId.RenameProjectChat: return Plugins.RenameProjectChat(Payload<ProjectChatRequest>(payload));
                    case BridgeOperationId.DeleteProjectChat:
                        var deletedChat = Payload<ProjectChatRequest>(payload);
                        var chatCatalog = Plugins.DeleteProjectChat(deletedChat);
                        new ProtectedChatDraftStore(_controller.StoragePaths).DeleteWorkspace(deletedChat.WorkspaceId, deletedChat.SessionId);
                        return chatCatalog;
                    case BridgeOperationId.DeleteProjectHistory:
                        var deletedHistory = Payload<ProjectChatRequest>(payload);
                        var historyCatalog = Plugins.DeleteProjectHistory(deletedHistory);
                        new ProtectedChatDraftStore(_controller.StoragePaths).DeleteWorkspace(deletedHistory.WorkspaceId);
                        return historyCatalog;
                    case BridgeOperationId.SubmitProjectChat:
                        var submit = Payload<ProjectChatRequest>(payload);
                        return await Plugins.SubmitProjectChatAsync(submit, settings.Load(), settings.LoadApiKey, token,
                            progress => ReportWorkspaceProgress(id, submit, progress));
                    case BridgeOperationId.BeginProjectChatFileUpload: return Plugins.BeginProjectChatFileUpload(Payload<ProjectChatUploadOpenRequest>(payload));
                    case BridgeOperationId.WriteProjectChatFileChunk: return Plugins.WriteProjectChatFileChunk(Payload<ProjectChatUploadChunkRequest>(payload));
                    case BridgeOperationId.CompleteProjectChatFileUpload: return Plugins.CompleteProjectChatFileUpload(Payload<ProjectChatUploadLeaseRequest>(payload),token);
                    case BridgeOperationId.CancelProjectChatFileUpload: return Plugins.CancelProjectChatFileUpload(Payload<ProjectChatUploadLeaseRequest>(payload));
                    case BridgeOperationId.ResolveProjectChat: case BridgeOperationId.CancelProjectChat:
                        var control = Payload<ProjectChatRequest>(payload);
                        return await Plugins.ControlProjectChatAsync(control, operation==BridgeOperationId.CancelProjectChat,
                            settings.Load(), settings.LoadApiKey, token,
                            progress => ReportWorkspaceProgress(id, control, progress));
                    default:throw new InvalidOperationException("Unknown plugin bridge operation.");
                }
            },token);
        }

        private void ReportWorkspaceProgress(string id, ProjectChatRequest request, WorkspaceRunEvent progress)
        {
            if (_postMessageJson == null || progress == null ||
                progress.ModelProgress == null && progress.Receipt == null) return;
            _postMessageJson(Newtonsoft.Json.JsonConvert.SerializeObject(new ProgressMessage
            {
                Type = "progress", Id = id,
                Payload = new ProgressPayload { WorkspaceId = request.WorkspaceId, SessionId = request.SessionId,
                    Phase = progress.Receipt == null ? "model_progress" : "input_receipt",
                    RunId = progress.Receipt?.RunId ?? progress.ModelProgress?.RunId,
                    ModelProgress = progress.ModelProgress, InputReceipt = progress.Receipt }
            }));
        }
    }
}
