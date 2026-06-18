using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.WebView
{
    public sealed class AssistantWebBridge
    {
        private readonly AssistantController _controller;
        private readonly Action<string> _postMessageJson;

        public AssistantWebBridge(AssistantController controller, Action<string> postMessageJson)
        {
            _controller = controller;
            _postMessageJson = postMessageJson;
        }

        public async Task<string> HandleMessageAsync(string requestJson)
        {
            string id = null;
            try
            {
                var request = JsonConvert.DeserializeObject<BridgeRequest>(requestJson) ?? new BridgeRequest();
                id = request.Id;
                var type = (request.Type ?? string.Empty).Trim();
                var payload = request.Payload ?? new JObject();
                object responsePayload;

                switch (type)
                {
                    case "init":
                        responsePayload = _controller.Initialize();
                        break;
                    case "listChats":
                        responsePayload = _controller.ListChats();
                        break;
                    case "createChat":
                        var createChat = Payload<CreateChatPayload>(payload);
                        responsePayload = _controller.CreateChat(createChat.Title);
                        break;
                    case "selectChat":
                        responsePayload = _controller.SelectChat(Payload<ChatPayload>(payload).ChatId);
                        break;
                    case "renameChat":
                        var renameChat = Payload<RenameChatPayload>(payload);
                        responsePayload = _controller.RenameChat(renameChat.ChatId, renameChat.Title);
                        break;
                    case "setChatModel":
                        var setChatModel = Payload<SetChatModelPayload>(payload);
                        responsePayload = _controller.SetChatModel(setChatModel.ChatId, setChatModel.Model);
                        break;
                    case "clearChat":
                        responsePayload = _controller.ClearChat(Payload<ChatPayload>(payload).ChatId);
                        break;
                    case "deleteChat":
                        responsePayload = _controller.DeleteChat(Payload<ChatPayload>(payload).ChatId);
                        break;
                    case "sendChat":
                        var sendChat = Payload<SendChatPayload>(payload);
                        responsePayload = await _controller.SendChatAsync(sendChat.Text, sendChat.ChatId, (phase, message, activity) => ReportProgress(id, phase, message, activity));
                        break;
                    case "deleteMessage":
                        var deleteMessage = Payload<MessageActionPayload>(payload);
                        responsePayload = _controller.DeleteMessage(deleteMessage.Id, deleteMessage.Index ?? -1, deleteMessage.ChatId);
                        break;
                    case "forkChat":
                        var forkChat = Payload<MessageActionPayload>(payload);
                        responsePayload = _controller.ForkChat(forkChat.Id, forkChat.Index ?? -1, forkChat.ChatId);
                        break;
                    case "getSettings":
                        responsePayload = _controller.GetSettings();
                        break;
                    case "getModelCatalog":
                        var modelCatalog = Payload<ModelCatalogPayload>(payload);
                        responsePayload = await _controller.GetModelCatalogAsync(
                            modelCatalog.Settings == null ? null : modelCatalog.Settings.ToObject<AppSettings>(),
                            modelCatalog.ApiKey);
                        break;
                    case "saveSettings":
                        var saveSettings = Payload<SaveSettingsPayload>(payload);
                        responsePayload = _controller.SaveSettings(
                            saveSettings.Settings == null ? new AppSettings() : saveSettings.Settings.ToObject<AppSettings>(),
                            saveSettings.ApiKey);
                        break;
                    case "clearRuntimeData":
                        responsePayload = _controller.ClearRuntimeData();
                        break;
                    case "getTools":
                        responsePayload = _controller.GetTools();
                        break;
                    case "saveTools":
                        var saveTools = Payload<SaveToolsPayload>(payload);
                        var toolsToSave = saveTools.Tools == null
                            ? (IEnumerable<SkillDefinition>)new SkillDefinition[0]
                            : saveTools.Tools.ToObject<List<SkillDefinition>>();
                        responsePayload = _controller.SaveTools(toolsToSave);
                        break;
                    case "runTool":
                        var runTool = Payload<RunToolPayload>(payload);
                        responsePayload = _controller.RunTool(
                            runTool.ToolId,
                            ToArguments(runTool.Arguments),
                            runTool.DryRun,
                            (phase, message) => ReportProgress(id, phase, message));
                        break;
                    case "getVbaProject":
                        responsePayload = _controller.GetVbaProject(Payload<VbaProjectPayload>(payload).MaxChars ?? 30000);
                        break;
                    case "saveVbaModule":
                        var saveVbaModule = Payload<VbaModulePayload>(payload);
                        responsePayload = _controller.SaveVbaModule(saveVbaModule.ModuleName, saveVbaModule.Code);
                        break;
                    case "restoreVbaBackup":
                        var restoreVba = Payload<RestoreVbaBackupPayload>(payload);
                        responsePayload = _controller.RestoreVbaBackup(restoreVba.BackupId, restoreVba.ModuleName);
                        break;
                    case "getContext":
                        responsePayload = _controller.GetContext(Payload<ChatPayload>(payload).ChatId);
                        break;
                    case "addSelectionContext":
                        var selectionContext = Payload<SelectionContextPayload>(payload);
                        responsePayload = _controller.AddSelectionContextFromBridge(selectionContext.Mode, selectionContext.ChatId);
                        break;
                    case "addTextContext":
                        var textContext = Payload<TextContextPayload>(payload);
                        responsePayload = _controller.AddTextContext(
                            textContext.Kind,
                            textContext.Title,
                            textContext.Reference,
                            textContext.Text,
                            textContext.DetailsJson,
                            textContext.ChatId);
                        break;
                    case "addVbaContext":
                        var vbaContext = Payload<VbaContextPayload>(payload);
                        responsePayload = _controller.AddVbaContext(
                            vbaContext.ChatId,
                            vbaContext.MaxChars ?? 0);
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

                return JsonConvert.SerializeObject(new BridgeResponse
                {
                    Id = id,
                    Ok = true,
                    Payload = ToPayloadToken(responsePayload)
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new BridgeResponse
                {
                    Id = id,
                    Ok = false,
                    Error = ex.Message,
                    ErrorDetail = ex.ToString()
                });
            }
        }

        private void ReportProgress(string id, string phase, string message)
        {
            ReportProgress(id, phase, message, null);
        }

        private void ReportProgress(string id, string phase, string message, ChatActivity activity)
        {
            if (_postMessageJson == null)
            {
                return;
            }

            _postMessageJson(JsonConvert.SerializeObject(new ProgressMessage
            {
                Type = "progress",
                Id = id,
                Payload = new ProgressPayload
                {
                    Phase = phase,
                    Message = message,
                    Activity = activity
                }
            }));
        }

        private static T Payload<T>(JObject payload) where T : class, new()
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

        private static Dictionary<string, object> ToArguments(JObject arguments)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (arguments == null)
            {
                return result;
            }

            foreach (var property in arguments.Properties())
            {
                result[property.Name] = property.Value.Type == JTokenType.String
                    ? (object)property.Value.Value<string>()
                    : property.Value.ToString(Formatting.None);
            }

            return result;
        }
    }
}
