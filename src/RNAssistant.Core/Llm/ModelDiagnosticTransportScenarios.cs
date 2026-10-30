using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Llm
{
    public static partial class ModelDiagnosticScenarios
    {
        public static IReadOnlyList<ModelDiagnosticScenario> All(string seed)
        {
            var history = NativeHistory(seed); // Also validates the seed.
            var cases = new List<ModelDiagnosticScenario> {
                Text("A1", seed, "user"), Text("A2", seed, "developer"), Text("A2-system", seed, "system"),
                V8("A3", seed, execute: true), V8("A4", seed)
            };
            cases.AddRange(history.Where(item => item.Id.StartsWith("B")));
            cases.Add(NativeGeneration(seed)); cases.Add(MultiRun(seed));
            cases.Add(V8("C1", seed)); cases.Add(V8("C2", seed, strict: true));
            cases.Add(Schema("C3-valid", seed)); cases.Add(Schema("C3", seed, invalid: true));
            cases.Add(Schema("C3-text", seed, text: true));
            var control = V8("C4", seed);
            control.Id = "C4-control"; control.Capability = ModelDiagnosticCapability.StreamingParity;
            var streaming = V8("C4", seed);
            streaming.Streaming = true; streaming.ParityControlId = control.Id;
            streaming.Capability = ModelDiagnosticCapability.StreamingParity;
            cases.Add(control); cases.Add(streaming);
            cases.AddRange(history.Where(item => item.Id.StartsWith("C")));
            return cases;
        }

        private static ModelDiagnosticScenario Text(string id, string seed, string role)
        {
            var marker = Marker(seed, id);
            var result = new ModelDiagnosticScenario { Id = id, Assertion = ModelDiagnosticAssertion.ExactText,
                Capability = role == "user" ? ModelDiagnosticCapability.TextResponse : ModelDiagnosticCapability.InstructionRole,
                ResponseFormat = LlmResponseFormats.Text, ExpectedText = marker };
            result.Messages.Add(new ChatMessage { Role = role, Content = "Diagnostic instruction. ROLE_MARKER=" + marker +
                ". Reply with exactly the ROLE_MARKER, without quotes or other text." });
            if (role != "user") result.Messages.Add(new ChatMessage { Role = "user",
                Content = "Return the ROLE_MARKER from the instruction message. Do not invent a marker." });
            return result;
        }

        private static ModelDiagnosticScenario V8(string id, string seed, bool execute = false, bool strict = false)
        {
            var marker = Marker(seed, id);
            var result = new ModelDiagnosticScenario { Id = id, Assertion = ModelDiagnosticAssertion.V8,
                Capability = id == "A3" ? ModelDiagnosticCapability.V8Execute : id == "A4" ? ModelDiagnosticCapability.V8Finish :
                    strict ? ModelDiagnosticCapability.JsonSchema : ModelDiagnosticCapability.JsonObject,
                ResponseFormat = strict ? LlmResponseFormats.JsonSchema : LlmResponseFormats.JsonObject };
            if (execute)
            {
                result.CallableTools.Add(new ToolCatalogEntry { Id = "compat.echo", Description = "Synthetic echo; never executed.",
                    ArgumentSchemaJson = ValueSchema() });
                result.ExpectedJson = ModelProtocolWire.WriteAcceptedCall("Probe V8 execute.", marker,
                    new ConversationToolCall { Name = "compat.echo", Arguments = new JObject { ["value"] = marker } });
            }
            else result.ExpectedJson = ModelProtocolWire.WriteAcceptedDecision(ConversationResponse.FinishAction,
                "Probe the current finish contract.", marker, results: new[] {
                    new ConversationResultClaim("Synthetic requirement remains unverified.", new string[0], ConversationAssessment.Unverified) },
                outcome: ConversationOutcome.UnableToComplete);
            var expected = strict ? new JObject { ["response"] = JObject.Parse(result.ExpectedJson) }.ToString(Formatting.None) : result.ExpectedJson;
            result.Messages.Add(new ChatMessage { Role = "user", Content =
                "Return exactly this raw JSON object, without Markdown or additional fields. This is a synthetic parser probe, not an Office action:\n" + expected });
            return result;
        }

        private static ModelDiagnosticScenario Schema(string id, string seed, bool invalid = false, bool text = false)
        {
            var expected = new JObject { ["marker"] = Marker(seed, id) }.ToString(Formatting.None);
            return new ModelDiagnosticScenario { Id = id,
                Assertion = invalid ? ModelDiagnosticAssertion.SchemaRejection : ModelDiagnosticAssertion.ExactJson,
                Capability = invalid ? ModelDiagnosticCapability.InvalidSchema : text ? ModelDiagnosticCapability.TextJson : ModelDiagnosticCapability.MinimalSchema,
                ResponseFormat = text ? LlmResponseFormats.Text : LlmResponseFormats.JsonSchema,
                ResponseSchemaJson = new JObject { ["type"] = "object", ["properties"] = new JObject {
                    ["marker"] = new JObject { ["type"] = invalid ? "invalid_diagnostic_type" : "string" } },
                    ["required"] = new JArray("marker"), ["additionalProperties"] = false }.ToString(Formatting.None),
                ExpectedJson = expected, Messages = new List<ChatMessage> { new ChatMessage { Role = "user",
                    Content = "Return exactly this raw JSON, without Markdown: " + expected } } };
        }

        private static ModelDiagnosticScenario NativeGeneration(string seed)
        {
            var marker = Marker(seed, "B8");
            return new ModelDiagnosticScenario { Id = "B8", Capability = ModelDiagnosticCapability.NativeGeneration,
                Assertion = ModelDiagnosticAssertion.NativeCall, ResponseFormat = LlmResponseFormats.Text,
                NativeFunction = new LlmFunctionDefinition { Name = "diagnostic_echo", Description = "Synthetic echo probe; no tool will execute.", ParametersJson = ValueSchema() },
                ExpectedJson = new JObject { ["name"] = "diagnostic_echo", ["arguments"] = new JObject { ["value"] = marker } }.ToString(Formatting.None),
                Messages = new List<ChatMessage> { new ChatMessage { Role = "user", Content =
                    "Use the native diagnostic_echo function exactly once with value=" + marker + ". Do not describe the call as JSON in content." } } };
        }

        private static string ValueSchema()
        { return "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\",\"description\":\"Exact synthetic marker to echo.\"}},\"required\":[\"value\"],\"additionalProperties\":false}"; }

        private static ModelDiagnosticScenario MultiRun(string seed)
        {
            var result = History("B9-old", seed, NativeToolCallContentMode.Null);
            result.Id = "B9"; result.Capability = ModelDiagnosticCapability.MultiRunHistory;
            result.Messages.RemoveAt(result.Messages.Count - 1);
            result.Messages.Add(new ChatMessage { Role = "assistant", Content = ModelProtocolWire.WriteAcceptedDecision(
                ConversationResponse.FinishAction, "Previous synthetic run ended.", "First read received.",
                results: new ConversationResultClaim[0], outcome: ConversationOutcome.Completed) });
            result.Messages.Add(new ChatMessage { Role = "user", Content = "A new request: read ModuleB again and use the latest result." });
            result.Messages.Add(new ChatMessage { Role = "assistant", Content = null, ToolCalls = new List<LlmToolCall> { Call("T4", "ModuleB") } });
            var marker = Marker(seed, "B9-current");
            result.Messages.Add(new ChatMessage { Role = "tool", ToolCallId = "T4", Content = new JObject { ["marker"] = marker }.ToString(Formatting.None) });
            result.Messages.Add(new ChatMessage { Role = "user", Content =
                "Return raw JSON with exactly target, marker and tool_call_id for the latest ModuleB result in this conversation. No Markdown." });
            result.ExpectedJson = Answer(marker, "T4").ToString(Formatting.None);
            return result;
        }
    }
}
