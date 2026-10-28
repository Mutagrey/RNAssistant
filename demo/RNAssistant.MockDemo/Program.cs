using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Harness;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;

namespace RNAssistant.MockDemo
{
    internal static partial class Program
    {
        public static int Main(string[] args)
        {
            return MainAsync(args).GetAwaiter().GetResult();
        }

        private static async Task<int> MainAsync(string[] args)
        {
            var options = DemoOptions.Parse(args);
            if ((args ?? new string[0]).Contains("--confirmation-input-test")) return await RunConfirmationInputTestsAsync().ConfigureAwait(false);
            if ((args ?? new string[0]).Contains("--resource-workflow-test")) return await RunResourceWorkflowTestAsync().ConfigureAwait(false);
            if (options.UnifiedAgentTest) return await RunUnifiedAgentTestAsync().ConfigureAwait(false);
            SettingsService.ConfigureDemoDefaults(options.BaseUrl, "mock-strict");
            if ((args ?? new string[0]).Contains("--artifact-transfer-test")) return RunArtifactTransferTest();
            if ((args ?? new string[0]).Contains("--inbox-test")) return await RunInboxTestAsync().ConfigureAwait(false);
            if (options.ArtifactCommitTest)
            {
                try
                {
                    await ExerciseFailedTurnPersistenceAsync().ConfigureAwait(false);
                    await ExerciseLiveToolArtifactProjectionAsync().ConfigureAwait(false);
                    Console.WriteLine("PASS artifact-commit-projection");
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("FAIL artifact-commit-projection: " + ex.Message);
                    return 1;
                }
            }
            if (options.SelfTest)
            {
                return await RunSelfTestAsync(options).ConfigureAwait(false);
            }

            if (options.Reset && Directory.Exists(options.DataRoot))
            {
                Directory.Delete(options.DataRoot, true);
            }

            var webRoot = ResolveWebRoot();
            var bridgeHost = CreateBridgeHost(options);
            var server = new MockDemoServer(options, bridgeHost, webRoot);
            var stop = new CancellationTokenSource();
            Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs eventArgs)
            {
                eventArgs.Cancel = true;
                stop.Cancel();
                server.Stop();
            };

            await server.RunAsync(stop.Token).ConfigureAwait(false);
            return 0;
        }

        private static MockBridgeHost CreateBridgeHost(DemoOptions options)
        {
            var paths = AppDataPaths.CreateForRoot(options.DataRoot);
            var adapter = FakeOfficeAdapter.ForHost(options.Host);
            var llm = new ScriptedDemoLlm(adapter.HostName);
            var controller = new AssistantController(adapter, paths, llm.CompleteAsync);
            return new MockBridgeHost(controller);
        }

