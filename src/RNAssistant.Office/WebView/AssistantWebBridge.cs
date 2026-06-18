using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
                var request = JObject.Parse(requestJson);
                id = (string)request["id"];
                var type = ((string)request["type"] ?? string.Empty).Trim();
                var payload = request["payload"] as JObject ?? new JObject();
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
                        payloadJson = _controller.CreateChatJson((string)payload["title"]);
                        break;
                    case "selectChat":
                        payloadJson = _controller.SelectChatJson((string)payload["chatId"]);
                        break;
                    case "renameChat":
                        payloadJson = _controller.RenameChatJson((string)payload["chatId"], (string)payload["title"]);
                        break;
                    case "clearChat":
                        payloadJson = _controller.ClearChatJson((string)payload["chatId"]);
                        break;
                    case "deleteChat":
                        payloadJson = _controller.DeleteChatJson((string)payload["chatId"]);
                        break;
                    case "sendChat":
                        payloadJson = await _controller.SendChatAsync((string)payload["text"], (string)payload["chatId"], (phase, message) => ReportProgress(id, phase, message));
                        break;
                    case "deleteMessage":
                        payloadJson = _controller.DeleteMessageJson((string)payload["id"], payload["index"] == null ? -1 : (int)payload["index"], (string)payload["chatId"]);
                        break;
                    case "getSettings":
                        payloadJson = _controller.GetSettingsJson();
                        break;
                    case "saveSettings":
                        payloadJson = _controller.SaveSettingsJson(
                            payload["settings"] == null ? "{}" : payload["settings"].ToString(Formatting.None),
                            payload["apiKey"] == null ? null : (string)payload["apiKey"]);
                        break;
                    case "getTools":
                        payloadJson = _controller.GetToolsJson();
                        break;
                    case "saveTools":
                        payloadJson = _controller.SaveToolsJson(payload["tools"] == null ? "[]" : payload["tools"].ToString(Formatting.None));
                        break;
                    case "runTool":
                        payloadJson = _controller.RunToolJson(
                            (string)payload["toolId"],
                            payload["arguments"] == null ? "{}" : payload["arguments"].ToString(Formatting.None),
                            payload["dryRun"] != null && (bool)payload["dryRun"],
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
                        payloadJson = _controller.GetContextJson();
                        break;
                    case "addSelectionContext":
                        payloadJson = _controller.AddSelectionContextJson((string)payload["mode"]);
                        break;
                    case "removeContextItem":
                        payloadJson = _controller.RemoveContextItemJson((string)payload["id"]);
                        break;
                    case "clearContext":
                        payloadJson = _controller.ClearContextJson();
                        break;
                    case "quickAction":
                        payloadJson = await _controller.RunQuickActionAsync((string)payload["action"]);
                        break;
                    default:
                        throw new InvalidOperationException("Unknown bridge message: " + type);
                }

                return JsonConvert.SerializeObject(new
                {
                    id = id,
                    ok = true,
                    payload = JToken.Parse(payloadJson)
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new
                {
                    id = id,
                    ok = false,
                    error = ex.Message,
                    errorDetail = ex.ToString()
                });
            }
        }

        private void ReportProgress(string id, string phase, string message)
        {
            if (_postMessageJson == null)
            {
                return;
            }

            _postMessageJson(JsonConvert.SerializeObject(new
            {
                type = "progress",
                id = id,
                payload = new
                {
                    phase = phase,
                    message = message
                }
            }));
        }
    }
}
