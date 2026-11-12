using System;
using System.Diagnostics;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.ModelProtocol;

namespace RNAssistant.Core.Llm
{
    public sealed class ModelDiagnosticResult
    {
        public string ScenarioId { get; set; }
        public ModelDiagnosticCapability Capability { get; set; }
        public ModelDiagnosticAssertion Assertion { get; set; }
        public ModelDiagnosticExecutionKind ExecutionKind { get; set; }
        public ModelDiagnosticVerdict Verdict { get; set; }
        public ModelDiagnosticVerdict ParserVerdict { get; set; }
        public ModelDiagnosticVerdict ParityVerdict { get; set; }
        public string ParserErrorCode { get; set; }
        public string ParsedResponseJson { get; set; }
        public string ActualNativeCallsJson { get; set; }
        public string Endpoint { get; set; }
        public string Model { get; set; }
        public string ResponseFormat { get; set; }
        public bool StreamingRequested { get; set; }
        public bool StreamingObserved { get; set; }
        public bool? ResponseBodyComplete { get; set; }
        public bool? ResponseCaptureTruncated { get; set; }
        public string ResponseContentType { get; set; }
        public string Reason { get; set; }
        public int? HttpStatusCode { get; set; }
        public bool? HttpAccepted { get; set; }
        public bool? JsonObjectAccepted { get; set; }
        public bool? MarkerMatches { get; set; }
        public bool? CallIdMatches { get; set; }
        public ModelDiagnosticVerdict VisibilityVerdict { get; set; }
        public ModelDiagnosticVerdict PairingVerdict { get; set; }
        public ModelDiagnosticVerdict CallIdVisibilityVerdict { get; set; }
        public ModelDiagnosticResult Continuation { get; set; }
        public bool? TargetMatches { get; set; }
        public string FailureKind { get; set; }
        public long DurationMs { get; set; }
        public int? PromptTokens { get; set; }
        public int? CompletionTokens { get; set; }
        public string ExpectedJson { get; set; }
        public string ExpectedText { get; set; }
        public string ActualContent { get; set; }
        public string RequestSha256 { get; set; }
        [JsonIgnore] public byte[] RequestUtf8 { get; set; }
        [JsonIgnore] public string ResponsePayload { get; set; }
        [JsonIgnore] public byte[] ResponseBytes { get; set; }
    }

    public sealed class ModelDiagnosticRunner
    {
        private readonly LlmCompletionDelegate _complete;
        public ModelDiagnosticRunner(LlmCompletionDelegate complete)
        { _complete = complete ?? throw new ArgumentNullException(nameof(complete)); }

