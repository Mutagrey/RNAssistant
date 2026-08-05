using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static string Decision(string kind, string message)
        {
            return "{\"protocolVersion\":1,\"kind\":\"" + kind + "\",\"decisionSummary\":\"test\",\"goal\":null,\"plan\":null,\"tool\":null,\"message\":" +
                (message == null ? "null" : "\"" + message + "\"") + "}";
        }

        private static void PlannerBoundaryCorpusStaysStrict()
        {
            var parser = new AgentPlannerResponseParser();
            for (var index = 0; index < 50; index++)
            {
                var valid = Decision("final", "answer-" + index);
                AssertTrue(parser.Parse(valid).Success, "valid decision corpus " + index);
                AssertTrue(!parser.Parse(valid.Substring(0, valid.Length - 1) + ",\"extra\":true}").Success, "extra key rejected " + index);
                AssertTrue(!parser.Parse("```json\n" + valid + "\n```").Success, "json fence rejected " + index);
                AssertTrue(!parser.Parse("[" + valid + "]").Success, "array rejected " + index);
            }
        }

        private static void PlannerStrictParsesJsonEnvelope()
        {
            var parsed = new AgentPlannerResponseParser().Parse("{\"protocolVersion\":1,\"kind\":\"tool\",\"decisionSummary\":\"Need context.\",\"goal\":null,\"plan\":null,\"tool\":{\"toolId\":\"excel.get_context\",\"arguments\":{}},\"message\":null}");
            AssertTrue(parsed.Success, "strict decision parse succeeds");
            AssertEqual(AgentResponseKinds.Tool, parsed.Response.Kind, "decision kind");
            AssertEqual("excel.get_context", parsed.Response.Tool.ToolId, "decision tool id");

            AssertTrue(new AgentPlannerResponseParser().Parse(Decision("clarify", "Уточните задачу.")).Success, "clarify parses");
            AssertTrue(new AgentPlannerResponseParser().Parse(Decision("final", "Готово.")).Success, "final parses");
            AssertEqual("invalid_tool", new AgentPlannerResponseParser().Parse(Decision("tool", null)).ErrorCode, "tool requires call");
        }

        private static void PlannerRejectsFencesAndProse()
        {
            var parser = new AgentPlannerResponseParser();
            AssertEqual("not_json_object", parser.Parse("```json\n" + Decision("final", "Done.") + "\n```").ErrorCode, "fence rejected");
            AssertEqual("not_json_object", parser.Parse("Done.").ErrorCode, "prose rejected");
        }

        private static void PlannerRejectsAlternateEnvelopes()
        {
            var parser = new AgentPlannerResponseParser();
            AssertEqual("not_json_object", parser.Parse("```json\n" + Decision("final", "Done.") + "\n```").ErrorCode, "json fence rejected");
            AssertEqual("not_json_object", parser.Parse("Result:\n" + Decision("final", "Done.")).ErrorCode, "prose around object rejected");
            AssertEqual("unexpected_field", parser.Parse("{\"protocolVersion\":1,\"kind\":\"final\",\"decisionSummary\":\"x\",\"goal\":null,\"plan\":null,\"tool\":null,\"message\":\"ok\",\"steps\":[]}").ErrorCode, "unsupported field rejected");
        }

        private static void PlannerRejectsInvalidIntentAndSteps()
        {
            var parser = new AgentPlannerResponseParser();
            AssertEqual("invalid_protocol_version", parser.Parse("{\"kind\":\"final\"}").ErrorCode, "version required");
            AssertEqual("missing_decision_summary", parser.Parse("{\"protocolVersion\":1,\"kind\":\"final\",\"message\":\"Done.\"}").ErrorCode, "summary required");
            AssertEqual("invalid_tool", parser.Parse("{\"protocolVersion\":1,\"kind\":\"tool\",\"decisionSummary\":\"read\",\"goal\":null,\"plan\":null,\"tool\":{\"toolId\":\"excel.read_range\",\"arguments\":[]},\"message\":null}").ErrorCode, "arguments object required");
            AssertEqual("unexpected_field", parser.Parse("{\"protocolVersion\":1,\"kind\":\"final\",\"decisionSummary\":\"x\",\"message\":\"Done.\",\"USER_REQUEST\":\"echo\"}").ErrorCode, "unexpected root field");
        }

        private static void ModelQualityRequiresToolRejectsFinal()
        {
            var parsed = new AgentPlannerResponseParser().Parse(Decision("final", "I can explain how to do it."));
            AssertTrue(parsed.Success && parsed.Response.Kind == AgentResponseKinds.Final, "quality gate detects final when tool required");
        }
    }
}
