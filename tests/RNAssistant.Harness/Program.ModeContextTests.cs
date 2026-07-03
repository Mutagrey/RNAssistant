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
            AssertEqual(ChatModes.Agent, selector.Select("объясни содержимое текущего документа", session, "Word"), "auto current document explanation");

            session.Mode = ChatModes.Chat;
            session.HtmlModeEnabled = true;
            AssertEqual(ChatModes.Agent, selector.Select("hello", session, "Excel"), "html forces agent");
        }

        private static void MissingSessionModeDefaultsToChat()
        {
            var session = JsonConvert.DeserializeObject<ChatSession>(
                "{\"Id\":\"legacy\",\"Messages\":[]}");
            AssertEqual(ChatModes.Chat, ChatModes.Normalize(session.Mode), "missing mode");
        }

        private static void PlainChatOmitsPlannerAndActivities()
        {
            IReadOnlyList<ChatMessage> captured = null;
            string capturedModel = null;
            var service = new PlainChatService(
                delegate(AppSettings settings, IEnumerable<ChatMessage> messages, Action<LlmStreamUpdate> progress, CancellationToken cancellationToken)
                {
                    capturedModel = settings.Model;
                    captured = messages.ToList();
                    return Task.FromResult(new LlmCompletionResult { Content = "plain answer", PromptTokens = 12 });
                });
            var session = new ChatSession { Model = "vision-model" };
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
                new AppSettings
                {
                    Model = "default-model",
                    SystemPrompt = "PLANNER_ONLY",
                    ChatSystemPrompt = "Helpful chat assistant",
                    SystemPromptRole = "system"
                },
                null,
                null,
                CancellationToken.None).GetAwaiter().GetResult();

            var prompt = FlattenMessages(captured);
            AssertEqual("plain answer", result.AssistantText, "plain result");
            AssertEqual("vision-model", capturedModel, "chat model applied");
            AssertContains(prompt, "new question", "current request");
            AssertContains(prompt, "old question", "history");
            AssertContains(prompt, "Helpful chat assistant", "chat-specific prompt");
            AssertTrue(prompt.IndexOf("PLANNER_ONLY", StringComparison.OrdinalIgnoreCase) < 0, "agent base prompt omitted");
            AssertTrue(prompt.IndexOf("tool_plan", StringComparison.OrdinalIgnoreCase) < 0, "planner protocol omitted");
            AssertTrue(prompt.IndexOf("tool diagnostic", StringComparison.OrdinalIgnoreCase) < 0, "activity omitted");
        }

        private static void PlainChatRepairsThoughtOnlyJson()
        {
            var calls = 0;
            IReadOnlyList<ChatMessage> repairMessages = null;
            var service = new PlainChatService(
                delegate(AppSettings settings, IEnumerable<ChatMessage> messages, Action<LlmStreamUpdate> progress, CancellationToken cancellationToken)
                {
                    calls += 1;
                    if (calls == 1)
                    {
                        return Task.FromResult(new LlmCompletionResult
                        {
                            Content = "{\"thought\":\"Нужно поприветствовать пользователя.\"}"
                        });
                    }
                    repairMessages = messages.ToList();
                    return Task.FromResult(new LlmCompletionResult { Content = "Здравствуйте! Чем могу помочь?" });
                });
            var session = new ChatSession();

            var result = service.ExecuteAsync(
                "Привет",
                session,
                new DocumentContext(),
                new AppSettings(),
                null,
                null,
                CancellationToken.None).GetAwaiter().GetResult();

            AssertEqual(2, calls, "thought-only response repaired once");
            AssertEqual("Здравствуйте! Чем могу помочь?", result.AssistantText, "repaired plain answer");
            AssertTrue(result.AssistantText.IndexOf("thought", StringComparison.OrdinalIgnoreCase) < 0, "thought hidden");
            AssertContains(FlattenMessages(repairMessages), "Do not return JSON", "repair instruction");
            AssertEqual(2, session.Messages.Count, "only user and final assistant persisted");

            var failedRepairService = new PlainChatService(
                delegate(AppSettings settings, IEnumerable<ChatMessage> messages, Action<LlmStreamUpdate> progress, CancellationToken cancellationToken)
                {
                    return Task.FromResult(new LlmCompletionResult { Content = "{\"thought\":\"still internal\"}" });
                });
            var failedRepair = failedRepairService.ExecuteAsync(
                "Привет",
                new ChatSession(),
                new DocumentContext(),
                new AppSettings(),
                null,
                null,
                CancellationToken.None).GetAwaiter().GetResult();
            AssertContains(failedRepair.AssistantText, "не вернула пользовательский ответ", "failed repair returns safe message");
            AssertTrue(failedRepair.AssistantText.IndexOf("still internal", StringComparison.OrdinalIgnoreCase) < 0, "failed repair thought hidden");
        }

        private static void ImageSwitchesToCompatibleModel()
        {
            var settings = new AppSettings { Model = "default-vision" };
            settings.ModelCapabilities["default-vision"] = new ModelCapabilitySettings { SupportsImages = true };
            settings.ModelCapabilities["text-only"] = new ModelCapabilitySettings { SupportsImages = false };
            var session = new ChatSession { Model = "text-only" };
            session.Messages.Add(new ChatMessage
            {
                Role = "user",
                Attachments = new List<ChatAttachment>
                {
                    new ChatAttachment { Kind = "image", FileName = "clipboard.png" }
                }
            });

            AssertTrue(
                ChatCompletionService.EnsureImageCompatibleModel(settings, session, null),
                "image model changed");
            AssertEqual("default-vision", settings.Model, "request model");
            AssertEqual("default-vision", session.Model, "session model");

            var unknownSettings = new AppSettings { Model = "custom-model" };
            var unknownSession = new ChatSession { Model = "custom-model" };
            AssertTrue(
                !ChatCompletionService.EnsureImageCompatibleModel(
                    unknownSettings,
                    unknownSession,
                    new[] { new ChatAttachment { Kind = "image" } }),
                "unknown image support left for endpoint");
            AssertEqual("custom-model", unknownSettings.Model, "unknown model retained");
        }

        private static void PlainChatExtractsAnswerWithoutThought()
        {
            var calls = 0;
            var service = new PlainChatService(
                delegate(AppSettings settings, IEnumerable<ChatMessage> messages, Action<LlmStreamUpdate> progress, CancellationToken cancellationToken)
                {
                    calls += 1;
                    return Task.FromResult(new LlmCompletionResult
                    {
                        Content = "{\"thought\":\"internal\",\"answer\":\"Готовый ответ.\"}"
                    });
                });

            var result = service.ExecuteAsync(
                "Ответь",
                new ChatSession(),
                new DocumentContext(),
                new AppSettings(),
                null,
                null,
                CancellationToken.None).GetAwaiter().GetResult();

            AssertEqual(1, calls, "embedded answer avoids repair request");
            AssertEqual("Готовый ответ.", result.AssistantText, "user-facing answer extracted");
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
                var diagnostic = session.Messages.Last(message => message.Activity != null).Activity;
                AssertContains(diagnostic.DataJson, "excludedCounts", "tool diagnostics");
            });
        }

        private static void ToolSliceBalancesMutationAndInspection()
        {
            var tools = new List<ToolDefinition>();
            for (var index = 0; index < 40; index++)
            {
                tools.Add(new ToolDefinition
                {
                    Id = "excel.write_" + index,
                    Host = "Excel",
                    MutatesDocument = true,
                    RiskLevel = 2
                });
            }
            tools.Add(new ToolDefinition { Id = "excel.read_range", Host = "Excel", MutatesDocument = false });
            tools.Add(new ToolDefinition { Id = "excel.get_context", Host = "Excel", MutatesDocument = false });
            tools.Add(new ToolDefinition { Id = "word.read_document", Host = "Word", MutatesDocument = false });
            tools.Add(new ToolDefinition { Id = "excel.confirm_mutation", Host = "Excel", MutatesDocument = true, RiskLevel = 2, AgentCanRun = false });
            tools.Add(new ToolDefinition { Id = "excel.unavailable", Host = "Excel", CapabilityStatus = "unavailable" });

            var slice = new ToolCatalogSlicer().Slice(
                new RoutedTask
                {
                    App = "Excel",
                    TaskType = "content",
                    Phase = AgentPhases.Mutation,
                    RiskAllowed = 2,
                    RequiresTool = true
                },
                tools,
                new List<AgentObservation>());

            AssertEqual(24, slice.Tools.Count, "balanced slice size");
            AssertTrue(slice.Tools.Any(tool => tool.Id == "excel.read_range"), "read tool retained");
            AssertTrue(slice.Tools.Any(tool => tool.Id == "excel.get_context"), "context tool retained");
            AssertTrue(slice.Tools.Any(tool => tool.Id == "excel.confirm_mutation"), "confirmation-only mutation retained");
            AssertTrue(slice.Excluded.Any(item => item.ToolId == "word.read_document" && item.Reason == "wrong_host"), "wrong host reason");
            AssertTrue(slice.Excluded.Any(item => item.ToolId == "excel.unavailable" && item.Reason == "capability_unavailable"), "capability reason");
            AssertTrue(slice.Excluded.Any(item => item.Reason == "selection_limit"), "selection limit reason");

            var compact = new ToolCatalogSlicer().Slice(
                new RoutedTask
                {
                    App = "Excel",
                    TaskType = "content",
                    Phase = AgentPhases.Mutation,
                    RiskAllowed = 2,
                    RequiresTool = true
                },
                tools,
                new List<AgentObservation>(),
                8);
            AssertEqual(8, compact.Tools.Count, "compact slice size");
            AssertTrue(compact.Tools.Any(tool => !tool.MutatesDocument), "compact slice keeps inspection");
            AssertTrue(compact.Tools.Any(tool => tool.MutatesDocument), "compact slice keeps mutation");
        }

        private static void PromptBudgetKeepsContiguousRecentHistory()
        {
            var builder = new ChatContextWindowBuilder();
            var session = new ChatSession();
            session.Messages.Add(new ChatMessage { Role = "user", Content = "OLD_SMALL_SENTINEL" });
            session.Messages.Add(new ChatMessage { Role = "assistant", Content = "old answer" });
            session.Messages.Add(new ChatMessage { Role = "user", Content = new string('x', 12000) });
            session.Messages.Add(new ChatMessage { Role = "assistant", Content = "RECENT_AFTER_LARGE" });
            session.Messages.Add(new ChatMessage { Role = "user", Content = "current" });

            var prompt = builder.BuildPlainMessages(
                "current",
                session,
                new DocumentContext(),
                new AppSettings
                {
                    SystemPromptRole = "system",
                    ContextWindowOverrideTokens = 4096,
                    MaxTokens = 2048
                },
                null);
            var text = FlattenMessages(prompt);
            AssertContains(text, "current", "current request retained");
            AssertContains(text, "RECENT_AFTER_LARGE", "newest fitting history retained");
            AssertTrue(text.IndexOf("OLD_SMALL_SENTINEL", StringComparison.Ordinal) < 0, "history remains contiguous after overflow");
        }

    }
}
