using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
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

        private static void PlannerCompatibilityUnwrapsSingleFence()
        {
            var parser = new AgentPlannerResponseParser();
            var jsonFence = parser.Parse("```json\n{\"kind\":\"final\",\"intent\":\"answer\",\"message\":\"Done.\",\"steps\":[]}\n```");
            var agentFence = parser.Parse("```rnassistant-agent\n{\"kind\":\"final\",\"intent\":\"answer\",\"message\":\"Done.\",\"steps\":[]}\n```");
            var bom = parser.Parse("\uFEFF{\"kind\":\"final\",\"intent\":\"answer\",\"message\":\"Done.\",\"steps\":[]}");
            var noisy = parser.Parse("Result:\n```json\n{\"kind\":\"final\",\"intent\":\"answer\",\"message\":\"Done.\",\"steps\":[]}\n```");

            AssertTrue(jsonFence.Success, "json fence accepted");
            AssertEqual("json_fence", jsonFence.SourceFormat, "json fence source");
            AssertTrue(agentFence.Success, "agent fence accepted");
            AssertEqual("rnassistant_agent_fence", agentFence.SourceFormat, "agent fence source");
            AssertTrue(bom.Success, "utf8 bom accepted");
            AssertTrue(!noisy.Success, "prose around fence rejected");
        }

        private static void PlannerCompatibilityMapsLegacyPlanEnvelope()
        {
            var photographedResponse =
                "```rnassistant-agent\n" +
                "{\"USER_REQUEST\":\"Привет\",\"ROUTE\":{\"app\":\"Excel\",\"mode\":\"answer\",\"requiresTool\":false}," +
                "\"AVAILABLE_TOOLS\":[],\"plan\":{\"steps\":[],\"response\":\"Здравствуйте! Чем могу помочь?\"}}\n" +
                "```";

            var parsed = new AgentPlannerResponseParser().Parse(photographedResponse);
            var toolPlan = new AgentPlannerResponseParser().Parse(
                "```rnassistant-agent\n" +
                "{\"plan\":{\"steps\":[{\"toolId\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Report\"},\"description\":\"Create sheet\"}],\"response\":null}}\n" +
                "```");

            AssertTrue(parsed.Success, "photographed legacy response accepted");
            AssertEqual(AgentResponseKinds.Final, parsed.Response.Kind, "legacy response kind");
            AssertEqual("Здравствуйте! Чем могу помочь?", parsed.Response.Message, "legacy response text");
            AssertEqual("rnassistant_agent_fence_legacy_plan", parsed.SourceFormat, "legacy response source");
            AssertTrue(toolPlan.Success, "legacy tool plan accepted");
            AssertEqual(AgentResponseKinds.ToolPlan, toolPlan.Response.Kind, "legacy tool plan kind");
            AssertEqual("excel.add_sheet", toolPlan.Response.Steps[0].ToolId, "legacy tool id");
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
