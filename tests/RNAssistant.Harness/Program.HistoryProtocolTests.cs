using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static async Task HistoryProtocolSeparatesResponseAndHistory()
        {
            foreach (var mode in new[] { AgentResponseModes.JsonObject, AgentResponseModes.JsonSchema })
            foreach (var role in new[] { ToolResultRoles.User, ToolResultRoles.Developer, ToolResultRoles.Tool })
            {
                var request = NewProtocolRequest(1, mode == AgentResponseModes.JsonSchema);
                request.Settings.ToolResultRole = role;
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
                    new ChatMessage { Role = "developer", Content = RuntimeContextPrompt.AgentHeader + AgentPromptDefaults.GeneralInstructions },
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
                    AssertContains(instruction, ModelProtocolWire.DecisionGuidance, "reflection guidance is bounded prose");
                    AssertContains(instruction, ModelProtocolWire.CriterionGuidance, "result criteria do not repeat reports");
                    if (mode == AgentResponseModes.JsonSchema)
                    {
                        var schema = JObject.Parse(options.ResponseSchemaJson);
                        AssertTrue(schema.SelectTokens("$..reason.description").All(item => (string)item == ModelProtocolWire.ReasonGuidance),
                            "schema and prompt share the reason rule");
                        AssertEqual(ModelProtocolWire.DecisionGuidance, (string)schema.SelectTokens("$..decision.description").Single(),
                            "schema describes concise reflection");
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

        private static async Task HistoryProtocolSurvivesRepairAndFallback()
        {
            var request = NewProtocolRequest(2, true);
            request.Settings.ToolResultRole = ToolResultRoles.Tool;
            request.AcceptedMessages = new[] { new ChatMessage { Role = "system", Content = AgentPromptDefaults.GeneralInstructions } };
            var frozen = request.AcceptedMessages;
            request.CompileRepair = notice => frozen.Concat(new[] { notice }).ToArray();
            var attempts = 0;
            var client = new ModelProtocolClient((settings, messages, options, progress, cancellation) => {
                attempts++;
                AssertContains(messages.First().Content, ModelProtocolWire.NativeHistoryGuidance, "repair/fallback preserve native history instructions");
                AssertTrue(!messages.First().Content.Contains("TOOL_RESULT"), "fallback changes output schema, not history transport");
                if (attempts == 2) throw new LlmRequestException(LlmFailureKind.ResponseFormatUnsupported, "synthetic schema refusal");
                return Task.FromResult(new LlmCompletionResult { Content = attempts == 1 ? "invalid" :
                    ModelProtocolWire.WriteAcceptedDecision(ConversationResponse.FinishAction, "Answer.", "Done.",
                        results: new ConversationResultClaim[0], outcome: ConversationOutcome.Completed) });
            });
            var completion = await client.GetResponseAsync(request, null, CancellationToken.None);
            AssertTrue(completion.Response != null && attempts == 3, "invalid content and explicit format refusal stay bounded");
            AssertEqual(AgentPromptDefaults.GeneralInstructions, frozen[0].Content, "wire projection does not overwrite accepted instructions");
        }
    }
}
