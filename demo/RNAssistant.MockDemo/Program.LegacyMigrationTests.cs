using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Application;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Storage;
using RNAssistant.Harness;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.WebView;

namespace RNAssistant.MockDemo
{
    internal static partial class Program
    {
        private static async Task<int> RunLegacyMigrationTestAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "RNAssistant.LegacyMigration." + Guid.NewGuid().ToString("N"));
            try
            {
                var paths = AppDataPaths.CreateForRoot(root);
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var identities = new DocumentAuthorityRegistry(paths);
                var authority = identities.Resolve(adapter.HostName, adapter.RuntimeDocumentKey, adapter.DocumentPathValue).Id;
                var legacy = new ChatStore(paths);
                IConversationStore old = new ChatConversationStoreAdapter(legacy);
                var officeChat = old.CreateTransient(adapter.HostName, adapter.DocumentKey, adapter.DocumentTitle, "Previous Office conversation");
                officeChat.DocumentAuthorityId = authority; officeChat.DocumentPath = adapter.DocumentPathValue;
                officeChat.Messages.AddRange(new[] { new ChatMessage { Role = "user", Content = "Previous Office question" },
                    new ChatMessage { Role = "assistant", Content = "Previous Office answer" } });
                old.Commit(officeChat, new ConversationCommit(officeChat.Messages));
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(root, "plugin-source"));
                var workspaceChat = old.CreateTransient("Workspace", workspace.WorkspaceId, workspace.Name, "Previous plugin conversation");
                workspaceChat.WorkspaceId = workspace.WorkspaceId;
                workspaceChat.Messages.Add(new ChatMessage { Role = "user", Content = "Previous plugin question" });
                old.Commit(workspaceChat, new ConversationCommit(workspaceChat.Messages));
                const string pluginId = "migration-test-plugin";
                File.WriteAllText(Path.Combine(workspace.RootPath, "plugin.json"), JsonConvert.SerializeObject(new PluginManifestV1 {
                    FormatVersion = 1, HostProtocol = 1, Id = pluginId, Name = "Previous plugin name", Version = "1.0.0",
                    Entry = "index.html", Files = new[] { "plugin.json", "index.html" }
                }));
                File.WriteAllText(Path.Combine(workspace.RootPath, "index.html"), "<main>Previous plugin source</main>");
                var oldAuthority = new ResourceAuthorityStore(paths); var mutations = new ResourceMutationJournal(paths);
                var files = new WorkspaceFileService(paths, oldAuthority, oldAuthority, mutations);
                new PluginStore(paths, old, oldAuthority, mutations, files, new ChatBlobStore(paths)).Connect(new PluginSourceWorkspace(workspace));
                var retained = new[] { paths.ChatDirectory, paths.ResourceAuthorityDirectory, workspace.RootPath }
                    .SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                    .ToDictionary(file => file, File.ReadAllBytes, StringComparer.Ordinal);