        public async Task<ModelDiagnosticResult> RunAsync(AppSettings settings, ModelDiagnosticScenario scenario,
            ModelDiagnosticExecutionKind kind, CancellationToken cancellationToken)
        {
            if (settings == null || scenario == null) throw new ArgumentNullException();
            settings = settings.Clone();
            settings.StreamResponses = scenario.Streaming;
            var result = new ModelDiagnosticResult { ScenarioId = scenario.Id, Capability = scenario.Capability, Assertion = scenario.Assertion,
                ExecutionKind = kind, Verdict = ModelDiagnosticVerdict.NotRun, ExpectedJson = scenario.ExpectedJson,
                ExpectedText = scenario.ExpectedText, Endpoint = settings.BaseUrl, Model = settings.Model,
                ResponseFormat = scenario.ResponseFormat, StreamingRequested = scenario.Streaming };
            var options = scenario.Assertion == ModelDiagnosticAssertion.V8
                ? ModelProtocolWire.CreateRequestOptions(scenario.ResponseFormat, scenario.CallableTools, new ModelProtocolCallContext(scenario.CallableTools.Where(t => t.Policy?.AllowsSequentialBatch == true).Select(t => t.Id)))
                : new LlmRequestOptions { ResponseFormat = scenario.ResponseFormat,
                    ResponseSchemaName = "diagnostic_marker", ResponseSchemaJson = scenario.ResponseSchemaJson };
            options.NativeToolCallContent = scenario.NativeCallContent;
            options.NativeTools = scenario.NativeFunction == null ? null : new[] { scenario.NativeFunction };
            options.RequireNativeToolCall = scenario.NativeFunction != null && scenario.ForceNativeCall;
            options.InspectNativeToolCalls = true;
            options.CaptureStreamBody = scenario.Streaming;
            options.ReasoningEnabled = false;
            options.TracePurpose = "model-diagnostic/" + scenario.Id;
            options.TraceSink = trace =>
                {
                    if (trace.Type == "request")
                    {
                        result.RequestUtf8 = trace.PayloadUtf8Bytes == null ? null : (byte[])trace.PayloadUtf8Bytes.Clone();
                        if (result.RequestUtf8 != null)
                            using (var sha = SHA256.Create())
                                result.RequestSha256 = BitConverter.ToString(sha.ComputeHash(result.RequestUtf8)).Replace("-", "").ToLowerInvariant();
                    }
                    else if (trace.Type == "stream-body")
                    {
                        result.ResponseBytes = trace.PayloadUtf8Bytes;
                        result.ResponsePayload = trace.PayloadJson;
                        result.ResponseBodyComplete = trace.Completed;
                        result.ResponseCaptureTruncated = trace.CaptureTruncated;
                        result.ResponseContentType = trace.PayloadContentType;
                    }
                    else if (trace.Type == "response" || trace.Type == "failure")
                    {
                        result.HttpStatusCode = trace.StatusCode;
                        result.HttpAccepted = trace.StatusCode.HasValue ? (bool?)(trace.StatusCode >= 200 && trace.StatusCode < 300) : null;
                        if (result.ResponseBytes == null && (!scenario.Streaming || trace.Type == "failure"))
                            result.ResponsePayload = trace.PayloadJson;
                        result.FailureKind = trace.FailureKind;
                    }
                };
            var watch = Stopwatch.StartNew();
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(settings.RequestTimeoutSeconds > 0 ? settings.RequestTimeoutSeconds : 30));
                try
                {
                    var completion = await _complete(settings, scenario.Messages, options, null, deadline.Token).ConfigureAwait(false);
                    result.ActualContent = completion == null ? null : completion.Content;
                    result.PromptTokens = completion?.PromptTokens;
                    result.CompletionTokens = completion?.CompletionTokens;
                    result.StreamingObserved = completion != null && completion.IsEventStream;
                    result.ActualNativeCallsJson = completion?.NativeToolCalls == null ? null :
                        JsonConvert.SerializeObject(completion.NativeToolCalls);
                    result.Reason = "incomplete_or_refused_answer";
                    var matches = completion != null && string.IsNullOrEmpty(completion.RefusalContent) &&
                        (completion.Termination == LlmCompletionTermination.Completed || completion.Termination == LlmCompletionTermination.Unspecified ||
                            scenario.Assertion == ModelDiagnosticAssertion.NativeCall && completion.Termination == LlmCompletionTermination.Other) &&
                        EvaluateAnswer(result, scenario, completion);
                    if (scenario.Streaming && matches && !completion.IsEventStream)
                    { matches = false; result.Reason = "json_response_instead_of_sse"; }
                    result.Verdict = matches ? ModelDiagnosticVerdict.Pass : ModelDiagnosticVerdict.Fail;
                    if (scenario.AmbiguousWithoutIds && result.CallIdMatches != true)
                    { result.Verdict = ModelDiagnosticVerdict.Inconclusive; result.Reason = "pairing_requires_visible_ids"; }
                    if (scenario.Assertion == ModelDiagnosticAssertion.HistoryJson && !scenario.NegativeControl)
                    {
                        result.VisibilityVerdict = result.MarkerMatches == true ? ModelDiagnosticVerdict.Pass : ModelDiagnosticVerdict.Fail;
                        if (scenario.Capability == ModelDiagnosticCapability.NativeHistoryPairing)
                            result.PairingVerdict = result.Verdict;
                    }
                    if (matches && scenario.NativeRoundTrip)
                    {
                        var call = completion.NativeToolCalls.Single();
                        if (string.IsNullOrWhiteSpace(call.Id))
                        { result.Verdict = ModelDiagnosticVerdict.Fail; result.Reason = "native_call_id_missing"; }
                        else
                        {
                            // The result marker must not already be present in the call arguments.
                            var marker = Guid.NewGuid().ToString("N");
                            var follow = new ModelDiagnosticScenario { Id = scenario.Id + "/result", Capability = ModelDiagnosticCapability.NativeGeneration,
                                Assertion = ModelDiagnosticAssertion.ExactJson, ResponseFormat = LlmResponseFormats.Text,
                                NativeFunction = scenario.NativeFunction, ForceNativeCall = false,
                                ExpectedJson = new JObject { ["marker"] = marker }.ToString(Formatting.None),
                                Messages = new List<ChatMessage>(scenario.Messages) };
                            follow.Messages.Add(new ChatMessage { Role = "assistant", Content = completion.Content, ToolCalls = completion.NativeToolCalls.ToList() });
                            follow.Messages.Add(new ChatMessage { Role = "tool", ToolCallId = call.Id, Content = follow.ExpectedJson });
                            follow.Messages.Add(new ChatMessage { Role = "user", Content = "Return only JSON with marker from the tool result. Do not call another tool." });
                            result.Continuation = await RunAsync(settings, follow, kind, deadline.Token).ConfigureAwait(false);
                            result.Verdict = result.Continuation.Verdict;
                            result.Reason = result.Verdict == ModelDiagnosticVerdict.Pass ? "native_cycle_completed" : "native_continuation_failed";
                        }
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    result.Verdict = ModelDiagnosticVerdict.Inconclusive; result.Reason = "request_timeout";
                }
                catch (LlmRequestException ex)
                {
                    result.FailureKind = ex.Kind.ToString();
                    result.Verdict = ex.Kind == LlmFailureKind.ResponseFormatUnsupported ? ModelDiagnosticVerdict.Unsupported :
                        ex.Kind == LlmFailureKind.Http && ex.StatusCode == 400 && !scenario.NegativeControl
                            ? ModelDiagnosticVerdict.Fail : ModelDiagnosticVerdict.Inconclusive;
                    // HTTP 500, auth, missing-pair rejection and malformed envelopes cannot prove absent tool visibility.
                    result.Reason = "request_failed";
                    if (scenario.Assertion == ModelDiagnosticAssertion.SchemaRejection)
                    {
                        if (SchemaRejection(ex.StatusCode, result.ResponsePayload))
                        { result.Verdict = ModelDiagnosticVerdict.Pass; result.Reason = "invalid_schema_rejected"; }
                        else if (result.Verdict != ModelDiagnosticVerdict.Unsupported)
                        { result.Verdict = ModelDiagnosticVerdict.Inconclusive; result.Reason = "schema_rejection_unproven"; }
                    }
                }
                finally { result.DurationMs = watch.ElapsedMilliseconds; }
            }
            if (result.Verdict == ModelDiagnosticVerdict.Pass &&
                (result.RequestUtf8 == null || result.ResponsePayload == null ||
                    result.HttpAccepted != true && scenario.Assertion != ModelDiagnosticAssertion.SchemaRejection ||
                    scenario.Streaming && (result.ResponseBytes == null || result.ResponseCaptureTruncated == true)))
            {
                result.Verdict = ModelDiagnosticVerdict.Inconclusive; result.Reason = "missing_exact_exchange";
            }
            return result;
        }

