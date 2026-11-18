using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Office;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void PowerQuerySyntaxPinsSourceWithoutHoldingDocument()
        {
            const string source = "let\r\n    label = \"😀\",\r\n    value = 42\r\nin value";
            var backend = new StubPowerQueryBackend(); backend.Seed("Syntax", source);
            var adapter = FakeOfficeAdapter.ForHost("Excel");
            adapter.RuntimeDocumentKeyValue = "pq-syntax-" + Guid.NewGuid().ToString("N");
            adapter.ExcelPowerQueryBackend = backend;
            WithTempExecutor(adapter, (executor, ignored) =>
            {
                var session = NewSession(adapter); executor.BindResourceAuthority(session);
                var gateway = executor.ResourceGateway;
                var host = new HostRuntime(adapter, null);
                var target = new OfficeDocumentExecutionExpectation { Host = "Excel", DocumentKey = adapter.DocumentKey,
                    RuntimeDocumentKey = adapter.RuntimeDocumentKey };
                var release = new TaskCompletionSource<PowerQuerySyntaxAnalysis>(TaskCreationOptions.RunContinuationsAsynchronously);
                var analyzer = new StubPowerQuerySyntaxAnalyzer { Analyze = (request, token) => release.Task };
                gateway.PowerQuerySyntaxAnalyzer = analyzer;
                var handler = new ExcelPowerQueryToolHandler(ExcelPowerQueryToolCatalog.Validate, gateway, host, session);
                var pending = handler.ExecuteAsync(PowerQuerySyntaxContext("syntax-pending"), CancellationToken.None);
                try
                {
                    AssertEqual(1, analyzer.Calls, "one exact snapshot is submitted to the language provider");
                    AssertTrue(!pending.IsCompleted, "source validation awaits only the optional analyzer");
                    AssertEqual(source, analyzer.LastRequest.Source, "the analyzer receives the unmodified captured M");
                    AssertEqual(TextPatternEngine.Sha256(source), analyzer.LastRequest.SourceSha256, "request hash pins exact source bytes");
                    AssertTrue(!string.IsNullOrWhiteSpace(analyzer.LastRequest.RequestId), "runtime supplies the analysis request identity");
                    AssertTrue(new HostRuntime(adapter, null).ReadDocument(target, () => true),
                        "the document gate is available while syntax analysis is awaiting its result");
                    host.ReadDocument(target, () => { backend.Seed("Syntax", "43"); return true; });
                }
                finally { release.TrySetResult(PowerQuerySyntaxPassed(analyzer.LastRequest)); }
                var result = pending.GetAwaiter().GetResult();
                var body = JObject.Parse(result.Result.DataJson);
                AssertEqual("passed", (string)body["syntax"], "syntax belongs to the captured source even if live M changes");
                AssertEqual(false, (bool)body["sourceMatchesCurrent"], "a changed live definition is not presented as the analyzed current source");
                AssertEqual("not_executed", (string)body["execution"], "syntax parsing never establishes M execution");
                AssertEqual("not_checked", (string)body["dependencies"], "syntax success does not claim dependency analysis");
                AssertEqual(0, result.ResourceEvidence.Count, "source analysis does not grant a whole-source model observation");
                AssertEqual(ToolEffectEvidence.None, result.Effect, "source analysis has no native effect evidence");
                AssertTrue(!result.Result.DataJson.Contains("label") && !result.Result.DataJson.Contains("value = 42"),
                    "the validation result does not deliver the full captured source body");
                var retainedRef = result.Result.Resources.Single(reference => reference.Identity.Equals(
                    ExcelResourceProvider.QueryIdentity(session.DocumentAuthorityId, "Syntax")));
                AssertTrue(retainedRef.IsExact, "the retained provenance points to an immutable source revision");
                var reads = backend.SourceReads;
                var retained = gateway.Read(session, new ResourceReadRequest { Reference = retainedRef,
                    Representation = ResourceRepresentations.Source, MaxChars = 32000 }).Result;
                AssertEqual(source, retained.Text, "the original M remains readable from CAS after live source drift");
                AssertEqual(reads, backend.SourceReads, "reading retained analysis provenance does not recapture live M");
                AssertEqual(0, backend.Dispatches, "syntax analysis and its source recheck never invoke native execution");
            });
        }

        private static void PowerQuerySyntaxRejectsInvalidResultsAndBoundsDiagnostics()
        {
            const string source = "let\r\n    x = \"😀\", y = in x";
            var backend = new StubPowerQueryBackend(); backend.Seed("Syntax", source);
            var adapter = FakeOfficeAdapter.ForHost("Excel");
            adapter.RuntimeDocumentKeyValue = "pq-syntax-" + Guid.NewGuid().ToString("N");
            adapter.ExcelPowerQueryBackend = backend;
            WithTempExecutor(adapter, (executor, ignored) =>
            {
                var session = NewSession(adapter); executor.BindResourceAuthority(session);
                var gateway = executor.ResourceGateway;
                var host = new HostRuntime(adapter, null);
                var analyzer = new StubPowerQuerySyntaxAnalyzer(); gateway.PowerQuerySyntaxAnalyzer = analyzer;
                var invalid = new Action<PowerQuerySyntaxAnalysis>[] {
                    item => item.RequestId = "foreign-request",
                    item => item.SourceSha256 = TextPatternEngine.Sha256("other M"),
                    item => item.ParserVersion = "unknown-version",
                    item => item.Syntax = (PowerQuerySyntaxStatus)42,
                    item => item.Syntax = PowerQuerySyntaxStatus.Failed,
                    item => item.Diagnostics.Add(new PowerQuerySyntaxDiagnostic { Code = "bad", Message = "Unexpected diagnostic on passed result." }),
                    item => { item.Syntax = PowerQuerySyntaxStatus.Failed; item.Diagnostics.Add(new PowerQuerySyntaxDiagnostic {
                        Code = "bad-offset", Message = "Outside source.", StartOffset = 0, EndOffset = source.Length + 1 }); },
                    item => { item.Syntax = PowerQuerySyntaxStatus.Failed; item.Diagnostics.AddRange(Enumerable.Range(0, 9).Select(index =>
                        new PowerQuerySyntaxDiagnostic { Code = "too-many", Message = "Bounded diagnostic." })); }
                };
                foreach (var change in invalid)
                {
                    analyzer.Analyze = (request, token) => { var value = PowerQuerySyntaxPassed(request); change(value); return Task.FromResult(value); };
                    var result = ExcelPowerQuerySyntaxService.ValidateAsync(gateway, host, session, "Syntax", CancellationToken.None).GetAwaiter().GetResult();
                    var body = JObject.Parse(result.Result.DataJson);
                    AssertEqual("not_checked", (string)body["syntax"], "invalid provider result cannot become syntax success/failure");
                    AssertEqual("analysis_unavailable_or_invalid_result", (string)body["reason"], "invalid transport evidence has an infrastructure reason");
                    AssertEqual(0, ((JArray)body["diagnostics"]).Count, "untrusted diagnostics are not forwarded as M findings");
                }
                analyzer.Analyze = (request, token) => Task.FromResult(JsonConvert.DeserializeObject<PowerQuerySyntaxAnalysis>(
                    new JObject { ["requestId"] = request.RequestId, ["sourceSha256"] = request.SourceSha256,
                        ["parserVersion"] = PowerQuerySyntaxAnalysis.SupportedParserVersion, ["diagnostics"] = new JArray() }.ToString()));
                var missing = ExcelPowerQuerySyntaxService.ValidateAsync(gateway, host, session, "Syntax", CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual("not_checked", (string)JObject.Parse(missing.Result.DataJson)["syntax"], "missing required syntax status cannot default to success");
                analyzer.Analyze = (request, token) => Task.FromResult(JsonConvert.DeserializeObject<PowerQuerySyntaxAnalysis>(
                    new JObject { ["requestId"] = request.RequestId, ["sourceSha256"] = request.SourceSha256,
                        ["syntax"] = 0, ["parserVersion"] = PowerQuerySyntaxAnalysis.SupportedParserVersion,
                        ["diagnostics"] = new JArray() }.ToString()));
                var numeric = ExcelPowerQuerySyntaxService.ValidateAsync(gateway, host, session, "Syntax", CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual("not_checked", (string)JObject.Parse(numeric.Result.DataJson)["syntax"], "numeric zero cannot impersonate the passed wire status");
                analyzer.Analyze = (request, token) => Task.FromResult(new PowerQuerySyntaxAnalysis {
                    RequestId = request.RequestId, SourceSha256 = request.SourceSha256,
                    ParserVersion = PowerQuerySyntaxAnalysis.SupportedParserVersion, Syntax = PowerQuerySyntaxStatus.Failed,
                    Diagnostics = new List<PowerQuerySyntaxDiagnostic> { new PowerQuerySyntaxDiagnostic {
                        Code = "ExpectedExpression", Message = "Expected an expression.",
                        StartOffset = source.IndexOf("in x", StringComparison.Ordinal), EndOffset = source.IndexOf("in x", StringComparison.Ordinal) + 2 } }
                });
                var failed = ExcelPowerQuerySyntaxService.ValidateAsync(gateway, host, session, "Syntax", CancellationToken.None).GetAwaiter().GetResult();
                var failure = JObject.Parse(failed.Result.DataJson);
                AssertEqual("failed", (string)failure["syntax"], "validated parser diagnostics are syntax failures, not transport failures");
                AssertEqual(true, (bool)failure["sourceMatchesCurrent"], "unchanged source currentness is observed separately");
                var diagnostic = failure["diagnostics"][0];
                AssertEqual(2, (int)diagnostic["line"], "CRLF is one line break");
                AssertEqual(19, (int)diagnostic["column"], "diagnostic columns count UTF-16 units, including surrogate pairs");
                AssertEqual(0, backend.Dispatches, "neither malformed results nor syntax diagnostics execute M");
            });
        }

        private static void PowerQuerySyntaxLimitsUnavailableAndCancellation()
        {
            var backend = new StubPowerQueryBackend(); backend.Seed("Syntax", "42");
            var adapter = FakeOfficeAdapter.ForHost("Excel");
            adapter.RuntimeDocumentKeyValue = "pq-syntax-" + Guid.NewGuid().ToString("N");
            adapter.ExcelPowerQueryBackend = backend;
            WithTempExecutor(adapter, (executor, ignored) =>
            {
                var session = NewSession(adapter); executor.BindResourceAuthority(session);
                var gateway = executor.ResourceGateway; var host = new HostRuntime(adapter, null);
                var analyzer = new StubPowerQuerySyntaxAnalyzer { Available = false, Analyze = (request, token) => Task.FromResult(PowerQuerySyntaxPassed(request)) };
                gateway.PowerQuerySyntaxAnalyzer = analyzer;
                Func<JObject> validate = () => JObject.Parse(ExcelPowerQuerySyntaxService.ValidateAsync(gateway, host, session,
                    "Syntax", CancellationToken.None).GetAwaiter().GetResult().Result.DataJson);
                var unavailable = validate();
                AssertEqual("not_checked", (string)unavailable["syntax"], "missing analyzer capability is not a syntax error");
                AssertEqual("analyzer_unavailable", (string)unavailable["reason"], "unavailable provider is explicit");
                AssertEqual(0, analyzer.Calls, "an unavailable provider is never invoked");
                analyzer.Available = true;
                backend.Seed("Syntax", new string(' ', PowerQuerySyntaxRequest.MaximumSourceCharacters) + "1");
                var oversized = validate();
                AssertEqual("source_exceeds_analysis_limit", (string)oversized["reason"], "source exceeding the parser bound is explicitly not checked");
                AssertEqual(0, analyzer.Calls, "oversized source is not sent to the parser");
                backend.Seed("Syntax", "42");
                analyzer.Analyze = (request, token) => { throw new InvalidOperationException("Infrastructure failure."); };
                AssertEqual("analysis_unavailable_or_invalid_result", (string)validate()["reason"], "provider failure is not reported as invalid M");
                analyzer.Analyze = (request, token) => { throw new OperationCanceledException(); };
                AssertEqual("analysis_cancelled_or_timed_out", (string)validate()["reason"], "provider timeout preserves not_checked");
                using (var cancellation = new CancellationTokenSource())
                {
                    analyzer.Analyze = (request, token) => { cancellation.Cancel(); return Task.FromCanceled<PowerQuerySyntaxAnalysis>(token); };
                    RuntimeThrows<OperationCanceledException>(() => ExcelPowerQuerySyntaxService.ValidateAsync(gateway, host, session,
                        "Syntax", cancellation.Token).GetAwaiter().GetResult());
                }
                AssertEqual(0, backend.Dispatches, "limits, failure and cancellation never evaluate M");
            });
        }

        private static PowerQuerySyntaxAnalysis PowerQuerySyntaxPassed(PowerQuerySyntaxRequest request)
        { return new PowerQuerySyntaxAnalysis { RequestId = request.RequestId, SourceSha256 = request.SourceSha256,
            ParserVersion = PowerQuerySyntaxAnalysis.SupportedParserVersion, Syntax = PowerQuerySyntaxStatus.Passed }; }

        private static ToolHandlerContext PowerQuerySyntaxContext(string id)
        {
            var call = new ToolCall(id, ExcelPowerQueryToolCatalog.Validate, "{\"target\":\"Excel query: Syntax\",\"kind\":\"source\"}");
            return new ToolHandlerContext(new ToolExecutionContext(call,
                new ToolPolicySnapshot(call.Name, "pq-test", Policy(ToolEffect.Read, ToolVerification.None)),
                "run", "turn", id, DateTime.UtcNow, false, 10),
                new Dictionary<string, object> { ["target"] = "Excel query: Syntax", ["kind"] = "source" });
        }

        private sealed class StubPowerQuerySyntaxAnalyzer : IPowerQuerySyntaxAnalyzer
        {
            internal bool Available = true;
            internal int Calls;
            internal PowerQuerySyntaxRequest LastRequest;
            internal Func<PowerQuerySyntaxRequest, CancellationToken, Task<PowerQuerySyntaxAnalysis>> Analyze;
            public bool IsAvailable { get { return Available; } }
            public Task<PowerQuerySyntaxAnalysis> AnalyzeAsync(PowerQuerySyntaxRequest request, CancellationToken token)
            { Calls++; LastRequest = request; return Analyze(request, token); }
        }
    }
}
