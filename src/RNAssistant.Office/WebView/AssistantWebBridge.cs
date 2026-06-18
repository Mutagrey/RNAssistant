using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
                string payloadJson;

                switch (type)
                {
                    case "init":
                        payloadJson = _controller.InitializeJson();
                        break;
                    case "listChats":
                        payloadJson = _controller.ListChatsJson();
                        break;
                    case "createChat":
                        var createChat = Payload<CreateChatPayload>(payload);
                        payloadJson = _controller.CreateChatJson(createChat.Title);
                        break;
                    case "selectChat":
                        payloadJson = _controller.SelectChatJson(Payload<ChatPayload>(payload).ChatId);
                        break;
                    case "renameChat":
                        var renameChat = Payload<RenameChatPayload>(payload);
                        payloadJson = _controller.RenameChatJson(renameChat.ChatId, renameChat.Title);
                        break;
                    case "setChatModel":
                        var setChatModel = Payload<SetChatModelPayload>(payload);
                        payloadJson = _controller.SetChatModelJson(setChatModel.ChatId, setChatModel.Model);
                        break;
                    case "clearChat":
                        payloadJson = _controller.ClearChatJson(Payload<ChatPayload>(payload).ChatId);
                        break;
                    case "deleteChat":
                        payloadJson = _controller.DeleteChatJson(Payload<ChatPayload>(payload).ChatId);
                        break;
                    case "sendChat":
                        var sendChat = Payload<SendChatPayload>(payload);
                        payloadJson = await _controller.SendChatAsync(sendChat.Text, sendChat.ChatId, (phase, message) => ReportProgress(id, phase, message));
                        break;
                    case "deleteMessage":
                        var deleteMessage = Payload<MessageActionPayload>(payload);
                        payloadJson = _controller.DeleteMessageJson(deleteMessage.Id, deleteMessage.Index ?? -1, deleteMessage.ChatId);
                        break;
                    case "forkChat":
                        var forkChat = Payload<MessageActionPayload>(payload);
                        payloadJson = _controller.ForkChatJson(forkChat.Id, forkChat.Index ?? -1, forkChat.ChatId);
                        break;
                    case "getSettings":
                        payloadJson = _controller.GetSettingsJson();
                        break;
                    case "getModelCatalog":
                        payloadJson = await _controller.GetModelCatalogJsonAsync(
                            payload["settings"] == null ? "{}" : payload["settings"].ToString(Formatting.None),
                            payload["apiKey"] == null ? null : (string)payload["apiKey"]);
                        break;
                    case "saveSettings":
                        payloadJson = _controller.SaveSettingsJson(
                            payload["settings"] == null ? "{}" : payload["settings"].ToString(Formatting.None),
                            payload["apiKey"] == null ? null : (string)payload["apiKey"]);
                        break;
                    case "clearRuntimeData":
                        payloadJson = _controller.ClearRuntimeDataJson();
                        break;
                    case "getTools":
                        payloadJson = _controller.GetToolsJson();
                        break;
                    case "saveTools":
                        payloadJson = _controller.SaveToolsJson(payload["tools"] == null ? "[]" : payload["tools"].ToString(Formatting.None));
                        break;
                    case "runTool":
                        var runTool = Payload<RunToolPayload>(payload);
                        payloadJson = _controller.RunToolJson(
                            runTool.ToolId,
                            runTool.Arguments == null ? "{}" : runTool.Arguments.ToString(Formatting.None),
                            runTool.DryRun,
                            (phase, message) => ReportProgress(id, phase, message));
                        break;
                    case "getVbaProject":
                        payloadJson = _controller.GetVbaProjectJson(payload["maxChars"] == null ? 30000 : (int)payload["maxChars"]);
                        break;
                    case "saveVbaModule":
                        payloadJson = _controller.SaveVbaModuleJson((string)payload["moduleName"], (string)payload["code"]);
                        break;
                    case "restoreVbaBackup":
                        payloadJson = _controller.RestoreVbaBackupJson((string)payload["backupId"], (string)payload["moduleName"]);
                        break;
                    case "getContext":
                        payloadJson = _controller.GetContextJson((string)payload["chatId"]);
                        break;
                    case "addSelectionContext":
                        payloadJson = _controller.AddSelectionContextJson((string)payload["mode"], (string)payload["chatId"]);
                        break;
                    case "addTextContext":
                        payloadJson = _controller.AddTextContextJson(
                            (string)payload["kind"],
                            (string)payload["title"],
                            (string)payload["reference"],
                            (string)payload["text"],
                            (string)payload["detailsJson"],
                            (string)payload["chatId"]);
                        break;
                    case "addVbaContext":
                        payloadJson = _controller.AddVbaContextJson(
                            (string)payload["chatId"],
                            payload["maxChars"] == null ? 0 : (int)payload["maxChars"]);
                        break;
                    case "removeContextItem":
                        payloadJson = _controller.RemoveContextItemJson((string)payload["id"], (string)payload["chatId"]);
                        break;
                    case "clearContext":
                        payloadJson = _controller.ClearContextJson((string)payload["chatId"]);
                        break;
                    case "quickAction":
                        payloadJson = await _controller.RunQuickActionAsync(Payload<QuickActionPayload>(payload).Action);
                        break;
                    default:
                        throw new InvalidOperationException("Unknown bridge message: " + type);
                }

                return JsonConvert.SerializeObject(new BridgeResponse
                {
                    Id = id,
                    Ok = true,
                    Payload = JToken.Parse(payloadJson)
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
                    Message = message
                }
            }));
        }

        private static T Payload<T>(JObject payload) where T : class, new()
        {
            return payload == null ? new T() : (payload.ToObject<T>() ?? new T());
        }
    }
}