        private static bool EvaluateAnswer(ModelDiagnosticResult result, ModelDiagnosticScenario scenario, LlmCompletionResult completion)
        {
            if (scenario.Assertion == ModelDiagnosticAssertion.SchemaRejection)
            { result.Reason = "invalid_schema_accepted"; return false; }
            if (scenario.Assertion == ModelDiagnosticAssertion.NativeCall)
            {
                var calls = completion.NativeToolCalls;
                if (calls == null || calls.Count != 1) { result.Reason = "native_call_missing_or_extra"; return false; }
                try
                {
                    var actual = new JObject { ["name"] = calls[0].Name, ["arguments"] = ParseObject(calls[0].ArgumentsJson) };
                    var matches = JToken.DeepEquals(actual, ParseObject(scenario.ExpectedJson));
                    result.Reason = matches ? "exact_native_function_and_arguments" : "native_arguments_mismatch";
                    return matches;
                }
                catch (JsonException) { result.Reason = "invalid_native_arguments"; return false; }
            }
            if (completion.NativeToolCalls?.Count > 0) { result.Reason = "unexpected_native_generation"; return false; }
            if (scenario.Assertion == ModelDiagnosticAssertion.ExactText)
            {
                var matches = result.ActualContent == scenario.ExpectedText;
                result.MarkerMatches = matches;
                result.Reason = matches ? "exact_text_marker" : "text_marker_mismatch";
                return matches;
            }
            if (scenario.Assertion == ModelDiagnosticAssertion.V8)
            {
                var parsed = ModelProtocolWire.Parse(result.ActualContent, scenario.CallableTools, scenario.CallableTools,
                    new ModelProtocolCallContext(scenario.CallableTools.Where(t => t.Policy?.AllowsSequentialBatch == true).Select(t => t.Id)), scenario.ResponseFormat == LlmResponseFormats.JsonSchema);
                result.ParserVerdict = parsed.Success ? ModelDiagnosticVerdict.Pass : ModelDiagnosticVerdict.Fail;
                result.ParserErrorCode = parsed.ErrorCode;
                if (!parsed.Success) { result.Reason = "v8_parser_rejected"; return false; }
                result.ParsedResponseJson = parsed.Response.ToJson();
                var matches = JToken.DeepEquals(ParseObject(result.ParsedResponseJson), ParseObject(scenario.ExpectedJson));
                result.Reason = matches ? "exact_v8_response" : "v8_response_mismatch";
                return matches;
            }
            return EvaluateJson(result, ParseObject(scenario.ExpectedJson), scenario.Assertion == ModelDiagnosticAssertion.HistoryJson);
        }

