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
        private static void ChatAgentDisabledSkipsToolBlock()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var service = ChatServiceWithResponses(adapter, executor, null, AgentBlock(Command("word.insert_text", "text", "Hello")));
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "Insert text into the document.",
                    session,
                    NewContext(adapter),
                    new AppSettings { AgentModeEnabled = false, AutoConfirmToolActions = true, ContextCharLimit = 8000 },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual(0, adapter.Executed.Count, "adapter execution count");
                AssertEqual(0, result.ToolResults.Count, "tool result count");
                AssertContains(result.AssistantText, "Agent mode is disabled", "assistant text");
                AssertTrue(ContainsMessage(session.Messages, "Agent mode is disabled"), "disabled message recorded");
            });
        }

        private static void ChatWaitingToolGetsPendingId()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var pendingIds = new List<string>();
                var service = ChatServiceWithResponses(adapter, executor, null, AgentBlock(Command("word.vba_replace_module", "moduleName", "Module1", "code", "Sub Test()\nEnd Sub")));
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "Replace a VBA module.",
                    session,
                    NewContext(adapter),
                    new AppSettings { AgentModeEnabled = true, AutoConfirmToolActions = false, ContextCharLimit = 8000 },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null,
                    delegate(ChatSession pendingSession, ToolCommand pendingCommand, ToolResult pendingResult)
                    {
                        AssertEqual(ChatStore.GetSessionId(session), ChatStore.GetSessionId(pendingSession), "pending session id");
                        AssertEqual("word.vba_replace_module", pendingCommand.ToolId, "pending tool id");
                        pendingIds.Add("pending-1");
                        return "pending-1";
                    }).GetAwaiter().GetResult();

                AssertEqual(0, adapter.Executed.Count, "adapter execution count");
                AssertEqual(1, pendingIds.Count, "pending count");
                var resultJson = JsonConvert.SerializeObject(result.ToolResults);
                AssertContains(resultJson, "waiting_confirmation", "waiting status");
                AssertContains(resultJson, "pending-1", "pending id");
                AssertTrue(session.Messages.Any(m => m != null && m.Activity != null && string.Equals(m.Activity.PendingId, "pending-1", StringComparison.OrdinalIgnoreCase)), "pending activity");
            });
        }

        private static void ChatWaitingToolStopsBatch()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var pendingIds = new List<string>();
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    null,
                    AgentBlock(
                        Command("word.vba_replace_module", "moduleName", "Module1", "code", "Sub Test()\nEnd Sub"),
                        Command("word.insert_text", "text", "Should not run")));
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "Replace VBA and then insert text.",
                    session,
                    NewContext(adapter),
                    new AppSettings { AgentModeEnabled = true, AutoConfirmToolActions = false, ContextCharLimit = 8000 },
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
                AssertTrue(JsonConvert.SerializeObject(result.ToolResults).IndexOf("word.insert_text", StringComparison.OrdinalIgnoreCase) < 0, "second tool skipped");
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
                    AgentBlock(Command("excel.list_sheets")),
                    AgentBlock(Command("excel.list_sheets")));
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "List sheets repeatedly.",
                    session,
                    NewContext(adapter),
                    new AppSettings { ContextCharLimit = 8000, MaxAgentIterations = 3 },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertContains(result.AssistantText, "Agent executed", "summary text");
                AssertTrue(result.AssistantText.IndexOf("rnassistant-agent", StringComparison.OrdinalIgnoreCase) < 0, "no raw agent block");
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
                    AgentBlock(
                        Command("excel.list_sheets"),
                        Command("excel.workbook_summary")));
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "Inspect workbook repeatedly.",
                    session,
                    NewContext(adapter),
                    new AppSettings { ContextCharLimit = 8000, MaxAgentToolSteps = 1 },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual(1, adapter.Executed.Count, "adapter execution count");
                AssertContains(result.AssistantText, "tool error", "step limit summary");
                AssertTrue(ContainsMessage(session.Messages, "Agent tool step limit exceeded"), "step limit transcript");
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
                    new AppSettings { AutoRunToolCalls = false, ContextCharLimit = 8000 },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual(0, adapter.Executed.Count, "adapter execution count");
                AssertContains(JsonConvert.SerializeObject(result.ToolResults), "Auto tool execution is disabled", "auto-run result");
                AssertTrue(ContainsMessage(session.Messages, "Agent plan"), "plan recorded");
                AssertTrue(ContainsMessage(session.Messages, "waiting"), "waiting recorded");
            });
        }

        private static void ChatMalformedToolResponseStaysProse()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("PowerPoint"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var malformed = "I tried this but it is broken:\n```rnassistant-agent\n{\"steps\":[\n```\nExtra noisy text.";
                var service = ChatServiceWithResponses(adapter, executor, calls, malformed);
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "Summarize the presentation.",
                    session,
                    NewContext(adapter),
                    new AppSettings { AgentModeEnabled = false, ContextCharLimit = 8000 },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual(malformed, result.AssistantText, "assistant text");
                AssertEqual(0, adapter.Executed.Count, "adapter execution count");
                AssertEqual(2, session.Messages.Count, "session message count");
                AssertEqual(malformed, session.Messages[1].Content, "assistant transcript");
            });
        }
    }
}
