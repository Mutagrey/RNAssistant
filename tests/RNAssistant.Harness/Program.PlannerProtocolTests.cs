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
            AssertEqual("invalid_protocol_version", parser.Parse("{\"protocolVersion\":2,\"kind\":\"final\",\"message\":\"Done.\"}").ErrorCode, "wrong version rejected");
            AssertEqual("invalid_kind", parser.Parse("{\"protocolVersion\":1,\"kind\":\"dance\",\"message\":\"Done.\"}").ErrorCode, "unknown kind rejected");
            AssertEqual("invalid_tool", parser.Parse("{\"protocolVersion\":1,\"kind\":\"tool\",\"decisionSummary\":\"read\",\"goal\":null,\"plan\":null,\"tool\":{\"toolId\":\"excel.read_range\",\"arguments\":[]},\"message\":null}").ErrorCode, "arguments object required");
            AssertEqual("unexpected_field", parser.Parse("{\"protocolVersion\":1,\"kind\":\"final\",\"decisionSummary\":\"x\",\"message\":\"Done.\",\"USER_REQUEST\":\"echo\"}").ErrorCode, "unexpected root field");
        }

        private static void PlannerNormalizesSafeModelVariants()
        {
            var parser = new AgentPlannerResponseParser();

            var missingInactive = parser.Parse("{\"kind\":\"final\",\"decisionSummary\":\"Готово.\"}");
            AssertTrue(missingInactive.Success, "missing inactive fields accepted");
            AssertEqual("Готово.", missingInactive.Response.Message, "terminal message recovered from summary");

            var photographedPlan = parser.Parse(
                "{\"protocolVersion\":1,\"kind\":\"plan\",\"decisionSummary\":\"Создам игру.\",\"plan\":[" +
                "{\"id\":\"step1\",\"action\":\"Создать HTML\",\"expected\":\"index.html\"}," +
                "{\"action\":\"Добавить стили\",\"status\":\"pending\"}],\"tool\":null}");
            AssertTrue(photographedPlan.Success, "action/expected plan variant accepted");
            AssertEqual("Создам игру.", photographedPlan.Response.Goal, "missing plan goal recovered");
            AssertEqual("Создать HTML", photographedPlan.Response.Plan[0].Title, "action becomes plan title");
            AssertEqual("step_2", photographedPlan.Response.Plan[1].Id, "missing step id generated");

            var photographedTool = parser.Parse(
                "{\"kind\":\"tool\",\"decisionSummary\":\"Создаю HTML.\",\"goal\":\"Создать Тетрис\"," +
                "\"tool\":{\"id\":\"common.html_workspace_upsert_file\",\"args\":{\"path\":\"index.html\"}}}",
                new[]
                {
                    new ToolDefinition
                    {
                        Id = "common.html_workspace_upsert_file",
                        ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"],\"additionalProperties\":false}"
                    }
                });
            AssertTrue(photographedTool.Success, "tool goal and id/args aliases accepted");
            AssertEqual("Создать Тетрис", photographedTool.Response.Goal, "advisory tool goal retained");
            AssertEqual("common.html_workspace_upsert_file", photographedTool.Response.Tool.ToolId, "tool id alias normalized");

            var replyAction = parser.Parse("{\"decisionSummary\":\"Отвечаю.\",\"action\":{\"type\":\"reply\",\"content\":\"Привет!\"}}");
            AssertTrue(replyAction.Success, "safe reply action envelope accepted");
            AssertEqual(AgentResponseKinds.Final, replyAction.Response.Kind, "reply action becomes final");
            AssertEqual("Привет!", replyAction.Response.Message, "reply content retained");

            var openAiCall = parser.Parse(
                "{\"decisionSummary\":\"Читаю книгу.\",\"tool_calls\":[{\"id\":\"call_123\",\"type\":\"function\",\"function\":{\"name\":\"excel.get_context\",\"arguments\":\"{}\"}}]}",
                new[] { new ToolDefinition { Id = "excel.get_context", ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}" } });
            AssertTrue(openAiCall.Success, "single OpenAI-style tool_calls envelope accepted");
            AssertEqual("excel.get_context", openAiCall.Response.Tool.ToolId, "function name wins over tool call id");

            AssertEqual("multiple_tool_calls", parser.Parse(
                "{\"decisionSummary\":\"x\",\"toolCalls\":[{\"id\":\"one\",\"args\":{}},{\"id\":\"two\",\"args\":{}}]}").ErrorCode,
                "parallel compatibility calls still rejected");
            AssertEqual("conflicting_envelope", parser.Parse(
                "{\"kind\":\"tool\",\"decisionSummary\":\"x\",\"action\":{\"type\":\"reply\",\"content\":\"Не выполнять tool\"}}").ErrorCode,
                "conflicting compatibility envelope rejected");
            AssertEqual("conflicting_alias", parser.Parse(
                "{\"protocolVersion\":1,\"protocol_version\":2,\"kind\":\"final\",\"message\":\"x\"}").ErrorCode,
                "conflicting root alias rejected");
            AssertEqual("invalid_tool", parser.Parse(
                "{\"kind\":\"tool\",\"tool\":{\"toolId\":\"excel.get_context\",\"name\":\"excel.read_range\",\"arguments\":{}}}").ErrorCode,
                "conflicting tool id aliases rejected");
            AssertEqual("invalid_tool", parser.Parse(
                "{\"kind\":\"tool\",\"tool\":{\"toolId\":\"excel.get_context\",\"arguments\":{},\"args\":{\"x\":1}}}").ErrorCode,
                "conflicting argument aliases rejected");
            AssertEqual("invalid_tool", parser.Parse(
                "{\"kind\":\"tool\",\"tool\":{\"toolId\":\"excel.get_context\",\"arguments\":{},\"invented\":true}}").ErrorCode,
                "unknown tool fields are not merged into arguments");
        }

        private static void ModelQualityRequiresToolRejectsFinal()
        {
            var parsed = new AgentPlannerResponseParser().Parse(Decision("final", "I can explain how to do it."));
            AssertTrue(parsed.Success && parsed.Response.Kind == AgentResponseKinds.Final, "quality gate detects final when tool required");
        }
    }
}
