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
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void OfficeHttpPrematureFinishKeepsPartialOutcome()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var paths = FixturePaths.Value;
                var session = NewSession(adapter);
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                var settings = new AppSettings { BaseUrl = "http://127.0.0.1:" + port,
                    Model = "scripted-v8-office", StreamResponses = false,
                    SmartChatTitles = false, AutoCompressContext = false,
                    AutoConfirmToolActions = true, MaxAgentIterations = 5,
                    RequestTimeoutSeconds = 10 };
                var requests = new List<JObject>();
                string evidenceAlias = null;
                using (var listener = new HttpListener())
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
                    listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                    listener.Start();
                    var server = Task.Run(async () =>
                    {
                        for (var step = 1; step <= 5; step++)
                        {
                            var exchange = await listener.GetContextAsync().ConfigureAwait(false);
                            string body;
                            using (var reader = new StreamReader(exchange.Request.InputStream))
                                body = await reader.ReadToEndAsync().ConfigureAwait(false);
                            requests.Add(JObject.Parse(body));
                            string answer;
                            if (step == 1)
                                answer = ModelProtocolWire.WriteAcceptedCall("Read the sheet tool schema.", "",
                                    new ConversationToolCall { Name = "core.tools.read",
                                        Arguments = new JObject { ["id"] = "excel.sheet.add" } });
                            else if (step == 2)
                                answer = ModelProtocolWire.WriteAcceptedCall("Create the requested sheet.", "",
                                    new ConversationToolCall { Name = "excel.sheet.add",
                                        Arguments = new JObject { ["name"] = "Report" } });
                            else if (step == 3)
                                answer = ModelProtocolWire.WriteAcceptedDecision("finish", "Claim all work is done.",
                                    "Лист Report создан. Теперь заполню данные и построю график.",
                                    results: new ConversationResultClaim[0], outcome: ConversationOutcome.Completed);
                            else
                            {
                                var saved = new ChatStore(paths).Load(session.Id);
                                evidenceAlias = saved.Messages.Where(message => message.EvidenceReference != null &&
                                        (message.ToolName == "excel.sheet.add" || message.Activity?.ToolId == "excel.sheet.add"))
                                    .Select(message => message.EvidenceReference.Alias).Distinct().Single();
                                answer = ModelProtocolWire.WriteAcceptedDecision("finish", "Only the sheet exists.",
                                    "Лист создан; данные и график не готовы.",
                                    results: new[] { new ConversationResultClaim("The sheet was created; data and chart are pending.",
                                        new[] { evidenceAlias }, ConversationAssessment.PartiallySatisfied) },
                                    outcome: ConversationOutcome.Partial);
                            }
                            var response = new JObject { ["choices"] = new JArray(new JObject {
                                ["message"] = new JObject { ["role"] = "assistant", ["content"] = answer } }) };
                            var bytes = Encoding.UTF8.GetBytes(response.ToString(Formatting.None));
                            exchange.Response.ContentType = "application/json";
                            exchange.Response.ContentLength64 = bytes.Length;
                            await exchange.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length,
                                timeout.Token).ConfigureAwait(false);
                            exchange.Response.Close();
                        }
                    });
                    var client = new LlmClient(() => null);
                    var service = CreateConversationRunService(adapter, executor,
                        (current, messages, options, stream, token) =>
                            client.CompleteAsync(current, messages, options, stream, token));
                    ChatTurnResult result;
                    try
                    {
                        result = service.ExecuteAsync(ChatModes.Agent,
                            "Создай лист Report, заполни A1:B4 данными и построй по ним график.",
                            session, NewContext(adapter), settings,
                            OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(),
                            null, cancellationToken: timeout.Token).GetAwaiter().GetResult();
                        server.GetAwaiter().GetResult();
                    }
                    finally { listener.Stop(); }

                    var replay = new ChatStore(paths).Load(session.Id);
                    var calls = replay.Messages.Where(message => message.AcceptedCallOrigin != null).ToArray();
                    AssertEqual(1, calls.Count(message => message.ToolName == "excel.sheet.add"),
                        "one accepted sheet call is retained");
                    AssertEqual(5, requests.Count, "schema read, write, rejected finish, partial recheck and final finish");
                    AssertContains(requests[3]["messages"].ToString(Formatting.None), "missing_finish_results",
                        "premature finish produces correction feedback");
                    AssertContains(requests[4]["messages"].ToString(Formatting.None),
                        "The proposed finish with outcome=partial was not accepted yet",
                        "partial finish receives the required recheck");
                    AssertContains(requests[4]["messages"].ToString(Formatting.None), evidenceAlias,
                        "final finish can cite delivered evidence");
                    AssertTrue(adapter.HasSheet("Report"), "the sheet was created");
                    AssertEqual(1, adapter.ExcelSheetRequests.Count(command => command.ToolId == "excel.sheet.add"),
                        "rejected finish does not repeat the write");
                    AssertEqual(0, adapter.ExcelRangeMutationRequests.Count, "data was not populated");
                    AssertEqual(0, adapter.ExcelChartRequests.Count, "chart was not created");
                    AssertEqual(2, calls.Length, "only the two accepted calls enter history");
                    AssertTrue(!replay.Messages.Any(message => message.ProtocolMessage &&
                        (message.Content ?? string.Empty).Contains("Теперь заполню данные и построю график")),
                        "rejected finish never enters accepted history");
                    AssertEqual(RunViewLifecycles.Completed, result.RunViewState.Lifecycle, "corrected finish completes run");
                    AssertEqual(ConversationFinishContract.Name(ConversationOutcome.Partial), result.RunViewState.ModelOutcome,
                        "the model reports partial completion");
                    AssertEqual(RunGoalVerification.NotAssessed, result.RunViewState.GoalVerification,
                        "model outcome is not runtime goal verification");
                    AssertEqual(1, result.RunViewState.VerifiedWrites, "one verified write is projected");
                    AssertEqual("partially_satisfied", result.RunViewState.ModelResults.Single().Assessment,
                        "partial assessment survives replay");
                    AssertEqual(evidenceAlias, result.RunViewState.ModelResults.Single().Evidence.Single(),
                        "claim retains the delivered write evidence");
                    AssertTrue(JToken.DeepEquals(JObject.FromObject(result.RunViewState),
                        JObject.FromObject(replay.Messages.Last().RunViewState)), "saved view matches live projection");
                    AssertEqual("Лист создан; данные и график не готовы.", result.AssistantText,
                        "visible answer reports only completed work");
                }
            });
        }
    }
}