                using (var plugins = new PluginApplicationService(paths))
                using (var controller = new AssistantController(adapter, paths,
                    (settings, messages, token) => Task.FromException<LlmCompletionResult>(new Exception("Migration must not execute a model or old run."))))
                using (var bridge = new AssistantWebBridge(controller, ignored => { }) { Plugins = plugins })
                {
                    var serial = 0; string bridgeToken = null;
                    async Task<JObject> Request(string type, JObject payload)
                    {
                        var result = JObject.Parse(await bridge.HandleMessageAsync(JsonConvert.SerializeObject(new BridgeRequest {
                            ContractVersion = BridgeOperationCatalog.Version, Id = "migration-" + ++serial,
                            Type = type, BridgeToken = bridgeToken, Payload = payload
                        })).ConfigureAwait(false));
                        if ((bool?)result["ok"] != true) throw new Exception(type + " failed: " + result);
                        return result;
                    }
                    var init = await Request("init", new JObject()).ConfigureAwait(false);
                    bridgeToken = (string)init["payload"]?["bridgeToken"];
                    var selected = controller.CreateChat("Unrelated current chat").ActiveChatId;
                    var catalog = (JArray)(await Request("listLegacyChats", new JObject()).ConfigureAwait(false))["payload"]["items"];
                    foreach (var chat in new[] { officeChat, workspaceChat })
                    {
                        var item = catalog.Single(value => (string)value["chatId"] == chat.Id);
                        var import = new JObject { ["sourceId"] = item["sourceId"], ["expectedRevision"] = item["revision"] };
                        var query = (JObject)import.DeepClone(); query["chatId"] = chat.Id;
                        var before = await Request("getLegacyChatImport", query).ConfigureAwait(false);
                        if (before["payload"] != null && before["payload"].Type != JTokenType.Null)
                            throw new Exception("A missing import receipt must not imply completed migration.");
                        var applied = (await Request("importLegacyChat", import).ConfigureAwait(false))["payload"];
                        if ((string)applied["chatId"] != chat.Id || (string)applied["status"] != "imported")
                            throw new Exception("Import must preserve the exact legacy chat identity.");
                        var reconciled = (await Request("getLegacyChatImport", query).ConfigureAwait(false))["payload"];
                        if ((string)reconciled["chatId"] != chat.Id)
                            throw new Exception("Read-only reconciliation lost the durable import receipt.");
                        var repeat = (await Request("importLegacyChat", import).ConfigureAwait(false))["payload"];
                        if ((string)repeat["status"] != "alreadyImported") throw new Exception("Explicit retry must be idempotent.");
                    }
                    var officeCatalog = (await Request("listChats", new JObject()).ConfigureAwait(false))["payload"];
                    if (!((JArray)officeCatalog["chats"]).Any(value => (string)(value["Id"] ?? value["id"]) == officeChat.Id))
                        throw new Exception("Imported Office history must appear in the ordinary catalog.");
                    var workspaceSummary = ((JArray)officeCatalog["chats"]).Single(value =>
                        (string)(value["Id"] ?? value["id"]) == workspaceChat.Id);
                    if ((string)(workspaceSummary["WorkspaceId"] ?? workspaceSummary["workspaceId"]) != workspace.WorkspaceId)
                        throw new Exception("A mixed Office/Workspace catalog must preserve the exact Workspace identity in its summary.");
                    var workspaceGroups = ((JArray)officeCatalog["groupScopes"]).Single(value =>
                        (string)value["workspaceId"] == workspace.WorkspaceId);
                    if ((string)workspaceGroups["host"] != "Workspace" ||
                        (string)workspaceGroups["documentKey"] != workspace.WorkspaceId || workspaceGroups["error"] != null)
                        throw new Exception("The mixed catalog must read groups from the retained Workspace scope without substituting the Office document.");
                    var detail = (await Request("getChatState", new JObject { ["chatId"] = officeChat.Id }).ConfigureAwait(false))["payload"];
                    if (!detail.ToString().Contains("Previous Office question") || !detail.ToString().Contains("Previous Office answer"))
                        throw new Exception("The ordinary chat view must read the transferred messages.");
                    if (controller.GetChatState().ActiveChatId != selected)
                        throw new Exception("Migration and addressed reads must preserve an unrelated current chat.");

                    var oldPlugins = (JArray)(await Request("listLegacyPlugins", new JObject()).ConfigureAwait(false))["payload"]["items"];
                    var plugin = oldPlugins.Single(value => (string)value["pluginId"] == pluginId);
                    var pluginRequest = new JObject { ["pluginId"] = pluginId, ["expectedRegistrationRevision"] = plugin["registrationRevision"] };
                    var pluginBefore = await Request("getLegacyPluginImport", pluginRequest).ConfigureAwait(false);
                    if (pluginBefore["payload"] != null && pluginBefore["payload"].Type != JTokenType.Null)
                        throw new Exception("No plugin import receipt exists before migration.");
                    var importedPlugin = (await Request("importLegacyPlugin", pluginRequest).ConfigureAwait(false))["payload"];
                    if ((string)importedPlugin["item"]?["pluginId"] != pluginId || (bool?)importedPlugin["alreadyImported"] != false)
                        throw new Exception("Native plugin import did not return its exact registration.");
                    var pluginReceipt = (await Request("getLegacyPluginImport", pluginRequest).ConfigureAwait(false))["payload"];
                    if ((string)pluginReceipt["item"]?["pluginId"] != pluginId)
                        throw new Exception("Read-only plugin receipt is unavailable after commit.");
                    var pluginRepeat = (await Request("importLegacyPlugin", pluginRequest).ConfigureAwait(false))["payload"];
                    if ((bool?)pluginRepeat["alreadyImported"] != true) throw new Exception("Plugin import must not duplicate or overwrite on retry.");
                    var ordinaryPlugins = (JArray)(await Request("getPlugins", new JObject()).ConfigureAwait(false))["payload"]["items"];
                    if (ordinaryPlugins.Count != 1 || (string)ordinaryPlugins[0]["name"] != "Previous plugin name")
                        throw new Exception("Imported registration must appear in the ordinary plugin catalog.");
                    var projects = (JArray)(await Request("getProjectChats", new JObject()).ConfigureAwait(false))["payload"]["projects"];
                    var restoredProject = projects.Single(value => (string)value["workspaceId"] == workspace.WorkspaceId);
                    if ((string)restoredProject["pluginId"] != pluginId ||
                        !((JArray)restoredProject["chats"]).Any(value => (string)(value["Id"] ?? value["id"]) == workspaceChat.Id))
                        throw new Exception("Imported plugin conversation must return to its ordinary workspace tree.");
                }
                if (retained.Any(pair => !File.Exists(pair.Key) || !pair.Value.SequenceEqual(File.ReadAllBytes(pair.Key))))
                    throw new Exception("Migration rewrote retained legacy or source files.");
                Console.WriteLine("PASS legacy-migration: real bridge import, ordinary Office/plugin catalogs, exact IDs, preserved selection, receipts and unchanged legacy files");
                return 0;
            }
            catch (Exception error) { Console.WriteLine("FAIL legacy-migration: " + error); return 1; }
            finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        }
    }
}
