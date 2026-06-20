using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Storage;
using RNAssistant.Office;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Office.WebView;
using RNAssistant.Desktop;
using RNAssistant.OfficeHosts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ChatFailedToolRetriesCorrectedCall()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.QueueResult("excel.write_table", ToolResult.Fail("No table values provided."));
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    calls,
                    AgentBlock(Command("excel.write_table", "sheet", "Report", "startAddress", "A1")),
                    AgentBlock(Command("excel.write_table", "sheet", "Report", "startAddress", "A1", "values", "[[\"Month\",\"Sales\"]]")));
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "Write a report table.",
                    session,
                    NewContext(adapter),
                    new AppSettings { ContextCharLimit = 8000 },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual(3, calls.Count, "llm call count");
                AssertEqual("Done.", result.AssistantText, "assistant text");
                AssertEqual(2, adapter.Executed.Count, "adapter execution count");
                AssertEqual("excel.write_table", adapter.Executed[0].ToolId, "first tool");
                AssertTrue(!adapter.Executed[0].Arguments.ContainsKey("values"), "first command missing values");
                AssertEqual("[[\"Month\",\"Sales\"]]", adapter.Executed[1].Arguments["values"], "retry values");
                var resultJson = JsonConvert.SerializeObject(result.ToolResults);
                AssertContains(resultJson, "No table values provided", "failed result logged");
                AssertContains(resultJson, "executed excel.write_table", "retry result logged");
                AssertTrue(ContainsMessage(session.Messages, "Local skill retry result") || ContainsMessage(session.Messages, "Agent step"), "retry transcript recorded");
            });
        }

        private static void ChatRetrySuccessContinuesToFinalAnswer()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.QueueResult("excel.write_table", ToolResult.Fail("No table values provided."));
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    calls,
                    AgentBlock(Command("excel.write_table", "sheet", "Report", "startAddress", "A1")),
                    AgentBlock(Command("excel.write_table", "sheet", "Report", "startAddress", "A1", "values", "[[\"Month\",\"Sales\"]]")),
                    "Finished.");
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "Write a report table.",
                    session,
                    NewContext(adapter),
                    new AppSettings { ContextCharLimit = 8000 },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual(3, calls.Count, "llm call count");
                AssertEqual("Finished.", result.AssistantText, "assistant text");
                AssertTrue(ContainsMessage(session.Messages, "Finished."), "final assistant message");
            });
        }

        private static void ChatMutationRequestsVerificationFollowUp()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    calls,
                    AgentBlock(Command("excel.add_sheet", "name", "Report")),
                    AgentBlock(Command("excel.workbook_summary")),
                    "Verified.");
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "Create a report sheet.",
                    session,
                    NewContext(adapter),
                    new AppSettings { ContextCharLimit = 8000, RequireVerificationForMutations = true },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Verified.", result.AssistantText, "assistant text");
                AssertTrue(calls.Count >= 2, "llm call count");
                AssertContains(FlattenMessages(calls[1]), "verify the result", "verification follow-up");
                AssertEqual(2, adapter.Executed.Count, "adapter execution count");
                AssertEqual("excel.add_sheet", adapter.Executed[0].ToolId, "mutation tool");
                AssertEqual("excel.workbook_summary", adapter.Executed[1].ToolId, "verification tool");
            });
        }
    }
}
