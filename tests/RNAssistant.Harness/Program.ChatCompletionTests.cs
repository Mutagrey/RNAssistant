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
        private static void LlmStreamingResponseIsAggregated()
        {
            var sse =
                "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"Check range. \"}}]}\\n\\n" +
                "data: {\"choices\":[{\"delta\":{\"reasoning\":\"Use tool.\"}}]}\\n\\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"Hello \"}}]}\\n\\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"world\"}}]}\\n\\n" +
                "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"excel.read_range\",\"arguments\":\"{\\\"address\\\":\"}}]}}]}\\n\\n" +
                "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\"A1:B2\\\"}\"}}]}}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5,\"total_tokens\":15,\"completion_tokens_details\":{\"reasoning_tokens\":2}}}\\n\\n" +
                "data: [DONE]\\n\\n";

            var result = LlmClient.ParseStreamingResponse(sse.Replace("\\n", "\n"));

            AssertContains(result.Content, "\"kind\":\"tool_plan\"", "stream tool plan");
            AssertContains(result.Content, "excel.read_range", "stream tool name");
            AssertContains(result.Content, "A1:B2", "stream tool arguments");
            AssertEqual("Check range. Use tool.", result.ReasoningContent, "stream reasoning");
            AssertEqual(2, result.ReasoningTokens.Value, "stream reasoning tokens");
            AssertEqual(10, result.PromptTokens.Value, "stream prompt tokens");
            AssertEqual(5, result.CompletionTokens.Value, "stream completion tokens");
            AssertEqual(15, result.TotalTokens.Value, "stream total tokens");
        }

        private static void LlmThinkTagsAreSeparated()
        {
            var result = LlmClient.ParseStreamingResponse(
                "data: {\"choices\":[{\"delta\":{\"content\":\"<think>check facts</think>{\\\"kind\\\":\\\"final\\\",\\\"intent\\\":\\\"answer\\\",\\\"message\\\":\\\"ok\\\",\\\"steps\\\":[]}\"}}]}\n\ndata: [DONE]\n\n");
            AssertEqual("check facts", result.ReasoningContent, "think tag reasoning");
            AssertTrue(result.Content.StartsWith("{\"kind\":\"final\"", StringComparison.Ordinal), "think tag final content");
        }

        private static void ChatPlannerIncludesRecentHistory()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var captured = new List<ChatMessage>();
                var service = new ChatCompletionService(
                    adapter,
                    executor,
                    delegate(AppSettings settings, IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
                    {
                        captured = new List<ChatMessage>(messages);
                        return Task.FromResult(new LlmCompletionResult
                        {
                            Content = "{\"kind\":\"final\",\"intent\":\"answer\",\"message\":\"done\",\"steps\":[]}"
                        });
                    });
                var session = NewSession(adapter);
                session.Messages.Add(new ChatMessage { Role = "user", Content = "Earlier question" });
                session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Earlier answer" });
                service.ExecuteAsync(
                    "Follow up",
                    session,
                    NewContext(adapter),
                    new AppSettings { ContextWindowOverrideTokens = 32768 },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertTrue(ContainsMessage(captured, "Earlier question"), "recent user history");
                AssertTrue(ContainsMessage(captured, "Earlier answer"), "recent assistant history");
            });
        }

        private static void ChatCompletionServiceRecordsProseResponse()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var capturedMessages = new List<ChatMessage>();
                var service = new ChatCompletionService(
                    adapter,
                    executor,
                    delegate(AppSettings settings, IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        capturedMessages = new List<ChatMessage>(messages ?? new ChatMessage[0]);
                        return Task.FromResult(new LlmCompletionResult
                        {
                            Content = FinalBlock("Done."),
                            PromptTokens = 10,
                            CompletionTokens = 2,
                            TotalTokens = 12
                        });
                    });

                var session = new ChatSession
                {
                    Host = "Excel",
                    DocumentKey = "doc",
                    DocumentTitle = "Harness.xlsx",
                    Title = "New chat"
                };
                var context = new DocumentContext
                {
                    Host = "Excel",
                    DocumentKey = "doc",
                    Title = "Harness.xlsx"
                };
                context.Notes.Add(new ContextNote
                {
                    Host = "Excel",
                    Kind = "selection",
                    Title = "Selection",
                    Reference = "A1",
                    Text = "Selected cells"
                });

                var result = service.ExecuteAsync(
                    "hello world",
                    session,
                    context,
                    new AppSettings { ContextCharLimit = 8000 },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Done.", result.AssistantText, "assistant text");
                AssertEqual(2, session.Messages.Count, "session message count");
                AssertEqual("hello world", session.Messages[0].Content, "user message");
                AssertEqual("Done.", session.Messages[1].Content, "assistant message");
                AssertEqual("New chat", session.Title, "session title");
                AssertTrue(ContainsMessage(capturedMessages, "User-added context:"), "context prompt captured");
            });
        }

        private static void ChatIncludesVbaContextWhenEnabled()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.SetVbaModule("Module1", "Sub Main()\nEnd Sub", "StdModule");
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var service = ChatServiceWithResponses(adapter, executor, calls, "Done.");
                var session = NewSession(adapter);

                service.ExecuteAsync(
                    "Analyze this workbook.",
                    session,
                    NewContext(adapter),
                    new AppSettings { ContextCharLimit = 8000, IncludeVbaContext = true },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertTrue(calls.Count > 0, "llm call count");
                AssertContains(FlattenMessages(calls[0]), "Current VBA project snapshot", "vba prompt section");
                AssertContains(FlattenMessages(calls[0]), "Module1", "vba module name");
            });
        }

        private static void ChatVbaTaskAutoIncludesVbaContext()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.SetVbaModule("Module1", "Sub ExistingMacro()\nEnd Sub", "StdModule");
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var service = ChatServiceWithResponses(adapter, executor, calls, "Done.");
                var session = NewSession(adapter);

                service.ExecuteAsync(
                    "Review the VBA macro before changing it.",
                    session,
                    NewContext(adapter),
                    new AppSettings { ContextCharLimit = 8000, IncludeVbaContext = false },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertTrue(calls.Count > 0, "llm call count");
                AssertContains(FlattenMessages(calls[0]), "Current VBA project snapshot", "auto vba prompt section");
                AssertContains(FlattenMessages(calls[0]), "Module1", "auto vba module name");
            });
        }

        private static void ChatCompletionServiceUsesDeferredSmartTitleSetting()
        {
            var requestedMessages = new List<ChatMessage>();
            var title = ChatTitleBuilder.GenerateLlmTitleAsync(
                new AppSettings { ContextCharLimit = 8000 },
                "Нужно сделать отчет по продажам.",
                "Отчет по продажам создан и сохранен.",
                delegate(AppSettings settings, IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    requestedMessages = new List<ChatMessage>(messages ?? new ChatMessage[0]);
                    AssertEqual(32, settings.MaxTokens, "title max tokens");
                    return Task.FromResult(new LlmCompletionResult { Content = "Продажи по месяцам." });
                },
                CancellationToken.None).GetAwaiter().GetResult();

            AssertEqual("Продажи по месяцам", title, "llm title");
            AssertTrue(ContainsMessage(requestedMessages, "Запрос пользователя"), "title prompt contains user label");

            var fallbackSession = new ChatSession { Title = "New chat" };
            ChatTitleBuilder.ApplyFallback(
                fallbackSession,
                "Нужно сделать отчет по продажам.",
                "Отчет по продажам создан и сохранен.");
            AssertEqual("Отчет по продажам создан и сохранен", fallbackSession.Title, "fallback title");
        }
    }
}
