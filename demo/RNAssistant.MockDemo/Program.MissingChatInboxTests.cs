using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Storage;
using RNAssistant.Harness;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.WebView;

namespace RNAssistant.MockDemo
{
    internal static partial class Program
    {
        private static async Task<int> RunMissingChatInboxTestAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "RNAssistant.MissingChatInbox." + Guid.NewGuid().ToString("N"));
            try
            {
                var paths = AppDataPaths.CreateForRoot(root);
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                using (var controller = new AssistantController(adapter, paths,
                    (settings, messages, token) => Task.FromException<LlmCompletionResult>(new Exception("Inbox reads must not call a model."))))
                using (var bridge = new AssistantWebBridge(controller, ignored => { }))
                {
                    var obsolete = controller.CreateChat("Deleted in another window").ActiveChatId;
                    var current = controller.CreateChat("Current chat").ActiveChatId;
                    IConversationStore store;
                    var sessions = ChatCreationSessions(paths, adapter, out store);
                    store.Delete(adapter.HostName, adapter.DocumentKey, obsolete);
                    var initialized = JObject.Parse(await bridge.HandleMessageAsync(JsonConvert.SerializeObject(new BridgeRequest {
                        ContractVersion = BridgeOperationCatalog.Version, Id = "init", Type = "init", Payload = new JObject()
                    })).ConfigureAwait(false));
                    var bridgeToken = (string)initialized["payload"]?["bridgeToken"];
                    if ((bool?)initialized["ok"] != true || string.IsNullOrWhiteSpace(bridgeToken))
                        throw new Exception("The real bridge must initialize before an addressed inbox read.");
                    async Task<JObject> Read(string requestId, string chatId)
                    {
                        return JObject.Parse(await bridge.HandleMessageAsync(JsonConvert.SerializeObject(new BridgeRequest {
                            ContractVersion = BridgeOperationCatalog.Version, Id = requestId, Type = "getChatInbox", BridgeToken = bridgeToken,
                            Payload = new JObject { ["chatId"] = chatId }
                        })).ConfigureAwait(false));
                    }
                    for (var index = 0; index < 3; index++)
                    {
                        var missing = await Read("missing-" + index, obsolete).ConfigureAwait(false);
                        if ((bool?)missing["ok"] != false || (string)missing["errorCode"] != "chat_session_not_found" ||
                            (bool?)missing["transportFailure"] == true || string.IsNullOrWhiteSpace((string)missing["errorDetail"]))
                            throw new Exception("Missing exact sessions require their typed non-transport failure.");
                    }
                    var valid = await Read("other-chat", current).ConfigureAwait(false);
                    if ((bool?)valid["ok"] != true || (string)valid["payload"]?["chatId"] != current ||
                        controller.GetChatState().ActiveChatId != current)
                        throw new Exception("Missing inbox reads must not replace the selected chat or block another chat.");
                    try { sessions.LoadDiagnosticsMetadata(obsolete); throw new Exception("Missing diagnostic address was accepted."); }
                    catch (ChatSessionNotFoundException error)
                    { if (error.ChatId != obsolete) throw new Exception("Typed failure lost its exact address."); }

                    var arriving = store.CreateTransient(adapter.HostName, adapter.DocumentKey, adapter.DocumentTitle, "Created in another window");
                    var before = await Read("before-create", arriving.Id).ConfigureAwait(false);
                    if ((string)before["errorCode"] != "chat_session_not_found") throw new Exception("An uncommitted foreign draft is unavailable.");
                    store.Commit(arriving, new ConversationCommit());
                    var retry = await Read("manual-retry", arriving.Id).ConfigureAwait(false);
                    if ((bool?)retry["ok"] != true || (string)retry["payload"]?["chatId"] != arriving.Id ||
                        controller.GetChatState().ActiveChatId != current)
                        throw new Exception("An explicit retry can observe the exact newly available chat without selecting it.");

                    store.Delete(adapter.HostName, adapter.DocumentKey, current);
                    var removedActive = await Read("removed-active", current).ConfigureAwait(false);
                    if ((string)removedActive["errorCode"] != "chat_session_not_found")
                        throw new Exception("A stale persisted active-session cache cannot resurrect a removed chat.");
                }
                var draftPaths = AppDataPaths.CreateForRoot(Path.Combine(root, "draft"));
                using (var drafts = new AssistantController(FakeOfficeAdapter.ForHost("Excel"), draftPaths,
                    (settings, messages, token) => Task.FromException<LlmCompletionResult>(new Exception("Draft reads must not call a model."))))
                {
                    var draft = drafts.GetChatState();
                    if (drafts.GetChatInbox(draft.ActiveChatId).ChatId != draft.ActiveChatId)
                        throw new Exception("The exact selected unsaved draft remains a valid inbox target.");
                }
                Console.WriteLine("PASS missing-chat-inbox: typed exact response, independent chat, explicit retry, stale cache and selected draft");
                return 0;
            }
            catch (Exception error) { Console.WriteLine("FAIL missing-chat-inbox: " + error); return 1; }
            finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        }
    }
}
