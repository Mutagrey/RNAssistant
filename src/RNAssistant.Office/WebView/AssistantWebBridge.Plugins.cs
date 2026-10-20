using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;
namespace RNAssistant.Office.WebView
{
    public sealed partial class AssistantWebBridge
    {
        public PluginApplicationService Plugins { get; set; }
        private Task<object> HandlePluginAsync(BridgeOperationId operation, JToken payload, CancellationToken token)
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
                    case BridgeOperationId.DrawPluginIcon: return await Plugins.DrawIconAsync(Payload<PluginIconDrawRequest>(payload),settings.Load(),settings.LoadApiKey,token);
                    case BridgeOperationId.CreatePlugin: return Plugins.Create(Payload<PluginCreateRequest>(payload));
                    case BridgeOperationId.ConnectPlugin: return Plugins.Connect(Payload<PluginConnectRequest>(payload));
                    case BridgeOperationId.GetPluginSourceState: return Plugins.SourceState(Payload<PluginIdRequest>(payload).PluginId);
                    case BridgeOperationId.CheckPlugin: return Plugins.Check(Payload<PluginIdRequest>(payload).PluginId);
                    case BridgeOperationId.ApplyPlugin: return Plugins.Apply(Payload<PluginApplyRequest>(payload));
                    case BridgeOperationId.OpenPlugin:
                        if(Plugins.Executor==null)throw new PluginException("plugins_unavailable",Plugins.StartupError ?? "Plugin browser host is unavailable.");
                        return Plugins.Open(Payload<PluginOpenRequest>(payload));
                    case BridgeOperationId.ClosePlugin: await Plugins.CloseInstanceAsync(Payload<PluginCloseRequest>(payload).InstanceId); return new PluginFrameResult();
                    case BridgeOperationId.DisablePlugin: return await Plugins.DisableAsync(Payload<PluginIdRequest>(payload).PluginId);
                    case BridgeOperationId.RemovePlugin: return await Plugins.RemoveAsync(Payload<PluginIdRequest>(payload).PluginId);
                    case BridgeOperationId.RestorePlugin: return Plugins.Restore(Payload<PluginIdRequest>(payload).PluginId);
                    case BridgeOperationId.GetPluginChat: return Plugins.ChatState(Payload<PluginIdRequest>(payload).PluginId);
                    case BridgeOperationId.SubmitPluginChat: return await Plugins.SubmitChatAsync(Payload<PluginChatRequest>(payload),settings.Load(),settings.LoadApiKey,token);
                    case BridgeOperationId.ResolvePluginChat: case BridgeOperationId.CancelPluginChat:
                        return await Plugins.ControlChatAsync(Payload<PluginChatControlRequest>(payload),operation==BridgeOperationId.CancelPluginChat,settings.Load(),settings.LoadApiKey,token);
                    case BridgeOperationId.GetPluginVendors: return new PluginVendorCatalogDto {Items=Plugins.Vendors.List()};
                    case BridgeOperationId.ImportPluginVendor: return Plugins.Vendors.Import(Payload<PluginConnectRequest>(payload).RootPath);
                    case BridgeOperationId.ResolveHtmlVendor: return Plugins.ResolveHtmlVendor(Payload<PluginVendorRequest>(payload));
                    case BridgeOperationId.GetProjectChats: return Plugins.ProjectChats();
                    case BridgeOperationId.ConnectProjectWorkspace: return Plugins.ConnectProjectWorkspace(Payload<ProjectConnectionRequest>(payload));
                    case BridgeOperationId.CreateProjectWorkspace: return Plugins.CreateProjectWorkspace(Payload<ProjectConnectionRequest>(payload));
                    case BridgeOperationId.CreateProjectChat: return Plugins.CreateProjectChat(Payload<ProjectChatRequest>(payload));
                    case BridgeOperationId.GetProjectChat: return Plugins.ProjectChat(Payload<ProjectChatRequest>(payload));
                    case BridgeOperationId.RenameProjectChat: return Plugins.RenameProjectChat(Payload<ProjectChatRequest>(payload));
                    case BridgeOperationId.DeleteProjectChat: return Plugins.DeleteProjectChat(Payload<ProjectChatRequest>(payload));
                    case BridgeOperationId.DeleteProjectHistory: return Plugins.DeleteProjectHistory(Payload<ProjectChatRequest>(payload));
                    case BridgeOperationId.SubmitProjectChat: return await Plugins.SubmitProjectChatAsync(Payload<ProjectChatRequest>(payload),settings.Load(),settings.LoadApiKey,token);
                    case BridgeOperationId.ResolveProjectChat: case BridgeOperationId.CancelProjectChat:
                        return await Plugins.ControlProjectChatAsync(Payload<ProjectChatRequest>(payload),operation==BridgeOperationId.CancelProjectChat,settings.Load(),settings.LoadApiKey,token);
                    default:throw new InvalidOperationException("Unknown plugin bridge operation.");
                }
            },token);
        }
    }
}
