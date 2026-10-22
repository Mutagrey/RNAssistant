using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        // Explicit red regression for the v8 audit, outside the default suite.
        // Real HTTP/production runtime; deterministic responses and fake Office.
        // Replace the v7 script with v8 forms at the atomic protocol cutover.
        private static int ConversationV8Audit(string outputDirectory)
        {
            var admissionGap = false;
            try
            {
                var output = Path.GetFullPath(outputDirectory);
                if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
                    throw new InvalidOperationException("Audit output must be a new or empty directory.");
                Directory.CreateDirectory(output);
                WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
                {
                    const string promise = "Лист Report создан. Теперь заполню данные и построю график.";
                    var responses = new[]
                    {
                        LoadToolSchemaResponse("excel.add_sheet"),
                        "{\"message\":\"Добавляю лист.\",\"action\":\"tool\",\"tool_calls\":[{\"name\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Report\"}}]}",
                        "{\"message\":\"" + promise + "\",\"action\":\"done\",\"tool_calls\":[]}"
                    };
                    var probe = new TcpListener(IPAddress.Loopback, 0);
                    probe.Start();
                    var port = ((IPEndPoint)probe.LocalEndpoint).Port;
                    probe.Stop();
                    using (var listener = new HttpListener())
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                    {
                        listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                        listener.Start();
                        var served = 0;
                        var http = new List<JObject>();
                        var server = Task.Run(async () =>
                        {
                            try
                            {
                                while (true)
                                {
                                    var context = await listener.GetContextAsync().ConfigureAwait(false);
                                    var number = Interlocked.Increment(ref served);
                                    var stem = "http-" + number.ToString("D2");
                                    byte[] request;
                                    using (var body = new MemoryStream())
                                    {
                                        await context.Request.InputStream.CopyToAsync(body, 81920, timeout.Token).ConfigureAwait(false);
                                        request = body.ToArray();
                                    }
                                    File.WriteAllBytes(Path.Combine(output, stem + ".request.json"), request);
                                    var response = new JObject
                                    {
                                        ["choices"] = new JArray(new JObject
                                        {
                                            ["index"] = 0,
                                            ["message"] = new JObject { ["role"] = "assistant",
                                                ["content"] = responses[Math.Min(number - 1, responses.Length - 1)] },
                                            ["finish_reason"] = "stop"
                                        })
                                    }.ToString(Formatting.None);
                                    var bytes = Encoding.UTF8.GetBytes(response);
                                    File.WriteAllBytes(Path.Combine(output, stem + ".response.json"), bytes);
                                    http.Add(new JObject { ["exchange"] = number,
                                        ["method"] = context.Request.HttpMethod, ["path"] = context.Request.RawUrl,
                                        ["status"] = 200, ["requestBytes"] = request.Length, ["responseBytes"] = bytes.Length });
                                    context.Response.ContentType = "application/json";
                                    context.Response.ContentLength64 = bytes.Length;
                                    await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, timeout.Token).ConfigureAwait(false);
                                    context.Response.Close();
                                }
                            }
                            catch (HttpListenerException) when (!listener.IsListening) { }
                            catch (ObjectDisposedException) when (!listener.IsListening) { }
                        });
                        try
                        {
                            var store = new ChatStore(FixturePaths.Value);
                            var session = NewSession(adapter);
                            var catalog = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                            var settings = new AppSettings { BaseUrl = "http://127.0.0.1:" + port,
                                Model = "scripted-v7-premature-done", StreamResponses = false,
                                SmartChatTitles = false, AutoCompressContext = false,
                                AutoConfirmToolActions = true, MaxAgentIterations = 4, RequestTimeoutSeconds = 10 };
                            var client = new LlmClient(() => null);
                            var trace = new ModelTracePersistenceService(EventStore(store));
                            var service = CreateConversationRunService(adapter, executor, (current, messages, options, stream, token) =>
                            {
                                trace.Configure(options);
                                return client.CompleteAsync(current, messages, options, stream, token);
                            });
                            var progress = new JArray();
                            var result = service.ExecuteWithApprovalsAsync(ChatModes.Agent,
                                    "Создай лист Report, заполни A1:B4 данными и построй по ним график.",
                                    session, NewContext(adapter), settings, catalog,
                                    (phase, message, activity) => progress.Add(new JObject {
                                        ["phase"] = phase, ["message"] = message, ["tool"] = activity?.ToolId,
                                        ["callId"] = activity?.ToolCallId, ["status"] = activity?.Status }),
                                    cancellationToken: timeout.Token).GetAwaiter().GetResult();
                            var events = store.ReadEvents(session.Host, session.DocumentKey, session.Id);
                            var replay = new ChatStore(FixturePaths.Value).Load(session.Host, session.DocumentKey, session.Id);
                            var parsed = ModelProtocolWire.Parse(responses[2], catalog, catalog,
                                new ModelProtocolCallContext(new string[0]));
                            var accepted = replay.Messages.Where(message => message.ProtocolMessage &&
                                message.Role == "assistant" && !string.IsNullOrEmpty(message.ToolCallId)).ToArray();
                            var summary = replay.LastRun.KernelState.Summary;
                            var report = new JObject
                            {
                                ["kind"] = "scripted_http_fake_office",
                                ["protocolVersion"] = ConversationResponse.ProtocolVersion,
                                ["responseMode"] = settings.AgentResponseMode, ["toolResultRole"] = settings.ToolResultRole,
                                ["model"] = settings.Model, ["stream"] = settings.StreamResponses,
                                ["httpExchanges"] = served,
                                ["parser"] = new JObject { ["success"] = parsed.Success, ["error"] = parsed.Error,
                                    ["action"] = parsed.Response?.Action, ["final"] = parsed.Response?.Final },
                                ["acceptedCalls"] = JArray.FromObject(accepted.Select(message => new {
                                    message.ToolCallId, message.ToolName, message.AcceptedCallOrigin })),
                                ["kernelSummary"] = JObject.FromObject(summary),
                                ["uiProjection"] = JObject.FromObject(result.RunViewState),
                                ["responseStatus"] = result.ResponseStatus,
                                ["sheetCreated"] = adapter.HasSheet("Report"),
                                ["sheetDispatches"] = adapter.ExcelSheetRequests.Count(command => command.ToolId == "excel.add_sheet"),
                                ["rangeDispatches"] = adapter.ExcelRangeMutationRequests.Count,
                                ["chartDispatches"] = adapter.ExcelChartRequests.Count,
                                ["repairRejections"] = events.Count(fact => fact.Type == SessionEventTypes.AgentResponseRejected),
                                ["targetInvariant"] = "A terminal proposal after accepted calls must present results/evidence before admission.",
                                ["targetInvariantMet"] = !(accepted.Length > 0 &&
                                    result.RunViewState.Lifecycle == RunViewLifecycles.Completed)
                            };
                            File.WriteAllText(Path.Combine(output, "trace.json"), report.ToString(Formatting.Indented), new UTF8Encoding(false));
                            File.WriteAllText(Path.Combine(output, "progress.json"), progress.ToString(Formatting.Indented), new UTF8Encoding(false));
                            // Exact decoded event export; raw HTTP bodies above also retain
                            // the wire bytes when the event points to a request/response CAS body.
                            using (var file = File.Create(Path.Combine(output, "events.export.jsonl.gz")))
                            using (var gzip = new GZipStream(file, CompressionMode.Compress))
                            using (var writer = new StreamWriter(gzip, new UTF8Encoding(false)))
                                foreach (var fact in events) writer.WriteLine(JsonConvert.SerializeObject(fact));

                            AssertTrue(parsed.Success && parsed.Response.Final, "v7 parser accepts the premature done");
                            AssertEqual(3, served, "no repair or continuation after premature done");
                            AssertEqual(2, accepted.Length, "schema read and mutation have durable accepted IDs");
                            AssertTrue(accepted.All(message => message.AcceptedCallOrigin != null), "durable model-attempt origins");
                            AssertEqual(1, result.RunViewState.VerifiedWrites, "sheet write has actual typed read-back");
                            AssertEqual(1, adapter.ExcelSheetRequests.Count(command => command.ToolId == "excel.add_sheet"), "mutation dispatched exactly once");
                            AssertEqual(0, adapter.ExcelRangeMutationRequests.Count, "requested data was never written");
                            AssertEqual(0, adapter.ExcelChartRequests.Count, "requested chart was never created");
                            AssertEqual("model_done", summary.Reason, "replayed kernel terminal reason");
                            AssertEqual(RunGoalVerification.NotAssessed, result.RunViewState.GoalVerification,
                                "current UI already distinguishes lifecycle from goal verification");
                            AssertTrue(JToken.DeepEquals(JObject.FromObject(result.RunViewState),
                                JObject.FromObject(replay.Messages.Last().RunViewState)), "UI projection survives replay");
                            AssertEqual(promise, result.AssistantText, "promise is the terminal answer");
                            AssertTrue(events.Any(fact => fact.Type == SessionEventTypes.ModelResponseAccepted), "actual parser acceptance is traced");
                            Console.WriteLine("Reproduced v7 premature done; HTTP, events and UI projection: " + output);
                            admissionGap = result.RunViewState.Lifecycle == RunViewLifecycles.Completed;
                        }
                        finally
                        {
                            listener.Stop();
                            server.GetAwaiter().GetResult();
                            File.WriteAllText(Path.Combine(output, "http.json"), JArray.FromObject(http).ToString(Formatting.Indented), new UTF8Encoding(false));
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ERROR conversation v8 audit fixture: " + ex.Message);
                return 2;
            }
            if (!admissionGap) return 0;
            Console.Error.WriteLine("FAIL conversation v8 admission: terminal response without results/evidence ended a run after accepted calls");
            return 1;
        }
    }
}
