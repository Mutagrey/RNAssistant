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
        private static void ModesSelectChatAndAgent()
        {
            var selector = new ChatExecutionModeSelector();
            var session = new ChatSession { Mode = ChatModes.Chat };
            AssertEqual(ChatModes.Chat, selector.Select("write values into A1", session), "explicit chat");

            session.Mode = ChatModes.Agent;
            AssertEqual(ChatModes.Agent, selector.Select("hello", session), "explicit agent");
            AssertEqual(ChatModes.Agent, selector.Select("hello", null), "missing session defaults to agent");

            session.Mode = ChatModes.Chat;
            session.HtmlModeEnabled = true;
            AssertEqual(ChatModes.Agent, selector.Select("hello", session), "html forces agent");
        }

        private static void PlainChatOmitsPlannerAndActivities()
        {
            IReadOnlyList<ChatMessage> captured = null;
            string capturedModel = null;
            var service = new PlainChatService(
                delegate(AppSettings settings, IEnumerable<ChatMessage> messages, LlmRequestOptions requestOptions, Action<LlmStreamUpdate> progress, CancellationToken cancellationToken)
                {
                    capturedModel = settings.Model;
                    captured = messages.ToList();
                    return Task.FromResult(new LlmCompletionResult { Content = "{\"thought\":\"model text\",\"answer\":\"plain answer\"}", PromptTokens = 12 });
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
            AssertEqual("{\"thought\":\"model text\",\"answer\":\"plain answer\"}", result.AssistantText, "plain chat does not parse content envelopes");
            AssertEqual("vision-model", capturedModel, "chat model applied");
            AssertContains(prompt, "new question", "current request");
            AssertContains(prompt, "old question", "history");
            AssertContains(prompt, "Helpful chat assistant", "chat-specific prompt");
            AssertTrue(prompt.IndexOf("PLANNER_ONLY", StringComparison.OrdinalIgnoreCase) < 0, "agent base prompt omitted");
            AssertTrue(prompt.IndexOf("tool_plan", StringComparison.OrdinalIgnoreCase) < 0, "planner protocol omitted");
            AssertTrue(prompt.IndexOf("tool diagnostic", StringComparison.OrdinalIgnoreCase) < 0, "activity omitted");
        }

        private static void AttachmentRoutingIsRequestScoped()
        {
            var settings = new AppSettings { Model = "global-text" };
            settings.ModelCapabilities["text-only"] = new ModelCapabilitySettings { SupportsImages = false, SupportsAudio = false };
            settings.ModelCapabilities["vision-first"] = new ModelCapabilitySettings { SupportsImages = true, SupportsAudio = false };
            settings.AttachmentModelPriority.Add("vision-first");
            var session = new ChatSession { Model = "text-only" };

            var routed = AttachmentModelRoutingService.Select(
                settings,
                session,
                new[] { new ChatAttachment { Kind = "image", FileName = "clipboard.png" } });
            AssertEqual("vision-first", routed.SelectedModel, "priority vision model");
            AssertEqual("vision-first", routed.Settings.Model, "request copy model");
            AssertEqual("text-only", session.Model, "session model unchanged");
            AssertEqual("global-text", settings.Model, "stored settings unchanged");
            routed.Settings.ModelCapabilities["text-only"].SupportsImages = true;
            routed.Settings.AttachmentModelPriority.Clear();
            AssertEqual(false, settings.ModelCapabilities["text-only"].SupportsImages.Value, "capability settings cloned deeply");
            AssertEqual(1, settings.AttachmentModelPriority.Count, "model priority cloned deeply");

            var text = AttachmentModelRoutingService.Select(settings, session, null);
            AssertEqual("text-only", text.SelectedModel, "next text request uses chat model");
        }

        private static void AttachmentRoutingCoversPdfAndMixedMedia()
        {
            var settings = new AppSettings { Model = "text-only" };
            settings.ModelCapabilities["text-only"] = new ModelCapabilitySettings { SupportsImages = false, SupportsAudio = false };
            settings.ModelCapabilities["vision"] = new ModelCapabilitySettings { SupportsImages = true, SupportsAudio = false };
            settings.ModelCapabilities["audio"] = new ModelCapabilitySettings { SupportsImages = false, SupportsAudio = true };
            settings.ModelCapabilities["both"] = new ModelCapabilitySettings { SupportsImages = true, SupportsAudio = true };
            settings.AttachmentModelPriority.AddRange(new[] { "vision", "audio", "both" });
            var session = new ChatSession { Model = "text-only" };

            var textPdf = AttachmentModelRoutingService.Select(settings, session, new[]
            {
                new ChatAttachment { Kind = "pdf", PageTextLengths = new List<int> { 100 } }
            });
            AssertEqual("text-only", textPdf.SelectedModel, "text pdf stays on base model");

            var scanPdf = AttachmentModelRoutingService.Select(settings, session, new[]
            {
                new ChatAttachment { Kind = "pdf", PageTextLengths = new List<int> { 0 } }
            });
            AssertEqual("vision", scanPdf.SelectedModel, "scanned pdf uses vision priority");

            var audio = AttachmentModelRoutingService.Select(settings, session, new[]
            {
                new ChatAttachment { Kind = "audio" }
            });
            AssertEqual("audio", audio.SelectedModel, "audio uses audio priority");

            var mixed = AttachmentModelRoutingService.Select(settings, session, new[]
            {
                new ChatAttachment { Kind = "image" },
                new ChatAttachment { Kind = "audio" }
            });
            AssertEqual("both", mixed.SelectedModel, "mixed request requires both capabilities");

            settings.AttachmentModelPriority.Remove("both");
            var rejected = false;
            try
            {
                AttachmentModelRoutingService.Select(settings, session, new[]
                {
                    new ChatAttachment { Kind = "image" },
                    new ChatAttachment { Kind = "audio" }
                });
            }
            catch (InvalidOperationException ex)
            {
                rejected = ex.Message.IndexOf("Vision и Audio", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            AssertTrue(rejected, "mixed request rejected without combined model");

            var ambiguous = new AppSettings { Model = "unknown-base" };
            ambiguous.ModelCapabilities["vision-a"] = new ModelCapabilitySettings { SupportsImages = true };
            ambiguous.ModelCapabilities["vision-b"] = new ModelCapabilitySettings { SupportsImages = true };
            rejected = false;
            try
            {
                AttachmentModelRoutingService.Select(
                    ambiguous,
                    new ChatSession { Model = "unknown-base" },
                    new[] { new ChatAttachment { Kind = "image" } });
            }
            catch (InvalidOperationException ex)
            {
                rejected = ex.Message.IndexOf("приоритет", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            AssertTrue(rejected, "empty ambiguous priority requires explicit order");
        }

        private static void OfflineAgentKeepsRequestOptions()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                LlmRequestOptions captured = null;
                var service = new OfflineChatService(
                    executor,
                    delegate(
                        AppSettings settings,
                        IEnumerable<ChatMessage> messages,
                        LlmRequestOptions requestOptions,
                        Action<LlmStreamUpdate> streamProgress,
                        CancellationToken cancellationToken)
                    {
                        captured = requestOptions;
                        return Task.FromResult(new LlmCompletionResult { Content = FinalBlock("Done.") });
                    });

                var result = service.ExecuteAsync(
                    "What is a pivot table?",
                    NewSession(adapter),
                    NewContext(adapter),
                    new AppSettings { AgentResponseMode = AgentResponseModes.JsonSchema },
                    new List<ToolDefinition>(),
                    null,
                    null,
                    null,
                    null,
                    CancellationToken.None).GetAwaiter().GetResult();

                AssertEqual("Done.", result.AssistantText, "offline answer");
                AssertTrue(captured != null, "offline request options preserved");
                AssertEqual(LlmResponseFormats.JsonSchema, captured.ResponseFormat, "offline response mode");
                AssertContains(captured.ResponseSchemaJson, "protocolVersion", "offline decision schema");
            });
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
                    delegate(AppSettings settings, IEnumerable<ChatMessage> messages, LlmRequestOptions requestOptions, Action<LlmStreamUpdate> streamProgress, CancellationToken cancellationToken)
                    {
                        calls += 1;
                        return Task.FromResult(new LlmCompletionResult { Content = FinalBlock("No local action is available.") });
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

                AssertEqual(1, calls, "model decides whether the request can be answered without tools");
                AssertEqual("No local action is available.", result.AssistantText, "model terminal response");
                AssertTrue(!session.Messages.Any(message =>
                    message.Activity != null &&
                    message.Activity.ExecutionStatus == "no_available_tools"), "generic agent route is not pre-classified as a tool task");
            });
        }

        private static void ToolSliceBalancesMutationAndInspection()
        {
            var tools = new List<ToolDefinition>();
            for (var index = 0; index < 70; index++)
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
                    RequiresTool = true
                },
                tools);

            AssertEqual(24, slice.Tools.Count, "balanced slice size");
            AssertTrue(slice.Tools.Any(tool => tool.Id == "excel.read_range"), "read tool retained");
            AssertTrue(slice.Tools.Any(tool => tool.Id == "excel.get_context"), "context tool retained");
            AssertTrue(slice.Excluded.Any(item => item.ToolId == "excel.confirm_mutation" && item.Reason == "selection_limit"), "overflow uses a neutral selection limit");
            AssertTrue(slice.Excluded.Any(item => item.ToolId == "word.read_document" && item.Reason == "wrong_host"), "wrong host reason");
            AssertTrue(slice.Excluded.Any(item => item.ToolId == "excel.unavailable" && item.Reason == "capability_unavailable"), "capability reason");
            AssertTrue(slice.Excluded.Any(item => item.Reason == "selection_limit"), "selection limit reason");

            var compact = new ToolCatalogSlicer().Slice(
                new RoutedTask
                {
                    App = "Excel",
                    TaskType = "content",
                    Phase = AgentPhases.Mutation,
                    RequiresTool = true
                },
                tools,
                8);
            AssertEqual(8, compact.Tools.Count, "compact slice size");
            AssertTrue(compact.Tools.Any(tool => !tool.MutatesDocument), "compact slice keeps inspection");
            AssertTrue(compact.Tools.Any(tool => tool.MutatesDocument), "compact slice keeps mutation");

            var expanded = new ToolCatalogSlicer().Slice(
                new RoutedTask
                {
                    App = "Excel",
                    TaskType = "content",
                    Phase = AgentPhases.Mutation,
                    RequiresTool = true
                },
                tools,
                100);
            AssertEqual(73, expanded.Tools.Count, "tool slice has no arbitrary 64 item cap");

            foreach (var tool in tools)
            {
                tool.ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{\"payload\":{\"type\":\"string\",\"description\":\"" + new string('x', 1800) + "\"}},\"required\":[\"payload\"],\"additionalProperties\":false}";
            }
            var budgeted = new ToolCatalogSlicer().Slice(
                new RoutedTask
                {
                    App = "Excel",
                    TaskType = "content",
                    Phase = AgentPhases.Mutation,
                    RequiresTool = true
                },
                tools,
                24,
                new AppSettings { ContextWindowOverrideTokens = 4096 });
            AssertTrue(budgeted.Tools.Count < 24, "large schemas are trimmed by request budget");
            AssertTrue(budgeted.Excluded.Any(item => item.Reason == "request_token_limit"), "schema budget exclusion reason");
        }

        private static void ConversationHistoryAvoidsOfficeTools()
        {
            var route = new AgentRouteResolver().Route(
                new OfficeSnapshot { Host = "Excel" },
                new ChatSession());
            AssertEqual("agent", route.TaskType, "runtime does not classify natural language");
            AssertTrue(!route.RequiresTool, "conversation history does not require Office tool");
            AssertEqual("model_decision", route.DecisionReason, "model owns semantic decision");
            AssertEqual(ChatModes.Agent, new ChatExecutionModeSelector().Select(
                "Сделай саммари нашего чата и диалога",
                new ChatSession { Mode = ChatModes.Agent }), "agent answers conversation history without Office tools");
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
            var first = new ChatSession { Id = "chat-a" };
            var second = new ChatSession { Id = "chat-b" };
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

        private static void PromptBudgetFailsClosedWithoutCompaction()
        {
            var builder = new ChatContextWindowBuilder();
            var session = new ChatSession();
            session.Messages.Add(new ChatMessage { Role = "user", Content = "OLD_SMALL_SENTINEL" });
            session.Messages.Add(new ChatMessage { Role = "assistant", Content = "old answer" });
            session.Messages.Add(new ChatMessage { Role = "user", Content = new string('x', 12000) });
            session.Messages.Add(new ChatMessage { Role = "assistant", Content = "RECENT_AFTER_LARGE" });
            session.Messages.Add(new ChatMessage { Role = "user", Content = "current" });

            var failedClosed = false;
            try
            {
                builder.BuildPlainMessages(
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
            }
            catch (InvalidOperationException ex)
            {
                failedClosed = ex.Message.IndexOf("context compaction", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            AssertTrue(failedClosed, "oversized raw history is never silently trimmed");
            AssertEqual(5, session.Messages.Count, "raw transcript remains intact");
            AssertContains(session.Messages[0].Content, "OLD_SMALL_SENTINEL", "oldest message remains stored");

            var protocolFailedClosed = false;
            try
            {
                new PromptBudgetComposer().AddProtocolHistory(
                    new List<ChatMessage> { new ChatMessage { Role = "system", Content = "root" } },
                    new List<ChatMessage>
                    {
                        new ChatMessage { Role = "assistant", Content = "tool call", ToolCalls = new List<LlmToolCall> { new LlmToolCall { Id = "call_budget", Name = "test.read", ArgumentsJson = "{}" } } },
                        new ChatMessage { Role = "tool", ToolCallId = "call_budget", Content = new string('r', 4000) }
                    },
                    64);
            }
            catch (InvalidOperationException ex)
            {
                protocolFailedClosed = ex.Message.IndexOf("preserved", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            AssertTrue(protocolFailedClosed, "current tool protocol is never silently trimmed");

            var requiredPromptFailedClosed = false;
            var stored = new ChatSession();
            stored.Messages.Add(new ChatMessage { Role = "user", Content = "STORED_HISTORY" });
            try
            {
                new PromptBudgetComposer().AddConversationHistory(
                    new List<ChatMessage> { new ChatMessage { Role = "user", Content = new string('q', 4000) } },
                    0,
                    stored,
                    new AppSettings(),
                    64);
            }
            catch (InvalidOperationException ex)
            {
                requiredPromptFailedClosed = ex.Message.IndexOf("No conversation history was removed", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            AssertTrue(requiredPromptFailedClosed, "oversized required prompt fails before history can disappear");
            AssertEqual("STORED_HISTORY", stored.Messages[0].Content, "stored history remains unchanged after required prompt overflow");
        }

        private static void PromptBudgetUsesCheckpointAndExactTail()
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

            var checkpoint = new ContextCheckpoint
            {
                ThroughMessageId = session.Messages[2].Id,
                SummaryMarkdown = "OLD_SMALL_SENTINEL preserved in model-generated summary.",
                SummaryJson = "{\"summary\":\"OLD_SMALL_SENTINEL preserved\"}"
            };
            session.ContextCheckpoints.Add(checkpoint);
            session.ActiveContextCheckpointId = checkpoint.Id;

            var prompt = builder.BuildPlainMessages("current", session, new DocumentContext(), settings, null);
            var text = FlattenMessages(prompt);
            AssertContains(text, "COMPACTED_EARLIER_CONTEXT", "checkpoint marker");
            AssertContains(text, "OLD_SMALL_SENTINEL", "older history retained compactly");
            AssertContains(text, "RECENT_AFTER_LARGE", "recent history retained verbatim");
            AssertTrue(text.IndexOf(new string('x', 12000), StringComparison.Ordinal) < 0, "raw compacted prefix is not replayed");
            AssertTrue(
                new PromptBudgetComposer().EstimateMessages(prompt) <= ModelContextBudget.InputBudgetTokens(settings),
                "checkpoint prompt remains within budget");
        }

        private static void ContextCompactionCreatesDurableCheckpoint()
        {
            var session = new ChatSession();
            var firstMessage = new ChatMessage { Role = "user", Content = "ORIGINAL_GOAL" };
            firstMessage.Attachments.Add(new ChatAttachment
            {
                Id = "memory-file",
                FileName = "memory.txt",
                Kind = "text",
                ExtractedText = "ATTACHMENT_FACT",
                ExtractedCharCount = 15
            });
            session.Messages.Add(firstMessage);
            session.Messages.Add(new ChatMessage { Role = "assistant", Content = "First answer" });
            session.Messages.Add(new ChatMessage { Role = "user", Content = "Second request" });
            session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Second answer" });
            session.Messages.Add(new ChatMessage { Role = "user", Content = "LATEST_REQUEST" });
            session.ActiveSkillIds.Add("common.test_memory");
            var referencedArtifact = new ChatArtifact { Kind = ChatArtifactKinds.Markdown, Title = "Memory artifact" };
            session.Artifacts.Add(referencedArtifact);
            session.Messages[0].ArtifactIds.Add(referencedArtifact.Id);
            var originalCount = session.Messages.Count;
            var summaryJson = "{\"summary\":\"Continue ORIGINAL_GOAL\",\"goals\":[\"ORIGINAL_GOAL\"],\"requirements\":[],\"decisions\":[],\"verifiedFacts\":[],\"completedActions\":[],\"pendingWork\":[\"LATEST_REQUEST\"],\"blockers\":[],\"stableReferences\":[],\"activeSkills\":[],\"artifactReferences\":[],\"warnings\":[]}";
            List<ChatMessage> compactionRequest = null;
            var compactionCalls = 0;
            var service = new ContextCompactionService((settings, messages, options, progress, cancellationToken) =>
            {
                compactionRequest = messages == null ? null : messages.ToList();
                compactionCalls += 1;
                return Task.FromResult(new LlmCompletionResult
                {
                    Content = compactionCalls == 1 ? summaryJson.TrimEnd('}') + ",\"unexpected\":true}" : summaryJson
                });
            });

            var checkpoint = service.EnsureWithinBudgetAsync(
                session,
                new AppSettings { ContextWindowOverrideTokens = 4096, MaxTokens = 2048 },
                string.Empty,
                true,
                null,
                CancellationToken.None).GetAwaiter().GetResult();

            AssertTrue(checkpoint != null, "checkpoint created");
            AssertEqual(2, compactionCalls, "invalid compaction receives one strict retry");
            AssertEqual(originalCount + 1, session.Messages.Count, "full transcript plus visible compaction event retained");
            AssertEqual("ORIGINAL_GOAL", session.Messages[0].Content, "raw first message retained");
            AssertEqual(checkpoint.Id, session.ActiveContextCheckpointId, "checkpoint activated");
            AssertTrue(session.Artifacts.Any(artifact => artifact.Kind == ChatArtifactKinds.Compaction), "compaction artifact registered");
            AssertContains(FlattenMessages(compactionRequest), "common.test_memory", "active skills supplied to compactor");
            AssertContains(FlattenMessages(compactionRequest), referencedArtifact.Id, "artifact reference supplied to compactor");
            AssertContains(FlattenMessages(compactionRequest), "ATTACHMENT_FACT", "text attachment facts supplied to compactor");
            var active = FlattenMessages(ContextCompactionService.BuildActiveWindow(session));
            AssertContains(active, "Continue ORIGINAL_GOAL", "summary enters active context");
            AssertContains(active, "LATEST_REQUEST", "recent raw tail remains");
            AssertTrue(active.IndexOf("First answer", StringComparison.Ordinal) < 0, "compacted prefix leaves active replay");
        }

        private static void PromptOverflowRetriesAfterModelCompaction()
        {
            var session = new ChatSession();
            session.Messages.Add(new ChatMessage
            {
                Role = "user",
                Content = "RAW_HISTORY_RETAINED " + new string('x', 9000)
            });
            var compactionCalls = 0;
            var chatCalls = 0;
            var summaryJson = "{\"summary\":\"Earlier request retained compactly.\",\"goals\":[],\"requirements\":[],\"decisions\":[],\"verifiedFacts\":[],\"completedActions\":[],\"pendingWork\":[],\"blockers\":[],\"stableReferences\":[],\"activeSkills\":[],\"artifactReferences\":[],\"warnings\":[]}";
            LlmCompletionDelegate completion = (requestSettings, messages, options, progress, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(options == null ? null : options.ResponseSchemaName, "rnassistant_context_compaction", StringComparison.Ordinal))
                {
                    compactionCalls += 1;
                    return Task.FromResult(new LlmCompletionResult { Content = summaryJson });
                }
                chatCalls += 1;
                return Task.FromResult(new LlmCompletionResult { Content = "Готово после compact." });
            };
            var compactor = new ContextCompactionService(completion);
            var service = new PlainChatService(completion, compactor);
            var settings = new AppSettings
            {
                AutoCompressContext = true,
                ContextWindowOverrideTokens = 4096,
                MaxTokens = 1024
            };

            var result = service.ExecuteAsync(
                "CURRENT_REQUEST",
                session,
                new DocumentContext(),
                settings,
                null,
                null,
                CancellationToken.None).GetAwaiter().GetResult();

            AssertEqual(1, compactionCalls, "overflow invokes one model compaction retry");
            AssertEqual(1, chatCalls, "main chat completion runs once after compaction");
            AssertContains(result.AssistantText, "после compact", "main answer returned");
            AssertContains(session.Messages[0].Content, "RAW_HISTORY_RETAINED", "raw history remains stored");
            AssertTrue(ContextCompactionService.ActiveCheckpoint(session) != null, "retry activates durable checkpoint");
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

            var catalog = JArray.Parse("[{\"id\":\"model-a\",\"display_name\":\"Model A\",\"context_window\":1048576,\"max_completion_tokens\":8192,\"supports_reasoning\":true,\"reasoning_request_mode\":\"enable_thinking\",\"supports_vision\":true,\"supports_audio\":false}]");
            AssertTrue(ModelCapabilityService.Merge(settings, catalog), "standard data catalog merged");
            AssertEqual(1048576, settings.ModelCapabilities["model-a"].MaxContextTokens.Value, "model context parsed");
            AssertEqual(8192, settings.ModelCapabilities["model-a"].MaxOutputTokens.Value, "model output parsed separately");
            AssertTrue(settings.ModelCapabilities["model-a"].SupportsImages == true, "model vision parsed");
            AssertTrue(settings.ModelCapabilities["model-a"].SupportsReasoning == true, "model reasoning parsed");
            AssertEqual(ReasoningRequestModes.EnableThinking, settings.ModelCapabilities["model-a"].ReasoningRequestMode, "model reasoning request mode parsed");
            AssertTrue(settings.ModelCapabilities["model-a"].SupportsAudio == false, "model audio parsed");
            AssertEqual("model-a", settings.AttachmentModelPriority[0], "multimodal model appended to priority");

            settings.ModelCapabilities["model-a"].MaxContextTokens = 2000000;
            settings.ModelCapabilities["model-a"].MaxOutputTokens = 16000;
            settings.ModelCapabilities["model-a"].SupportsReasoning = false;
            settings.ModelCapabilities["model-a"].ReasoningRequestMode = ReasoningRequestModes.CustomJson;
            var minimalCatalog = JObject.Parse("{\"data\":[{\"id\":\"model-a\",\"value\":\"legacy-name\",\"context_window\":4096,\"max_completion_tokens\":512,\"supports_reasoning\":true},{\"id\":\"model-b\"}]}");
            AssertTrue(ModelCapabilityService.Merge(settings, minimalCatalog), "minimal id catalog adds a model without clearing stored capabilities");
            AssertEqual(2000000, settings.ModelCapabilities["model-a"].MaxContextTokens.Value, "manual context survives catalog refresh");
            AssertEqual(16000, settings.ModelCapabilities["model-a"].MaxOutputTokens.Value, "manual output limit survives catalog refresh");
            AssertTrue(settings.ModelCapabilities["model-a"].SupportsReasoning == false, "manual reasoning support survives catalog refresh");
            AssertEqual(ReasoningRequestModes.CustomJson, settings.ModelCapabilities["model-a"].ReasoningRequestMode, "manual reasoning mode survives catalog refresh");
            AssertTrue(settings.ModelCapabilities.ContainsKey("model-b"), "id-only model is retained");
            AssertTrue(!settings.ModelCapabilities.ContainsKey("legacy-name"), "id is preferred over legacy value field");
        }

        private static void InvalidModelCatalogResponseIsConcise()
        {
            LlmRequestException failure = null;
            try
            {
                ModelCapabilityService.ParseCatalog(
                    "<html><body>proxy error</body></html>",
                    "https://catalog.example.test/models");
            }
            catch (LlmRequestException ex)
            {
                failure = ex;
            }

            AssertTrue(failure != null, "HTML catalog response rejected");
            AssertEqual(LlmFailureKind.InvalidResponse, failure.Kind, "catalog response failure kind");
            AssertContains(failure.Message, "response is not JSON", "concise catalog reason");
            AssertContains(failure.Message, "https://catalog.example.test/models", "catalog endpoint diagnostic");
            AssertTrue(failure.Message.IndexOf("Newtonsoft", StringComparison.OrdinalIgnoreCase) < 0, "stack trace is not exposed in message");

            failure = null;
            try
            {
                ModelCapabilityService.ParseCatalog(
                    "{\"error\":{\"message\":\"unauthorized\"}}",
                    "https://catalog.example.test/models");
            }
            catch (LlmRequestException ex)
            {
                failure = ex;
            }
            AssertTrue(failure != null, "JSON error object is not accepted as an empty catalog");
            AssertContains(failure.Message, "does not contain a supported model array", "catalog shape diagnostic");
        }

        private static void SettingsRoundTripPreservesValues()
        {
            WithTempPaths(delegate(RNAssistant.Core.Storage.AppDataPaths paths)
            {
                var store = new RNAssistant.Core.Storage.JsonFileStore();
                store.Save(paths.SettingsFile, new AppSettings
                {
                    BaseUrl = "https://api.example.test/v1",
                    Model = "test-model",
                    Temperature = 0,
                    MaxAgentIterations = 17,
                    AutoRetryToolErrors = false,
                    DebugModelTraffic = true
                });

                var loaded = store.Load(paths.SettingsFile, new AppSettings());
                AssertEqual("https://api.example.test/v1", loaded.BaseUrl, "base URL persists");
                AssertEqual("test-model", loaded.Model, "model persists");
                AssertEqual(0d, loaded.Temperature, "zero temperature persists");
                AssertEqual(17, loaded.MaxAgentIterations, "agent iteration setting persists");
                AssertTrue(!loaded.AutoRetryToolErrors, "false boolean setting persists");
                AssertTrue(loaded.DebugModelTraffic, "model traffic debug setting persists");
            });
        }

        private static void RuntimeLogTailAndClear()
        {
            WithTempPaths(delegate(RNAssistant.Core.Storage.AppDataPaths paths)
            {
                RNAssistant.Office.Diagnostics.RuntimeLog.Configure(paths.Root);
                RNAssistant.Office.Diagnostics.RuntimeLog.Debug("MODEL TRAFFIC TEST\n{\n  \"ok\": true\n}");
                var content = RNAssistant.Office.Diagnostics.RuntimeLog.ReadTail(4096);
                AssertContains(content, "MODEL TRAFFIC TEST", "runtime debug entry is readable");
                AssertContains(content, "\"ok\": true", "runtime debug JSON stays formatted");
                RNAssistant.Office.Diagnostics.RuntimeLog.Clear();
                AssertEqual(string.Empty, RNAssistant.Office.Diagnostics.RuntimeLog.ReadTail(4096), "runtime log clears");
            });
        }

        private static void VbaCreationRouteAllowsMutation()
        {
            var route = new AgentRouteResolver().Route(
                new OfficeSnapshot { Host = "Excel" });

            AssertEqual("agent", route.TaskType, "VBA wording is not parsed by runtime");
            AssertEqual("agent", route.Mode, "generic agent mode");
            AssertEqual(AgentPhases.Decision, route.Phase, "model may select the next concrete action");
            AssertTrue(!route.RequiresInspection, "inspection is chosen from tool guidance, not keywords");

            var tools = new List<ToolDefinition>(FakeOfficeAdapter.ForHost("Excel").GetBuiltInTools());
            var slice = new ToolCatalogSlicer().Slice(route, tools, 100);
            AssertTrue(slice.Find("excel.insert_vba_module") != null, "insert vba module is available");

            var macroRoute = new AgentRouteResolver().Route(new OfficeSnapshot { Host = "Excel" });
            AssertTrue(new ToolCatalogSlicer().Slice(macroRoute, tools, 100).Find("excel.run_macro") != null, "confirmation-requiring tool remains selectable");
        }

        private static void DestructiveChartRouteAdvancesToMutation()
        {
            var route = new AgentRouteResolver().Route(
                new OfficeSnapshot { Host = "Excel" });
            var tools = new List<ToolDefinition>
            {
                new ToolDefinition { Id = "excel.list_charts", Host = "Excel", Enabled = true, AgentCanRun = true },
                new ToolDefinition { Id = "excel.delete_chart", Host = "Excel", Enabled = true, MutatesDocument = true, AgentCanRun = false, RiskLevel = 3 }
            };

            AssertEqual("agent", route.TaskType, "destructive wording is not parsed by runtime");
            AssertEqual(AgentPhases.Decision, route.Phase, "generic agent phase");
            AssertTrue(new ToolCatalogSlicer().Slice(route, tools).Find("excel.delete_chart") != null, "confirmation-requiring delete remains selectable");
        }

        private static void NewFormattedReportStartsWithMutation()
        {
            var route = new AgentRouteResolver().Route(
                new OfficeSnapshot { Host = "Excel" });

            AssertEqual("agent", route.TaskType, "report wording is not parsed by runtime");
            AssertEqual(AgentPhases.Decision, route.Phase, "generic decision phase");
            AssertTrue(!route.RequiresInspection, "runtime does not invent an inspection requirement");
            AssertEqual("model_decision", route.DecisionReason, "model owns the requested workflow");
        }

        private static void ShortFollowUpContinuesPendingAgentTask()
        {
            var session = new ChatSession
            {
                Mode = ChatModes.Chat,
                PendingAgentTask = new PendingAgentTask
                {
                    Request = "Создай новый лист, график и VBA-макрос.",
                    LastQuestion = "Подтвердите выполнение.",
                    Kind = AgentResponseKinds.Clarify,
                    UpdatedUtc = DateTime.UtcNow
                }
            };

            AssertEqual(ChatModes.Agent, new ChatExecutionModeSelector().Select("да именно так", session), "pending agent task overrides chat mode");
            var resolved = AgentTaskContinuationResolver.Resolve("да именно так", session);
            AssertContains(resolved, "Создай новый лист", "original task retained");
            AssertContains(resolved, "USER_MESSAGE", "current-message marker included");
            AssertContains(resolved, "да именно так", "follow-up content included");
            AssertTrue(AgentTaskContinuationResolver.ShouldContinue("Реализуй план", session), "implement-plan command continues pending task");

            var replacement = AgentTaskContinuationResolver.Resolve("Новая независимая задача", session);
            AssertContains(replacement, "PENDING_TASK_CONTEXT", "pending request remains context without lexical guessing");
            AssertContains(replacement, "Новая независимая задача", "model can replace the pending request");
            AssertTrue(session.PendingAgentTask != null, "runtime does not discard pending state by message length");
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
                RequiresTool = true
            };
            var mutationSlice = new ToolCatalogSlicer().Slice(mutationRoute, tools);
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
                RequiresTool = true,
                RequiresInspection = true
            };
            var readSlice = new ToolCatalogSlicer().Slice(readRoute, tools);
            var excluded = new AgentActionValidator().Validate(
                new AgentPlannerStep { ToolId = insert.Id },
                readSlice,
                readRoute,
                new List<AgentObservation>(),
                tools);
            AssertTrue(!excluded.Success, "phase-excluded tool rejected");
            AssertContains(excluded.Message, "Target must be inspected", "explicit inspection precondition is precise");
        }

        private static void OptionalToolAuthoringIsExplicitAndDoesNotCompleteDocumentTask()
        {
            var promptRoute = new AgentRouteResolver().Route(
                new OfficeSnapshot { Host = "Excel" });
            AssertEqual("agent", promptRoute.TaskType, "prompt wording is not parsed by runtime");
            AssertEqual(AgentPhases.Decision, promptRoute.Phase, "model selects authoring tools through skills");

            var route = new RoutedTask
            {
                App = "Excel",
                TaskType = "chart",
                Mode = "mutate_chart",
                Phase = AgentPhases.Mutation,
                RequiresTool = true
            };
            var tools = new List<ToolDefinition>
            {
                new ToolDefinition { Id = "excel.update_chart", Host = "Excel", Enabled = true, MutatesDocument = true, RiskLevel = 2 },
                new ToolDefinition { Id = "common.tools_validate", Host = "Common", Enabled = true, RiskLevel = 0 },
                new ToolDefinition { Id = "common.tools_save", Host = "Common", Enabled = true, MutatesLocalState = true, RiskLevel = 1 }
            };

            var visible = new ToolCatalogSlicer().Slice(route, tools);
            AssertTrue(visible.Find("common.tools_validate") != null, "explicit catalog tool remains selectable");
            AssertTrue(visible.Find("common.tools_save") != null, "confirmation policy is enforced by executor metadata");

            AgentPhaseController.Advance(route, new List<AgentObservation>
            {
                new AgentObservation { Status = "success", ToolId = "common.tools_save", LocalMutation = true, Purpose = AgentObservationPurposes.Mutation }
            }, false);
            AssertEqual(AgentPhases.Mutation, route.Phase, "saving helper tool does not complete chart task");
        }

    }
}
