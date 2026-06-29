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

        private static void ModelQualityRequiresToolRejectsFinal()
        {
            var parsed = new AgentPlannerResponseParser().ParseStrict("{\"kind\":\"final\",\"intent\":\"answer\",\"message\":\"I can explain how to do it.\",\"steps\":[]}");
            var requiresTool = true;
            var qualityGateWouldFail = parsed.Success && requiresTool && parsed.Response.Kind == AgentResponseKinds.Final;

            AssertTrue(qualityGateWouldFail, "quality gate detects final answer when tool is required");
        }
    }
}