        private static async Task<int> RunSelfTestAsync(DemoOptions options)
        {
            var failed = 0;
            foreach (var model in ScriptedDemoLlm.ModelIds)
            {
                var root = Path.Combine(Path.GetTempPath(), "RNAssistant.MockDemo." + Guid.NewGuid().ToString("N"));
                try
                {
                    var localOptions = new DemoOptions
                    {
                        Host = "Excel",
                        Port = options.Port,
                        DataRoot = root,
                        Reset = true
                    };
                    SettingsService.ConfigureDemoDefaults(localOptions.BaseUrl, model);
                    // This scripted model covers tools/repair; compaction has its own fixture.
                    new SettingsService(AppDataPaths.CreateForRoot(root)).Save(new AppSettings {
                        AutoConfirmToolActions = true, ContextWindowOverrideTokens = 131072 });
                    var bridge = CreateBridgeHost(localOptions);
                    await ExerciseModelAsync(bridge, model).ConfigureAwait(false);
                    Console.WriteLine("PASS " + model);
                }
                catch (Exception ex)
                {
                    failed += 1;
                    Console.WriteLine("FAIL " + model + ": " + ex.Message);
                    if (Environment.GetEnvironmentVariable("RNA_MOCK_TRACE") == "1") Console.WriteLine(ex);
                }
                finally
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, true);
                    }
                }
            }
            try
            {
                await ExerciseFailedTurnPersistenceAsync().ConfigureAwait(false);
                Console.WriteLine("PASS failed-turn-persistence");
            }
            catch (Exception ex)
            {
                failed += 1;
                Console.WriteLine("FAIL failed-turn-persistence: " + ex.Message);
                if (Environment.GetEnvironmentVariable("RNA_MOCK_TRACE") == "1") Console.WriteLine(ex);
            }

            Console.WriteLine(failed == 0 ? "OK" : "FAILED " + failed);
            return failed == 0 ? 0 : 1;
        }

        private static async Task ExerciseFailedTurnPersistenceAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "RNAssistant.MockDemo.Failure." + Guid.NewGuid().ToString("N"));
            try
            {
                var committedProjectionQueued = false;
                var transportCalled = false;
                JObject committedProjection = null;
                var controller = new AssistantController(
                    FakeOfficeAdapter.ForHost("Excel"),
                    AppDataPaths.CreateForRoot(root),
                    delegate(AppSettings settings, System.Collections.Generic.IEnumerable<ChatMessage> requestMessages, CancellationToken cancellationToken)
                    {
                        transportCalled = true;
                        if (!committedProjectionQueued)
                        {
                            throw new InvalidOperationException("model transport started before committed projection");
                        }
                        return Task.FromException<LlmCompletionResult>(new InvalidOperationException("scripted transport failure"));
                    });
                var bridge = new MockBridgeHost(controller, eventJson =>
                {
                    var message = JObject.Parse(eventJson);
                    if (string.Equals((string)message["type"], "chatState", StringComparison.Ordinal) &&
                        string.Equals((string)message["scope"], "full", StringComparison.Ordinal) && !transportCalled &&
                        message["payload"]?["messages"] is JArray projectedMessages &&
                        projectedMessages.OfType<JObject>().Any(item => (string)item["Content"] == "persist failed turn"))
                    {
                        committedProjection = message["payload"] as JObject;
                        committedProjectionQueued = committedProjection != null;
                    }
                });
                var init = await SendAsync(bridge, "failure-init", "init", null, null).ConfigureAwait(false);
                var token = Payload(init)["bridgeToken"].ToString();
                var chatId = Payload(init)["init"]["activeChatId"].ToString();
                var bytes = System.Text.Encoding.UTF8.GetBytes("durable attachment");
                var upload = controller.BeginChatResourceUpload(new ResourceUploadOpenRequest
                {
                    ChatId = chatId,
                    FileName = "failure-note.txt",
                    ContentType = "text/plain",
                    ByteLength = bytes.Length
                });
                using (var body = new MemoryStream(bytes, false))
                using (var response = controller.HandleResourceData(
                    "POST",
                    upload.Url + "?offset=0&count=" + bytes.Length,
                    CancellationToken.None,
                    body).Body)
                {
                }
                var draft = await controller.CompleteChatResourceUploadAsync(new ResourceUploadLeaseRequest
                {
                    ChatId = chatId,
                    LeaseId = upload.LeaseId
                }).ConfigureAwait(false);
                var request = JsonConvert.SerializeObject(new
                {
                    contractVersion = BridgeOperationCatalog.Version,
                    id = "failure-send",
                    type = "submitChatInput",
                    bridgeToken = token,
                    payload = new
                    {
                        chatId = chatId,
                        text = "persist failed turn",
                        operationId = "failure-input", delivery = "Queue",
                        resourceDraftIds = new[] { draft.Resource.Id }
                    }
                });
                var failedPacket = await bridge.HandleAsync(request).ConfigureAwait(false);
                if (!(bool)JObject.Parse(failedPacket.Response)["ok"]) throw new InvalidOperationException("intake failed");
                await bridge.DrainAsync().ConfigureAwait(false);
                var failedResponse = await SendAsync(bridge, "failure-state", "getChatState", new { chatId }, token).ConfigureAwait(false);
                var failedPayload = failedResponse["payload"] as JObject;
                if (!(bool)failedResponse["ok"] || failedPayload == null ||
                    !string.Equals((string)failedPayload["runViewState"]?["Lifecycle"], "failed", StringComparison.OrdinalIgnoreCase) ||
                    failedPayload.ToString().IndexOf("scripted transport failure", StringComparison.Ordinal) < 0)
                {
                    throw new InvalidOperationException("transport failure did not produce typed failed state");
                }

                if (!transportCalled || committedProjection == null || (long?)committedProjection["sessionRevision"] <= 0)
                {
                    throw new InvalidOperationException("committed projection was not queued before transport");
                }
                var committedUser = ((JArray)committedProjection["messages"])
                    .OfType<JObject>()
                    .Single(message => string.Equals((string)message["Role"], "user", StringComparison.OrdinalIgnoreCase));
                var committedAttachment = ((JArray)committedUser["Attachments"]).OfType<JObject>().Single();
                var committedArtifactId = ((JArray)committedUser["ArtifactIds"]).Values<string>().Single();
                var committedArtifact = ((JArray)committedProjection["artifacts"])
                    .OfType<JObject>()
                    .Single(artifact => (string)artifact["id"] == committedArtifactId);
                var committedReference = new ChatStore(AppDataPaths.CreateForRoot(root)).Load(chatId).Messages
                    .Single(item => item.Id == (string)committedUser["Id"]).ResourceRefs.Single();
                if (string.IsNullOrWhiteSpace((string)committedAttachment["FileName"]) ||
                    (int)committedArtifact["revision"] != 1 || string.IsNullOrWhiteSpace(committedReference.Revision) ||
                    (string)committedArtifact["resourceUri"] != committedReference.Uri)
                {
                    throw new InvalidOperationException("projection contains draft or unpinned resource state");
                }

                var selected = await SendAsync(
                    bridge,
                    "failure-select",
                    "selectChat",
                    new { chatId = chatId },
                    token).ConfigureAwait(false);
                var selectedPayload = Payload(selected);
                var storedMessages = selectedPayload["messages"] as JArray;
                var storedJson = storedMessages == null ? string.Empty : storedMessages.ToString(Formatting.None);
                if (storedJson.IndexOf("persist failed turn", StringComparison.Ordinal) < 0 ||
                    !string.Equals((string)selectedPayload["runViewState"]?["Lifecycle"], "failed", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("failed user turn and diagnostic were not persisted");
                }
                var storedArtifacts = selectedPayload["artifacts"] as JArray;
                if (storedArtifacts == null || storedArtifacts.OfType<JObject>().All(artifact =>
                    !string.Equals((string)artifact["resourceUri"], committedReference.Uri, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException("provider failure rolled back the committed resource");
                }
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private static async Task ExerciseLiveToolArtifactProjectionAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "RNAssistant.MockDemo.LiveArtifact." + Guid.NewGuid().ToString("N"));
            try
            {
                var terminalReturned = false;
                var liveArtifactProjected = false;
                var llm = new ScriptedDemoLlm("Excel");
                new SettingsService(AppDataPaths.CreateForRoot(root)).Save(new AppSettings {
                    AutoConfirmToolActions = true, ContextWindowOverrideTokens = 131072 });
                var controller = new AssistantController(
                    FakeOfficeAdapter.ForHost("Excel"),
                    AppDataPaths.CreateForRoot(root),
                    llm.CompleteAsync);
                var bridge = new MockBridgeHost(controller, eventJson =>
                {
                    var message = JObject.Parse(eventJson);
                    if (!string.Equals((string)message["type"], "chatState", StringComparison.Ordinal) ||
                        !string.Equals((string)message["scope"], "full", StringComparison.Ordinal)) return;
                    var artifacts = message["payload"]?["artifacts"] as JArray;
                    if (!terminalReturned && artifacts != null && artifacts.OfType<JObject>().Any(artifact =>
                        string.Equals((string)artifact["kind"], ChatArtifactKinds.HtmlWorkspace, StringComparison.OrdinalIgnoreCase)))
                    {
                        liveArtifactProjected = true;
                    }
                });
                var init = await SendAsync(bridge, "live-init", "init", null, null).ConfigureAwait(false);
                var token = Payload(init)["bridgeToken"].ToString();
                var chatId = Payload(init)["init"]["activeChatId"].ToString();
                await SendAsync(
                    bridge,
                    "live-send",
                    "submitChatInput",
                    new { chatId = chatId, text = "Создай HTML dashboard с таблицей продаж." },
                    token).ConfigureAwait(false);
                terminalReturned = true;
                if (!liveArtifactProjected)
                {
                    throw new InvalidOperationException("tool artifact was not projected before terminal response");
                }
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private static async Task ExerciseModelAsync(MockBridgeHost bridge, string model)
        {
            var init = await SendAsync(bridge, "1", "init", null, null).ConfigureAwait(false);
            var token = Payload(init)["bridgeToken"].ToString();
            var chatId = Payload(init)["init"]["activeChatId"].ToString();
            if (!string.Equals((string)Payload(init)["init"]["activeChatMode"], "agent", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("new chat did not default to Agent mode");
            }
            await SendAsync(bridge, "2", "setChatModel", new { chatId = chatId, model = model }, token).ConfigureAwait(false);
            var mode = await SendAsync(bridge, "2-mode", "setChatMode", new { chatId = chatId, mode = "agent" }, token).ConfigureAwait(false);
            if (!string.Equals((string)Payload(mode)["activeChatMode"], "agent", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Agent mode was not persisted by the bridge");
            }
            var send = await SendAsync(
                bridge,
                "3",
                "submitChatInput",
                new { chatId = chatId, text = "Создай демо отчет продаж с таблицей и графиком." },
                token).ConfigureAwait(false);
            var payload = Payload(send);
            var message = LastAnswer(payload);
            if (message.IndexOf("Demo Report", StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidOperationException(
                    "final answer did not mention Demo Report: " + message +
                    "; run=" + payload["runViewState"]?.ToString(Formatting.None));
            }

            var messages = payload["messages"] as JArray;
            if (messages == null || messages.Count < 2)
            {
                throw new InvalidOperationException("chat messages were not returned");
            }
            var transcriptJson = messages.ToString(Formatting.None);
            if (transcriptJson.IndexOf("excel.sheet.add", StringComparison.OrdinalIgnoreCase) < 0 ||
                transcriptJson.IndexOf("excel.chart.upsert", StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidOperationException("agent transcript did not retain executed tool activities");
            }

            var plain = await SendAsync(
                bridge,
                "4",
                "submitChatInput",
                new { chatId = chatId, text = "Что такое EBITDA простыми словами?" },
                token).ConfigureAwait(false);
            var plainPayload = Payload(plain);
            var plainMessage = LastAnswer(plainPayload);
            if (plainMessage.IndexOf("EBITDA", StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidOperationException("plain answer did not answer the ordinary question: " + plainMessage);
            }

            if (CurrentToolActivities(plainPayload).Any())
            {
                throw new InvalidOperationException("plain answer unexpectedly executed tools");
            }

            var htmlCreate = await SendAsync(
                bridge,
                "5",
                "submitChatInput",
                new { chatId = chatId, text = "Сделай HTML страницу Sales HTML Dashboard с данными продаж, CSS и отдельным JS скриптом." },
                token).ConfigureAwait(false);
            var htmlCreatePayload = Payload(htmlCreate);
            AssertHtmlWorkspace(htmlCreatePayload, false);

            var htmlEdit = await SendAsync(
                bridge,
                "6",
                "submitChatInput",
                new { chatId = chatId, text = "Обнови HTML страницу: добавь март в данные и измени JS так, чтобы total стал обновленным." },
                token).ConfigureAwait(false);
            var htmlEditPayload = Payload(htmlEdit);
            AssertHtmlWorkspace(htmlEditPayload, true);

        }

        private static void AssertHtmlWorkspace(JObject payload, bool expectEdit)
        {
            var workspace = payload["htmlWorkspace"] as JObject;
            if (workspace == null)
            {
                throw new InvalidOperationException("htmlWorkspace was not returned");
            }

            var files = (workspace["files"] ?? workspace["Files"]) as JArray;
            var dataSources = (workspace["dataSources"] ?? workspace["DataSources"]) as JArray;
            if (files == null || files.Count < 3)
            {
                throw new InvalidOperationException("HTML workspace files were not created; run=" + payload["runViewState"]?.ToString(Formatting.None));
            }
            if (dataSources == null || dataSources.Count != 1)
            {
                throw new InvalidOperationException("HTML workspace data source was not created");
            }

            var hasHtml = false;
            var hasCss = false;
            var hasScript = false;
            foreach (var token in files.OfType<JObject>())
            {
                var kind = ((string)(token["kind"] ?? token["Kind"]) ?? string.Empty).ToLowerInvariant();
                var path = ((string)(token["path"] ?? token["Path"]) ?? string.Empty).ToLowerInvariant();
                hasHtml = hasHtml || kind == "html" || path.EndsWith(".html", StringComparison.OrdinalIgnoreCase);
                hasCss = hasCss || kind == "css" || path.EndsWith(".css", StringComparison.OrdinalIgnoreCase);
                if (kind == "script" || path.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                {
                    hasScript = true;
                }
                if ((int?)(token["byteLength"] ?? token["ByteLength"]) <= 0 ||
                    string.IsNullOrWhiteSpace((string)(token["sha256"] ?? token["Sha256"])))
                    throw new InvalidOperationException("HTML workspace file metadata is incomplete");
            }

            if (!hasHtml || !hasCss || !hasScript)
            {
                throw new InvalidOperationException("HTML workspace must contain html, css, and script files");
            }

            var data = (JObject)dataSources[0];
            var binding = (data["binding"] ?? data["Binding"]) as JObject;
            var resource = binding == null ? null : (binding["resource"] ?? binding["Resource"]) as JObject;
            if (!string.Equals((string)(data["name"] ?? data["Name"]), "sales", StringComparison.OrdinalIgnoreCase) ||
                resource == null || string.IsNullOrWhiteSpace((string)(resource["uri"] ?? resource["Uri"])) ||
                string.IsNullOrWhiteSpace((string)(resource["revision"] ?? resource["Revision"])))
            {
                throw new InvalidOperationException("HTML workspace data source does not expose an exact resource binding");
            }

            if (expectEdit)
            {
                var results = CurrentToolActivities(payload).ToList();
                foreach (var expected in new[] { "html.data.write", "html.source.write" })
                    if (!results.Any(item =>
                        string.Equals((string)item["ToolId"], expected, StringComparison.Ordinal) &&
                        string.Equals((string)item["Status"], "completed", StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("HTML edit did not complete " + expected);
            }
        }

        private static async Task<JObject> SendAsync(MockBridgeHost bridge, string id, string type, object payload, string token)
        {
            if (type == "submitChatInput")
            {
                var input = JObject.FromObject(payload);
                input["operationId"] = id;
                input["delivery"] = "Steer";
                payload = input;
            }
            var request = JsonConvert.SerializeObject(new
            {
                contractVersion = BridgeOperationCatalog.Version,
                id = id,
                type = type,
                bridgeToken = token,
                payload = payload ?? new { }
            });
            var packet = await bridge.HandleAsync(request).ConfigureAwait(false);
            var response = JObject.Parse(packet.Response);
            if (!(bool)response["ok"])
            {
                throw new InvalidOperationException((string)response["error"]);
            }

            if (type == "submitChatInput")
            {
                await bridge.DrainAsync().ConfigureAwait(false);
                return await SendAsync(bridge, id + "-state", "getChatState",
                    new { chatId = (string)((JObject)payload)["chatId"] }, token).ConfigureAwait(false);
            }
            return response;
        }

        private static string LastAnswer(JObject state)
        {
            return ((JArray)state["messages"]).OfType<JObject>()
                .Where(message => (string)message["Role"] == "assistant" &&
                    (bool?)message["ProtocolMessage"] != true && message["Activity"]?.Type != JTokenType.Object)
                .Select(message => (string)message["Content"]).LastOrDefault() ?? string.Empty;
        }

        private static System.Collections.Generic.IEnumerable<JObject> CurrentToolActivities(JObject state)
        {
            var runId = (string)state["runViewState"]?["RunId"];
            return ((JArray)state["messages"]).OfType<JObject>().Where(message => (string)message["RunId"] == runId)
                .Select(message => message["Activity"]).OfType<JObject>()
                .SelectMany(activity => activity.DescendantsAndSelf().OfType<JObject>())
                .Where(activity => !string.IsNullOrWhiteSpace((string)activity["ToolId"]));
        }

        private static JObject Payload(JObject response)
        {
            return response["payload"] as JObject ?? new JObject();
        }

        private static string ResolveWebRoot()
        {
            var current = Directory.GetCurrentDirectory();
            var found = FindWebRoot(current);
            if (!string.IsNullOrWhiteSpace(found))
            {
                return found;
            }

            found = FindWebRoot(AppDomain.CurrentDomain.BaseDirectory);
            if (!string.IsNullOrWhiteSpace(found))
            {
                return found;
            }

            throw new DirectoryNotFoundException("Could not locate web/index.html from " + current);
        }

        private static string FindWebRoot(string start)
        {
            var directory = new DirectoryInfo(start);
            while (directory != null)
            {
                var candidate = Path.Combine(directory.FullName, "web");
                if (File.Exists(Path.Combine(candidate, "index.html")))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            return null;
        }
    }
}
