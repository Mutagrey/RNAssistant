using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static ModelProtocolRequest NewProtocolRequest(int attempts = 2, bool strict = false)
        {
            return new ModelProtocolRequest
            {
                Settings = new AppSettings { MaxAgentFormatRetries = attempts, FallbackToJsonObject = true,
                    AgentResponseMode = strict ? AgentResponseModes.JsonSchema : AgentResponseModes.JsonObject },
                AcceptedMessages = new[]
                {
                    new ChatMessage { Role = "system", Content = "Use the conversation response contract." },
                    new ChatMessage { Role = "user", Content = "Current accepted request." }
                },
                CallableTools = new ToolDefinition[0],
                RunnableCatalog = new ToolDefinition[0],
                Options = new LlmRequestOptions
                {
                    ResponseFormat = strict ? LlmResponseFormats.JsonSchema : LlmResponseFormats.JsonObject,
                    ResponseSchemaName = strict ? "conversation_response" : null,
                    ResponseSchemaJson = strict ? AgentResponseSchemaBuilder.Build(new ToolDefinition[0]) : null,
                    TraceStepId = Guid.NewGuid().ToString("N")
                }
            };
        }

        private static async Task ModelProtocolRepairsFromAcceptedPrompt()
        {
            var invalid = new[]
            {
                "PROTECTION_RESPONSE: request blocked by protection layer",
                "<html>REJECTED_HTML: gateway challenge</html>",
                "{\"status\": }",
                "{\"status\":\"completed\",\"message\":\"REJECTED_SCHEMA\",\"tool_calls\":{}}",
                "",
                "{\"status\":\"completed\",\"message\":\"REJECTED_TRUNCATED"
            };
            var request = NewProtocolRequest(invalid.Length + 1);
            var originalPrompt = JsonConvert.SerializeObject(request.AcceptedMessages);
            var trace = new List<LlmTraceRecord>();
            var attemptIds = new HashSet<string>();
            var stepId = request.Options.TraceStepId;
            request.Options.TraceSink = trace.Add;
            var calls = 0;
            var started = 0;
            var completed = 0;
            IModelProtocol protocol = new ModelProtocolClient((settings, messages, options, stream, token) =>
            {
                calls++;
                var prompt = messages.ToList();
                AssertEqual(originalPrompt, JsonConvert.SerializeObject(prompt.Take(request.AcceptedMessages.Count)),
                    "every raw attempt starts from the same accepted messages");
                AssertEqual(request.AcceptedMessages.Count + (calls == 1 ? 0 : 1), prompt.Count,
                    "repairs neither accumulate nor append rejected responses");
                if (calls > 1)
                {
                    const string prefix = "FORMAT_REPAIR:\n";
                    AssertTrue(prompt.Last().Content.StartsWith(prefix, StringComparison.Ordinal), "one transient repair instruction");
                    var repair = JObject.Parse(prompt.Last().Content.Substring(prefix.Length));
                    AssertEqual(calls, (int)repair["attempt"], "repair names the total protocol attempt, including the initial response");
                    AssertEqual(invalid.Length + 1, (int)repair["max_attempts"], "repair reports the configured total limit");
                }
                AssertTrue(prompt.All(message => string.IsNullOrEmpty(message.ReasoningContent)), "rejected reasoning never enters prompt");
                AssertEqual(stepId, options.TraceStepId, "one logical step across repairs");
                AssertTrue(attemptIds.Add(options.TraceModelAttemptId), "distinct raw attempt identity");
                AssertTrue(options.TraceRequestId == null, "previous request identity is cleared");
                options.TraceRequestId = "protocol-request-" + calls;
                return Task.FromResult(new LlmCompletionResult
                {
                    Content = calls <= invalid.Length ? invalid[calls - 1] :
                        "{\"status\":\"completed\",\"message\":\"  Accepted.  \",\"tool_calls\":[]}",
                    ReasoningContent = calls <= invalid.Length ? "REJECTED_REASONING" : "accepted reasoning",
                    PromptTokens = calls * 10
                });
            });
            var result = await protocol.GetResponseAsync(request, new ModelProtocolProgress
            {
                AttemptStarted = streaming => started++, AttemptCompleted = () => completed++
            }, CancellationToken.None);
            AssertTrue(result.Failure == null, result.Failure == null ? "accepted typed result" : result.Failure.Message);
            AssertEqual("Accepted.", result.Response.Message, "accepted v2 text retains trimming behavior");
            AssertEqual("accepted reasoning", result.Completion.ReasoningContent, "only accepted completion metadata leaves protocol");
            AssertEqual(invalid.Length + 1, calls, "valid response ends the protocol operation");
            AssertEqual(calls, started, "each raw attempt resets presentation");
            AssertEqual(calls, completed, "each completed response flushes presentation");
            AssertEqual(originalPrompt, JsonConvert.SerializeObject(request.AcceptedMessages), "accepted input remains unchanged");
            AssertEqual(string.Join("|", invalid), string.Join("|", trace.Where(record => record.Type == "rejected")
                .Select(record => record.PayloadJson)), "rejected raw bodies are diagnostics only");
            AssertEqual(1, trace.Count(record => record.Type == "accepted"), "exactly one accepted marker");
            AssertEqual("protocol-request-" + calls, trace.Last().RequestId, "accepted marker links to the valid raw request");
            AssertTrue(!JsonConvert.SerializeObject(result).Contains("REJECTED_"), "result contains no rejected payloads");
        }

        private static async Task ModelProtocolReturnsTypedExhaustion()
        {
            var request = NewProtocolRequest(1);
            var trace = new List<LlmTraceRecord>();
            request.Options.TraceSink = trace.Add;
            var calls = 0;
            var protocol = new ModelProtocolClient((settings, messages, options, stream, token) =>
            {
                calls++;
                return Task.FromResult(new LlmCompletionResult { Content = "REJECTED_EXHAUSTED", ReasoningContent = "REJECTED_REASONING" });
            });
            var result = await protocol.GetResponseAsync(request, null, CancellationToken.None);
            AssertEqual(ModelProtocolFailureKind.ProtocolExhausted, result.Failure.Kind, "format exhaustion is a typed failure");
            AssertTrue(result.Response == null && result.Completion == null, "no rejected completion crosses the result boundary");
            AssertEqual(1, calls, "one configured protocol attempt includes the initial response");
            AssertEqual(calls, trace.Count(record => record.Type == "rejected"), "all rejected attempts remain diagnostic evidence");
            AssertTrue(trace.All(record => record.Type != "accepted"), "exhaustion has no accepted response");
            AssertTrue(!JsonConvert.SerializeObject(result).Contains("REJECTED_"), "failure projection excludes raw bodies and reasoning");
        }

        private static async Task ModelProtocolSeparatesProviderFailures()
        {
            foreach (var kind in new[] { LlmFailureKind.Timeout, LlmFailureKind.Network,
                LlmFailureKind.TransientServer, LlmFailureKind.RateLimited, LlmFailureKind.Http,
                LlmFailureKind.InvalidResponse, LlmFailureKind.RequestTooLarge, LlmFailureKind.ResponseTooLarge })
            foreach (var afterInvalid in new[] { false, true })
            {
                var request = NewProtocolRequest(3, true);
                var transient = kind == LlmFailureKind.Timeout || kind == LlmFailureKind.Network || kind == LlmFailureKind.TransientServer;
                var failure = new LlmRequestException(kind, "provider failure", statusCode:
                    kind == LlmFailureKind.TransientServer ? 503 : kind == LlmFailureKind.RateLimited ? 429 : kind == LlmFailureKind.Http ? 401 : (int?)null);
                var calls = 0;
                var delays = new List<TimeSpan>();
                string retryPrompt = null;
                var protocol = new ModelProtocolClient((settings, messages, options, stream, token) =>
                {
                    calls++;
                    if (afterInvalid && calls == 1) return Task.FromResult(new LlmCompletionResult { Content = "invalid response" });
                    var currentPrompt = JsonConvert.SerializeObject(messages);
                    if (retryPrompt == null) retryPrompt = currentPrompt;
                    AssertEqual(retryPrompt, currentPrompt, "provider retries do not create or change a repair prompt");
                    throw failure;
                }, (delay, token) => { delays.Add(delay); return Task.CompletedTask; });
                var result = await protocol.GetResponseAsync(request, null, CancellationToken.None);
                AssertEqual(ModelProtocolFailureKind.Provider, result.Failure.Kind, "provider failure is not format exhaustion");
                AssertEqual(kind, result.Failure.ProviderKind.Value, "transport failure kind is retained");
                AssertEqual(failure.StatusCode, result.Failure.StatusCode, "transport status is retained");
                AssertTrue(ReferenceEquals(failure, result.Failure.Cause), "legacy exception adapter retains the original exception");
                AssertTrue(result.Response == null && result.Completion == null, "transport failure has no accepted model response");
                AssertEqual((afterInvalid ? 1 : 0) + (transient ? 3 : 1), calls, "only transient transport failures retry, within a separate bounded budget");
                AssertEqual(transient ? "1,2" : "", string.Join(",", delays.Select(delay => delay.TotalSeconds)), "transient retries use cancellable 1s/2s delays");
                AssertEqual(LlmResponseFormats.JsonSchema, request.Options.ResponseFormat, "only explicit schema rejection permits fallback");
            }
        }

        private static async Task ModelProtocolCancellationStopsAttempts()
        {
            // Before dispatch, during response, at the final rejection, and a late valid response.
            foreach (var point in new[] { 0, 1, 2, 3 })
            using (var cancellation = new CancellationTokenSource())
            {
                var request = NewProtocolRequest(1);
                var calls = 0;
                request.Options.TraceSink = record => { if (point == 2 && record.Type == "rejected") cancellation.Cancel(); };
                var protocol = new ModelProtocolClient((settings, messages, options, stream, token) =>
                {
                    calls++;
                    if (point == 1)
                    {
                        cancellation.Cancel();
                        return Task.FromCanceled<LlmCompletionResult>(token);
                    }
                    if (point == 3)
                    {
                        cancellation.Cancel();
                        return Task.FromResult(new LlmCompletionResult { Content = "{\"status\":\"completed\",\"message\":\"Late response.\",\"tool_calls\":[]}" });
                    }
                    return Task.FromResult(new LlmCompletionResult { Content = "invalid response" });
                });
                if (point == 0) cancellation.Cancel();
                var result = await protocol.GetResponseAsync(request, null, cancellation.Token);
                AssertEqual(ModelProtocolFailureKind.Cancelled, result.Failure.Kind, "cancellation stays typed at each boundary");
                AssertEqual(point == 0 ? 0 : 1, calls, "cancellation prevents further raw dispatch");
                AssertTrue(result.Response == null && result.Completion == null, "cancelled attempt is never accepted");
            }
        }

        private static async Task ModelProtocolFallbackStaysWithinRun()
        {
            var request = NewProtocolRequest(strict: true);
            var stepId = request.Options.TraceStepId;
            var formats = new List<string>();
            var attempts = new HashSet<string>();
            var fallbacks = 0;
            LlmCompletionDelegate completion = (settings, messages, options, stream, token) =>
            {
                formats.Add(options.ResponseFormat);
                AssertTrue(attempts.Add(options.TraceModelAttemptId), "fallback also gets a distinct attempt identity");
                if (formats.Count <= 2)
                {
                    AssertTrue(ReferenceEquals(request.Options, options), "configured trace sink stays attached to its options instance");
                    AssertEqual(stepId, options.TraceStepId, "fallback retains the logical step");
                }
                if (formats.Count == 1) throw new LlmRequestException(LlmFailureKind.ResponseFormatUnsupported, "unsupported schema");
                if (options.ResponseFormat == LlmResponseFormats.JsonObject)
                    AssertTrue(options.ResponseSchemaName == null && options.ResponseSchemaJson == null, "fallback clears strict schema fields");
                return Task.FromResult(new LlmCompletionResult { Content = "{\"status\":\"completed\",\"message\":\"Done.\",\"tool_calls\":[]}" });
            };
            var protocol = new ModelProtocolClient(completion);
            var first = await protocol.GetResponseAsync(request,
                new ModelProtocolProgress { JsonObjectFallback = () => fallbacks++ }, CancellationToken.None);
            var next = await protocol.GetResponseAsync(NewProtocolRequest(strict: true), null, CancellationToken.None);
            var anotherRun = await new ModelProtocolClient(completion).GetResponseAsync(NewProtocolRequest(strict: true), null, CancellationToken.None);
            AssertTrue(first.Failure == null && next.Failure == null && anotherRun.Failure == null, "fallback and later steps accept valid responses");
            AssertEqual("json_schema,json_object,json_object,json_schema", string.Join(",", formats), "compatibility state persists only within one run");
            AssertEqual(1, fallbacks, "one local fallback notification");
            AssertEqual(AgentResponseModes.JsonSchema, request.Settings.AgentResponseMode, "saved response mode never changes");
        }

        private static async Task ModelProtocolFallbackIsBounded()
        {
            foreach (var enabled in new[] { false, true })
            {
                var request = NewProtocolRequest(strict: true);
                request.Settings.FallbackToJsonObject = enabled;
                var calls = 0;
                var protocol = new ModelProtocolClient((settings, messages, options, stream, token) =>
                {
                    calls++;
                    throw new LlmRequestException(LlmFailureKind.ResponseFormatUnsupported, "unsupported format");
                });
                var result = await protocol.GetResponseAsync(request, null, CancellationToken.None);
                AssertEqual(enabled ? 2 : 1, calls, "fallback requires opt-in and never repeats");
                AssertEqual(ModelProtocolFailureKind.Provider, result.Failure.Kind, "fallback failure remains a provider failure");
                AssertTrue(result.Completion == null, "failed fallback cannot produce an accepted completion");
            }
        }

        private static async Task ModelProtocolFallbackDuringRepair()
        {
            var request = NewProtocolRequest(2, true);
            var formats = new List<string>();
            var prompts = new List<string>();
            var protocol = new ModelProtocolClient((settings, messages, options, stream, token) =>
            {
                formats.Add(options.ResponseFormat);
                prompts.Add(JsonConvert.SerializeObject(messages));
                if (formats.Count == 2) throw new LlmRequestException(LlmFailureKind.ResponseFormatUnsupported, "schema rejected during repair");
                return Task.FromResult(new LlmCompletionResult
                {
                    Content = formats.Count == 1 ? "invalid model response" :
                        "{\"status\":\"completed\",\"message\":\"Accepted after fallback.\",\"tool_calls\":[]}"
                });
            });
            var result = await protocol.GetResponseAsync(request, null, CancellationToken.None);
            AssertTrue(result.Failure == null, "explicit schema rejection during repair permits the one local fallback");
            AssertEqual("json_schema,json_schema,json_object", string.Join(",", formats), "fallback does not consume a protocol response attempt");
            AssertEqual(prompts[1], prompts[2], "fallback reuses the exact current repair prompt");
            AssertEqual("Accepted after fallback.", result.Response.Message, "the second protocol response can accept");
        }

        private static async Task ModelProtocolProviderRecoveryKeepsProtocolSlots()
        {
            var request = NewProtocolRequest(2);
            var prompts = new List<string>();
            var trace = new List<LlmTraceRecord>();
            var attempts = new HashSet<string>();
            request.Options.TraceSink = trace.Add;
            var delays = new List<TimeSpan>();
            var protocol = new ModelProtocolClient((settings, messages, options, stream, token) =>
            {
                prompts.Add(JsonConvert.SerializeObject(messages));
                AssertTrue(attempts.Add(options.TraceModelAttemptId), "every raw request has a distinct model attempt id");
                options.TraceRequestId = "recovered-request-" + prompts.Count;
                if (prompts.Count == 1 || prompts.Count == 3)
                    throw new LlmRequestException(prompts.Count == 1 ? LlmFailureKind.Timeout : LlmFailureKind.Network, "temporary failure");
                return Task.FromResult(new LlmCompletionResult { Content = prompts.Count == 2 ? "invalid response" :
                    "{\"status\":\"completed\",\"message\":\"Recovered.\",\"tool_calls\":[]}" });
            }, (delay, token) => { delays.Add(delay); return Task.CompletedTask; });
            var result = await protocol.GetResponseAsync(request, null, CancellationToken.None);
            AssertTrue(result.Failure == null, "two transient failures do not consume either protocol response slot");
            AssertEqual("Recovered.", result.Response.Message, "valid second response accepts");
            AssertEqual(4, prompts.Count, "two responses plus two provider retries");
            AssertEqual(prompts[0], prompts[1], "initial provider retry keeps the exact accepted prompt");
            AssertEqual(prompts[2], prompts[3], "repair provider retry keeps the exact repair prompt");
            AssertEqual("1,2", string.Join(",", delays.Select(delay => delay.TotalSeconds)), "provider budget is not reset by a protocol rejection");
            AssertEqual(1, trace.Count(record => record.Type == "rejected"), "only the received invalid completion is a protocol rejection");
            AssertEqual(1, trace.Count(record => record.Type == "accepted"), "only the recovered completion accepts");
            AssertEqual("recovered-request-4", trace.Last().RequestId, "acceptance identifies the recovered request");
        }

        private static async Task ModelProtocolProviderBudgetSpansWholeStep()
        {
            var calls = 0;
            var delays = new List<TimeSpan>();
            var protocol = new ModelProtocolClient((settings, messages, options, stream, token) =>
            {
                calls++;
                if (calls == 1 || calls == 3 || calls == 5 || calls == 6)
                    throw new LlmRequestException(LlmFailureKind.TransientServer, "gateway unavailable", statusCode: 502);
                return Task.FromResult(new LlmCompletionResult { Content = calls < 5 ? "invalid response" :
                    "{\"status\":\"completed\",\"message\":\"Next step.\",\"tool_calls\":[]}" });
            }, (delay, token) => { delays.Add(delay); return Task.CompletedTask; });
            var failed = await protocol.GetResponseAsync(NewProtocolRequest(3), null, CancellationToken.None);
            AssertEqual(ModelProtocolFailureKind.Provider, failed.Failure.Kind, "third transient failure ends the step");
            AssertEqual(5, calls, "provider retries are not multiplied by each protocol attempt");
            AssertEqual("1,2", string.Join(",", delays.Select(delay => delay.TotalSeconds)), "whole step gets two retries total");
            var recovered = await protocol.GetResponseAsync(NewProtocolRequest(1), null, CancellationToken.None);
            AssertTrue(recovered.Failure == null, "a new logical step gets a fresh provider budget");
            AssertEqual(7, calls, "one new provider retry followed by the first valid response");
            AssertEqual("1,2,1", string.Join(",", delays.Select(delay => delay.TotalSeconds)), "new step starts its delay sequence again");
        }

        private static async Task ModelProtocolCombinedBudgetsAreBounded()
        {
            var request = NewProtocolRequest(20, true);
            var trace = new List<LlmTraceRecord>();
            request.Options.TraceSink = trace.Add;
            var calls = 0;
            var delays = 0;
            var fallbacks = 0;
            var protocol = new ModelProtocolClient((settings, messages, options, stream, token) =>
            {
                calls++;
                if (calls == 1) throw new LlmRequestException(LlmFailureKind.ResponseFormatUnsupported, "schema unsupported");
                if (calls == 2 || calls == 4) throw new LlmRequestException(LlmFailureKind.Network, "connection lost");
                return Task.FromResult(new LlmCompletionResult { Content = "invalid response" });
            }, (delay, token) => { delays++; return Task.CompletedTask; });
            var result = await protocol.GetResponseAsync(request,
                new ModelProtocolProgress { JsonObjectFallback = () => fallbacks++ }, CancellationToken.None);
            AssertEqual(ModelProtocolFailureKind.ProtocolExhausted, result.Failure.Kind, "received-response budget still exhausts at twenty");
            AssertEqual(20, trace.Count(record => record.Type == "rejected"), "twenty invalid responses, not twenty-one");
            AssertEqual(23, calls, "total raw ceiling is protocol limit plus two provider retries plus one fallback");
            AssertEqual(2, delays, "provider retry budget remains independent");
            AssertEqual(1, fallbacks, "fallback budget remains independent");
            AssertTrue(trace.All(record => record.Type != "accepted"), "no acceptance after exhaustion");
        }

        private static async Task ModelProtocolCancellationDuringBackoff()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                var calls = 0;
                var delays = 0;
                var protocol = new ModelProtocolClient((settings, messages, options, stream, token) =>
                {
                    calls++;
                    throw new LlmRequestException(LlmFailureKind.Timeout, "temporary timeout");
                }, (delay, token) =>
                {
                    delays++;
                    AssertEqual(cancellation.Token, token, "backoff uses the caller cancellation token");
                    cancellation.Cancel();
                    return Task.FromCanceled(token);
                });
                var result = await protocol.GetResponseAsync(NewProtocolRequest(), null, cancellation.Token);
                AssertEqual(ModelProtocolFailureKind.Cancelled, result.Failure.Kind, "backoff cancellation is not a provider failure");
                AssertEqual(1, calls, "cancellation prevents retry dispatch");
                AssertEqual(1, delays, "only the first backoff begins");
                AssertTrue(result.Response == null && result.Completion == null, "backoff cannot create a model or tool result");
            }
        }

        private static async Task ModelProtocolPreservesRefusalAndTracePolicy()
        {
            var request = NewProtocolRequest();
            var calls = 0;
            var optionalFailures = 0;
            var refusal = new LlmCompletionResult { RefusalContent = "  Native refusal.\n", ReasoningContent = "provider reason" };
            request.Options.TraceSink = record => { throw new IOException("trace sink unavailable"); };
            var protocol = new ModelProtocolClient((settings, messages, options, stream, token) =>
            {
                calls++;
                return Task.FromResult(calls == 1 ? new LlmCompletionResult { Content = "rejected response" } : refusal);
            });
            var rejected = await protocol.GetResponseAsync(request, null, CancellationToken.None);
            AssertEqual(ModelProtocolFailureKind.Infrastructure, rejected.Failure.Kind, "required rejected diagnostic failure stops execution");
            AssertEqual(1, calls, "cannot repair past a failed diagnostic append");
            var accepted = await protocol.GetResponseAsync(request, new ModelProtocolProgress
            {
                OptionalTraceFailed = () => { optionalFailures++; throw new IOException("optional logger unavailable"); }
            }, CancellationToken.None);
            AssertTrue(accepted.Failure == null, "optional accepted marker and logger failure do not change acceptance");
            AssertEqual(AgentResponseStatuses.Refused, accepted.Response.Status, "native refusal is a typed terminal response");
            AssertEqual(refusal.RefusalContent, accepted.Response.Message, "native refusal text is preserved verbatim");
            AssertTrue(ReferenceEquals(refusal, accepted.Completion), "accepted provider metadata is retained");
            AssertEqual(1, optionalFailures, "optional trace failure is reported once");
        }

        private static async Task ModelProtocolStopsBeforeOversizedRequest()
        {
            var request = NewProtocolRequest();
            request.Settings.ContextWindowOverrideTokens = 4096;
            request.AcceptedMessages = new[] { new ChatMessage { Role = "user", Content = new string('x', 100000) } };
            var calls = 0;
            var protocol = new ModelProtocolClient((settings, messages, options, stream, token) =>
            {
                calls++;
                throw new InvalidOperationException("oversized prompt must not reach transport");
            });
            var result = await protocol.GetResponseAsync(request, null, CancellationToken.None);
            AssertEqual(ModelProtocolFailureKind.PromptBudgetExceeded, result.Failure.Kind, "budget rejection belongs to protocol boundary");
            AssertEqual(0, calls, "prompt budget is checked before raw dispatch");
            AssertTrue(result.Response == null && result.Completion == null, "budget failure cannot become an accepted answer");
        }

        private static void RunSummaryUsesActualOutcomesAndMetadata()
        {
            var catalog = new[]
            {
                new ToolDefinition { Id = "test.write_named_read" },
                new ToolDefinition { Id = "test.inspect", MutatesDocument = true },
                new ToolDefinition { Id = "test.local", MutatesLocalState = true },
                new ToolDefinition { Id = "test.pipeline", Executor = "pipeline",
                    PipelineJson = "{\"steps\":[{\"id\":\"nested\",\"toolId\":\"test.inspect\",\"arguments\":{}}]}" }
            };
            var builder = new RunSummaryBuilder(catalog);
            builder.Observe(new ToolCommand { ToolId = catalog[0].Id }, ToolResult.Ok("unknown; all writes applied"));
            AssertEqual(1, builder.Snapshot().ReadOk, "tool names and prose do not imply writes or uncertainty");
            builder.Observe(new ToolCommand { ToolId = catalog[0].Id }, ToolResult.Fail("Everything applied"));
            AssertEqual("errors", builder.Snapshot().ExecutionHealth, "a read error also prevents clean health");
            builder.Observe(new ToolCommand { ToolId = catalog[1].Id }, ToolResult.WaitingConfirmation("Confirm"));
            AssertEqual(0, builder.Snapshot().WriteOk + builder.Snapshot().WriteError, "pending has no final effect");
            builder.Observe(new ToolCommand { ToolId = catalog[2].Id }, ToolResult.Ok("Local state saved"));
            var uncertain = new ToolCommand { ToolId = catalog[3].Id, ToolCallId = "same_model_id" };
            builder.Observe(uncertain, ToolResult.PartialFailure("Some nested writes completed", null, "pipeline_partial_failure"));
            builder.Observe(uncertain, ToolResult.Fail("Later result delivery failed"));
            builder.Observe(new ToolCommand { ToolId = catalog[1].Id }, ToolResult.Fail("No change", null, "write_rejected"));
            var snapshot = builder.Snapshot();
            AssertEqual("unknown", snapshot.ExecutionHealth, "unknown wins over both read and write errors");
            AssertEqual(1, snapshot.WriteUnknown, "nested policy marks pipeline write; re-observation is not a second call");
            AssertEqual(1, snapshot.WriteError, "definite write error counted separately");
            AssertEqual(1, snapshot.ReadError, "read error is not a write error");
            builder.Observe(new ToolCommand { ToolId = catalog[1].Id, ToolCallId = "same_model_id" }, ToolResult.Ok("Saved"));
            AssertEqual(2, builder.Snapshot().WriteOk, "local mutation and distinct invocation sharing a model id both count");
            AssertEqual("unknown", builder.Snapshot().ExecutionHealth, "later success cannot hide unknown");
            AssertEqual(1, snapshot.WriteOk, "published snapshots cannot change when execution continues");
        }

        private static void RunSummaryMapsLegacyUncertaintyConservatively()
        {
            var catalog = new[] { new ToolDefinition { Id = "test.effect", MutatesDocument = true } };
            var outcomes = new[]
            {
                null,
                new ToolResult { Status = "unknown" },
                new ToolResult { Status = "interrupted_unknown" },
                ToolResult.Fail("cancelled after dispatch", null, "tool_effect_uncertain"),
                ToolResult.Fail("no evidence", null, "missing_result")
            };
            foreach (var result in outcomes)
            {
                var builder = new RunSummaryBuilder(catalog);
                builder.Observe(new ToolCommand { ToolId = catalog[0].Id }, result);
                AssertEqual("unknown", builder.Snapshot().ExecutionHealth, "structured uncertainty never becomes an ordinary error");
                AssertEqual(1, builder.Snapshot().WriteUnknown, "one uncertain invocation");
            }
            var missingPolicy = new RunSummaryBuilder(catalog);
            missingPolicy.Observe(new ToolCommand { ToolId = "missing" }, ToolResult.Ok("Success"));
            AssertEqual("unknown", missingPolicy.Snapshot().ExecutionHealth, "unknown policy cannot certify success");
            var legacy = new RunSummaryBuilder(catalog, RunSummaryBuilder.ContinuationSeed(new ChatSession()));
            legacy.Observe(new ToolCommand { ToolId = catalog[0].Id }, ToolResult.Ok("Current call succeeded"));
            AssertEqual("unknown", legacy.Snapshot().ExecutionHealth, "legacy continuation has no invented clean history");
            AssertEqual(0, legacy.Snapshot().WriteUnknown, "missing historical evidence does not fabricate a dispatched write");
        }

        private static void RunSummarySurvivesCancellationAfterUnknown()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                adapter.ThrowOnToolId = "excel.add_sheet";
                var responses = new Queue<string>(new[]
                {
                    LoadToolSchemaResponse("excel.add_sheet", "schema_cancelled_write"),
                    "{\"status\":\"in_progress\",\"message\":\"Создаю лист.\",\"tool_calls\":[{\"id\":\"write\",\"name\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Report\"}}]}"
                });
                var session = NewSession(adapter);
                session.LastRun = new ChatRunRecord { Status = "running" };
                var service = new ConversationRunService(adapter, executor, (settings, messages, options, stream, token) =>
                {
                    if (responses.Count == 0) throw new OperationCanceledException("cancel after unknown write result");
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                });
                var cancelled = false;
                try
                {
                    service.ExecuteAsync(ChatModes.Agent, "Создай лист.", session, NewContext(adapter),
                        new AppSettings { AutoConfirmToolActions = true },
                        adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(), null).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) { cancelled = true; }
                AssertTrue(cancelled, "cancellation propagates to lifecycle owner");
                AssertEqual(1, adapter.Executed.Count(command => command.ToolId == "excel.add_sheet"), "no automatic write retry");
                AssertEqual("unknown", session.LastRun.ExecutionSummary.ExecutionHealth, "cancellation cannot erase unknown");
                var activity = session.Messages.Last(message => message.Activity != null);
                AssertEqual("tool_effect_uncertain", activity.Activity.ErrorCode, "real executor classifies thrown mutation as uncertain");
                AssertEqual(1, activity.ExecutionSummary.WriteUnknown, "visible activity retains uncertainty before controller handling");
            });
        }

        private static void DefaultPromptsAreStructuredMarkdown()
        {
            var settings = new AppSettings();
            AssertTrue(settings.SystemPrompt.StartsWith("# RNAssistant Agent", StringComparison.Ordinal), "agent prompt Markdown heading");
            AssertContains(settings.SystemPrompt, "## Response contract", "agent prompt structured section");
            AssertContains(settings.SystemPrompt, "`status` is required", "agent prompt requires explicit status");
            AssertContains(settings.SystemPrompt, "Choose `tool_calls` before `status`", "agent prompt chooses actions before status");
            AssertContains(settings.SystemPrompt, "`awaiting_user`", "agent prompt distinguishes clarification status");
            AssertTrue(settings.AgentToolsPrompt.StartsWith("# Agent tool policy", StringComparison.Ordinal), "tool prompt is separate Markdown");
            AssertContains(settings.AgentToolsPrompt, "status=in_progress", "tool prompt couples explicit status to execution");
            AssertContains(settings.AgentToolsPrompt, "optional exact `resources`", "tool prompt explains externalized results");
            AssertTrue(settings.AgentSkillsPrompt.StartsWith("# Agent skill policy", StringComparison.Ordinal), "skill prompt is separate Markdown");
            AssertContains(settings.AgentSkillsPrompt, "metadata only", "skill catalog is explicitly not loaded guidance");
            AssertContains(settings.AgentSkillsPrompt, "package `revision`", "skill prompt describes revisions");
            AssertContains(settings.AgentSkillsPrompt, "`loaded=true`", "skill prompt defines explicit loaded evidence");
            AssertContains(settings.AgentSkillsPrompt, "do not retry unchanged", "skill prompt prevents truncated skill loops");
            AssertContains(settings.AgentSkillsPrompt, "referencePath", "skill prompt explains progressive reference reads");
            AssertTrue(settings.ChatSystemPrompt.StartsWith("# RNAssistant Chat", StringComparison.Ordinal), "chat prompt Markdown heading");
            AssertContains(settings.ChatSystemPrompt, "common.resources_*", "chat prompt documents read-only resource access");
            AssertContains(settings.ChatSystemPrompt, "## Response contract", "chat uses the structured response envelope");
            AssertContains(settings.ChatSystemPrompt, "multimodal model", "chat prompt keeps current media direct when supported");
            AssertTrue(settings.ContextCompactionPrompt.StartsWith("# Context compaction", StringComparison.Ordinal), "compaction prompt Markdown heading");
            AssertContains(settings.ContextCompactionPrompt, "Skill ids and revisions", "compaction preserves pending skill references");
            AssertTrue(settings.ChatTitlePrompt.StartsWith("# Chat title", StringComparison.Ordinal), "title prompt Markdown heading");
            AssertTrue(settings.AttachmentAnalysisPrompt.StartsWith("# Attachment analysis", StringComparison.Ordinal), "attachment worker prompt is editable Markdown");
            AssertEqual(AppSettings.DefaultMaxTokens, settings.MaxTokens, "long-run output token default");
            AssertEqual(AppSettings.DefaultRequestTimeoutSeconds, settings.RequestTimeoutSeconds, "long-run request timeout default");
            AssertEqual(AppSettings.DefaultMaxAgentIterations, settings.MaxAgentIterations, "long-run iteration default");
            AssertEqual(AppSettings.DefaultMaxAgentFormatRetries, settings.MaxAgentFormatRetries, "long-run format retry default");
            AssertEqual(AppSettings.DefaultMaxAgentToolSteps, settings.MaxAgentToolSteps, "long-run tool step default");
            AssertTrue(settings.ScreenCaptureProtectionEnabled, "screen capture protection default");
            AssertEqual(ReasoningRequestModes.ChatTemplateKwargs, settings.ReasoningRequestMode, "reasoning request mode default");
            AssertEqual(string.Empty, settings.BaseUrl, "base URL default");
            AssertEqual("/v1/models", settings.ModelsConfigUrl, "models endpoint default");
            AssertEqual(string.Empty, settings.Model, "model default");
            AssertEqual(5, AppSettings.DefaultMaxImagesPerPrompt, "configured image count default");
            AssertEqual(AppSettings.DefaultMaxImagesPerPrompt, ModelContextBudget.MaxImagesPerPrompt(settings), "image count default");
            settings.BaseUrl = "http://127.0.0.1:8000/v1";
            AssertEqual("http://127.0.0.1:8000/v1/models", LlmClient.BuildModelsConfigUrl(settings), "relative models endpoint");
        }

        private static void ConversationV3SchemaMatchesParserAndWire()
        {
            var tool = V3ReadTool();
            var originalSchema = tool.ArgumentSchemaJson;
            var schemaJson = ConversationResponseSchemaBuilder.Build(new[] { tool });
            var schema = JObject.Parse(schemaJson);
            var valid = JObject.Parse(V3Envelope(V3Call(arguments: new JObject
            {
                ["query"] = "A", ["limit"] = JValue.CreateNull(), ["at"] = JValue.CreateNull()
            })));
            string error;
            AssertTrue(ToolSchemaSupport.ValidateArguments((JObject)valid.DeepClone(), schema, false, out error),
                "strict v3 envelope matches generated schema: " + error);
            AssertTrue(ParseV3(valid.ToString(Formatting.None), tool).Success, "local parser accepts strict optional null representation");
            AssertEqual(originalSchema, tool.ArgumentSchemaJson, "response schema construction never rewrites native tool schemas");

            foreach (var field in new[] { "status", "verified", "phase" })
            {
                var invalid = (JObject)valid.DeepClone();
                invalid[field] = "completed";
                AssertTrue(!ToolSchemaSupport.ValidateArguments(invalid, schema, false, out error) &&
                    !ParseV3(invalid.ToString(Formatting.None), tool).Success, "schema/parser agree on unknown root field: " + field);
            }
            var unknown = (JObject)valid.DeepClone();
            unknown["tool_calls"][0]["name"] = "test.unloaded";
            AssertTrue(!ToolSchemaSupport.ValidateArguments(unknown, schema, false, out error) &&
                !ParseV3(unknown.ToString(Formatting.None), tool).Success, "schema/parser reject names outside callable set");
            var missing = (JObject)valid.DeepClone();
            ((JObject)missing["tool_calls"][0]["arguments"]).Remove("query");
            AssertTrue(!ToolSchemaSupport.ValidateArguments(missing, schema, false, out error) &&
                !ParseV3(missing.ToString(Formatting.None), tool).Success, "schema/parser reject missing required arguments");

            var body = LlmClient.BuildRequestBody(new AppSettings { StreamResponses = false },
                new List<object> { new { role = "user", content = "test" } }, 10, new LlmRequestOptions
                {
                    ResponseFormat = LlmResponseFormats.JsonSchema,
                    ResponseSchemaName = ConversationResponseSchemaBuilder.SchemaName,
                    ResponseSchemaJson = schemaJson
                });
            AssertEqual("rnassistant_conversation_response_v3", (string)body.SelectToken("response_format.json_schema.name"), "explicit v3 wire schema identity");
            AssertTrue(JToken.DeepEquals(schema, body.SelectToken("response_format.json_schema.schema")), "LLM transport sends the exact v3 schema");
            AssertTrue(body.SelectToken("response_format.json_schema.strict").Value<bool>(), "v3 schema uses strict transport");
        }

        private static void ConversationV3SchemaAllowsOnlyCallableTools()
        {
            var invalid = V3ReadTool("test.invalid");
            invalid.ArgumentSchemaJson = "{}";
            foreach (var tools in new[] { new ToolDefinition[0], new[] { invalid, null, new ToolDefinition() } })
            {
                var schema = JObject.Parse(ConversationResponseSchemaBuilder.Build(tools));
                string error;
                AssertTrue(ToolSchemaSupport.ValidateArguments(JObject.Parse(V3Envelope()), schema, false, out error), "empty callable set allows final message");
                AssertTrue(!ToolSchemaSupport.ValidateArguments(JObject.Parse(V3Envelope(V3Call())), schema, false, out error),
                    "empty/invalid callable set never offers phantom calls");
            }
            var tool = V3ReadTool();
            var bounded = JObject.Parse(ConversationResponseSchemaBuilder.Build(new[] { tool, tool, invalid }));
            AssertEqual(1, ((JArray)bounded.SelectToken("properties.tool_calls.items.anyOf")).Count,
                "schema uses only unique valid callable contracts");
            var calls = Enumerable.Range(0, 32).Select(i => V3Call("call_" + i, arguments: new JObject
            {
                ["query"] = "A", ["limit"] = JValue.CreateNull(), ["at"] = JValue.CreateNull()
            })).ToArray();
            string limitError;
            AssertTrue(ToolSchemaSupport.ValidateArguments(JObject.Parse(V3Envelope(calls)), bounded, false, out limitError), "schema accepts 32 calls");
            AssertTrue(!ToolSchemaSupport.ValidateArguments(JObject.Parse(V3Envelope(calls.Concat(new[] { calls[0] }).ToArray())), bounded, false, out limitError),
                "schema bounds calls at 32; run uniqueness is enforced locally");
        }

        private static void AgentSupportsSelectableResponseFormats()
        {
            AssertEqual(2, AgentResponseProtocol.CurrentVersion,
                "conversation response protocol cutover version");
            var settings = new AppSettings { StreamResponses = false };
            var messages = new List<object> { new { role = "user", content = "test" } };
            var objectBody = LlmClient.BuildRequestBody(settings, messages, 10, new LlmRequestOptions
            {
                ResponseFormat = LlmResponseFormats.JsonObject
            });
            AssertEqual("json_object", (string)objectBody.SelectToken("response_format.type"), "json_object request type");

            var schemaJson = AgentResponseSchemaBuilder.Build(new ToolDefinition[0]);
            var schemaBody = LlmClient.BuildRequestBody(settings, messages, 10, new LlmRequestOptions
            {
                ResponseFormat = LlmResponseFormats.JsonSchema,
                ResponseSchemaName = AgentResponseSchemaBuilder.SchemaName,
                ResponseSchemaJson = schemaJson
            });
            AssertEqual("json_schema", (string)schemaBody.SelectToken("response_format.type"), "json_schema request type");
            AssertEqual(AgentResponseSchemaBuilder.SchemaName,
                (string)schemaBody.SelectToken("response_format.json_schema.name"), "schema name");
            AssertTrue(schemaBody.SelectToken("response_format.json_schema.strict").Value<bool>(), "strict response schema");
        }

        private static void AgentJsonSchemaMirrorsToolContracts()
        {
            var tool = new ToolDefinition
            {
                Id = "excel.read_range",
                Description = "Read cells.",
                ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{" +
                    "\"range\":{\"type\":\"string\",\"description\":\"A1 range.\"}," +
                    "\"sheet\":{\"type\":\"string\",\"description\":\"Optional sheet name.\"}," +
                    "\"mode\":{\"type\":\"string\",\"description\":\"Read mode.\",\"default\":\"values\",\"enum\":[\"values\",\"formulas\"]}" +
                    "},\"required\":[\"range\"],\"additionalProperties\":false}"
            };
            var schema = JObject.Parse(AgentResponseSchemaBuilder.Build(new[] { tool }));
            var rootRequired = schema["required"] as JArray;
            AssertTrue(rootRequired != null && rootRequired.Values<string>().Contains("status"),
                "strict response schema requires status");
            AssertTrue(((JObject)schema["properties"]).Properties().Select(property => property.Name).SequenceEqual(
                new[] { "message", "tool_calls", "status" }),
                "strict response schema chooses status after the action list");
            var statuses = schema.SelectToken("properties.status.enum") as JArray;
            AssertTrue(statuses != null &&
                statuses.Values<string>().SequenceEqual(new[]
                {
                    AgentResponseStatuses.Completed,
                    AgentResponseStatuses.AwaitingUser,
                    AgentResponseStatuses.Blocked,
                    AgentResponseStatuses.Refused,
                    AgentResponseStatuses.InProgress
                }), "strict response schema exposes the closed status enum");
            AssertTrue(!statuses.Values<string>().Contains(AgentResponseStatuses.Planned),
                "reserved planned status is not offered to the model");
            var call = schema.SelectToken("properties.tool_calls.items.anyOf[0]");
            AssertEqual("excel.read_range", (string)call.SelectToken("properties.name.const"), "exact tool name in schema");
            AssertEqual("string", (string)call.SelectToken("properties.arguments.properties.range.type"), "tool argument schema copied");
            var optionalSheetType = call.SelectToken("properties.arguments.properties.sheet.type") as JArray;
            AssertTrue(optionalSheetType != null && optionalSheetType.Values<string>().Contains("null"),
                "strict response schema makes optional arguments nullable");
            var optionalModeEnum = call.SelectToken("properties.arguments.properties.mode.enum") as JArray;
            AssertTrue(optionalModeEnum != null && optionalModeEnum.Any(item => item.Type == JTokenType.Null),
                "nullable optional enum accepts null");
            var strictRequired = call.SelectToken("properties.arguments.required") as JArray;
            AssertTrue(strictRequired != null && strictRequired.Values<string>().Contains("sheet"),
                "strict response schema still lists every property as required");
            AssertTrue(call.SelectToken("properties.arguments.additionalProperties").Value<bool>() == false, "tool arguments remain strict");
            AssertTrue(schema["additionalProperties"].Value<bool>() == false, "agent response root is strict");

            var noToolSchema = JObject.Parse(AgentResponseSchemaBuilder.Build(new ToolDefinition[0]));
            var noToolStatuses = noToolSchema.SelectToken("properties.status.enum") as JArray;
            AssertTrue(noToolStatuses != null &&
                !noToolStatuses.Values<string>().Contains(AgentResponseStatuses.InProgress),
                "in_progress is not offered when no tool can be called");
        }

        private static void AgentJsonSchemaSupportsTypeNamedArguments()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tool = executor.GetControllerTools().Single(candidate => candidate.Id == "common.tools_upsert");
                var schema = JObject.Parse(AgentResponseSchemaBuilder.Build(new[] { tool }));
                AssertEqual("string",
                    (string)schema.SelectToken("properties.tool_calls.items.anyOf[0].properties.arguments.properties.parameters.properties.type.type"),
                    "schema property named type");

                var patchTool = executor.GetControllerTools().Single(candidate => candidate.Id == "common.vba_apply_patch");
                var patchSchema = JObject.Parse(AgentResponseSchemaBuilder.Build(new[] { patchTool }));
                var exactReplace = patchSchema.SelectToken(
                    "properties.tool_calls.items.anyOf[0].properties.arguments.properties.patch.items") as JObject;
                AssertTrue(exactReplace != null, "patch schema exposes one exact replacement contract");
                AssertEqual("replace", (string)exactReplace.SelectToken("properties.op.enum[0]"),
                    "exact replacement is the only VBA patch operation");
                AssertEqual(3, ((JObject)exactReplace["properties"]).Properties().Count(),
                    "exact replacement exposes only op, find, and text");
                AssertTrue(exactReplace.SelectToken("properties.startLine") == null &&
                    exactReplace.SelectToken("properties.pattern") == null,
                    "line-number and regex patch fields are absent from the model schema");

                var restoreTool = executor.GetControllerTools().Single(candidate => candidate.Id == "common.vba_restore_backup");
                var restoreSchema = JObject.Parse(AgentResponseSchemaBuilder.Build(new[] { restoreTool }));
                var restoreVariants = restoreSchema.SelectToken(
                    "properties.tool_calls.items.anyOf[0].properties.arguments.anyOf") as JArray;
                AssertEqual(2, restoreVariants == null ? 0 : restoreVariants.Count,
                    "restore schema requires either backup id or module name");
                var backupVariant = restoreVariants.OfType<JObject>().Single(item =>
                    item.SelectToken("properties.backupId.type").Type == JTokenType.String);
                var optionalRestoreModuleType = backupVariant.SelectToken("properties.moduleName.type") as JArray;
                AssertTrue(optionalRestoreModuleType != null && optionalRestoreModuleType.Values<string>().Contains("null"),
                    "irrelevant restore selector is nullable in strict output");

                var strictPatchArguments = new JObject
                {
                    ["moduleName"] = "Module1",
                    ["patch"] = new JArray(new JObject
                    {
                        ["op"] = "replace",
                        ["find"] = "Old",
                        ["text"] = "New"
                    })
                };
                JObject runtimePatchSchema;
                string parseError;
                AssertTrue(ToolSchemaSupport.TryParse(patchTool, out runtimePatchSchema, out parseError),
                    "runtime patch schema parses: " + parseError);
                ToolSchemaSupport.RemoveOptionalNulls(strictPatchArguments, runtimePatchSchema);
                AssertEqual(3, ((JObject)((JArray)strictPatchArguments["patch"])[0]).Properties().Count(),
                    "exact patch arguments remain unchanged by strict normalization");
            });
        }

        private static void AgentSupportsSelectableToolResultRoles()
        {
            var call = new AgentToolCall
            {
                Id = "call_1",
                Name = "excel.read_range",
                Arguments = new Dictionary<string, object> { ["range"] = "A1" }
            };
            var command = new ToolCommand { ToolId = call.Name, ToolCallId = call.Id };
            var result = ToolResult.Ok("read", "{\"value\":1}");

            foreach (var role in new[] { ToolResultRoles.User, ToolResultRoles.Developer })
            {
                var callMessage = AgentJsonProtocol.CreateToolCallMessage(call, "Reading.", null, role);
                var resultMessage = AgentJsonProtocol.CreateToolResultMessage(command, result, role);
                AssertTrue(callMessage.ToolCalls.Count == 0, role + " uses JSON envelope history");
                AssertContains(callMessage.Content, "\"status\":\"in_progress\"", role + " replays response status");
                AssertEqual(AgentResponseProtocol.CurrentVersion, callMessage.ResponseProtocolVersion,
                    role + " stores response protocol version");
                AssertEqual(AgentResponseStatuses.InProgress, callMessage.ResponseStatus,
                    role + " stores response status");
                AssertEqual(role, resultMessage.Role, role + " result role");
                AssertContains(resultMessage.Content, "TOOL_RESULT:", role + " result prefix");
            }

            var nativeCall = AgentJsonProtocol.CreateToolCallMessage(call, "Reading.", null, ToolResultRoles.Tool);
            var nativeResult = AgentJsonProtocol.CreateToolResultMessage(command, result, ToolResultRoles.Tool);
            var api = new LlmMessageBuilder().Build(new[] { nativeCall, nativeResult }, new AppSettings());
            var assistant = (JObject)api.Messages[0];
            var toolMessage = (JObject)api.Messages[1];
            AssertEqual("assistant", (string)assistant["role"], "native call role");
            AssertEqual(AgentResponseProtocol.CurrentVersion, nativeCall.ResponseProtocolVersion,
                "native call stores response protocol version");
            AssertEqual(AgentResponseStatuses.InProgress, nativeCall.ResponseStatus,
                "native call stores response status");
            AssertEqual("call_1", (string)assistant.SelectToken("tool_calls[0].id"), "native call id");
            AssertEqual("tool", (string)toolMessage["role"], "native result role");
            AssertEqual("call_1", (string)toolMessage["tool_call_id"], "native result matches call");
            AssertTrue(string.IsNullOrWhiteSpace((string)toolMessage["name"]) == false, "native tool name is API-safe");
        }

        private static void AgentJsonSchemaFallbackIsRequestLocal()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var formats = new List<string>();
                LlmCompletionDelegate completion = (completionSettings, messages, options, stream, cancellationToken) =>
                {
                    formats.Add(options.ResponseFormat);
                    if (formats.Count == 1)
                    {
                        throw new LlmRequestException(LlmFailureKind.ResponseFormatUnsupported, "json_schema unsupported");
                    }
                    return Task.FromResult(new LlmCompletionResult
                    {
                        Content = "{\"status\":\"completed\",\"message\":\"Done.\",\"tool_calls\":[]}"
                    });
                };
                var settings = new AppSettings
                {
                    AgentResponseMode = AgentResponseModes.JsonSchema,
                    FallbackToJsonObject = true
                };
                var result = new ConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent,
                    "Test.", NewSession(adapter), NewContext(adapter), settings, new ToolDefinition[0],
                    null, null, null, CancellationToken.None).GetAwaiter().GetResult();

                AssertEqual("Done.", result.AssistantText, "fallback completes request");
                AssertEqual(2, formats.Count, "fallback makes one retry");
                AssertEqual(LlmResponseFormats.JsonSchema, formats[0], "selected format is tried first");
                AssertEqual(LlmResponseFormats.JsonObject, formats[1], "fallback uses json_object");
                AssertEqual(AgentResponseModes.JsonSchema, settings.AgentResponseMode, "saved selection is unchanged");
            });
        }

        private static void AgentToolResultDataIsBounded()
        {
            var command = new ToolCommand { ToolId = "excel.read_range", ToolCallId = "call_large" };
            var data = JsonConvert.SerializeObject(new { value = new string('x', 50000) + "TOOL_RESULT_END" });
            var toolResult = ToolResult.Ok("read", data);
            var result = JObject.Parse(AgentJsonProtocol.BuildToolResult(command, toolResult, 256));

            AssertTrue(result.SelectToken("data.truncated").Value<bool>(), "oversized data is marked truncated");
            AssertTrue(result.SelectToken("data.original_chars").Value<int>() > 49000, "original size retained");
            AssertTrue(((string)result.SelectToken("data.preview") ?? string.Empty).Length < 1000, "preview is bounded");
            AssertEqual("call_large", (string)result["tool_call_id"], "tool call id retained");

            var resourceSession = new ChatSession();
            var resourceArtifact = ToolResultResourceService.ExternalizeIfNeeded(
                resourceSession,
                command,
                toolResult,
                256,
                new AppSettings());
            AssertTrue(resourceArtifact != null && resourceArtifact.Kind == ChatArtifactKinds.ToolResult,
                "oversized generic result becomes a tool-result resource");
            var resourceEnvelope = JObject.Parse(AgentJsonProtocol.BuildToolResult(command, toolResult, 256));
            var resourceUri = (string)resourceEnvelope.SelectToken("resources[0].uri");
            AssertEqual(ArtifactUri(resourceSession, resourceArtifact), resourceUri,
                "bounded envelope exposes the exact durable result reference");
            AssertEqual("result", (string)resourceEnvelope.SelectToken("resources[0].relation"),
                "externalized full result is distinguished from other produced resources");
            AssertContains((string)resourceEnvelope.SelectToken("data.hint"), "common.resources_read",
                "bounded envelope tells the model how to read the externalized result");
            var firstPage = ReadResource(
                new ResourceGatewayService(), resourceSession, resourceUri, ResourceRepresentations.Text, null, 32000).Result;
            var secondPage = ReadResource(
                new ResourceGatewayService(), resourceSession, resourceUri, ResourceRepresentations.Text, firstPage.NextCursor, 32000).Result;
            AssertContains(firstPage.Text + secondPage.Text, "TOOL_RESULT_END",
                "externalized result remains pageable through the resource gateway");
            WithTempPaths(paths =>
            {
                resourceSession.Host = "Excel";
                resourceSession.DocumentKey = "tool-result-resource";
                resourceSession.DocumentTitle = "ToolResult.xlsx";
                resourceSession.Messages.Add(new ChatMessage
                {
                    Role = "developer",
                    Content = "TOOL_RESULT resource",
                    ProtocolMessage = true,
                    RunId = "run_tool_result",
                    ResourceRefs = new List<ResourceRef> { ArtifactReference(resourceSession, resourceArtifact) }
                });
                new ChatStore(paths).Save(resourceSession);
                var durable = new ChatStore(paths).Load(
                    resourceSession.Host,
                    resourceSession.DocumentKey,
                    resourceSession.Id);
                var durableArtifact = durable.Artifacts.Single(item => item.Id == resourceArtifact.Id);
                AssertTrue(!string.IsNullOrWhiteSpace(durableArtifact.ContentSha256),
                    "tool-result resource body is externalized to CAS");
                AssertContains(durableArtifact.InlineText, "TOOL_RESULT_END",
                    "tool-result resource body survives event replay and CAS hydration");
            });

            var producedArtifact = new ChatArtifact
            {
                Kind = ChatArtifactKinds.Markdown,
                Title = "Produced resource",
                InlineText = "produced"
            };
            resourceSession.Artifacts.Add(producedArtifact);
            var resultWithProducedResource = ToolResult.Ok("read", data);
            resultWithProducedResource.ModelResourceRefs = new[] { ArtifactReference(resourceSession, producedArtifact) };
            var externalizedAlongsideProduced = ToolResultResourceService.ExternalizeIfNeeded(
                resourceSession, command, resultWithProducedResource, 256, new AppSettings());
            AssertTrue(externalizedAlongsideProduced != null && resultWithProducedResource.ModelResourceRefs.Count == 2,
                "a produced-resource reference does not suppress externalization of an independent oversized result");

            var chartSession = new ChatSession
            {
                Host = "Excel",
                DocumentKey = "chart-resource",
                DocumentTitle = "Chart.xlsx"
            };
            var chartData = JsonConvert.SerializeObject(new
            {
                type = "rnassistant.chart",
                title = "Sales",
                rows = new[] { new { month = "Jan", value = 10 } }
            });
            var chartResult = ToolResult.Ok("chart", chartData);
            var chartArtifact = ToolResultResourceService.ExternalizeIfNeeded(
                chartSession, command, chartResult, 10000, new AppSettings());
            AssertEqual(ChatArtifactKinds.Chart, chartArtifact == null ? null : chartArtifact.Kind,
                "chart result becomes its specialized resource even when it fits inline");
            var chartEnvelope = JObject.Parse(AgentJsonProtocol.BuildToolResult(command, chartResult, 10000));
            AssertEqual(ArtifactUri(chartSession, chartArtifact), (string)chartEnvelope.SelectToken("resources[0].uri"),
                "chart URI is available to the next model step");
            AssertEqual("result", (string)chartEnvelope.SelectToken("resources[0].relation"),
                "chart result resource has an explicit relation");
            AssertEqual(ChatArtifactKinds.Chart, (string)chartEnvelope.SelectToken("resources[0].kind"),
                "chart result resource exposes its specialized kind");
            AssertEqual(true, (bool?)chartEnvelope.SelectToken("data.externalized"),
                "chart result body is reference-only in model history");
            AssertTrue(chartEnvelope.ToString(Formatting.None).IndexOf("\"month\":\"Jan\"", StringComparison.Ordinal) < 0,
                "chart body is absent from the model tool-result envelope");
            AssertEqual(chartArtifact.Id, ToolResultResourceService.ExternalizeIfNeeded(
                chartSession, command, chartResult, 10000, new AppSettings()).Id,
                "chart result externalization is idempotent for an existing exact reference");
            var chartActivity = AgentTranscript.CreateToolActivity(command, chartResult, "tool");
            AssertEqual(true, (bool?)JObject.Parse(chartActivity.DataJson)["externalized"],
                "durable chart activity keeps a resource pointer instead of duplicate chart data");
            AssertEqual(ArtifactUri(chartSession, chartArtifact),
                (string)JObject.Parse(chartActivity.DataJson).SelectToken("resource.uri"),
                "durable chart activity points at the exact chart revision");
            chartSession.Messages.Add(new ChatMessage
            {
                Role = "assistant",
                Activity = chartActivity,
                ResourceRefs = chartResult.ModelResourceRefs.ToList()
            });
            WithTempPaths(paths =>
            {
                new ChatStore(paths).Save(chartSession);
                var events = File.ReadAllText(SessionEventFile(paths, chartSession));
                AssertTrue(events.IndexOf("\"month\":\"Jan\"", StringComparison.Ordinal) < 0,
                    "chart body is absent from the durable conversation event");
                var durable = new ChatStore(paths).Load(
                    chartSession.Host,
                    chartSession.DocumentKey,
                    chartSession.Id);
                AssertEqual(1, durable.Artifacts.Count(item => item.Kind == ChatArtifactKinds.Chart),
                    "chart storage projection reuses the pre-dispatch resource without a duplicate");
                AssertContains(durable.Messages.Single().Activity.DataJson, "\"month\":\"Jan\"",
                    "chart UI projection rehydrates the body from CAS");
            });

            var skillCommand = new ToolCommand { ToolId = CapabilityDiscoveryExecutor.ReadToolId, ToolCallId = "call_skill_large" };
            var skillData = JsonConvert.SerializeObject(new { kind = "skill", id = "common.large", revision = "r1", loaded = true, complete = true, truncated = false, bodyMarkdown = new string('x', 50000) });
            var oversizedSkillResult = ToolResult.Ok("Skill loaded", skillData);
            AgentJsonProtocol.FailClosedOversizedCapabilityEvidence(skillCommand, oversizedSkillResult, 256, new AppSettings());
            var boundedSkill = JObject.Parse(AgentJsonProtocol.BuildToolResult(skillCommand, oversizedSkillResult, 256));
            AssertEqual(false, (bool)boundedSkill["ok"], "oversized capability evidence fails closed");
            AssertEqual("capability_evidence_context_too_large", (string)boundedSkill.SelectToken("error.code"),
                "oversized capability evidence has an actionable error");
            AssertEqual(false, (bool)boundedSkill.SelectToken("data.loaded"), "oversized skill never claims loaded evidence");
            AssertEqual(true, (bool)boundedSkill.SelectToken("data.truncated"), "oversized skill reports incomplete transport");
            AssertTrue(ToolResultResourceService.ExternalizeIfNeeded(
                    new ChatSession(), skillCommand, ToolResult.Ok("read", skillData), 256, new AppSettings()) == null,
                "trusted skill evidence is not duplicated into an untrusted artifact");

            var fittingSchemaResult = ToolResult.Ok("Tool schema loaded", JsonConvert.SerializeObject(new
            {
                kind = "tool-schema",
                id = "common.small",
                revision = "r2",
                loaded = true,
                complete = true,
                truncated = false,
                descriptor = new { type = "function" }
            }));
            AgentJsonProtocol.FailClosedOversizedCapabilityEvidence(skillCommand, fittingSchemaResult, 256, new AppSettings());
            AssertTrue(fittingSchemaResult.Success, "complete capability evidence that fits remains successful");

            var nestedData = JsonConvert.SerializeObject(new { value = new string('x', 200000) });
            var pipeline = AgentTranscript.CreateToolActivity(command, ToolResult.Ok("pipeline", JsonConvert.SerializeObject(new
            {
                steps = new[]
                {
                    new { id = "nested", toolId = "excel.read_range", success = true, dataJson = nestedData }
                }
            })), "tool");
            AssertEqual(1, pipeline.Children.Count, "pipeline child retained");
            AssertContains(pipeline.Children[0].DataJson, "truncated", "nested pipeline data is bounded");
            AssertTrue(pipeline.Children[0].DataJson.Length < 10000, "nested pipeline preview is bounded");
        }

        private static void AgentToolResultFitsRemainingPromptBudget()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.QueueResult("excel.inspect", ToolResult.Ok(
                    "large read",
                    JsonConvert.SerializeObject(new { value = new string('x', 150000) })));
                var responses = new Queue<string>(new[]
                {
                    LoadToolSchemaResponse("excel.inspect", "schema_large_inspect"),
                    "{\"status\":\"in_progress\",\"message\":\"Читаю.\",\"tool_calls\":[{\"id\":\"call_large\",\"name\":\"excel.inspect\",\"arguments\":{\"kind\":\"sheets\"}}]}",
                    "{\"status\":\"completed\",\"message\":\"Диапазон результата нужно сузить.\",\"tool_calls\":[]}"
                });
                var calls = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (completionSettings, messages, options, stream, cancellationToken) =>
                {
                    calls.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var settings = new AppSettings
                {
                    ContextWindowOverrideTokens = 12000,
                    MaxTokens = 512
                };
                var tools = adapter.GetBuiltInTools().Where(tool => tool.Id == "excel.inspect")
                    .Concat(executor.GetControllerTools())
                    .ToList();

                var turn = new ConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent,
                    "List sheets.", NewSession(adapter), NewContext(adapter), settings, tools,
                    null, null, null, null, CancellationToken.None, true).GetAwaiter().GetResult();

                AssertEqual("Диапазон результата нужно сузить.", turn.AssistantText, "agent continues after bounded result");
                AssertEqual(3, calls.Count, "schema read, data read, and final model calls");
                var replay = FlattenSimple(calls[2]);
                AssertContains(replay, "\"truncated\":true", "bounded marker reaches model");
                var estimated = ModelContextBudget.EstimateMessagesTokens(calls[1]) +
                    ModelContextBudget.EstimateRequestOptionsTokens(new LlmRequestOptions { ResponseFormat = LlmResponseFormats.JsonObject });
                AssertTrue(estimated <= ModelContextBudget.InputBudgetTokens(settings), "next prompt stays within budget");
            });
        }

        private static void ModelCompatibilityAcceptsExactSentinels()
        {
            var responses = new Queue<string>(new[]
            {
                "ROLE_OK",
                "{\"status\":\"in_progress\",\"message\":\"TOOL_OK\",\"tool_calls\":[{\"id\":\"call_1\",\"name\":\"compat.echo\",\"arguments\":{\"value\":\"A\"}}]}",
                "{\"status\":\"completed\",\"message\":\"RESULT_OK\",\"tool_calls\":[]}"
            });
            var requests = new List<Tuple<List<ChatMessage>, LlmRequestOptions>>();
            LlmCompletionDelegate completion = (providerSettings, messages, options, stream, cancellationToken) =>
            {
                requests.Add(Tuple.Create(messages.ToList(), options));
                return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
            };

            var settings = new AppSettings
            {
                SystemPromptRole = "system",
                AgentResponseMode = AgentResponseModes.JsonSchema,
                ToolResultRole = ToolResultRoles.Tool
            };
            var result = new ModelCompatibilityService(completion).TestAsync(settings, CancellationToken.None)
                .GetAwaiter().GetResult();

            AssertTrue(result.Compatible, "exact compatibility flow accepted");
            AssertTrue(result.Checks.All(check => check.Passed), "all exact probes pass");
            AssertEqual("system", result.InstructionRole, "selected instruction role reported");
            AssertEqual(AgentResponseModes.JsonSchema, result.ResponseMode, "selected response mode reported");
            AssertEqual(ToolResultRoles.Tool, result.ToolResultRole, "selected tool result role reported");
            AssertEqual(LlmResponseFormats.JsonSchema, requests[1].Item2.ResponseFormat, "compatibility uses selected schema mode");
            AssertTrue(requests[2].Item1.Any(message => string.Equals(message.Role, "tool", StringComparison.Ordinal)),
                "compatibility uses selected tool role");
            AssertTrue(requests[2].Item1.Any(message => message.ToolCalls != null && message.ToolCalls.Count == 1),
                "compatibility sends matched assistant tool call");
        }

        private static void ModelCompatibilityRejectsLooseResponses()
        {
            var responses = new Queue<string>(new[]
            {
                "Any non-empty response",
                "{\"status\":\"in_progress\",\"message\":\"TOOL_OK\",\"tool_calls\":[{\"id\":\"call_1\",\"name\":\"compat.echo\",\"arguments\":{\"value\":\"WRONG\"}}]}",
                "{\"status\":\"completed\",\"message\":\"Any final message\",\"tool_calls\":[]}"
            });
            LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
                Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });

            var result = new ModelCompatibilityService(completion).TestAsync(new AppSettings(), CancellationToken.None)
                .GetAwaiter().GetResult();

            AssertTrue(!result.Compatible, "loose compatibility flow rejected");
            AssertTrue(result.Checks.All(check => !check.Passed), "each loose probe fails");
        }

        private static void ModelConnectionProbeReportsTimings()
        {
            LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
            {
                AssertEqual(16, settings.MaxTokens, "probe output is bounded");
                AssertEqual(false, options.ReasoningEnabled.Value, "probe disables reasoning");
                options.DiagnosticProgress(new LlmRequestDiagnosticUpdate
                {
                    RequestId = "probe-1",
                    Phase = LlmRequestDiagnosticPhases.Completed,
                    Model = settings.Model,
                    StreamRequested = settings.StreamResponses,
                    ElapsedMs = 25,
                    PreparationMs = 2,
                    ResponseHeadersMs = 15,
                    FirstChunkMs = 20,
                    TotalMs = 25,
                    StatusCode = 200
                });
                return Task.FromResult(new LlmCompletionResult { Content = "PONG" });
            };

            var result = new ModelConnectionTestService(completion).TestAsync(new AppSettings(), CancellationToken.None)
                .GetAwaiter().GetResult();

            AssertTrue(result.Success, "connection probe succeeds on non-empty response");
            AssertEqual("probe-1", result.Diagnostics.RequestId, "probe diagnostics retained");
            AssertEqual(20L, result.Diagnostics.FirstChunkMs.Value, "first chunk timing retained");
            AssertEqual(200, result.Diagnostics.StatusCode.Value, "HTTP status retained");
        }

        private static void ModelDiagnosticsStreamReportsFirstChunk()
        {
            const string sse = "data: {\"choices\":[{\"delta\":{\"content\":\"PONG\"}}]}\n\ndata: [DONE]\n";
            var firstChunkCount = 0;
            LlmCompletionResult result;
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse)))
            {
                result = LlmResponseParser.ReadStreamingOrJsonResponseAsync(
                    stream,
                    null,
                    CancellationToken.None,
                    null,
                    () => firstChunkCount++).GetAwaiter().GetResult();
            }

            AssertEqual("PONG", result.Content, "stream content parsed");
            AssertEqual(1, firstChunkCount, "first stream chunk reported once");
        }

        private static void ModelDiagnosticsTrackerReportsOneTerminalLifecycle()
        {
            var requestUpdates = new List<LlmRequestDiagnosticUpdate>();
            var globalUpdates = new List<LlmRequestDiagnosticUpdate>();
            var tracker = new LlmRequestDiagnosticsTracker(
                new AppSettings { Model = "diagnostic-model", StreamResponses = true },
                requestUpdates.Add,
                globalUpdates.Add,
                null);

            tracker.Sending(123);
            tracker.Headers(202);
            tracker.FirstChunk();
            tracker.FirstChunk();
            tracker.Completed();
            tracker.Failed(new InvalidOperationException("ignored after completion"));

            AssertEqual(
                "preparing,sending,headers,first_chunk,completed",
                string.Join(",", requestUpdates.Select(update => update.Phase).ToArray()),
                "diagnostic lifecycle phases");
            AssertEqual(requestUpdates.Count, globalUpdates.Count, "request and global diagnostics receive each phase");
            AssertEqual(123L, requestUpdates.Last().RequestBytes.Value, "request size retained");
            AssertEqual(202, requestUpdates.Last().StatusCode.Value, "response status retained");
            AssertTrue(requestUpdates.Last().TotalMs.HasValue, "terminal duration retained");

            var cancelledUpdates = new List<LlmRequestDiagnosticUpdate>();
            var cancelled = new LlmRequestDiagnosticsTracker(new AppSettings(), cancelledUpdates.Add, null, null);
            cancelled.Failed(new OperationCanceledException("cancelled"));
            AssertEqual(LlmRequestDiagnosticPhases.Cancelled, cancelledUpdates.Last().Phase, "cancellation is terminal phase");

            var failedUpdates = new List<LlmRequestDiagnosticUpdate>();
            var failed = new LlmRequestDiagnosticsTracker(new AppSettings(), failedUpdates.Add, null, null);
            failed.Failed(new LlmRequestException(LlmFailureKind.Timeout, "timeout"));
            AssertEqual(LlmRequestDiagnosticPhases.Failed, failedUpdates.Last().Phase, "request error is failed phase");
            AssertEqual(LlmFailureKind.Timeout, failedUpdates.Last().FailureKind.Value, "failure kind retained");
        }
    }
}
