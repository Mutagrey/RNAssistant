using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void AgentToolResultDataIsBounded()
        {
            var command = new ToolCommand { ToolId = "excel.read_range", ToolCallId = "call_large" };
            var data = JsonConvert.SerializeObject(new { value = new string('x', 50000) });
            var result = JObject.Parse(AgentJsonProtocol.BuildToolResult(command, ToolResult.Ok("read", data), 256));

            AssertTrue(result.SelectToken("data.truncated").Value<bool>(), "oversized data is marked truncated");
            AssertTrue(result.SelectToken("data.original_chars").Value<int>() > 49000, "original size retained");
            AssertTrue(((string)result.SelectToken("data.preview") ?? string.Empty).Length < 1000, "preview is bounded");
            AssertEqual("call_large", (string)result["tool_call_id"], "tool call id retained");
        }

        private static void AgentToolResultFitsRemainingPromptBudget()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.QueueResult("excel.list_sheets", ToolResult.Ok(
                    "large read",
                    JsonConvert.SerializeObject(new { value = new string('x', 100000) })));
                var responses = new Queue<string>(new[]
                {
                    "{\"message\":\"Читаю.\",\"tool_calls\":[{\"id\":\"call_large\",\"name\":\"excel.list_sheets\",\"arguments\":{}}]}",
                    "{\"message\":\"Диапазон результата нужно сузить.\",\"tool_calls\":[]}"
                });
                var calls = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (completionSettings, messages, options, stream, cancellationToken) =>
                {
                    calls.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var settings = new AppSettings
                {
                    ContextWindowOverrideTokens = 12000,
                    MaxTokens = 512
                };
                var tools = adapter.GetBuiltInTools().Where(tool => tool.Id == "excel.list_sheets").ToList();

                var turn = new AgentRunService(adapter, executor, completion).ExecuteAsync(
                    "List sheets.", NewSession(adapter), NewContext(adapter), settings, tools,
                    null, null, null, null, CancellationToken.None, true).GetAwaiter().GetResult();

                AssertEqual("Диапазон результата нужно сузить.", turn.AssistantText, "agent continues after bounded result");
                AssertEqual(2, calls.Count, "two model calls");
                var replay = FlattenSimple(calls[1]);
                AssertContains(replay, "\"truncated\":true", "bounded marker reaches model");
                var estimated = ModelContextBudget.EstimateMessagesTokens(calls[1]) +
                    ModelContextBudget.EstimateRequestOptionsTokens(new LlmRequestOptions { ResponseFormat = LlmResponseFormats.JsonObject });
                AssertTrue(estimated <= ModelContextBudget.InputBudgetTokens(settings), "next prompt stays within budget");
            });
        }

        private static void ModelCompatibilityAcceptsExactSentinels()
        {
            var responses = new Queue<string>(new[]
            {
                "ROLE_OK",
                "{\"message\":\"TOOL_OK\",\"tool_calls\":[{\"id\":\"call_1\",\"name\":\"compat.echo\",\"arguments\":{\"value\":\"A\"}}]}",
                "{\"message\":\"RESULT_OK\",\"tool_calls\":[]}"
            });
            LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
                Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });

            var result = new ModelCompatibilityService(completion).TestAsync(new AppSettings(), CancellationToken.None)
                .GetAwaiter().GetResult();

            AssertTrue(result.Compatible, "exact compatibility flow accepted");
            AssertTrue(result.Checks.All(check => check.Passed), "all exact probes pass");
        }

        private static void ModelCompatibilityRejectsLooseResponses()
        {
            var responses = new Queue<string>(new[]
            {
                "Any non-empty response",
                "{\"message\":\"TOOL_OK\",\"tool_calls\":[{\"id\":\"call_1\",\"name\":\"compat.echo\",\"arguments\":{\"value\":\"WRONG\"}}]}",
                "{\"message\":\"Any final message\",\"tool_calls\":[]}"
            });
            LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
                Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });

            var result = new ModelCompatibilityService(completion).TestAsync(new AppSettings(), CancellationToken.None)
                .GetAwaiter().GetResult();

            AssertTrue(!result.Compatible, "loose compatibility flow rejected");
            AssertTrue(result.Checks.All(check => !check.Passed), "each loose probe fails");
        }
    }
}
