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

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ModelDiagnosticsHistory()
        {
            var scenarios = ModelDiagnosticScenarios.NativeHistory("history-regression");
            WithDiagnosticEndpoint(Enumerable.Repeat<Func<JObject, DiagnosticReply>>(DiagnosticAnswer, scenarios.Count), (settings, requests) =>
            {
                var runner = new ModelDiagnosticRunner(new LlmClient(() => "synthetic").CompleteAsync);
                for (var i = 0; i < scenarios.Count; i++)
                {
                    var scenario = scenarios[i];
                    var result = runner.RunAsync(settings, scenario, ModelDiagnosticExecutionKind.Synthetic, CancellationToken.None).GetAwaiter().GetResult();
                    AssertEqual(ModelDiagnosticVerdict.Pass, result.Verdict, scenario.Id + " exact assertion");
                    AssertEqual(requests[i], Encoding.UTF8.GetString(result.RequestUtf8), "trace captures sent bytes");
                    AssertEqual(200, result.HttpStatusCode.Value, "HTTP acceptance separately recorded");
                    AssertTrue(result.RequestSha256.Length == 64 && result.ResponsePayload.Contains("choices"), "exact exchange retained");
                    var messages = (JArray)JObject.Parse(requests[i])["messages"];
                    var native = messages.OfType<JObject>().FirstOrDefault(message => message["tool_calls"] != null);
                    if (scenario.Id == "B1") AssertEqual(JTokenType.Null, native["content"].Type, "literal null");
                    if (scenario.Id == "B2" || scenario.Id == "B4") AssertEqual("", (string)native["content"], "explicit empty/default content");
                    if (scenario.Id == "B3") AssertTrue(native.Property("content") == null, "content omitted");
                    if (scenario.Id == "B5")
                    {
                        var ids = native["tool_calls"].Select(call => (string)call["id"]).ToArray();
                        AssertEqual(string.Join(",", ids.Reverse()), string.Join(",", messages.Where(message => (string)message["role"] == "tool")
                            .Select(message => (string)message["tool_call_id"])), "results reverse the calls");
                    }
                    var expectedMarker = (string)JObject.Parse(scenario.ExpectedJson)["marker"];
                    if (scenario.Id.StartsWith("B") && expectedMarker != null)
                        AssertTrue(messages.Where(message => (string)message["role"] != "tool")
                            .All(message => !message.ToString().Contains(expectedMarker)), "marker exists only in tool result");
                }
            });
            AssertEqual(scenarios[4].ExpectedJson, ModelDiagnosticScenarios.NativeHistory("history-regression")[4].ExpectedJson, "same seed reproduces markers");
            AssertTrue(scenarios[4].ExpectedJson != ModelDiagnosticScenarios.NativeHistory("other-seed")[4].ExpectedJson, "new seed changes markers");
            var narrated = scenarios[0].Messages.First(message => message.Role == "assistant");
            narrated.Content = "Keep this accepted narration.";
            var preserved = new LlmMessageBuilder().Build(scenarios[0].Messages, new AppSettings(), new LlmRequestOptions());
            AssertEqual(narrated.Content, (string)((JObject)preserved.Messages[1])["content"], "production default preserves narration");
            var rejected = false;
            try { new LlmMessageBuilder().Build(scenarios[0].Messages, new AppSettings(),
                new LlmRequestOptions { NativeToolCallContent = NativeToolCallContentMode.Null }); }
            catch (InvalidOperationException) { rejected = true; }
            AssertTrue(rejected, "diagnostic variants cannot drop real narration");
        }

        private static void ModelDiagnosticsFailures()
        {
            var scenarios = ModelDiagnosticScenarios.NativeHistory("failure-regression");
            var pairing = scenarios.Single(item => item.Id == "B5");
            var negative = scenarios.Single(item => item.Id == "B6");
            var replies = new List<Func<JObject, DiagnosticReply>>
            {
                request => DiagnosticContent(pairing.ExpectedJson.Replace("T2", "T1")),
                request => DiagnosticContent(pairing.ExpectedJson), // Hallucinated B result in negative control.
                request => DiagnosticContent(pairing.ExpectedJson.TrimEnd('}') + ",\"marker\":null}"),
                request => DiagnosticContent(pairing.ExpectedJson + " {}"),
                request => new DiagnosticReply { Status = 500, Body = "{\"error\":\"proxy failure\"}" },
                request => new DiagnosticReply { Status = 200, Body = "<html>invalid provider envelope</html>" },
                request => new DiagnosticReply { Status = 400, Body = "{\"error\":\"missing tool result\"}" },
                request => DiagnosticContent("{\"target\":\"ModuleB\",\"marker\":123,\"tool_call_id\":\"T2\"}"),
                request => DiagnosticContent(pairing.ExpectedJson.Replace('"', '\'')),
                request => DiagnosticContent("/* not JSON */" + pairing.ExpectedJson),
                request => DiagnosticContent(pairing.ExpectedJson.TrimEnd('}') + ",}")
            };
            WithDiagnosticEndpoint(replies, (settings, requests) =>
            {
                var runner = new ModelDiagnosticRunner(new LlmClient(() => "synthetic").CompleteAsync);
                for (var i = 0; i < replies.Count; i++)
                {
                    var result = runner.RunAsync(settings, i == 1 || i == 6 ? negative : pairing,
                        ModelDiagnosticExecutionKind.Synthetic, CancellationToken.None).GetAwaiter().GetResult();
                    AssertEqual(i >= 4 && i <= 6 ? ModelDiagnosticVerdict.Inconclusive : ModelDiagnosticVerdict.Fail,
                        result.Verdict, "bad answer or transport is not success: " + i);
                    AssertTrue(result.RequestUtf8 != null && result.ResponsePayload != null, "failures retain exact exchange");
                    if (i == 4) AssertEqual(500, result.HttpStatusCode.Value, "500 is recorded, never inferred unsupported");
                    if (i == 5)
                    {
                        AssertEqual(200, result.HttpStatusCode.Value, "HTTP success does not imply parser success");
                        AssertEqual("<html>invalid provider envelope</html>", result.ResponsePayload, "invalid envelope is not lost");
                    }
                }
                AssertEqual(replies.Count, requests.Count, "no hidden retries or fallback");
            });
        }

        private static async Task ModelDiagnosticsCancellation()
        {
            var settings = new AppSettings { StreamResponses = false, RequestTimeoutSeconds = 1 };
            var scenario = ModelDiagnosticScenarios.NativeHistory("cancel")[0];
            var runner = new ModelDiagnosticRunner(async (config, messages, options, progress, token) =>
            { await Task.Delay(Timeout.Infinite, token); return null; });
            var timeout = await runner.RunAsync(settings, scenario, ModelDiagnosticExecutionKind.Synthetic, CancellationToken.None);
            AssertEqual(ModelDiagnosticVerdict.Inconclusive, timeout.Verdict, "timeout is a result");
            AssertEqual("request_timeout", timeout.Reason, "timeout has typed reason");
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                var cancelled = false;
                try { await runner.RunAsync(settings, scenario, ModelDiagnosticExecutionKind.Synthetic, cancel.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                AssertTrue(cancelled, "caller cancellation propagates");
            }
            var uncaptured = new ModelDiagnosticRunner((config, messages, options, progress, token) =>
                Task.FromResult(new LlmCompletionResult { Content = scenario.ExpectedJson }));
            AssertEqual(ModelDiagnosticVerdict.Inconclusive,
                (await uncaptured.RunAsync(settings, scenario, ModelDiagnosticExecutionKind.Synthetic, CancellationToken.None)).Verdict,
                "a correct answer without exact exchange is not a pass");
        }

        private sealed class DiagnosticReply { public int Status = 200; public string Body; }
        private static DiagnosticReply DiagnosticContent(string content)
        { return new DiagnosticReply { Body = new JObject { ["choices"] = new JArray(new JObject {
            ["finish_reason"] = "stop", ["message"] = new JObject { ["role"] = "assistant", ["content"] = content } }) }.ToString(Formatting.None) }; }

        private static DiagnosticReply DiagnosticAnswer(JObject request)
        {
            string marker = null;
            foreach (var message in request["messages"])
            {
                var text = (string)message["content"] ?? "";
                if ((string)message["role"] == "tool" && (string)message["tool_call_id"] == "T2")
                    marker = (string)JObject.Parse(text)["marker"];
                else if (text.StartsWith("TOOL_RESULT (", StringComparison.Ordinal))
                {
                    var result = JObject.Parse(text.Substring(text.IndexOf('\n') + 1));
                    if ((string)result["tool_call_id"] == "T2") marker = (string)result["data"]["marker"];
                }
            }
            return DiagnosticContent(new JObject { ["target"] = "ModuleB", ["marker"] = marker == null ? JValue.CreateNull() : new JValue(marker),
                ["tool_call_id"] = "T2" }.ToString(Formatting.None));
        }

        private static void WithDiagnosticEndpoint(IEnumerable<Func<JObject, DiagnosticReply>> replies,
            Action<AppSettings, List<string>> run)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            using (var listener = new HttpListener())
            {
                var url = "http://127.0.0.1:" + port;
                listener.Prefixes.Add(url + "/"); listener.Start();
                var requests = new List<string>();
                var server = Task.Run(async () =>
                {
                    try
                    {
                        foreach (var reply in replies)
                        {
                            var context = await listener.GetContextAsync();
                            string raw;
                            using (var reader = new StreamReader(context.Request.InputStream)) raw = await reader.ReadToEndAsync();
                            requests.Add(raw);
                            var response = reply(JObject.Parse(raw));
                            var bytes = Encoding.UTF8.GetBytes(response.Body);
                            context.Response.StatusCode = response.Status;
                            context.Response.ContentType = "application/json";
                            context.Response.ContentLength64 = bytes.Length;
                            await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                            context.Response.Close();
                        }
                    }
                    catch (HttpListenerException) when (!listener.IsListening) { }
                    catch (ObjectDisposedException) when (!listener.IsListening) { }
                });
                try { run(new AppSettings { BaseUrl = url, Model = "synthetic", StreamResponses = false,
                    ContextWindowOverrideTokens = 8192, MaxTokens = 512, RequestTimeoutSeconds = 5 }, requests); }
                finally { listener.Stop(); server.GetAwaiter().GetResult(); }
            }
        }
    }
}