        private static bool EvaluateJson(ModelDiagnosticResult result, JObject expected, bool history)
        {
            try
            {
                var actual = ParseObject(result.ActualContent);
                result.JsonObjectAccepted = true;
                result.MarkerMatches = JToken.DeepEquals(actual["marker"], expected["marker"]);
                if (history)
                {
                    result.CallIdMatches = actual["tool_call_id"]?.Type == JTokenType.String
                        ? (bool?)JToken.DeepEquals(actual["tool_call_id"], expected["tool_call_id"]) : null;
                    result.CallIdVisibilityVerdict = !result.CallIdMatches.HasValue ? ModelDiagnosticVerdict.Inconclusive :
                        result.CallIdMatches.Value ? ModelDiagnosticVerdict.Pass : ModelDiagnosticVerdict.Fail;
                    result.TargetMatches = JToken.DeepEquals(actual["target"], expected["target"]);
                }
                var compared = (JObject)actual.DeepClone();
                var target = (JObject)expected.DeepClone();
                if (history) { compared.Remove("tool_call_id"); target.Remove("tool_call_id"); }
                var matches = JToken.DeepEquals(compared, target);
                result.Reason = result.MarkerMatches != true ? "marker_mismatch" : history && result.TargetMatches != true ? "target_mismatch" : matches ? (history && !result.CallIdMatches.HasValue ? "content_visible_id_unobserved" : "exact_json_answer") : "unexpected_answer_fields";
                return matches;
            }
            catch (JsonException) { result.JsonObjectAccepted = false; result.Reason = "invalid_answer_json"; return false; }
        }

        private static bool SchemaRejection(int? status, string body)
        {
            if (status != 400 && status != 422) return false;
            try
            {
                var error = ParseObject(body)["error"] as JObject;
                var code = (string)error?["code"];
                var parameter = (string)error?["param"];
                return code == "invalid_json_schema" || code == "invalid_schema" ||
                    code == "invalid_request_error" && parameter == "response_format.json_schema.schema";
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidCastException || ex is ArgumentException) { return false; }
        }

        public static void CompareStreamControl(ModelDiagnosticResult streamed, ModelDiagnosticResult control)
        {
            if (!streamed.StreamingRequested) throw new ArgumentException("A streaming result is required.");
            if (control == null || control.StreamingRequested || control.Verdict != ModelDiagnosticVerdict.Pass ||
                streamed.Verdict != ModelDiagnosticVerdict.Pass || control.Endpoint != streamed.Endpoint || control.Model != streamed.Model ||
                control.ExpectedJson != streamed.ExpectedJson || control.ExecutionKind != streamed.ExecutionKind)
            {
                streamed.ParityVerdict = ModelDiagnosticVerdict.Inconclusive;
                if (streamed.Verdict == ModelDiagnosticVerdict.Pass)
                { streamed.Verdict = ModelDiagnosticVerdict.Inconclusive; streamed.Reason = "sse_control_unavailable"; }
                return;
            }
            var matches = streamed.StreamingObserved && streamed.ParsedResponseJson != null &&
                streamed.ParsedResponseJson == control.ParsedResponseJson;
            streamed.ParityVerdict = matches ? ModelDiagnosticVerdict.Pass : ModelDiagnosticVerdict.Fail;
            if (!matches) { streamed.Verdict = ModelDiagnosticVerdict.Fail; streamed.Reason = "sse_parity_mismatch"; }
        }

        private static JObject ParseObject(string value)
        {
            try
            {
                // Reuse the existing strict JSON value parser; Json.NET alone accepts non-JSON extensions.
                var parsed = ToolResultWire.ParseData(value) as JObject;
                if (parsed == null) throw new JsonReaderException("Diagnostic answer must be one JSON object.");
                return parsed;
            }
            catch (ArgumentException ex) { throw new JsonReaderException("Invalid diagnostic JSON object.", ex); }
        }
    }
}
