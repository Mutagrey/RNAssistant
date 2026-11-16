using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;
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
        private static async Task<int> RunLegacyRecoveryTestAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "RNAssistant.LegacyRecovery." + Guid.NewGuid().ToString("N"));
            try
            {
                var paths = AppDataPaths.CreateForRoot(root);
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var legacy = new ChatStore(paths);
                IConversationStore oldChats = new ChatConversationStoreAdapter(legacy);
                var chat = oldChats.CreateTransient(adapter.HostName, adapter.DocumentKey, adapter.DocumentTitle, "Retained legacy conversation");
                chat.Messages.AddRange(new[] { new ChatMessage { Role = "user", Content = "Legacy user question" },
                    new ChatMessage { Role = "assistant", Content = "Legacy assistant answer" } });
                oldChats.Commit(chat, new ConversationCommit(chat.Messages));
                var eventPath = Directory.GetFiles(paths.ChatDirectory, "*.events.jsonl", SearchOption.AllDirectories).Single();
                var eventBytes = File.ReadAllBytes(eventPath);
                File.Delete(eventPath + ".projection.sqlite");

                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "plugin-source"));
                const string pluginId = "archive-recovery-plugin";
                File.WriteAllText(Path.Combine(workspace.RootPath, "plugin.json"), JsonConvert.SerializeObject(new PluginManifestV1 {
                    FormatVersion = 1, HostProtocol = 1, Id = pluginId, Name = "Retained plugin", Version = "1.0.0",
                    Entry = "index.html", Files = new[] { "plugin.json", "index.html" }
                }));
                File.WriteAllText(Path.Combine(workspace.RootPath, "index.html"), "<main>Retained source bytes</main>");
                var oldAuthority = new ResourceAuthorityStore(paths); var oldMutations = new ResourceMutationJournal(paths);
                var oldFiles = new WorkspaceFileService(paths, oldAuthority, oldAuthority, oldMutations);
                var oldPlugins = new PluginStore(paths, oldChats, oldAuthority, oldMutations, oldFiles, new ChatBlobStore(paths));
                oldPlugins.Connect(new PluginSourceWorkspace(workspace));
                var sourceBytes = Directory.EnumerateFiles(workspace.RootPath, "*", SearchOption.AllDirectories)
                    .ToDictionary(file => file, File.ReadAllBytes, StringComparer.Ordinal);
                var authorityBytes = Directory.EnumerateFiles(paths.ResourceAuthorityDirectory, "*.jsonl")
                    .ToDictionary(file => file, File.ReadAllBytes, StringComparer.Ordinal);

                using (var plugins = new PluginApplicationService(paths))
                using (var controller = new AssistantController(adapter, paths,
                    (settings, messages, token) => Task.FromException<LlmCompletionResult>(new Exception("Archive recovery must not call a model."))))
                using (var bridge = new AssistantWebBridge(controller, ignored => { }) { Plugins = plugins })
                {
                    var serial = 0; string bridgeToken = null;
                    async Task<JObject> Request(string type, JObject payload)
                    {
                        return JObject.Parse(await bridge.HandleMessageAsync(JsonConvert.SerializeObject(new BridgeRequest {
                            ContractVersion = BridgeOperationCatalog.Version, Id = "legacy-recovery-" + ++serial,
                            Type = type, BridgeToken = bridgeToken, Payload = payload
                        })).ConfigureAwait(false));
                    }
                    var init = await Request("init", new JObject()).ConfigureAwait(false);
                    bridgeToken = (string)init["payload"]?["bridgeToken"];
                    if ((bool?)init["ok"] != true || string.IsNullOrWhiteSpace(bridgeToken)) throw new Exception("Real bridge initialization failed.");
                    IConversationStore native;
                    ChatCreationSessions(paths, adapter, out native);
                    if (native.ListHeaders(includeStorageUsage: false).Count != 0 || plugins.List().Items.Count != 0)
                        throw new Exception("Legacy fixtures must not appear as native chats or plugins during init.");

                    var listed = await Request("listLegacyChats", new JObject()).ConfigureAwait(false);
                    if ((bool?)listed["ok"] != true) throw new Exception("Legacy chat catalog failed: " + listed);
                    var item = ((JArray)listed["payload"]["items"]).Single(value => (string)value["chatId"] == chat.Id);
                    if ((string)item["status"] != "readable" || (long?)item["revision"] != chat.Revision)
                        throw new Exception("Archive catalog lost its canonical revision or readability.");
                    var sourceId = (string)item["sourceId"];
                    JObject Transcript(string source, long revision) { return new JObject {
                        ["sourceId"] = source, ["expectedRevision"] = revision, ["maxCharacters"] = 2048 }; }
                    var transcript = await Request("readLegacyTranscript", Transcript(sourceId, chat.Revision)).ConfigureAwait(false);
                    var text = (string)transcript["payload"]?["text"];
                    if ((bool?)transcript["ok"] != true || (string)transcript["payload"]?["chatId"] != chat.Id ||
                        (long?)transcript["payload"]?["revision"] != chat.Revision || text == null || text.Length > 2048 ||
                        !text.Contains("Legacy user question") || !text.Contains("Legacy assistant answer"))
                        throw new Exception("Exact bounded transcript did not preserve the selected conversation.");
                    foreach (var invalid in new[] { Transcript(sourceId, chat.Revision + 1), Transcript("../outside.events.jsonl", chat.Revision),
                        Transcript(new string('0', 64) + "/" + new string('0', 64) + ".events.jsonl", chat.Revision) })
                        if ((bool?)(await Request("readLegacyTranscript", invalid).ConfigureAwait(false))["ok"] != false)
                            throw new Exception("Wrong revision/source/path must not become an archive read.");
                    if (native.ListHeaders(includeStorageUsage: false).Count != 0 || File.Exists(eventPath + ".projection.sqlite") ||
                        !eventBytes.SequenceEqual(File.ReadAllBytes(eventPath)))
                        throw new Exception("Archive reads must not create a native chat, rewrite events or rebuild the legacy projection.");

                    var legacyPlugins = await Request("listLegacyPlugins", new JObject()).ConfigureAwait(false);
                    if ((bool?)legacyPlugins["ok"] != true) throw new Exception("Legacy plugin catalog failed: " + legacyPlugins);
                    var registration = ((JArray)legacyPlugins["payload"]["items"]).Single(value => (string)value["pluginId"] == pluginId);
                    if ((bool?)registration["canConnect"] != true || (string)registration["sourceRoot"] != workspace.RootPath)
                        throw new Exception("Only the validated retained registration may offer reconnection.");
                    var arbitraryRoot = Path.Combine(paths.Root, "not-a-registered-source");
                    foreach (var invalid in new[] { new JObject { ["pluginId"] = "unregistered-plugin" },
                        new JObject { ["pluginId"] = workspace.RootPath }, new JObject { ["rootPath"] = arbitraryRoot } })
                        if ((bool?)(await Request("reconnectLegacyPlugin", invalid).ConfigureAwait(false))["ok"] != false)
                            throw new Exception("Reconnection must reject unknown IDs and caller-supplied paths.");
                    if (plugins.List().Items.Count != 0 || Directory.Exists(arbitraryRoot))
                        throw new Exception("Rejected reconnection must not register a plugin or create an arbitrary source.");

                    var pluginMismatch = false;
                    try { plugins.Connect(new PluginConnectRequest { RootPath = workspace.RootPath,
                        ExpectedPluginId = "different-plugin", ExpectedSourceWorkspaceId = workspace.WorkspaceId }); }
                    catch (PluginException) { pluginMismatch = true; }
                    if (!pluginMismatch || plugins.List().Items.Count != 0) throw new Exception("Expected plugin identity must be fenced before registration.");
                    var associations = Directory.GetFiles(paths.WorkspaceDirectory, "*.json");
                    foreach (var file in associations) File.SetLastWriteTimeUtc(file, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                    var associationBytes = associations.ToDictionary(file => file, File.ReadAllBytes, StringComparer.Ordinal);
                    var associationTimes = associations.ToDictionary(file => file, File.GetLastWriteTimeUtc, StringComparer.Ordinal);
                    var workspaceMismatch = false;
                    try { plugins.Connect(new PluginConnectRequest { RootPath = workspace.RootPath,
                        ExpectedPluginId = pluginId, ExpectedSourceWorkspaceId = "different-workspace" }); }
                    catch (InvalidDataException) { workspaceMismatch = true; }
                    if (!workspaceMismatch || plugins.List().Items.Count != 0 || associationBytes.Any(pair =>
                        !pair.Value.SequenceEqual(File.ReadAllBytes(pair.Key)) || File.GetLastWriteTimeUtc(pair.Key) != associationTimes[pair.Key]))
                        throw new Exception("Expected workspace identity must be fenced before association changes.");

                    var connected = await Request("reconnectLegacyPlugin", new JObject { ["pluginId"] = pluginId,
                        ["rootPath"] = arbitraryRoot, ["expectedPluginId"] = "wire-cannot-override-runtime-identity" }).ConfigureAwait(false);
                    if ((bool?)connected["ok"] != true || (string)connected["payload"]?["pluginId"] != pluginId ||
                        (string)connected["payload"]?["sourceRoot"] != workspace.RootPath || (bool?)connected["payload"]?["enabled"] != false)
                        throw new Exception("Explicit reconnection must use the retained source and remain disabled: " + connected);
                    var catalog = await Request("getPlugins", new JObject()).ConfigureAwait(false);
                    var nativeItem = ((JArray)catalog["payload"]["items"]).Single();
                    if ((string)nativeItem["pluginId"] != pluginId || (bool?)nativeItem["enabled"] != false || Directory.Exists(arbitraryRoot))
                        throw new Exception("The reconnected plugin must appear once in the native catalog without source redirection.");
                    if (sourceBytes.Any(pair => !pair.Value.SequenceEqual(File.ReadAllBytes(pair.Key))) ||
                        authorityBytes.Any(pair => !pair.Value.SequenceEqual(File.ReadAllBytes(pair.Key))) ||
                        !eventBytes.SequenceEqual(File.ReadAllBytes(eventPath)) || native.ListHeaders(includeStorageUsage: false).Count != 0)
                        throw new Exception("Recovery must preserve source and legacy bytes without importing conversation runtime state.");
                }
                Console.WriteLine("PASS legacy-recovery: real bridge archive, exact bounded transcript, guarded disabled plugin reconnect and retained bytes");
                return 0;
            }
            catch (Exception error) { Console.WriteLine("FAIL legacy-recovery: " + error); return 1; }
            finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        }
    }
}
