using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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

            Console.WriteLine(failed == 0 ? "OK" : "FAILED " + failed);
            return failed == 0 ? 0 : 1;
        }

        private static async Task ExerciseModelAsync(MockBridgeHost bridge, string model)
        {
            var init = await SendAsync(bridge, "1", "init", null, null).ConfigureAwait(false);
            var token = Payload(init)["bridgeToken"].ToString();
            var chatId = Payload(init)["activeChatId"].ToString();
            await SendAsync(bridge, "2", "setChatModel", new { chatId = chatId, model = model }, token).ConfigureAwait(false);
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
                throw new InvalidOperationException("final answer did not mention Demo Report: " + message);
            }

            var messages = payload["messages"] as JArray;
            if (messages == null || messages.Count < 2)
            {
                throw new InvalidOperationException("chat messages were not returned");
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
