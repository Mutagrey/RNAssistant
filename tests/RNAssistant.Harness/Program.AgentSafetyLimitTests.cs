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
        private static void ChatWaitingToolGetsPendingId()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var pendingIds = new List<string>();
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    null,
                    AgentBlock(Command("common.skills_load", "ids", new[] { "common.vba_code_editing" })),
                    AgentBlock(Command("word.vba_read_module", "moduleName", "Module1")),
                    AgentBlock(Command("word.vba_replace_module", "moduleName", "Module1", "code", "Sub Test()\nEnd Sub")));
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "Replace a VBA module.",
                    session,
                    NewContext(adapter),
                    new AppSettings { AutoConfirmToolActions = false },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null,
                    delegate(ChatSession pendingSession, ToolCommand pendingCommand, ToolResult pendingResult)
                    {
                        AssertEqual(session.Id, pendingSession.Id, "pending session id");
                        AssertEqual("word.vba_replace_module", pendingCommand.ToolId, "pending tool id");
                        pendingIds.Add("pending-1");
                        return "pending-1";
                    },
                    BuiltInSkillProvider.GetSkills(adapter)).GetAwaiter().GetResult();

                AssertEqual(1, adapter.Executed.Count, "only inspection executes before confirmation");
                AssertEqual("word.vba_read_module", adapter.Executed[0].ToolId, "inspection tool");
                AssertEqual(1, pendingIds.Count, "pending count");
                var resultJson = JsonConvert.SerializeObject(result.ToolResults);
                AssertContains(resultJson, "waiting_confirmation", "waiting status");
                AssertContains(resultJson, "pending-1", "pending id");
                AssertTrue(session.Messages.Any(m => m != null && m.Activity != null && string.Equals(m.Activity.PendingId, "pending-1", StringComparison.OrdinalIgnoreCase)), "pending activity");
            });
        }

        private static void ChatWaitingToolStopsRun()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var pendingIds = new List<string>();
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    null,
                    AgentBlock(Command("word.vba_replace_module", "moduleName", "Module1", "code", "Sub Test()\nEnd Sub")));
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "Replace VBA and then insert text.",
                    session,
                    NewContext(adapter),
                    new AppSettings { AutoConfirmToolActions = false },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null,
                    delegate(ChatSession pendingSession, ToolCommand pendingCommand, ToolResult pendingResult)
                    {
                        pendingIds.Add("pending-" + (pendingIds.Count + 1));
                        return pendingIds[pendingIds.Count - 1];
                    }).GetAwaiter().GetResult();

                AssertEqual(0, adapter.Executed.Count, "adapter execution count");
                AssertEqual(1, pendingIds.Count, "pending count");
                AssertEqual(1, result.ToolResults.Count, "tool result count");
                AssertContains(JsonConvert.SerializeObject(result.ToolResults), "word.vba_replace_module", "first tool logged");
                AssertEqual(1, session.Messages.Count(message =>
                    message != null &&
                    message.Activity != null &&
                    string.Equals(message.Activity.PendingId, "pending-1", StringComparison.OrdinalIgnoreCase)), "one pending activity");
            });
        }

        private static void ChatMaxIterationsReturnsRuntimeSummary()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    null,
                    AgentBlock(Command("excel.list_sheets")),
                    AgentBlock(Command("excel.get_context")),
                    AgentBlock(Command("excel.get_selection")));
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "List sheets repeatedly.",
                    session,
                    NewContext(adapter),
                    new AppSettings { MaxAgentIterations = 3 },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertContains(result.AssistantText, "Готово: выполнено действий", "summary text");
                AssertTrue(result.AssistantText.IndexOf("rnassistant-agent", StringComparison.OrdinalIgnoreCase) < 0, "no raw agent block");
                AssertTrue(ContainsMessage(session.Messages, "Готово: выполнено действий"), "runtime summary persisted");
            });
        }

        private static void ChatToolStepLimitStopsRun()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    null,
                    AgentBlock(Command("excel.get_context")),
                    AgentBlock(Command("excel.get_selection")));
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "Inspect workbook repeatedly.",
                    session,
                    NewContext(adapter),
                    new AppSettings { MaxAgentToolSteps = 1 },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual(1, adapter.Executed.Count, "adapter execution count");
                AssertContains(result.AssistantText, "ошибки инструмента", "step limit summary");
                AssertTrue(ContainsMessage(session.Messages, "Agent tool step limit exceeded"), "step limit transcript");
            });
        }

        private static void MultipleToolCallsAreRejectedAndReplanned()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    calls,
                    AgentBlock(
                        Command("excel.add_sheet", "name", "Report"),
                        Command("excel.add_chart", "sheet", "Report", "sourceRange", "A1:B2")),
                    AgentBlock(Command("excel.add_sheet", "name", "Report")),
                    FinalBlock("Done."));

                var result = service.ExecuteAsync(
                    "Create a report sheet.",
                    NewSession(adapter),
                    NewContext(adapter),
                    new AppSettings
                    {
                        AutoConfirmToolActions = true,
                        RequireVerificationForMutations = false,
                        FallbackToJsonObject = false
                    },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Done.", result.AssistantText, "replanned final answer");
                AssertEqual(1, adapter.Executed.Count, "only corrected action executed");
                AssertEqual("excel.add_sheet", adapter.Executed[0].ToolId, "corrected action");
                AssertContains(FlattenMessages(calls[1]), "Multi-tool пакет", "unsafe multi-call decision rejected");
                AssertContains(FlattenMessages(calls[1]), "read-only", "unsafe batch explains single mutation rule");
            });
        }

        private static void MultipleReadOnlyToolsExecuteAsBatch()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = NewSession(adapter);
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    null,
                    AgentBlock(Command("excel.get_context"), Command("excel.get_selection")),
                    FinalBlock("Read batch complete."));

                var result = service.ExecuteAsync(
                    "Inspect the current workbook and selection.",
                    session,
                    NewContext(adapter),
                    new AppSettings { FallbackToJsonObject = false },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Read batch complete.", result.AssistantText, "multi-read final answer");
                AssertEqual(2, adapter.Executed.Count, "multi-read execution count");
                var batch = session.Messages.FirstOrDefault(message =>
                    message != null && message.Activity != null &&
                    string.Equals(message.Activity.Kind, "tool_batch", StringComparison.OrdinalIgnoreCase));
                AssertTrue(batch != null, "multi-read batch activity persisted");
                AssertEqual(2, batch.Activity.Children.Count, "multi-read UI child count");
                AssertTrue(batch.Activity.Children.All(child => string.Equals(child.Status, "completed", StringComparison.OrdinalIgnoreCase)), "multi-read UI statuses");
                var protocol = session.Messages.FirstOrDefault(message => message != null && message.ProtocolMessage && message.ToolCalls != null && message.ToolCalls.Count == 2);
                AssertTrue(protocol != null, "multi-read protocol assistant call persisted");
                AssertEqual(2, session.Messages.Count(message => message != null && message.ProtocolMessage && string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase)), "multi-read protocol results persisted");
            });
        }

        private static void ChatAutoRunDisabledRecordsLocalFailure()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var service = ChatServiceWithResponses(adapter, executor, calls, AgentBlock(Command("word.insert_text", "text", "Hello")));
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "Insert text into the document.",
                    session,
                    NewContext(adapter),
                    new AppSettings { AutoRunToolCalls = false, AutoConfirmToolActions = true },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual(0, adapter.Executed.Count, "adapter execution count");
                AssertContains(JsonConvert.SerializeObject(result.ToolResults), "Auto tool execution is disabled", "auto-run result");
                AssertTrue(ContainsMessage(session.Messages, "Run word.insert_text"), "tool decision recorded");
                AssertTrue(ContainsMessage(session.Messages, "waiting"), "waiting recorded");
            });
        }

        private static void ChatMalformedPlannerResponseIsRepaired()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("PowerPoint"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var malformed = "I tried this but it is broken:\n```rnassistant-agent\n{\"steps\":[\n```\nExtra noisy text.";
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    calls,
                    RawResponse(malformed),
                    AgentBlock(Command("powerpoint.read_slides", "maxSlides", 20)),
                    FinalBlock("Done."));
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "Summarize the presentation.",
                    session,
                    NewContext(adapter),
                    new AppSettings { FallbackToJsonObject = false },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Done.", result.AssistantText, "assistant text");
                AssertEqual(1, adapter.Executed.Count, "adapter execution count");
                AssertEqual("powerpoint.read_slides", adapter.Executed[0].ToolId, "repair tool id");
                AssertTrue(ContainsMessage(session.Messages, "Done."), "assistant transcript");
                AssertEqual(3, calls.Count, "llm call count");
            });
        }

        private static void ChatInvalidPlannerRecordsResponseDiagnostics()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("PowerPoint"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    null,
                    RawResponse("not json first"),
                    RawResponse("not json after repair"));
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "Summarize the presentation.",
                    session,
                    NewContext(adapter),
                    new AppSettings { FallbackToJsonObject = false, MaxAgentFormatRetries = 1 },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertContains(result.AssistantText, "not_json_object", "invalid response result");
                var diagnosticMessage = session.Messages.Last();
                var diagnostic = diagnosticMessage.Activity;
                AssertTrue(diagnostic != null, "diagnostic activity");
                AssertTrue(diagnosticMessage.ExcludeFromModelContext, "terminal diagnostic excluded from model context");
                AssertEqual("Planner JSON invalid", diagnostic.Title, "diagnostic title");
                AssertContains(diagnostic.ResultMessage, "not_json_object", "diagnostic error");
                AssertContains(diagnostic.DataJson, "not json after repair", "diagnostic response preview");
            });
        }

        private static void ChatNullCompletionBecomesPlannerDiagnostic()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = 0;
                var service = new ChatCompletionService(
                    adapter,
                    executor,
                    delegate(AppSettings settings, IEnumerable<ChatMessage> messages, LlmRequestOptions requestOptions, Action<LlmStreamUpdate> streamProgress, CancellationToken cancellationToken)
                    {
                        calls += 1;
                        return Task.FromResult<LlmCompletionResult>(null);
                    });
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "Hello.",
                    session,
                    NewContext(adapter),
                    new AppSettings { FallbackToJsonObject = false, MaxAgentFormatRetries = 1 },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual(2, calls, "null completion repair count");
                AssertContains(result.AssistantText, "empty_response", "null completion error");
                AssertEqual("Planner JSON invalid", session.Messages.Last().Activity.Title, "null completion diagnostic");
            });
        }
    }
}
