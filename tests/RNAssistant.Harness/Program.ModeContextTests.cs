using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using Newtonsoft.Json;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ModesSelectChatAutoAndAgent()
        {
            var selector = new ChatExecutionModeSelector();
            var session = new ChatSession { Mode = ChatModes.Chat };
            AssertEqual(ChatModes.Chat, selector.Select("write values into A1", session, "Excel"), "explicit chat");

            session.Mode = ChatModes.Agent;
            AssertEqual(ChatModes.Agent, selector.Select("hello", session, "Excel"), "explicit agent");

            session.Mode = ChatModes.Auto;
            AssertEqual(ChatModes.Chat, selector.Select("explain pivot tables", session, "Excel"), "auto plain question");
            AssertEqual(ChatModes.Agent, selector.Select("write values into A1", session, "Excel"), "auto office action");

            session.Mode = ChatModes.Chat;
            session.HtmlModeEnabled = true;
            AssertEqual(ChatModes.Agent, selector.Select("hello", session, "Excel"), "html forces agent");
        }

        private static void LegacySessionDefaultsToChatMode()
        {
            var session = JsonConvert.DeserializeObject<ChatSession>(
                "{\"Id\":\"legacy\",\"Messages\":[]}");
            AssertEqual(ChatModes.Chat, ChatModes.Normalize(session.Mode), "legacy mode");
        }

        private static void PlainChatOmitsPlannerAndActivities()
        {
            IReadOnlyList<ChatMessage> captured = null;
            var service = new PlainChatService(
                delegate(AppSettings settings, IEnumerable<ChatMessage> messages, Action<LlmStreamUpdate> progress, CancellationToken cancellationToken)
                {
                    captured = messages.ToList();
                    return Task.FromResult(new LlmCompletionResult { Content = "plain answer", PromptTokens = 12 });
                });
            var session = new ChatSession();
            session.Messages.Add(new ChatMessage { Role = "user", Content = "old question" });
            session.Messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = "tool diagnostic",
                Activity = new ChatActivity { Kind = "diagnostic" }
            });
            session.Messages.Add(new ChatMessage { Role = "assistant", Content = "old answer" });

            var result = service.ExecuteAsync(
                "new question",
                session,
                new DocumentContext(),
                new AppSettings { SystemPrompt = "Helpful assistant", SystemPromptRole = "system" },
                null,
                null,
                CancellationToken.None).GetAwaiter().GetResult();

            var prompt = FlattenMessages(captured);
            AssertEqual("plain answer", result.AssistantText, "plain result");
            AssertContains(prompt, "new question", "current request");
            AssertContains(prompt, "old question", "history");
            AssertTrue(prompt.IndexOf("tool_plan", StringComparison.OrdinalIgnoreCase) < 0, "planner protocol omitted");
            AssertTrue(prompt.IndexOf("tool diagnostic", StringComparison.OrdinalIgnoreCase) < 0, "activity omitted");
        }

        private static void DeletedMessageIsAbsentFromRebuiltContext()
        {
            var builder = new ChatContextWindowBuilder();
            var session = new ChatSession();
            session.Messages.Add(new ChatMessage { Role = "user", Content = "keep user" });
            session.Messages.Add(new ChatMessage { Role = "assistant", Content = "remove me" });
            session.Messages.Add(new ChatMessage { Role = "assistant", Content = "keep assistant" });
            session.Messages.RemoveAt(1);
            session.Messages.Add(new ChatMessage { Role = "user", Content = "current" });

            var prompt = builder.BuildPlainMessages(
                "current",
                session,
                new DocumentContext(),
                new AppSettings { SystemPromptRole = "system" },
                null);
            var text = FlattenMessages(prompt);
            AssertContains(text, "keep user", "remaining user");
            AssertContains(text, "keep assistant", "remaining assistant");
            AssertTrue(text.IndexOf("remove me", StringComparison.Ordinal) < 0, "deleted message absent");
        }

        private static void RequiredEmptyToolSliceStopsBeforeLlm()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = 0;
                var runner = new AgentRunService(
                    adapter,
                    executor,
                    delegate(AppSettings settings, IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
                    {
                        calls += 1;
                        return Task.FromResult(new LlmCompletionResult { Content = FinalBlock("unexpected") });
                    },
                    false);
                var service = new ChatCompletionService(runner);
                var session = NewSession(adapter);
                var result = service.ExecuteAsync(
                    "write values into A1",
                    session,
                    NewContext(adapter),
                    new AppSettings(),
                    new List<ToolDefinition>(),
                    null).GetAwaiter().GetResult();

                AssertEqual(0, calls, "llm not called");
                AssertContains(result.AssistantText, "Нет доступного", "local diagnostic");
                AssertTrue(session.Messages.Any(message =>
                    message.Activity != null &&
                    message.Activity.ExecutionStatus == "no_available_tools"), "diagnostic activity");
            });
        }

    }
}
