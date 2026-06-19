using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.WebView
{
    public sealed class AssistantWebBridge
    {
        private readonly AssistantController _controller;
        private readonly Action<string> _postMessageJson;
        private readonly object _cancellationSync;
        private readonly Dictionary<string, CancellationTokenSource> _requestCancellations;

        public AssistantWebBridge(AssistantController controller, Action<string> postMessageJson)
        {
            _controller = controller;
            _postMessageJson = postMessageJson;
            _cancellationSync = new object();
            _requestCancellations = new Dictionary<string, CancellationTokenSource>(StringComparer.OrdinalIgnoreCase);
        }

        public async Task<string> HandleMessageAsync(string requestJson)
        {
            string id = null;
            CancellationTokenSource cancellationSource = null;
            try
            {
                var request = JsonConvert.DeserializeObject<BridgeRequest>(requestJson) ?? new BridgeRequest();
                id = request.Id;
                var type = (request.Type ?? string.Empty).Trim();
                var payload = request.Payload ?? JValue.CreateNull();
                object responsePayload;
                if (string.Equals(type, "cancelRequest", StringComparison.OrdinalIgnoreCase))
                {
                    responsePayload = CancelRequest(Payload<CancelRequestPayload>(payload).RequestId);
                    return JsonConvert.SerializeObject(new BridgeResponse
                    {
                        Id = id,
                        Ok = true,
                        Payload = ToPayloadToken(responsePayload)
                    });
                }

                cancellationSource = CreateCancellationSource(id, type);
                var cancellationToken = cancellationSource == null ? CancellationToken.None : cancellationSource.Token;

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
                        responsePayload = await _controller.SendChatAsync(sendChat.Text, sendChat.ChatId, (phase, message, activity) => ReportProgress(id, phase, message, activity), cancellationToken);
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
                            modelCatalog.Settings,
                            modelCatalog.ApiKey);
                        break;
                    case "saveSettings":
                        var saveSettings = Payload<SaveSettingsPayload>(payload);
                        responsePayload = _controller.SaveSettings(
                            saveSettings.Settings ?? new AppSettings(),
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
                        responsePayload = _controller.SaveTools(saveTools.Tools ?? new List<ToolDefinition>());
                        break;
                    case "getSkills":
                        responsePayload = _controller.GetSkills();
                        break;
                    case "saveSkills":
                        var saveSkills = Payload<SaveSkillsPayload>(payload);
                        responsePayload = _controller.SaveSkills(saveSkills.Skills ?? new List<SkillDefinition>());
                        break;
                    case "runTool":
                        var runTool = Payload<RunToolPayload>(payload);
                        responsePayload = _controller.RunTool(
                            runTool.ToolId,
                            ToArguments(runTool.Arguments),
                            runTool.DryRun,
                            (phase, message) => ReportProgress(id, phase, message));
                        break;
                    case "confirmAgentTool":
                        var confirmAgentTool = Payload<PendingAgentToolPayload>(payload);
                        responsePayload = _controller.ConfirmAgentTool(confirmAgentTool.PendingId, confirmAgentTool.ChatId);
                        break;
                    case "cancelAgentTool":
                        var cancelAgentTool = Payload<PendingAgentToolPayload>(payload);
                        responsePayload = _controller.CancelAgentTool(cancelAgentTool.PendingId, cancelAgentTool.ChatId);
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
            catch (OperationCanceledException ex)
            {
                return JsonConvert.SerializeObject(new BridgeResponse
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
                return JsonConvert.SerializeObject(new BridgeResponse
                {
                    Id = id,
                    Ok = false,
                    Error = ex.Message,
                    ErrorDetail = ex.ToString()
                });
            }
            finally
            {
                ReleaseCancellationSource(id, cancellationSource);
            }
        }

        private CancellationTokenSource CreateCancellationSource(string id, string type)
        {
            if (string.IsNullOrWhiteSpace(id) || !string.Equals(type, "sendChat", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var source = new CancellationTokenSource();
            lock (_cancellationSync)
            {
                _requestCancellations[id] = source;
            }

            return source;
        }

        private object CancelRequest(string requestId)
        {
            var cancelled = false;
            lock (_cancellationSync)
            {
                CancellationTokenSource source;
                _requestCancellations.TryGetValue(requestId ?? string.Empty, out source);
                if (source != null)
                {
                    source.Cancel();
                    cancelled = true;
                }
            }

            return new { cancelled = cancelled };
        }

        private void ReleaseCancellationSource(string id, CancellationTokenSource source)
        {
            if (source == null)
            {
                return;
            }

            lock (_cancellationSync)
            {
                CancellationTokenSource current;
                if (_requestCancellations.TryGetValue(id ?? string.Empty, out current) && object.ReferenceEquals(current, source))
                {
                    _requestCancellations.Remove(id ?? string.Empty);
                }
            }

            source.Dispose();
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
