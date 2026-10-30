using System;
using System.Diagnostics;
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
        public ModelDiagnosticExecutionKind ExecutionKind { get; set; }
        public ModelDiagnosticVerdict Verdict { get; set; }
        public string Reason { get; set; }
        public int? HttpStatusCode { get; set; }
        public bool? HttpAccepted { get; set; }
        public bool? JsonObjectAccepted { get; set; }
        public bool? MarkerMatches { get; set; }
        public bool? CallIdMatches { get; set; }
        public bool? TargetMatches { get; set; }
        public string FailureKind { get; set; }
        public long DurationMs { get; set; }
        public int? PromptTokens { get; set; }
        public int? CompletionTokens { get; set; }
        public string ExpectedJson { get; set; }
        public string ActualContent { get; set; }
        public string RequestSha256 { get; set; }
        [JsonIgnore] public byte[] RequestUtf8 { get; set; }
        [JsonIgnore] public string ResponsePayload { get; set; }
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
            if (settings.StreamResponses) throw new ArgumentException("This diagnostic slice requires non-streaming requests; SSE qualification is separate.");
            var expected = ParseObject(scenario.ExpectedJson);
            var result = new ModelDiagnosticResult { ScenarioId = scenario.Id, Capability = scenario.Capability,
                ExecutionKind = kind, Verdict = ModelDiagnosticVerdict.NotRun, ExpectedJson = scenario.ExpectedJson };
            var options = new LlmRequestOptions { ResponseFormat = LlmResponseFormats.JsonObject,
                NativeToolCallContent = scenario.NativeCallContent, ReasoningEnabled = false,
                TracePurpose = "model-diagnostic/" + scenario.Id,
                TraceSink = trace =>
                {
                    if (trace.Type == "request")
                    {
                        result.RequestUtf8 = trace.PayloadUtf8Bytes == null ? null : (byte[])trace.PayloadUtf8Bytes.Clone();
                        if (result.RequestUtf8 != null)
                            using (var sha = SHA256.Create())
                                result.RequestSha256 = BitConverter.ToString(sha.ComputeHash(result.RequestUtf8)).Replace("-", "").ToLowerInvariant();
                    }
                    else if (trace.Type == "response" || trace.Type == "failure")
                    {
                        result.HttpStatusCode = trace.StatusCode;
                        result.HttpAccepted = trace.StatusCode.HasValue ? (bool?)(trace.StatusCode >= 200 && trace.StatusCode < 300) : null;
                        result.ResponsePayload = trace.PayloadJson;
                        result.FailureKind = trace.FailureKind;
                    }
                } };
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
                    result.Reason = "incomplete_or_refused_answer";
                    var matches = completion != null && string.IsNullOrEmpty(completion.RefusalContent) &&
                        (completion.Termination == LlmCompletionTermination.Completed || completion.Termination == LlmCompletionTermination.Unspecified) &&
                        EvaluateAnswer(result, expected);
                    result.Verdict = matches ? ModelDiagnosticVerdict.Pass : ModelDiagnosticVerdict.Fail;
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
                }
                finally { result.DurationMs = watch.ElapsedMilliseconds; }
            }
            if (result.Verdict == ModelDiagnosticVerdict.Pass &&
                (result.RequestUtf8 == null || result.ResponsePayload == null || result.HttpAccepted != true))
            {
                result.Verdict = ModelDiagnosticVerdict.Inconclusive; result.Reason = "missing_exact_exchange";
            }
            return result;
        }

        private static bool EvaluateAnswer(ModelDiagnosticResult result, JObject expected)
        {
            try
            {
                var actual = ParseObject(result.ActualContent);
                result.JsonObjectAccepted = true;
                result.MarkerMatches = JToken.DeepEquals(actual["marker"], expected["marker"]);
                result.CallIdMatches = JToken.DeepEquals(actual["tool_call_id"], expected["tool_call_id"]);
                result.TargetMatches = JToken.DeepEquals(actual["target"], expected["target"]);
                var matches = JToken.DeepEquals(actual, expected);
                result.Reason = result.MarkerMatches != true ? "marker_mismatch" : result.CallIdMatches != true ? "call_id_mismatch" :
                    result.TargetMatches != true ? "target_mismatch" : matches ? "exact_marker_and_call_id" : "unexpected_answer_fields";
                return matches;
            }
            catch (JsonException) { result.JsonObjectAccepted = false; result.Reason = "invalid_answer_json"; return false; }
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
