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
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static async Task HistoryProtocolSeparatesResponseAndHistory()
        {
            foreach (var mode in new[] { AgentResponseModes.JsonObject, AgentResponseModes.JsonSchema })
            foreach (var role in new[] { ToolResultRoles.User, ToolResultRoles.Developer, ToolResultRoles.Tool })
            foreach (var instructionRole in new[] { "developer", "system", "user" })
            foreach (var reflectionEnabled in new[] { false, true })
            {
                var request = NewProtocolRequest(1, mode == AgentResponseModes.JsonSchema);
                request.Settings.ToolResultRole = role;
                request.Settings.SystemPromptRole = instructionRole;
                request.Settings.MaxConsecutiveReflections = reflectionEnabled ? 2 : 0;
                request.CallContext = new ModelProtocolCallContext(new string[0], reflectionEnabled: reflectionEnabled);
                request.Options = ModelProtocolWire.CreateRequestOptions(mode, request.CallableTools, request.CallContext);
                var call = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall { Id = "runtime-call", Name = "test.read" },
                    "Read.", "Inspect.", null, role, FixtureCallOrigin());
                call.ToolCallReference = ModelReference.Create(ModelReferenceKind.ToolCall, 1);
                var result = ContinuityResult(call.ToolCallId, call.ToolName, RNAssistant.Core.Tools.Contracts.ToolResult.Ok("Read."), role);
                result.ToolCallReference = call.ToolCallReference;
                result.EvidenceReference = ModelReference.Create(ModelReferenceKind.Evidence, 1);
                var reflection = ModelProtocolWire.WriteAcceptedDecision(ConversationResponse.ReflectionAction,
                    "The read returned the requested source.", "Reviewing the result.", decision: "Report the observed source.");
                var finish = ModelProtocolWire.WriteAcceptedDecision(ConversationResponse.FinishAction,
                    "The read is confirmed by E1.", "Source inspected.",
                    results: new[] { new ConversationResultClaim("Inspect source.", new[] { "E1" }, ConversationAssessment.Satisfied) },
                    outcome: ConversationOutcome.Completed);
                request.AcceptedMessages = new[] {
                    new ChatMessage { Role = instructionRole, Content = RuntimeContextPrompt.AgentHeader + AgentPromptDefaults.GeneralInstructions },
                    new ChatMessage { Role = "user", Content = ModelProtocolWire.JsonHistoryGuidance }, call, result,
                    new ChatMessage { Role = "assistant", Content = reflection, ProtocolMessage = true,
                        ResponseProtocolVersion = ConversationResponse.ProtocolVersion, ResponseModelAttemptId = "reflection-attempt" },
                    new ChatMessage { Role = "assistant", Content = finish, ProtocolMessage = true,
                        ResponseProtocolVersion = ConversationResponse.ProtocolVersion, ResponseModelAttemptId = "finish-attempt" },
                    new ChatMessage { Role = "user", Content = "Continue from the previous result." } };
                var source = request.AcceptedMessages;
                var stored = JsonConvert.SerializeObject(source);
                var authority = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new ResourceAuthoritySnapshot[0]),
                    "pack", new SkillCatalogSnapshot(null), null, 1);
                request.AcceptedMessages = new ModelContextCompiler().Compile(authority,
                    new[] { source[0] }, source.Skip(1).ToArray(), null, new RNAssistant.Core.Tools.ToolCatalogEntry[0],
                    request.Settings, 16000).Messages;
                AssertEqual(stored, JsonConvert.SerializeObject(source), "compilation never rewrites stored decisions");
                var frozen = JsonConvert.SerializeObject(request.AcceptedMessages);
                request.ProjectReferences = ModelReferenceProjection.Project;
                var client = new ModelProtocolClient((settings, messages, options, progress, cancellation) => {
                    var instruction = messages.First().Content;
                    var native = role == ToolResultRoles.Tool;
                    AssertContains(instruction, native ? ModelProtocolWire.NativeHistoryGuidance : ModelProtocolWire.JsonHistoryGuidance,
                        "history guidance follows the selected history representation");
                    AssertEqual(!native, instruction.Contains("TOOL_RESULT"), "native instructions do not describe fallback envelopes");
                    AssertEqual(!native, instruction.Contains("runtime-added id"), "native instructions omit fallback-only id decoration");
                    AssertContains(instruction, mode == AgentResponseModes.JsonSchema ? "response property" : "JSON object directly",
                        "the next content response keeps its own selected contract");
                    AssertContains(instruction, ModelProtocolWire.ReasonGuidance, "short reason guidance reaches both response formats");
                    AssertContains(instruction, ModelProtocolWire.ExecuteGuidance, "new execute always requires calls inside content, independently of history");
                    AssertEqual(instructionRole, messages.First().Role, "selected instruction role survives projection");
                    AssertEqual(reflectionEnabled, instruction.Contains(ModelProtocolWire.DecisionGuidance), "reflection instructions follow the allowance");
                    AssertEqual(!reflectionEnabled, instruction.Contains("Reflection is disabled for this run"), "disabled reflection cannot be requested");
                    AssertContains(instruction, ModelProtocolWire.CriterionGuidance, "result criteria do not repeat reports");
                    if (mode == AgentResponseModes.JsonSchema)
                    {
                        var schema = JObject.Parse(options.ResponseSchemaJson);
                        AssertTrue(schema.SelectTokens("$..reason.description").All(item => (string)item == ModelProtocolWire.ReasonGuidance),
                            "schema and prompt share the reason rule");
                        AssertEqual(reflectionEnabled ? ModelProtocolWire.DecisionGuidance : null,
                            (string)schema.SelectTokens("$..decision.description").SingleOrDefault(), "schema follows the reflection allowance");
                        AssertEqual(ModelProtocolWire.CriterionGuidance, (string)schema.SelectTokens("$..criterion.description").Single(),
                            "schema describes concise result criteria");
                    }
                    AssertEqual(ModelProtocolWire.JsonHistoryGuidance, messages.ElementAt(1).Content, "user content is never instruction-projected");
                    AssertEqual(mode, options.ResponseFormat, "native history cannot change output response_format");
                    var wire = JArray.FromObject(new LlmMessageBuilder().Build(messages, settings, options).Messages);
                    AssertTrue(JToken.DeepEquals(JObject.Parse(reflection), JObject.Parse((string)wire[4]["content"])),
                        "compiled reflection preserves action, reason, decision and message");
                    AssertTrue(JToken.DeepEquals(JObject.Parse(finish), JObject.Parse((string)wire[5]["content"])),
                        "compiled prior-run finish preserves outcome and evidence assessments");
                    AssertEqual(role, (string)wire[3]["role"], "selected result role survives actual message serialization");
                    if (native)
                    {
                        AssertEqual("T1", (string)wire[2]["tool_calls"][0]["id"], "native accepted call uses runtime alias");
                        AssertEqual("T1", (string)wire[3]["tool_call_id"], "native result is paired");
                        var decision = JObject.Parse((string)wire[2]["content"]);
                        AssertEqual("execute", (string)decision["action"], "native history retains the chosen action");
                        AssertEqual("Inspect.", (string)decision["reason"], "native history retains the decision reason");
                        AssertEqual("Read.", (string)decision["message"], "native history retains narration");
                        AssertTrue(decision["tool_calls"] == null && decision.Count == 3, "calls occur only in the native field");
                    }
                    else
                    {
                        AssertTrue(wire[2]["tool_calls"] == null, "JSON history uses assistant content");
                        AssertContains((string)wire[3]["content"], "TOOL_RESULT:", "fallback result remains identified");
                    }
                    var response = ModelProtocolWire.WriteAcceptedDecision(ConversationResponse.FinishAction, "Report read.", "Read verified.",
                        results: new[] { new ConversationResultClaim("Read the source.", new[] { "E1" }, ConversationAssessment.Satisfied) },
                        outcome: ConversationOutcome.Completed);
                    return Task.FromResult(new LlmCompletionResult { Content = mode == AgentResponseModes.JsonSchema
                        ? "{\"response\":" + response + "}" : response });
                });
                var completion = await client.GetResponseAsync(request, null, CancellationToken.None);
                AssertTrue(completion.Response != null && completion.Failure == null, "content JSON is accepted with every history transport");
                AssertEqual(frozen, JsonConvert.SerializeObject(request.AcceptedMessages), "projection preserves the frozen source for fallback");
            }
        }

        private static async Task HistoryProtocolRequiresNewExecuteCalls()
        {
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
                var client = new ModelProtocolClient(async (settings, messages, options, progress, cancellation) => {
                    attempts++;
                    AssertContains(messages.First().Content, ModelProtocolWire.ExecuteGuidance, "both formats state the fresh-response rule");
                    AssertContains(messages.First().Content, "not a valid template for a new execute response", "native history is explicitly historical");
                    var historical = messages.Single(candidate => candidate.ToolCalls?.Count > 0);
                    AssertTrue(JObject.Parse(historical.Content)["tool_calls"] == null && historical.ToolCalls[0].Id == "T1",
                        "history still separates decision content and native calls on every attempt");
                    if (attempts == 2)
                    {
                        var repair = JObject.Parse(messages.Last().Content.Substring("FORMAT_REPAIR:\n".Length));
                        AssertEqual("empty_execute_calls", (string)repair["code"], "photo-shaped execute is rejected before acceptance");
                        AssertContains((string)repair["instruction"], "supply the intended next calls inside the new decision JSON", "repair explains the missing field");
                        AssertEqual(nativeReply, ((string)repair["error"]).Contains("Provider-native tool_calls were observed"),
                            "only observed native generation gets the separate-field explanation");
                    }
                    var content = attempts == 1
                        ? ModelProtocolWire.WriteNativeHistoryDecision("Исправляю найденные баги в VBA-модулях.", "Применяю исправления к Module1 и SceneParser.")
                        : V8Envelope(V8Call(arguments: new JObject { ["query"] = "next" }));
                    if (strict) content = "{\"response\":" + content + "}";
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
                    AssertEqual(nativeReply && attempts == 1, completion.NativeToolCallsObserved, "JSON/SSE retain native presence without assembling calls");
                    AssertTrue(completion.NativeToolCalls == null, "native-call assembly stays diagnostic-only");
                    return completion;
                });
                var accepted = await client.GetResponseAsync(request, null, CancellationToken.None);
                AssertTrue(accepted.Failure == null && accepted.Response != null && attempts == 2, "complete execute is accepted after one repair");
                AssertEqual("next", (string)accepted.Response.ToolCalls.Single().Arguments["query"], "neither historical nor provider-native calls are executed as the new proposal");
                AssertEqual(1, trace.Count(item => item.Type == "accepted"), "only the corrected JSON is accepted");
                AssertEqual(frozen, JsonConvert.SerializeObject(request.AcceptedMessages), "repair never rewrites accepted history");
            }
        }

        private static async Task HistoryProtocolSurvivesRepairAndFallback()
        {
            foreach (var role in new[] { ToolResultRoles.User, ToolResultRoles.Developer, ToolResultRoles.Tool })
            foreach (var instructionRole in new[] { "developer", "system", "user" })
            foreach (var reflectionEnabled in new[] { false, true })
            {
                var request = NewProtocolRequest(2, true);
                request.Settings.ToolResultRole = role;
                request.Settings.SystemPromptRole = instructionRole;
                request.CallContext = new ModelProtocolCallContext(new string[0], reflectionEnabled: reflectionEnabled);
                request.Options = ModelProtocolWire.CreateRequestOptions(request.Settings.AgentResponseMode, request.CallableTools, request.CallContext);
                request.AcceptedMessages = new[] { new ChatMessage { Role = instructionRole,
                    Content = RuntimeContextPrompt.AgentHeader + AgentPromptDefaults.GeneralInstructions } };
                var frozen = request.AcceptedMessages;
                request.CompileRepair = notice => frozen.Concat(new[] { notice }).ToArray();
                var attempts = 0;
                var client = new ModelProtocolClient((settings, messages, options, progress, cancellation) => {
                    attempts++;
                    var strict = options.ResponseFormat == LlmResponseFormats.JsonSchema;
                    AssertEqual(attempts < 3, strict, "only explicit schema refusal changes the response transport");
                    AssertEqual(instructionRole, messages.First().Role, "repair and fallback retain the instruction role");
                    AssertContains(messages.First().Content, role == ToolResultRoles.Tool
                        ? ModelProtocolWire.NativeHistoryGuidance : ModelProtocolWire.JsonHistoryGuidance, "repair/fallback preserve history instructions");
                    AssertEqual(role != ToolResultRoles.Tool, messages.First().Content.Contains("TOOL_RESULT"), "fallback changes output schema, not history transport");
                    AssertContains(messages.First().Content, ModelProtocolWire.ResponseGuidance(strict, reflectionEnabled), "owned contract follows the active response format");
                    if (attempts > 1)
                    {
                        var repair = JObject.Parse(messages.Last().Content.Substring("FORMAT_REPAIR:\n".Length));
                        AssertContains((string)repair["instruction"], ModelProtocolWire.ResponseGuidance(strict, reflectionEnabled),
                            "repair follows response format and reflection allowance too");
                    }
                    if (attempts == 2) throw new LlmRequestException(LlmFailureKind.ResponseFormatUnsupported, "synthetic schema refusal");
                    return Task.FromResult(new LlmCompletionResult { Content = attempts == 1 ? "invalid" :
                        ModelProtocolWire.WriteAcceptedDecision(ConversationResponse.FinishAction, "Answer.", "Done.",
                            results: new ConversationResultClaim[0], outcome: ConversationOutcome.Completed) });
                });
                var completion = await client.GetResponseAsync(request, null, CancellationToken.None);
                AssertTrue(completion.Response != null && attempts == 3, "invalid content and explicit format refusal stay bounded");
                AssertEqual(RuntimeContextPrompt.AgentHeader + AgentPromptDefaults.GeneralInstructions, frozen[0].Content,
                    "wire projection does not overwrite accepted instructions");
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
                    AssertContains(messages.First().Content, native ? ModelProtocolWire.NativeHistoryGuidance : ModelProtocolWire.JsonHistoryGuidance,
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
                    AssertEqual(2, requests, "each run has one call and its answer, without stale-format repair");
                    AssertEqual("Use border-radius: 8px;", result.AssistantText, "the same live chat completes with each saved configuration");
                }
            });
        }
    }
}
