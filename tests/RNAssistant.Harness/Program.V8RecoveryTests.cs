using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Application;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Core.Storage;
using RNAssistant.Runtime;
using RNAssistant.Runtime.Contracts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static string RecoveryReflection()
        { return ModelProtocolWire.WriteAcceptedDecision("reflection", "Choose a safe approach.", "", "Read before changing."); }
        private static string RecoveryFinish()
        { return ModelProtocolWire.WriteAcceptedDecision("finish", "Answer the question.", "Ready.", results: new ConversationResultClaim[0]); }

        private static void V8RecoveryReflectionDepth()
        {
            AssertEqual(2, JsonConvert.DeserializeObject<AgentRunLimits>("{\"MaxIterations\":10,\"MaxToolSteps\":10}").MaxConsecutiveReflections,
                "an older v8 checkpoint retains its previous depth");
            foreach (var depth in new[] { 0, 1, 3 })
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) => {
                var settings = new AppSettings { MaxConsecutiveReflections = depth, MaxAgentIterations = 10, SmartChatTitles = false };
                AssertEqual(depth, settings.Clone().MaxConsecutiveReflections, "settings clone preserves zero/nondefault depth");
                var controls = SettingsControlsDto.From(settings);
                AssertEqual(depth, controls.ApplyTo(settings).MaxConsecutiveReflections, "UI settings roundtrip preserves depth");
                var script = new Queue<string>();
                for (var index = 0; index < depth; index++) script.Enqueue(RecoveryReflection());
                script.Enqueue("{\"action\":\"reflection\",\"decision\":\"invalid\",\"message\":\"\"}");
                script.Enqueue(RecoveryReflection()); // One over-limit proposal receives semantic feedback.
                script.Enqueue("{\"action\":\"finish\",\"results\":[],\"message\":\"invalid\"}");
                script.Enqueue(RecoveryFinish());
                var requests = new List<string>(); var activities = new List<ChatActivity>();
                var session = NewSession(adapter);
                var service = CreateConversationRunService(adapter, executor, (config, messages, options, stream, token) => {
                    requests.Add(string.Join("\n", messages.Select(message => message.Content)));
                    return Task.FromResult(new LlmCompletionResult { Content = script.Dequeue(), ReasoningContent = "Saved reasoning.", ReasoningTokens = 7 });
                });
                var result = service.ExecuteAsync(ChatModes.Agent, "Answer directly.", session, NewContext(adapter), settings,
                    OfficeToolCatalog.ForHost("Excel").Concat(executor.GetControllerTools()).ToArray(),
                    (phase, text, activity) => { if (phase == "reflection") activities.Add(activity); }).GetAwaiter().GetResult();
                AssertEqual("model_finish", result.RunViewState.Reason, "format repairs preserve the correction opportunity");
                AssertEqual(depth + 2, session.LastRun.IterationsUsed, "inner format retries do not consume kernel iterations or reflection depth");
                AssertEqual(depth, session.LastRun.KernelState.ConsecutiveReflections, "only accepted reflections count");
                AssertEqual(depth, session.LastRun.KernelState.Limits.MaxConsecutiveReflections, "run freezes selected depth");
                AssertContains(requests[depth], "0 remaining", "the request at the cap already asks execute/finish");
                AssertContains(requests[depth + 3], "reflection_limit_feedback", "format repair preserves semantic correction");
                AssertEqual(depth, activities.Count, "one typed live step per accepted reflection");
                AssertTrue(activities.All(activity => activity != null && !string.IsNullOrEmpty(activity.StepId) && activity.Status == "completed"),
                    "accepted reflections are terminal step activities, not generic thinking notices");
                var reopened = new ChatStore(FixturePaths.Value).Load(session.Id);
                var visible = ChatCloneService.CloneMessagesForBridge(reopened, reopened.Messages).Where(message => !message.ProtocolMessage && message.Activity?.Kind == "step").ToArray();
                AssertEqual(depth, visible.Length, "visible reflection history survives bridge projection");
                AssertTrue(visible.All(message => message.ReasoningContent == "Saved reasoning." && message.ReasoningTokens == 7),
                    "accepted reflection reasoning survives reopen");
                AssertTrue(!session.Messages.Any(message => (message.Content ?? "").Contains("invalid")), "rejected formats remain out of accepted history");
            });
        }

        private static void V8RecoveryExecuteReasoning()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) => {
                var session = NewSession(adapter); var attempt = 0;
                var service = CreateConversationRunService(adapter, executor, (settings, messages, options, stream, token) => {
                    var first = ++attempt == 1;
                    return Task.FromResult(new LlmCompletionResult {
                        Content = first ? ModelProtocolWire.WriteAcceptedCall("Inspect available resources.", "",
                            new ConversationToolCall { Name = "common.resources_find", Arguments = new JObject() }) :
                            ScriptedVerifiedFinish(session, "Report the search.", "Ready.", "Resources inspected.", "common.resources_find"),
                        ReasoningContent = first ? "Accepted tool reasoning." : null
                    });
                });
                var result = service.ExecuteAsync(ChatModes.Agent, "Inspect resources.", session, NewContext(adapter),
                    new AppSettings { SmartChatTitles = false, MaxConsecutiveReflections = 0 },
                    OfficeToolCatalog.ForHost("Excel").Concat(executor.GetControllerTools()).ToArray(), null).GetAwaiter().GetResult();
                AssertEqual("model_finish", result.RunViewState.Reason, "ordinary tool execution completes");
                var reopened = new ChatStore(FixturePaths.Value).Load(session.Id);
                var visible = ChatCloneService.CloneMessagesForBridge(reopened, reopened.Messages);
                AssertEqual(1, visible.Count(message => !message.ProtocolMessage && message.ReasoningContent == "Accepted tool reasoning."),
                    "accepted tool reasoning survives the next step and reopen exactly once");
            });
        }

        private static async Task V8RecoveryStrictTransport()
        {
            var logical = RecoveryFinish(); var wrapped = SchemaTransport(JObject.Parse(logical)).ToString(Formatting.None);
            var tools = new ToolCatalogEntry[0]; var context = new ModelProtocolCallContext(new string[0]);
            AssertTrue(ModelProtocolWire.Parse(wrapped, tools, tools, context, true).Success, "explicit strict transport unwraps once");
            AssertTrue(!ModelProtocolWire.Parse(logical, tools, tools, context, true).Success, "no hidden unwrapped strict fallback");
            AssertTrue(!ModelProtocolWire.Parse(wrapped, tools, tools, context).Success, "logical history cannot accept transport wrappers");
            AssertTrue(!ModelProtocolWire.Parse("{\"response\":" + logical + ",\"extra\":0}", tools, tools, context, true).Success, "closed transport root");
            for (var split = 0; split <= wrapped.Length; split++)
            {
                var extractor = new ConversationMessageStreamExtractor(true);
                AssertEqual("Ready.", extractor.Add(wrapped.Substring(0, split)) + extractor.Add(wrapped.Substring(split)) + extractor.Complete(),
                    "wrapped message projects across every chunk boundary");
            }
            var calls = 0; var request = NewProtocolRequest(2, strict: true);
            request.AcceptedMessages = new[] { new ChatMessage { Role = "system", Content = AgentPromptDefaults.StructuredResponseContract } };
            var protocol = new ModelProtocolClient((settings, messages, options, progress, token) => {
                var prompt = string.Join("\n", messages.Select(item => item.Content));
                AssertContains(prompt, ModelProtocolWire.SchemaGuidance, "strict prompt explains semantics and wrapper");
                AssertTrue(!prompt.Contains("TOOL_NAME"), "strict prompt omits duplicate shape examples");
                return Task.FromResult(new LlmCompletionResult { Content = ++calls == 1 ? logical : wrapped });
            });
            var result = await protocol.GetResponseAsync(request, null, CancellationToken.None);
            AssertTrue(result.Response != null && calls == 2, "strict wrapper can be corrected within the format budget");
            AssertEqual(logical, result.Response.ToJson(), "accepted history uses the canonical inner decision");
        }

        private static async Task V8RecoveryOutputLimit()
        {
            foreach (var streaming in new[] { false, true })
            {
                var payload = streaming
                    ? "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"},\"finish_reason\":\"length\"}]}\n\ndata: [DONE]\n\n"
                    : "{\"choices\":[{\"message\":{\"content\":\"partial\"},\"finish_reason\":\"length\"}]}";
                using (var source = new MemoryStream(Encoding.UTF8.GetBytes(payload)))
                {
                    var parsed = streaming ? await LlmResponseParser.ReadStreamingOrJsonResponseAsync(source, null, CancellationToken.None)
                        : LlmResponseParser.ParseCompletionResponse(payload);
                    AssertEqual(LlmCompletionTermination.OutputLimit, parsed.Termination, "output limit survives transport " + streaming);
                }
            }
            foreach (var recover in new[] { true, false })
            {
                var count = 0;
                var protocol = new ModelProtocolClient((settings, messages, options, progress, token) => {
                    if (count > 0) AssertContains(messages.Last().Content, "OUTPUT_LIMIT", "cause-aware short-response correction");
                    return Task.FromResult(new LlmCompletionResult { Content = RecoveryFinish(),
                        Termination = ++count == 1 || !recover ? LlmCompletionTermination.OutputLimit : LlmCompletionTermination.Completed });
                });
                var result = await protocol.GetResponseAsync(NewProtocolRequest(10), null, CancellationToken.None);
                AssertEqual(2, count, "one bounded output-limit correction, not ten identical generations");
                if (recover) AssertTrue(result.Response != null, "full corrected response accepted");
                else AssertEqual(ModelProtocolFailureKind.OutputTruncated, result.Failure.Kind, "repeated truncation remains a distinct terminal cause");
            }
        }

        private static void V8RecoveryWorkspaceStream()
        {
            foreach (var strict in new[] { false, true })
            WithTempPaths(paths => {
                var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start();
                var port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
                using (var listener = new HttpListener())
                using (var service = new WorkspaceConversationService(paths))
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                {
                    listener.Prefixes.Add("http://127.0.0.1:" + port + "/"); listener.Start();
                    var wireRequests = new List<JObject>();
                    var server = Task.Run(async () => {
                        for (var index = 0; index < 2; index++)
                        {
                            var context = await listener.GetContextAsync();
                            string raw; using (var reader = new StreamReader(context.Request.InputStream)) raw = await reader.ReadToEndAsync();
                            var request = JObject.Parse(raw);
                            wireRequests.Add(request);
                            var prompt = (string)request["messages"][0]["content"];
                            AssertEqual(strict, (string)request.SelectToken("response_format.type") == "json_schema", "selected wire transport");
                            if (strict) {
                                AssertContains(prompt, ModelProtocolWire.SchemaGuidance, "workspace uses concise schema guidance");
                                AssertTrue(!prompt.Contains("TOOL_NAME") && !prompt.Contains("\"Schema\""), "workspace does not repeat format examples or tool schemas");
                                AssertTrue(request.SelectTokens("response_format.json_schema.schema..description").Any(), "argument meaning survives in schema");
                            }
                            var logical = index == 0 ? RecoveryReflection() : RecoveryFinish();
                            var wire = strict ? SchemaTransport(JObject.Parse(logical)).ToString(Formatting.None) : logical;
                            var chunk = new JObject { ["choices"] = new JArray(new JObject {
                                ["delta"] = new JObject { ["content"] = wire, ["reasoning_content"] = "Workspace reasoning." },
                                ["finish_reason"] = "stop" }), ["usage"] = new JObject { ["completion_tokens"] = 25 } };
                            var bytes = Encoding.UTF8.GetBytes("data: " + chunk.ToString(Formatting.None) + "\n\ndata: [DONE]\n\n");
                            context.Response.ContentType = "text/event-stream"; context.Response.ContentLength64 = bytes.Length;
                            await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length); context.Response.Close();
                        }
                    });
                    try {
                        var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "stream"));
                        var session = service.CreateSession(workspace, "Stream"); var previewSeen = false;
                        var settings = new AppSettings { BaseUrl = "http://127.0.0.1:" + port, Model = "fixture",
                            StreamResponses = true, SmartChatTitles = false, AutoCompressContext = false,
                            ContextWindowOverrideTokens = strict ? 19000 : 131072, MaxConsecutiveReflections = 1,
                            AgentResponseMode = strict ? AgentResponseModes.JsonSchema : AgentResponseModes.JsonObject };
                        var result = service.SubmitInputAsync(workspace, new WorkspaceSubmitInputRequest {
                            SessionId = session.Id, Message = "Answer directly.", ReasoningEnabled = true }, settings, () => "fixture-key",
                            progress: item => {
                                if (item.ModelProgress?.Reasoning != "Workspace reasoning.") return;
                                var state = service.ReadState(workspace, session.Id);
                                AssertEqual(item.ModelProgress.AttemptId, state.ModelProgress.AttemptId, "polling gets current scoped preview");
                                AssertEqual(state.RunId, state.ModelProgress.RunId, "preview cannot cross runs");
                                previewSeen = true;
                            }, cancellationToken: timeout.Token).GetAwaiter().GetResult();
                        AssertEqual(RunLifecycle.Completed, result.Summary.Lifecycle,
                            "streamed workspace run completes: " + result.Summary.Reason + " " + result.Summary.AssistantMessage);
                        AssertTrue(previewSeen, "workspace progress sink is connected");
                        AssertTrue(service.ReadState(workspace, session.Id).ModelProgress == null, "terminal state retires transient preview");
                        var stored = service.GetSession(workspace, session.Id).Messages.Where(message => message.ResponseProtocolVersion == 8).ToArray();
                        AssertEqual(2, stored.Length, "reflection and finish retained once");
                        AssertTrue(stored.All(message => message.ReasoningContent == "Workspace reasoning." && message.CompletionTokens == 25),
                            "accepted workspace messages retain reasoning and usage");
                        AssertEqual(RunViewLifecycles.Completed, stored.Last().RunViewState?.Lifecycle,
                            "workspace persists the terminal run outcome on its visible accepted response");
                        server.GetAwaiter().GetResult();
                        if (strict)
                        {
                            var format = wireRequests.Last()["response_format"];
                            var budgetOptions = new LlmRequestOptions {
                                ResponseFormat = (string)format["type"],
                                ResponseSchemaName = (string)format.SelectToken("json_schema.name"),
                                ResponseSchemaJson = format.SelectToken("json_schema.schema").ToString(Formatting.None)
                            };
                            var unprojectedBudget = Math.Max(1, ModelContextBudget.InputBudgetTokens(settings) -
                                ModelContextBudget.EstimateRequestOptionsTokens(budgetOptions, settings) -
                                ModelProtocolClient.EstimateFormatRepairOverheadTokens(settings) -
                                ModelContextBudget.ContinuationReserveTokens(settings));
                            AssertTrue(service.GetSession(workspace, session.Id).LastContextReceipt.EstimatedTokens > unprojectedBudget,
                                "the full frozen prompt exceeds the strict message budget, while its projected wire completed");
                        }
                    } finally { listener.Stop(); }
                }
            });
        }

        private static void V8RecoveryWorkspaceFailureHistory()
        {
            WithTempPaths(paths => {
                var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
                using (var listener = new HttpListener())
                using (var service = new WorkspaceConversationService(paths))
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                {
                    listener.Prefixes.Add("http://127.0.0.1:" + port + "/"); listener.Start();
                    var server = Task.Run(async () => {
                        var context = await listener.GetContextAsync();
                        var bytes = Encoding.UTF8.GetBytes("{\"error\":{\"message\":\"fixture refusal\"}}");
                        context.Response.StatusCode = 401;
                        context.Response.ContentType = "application/json";
                        context.Response.ContentLength64 = bytes.Length;
                        await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                        context.Response.Close();
                    });
                    try {
                        var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "failed-run"));
                        var session = service.CreateSession(workspace, "Failure history");
                        var settings = new AppSettings { BaseUrl = "http://127.0.0.1:" + port,
                            Model = "fixture", StreamResponses = false, SmartChatTitles = false,
                            AutoCompressContext = false, ContextWindowOverrideTokens = 131072,
                            AgentResponseMode = AgentResponseModes.JsonObject };
                        var result = service.SubmitInputAsync(workspace, new WorkspaceSubmitInputRequest {
                            SessionId = session.Id, Message = "Answer directly." }, settings,
                            () => "fixture-key", cancellationToken: timeout.Token).GetAwaiter().GetResult();
                        AssertEqual(RunLifecycle.Failed, result.Summary.Lifecycle,
                            "provider refusal is a failed runtime outcome");
                        var saved = service.GetSession(workspace, session.Id).Messages.Last();
                        AssertEqual("diagnostic", saved.Activity?.Kind,
                            "workspace appends a visible terminal record when no model response was accepted");
                        AssertEqual(RunViewLifecycles.Failed, saved.RunViewState?.Lifecycle,
                            "the terminal message carries the typed run outcome");
                        AssertEqual("diagnostic", PluginApplicationService.PresentWorkspaceMessage(saved).Activity?.Kind,
                            "the shared message view shows the saved failure after reopen");
                        server.GetAwaiter().GetResult();
                    } finally { listener.Stop(); }
                }
            });
        }

        private static void V8RecoveryWorkspacePresentation()
        {
            var message = new ChatMessage { Role = "assistant", ProtocolMessage = true, RunId = "run",
                ResponseProtocolVersion = 8, ResponseModelAttemptId = "attempt", Content = RecoveryReflection(),
                ReasoningContent = "Saved reasoning.", ReasoningTokens = 7 };
            message.RunViewState = new RunViewState("run", "turn", "Saved outcome.",
                RunViewLifecycles.Failed, RunViewHealth.Clean, 0, 0, 0, 0, 0, 0,
                null, "provider", "Saved outcome.", DateTime.UtcNow);
            var view = PluginApplicationService.PresentWorkspaceMessage(message);
            AssertEqual(message.Id, view.Id, "project and plugin receive stable canonical message identity");
            AssertEqual("run", view.RunId, "message belongs to exact run");
            AssertEqual("Saved reasoning.", view.ReasoningContent, "workspace retains accepted reasoning");
            AssertEqual("Read before changing.", view.ReflectionDecision, "reflection survives projection");
            AssertEqual(RunViewLifecycles.Failed, view.RunViewState?.Lifecycle,
                "workspace projection retains the per-message outcome for history paging");
            message.ResponseProtocolVersion = 7;
            view = PluginApplicationService.PresentWorkspaceMessage(message);
            AssertTrue(!view.Content.Contains("\"action\"") && view.Content.Contains("неподдерживаемый"), "old JSON is explicitly skipped without migration");
            AssertEqual(RecoveryReflection(), message.Content, "presentation cannot rewrite historical bytes");
        }
    }
}
