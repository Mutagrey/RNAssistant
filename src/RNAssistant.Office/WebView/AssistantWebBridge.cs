using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Office.WebView
{
    public sealed class AssistantWebBridge
    {
        private readonly AssistantController _controller;

        public AssistantWebBridge(AssistantController controller)
        {
            _controller = controller;
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
                    case "sendChat":
                        payloadJson = await _controller.SendChatAsync((string)payload["text"]);
                        break;
                    case "getSettings":
                        payloadJson = _controller.GetSettingsJson();
                        break;
                    case "saveSettings":
                        payloadJson = _controller.SaveSettingsJson(
                            payload["settings"] == null ? "{}" : payload["settings"].ToString(Formatting.None),
                            payload["apiKey"] == null ? null : (string)payload["apiKey"]);
                        break;
                    case "getSkills":
                        payloadJson = _controller.GetSkillsJson();
                        break;
                    case "saveSkills":
                        payloadJson = _controller.SaveSkillsJson(payload["skills"] == null ? "[]" : payload["skills"].ToString(Formatting.None));
                        break;
                    case "getContext":
                        payloadJson = _controller.GetContextJson();
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
                    error = ex.Message
                });
            }
        }
    }
}
