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
                    new AppSettings { ContextCharLimit = 8000, AutoConfirmToolActions = true, RequireVerificationForMutations = false },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual(3, calls.Count, "llm call count");
                AssertEqual("Done.", result.AssistantText, "assistant text");
                AssertEqual(2, adapter.Executed.Count, "adapter execution count");
                AssertEqual("excel.write_table", adapter.Executed[0].ToolId, "first tool");
                AssertTrue(!adapter.Executed[0].Arguments.ContainsKey("values"), "first command missing values");
                AssertEqual("[[\"Month\",\"Sales\"]]", adapter.Executed[1].Arguments["values"], "retry values");
                var resultJson = JsonConvert.SerializeObject(result.ToolResults);
                AssertContains(resultJson, "No table values provided", "recoverable failure logged");
                AssertContains(resultJson, "wrote 1 row", "retry result logged");
                AssertTrue(session.Messages.Any(m => m != null && m.Activity != null && (m.Activity.ResultMessage ?? string.Empty).IndexOf("No table values provided", StringComparison.OrdinalIgnoreCase) >= 0), "recoverable failure persisted in activity");
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
                    new AppSettings { ContextCharLimit = 8000, AutoConfirmToolActions = true, RequireVerificationForMutations = false },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual(3, calls.Count, "llm call count");
                AssertEqual("Finished.", result.AssistantText, "assistant text");
                AssertTrue(ContainsMessage(session.Messages, "Finished."), "final assistant message");
            });
        }

        private static void ChatAdapterExceptionRequiresSuccessfulRetry()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.ThrowOnToolId = "excel.add_sheet";
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    calls,
                    AgentBlock(Command("excel.add_sheet", "name", "Report")),
                    FinalBlock("Done without retry."),
                    AgentBlock(Command("excel.add_sheet", "name", "Report")),
                    FinalBlock("Done after retry."));

                var result = service.ExecuteAsync(
                    "Create a report sheet.",
                    NewSession(adapter),
                    NewContext(adapter),
                    new AppSettings { ContextCharLimit = 8000, AutoConfirmToolActions = true, RequireVerificationForMutations = false },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual(4, calls.Count, "adapter exception retry call count");
                AssertEqual(2, adapter.Executed.Count, "adapter exception execution count");
                AssertContains(FlattenMessages(calls[1]), "scripted adapter failure", "adapter exception becomes observation");
                AssertContains(FlattenMessages(calls[2]), "requires Office tool use", "failed tool does not satisfy required tool gate");
                AssertEqual("Done after retry.", result.AssistantText, "final after successful retry");
            });
        }

        private static void ChatInspectionDoesNotSatisfyMutationRoute()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    calls,
                    AgentBlock(Command("excel.get_context")),
                    FinalBlock("Inspected, so formatting is done."),
                    AgentBlock(Command("excel.format_range", "sheet", "Data", "address", "A1:B4")),
                    FinalBlock("Formatting done."));

                var result = service.ExecuteAsync(
                    "Оформи текущую таблицу красиво.",
                    NewSession(adapter),
                    NewContext(adapter),
                    new AppSettings { ContextCharLimit = 8000, AutoConfirmToolActions = true, RequireVerificationForMutations = false },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual(4, calls.Count, "inspection mutation call count");
                AssertEqual(2, adapter.Executed.Count, "inspection and mutation executed");
                AssertEqual("excel.get_context", adapter.Executed[0].ToolId, "inspection tool");
                AssertEqual("excel.format_range", adapter.Executed[1].ToolId, "mutation tool");
                AssertContains(FlattenMessages(calls[2]), "requires Office tool use", "premature final corrected");
                AssertEqual("Formatting done.", result.AssistantText, "final after mutation");
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
                    "Verified.");
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "Create a report sheet.",
                    session,
                    NewContext(adapter),
                    new AppSettings { ContextCharLimit = 8000, AutoConfirmToolActions = true, RequireVerificationForMutations = true },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Verified.", result.AssistantText, "assistant text");
                AssertTrue(calls.Count >= 2, "llm call count");
                AssertContains(FlattenMessages(calls[1]), "excel.workbook_summary succeeded", "verification observation");
                AssertEqual(2, adapter.Executed.Count, "adapter execution count");
                AssertEqual("excel.add_sheet", adapter.Executed[0].ToolId, "mutation tool");
                AssertEqual("excel.workbook_summary", adapter.Executed[1].ToolId, "verification tool");
            });
        }

        private static void ChatUnavailableVerificationFailsClosed()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var mutation = new ToolDefinition
                {
                    Id = "excel.custom_mutation",
                    Host = "Excel",
                    BuiltIn = true,
                    Enabled = true,
                    MutatesDocument = true,
                    AgentCanRun = true,
                    RiskLevel = 1
                };
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    null,
                    AgentBlock(Command(mutation.Id)),
                    FinalBlock("Done without verification."));

                var result = service.ExecuteAsync(
                    "Create custom content.",
                    NewSession(adapter),
                    NewContext(adapter),
                    new AppSettings { AutoConfirmToolActions = true, RequireVerificationForMutations = true },
                    new List<ToolDefinition> { mutation },
                    null).GetAwaiter().GetResult();

                AssertTrue(result.AssistantText.IndexOf("Done without verification", StringComparison.OrdinalIgnoreCase) < 0, "unverified final rejected");
                AssertContains(JsonConvert.SerializeObject(result.ToolResults), "No deterministic verification tool", "verification unavailable diagnostic");
            });
        }

        private static void ChatFailedVerificationRecovers()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var mutation = new ToolDefinition
                {
                    Id = "excel.custom_mutation",
                    Host = "Excel",
                    BuiltIn = true,
                    Enabled = true,
                    MutatesDocument = true,
                    AgentCanRun = true,
                    RiskLevel = 1,
                    VerifyJson = "{\"toolId\":\"excel.custom_verify\"}"
                };
                var verify = new ToolDefinition
                {
                    Id = "excel.custom_verify",
                    Host = "Excel",
                    BuiltIn = true,
                    Enabled = true
                };
                adapter.QueueResult(verify.Id, ToolResult.Fail("Verification failed."));
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    null,
                    AgentBlock(Command(mutation.Id)),
                    AgentBlock(Command(verify.Id)),
                    FinalBlock("Verified after retry."));

                var result = service.ExecuteAsync(
                    "Create custom content.",
                    NewSession(adapter),
                    NewContext(adapter),
                    new AppSettings { AutoConfirmToolActions = true, RequireVerificationForMutations = true },
                    new List<ToolDefinition> { mutation, verify },
                    null).GetAwaiter().GetResult();

                AssertEqual("Verified after retry.", result.AssistantText, "verification recovery final");
                AssertEqual(3, adapter.Executed.Count, "mutation and two verification executions");
                AssertEqual(verify.Id, adapter.Executed[2].ToolId, "planner verification retry");
            });
        }

        private static void ChatPriorInspectionDoesNotVerifyMutation()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var mutation = new ToolDefinition
                {
                    Id = "excel.custom_format",
                    Host = "Excel",
                    BuiltIn = true,
                    Enabled = true,
                    MutatesDocument = true,
                    AgentCanRun = true,
                    RiskLevel = 1
                };
                var read = new ToolDefinition
                {
                    Id = "excel.get_context",
                    Host = "Excel",
                    BuiltIn = true,
                    Enabled = true
                };
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    null,
                    AgentBlock(Command(read.Id)),
                    AgentBlock(Command(mutation.Id)),
                    FinalBlock("Premature final."),
                    AgentBlock(Command(read.Id)),
                    FinalBlock("Verified with a new read."));

                var result = service.ExecuteAsync(
                    "Оформи текущую таблицу красиво.",
                    NewSession(adapter),
                    NewContext(adapter),
                    new AppSettings { AutoConfirmToolActions = true, RequireVerificationForMutations = true },
                    new List<ToolDefinition> { read, mutation },
                    null).GetAwaiter().GetResult();

                AssertEqual("Verified with a new read.", result.AssistantText, "fresh verification final");
                AssertEqual(3, adapter.Executed.Count, "inspection mutation verification count");
                AssertEqual(read.Id, adapter.Executed[2].ToolId, "fresh read verifies mutation");
            });
        }
    }
}
