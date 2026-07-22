using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Office.Services;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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

        private static void ConversationHistoryAvoidsOfficeTools()
        {
            var route = new OfficeIntentRouter().Route(
                "Что было в первом сообщении нашей переписки?",
                new OfficeSnapshot { Host = "Excel" },
                new ChatSession());
            AssertEqual("conversation_history", route.TaskType, "conversation route");
            AssertTrue(!route.RequiresTool, "conversation history does not require Office tool");
            AssertEqual(ChatModes.Chat, new ChatExecutionModeSelector().Select(
                "Сделай саммари нашего чата и диалога",
                new ChatSession { Mode = ChatModes.Auto },
                "Excel"), "auto mode uses plain chat");
        }

        private static void OutputBudgetReservesPromptSpace()
        {
            var settings = new AppSettings
            {
                ContextWindowOverrideTokens = 65536,
                MaxTokens = 65536
            };
            var messages = new[] { new ChatMessage { Role = "user", Content = new string('я', 10580) } };
            var output = ModelContextBudget.EffectiveOutputTokens(settings, messages);
            AssertTrue(output < 65536, "output is reduced below full context window");
            AssertTrue(output + ModelContextBudget.EstimateMessagesTokens(messages) < 65536, "prompt space remains reserved");

            settings.ContextWindowOverrideTokens = 1048576;
            settings.MaxTokens = 32768;
            settings.Model = "large-context";
            settings.ModelCapabilities["large-context"] = new ModelCapabilitySettings
            {
                MaxContextTokens = 1048576,
                MaxOutputTokens = 8192
            };
            AssertEqual(8192, ModelContextBudget.RequestedOutputTokens(settings), "model output limit is separate from context window");
            AssertEqual(16384, ModelContextBudget.SafetyReserveTokens(1048576), "large context safety reserve is bounded");
            AssertEqual(1024000, ModelContextBudget.InputBudgetTokens(settings), "large context keeps almost the whole input window");

            settings.MaxTokens = 32;
            AssertEqual(32, ModelContextBudget.EffectiveOutputTokens(settings, messages), "small requested output is not raised implicitly");
        }

        private static void ChatRunRegistryIsolatesSessions()
        {
            var registry = new ChatRunRegistry();
            var secondCancellation = new CancellationTokenSource();
            var first = new ChatSession { Id = "chat-a", SessionId = "chat-a" };
            var second = new ChatSession { Id = "chat-b", SessionId = "chat-b" };
            registry.Start("chat-a", "run-a", first);
            registry.Start("chat-b", "run-b", second, secondCancellation);
            AssertEqual(2, registry.Sessions().Count, "parallel chat sessions");
            AssertEqual("run-a", registry.Get("chat-a").RunId, "first run isolated");
            AssertEqual("run-b", registry.Get("chat-b").RunId, "second run isolated");
            var duplicateRejected = false;
            try { registry.Start("chat-a", "run-c", first); }
            catch (InvalidOperationException) { duplicateRejected = true; }
            AssertTrue(duplicateRejected, "duplicate run rejected");
            AssertTrue(registry.Cancel("chat-b", "run-b"), "run cancelled by chat and run ids");
            AssertTrue(secondCancellation.IsCancellationRequested, "matching cancellation token signalled");
            AssertTrue(!registry.Cancel("chat-b", "wrong-run"), "wrong run id is not cancelled");
            registry.Complete("chat-a", "run-a");
            AssertTrue(!registry.IsRunning("chat-a") && registry.IsRunning("chat-b"), "completion only removes matching chat");
            registry.Complete("chat-b", "run-b");
        }

        private static void HtmlNetworkOriginRequiresPermission()
        {
            var settings = new AppSettings();
            var service = new HtmlNetworkService(() => settings, value => settings = value);
            var denied = false;
            try
            {
                service.FetchAsync(new HtmlFetchRequest { Url = "https://example.test/data", Method = "GET" }, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (UnauthorizedAccessException) { denied = true; }
            AssertTrue(denied, "origin denied by default");
            AssertEqual("https://example.test", service.AllowOrigin("https://example.test/path"), "origin normalized");
            AssertEqual(1, settings.HtmlNetworkAllowedOrigins.Count, "origin persisted once");
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
                    MaxTokens = 2048,
                    AutoCompressContext = false
                },
                null);
            var text = FlattenMessages(prompt);
            AssertContains(text, "current", "current request retained");
            AssertContains(text, "RECENT_AFTER_LARGE", "newest fitting history retained");
            AssertTrue(text.IndexOf("OLD_SMALL_SENTINEL", StringComparison.Ordinal) < 0, "history remains contiguous after overflow");
        }

        private static void PromptBudgetCompressesEarlierHistory()
        {
            var builder = new ChatContextWindowBuilder();
            var session = new ChatSession();
            session.Messages.Add(new ChatMessage { Role = "user", Content = "OLD_SMALL_SENTINEL" });
            session.Messages.Add(new ChatMessage { Role = "assistant", Content = "old answer" });
            session.Messages.Add(new ChatMessage { Role = "user", Content = new string('x', 12000) });
            session.Messages.Add(new ChatMessage { Role = "assistant", Content = "RECENT_AFTER_LARGE" });
            session.Messages.Add(new ChatMessage { Role = "user", Content = "current" });
            var settings = new AppSettings
            {
                SystemPromptRole = "system",
                ContextWindowOverrideTokens = 4096,
                MaxTokens = 2048,
                AutoCompressContext = true
            };

            var prompt = builder.BuildPlainMessages("current", session, new DocumentContext(), settings, null);
            var text = FlattenMessages(prompt);
            AssertContains(text, "COMPRESSED_EARLIER_CONVERSATION", "compressed history marker");
            AssertContains(text, "OLD_SMALL_SENTINEL", "older history retained compactly");
            AssertContains(text, "RECENT_AFTER_LARGE", "recent history retained verbatim");
            AssertTrue(
                new PromptBudgetComposer().EstimateMessages(prompt) <= ModelContextBudget.InputBudgetTokens(settings),
                "compressed prompt remains within budget");
        }

        private static void ModelCatalogUsesExplicitUrlAndStandardDataShape()
        {
            var settings = new AppSettings
            {
                BaseUrl = "https://api.example.test/v1",
                ModelsConfigUrl = " https://catalog.example.test/models "
            };
            AssertEqual(
                "https://catalog.example.test/models",
                LlmClient.BuildModelsConfigUrl(settings),
                "explicit model catalog url");
            AssertEqual(
                "https://api.example.test/config/models.json",
                LlmClient.BuildModelsConfigUrl(new AppSettings { BaseUrl = "https://api.example.test/v1" }),
                "derived model catalog url fallback");

            var catalog = JArray.Parse("[{\"id\":\"model-a\",\"display_name\":\"Model A\",\"context_window\":1048576,\"max_completion_tokens\":8192,\"supports_reasoning\":true,\"supports_vision\":true,\"supports_audio\":false}]");
            AssertTrue(ModelCapabilityService.Merge(settings, catalog), "standard data catalog merged");
            AssertEqual(1048576, settings.ModelCapabilities["model-a"].MaxContextTokens.Value, "model context parsed");
            AssertEqual(8192, settings.ModelCapabilities["model-a"].MaxOutputTokens.Value, "model output parsed separately");
            AssertTrue(settings.ModelCapabilities["model-a"].SupportsImages == true, "model vision parsed");
            AssertTrue(settings.ModelCapabilities["model-a"].SupportsReasoning == true, "model reasoning parsed");
            AssertTrue(settings.ModelCapabilities["model-a"].SupportsAudio == false, "model audio parsed");
        }

        private static void VbaCreationRouteAllowsMutation()
        {
            var route = new OfficeIntentRouter().Route(
                "Создай новый VBA-модуль и добавь в него макрос.",
                new OfficeSnapshot { Host = "Excel" });

            AssertEqual("vba", route.TaskType, "vba task type");
            AssertEqual("mutate_vba", route.Mode, "vba mutation mode");
            AssertEqual(AgentPhases.Mutation, route.Phase, "vba creation phase");
            AssertEqual(3, route.RiskAllowed, "vba risk allowance");
            AssertTrue(!route.RequiresInspection, "new vba module does not require prior module inspection");

            var tools = new List<ToolDefinition>(FakeOfficeAdapter.ForHost("Excel").GetBuiltInTools());
            var slice = new ToolCatalogSlicer().Slice(route, tools, new List<AgentObservation>());
            AssertTrue(slice.Find("excel.insert_vba_module") != null, "insert vba module is available");

            var macroRoute = new OfficeIntentRouter().Route("Запусти макрос Module1.Test", new OfficeSnapshot { Host = "Excel" });
            AgentPhaseController.Advance(macroRoute, new List<AgentObservation>
            {
                new AgentObservation { Status = "success", ToolId = "excel.vba_read_project", Purpose = AgentObservationPurposes.Inspection }
            }, false);
            AssertTrue(new ToolCatalogSlicer().Slice(macroRoute, tools, new List<AgentObservation>()).Find("excel.run_macro") != null, "run macro is available after inspection");
        }

        private static void DestructiveChartRouteAdvancesToMutation()
        {
            var route = new OfficeIntentRouter().Route(
                "Удали лишние графики, оставь один.",
                new OfficeSnapshot { Host = "Excel" });
            var tools = new List<ToolDefinition>
            {
                new ToolDefinition { Id = "excel.list_charts", Host = "Excel", Enabled = true, AgentCanRun = true },
                new ToolDefinition { Id = "excel.delete_chart", Host = "Excel", Enabled = true, MutatesDocument = true, AgentCanRun = false, RiskLevel = 3 }
            };

            AssertEqual("chart", route.TaskType, "destructive target remains chart-specific");
            AssertEqual(AgentPhases.ReadOnly, route.Phase, "destructive request inspects first");
            AssertTrue(new ToolCatalogSlicer().Slice(route, tools, new List<AgentObservation>()).Find("excel.delete_chart") == null, "delete hidden before inspection");

            AgentPhaseController.Advance(route, new List<AgentObservation>
            {
                new AgentObservation { Status = "success", ToolId = "excel.list_charts", Purpose = AgentObservationPurposes.Inspection }
            }, false);

            AssertEqual(AgentPhases.Mutation, route.Phase, "destructive route advances to mutation");
            AssertEqual(3, route.RiskAllowed, "destructive route raises risk allowance");
            AssertTrue(new ToolCatalogSlicer().Slice(route, tools, new List<AgentObservation>()).Find("excel.delete_chart") != null, "delete capability available after inspection");
        }

        private static void ShortFollowUpContinuesPendingAgentTask()
        {
            var session = new ChatSession
            {
                Mode = ChatModes.Auto,
                PendingAgentTask = new PendingAgentTask
                {
                    Request = "Создай новый лист, график и VBA-макрос.",
                    LastQuestion = "Подтвердите выполнение.",
                    Kind = AgentResponseKinds.Clarify,
                    UpdatedUtc = DateTime.UtcNow
                }
            };

            AssertEqual(ChatModes.Agent, new ChatExecutionModeSelector().Select("да именно так", session, "Excel"), "auto mode keeps pending agent route");
            var resolved = AgentTaskContinuationResolver.Resolve("да именно так", session);
            AssertContains(resolved, "Создай новый лист", "original task retained");
            AssertContains(resolved, "USER_FOLLOW_UP", "follow-up marker included");
            AssertContains(resolved, "да именно так", "follow-up content included");

            AssertEqual("Новая независимая задача", AgentTaskContinuationResolver.Resolve("Новая независимая задача", session), "substantive request is not merged");
            AssertTrue(session.PendingAgentTask == null, "new request clears pending task");
        }

        private static void ToolValidationExplainsUnknownAndExcludedTools()
        {
            var insert = new ToolDefinition
            {
                Id = "excel.insert_vba_module",
                Host = "Excel",
                Enabled = true,
                MutatesDocument = true,
                RiskLevel = 3
            };
            var tools = new List<ToolDefinition> { insert };
            var mutationRoute = new RoutedTask
            {
                App = "Excel",
                TaskType = "vba",
                Mode = "mutate_vba",
                Phase = AgentPhases.Mutation,
                RiskAllowed = 3,
                RequiresTool = true
            };
            var mutationSlice = new ToolCatalogSlicer().Slice(mutationRoute, tools, new List<AgentObservation>());
            var unknown = new AgentActionValidator().Validate(
                new AgentPlannerStep { ToolId = "excel.vba_create_module" },
                mutationSlice,
                mutationRoute,
                new List<AgentObservation>(),
                tools);
            AssertTrue(!unknown.Success, "unknown tool rejected");
            AssertContains(unknown.Message, "Unknown tool id", "unknown diagnostic category");
            AssertContains(unknown.Message, "excel.insert_vba_module", "unknown diagnostic suggestion");

            var readRoute = new RoutedTask
            {
                App = "Excel",
                TaskType = "vba",
                Mode = "mutate_vba",
                Phase = AgentPhases.ReadOnly,
                RiskAllowed = 3,
                RequiresTool = true,
                RequiresInspection = true
            };
            var readSlice = new ToolCatalogSlicer().Slice(readRoute, tools, new List<AgentObservation>());
            var excluded = new AgentActionValidator().Validate(
                new AgentPlannerStep { ToolId = insert.Id },
                readSlice,
                readRoute,
                new List<AgentObservation>(),
                tools);
            AssertTrue(!excluded.Success, "phase-excluded tool rejected");
            AssertContains(excluded.Message, "wrong_phase", "excluded diagnostic reason");
        }

        private static void OptionalToolAuthoringIsExplicitAndDoesNotCompleteDocumentTask()
        {
            var route = new RoutedTask
            {
                App = "Excel",
                TaskType = "chart",
                Mode = "mutate_chart",
                Phase = AgentPhases.Mutation,
                RiskAllowed = 2,
                RequiresTool = true
            };
            var tools = new List<ToolDefinition>
            {
                new ToolDefinition { Id = "excel.update_chart", Host = "Excel", Enabled = true, MutatesDocument = true, RiskLevel = 2 },
                new ToolDefinition { Id = "common.tools_validate", Host = "Common", Enabled = true, RiskLevel = 0 },
                new ToolDefinition { Id = "common.tools_save", Host = "Common", Enabled = true, MutatesLocalState = true, RiskLevel = 1 }
            };

            AssertTrue(new ToolCatalogSlicer().Slice(route, tools, new List<AgentObservation>()).Find("common.tools_save") == null, "tool authoring disabled by default");
            var enabled = new ToolCatalogSlicer().Slice(route, tools, new List<AgentObservation>(), 24, true);
            AssertTrue(enabled.Find("common.tools_validate") != null, "tool validation exposed by option");
            AssertTrue(enabled.Find("common.tools_save") != null, "tool save exposed by option");

            AgentPhaseController.Advance(route, new List<AgentObservation>
            {
                new AgentObservation { Status = "success", ToolId = "common.tools_save", LocalMutation = true, Purpose = AgentObservationPurposes.Mutation }
            }, false);
            AssertEqual(AgentPhases.Mutation, route.Phase, "saving helper tool does not complete chart task");
        }

    }
}
