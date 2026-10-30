using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ModelDiagnosticsTransportMatrix()
        {
            var scenarios = ModelDiagnosticScenarios.All("transport-matrix");
            AssertEqual(23, scenarios.Count, "bounded A/B/C matrix");
            AssertEqual(scenarios.Count, scenarios.Select(item => item.Id).Distinct().Count(), "unique scenario IDs");
            var replies = scenarios.Select(scenario => DiagnosticTransportReply(scenario)).ToArray();
            WithDiagnosticEndpoint(replies.Select(reply => new Func<JObject, DiagnosticReply>(request => reply)), (settings, requests) =>
            {
                var runner = new ModelDiagnosticRunner(new LlmClient(() => "synthetic").CompleteAsync);
                ModelDiagnosticResult control = null;
                for (var i = 0; i < scenarios.Count; i++)
                {
                    var scenario = scenarios[i];
                    var result = runner.RunAsync(settings, scenario, ModelDiagnosticExecutionKind.Synthetic, CancellationToken.None).GetAwaiter().GetResult();
                    if (scenario.Id == "C4-control") control = result;
                    if (scenario.Id == "C4") ModelDiagnosticRunner.CompareStreamControl(result, control);
                    AssertEqual(ModelDiagnosticVerdict.Pass, result.Verdict, scenario.Id + ": " + result.Reason + " " + result.ParserErrorCode);
                    AssertEqual(requests[i], Encoding.UTF8.GetString(result.RequestUtf8), "post-serialization request captured");
                    var request = JObject.Parse(requests[i]);
                    AssertEqual(scenario.Streaming, (bool)request["stream"], "scenario-specific streaming");
                    if (scenario.ResponseFormat == LlmResponseFormats.Text)
                        AssertTrue(request["response_format"] == null, "explicit text request, no fallback format");
                    else AssertEqual(scenario.ResponseFormat, (string)request["response_format"]["type"], "exact requested format");
                    AssertEqual(scenario.Id == "B8", request["tools"] != null, "native tools only on generation probe");
                    if (scenario.Id == "B8")
                    {
                        AssertEqual("required", (string)request["tool_choice"], "request a native call");
                        AssertTrue(result.ActualNativeCallsJson.Contains("provider_call_1"), "provider generation IDs retained, not runtime authority");
                    }
                    if (scenario.Id == "A2" || scenario.Id == "A2-system")
                    {
                        AssertEqual(scenario.Id == "A2" ? "developer" : "system", (string)request["messages"][0]["role"], "instruction role tested separately");
                        AssertTrue(!request["messages"][1].ToString().Contains(scenario.ExpectedText), "role marker absent from user prompt");
                    }
                    if (scenario.Assertion == ModelDiagnosticAssertion.V8)
                        AssertEqual(ModelDiagnosticVerdict.Pass, result.ParserVerdict, "production v8 parser used");
                    if (scenario.Id == "C2")
                        AssertTrue(request["response_format"]["json_schema"]["schema"]["properties"]["response"] != null, "exact V8 response wrapper schema");
                    if (scenario.Id == "C3")
                    {
                        AssertEqual(false, result.HttpAccepted.Value, "expected schema rejection is not HTTP acceptance");
                        AssertTrue(request.ToString().Contains("invalid_diagnostic_type"), "invalid schema reaches provider unchanged");
                    }
                    if (scenario.Id == "C4")
                    {
                        AssertTrue(result.StreamingObserved, "actual SSE detected");
                        AssertEqual(ModelDiagnosticVerdict.Pass, result.ParityVerdict, "same typed V8 response across JSON/SSE");
                        AssertTrue(result.ResponseBytes != null && replies[i].Body.StartsWith(result.ResponsePayload, StringComparison.Ordinal), "exact received SSE bytes, never reconstructed JSON");
                        if (result.ResponseBodyComplete == true) AssertEqual(replies[i].Body, result.ResponsePayload, "complete body assertion is truthful");
                    }
                    if (scenario.Id == "B9")
                    {
                        var messages = (JArray)request["messages"];
                        AssertTrue(messages.Count(message => (string)message["role"] == "user") >= 2, "multiple user requests kept");
                        AssertEqual("T2,T4", string.Join(",", messages.Where(message => (string)message["role"] == "tool")
                            .Select(message => (string)message["tool_call_id"])), "distinct synthetic calls across runs");
                    }
                }
                AssertTrue(!settings.StreamResponses, "runner does not mutate caller settings");
            });
        }

        private static DiagnosticReply DiagnosticTransportReply(ModelDiagnosticScenario scenario)
        {
            if (scenario.Assertion == ModelDiagnosticAssertion.SchemaRejection)
                return new DiagnosticReply { Status = 400, Body = "{\"error\":{\"code\":\"invalid_json_schema\",\"param\":\"response_format.json_schema.schema\",\"message\":\"schema is invalid\"}}" };
            if (scenario.Assertion == ModelDiagnosticAssertion.NativeCall)
            {
                var expected = JObject.Parse(scenario.ExpectedJson);
                return new DiagnosticReply { Body = new JObject { ["choices"] = new JArray(new JObject {
                    ["finish_reason"] = "tool_calls", ["message"] = new JObject { ["role"] = "assistant", ["content"] = JValue.CreateNull(),
                        ["tool_calls"] = new JArray(new JObject { ["id"] = "provider_call_1", ["type"] = "function", ["function"] = new JObject {
                            ["name"] = expected["name"], ["arguments"] = expected["arguments"].ToString(Formatting.None) } }) } }) }.ToString(Formatting.None) };
            }
            var content = scenario.ExpectedText ?? scenario.ExpectedJson;
            if (scenario.Assertion == ModelDiagnosticAssertion.V8 && scenario.ResponseFormat == LlmResponseFormats.JsonSchema)
                content = new JObject { ["response"] = JObject.Parse(content) }.ToString(Formatting.None);
            return scenario.Streaming ? DiagnosticSse(content) : DiagnosticContent(content);
        }

        private static DiagnosticReply DiagnosticSse(string content)
        {
            var split = content.Length / 2;
            Func<string, string, string> delta = (text, finish) => "data: " + new JObject {
                ["choices"] = new JArray(new JObject { ["delta"] = new JObject { ["content"] = text }, ["finish_reason"] = finish })
            }.ToString(Formatting.None) + "\r\n\r\n";
            return new DiagnosticReply { ContentType = "text/event-stream", Body = ": synthetic stream\r\n\r\n" +
                delta(content.Substring(0, split), null) + delta(content.Substring(split), "stop") +
                "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":42,\"completion_tokens\":21}}\r\n\r\ndata: [DONE]\r\n\r\n" };
        }

        private static void ModelDiagnosticsTransportFailures()
        {
            var scenarios = ModelDiagnosticScenarios.All("transport-failures").ToDictionary(item => item.Id);
            var badCall = JObject.Parse(scenarios["A3"].ExpectedJson);
            badCall["tool_calls"][0]["id"] = "model_owned";
            var contradiction = JObject.Parse(scenarios["A4"].ExpectedJson);
            contradiction["outcome"] = "completed";
            var rows = new[] {
                Tuple.Create("A3", DiagnosticContent(badCall.ToString(Formatting.None)), ModelDiagnosticVerdict.Fail),
                Tuple.Create("A4", DiagnosticContent(contradiction.ToString(Formatting.None)), ModelDiagnosticVerdict.Fail),
                Tuple.Create("C2", DiagnosticContent(scenarios["C2"].ExpectedJson), ModelDiagnosticVerdict.Fail),
                Tuple.Create("C3", DiagnosticContent(scenarios["C3"].ExpectedJson), ModelDiagnosticVerdict.Fail),
                Tuple.Create("C3", new DiagnosticReply { Status = 400, Body = "{\"error\":{\"message\":\"unrelated failure\"}}" }, ModelDiagnosticVerdict.Inconclusive),
                Tuple.Create("C3", new DiagnosticReply { Status = 500, Body = "{\"error\":{\"code\":\"invalid_json_schema\"}}" }, ModelDiagnosticVerdict.Inconclusive),
                Tuple.Create("C2", new DiagnosticReply { Status = 400, Body = "{\"error\":{\"message\":\"response_format json_schema is not supported\"}}" }, ModelDiagnosticVerdict.Unsupported),
                Tuple.Create("B8", DiagnosticContent(scenarios["B8"].ExpectedJson), ModelDiagnosticVerdict.Fail),
                Tuple.Create("C4", DiagnosticContent(scenarios["C4"].ExpectedJson), ModelDiagnosticVerdict.Fail),
                Tuple.Create("C4", new DiagnosticReply { ContentType = "text/event-stream", Body = "data: {broken\n\n" }, ModelDiagnosticVerdict.Inconclusive),
                Tuple.Create("C4", new DiagnosticReply { ContentType = "text/event-stream", Body = "data: {\"error\":{\"message\":\"provider failure\"}}\n\n" }, ModelDiagnosticVerdict.Inconclusive)
            };
            WithDiagnosticEndpoint(rows.Select(row => new Func<JObject, DiagnosticReply>(request => row.Item2)), (settings, requests) =>
            {
                var runner = new ModelDiagnosticRunner(new LlmClient(() => "synthetic").CompleteAsync);
                for (var i = 0; i < rows.Length; i++)
                {
                    var row = rows[i];
                    var result = runner.RunAsync(settings, scenarios[row.Item1], ModelDiagnosticExecutionKind.Synthetic, CancellationToken.None).GetAwaiter().GetResult();
                    AssertEqual(row.Item3, result.Verdict, row.Item1 + " failure " + i + ": " + result.Reason);
                    AssertTrue(result.RequestUtf8 != null && result.ResponsePayload != null, "failed requests retain evidence");
                    if (i == 8) AssertEqual("json_response_instead_of_sse", result.Reason, "JSON fallback does not qualify SSE");
                    if (i >= 9) AssertEqual(row.Item2.Body, result.ResponsePayload, "malformed/error SSE body retained");
                }
                AssertEqual(rows.Length, requests.Count, "no retries, repair or format fallback");
            });
        }

        private static void ModelDiagnosticsStreamCapture()
        {
            using (var source = new MemoryStream(Encoding.UTF8.GetBytes("0123456789")))
            using (var capture = new LlmResponseCaptureStream(source, 4))
            {
                var buffer = new byte[20];
                AssertEqual(10, capture.Read(buffer, 0, buffer.Length), "capture does not limit parser input");
                var prefix = capture.Snapshot();
                AssertEqual("0123", Encoding.UTF8.GetString(prefix.Bytes), "bounded exact prefix");
                AssertTrue(prefix.Truncated && !prefix.EndOfStream, "limits and EOF are distinct");
                capture.Read(buffer, 0, buffer.Length);
                AssertTrue(capture.Snapshot().EndOfStream, "EOF recorded only after actual read");
            }
            var scenario = ModelDiagnosticScenarios.All("native-stream").Single(item => item.Id == "B8");
            scenario.Streaming = true;
            var expected = JObject.Parse(scenario.ExpectedJson);
            var arguments = expected["arguments"].ToString(Formatting.None);
            var chunks = new[] {
                new JObject { ["index"] = 0, ["id"] = "provider_call", ["type"] = "function", ["function"] = new JObject {
                    ["name"] = "diagnostic_", ["arguments"] = arguments.Substring(0, 5) } },
                new JObject { ["index"] = 0, ["function"] = new JObject {
                    ["name"] = "echo", ["arguments"] = arguments.Substring(5) } }
            };
            var body = string.Join("", chunks.Select((chunk, index) => "data: " + new JObject {
                ["choices"] = new JArray(new JObject { ["delta"] = new JObject { ["tool_calls"] = new JArray(chunk) },
                    ["finish_reason"] = index == 1 ? "tool_calls" : null }) }.ToString(Formatting.None) + "\n\n")) + "data: [DONE]\n\n";
            WithDiagnosticEndpoint(new[] {
                new Func<JObject, DiagnosticReply>(request => new DiagnosticReply { Body = body, ContentType = "text/event-stream" }),
                request => new DiagnosticReply { Body = body.Replace("\"index\":0", "\"index\":999999999999999999999"), ContentType = "text/event-stream" }
            },
                (settings, requests) => {
                    var runner = new ModelDiagnosticRunner(new LlmClient(() => "synthetic").CompleteAsync);
                    var result = runner.RunAsync(settings, scenario, ModelDiagnosticExecutionKind.Synthetic, CancellationToken.None).GetAwaiter().GetResult();
                    AssertEqual(ModelDiagnosticVerdict.Pass, result.Verdict, "indexed native deltas assembled by existing parser");
                    AssertTrue(result.ActualNativeCallsJson.Contains("diagnostic_echo"), "function name fragments joined");
                    var malformed = runner.RunAsync(settings, scenario, ModelDiagnosticExecutionKind.Synthetic, CancellationToken.None).GetAwaiter().GetResult();
                    AssertEqual(ModelDiagnosticVerdict.Inconclusive, malformed.Verdict, "oversized native index is a typed provider failure, not a process crash");
                    AssertTrue(malformed.ResponsePayload.Contains("999999999999999999999"), "malformed native stream retained");
                });
            var json = DiagnosticTransportReply(ModelDiagnosticScenarios.All("native-default").Single(item => item.Id == "B8")).Body;
            AssertTrue(LlmResponseParser.ParseCompletionResponse(json).NativeToolCalls == null, "native inspection is opt-in; production dispatch unchanged");
            var streamed = new ModelDiagnosticResult { StreamingRequested = true, Verdict = ModelDiagnosticVerdict.Pass };
            ModelDiagnosticRunner.CompareStreamControl(streamed, null);
            AssertEqual(ModelDiagnosticVerdict.Inconclusive, streamed.Verdict, "missing parity control cannot pass");
        }
    }
}
