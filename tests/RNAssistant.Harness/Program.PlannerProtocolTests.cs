using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void PlannerBoundaryCorpusStaysStrict()
        {
            var parser = new AgentPlannerResponseParser();
            for (var index = 0; index < 50; index++)
            {
                var message = "answer-" + index;
                var valid = "{\"kind\":\"final\",\"intent\":\"answer\",\"message\":\"" + message + "\",\"steps\":[],\"expectedOutcome\":null}";
                AssertTrue(parser.ParseStrict(valid).Success, "valid planner corpus " + index);

                var extra = valid.Substring(0, valid.Length - 1) + ",\"extra" + index + "\":true}";
                AssertTrue(!parser.ParseStrict(extra).Success, "extra key rejected " + index);
                AssertTrue(!parser.ParseStrict("```json\n" + valid + "\n```").Success, "fence rejected " + index);
                AssertTrue(!parser.ParseStrict("[" + valid + "]").Success, "array rejected " + index);
            }
        }

        private static void PlannerStrictParsesJsonEnvelope()
        {
            var parsed = new AgentPlannerResponseParser().ParseStrict("{\"kind\":\"tool_plan\",\"intent\":\"read\",\"message\":null,\"steps\":[{\"toolId\":\"excel.get_context\",\"arguments\":{},\"reason\":\"Need context.\"}],\"expectedOutcome\":\"Read context.\"}");

            AssertTrue(parsed.Success, "strict planner parse succeeds");
            AssertEqual(AgentResponseKinds.ToolPlan, parsed.Response.Kind, "planner kind");
            AssertEqual("excel.get_context", parsed.Response.Steps[0].ToolId, "planner tool id");
        }

        private static void PlannerStrictRejectsMarkdownAndProse()
        {
            var fenced = new AgentPlannerResponseParser().ParseStrict("```json\n{\"kind\":\"final\",\"message\":\"Done.\",\"steps\":[]}\n```");
            var prose = new AgentPlannerResponseParser().ParseStrict("Done.");

            AssertTrue(!fenced.Success, "strict parser rejects fenced json");
            AssertEqual("not_json_object", fenced.ErrorCode, "fenced error code");
            AssertTrue(!prose.Success, "strict parser rejects prose");
            AssertEqual("not_json_object", prose.ErrorCode, "prose error code");
        }

        private static void PlannerRejectsAlternateEnvelopes()
        {
            var parser = new AgentPlannerResponseParser();
            var jsonFence = parser.Parse("```json\n{\"kind\":\"final\",\"intent\":\"answer\",\"message\":\"Done.\",\"steps\":[]}\n```");
            var agentFence = parser.Parse("```rnassistant-agent\n{\"kind\":\"final\",\"intent\":\"answer\",\"message\":\"Done.\",\"steps\":[]}\n```");
            var noisy = parser.Parse("Result:\n```json\n{\"kind\":\"final\",\"intent\":\"answer\",\"message\":\"Done.\",\"steps\":[]}\n```");
            var photographedResponse =
                "```rnassistant-agent\n" +
                "{\"USER_REQUEST\":\"Привет\",\"ROUTE\":{\"app\":\"Excel\",\"mode\":\"answer\",\"requiresTool\":false}," +
                "\"AVAILABLE_TOOLS\":[],\"plan\":{\"steps\":[],\"response\":\"Здравствуйте! Чем могу помочь?\"}}\n" +
                "```";

            AssertEqual("not_json_object", jsonFence.ErrorCode, "json fence rejected");
            AssertEqual("not_json_object", agentFence.ErrorCode, "agent fence rejected");
            AssertEqual("not_json_object", noisy.ErrorCode, "prose around fence rejected");
            AssertEqual("not_json_object", parser.Parse(photographedResponse).ErrorCode, "legacy plan rejected");
        }

        private static void PlannerRejectsInvalidIntentAndSteps()
        {
            var parser = new AgentPlannerResponseParser();
            var intent = parser.Parse("{\"kind\":\"final\",\"intent\":\"browse\",\"message\":\"Done.\",\"steps\":[]}");
            var toolId = parser.Parse("{\"kind\":\"tool_plan\",\"intent\":\"read\",\"message\":null,\"steps\":[{\"arguments\":{}}]}");
            var arguments = parser.Parse("{\"kind\":\"tool_plan\",\"intent\":\"read\",\"message\":null,\"steps\":[{\"toolId\":\"excel.read_range\",\"arguments\":[]}]}");
            var missingSteps = parser.Parse("{\"kind\":\"final\",\"intent\":\"answer\",\"message\":\"Done.\"}");
            var extra = parser.Parse("{\"kind\":\"final\",\"intent\":\"answer\",\"message\":\"Done.\",\"steps\":[],\"USER_REQUEST\":\"echo\"}");
            var objectMessage = parser.Parse("{\"kind\":\"final\",\"intent\":\"answer\",\"message\":{\"text\":\"Done.\"},\"steps\":[]}");
            var extraStep = parser.Parse("{\"kind\":\"tool_plan\",\"intent\":\"read\",\"message\":null,\"steps\":[{\"toolId\":\"excel.read_range\",\"arguments\":{},\"description\":\"read\"}]}");

            AssertEqual("invalid_intent", intent.ErrorCode, "invalid intent");
            AssertEqual("missing_tool_id", toolId.ErrorCode, "missing tool id");
            AssertEqual("invalid_arguments", arguments.ErrorCode, "invalid arguments");
            AssertEqual("missing_steps", missingSteps.ErrorCode, "missing steps");
            AssertEqual("unexpected_field", extra.ErrorCode, "unexpected root field");
            AssertEqual("invalid_message", objectMessage.ErrorCode, "object message");
            AssertEqual("unexpected_step_field", extraStep.ErrorCode, "unexpected step field");
        }

        private static void ModelQualityRequiresToolRejectsFinal()
        {
            var parsed = new AgentPlannerResponseParser().ParseStrict("{\"kind\":\"final\",\"intent\":\"answer\",\"message\":\"I can explain how to do it.\",\"steps\":[]}");
            var requiresTool = true;
            var qualityGateWouldFail = parsed.Success && requiresTool && parsed.Response.Kind == AgentResponseKinds.Final;

            AssertTrue(qualityGateWouldFail, "quality gate detects final answer when tool is required");
        }
    }
}
