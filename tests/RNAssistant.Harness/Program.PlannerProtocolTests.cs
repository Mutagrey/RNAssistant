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
                AssertTrue(parser.Parse(valid).Success, "valid planner corpus " + index);

                var extra = valid.Substring(0, valid.Length - 1) + ",\"extra" + index + "\":true}";
                AssertTrue(!parser.Parse(extra).Success, "extra key rejected " + index);
                AssertTrue(parser.Parse("```json\n" + valid + "\n```").Success, "clean json fence accepted " + index);
                AssertTrue(!parser.Parse("[" + valid + "]").Success, "array rejected " + index);
            }
        }

        private static void PlannerStrictParsesJsonEnvelope()
        {
            var parsed = new AgentPlannerResponseParser().Parse("{\"kind\":\"tool_plan\",\"intent\":\"read\",\"message\":null,\"steps\":[{\"toolId\":\"excel.get_context\",\"arguments\":{},\"reason\":\"Need context.\"}],\"expectedOutcome\":\"Read context.\"}");

            AssertTrue(parsed.Success, "strict planner parse succeeds");
            AssertEqual(AgentResponseKinds.ToolPlan, parsed.Response.Kind, "planner kind");
            AssertEqual("excel.get_context", parsed.Response.Steps[0].ToolId, "planner tool id");

            var clarify = new AgentPlannerResponseParser().Parse(
                "{\"kind\":\"clarify\",\"intent\":\"clarify\",\"message\":\"Уточните задачу.\",\"steps\":null,\"expectedOutcome\":null}");
            var finalWithoutSteps = new AgentPlannerResponseParser().Parse(
                "{\"kind\":\"final\",\"intent\":\"answer\",\"message\":\"Готово.\"}");
            var toolPlanWithoutSteps = new AgentPlannerResponseParser().Parse(
                "{\"kind\":\"tool_plan\",\"intent\":\"read\",\"message\":null,\"steps\":null}");
            AssertTrue(clarify.Success, "clarify accepts null steps");
            AssertTrue(finalWithoutSteps.Success, "final accepts omitted steps");
            AssertEqual("missing_steps", toolPlanWithoutSteps.ErrorCode, "tool plan still requires steps");
        }

        private static void PlannerAcceptsCleanJsonFenceAndRejectsProse()
        {
            var fenced = new AgentPlannerResponseParser().Parse("```json\n{\"kind\":\"final\",\"message\":\"Done.\",\"steps\":[]}\n```");
            var prose = new AgentPlannerResponseParser().Parse("Done.");

            AssertTrue(fenced.Success, "parser accepts one clean json fence");
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

            AssertTrue(jsonFence.Success, "json fence accepted");
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
            AssertTrue(missingSteps.Success, "non-tool response may omit steps");
            AssertEqual("unexpected_field", extra.ErrorCode, "unexpected root field");
            AssertEqual("invalid_message", objectMessage.ErrorCode, "object message");
            AssertEqual("unexpected_step_field", extraStep.ErrorCode, "unexpected step field");
        }

        private static void ModelQualityRequiresToolRejectsFinal()
        {
            var parsed = new AgentPlannerResponseParser().Parse("{\"kind\":\"final\",\"intent\":\"answer\",\"message\":\"I can explain how to do it.\",\"steps\":[]}");
            var requiresTool = true;
            var qualityGateWouldFail = parsed.Success && requiresTool && parsed.Response.Kind == AgentResponseKinds.Final;

            AssertTrue(qualityGateWouldFail, "quality gate detects final answer when tool is required");
        }
    }
}
