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

namespace RNAssistant.MockDemo
{
    internal static class Program
    {
        public static int Main(string[] args)
        {
            return MainAsync(args).GetAwaiter().GetResult();
        }

        private static async Task<int> MainAsync(string[] args)
        {
            var options = DemoOptions.Parse(args);
            SettingsService.ConfigureDemoDefaults(options.BaseUrl, "mock-strict");
            if (options.ArtifactCommitTest)
            {
                try
                {
                    await ExerciseFailedTurnPersistenceAsync().ConfigureAwait(false);
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
                    var bridge = CreateBridgeHost(localOptions);
                    await ExerciseModelAsync(bridge, model).ConfigureAwait(false);
                    Console.WriteLine("PASS " + model);
                }
                catch (Exception ex)
                {
                    failed += 1;
                    Console.WriteLine("FAIL " + model + ": " + ex.Message);
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
                        string.Equals((string)message["scope"], "full", StringComparison.Ordinal))
                    {
                        committedProjection = message["payload"] as JObject;
                        committedProjectionQueued = committedProjection != null;
                    }
                });
                var init = await SendAsync(bridge, "failure-init", "init", null, null).ConfigureAwait(false);
                var token = Payload(init)["bridgeToken"].ToString();
                var chatId = Payload(init)["activeChatId"].ToString();
                var draft = controller.StageChatResource(
                    chatId,
                    "failure-note.txt",
                    "text/plain",
                    Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("durable attachment")));
                var request = JsonConvert.SerializeObject(new
                {
                    id = "failure-send",
                    type = "sendChat",
                    bridgeToken = token,
                    payload = new
                    {
                        chatId = chatId,
                        text = "persist failed turn",
                        resourceDraftIds = new[] { draft.Resource.Id }
                    }
                });
                var failedPacket = await bridge.HandleAsync(request).ConfigureAwait(false);
                var failedResponse = JObject.Parse(failedPacket.Response);
                var failedPayload = failedResponse["payload"] as JObject;
                if (!(bool)failedResponse["ok"] || failedPayload == null ||
                    !string.Equals((string)failedPayload["runViewState"]?["Lifecycle"], "failed", StringComparison.OrdinalIgnoreCase) ||
                    ((string)failedPayload["message"] ?? string.Empty).IndexOf("scripted transport failure", StringComparison.Ordinal) < 0)
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
                var committedReference = ((JArray)committedUser["ResourceRefs"]).OfType<JObject>().Single();
                var committedArtifact = ((JArray)committedProjection["artifacts"])
                    .OfType<JObject>()
                    .Single(artifact => string.Equals((string)artifact["resourceUri"], (string)committedReference["uri"], StringComparison.Ordinal));
                if (committedAttachment["DraftChatId"] != null ||
                    (int)committedArtifact["revision"] != 1 || string.IsNullOrWhiteSpace((string)committedReference["revision"]))
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
                    !string.Equals((string)artifact["resourceUri"], (string)committedReference["uri"], StringComparison.Ordinal)))
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

        private static async Task ExerciseModelAsync(MockBridgeHost bridge, string model)
        {
            var init = await SendAsync(bridge, "1", "init", null, null).ConfigureAwait(false);
            var token = Payload(init)["bridgeToken"].ToString();
            var chatId = Payload(init)["activeChatId"].ToString();
            if (!string.Equals((string)Payload(init)["activeChatMode"], "agent", StringComparison.OrdinalIgnoreCase))
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
                "sendChat",
                new { chatId = chatId, text = "Создай демо отчет продаж с таблицей и графиком." },
                token).ConfigureAwait(false);
            var payload = Payload(send);
            var message = payload["message"] == null ? string.Empty : payload["message"].ToString();
            if (message.IndexOf("Demo Report", StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidOperationException(
                    "final answer did not mention Demo Report: " + message +
                    "; toolResults=" + (payload["toolResults"] == null ? "null" : payload["toolResults"].ToString(Formatting.None)));
            }

            var messages = payload["messages"] as JArray;
            if (messages == null || messages.Count < 2)
            {
                throw new InvalidOperationException("chat messages were not returned");
            }
            var transcriptJson = messages.ToString(Formatting.None);
            if (transcriptJson.IndexOf("excel.add_sheet", StringComparison.OrdinalIgnoreCase) < 0 ||
                transcriptJson.IndexOf("excel.upsert_chart", StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidOperationException("agent transcript did not retain executed tool activities");
            }

            var plain = await SendAsync(
                bridge,
                "4",
                "sendChat",
                new { chatId = chatId, text = "Что такое EBITDA простыми словами?" },
                token).ConfigureAwait(false);
            var plainPayload = Payload(plain);
            var plainMessage = plainPayload["message"] == null ? string.Empty : plainPayload["message"].ToString();
            if (plainMessage.IndexOf("EBITDA", StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidOperationException("plain answer did not answer the ordinary question: " + plainMessage);
            }

            var toolResults = plainPayload["toolResults"] as JArray;
            if (toolResults != null && toolResults.Count != 0)
            {
                throw new InvalidOperationException("plain answer unexpectedly executed tools");
            }

            var htmlCreate = await SendAsync(
                bridge,
                "5",
                "sendChat",
                new { chatId = chatId, text = "Сделай HTML страницу Sales HTML Dashboard с данными продаж, CSS и отдельным JS скриптом." },
                token).ConfigureAwait(false);
            var htmlCreatePayload = Payload(htmlCreate);
            AssertHtmlWorkspace(htmlCreatePayload, false);

            var htmlEdit = await SendAsync(
                bridge,
                "6",
                "sendChat",
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
                throw new InvalidOperationException("HTML workspace files were not created: " + workspace.ToString(Formatting.None));
            }
            if (dataSources == null || dataSources.Count != 1)
            {
                throw new InvalidOperationException("HTML workspace data source was not created");
            }

            var hasHtml = false;
            var hasCss = false;
            var hasScript = false;
            var scriptContent = string.Empty;
            foreach (var token in files.OfType<JObject>())
            {
                var kind = ((string)(token["kind"] ?? token["Kind"]) ?? string.Empty).ToLowerInvariant();
                var path = ((string)(token["path"] ?? token["Path"]) ?? string.Empty).ToLowerInvariant();
                var content = (string)(token["content"] ?? token["Content"]) ?? string.Empty;
                hasHtml = hasHtml || kind == "html" || path.EndsWith(".html", StringComparison.OrdinalIgnoreCase);
                hasCss = hasCss || kind == "css" || path.EndsWith(".css", StringComparison.OrdinalIgnoreCase);
                if (kind == "script" || path.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                {
                    hasScript = true;
                    scriptContent = content;
                }
            }

            if (!hasHtml || !hasCss || !hasScript)
            {
                throw new InvalidOperationException("HTML workspace must contain html, css, and script files");
            }

            var dataJson = (string)(dataSources[0]["json"] ?? dataSources[0]["Json"]) ?? string.Empty;
            if (dataJson.IndexOf("\"sales\"", StringComparison.OrdinalIgnoreCase) < 0 &&
                dataJson.IndexOf("\"rows\"", StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidOperationException("HTML workspace data source does not contain rows");
            }

            if (expectEdit)
            {
                if (dataJson.IndexOf("Mar", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    throw new InvalidOperationException("HTML edit did not add March data");
                }
                if (scriptContent.IndexOf("updated", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    throw new InvalidOperationException("HTML edit did not update app.js");
                }
            }
        }

        private static async Task<JObject> SendAsync(MockBridgeHost bridge, string id, string type, object payload, string token)
        {
            var request = JsonConvert.SerializeObject(new
            {
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

            return response;
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
