using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.WebView;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ModelDiagnosticsUiRunsSharedControls()
        {
            const string seed = "ui-controls";
            var cases = ModelDiagnosticScenarios.Select(seed, new[] { "C4" });
            WithDiagnosticEndpoint(cases.Select(item => new Func<JObject, DiagnosticReply>(_ => DiagnosticTransportReply(item))), (settings, requests) =>
            {
                settings.DebugModelTraffic = true;
                var before = JsonConvert.SerializeObject(settings);
                var service = new ModelDiagnosticsService(new LlmClient(() => "fixture").CompleteAsync);
                var catalog = ModelDiagnosticsService.Catalog(settings);
                AssertEqual(23, catalog.Scenarios.Count, "shared catalog");
                AssertEqual(3, catalog.Scenarios.Count(item => item.IsControl), "controls cannot be accidentally omitted");
                AssertEqual(0, requests.Count, "listing sends no request");
                var request = new RunModelDiagnosticRequest { Endpoint = settings.BaseUrl, Model = settings.Model, ScenarioId = "C4", Seed = seed };
                var report = service.RunAsync(settings, request, "fixture", CancellationToken.None, ModelDiagnosticExecutionKind.Synthetic).GetAwaiter().GetResult();
                AssertEqual("C4-control,C4", string.Join(",", report.Exchanges.Select(item => item.Result.ScenarioId)), "control precedes stream");
                AssertTrue(report.Exchanges.All(item => item.Result.Verdict == ModelDiagnosticVerdict.Pass), "real runner assertions pass");
                AssertEqual(ModelDiagnosticVerdict.Pass, report.Exchanges.Last().Result.ParityVerdict, "host computes parity");
                AssertEqual(requests[1], report.Exchanges[1].Request.Text, "UI receives actual serialized HTTP request");
                AssertContains(report.Exchanges[1].Response.Text, "data: ", "UI receives raw SSE rather than reconstructed JSON");
                AssertEqual(before, JsonConvert.SerializeObject(settings), "caller settings unchanged");
                request.Endpoint = "https://different.example";
                try { service.RunAsync(settings, request, "fixture", CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Endpoint drift accepted."); }
                catch (ArgumentException) { }
                AssertEqual(2, requests.Count, "drift fails before dispatch and never retries");
            });
        }

        private static void ModelDiagnosticsUiBoundsAndRedacts()
        {
            const string secret = "fixture-secret-\"key\\value";
            var content = new string('x', ModelDiagnosticsService.PreviewCharacters - 3) + secret + new string('y', 100);
            var source = new ModelDiagnosticResult { ScenarioId = "B5", Verdict = ModelDiagnosticVerdict.Fail,
                ActualContent = content, RequestUtf8 = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { content = secret })),
                ResponsePayload = JsonConvert.SerializeObject(new { content = content }), RequestSha256 = new string('a', 64) };
            var exported = ModelDiagnosticsService.Export(source, new[] { secret });
            AssertTrue(exported.ResultTruncated && exported.ResultRedacted && exported.Request.Redacted && exported.Response.Truncated,
                "redaction and preview bounds are explicit");
            AssertTrue(exported.Result.ActualContent.Length <= ModelDiagnosticsService.PreviewCharacters, "bridge bounds full content too");
            AssertTrue(!exported.Request.Text.Contains("fixture-secret") && !exported.Response.Text.Contains("fixture-secret"), "redact before cutting a secret");
            AssertEqual(content, source.ActualContent, "source exchange is immutable during export");
            AssertEqual(source.RequestSha256, exported.Result.RequestSha256, "hash identifies original request");
            var calls = 0;
            var service = new ModelDiagnosticsService(async (settings, messages, options, progress, token) => {
                calls++; await Task.Delay(Timeout.Infinite, token); return null;
            });
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.CancelAfter(30);
                try { service.RunAsync(new AppSettings { BaseUrl = "http://fixture" }, new RunModelDiagnosticRequest {
                    Endpoint = "http://fixture", Model = "scripted", ScenarioId = "C4", Seed = "cancel" }, null, cancellation.Token).GetAwaiter().GetResult();
                    throw new Exception("Cancellation swallowed."); }
                catch (OperationCanceledException) { }
            }
            AssertEqual(1, calls, "cancellation prevents the next scenario");
        }

        private static void ModelDiagnosticsUiBridgeIsTyped()
        {
            using (var bridge = new AssistantWebBridge(new AssistantController(), null))
            {
                var token = BridgeToken(bridge);
                Func<string, string, JObject> send = (operation, payload) => JObject.Parse(bridge.HandleMessageAsync(
                    "{\"contractVersion\":2,\"id\":\"lab\",\"type\":\"" + operation + "\",\"bridgeToken\":\"" + token + "\",\"payload\":" + payload + "}")
                    .GetAwaiter().GetResult());
                var catalog = send("getModelDiagnosticScenarios", "{}");
                AssertTrue((bool)catalog["ok"], "catalog is registered");
                AssertEqual(23, ((JArray)catalog["payload"]["scenarios"]).Count, "catalog DTO crosses bridge");
                var result = send("runModelDiagnostic", "{\"scenarioId\":\"B5\",\"model\":\"scripted\",\"seed\":\"one\"}");
                AssertTrue((bool)result["ok"], "runner is registered");
                AssertEqual("B5", (string)result.SelectToken("payload.exchanges[0].result.ScenarioId"), "typed request and response");
                AssertEqual("Inconclusive", (string)result.SelectToken("payload.exchanges[0].result.Verdict"), "enum wire value is explicit");
            }
        }
    }
}
