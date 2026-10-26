using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    // Compiled beside the fake host in the source-linked harness library. The
    // normal Harness suite still contains v7 fixtures and is not used here.
    public static class V8OfficeFinishBridge
    {
        public static void Run()
        {
            var root = Path.Combine(Path.GetTempPath(), "rna-v8-office-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try { Check(AppDataPaths.CreateForRoot(root)); }
            finally { Directory.Delete(root, true); }
        }

        private static void Check(AppDataPaths paths)
        {
            var adapter = FakeOfficeAdapter.ForHost("Excel");
            var portProbe = new TcpListener(IPAddress.Loopback, 0);
            portProbe.Start();
            var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
            portProbe.Stop();
            var settings = new AppSettings { BaseUrl = "http://127.0.0.1:" + port,
                Model = "scripted-v8-office", StreamResponses = false,
                SmartChatTitles = false, AutoCompressContext = false,
                AutoConfirmToolActions = true, MaxAgentIterations = 5,
                RequestTimeoutSeconds = 10 };
            var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths),
                new SkillStore(paths), new ToolStore(paths), () => settings,
                value => settings = value, paths);
            var store = new ChatStore(paths);
            var session = new ChatSession { Host = adapter.HostName,
                DocumentKey = adapter.DocumentKey, DocumentTitle = adapter.DocumentTitle,
                Title = "New chat" };
            var catalog = OfficeToolCatalog.ForHost(adapter.HostName)
                .Concat(executor.GetControllerTools()).ToList();
            var client = new LlmClient(() => null);
            var service = new ConversationRunService(adapter, executor,
                new ChatConversationStoreAdapter(store), new ChatEventStoreAdapter(store),
                (current, messages, options, stream, token) =>
                    client.CompleteAsync(current, messages, options, stream, token));
            var requests = new List<JObject>();
            string sheetCallId = null;
            using (var listener = new HttpListener())
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                listener.Start();
                var server = Task.Run(async () =>
                {
                    for (var step = 1; step <= 4; step++)
                    {
                        var exchange = await listener.GetContextAsync().ConfigureAwait(false);
                        string body;
                        using (var reader = new StreamReader(exchange.Request.InputStream))
                            body = await reader.ReadToEndAsync().ConfigureAwait(false);
                        var request = JObject.Parse(body);
                        requests.Add(request);
                        JObject answer;
                        if (step == 1)
                            answer = Execute("common.capabilities_read", new JObject { ["id"] = "excel.add_sheet" });
                        else if (step == 2)
                            answer = Execute("excel.add_sheet", new JObject { ["name"] = "Report" });
                        else if (step == 3)
                            answer = new JObject { ["action"] = "finish", ["reason"] = "Premature claim.", ["outcome"] = "completed",
                                ["results"] = new JArray(),
                                ["message"] = "Лист Report создан. Теперь заполню данные и построю график." };
                        else
                        {
                            sheetCallId = SheetResultId(request);
                            if (sheetCallId == null) throw new InvalidOperationException("Sheet terminal ID missing from model context.");
                            answer = new JObject { ["action"] = "finish", ["reason"] = "Only the sheet exists.", ["outcome"] = "partial",
                                ["results"] = new JArray(new JObject {
                                    ["summary"] = "Лист создан; данные и график не готовы.",
                                    ["evidence"] = new JArray(sheetCallId),
                                    ["assessment"] = "partially_satisfied" }),
                                ["message"] = "Лист создан; данные и график не готовы." };
                        }
                        var response = new JObject { ["choices"] = new JArray(new JObject {
                            ["message"] = new JObject { ["role"] = "assistant",
                                ["content"] = answer.ToString(Formatting.None) } }) };
                        var bytes = Encoding.UTF8.GetBytes(response.ToString(Formatting.None));
                        exchange.Response.ContentType = "application/json";
                        exchange.Response.ContentLength64 = bytes.Length;
                        await exchange.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length,
                            timeout.Token).ConfigureAwait(false);
                        exchange.Response.Close();
                    }
                });
                ChatTurnResult result;
                try
                {
                    result = service.ExecuteAsync(ChatModes.Agent,
                        "Создай лист Report, заполни A1:B4 данными и построй по ним график.",
                        session, new DocumentContext { Host = adapter.HostName,
                            DocumentKey = adapter.DocumentKey, Title = adapter.DocumentTitle },
                        settings, catalog, null, cancellationToken: timeout.Token)
                        .GetAwaiter().GetResult();
                    server.GetAwaiter().GetResult();
                }
                finally { listener.Stop(); }
                var replay = new ChatStore(paths).Load(session.Host, session.DocumentKey, session.Id);
                var accepted = replay.Messages.Where(message => message.ProtocolMessage &&
                    message.Role == "assistant" && !string.IsNullOrEmpty(message.ToolCallId)).ToArray();
                if (requests.Count != 4 || sheetCallId == null ||
                    !requests[3]["messages"].ToString(Formatting.None).Contains("missing_finish_results") ||
                    !adapter.HasSheet("Report") ||
                    adapter.ExcelSheetRequests.Count(command => command.ToolId == "excel.add_sheet") != 1 ||
                    adapter.ExcelRangeMutationRequests.Count != 0 || adapter.ExcelChartRequests.Count != 0 ||
                    accepted.Length != 2 || accepted.Any(message => message.AcceptedCallOrigin == null) ||
                    replay.Messages.Any(message => message.ProtocolMessage && message.Role == "assistant" &&
                        (message.Content ?? string.Empty).Contains("Теперь заполню данные и построю график")) ||
                    result.RunViewState.Lifecycle != RunViewLifecycles.Completed ||
                    result.RunViewState.GoalVerification != RunGoalVerification.NotAssessed ||
                    result.RunViewState.VerifiedWrites != 1 ||
                    result.RunViewState.ModelResults.Count != 1 ||
                    result.RunViewState.ModelResults[0].Assessment != "partially_satisfied" ||
                    !result.RunViewState.ModelResults[0].Evidence.SequenceEqual(new[] { sheetCallId }) ||
                    !JToken.DeepEquals(JObject.FromObject(result.RunViewState),
                        JObject.FromObject(replay.Messages.Last().RunViewState)) ||
                    result.AssistantText != "Лист создан; данные и график не готовы.")
                    throw new InvalidOperationException("v8 Office finish rejection or replayed partial outcome disagreed with the terminal evidence.");
            }
        }

        private static JObject Execute(string name, JObject arguments)
        {
            return new JObject { ["action"] = "execute", ["reason"] = "Read or create the sheet.",
                ["tool_calls"] = new JArray(new JObject { ["name"] = name,
                    ["arguments"] = arguments }), ["message"] = "" };
        }

        private static string SheetResultId(JObject request)
        {
            foreach (var item in (JArray)request["messages"])
            {
                var content = (string)item["content"];
                if (content == null) continue;
                if (content.StartsWith("TOOL_RESULT:\n", StringComparison.Ordinal))
                    content = content.Substring("TOOL_RESULT:\n".Length);
                else if ((string)item["role"] != "tool") continue;
                JObject result;
                try { result = JObject.Parse(content); }
                catch (JsonException) { continue; }
                if ((string)result["name"] == "excel.add_sheet" && (string)result["status"] == "ok")
                    return (string)result["tool_call_id"];
            }
            return null;
        }
    }
}
