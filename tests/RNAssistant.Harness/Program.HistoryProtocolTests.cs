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
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void HistoryProtocolOptionalMessage()
        {
            var migrated = JsonConvert.DeserializeObject<AppSettings>("{\"ToolResultRole\":\"tool\",\"FallbackToJsonObject\":true}");
            AssertEqual("user", migrated.ToolResultRole, "old native preference migrates explicitly");
            AssertTrue(!migrated.FallbackToJsonObject && !string.IsNullOrEmpty(migrated.ProtocolMigrationNotice), "migration notice is retained");
            var saved = JObject.FromObject(migrated);
            AssertTrue(saved["ToolResultRole"] == null && saved["FallbackToJsonObject"] == null, "retired controls are not serialized");
            var oldJson = new AppSettings { AgentPromptSchemaVersion = 66 };
            oldJson.NormalizeAgentPrompts();
            AssertTrue(!string.IsNullOrEmpty(oldJson.ProtocolMigrationNotice), "old JSON history also announces the new contract");
            var tool = V8ReadTool();
            var context = new ModelProtocolCallContext(new[] { tool.Id });
            var schema = ExpandResponseSchema(JObject.Parse(ConversationResponseSchemaBuilder.Build(new[] { tool }, context)));
            foreach (var action in new[] { "execute", "reflection", "finish" })
            foreach (var variant in new[] { "omitted", "empty", "null", "text" })
            {
                var value = action == "execute" ? JObject.Parse(V8Envelope(V8Call(arguments: new JObject {
                    ["query"] = "A", ["limit"] = JValue.CreateNull(), ["at"] = JValue.CreateNull() }))) :
                    action == "reflection" ? new JObject { ["action"] = action, ["reason"] = "Choose a check.", ["decision"] = "Inspect the observed source." } :
                    JObject.Parse(V8Envelope());
                value.Remove("message");
                if (variant != "omitted") value["message"] = variant == "null" ? JValue.CreateNull() : new JValue(variant == "empty" ? "" : "Ready.");
                var expected = action == "finish" ? variant == "text" : variant != "null";
                string error;
                var parsed = new ConversationResponseParser().Parse(value.ToString(Formatting.None), new[] { tool }, new[] { tool }, context);
                AssertEqual(expected, parsed.Success, action + "/" + variant + " parser");
                AssertEqual(expected, ToolSchemaSupport.ValidateArguments(SchemaTransport(value), schema, false, out error), action + "/" + variant + " schema: " + error);
                if (expected) AssertEqual(variant != "omitted", JObject.Parse(parsed.Response.ToJson()).Property("message") != null, "presence survives accepted serialization");
            }
        }

        private static async Task HistoryProtocolStopsIdenticalRepair()
        {
            AssertEqual(3, new AppSettings().MaxAgentFormatRetries, "three received responses by default");
            var calls = 0;
            var client = new ModelProtocolClient((settings, messages, options, progress, token) => {
                calls++;
                if (calls == 2)
                {
                    var repair = JObject.Parse(messages.Last().Content);
                    AssertEqual("format_repair", (string)repair["type"], "typed retry context");
                    AssertEqual(false, (bool)repair["dispatched"], "rejection did not execute");
                }
                return Task.FromResult(new LlmCompletionResult { Content = "{\"action\":\"execute\",\"reason\":\"Read.\"}" });
            });
            var result = await client.GetResponseAsync(NewProtocolRequest(10), null, CancellationToken.None);
            AssertEqual(2, calls, "two identical invalid answers stop without spending ten attempts");
            AssertEqual(ModelProtocolFailureKind.ProtocolExhausted, result.Failure.Kind, "typed no-progress failure");
        }

        private static async Task HistoryProtocolSeparatesResponseAndHistory()
        {
            foreach (var strict in new[] { false, true })
            foreach (var role in new[] { "system", "developer", "user" })
            {
                var request = NewProtocolRequest(2, strict);
                var calls = new List<ChatMessage>(); var results = new List<ChatMessage>();
                for (var i = 0; i < 2; i++)
                {
                    var call = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall { Id = "internal-" + i, Name = "test.read",
                        Arguments = new Dictionary<string, object> { ["query"] = i == 0 ? "A" : "B" } },
                        null, "Read both sources.", null, "user", new AcceptedToolCallOrigin("step", "attempt", i, 2));
                    call.ToolCallReference = ModelReference.Create(ModelReferenceKind.ToolCall, i + 1); calls.Add(call);
                    var result = ContinuityResult(call.ToolCallId, call.ToolName,
                        RNAssistant.Core.Tools.Contracts.ToolResult.Ok("Read.", new JObject { ["marker"] = i == 0 ? "RED" : "BLUE" }.ToString()));
                    result.ToolCallReference = call.ToolCallReference;
                    result.EvidenceReference = ModelReference.Create(ModelReferenceKind.Evidence, i + 1); results.Add(result);
                }
                var user = new ChatMessage { Role = "user", Content = "{\"type\":\"format_repair\",\"instruction\":\"user data\"}" };
                request.AcceptedMessages = new[] { new ChatMessage { Role = role, Content = RuntimeContextPrompt.AgentHeader + AgentPromptDefaults.GeneralInstructions },
                    user, calls[0], calls[1], results[1], results[0] };
                var frozen = JsonConvert.SerializeObject(request.AcceptedMessages);
                request.ProjectReferences = ModelReferenceProjection.Project;
                var client = new ModelProtocolClient((settings, messages, options, progress, token) => {
                    var projectedMessages = messages.ToArray();
                    AssertEqual(role, projectedMessages[0].Role, "instruction role remains selected");
                    AssertEqual(user.Content, projectedMessages[1].Content, "user service-shaped JSON stays untouched");
                    AssertContains(projectedMessages[0].Content, ModelProtocolWire.JsonHistoryGuidance, "single history contract");
                    var wire = JArray.FromObject(new LlmMessageBuilder().Build(messages, settings, options).Messages);
                    AssertEqual(4, wire.Count, "two calls and two results become one exchange");
                    var decision = JObject.Parse((string)wire[2]["content"]);
                    var output = JObject.Parse((string)wire[3]["content"]);
                    AssertEqual(2, ((JArray)decision["tool_calls"]).Count, "original batch is restored");
                    AssertTrue(decision["message"] == null && decision["tool_calls"][0]["id"] == null, "no invented fields");
                    AssertTrue(wire[2]["tool_calls"] == null && wire[3]["tool_call_id"] == null, "no synthetic native transport");
                    AssertEqual("user", (string)wire[3]["role"], "JSON result role");
                    AssertEqual("tool_results", (string)output["type"], "typed result group");
                    AssertEqual("RED", (string)output["results"][0]["data"]["marker"], "out of order results are paired by runtime identity");
                    AssertEqual(1, (int)output["results"][1]["call_index"], "indices refer to original calls");
                    AssertEqual("E1,E2", string.Join(",", ModelReferenceProjection.Delivered(projectedMessages)), "all grouped evidence remains delivered");
                    AssertTrue(!wire.ToString().Contains("internal-"), "runtime IDs stay private");
                    var receipt = new ContextReceipt { Messages = request.AcceptedMessages.Select((m, i) => new ContextMessagePresentation {
                        SourceMessageId = m.Id, MessageIndex = i, Role = m.Role, ToolCallId = m.ToolCallId, EvidenceReference = m.EvidenceReference, Presentation = ContextPresentationKind.Full }).ToList() };
                    var attributed = LlmClient.DescribeWireMessages(projectedMessages, receipt, null, null, wire, settings);
                    AssertEqual(2, attributed[2].Parts.Count, "grouped decision keeps both journal links");
                    AssertEqual(2, attributed[3].Parts.Count, "grouped results keep both journal links");
                    var savedTrace = JsonConvert.DeserializeObject<List<ContextMessagePresentation>>(JsonConvert.SerializeObject(attributed));
                    var terminalSources = ContextMessagePresentation.Sources(savedTrace).Where(m => m.EvidenceReference != null).ToArray();
                    AssertEqual("internal-0,internal-1", string.Join(",", terminalSources.Select(m => m.ToolCallId)), "Inspector preserves each operation after trace round-trip");
                    AssertTrue(terminalSources.All(m => m.Presentation == ContextPresentationKind.Summary), "delivered E references remain citable without claiming exact source bytes");
                    AssertTrue(ModelReferenceProjection.Delivered(ModelReferenceProjection.Project(projectedMessages)).SequenceEqual(new[] { "E1", "E2" }), "projection is idempotent");
                    var finish = ModelProtocolWire.WriteAcceptedDecision("finish", "Answer ready.", "Done.",
                        results: new ConversationResultClaim[0], outcome: ConversationOutcome.Completed);
                    return Task.FromResult(new LlmCompletionResult { Content = strict ? "{\"response\":" + finish + "}" : finish });
                });
                var response = await client.GetResponseAsync(request, null, CancellationToken.None);
                AssertTrue(response.Failure == null, "JSON batch request: " + response.Failure?.Message);
                AssertEqual(frozen, JsonConvert.SerializeObject(request.AcceptedMessages), "canonical history is immutable");
                var partial = ModelReferenceProjection.Project(new[] { calls[1], results[1] });
                AssertEqual("completed_operation", (string)JObject.Parse(partial.Single().Content)["type"], "compacted sibling is an explicit receipt");
            }
        }

        private static async Task HistoryProtocolRequiresNewExecuteCalls()
        {
            foreach (var invalidShape in new[] { "missing", "string_call", "string_arguments", "native_wrapper" })
            foreach (var strict in new[] { false, true })
            foreach (var nativeReply in new[] { false, true })
            foreach (var streaming in new[] { false, true })
            {
                var request = NewProtocolRequest(2, strict);
                request.Settings.ToolResultRole = ToolResultRoles.Tool;
                request.CallableTools = request.RunnableCatalog = new[] { V8ReadTool() };
                request.CallContext = new ModelProtocolCallContext(new[] { "test.read" });
                request.Options = ModelProtocolWire.CreateRequestOptions(request.Settings.AgentResponseMode, request.CallableTools, request.CallContext);
                var trace = new List<LlmTraceRecord>(); request.Options.TraceSink = trace.Add;
                var call = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall { Id = "accepted-call", Name = "test.read" },
                    "Read.", "Inspect.", null, ToolResultRoles.Tool, FixtureCallOrigin());
                call.ToolCallReference = ModelReference.Create(ModelReferenceKind.ToolCall, 1);
                var result = ContinuityResult(call.ToolCallId, call.ToolName, RNAssistant.Core.Tools.Contracts.ToolResult.Ok("Read."), ToolResultRoles.Tool);
                result.ToolCallReference = call.ToolCallReference; result.EvidenceReference = ModelReference.Create(ModelReferenceKind.Evidence, 1);
                request.AcceptedMessages = new[] { new ChatMessage { Role = "system", Content = AgentPromptDefaults.GeneralInstructions },
                    new ChatMessage { Role = "user", Content = "Continue the requested repair." }, call, result };
                var frozen = JsonConvert.SerializeObject(request.AcceptedMessages);
                request.ProjectReferences = ModelReferenceProjection.Project;
                request.CompileRepair = notice => request.AcceptedMessages.Concat(new[] { notice }).ToArray();
                var attempts = 0;
                string rejectedContent = null;
                var client = new ModelProtocolClient(async (settings, messages, options, progress, cancellation) => {
                    attempts++;
                    AssertContains(messages.First().Content, ModelProtocolWire.ExecuteGuidance, "both formats state the fresh-response rule");
                    var historical = messages.Single(candidate => candidate.Role == "assistant");
                    AssertTrue(JObject.Parse(historical.Content)["tool_calls"] != null && historical.ToolCalls.Count == 0,
                        "history uses the same execute shape on every attempt");
                    if (attempts == 2)
                    {
                        var repair = JObject.Parse(messages.Last().Content);
                        AssertEqual(rejectedContent, (string)repair["rejected_response"]["content"], "repair quotes the rejected content before any native history projection");
                        AssertEqual(nativeReply, (bool)repair["rejected_response"]["native_tool_calls_observed"], "repair identifies separate native generation without executing it");
                        AssertEqual(invalidShape == "missing" ? "empty_execute_calls" : "invalid_tool_call", (string)repair["code"], "malformed execute is rejected before acceptance");
                        AssertContains((string)repair["instruction"], "supply the intended next calls inside the new decision JSON", "repair retains the fresh-call rule");
                        AssertEqual(nativeReply && invalidShape == "missing", ((string)repair["error"]).Contains("Provider-native tool_calls were observed"),
                            "only observed native generation gets the separate-field explanation");
                        if (invalidShape != "missing") AssertContains((string)repair["error"],
                            invalidShape == "string_call" ? "tool_calls[0] must be an object with name and arguments; received string" :
                            invalidShape == "string_arguments" ? "tool_calls[0].arguments must be an object; received string" :
                            "tool_calls[0] has unsupported fields \"id\", \"type\", \"function\"", "repair identifies the actual incoming shape");
                    }
                    var content = attempts == 1
                        ? ModelProtocolWire.WriteNativeHistoryDecision("Исправляю найденные баги в VBA-модулях.", "Применяю исправления к Module1 и SceneParser.")
                        : V8Envelope(V8Call(arguments: new JObject { ["query"] = "next" }));
                    if (attempts == 1 && invalidShape != "missing")
                    {
                        var envelope = JObject.Parse(V8Envelope(V8Call()));
                        var calls = (JArray)envelope["tool_calls"];
                        if (invalidShape == "string_call") calls[0] = calls[0].ToString(Formatting.None);
                        else if (invalidShape == "string_arguments") calls[0]["arguments"] = calls[0]["arguments"].ToString(Formatting.None);
                        else calls[0] = new JObject { ["id"] = "copied-id", ["type"] = "function",
                            ["function"] = new JObject { ["name"] = "test.read", ["arguments"] = "{\"query\":\"A\"}" } };
                        content = envelope.ToString(Formatting.None);
                    }
                    if (strict) content = "{\"response\":" + content + "}";
                    if (attempts == 1) rejectedContent = content;
                    var message = new JObject { ["role"] = "assistant", ["content"] = content };
                    if (nativeReply && attempts == 1) message["tool_calls"] = new JArray(new JObject {
                        ["index"] = 0, ["id"] = "provider-id", ["type"] = "function",
                        ["function"] = new JObject { ["name"] = "test.read", ["arguments"] = "{\"query\":\"native-only\"}" } });
                    var payload = new JObject { ["choices"] = new JArray(new JObject {
                        [streaming ? "delta" : "message"] = message, ["finish_reason"] = "stop" }) }.ToString(Formatting.None);
                    LlmCompletionResult completion;
                    if (streaming)
                    {
                        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes("data: " + payload + "\n\ndata: [DONE]\n\n")))
                            completion = await LlmResponseParser.ReadStreamingOrJsonResponseAsync(stream, null, cancellation, null);
                    }
                    else completion = LlmResponseParser.ParseCompletionResponse(payload);
                    AssertEqual(content, completion.Content, "HTTP JSON/SSE decoding preserves content exactly before admission");
                    AssertEqual(nativeReply && attempts == 1, completion.NativeToolCallsObserved, "JSON/SSE retain native presence without assembling calls");
                    AssertTrue(completion.NativeToolCalls == null, "native-call assembly stays diagnostic-only");
                    return completion;
                });
                var accepted = await client.GetResponseAsync(request, null, CancellationToken.None);
                AssertTrue(accepted.Failure == null && accepted.Response != null && attempts == 2,
                    "complete execute is accepted after one repair (" + invalidShape + ", strict=" + strict + ", native=" + nativeReply +
                    ", streaming=" + streaming + "): " + accepted.Failure?.Message);
                AssertEqual("next", (string)accepted.Response.ToolCalls.Single().Arguments["query"], "neither historical nor provider-native calls are executed as the new proposal");
                AssertEqual(1, trace.Count(item => item.Type == "accepted"), "only the corrected JSON is accepted");
                AssertEqual(rejectedContent, trace.Single(item => item.Type == "rejected").PayloadJson, "trace preserves the raw rejected content, including encoded calls");
                AssertEqual(frozen, JsonConvert.SerializeObject(request.AcceptedMessages), "repair never rewrites accepted history");
            }
        }

        private static async Task HistoryProtocolRepairPreservesReflectionDecision()
        {
            foreach (var strict in new[] { false, true })
            foreach (var role in new[] { ToolResultRoles.User, ToolResultRoles.Developer, ToolResultRoles.Tool })
            {
                var request = NewProtocolRequest(2, strict);
                request.Settings.ToolResultRole = role;
                var call = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall { Id = "accepted-read", Name = "test.read" },
                    "", "Read Module1 source.", null, role, FixtureCallOrigin());
                var result = ContinuityResult(call.ToolCallId, call.ToolName,
                    RNAssistant.Core.Tools.Contracts.ToolResult.Ok("Source read.", "{\"text\":\"Sub TestScene3DRun1()\\nEnd Sub\"}"), role);
                request.AcceptedMessages = new[] { new ChatMessage { Role = "system", Content = AgentPromptDefaults.GeneralInstructions },
                    new ChatMessage { Role = "user", Content = "Run the checks in Module1." }, call, result };
                var frozen = JsonConvert.SerializeObject(request.AcceptedMessages);
                request.CompileRepair = notice => request.AcceptedMessages.Concat(new[] { notice }).ToArray();
                var decision = new JObject { ["action"] = "reflection", ["reason"] = "Identify which checks to run on Module1.",
                    ["decision"] = "Load the macro tool, then run TestScene3DRun1." };
                var corrected = (JObject)decision.DeepClone();
                decision["tool_calls"] = new JArray();
                var rejected = (strict ? new JObject { ["response"] = decision.DeepClone() } : decision).ToString(Formatting.None);
                var attempts = 0;
                var client = new ModelProtocolClient((settings, messages, options, progress, cancellation) => {
                    attempts++;
                    if (attempts == 2)
                    {
                        var repair = JObject.Parse(messages.Last().Content);
                        AssertEqual("wrong_action_fields", (string)repair["code"], "reflection rejects execute-only fields");
                        AssertEqual(rejected, (string)repair["rejected_response"]["content"], "reason and decision survive only in the rejected diagnostic");
                        AssertContains((string)repair["error"], "forbids root field tool_calls", "precise action-specific error");
                        AssertContains((string)repair["instruction"], "not to the preceding accepted assistant/tool exchange", "repair cannot refer to the successful read");
                        AssertContains((string)repair["instruction"], "Do not change action", "repair preserves reflection instead of inviting empty execute");
                        AssertEqual(result.Content, messages.ElementAt(messages.Count() - 2).Content, "preceding read data remains intact");
                    }
                    AssertTrue(messages.All(message => string.IsNullOrEmpty(message.ReasoningContent)), "provider reasoning is not model history");
                    return Task.FromResult(new LlmCompletionResult { Content = attempts == 1 ? rejected :
                        (strict ? new JObject { ["response"] = corrected } : corrected).ToString(Formatting.None),
                        ReasoningContent = "PRIVATE_PROVIDER_REASONING" });
                });
                var accepted = await client.GetResponseAsync(request, null, CancellationToken.None);
                AssertTrue(accepted.Failure == null && attempts == 2, "missing message repairs without executing another read: " + accepted.Failure?.Message);
                AssertEqual("reflection", accepted.Response.Action, "reflection remains reflection");
                AssertEqual((string)decision["decision"], accepted.Response.Decision, "chosen next step is retained");
                AssertEqual<string>(null, accepted.Response.Message, "absent nonfinal message stays absent");
                AssertEqual(frozen, JsonConvert.SerializeObject(request.AcceptedMessages), "repair never adds the rejected draft to accepted history");
            }
        }

        private static async Task HistoryProtocolSurvivesRepairAndFallback()
        {
            foreach (var strict in new[] { false, true })
            {
                var request = NewProtocolRequest(2, strict); var attempts = 0;
                var rejected = "{\"unrecognized\":\"quoted diagnostic\"}";
                var client = new ModelProtocolClient((settings, messages, options, progress, token) => {
                    attempts++;
                    AssertEqual(strict ? "json_schema" : "json_object", options.ResponseFormat, "repair keeps the selected transport");
                    if (attempts == 2)
                    {
                        var repair = JObject.Parse(messages.Last().Content);
                        AssertEqual("format_repair", (string)repair["type"], "typed repair");
                        AssertEqual(rejected, (string)repair["rejected_response"]["content"], "exact rejected response");
                        AssertEqual(false, (bool)repair["dispatched"], "no actions from rejection");
                    }
                    var finish = ModelProtocolWire.WriteAcceptedDecision("finish", "Answer ready.", "Done.",
                        results: new ConversationResultClaim[0], outcome: ConversationOutcome.Completed);
                    return Task.FromResult(new LlmCompletionResult { Content = attempts == 1 ? rejected :
                        strict ? "{\"response\":" + finish + "}" : finish });
                });
                var result = await client.GetResponseAsync(request, null, CancellationToken.None);
                AssertTrue(result.Failure == null && attempts == 2, "same-profile repair succeeds: " + result.Failure?.Message);
            }
        }

        private static void HistoryProtocolSettingsApplyWithoutRestart()
        {
            WithTempPaths(paths =>
            {
                var storage = new SettingsService(paths);
                storage.Save(new AppSettings());
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths),
                    new SkillStore(paths), new ToolStore(paths), storage.Load, storage.Save, paths);
                var session = NewSession(adapter);
                AppSettings expected = null;
                var requests = 0;
                var run = CreateConversationRunService(adapter, executor, (settings, messages, options, progress, token) =>
                {
                    requests++;
                    AssertEqual(expected.AgentResponseMode, options.ResponseFormat, "next run uses the saved response format without a new runtime");
                    AssertEqual(expected.ToolResultRole, settings.ToolResultRole, "next run uses the saved history setting");
                    AssertEqual(expected.SystemPromptRole, messages.First().Role, "published instruction role changes immediately");
                    var native = expected.ToolResultRole == ToolResultRoles.Tool;
                    AssertContains(messages.First().Content, native ? ModelProtocolWire.JsonHistoryGuidance : ModelProtocolWire.JsonHistoryGuidance,
                        "next run has matching instructions");
                    var wire = JArray.FromObject(new LlmMessageBuilder().Build(messages, settings, options).Messages);
                    if (wire.Any(message => message["tool_calls"] != null) || requests > 1)
                    {
                        AssertEqual(native, wire.Any(message => message["tool_calls"] != null), "existing calls use the newly selected history representation");
                        AssertEqual(native, wire.Any(message => (string)message["role"] == "tool"), "existing results use the newly selected role");
                        if (!native) AssertTrue(wire.Where(message => ((string)message["content"] ?? "").StartsWith("TOOL_RESULT:"))
                            .All(message => (string)message["role"] == expected.ToolResultRole), "JSON results use the current selected role");
                    }
                    return Task.FromResult(new LlmCompletionResult { Content = requests == 1
                        ? ModelProtocolWire.WriteAcceptedCall("Find available capabilities.", string.Empty,
                            new ConversationToolCall { Name = "core.tools.search", Arguments = new JObject { ["query"] = "CSS" } })
                        : ModelProtocolWire.WriteAcceptedDecision(ConversationResponse.FinishAction, "Answer ready.", "Use border-radius: 8px;",
                            results: new ConversationResultClaim[0], outcome: ConversationOutcome.Completed) });
                });
                foreach (var role in new[] { ToolResultRoles.User, ToolResultRoles.Tool, ToolResultRoles.Developer, ToolResultRoles.Tool })
                {
                    expected = storage.Load();
                    expected.ToolResultRole = role;
                    expected.AgentResponseMode = role == ToolResultRoles.Tool ? AgentResponseModes.JsonSchema : AgentResponseModes.JsonObject;
                    expected.SystemPromptRole = role == ToolResultRoles.Tool ? "developer" : "system";
                    executor.SaveSettingsControls(storage.Load(), new SaveSettingsPayload {
                        ChatId = session.Id, Settings = SettingsControlsDto.From(expected), ExpectedPromptPublication = executor.GetPromptLibrary().Publication
                    }, null, storage.Save);
                    requests = 0;
                    var result = run.ExecuteAsync(ChatModes.Agent, "Найди инструменты CSS и предложи скругление button.",
                        session, NewContext(adapter), storage.Load(), OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(), null)
                        .GetAwaiter().GetResult();
                    AssertEqual(2, requests, "each run has one call and its answer, without stale-format repair (" + role + "): " + result.AssistantText);
                    AssertEqual("Use border-radius: 8px;", result.AssistantText, "the same live chat completes with each saved configuration");
                }
            });
        }
    }
}
