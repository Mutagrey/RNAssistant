using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
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
        private static void WithTempExecutor(Action<OfficeToolExecutor, FakeOfficeAdapter> action)
        {
            WithTempExecutor(new FakeOfficeAdapter(), action);
        }

        private static void WithTempExecutor(FakeOfficeAdapter adapter, Action<OfficeToolExecutor, FakeOfficeAdapter> action)
        {
            WithTempExecutor(adapter, new AppSettings(), action);
        }

        private static void WithTempExecutor(FakeOfficeAdapter adapter, AppSettings settings,
            Action<OfficeToolExecutor, FakeOfficeAdapter> action)
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var executor = new OfficeToolExecutor(
                    adapter,
                    new VbaJournalStore(paths),
                    new SkillStore(paths),
                    new ToolStore(paths),
                    () => settings,
                    value => settings = value,
                    paths);
                action(executor, adapter);
            });
        }

        private static ToolInvocation PendingCommand(ChatSession session)
        {
            var call = session.LastRun.KernelState.Summary.PendingConfirmation.Call;
            return new ToolInvocation { ToolId = call.Name, ToolCallId = call.Id,
                Arguments = JsonConvert.DeserializeObject<Dictionary<string, object>>(call.ArgumentsJson,
                    new JsonSerializerSettings { DateParseHandling = DateParseHandling.None }) };
        }

        private static AcceptedToolCallOrigin FixtureCallOrigin(string stepId = "fixture-step",
            string modelAttemptId = null, int callIndex = 0)
        {
            return new AcceptedToolCallOrigin(stepId, modelAttemptId ?? Guid.NewGuid().ToString("N"), callIndex);
        }

        private static readonly AsyncLocal<AppDataPaths> FixturePaths = new AsyncLocal<AppDataPaths>();

        private static IEventStore EventStore(ChatStore store)
        {
            return new ChatEventStoreAdapter(store);
        }

        private static IConversationStore ConversationStore(ChatStore store)
        {
            return new ChatConversationStoreAdapter(store);
        }

        private static JObject SchemaTransport(JObject logical)
        { return new JObject { ["response"] = logical.DeepClone() }; }

        // Behavior fixtures script logical decisions. Transport-specific tests call
        // ModelProtocolClient directly and must supply their actual wire bytes.
        private static async Task<LlmCompletionResult> FixtureCompletion(LlmCompletionDelegate completion,
            AppSettings settings, IEnumerable<ChatMessage> messages, LlmRequestOptions options,
            Action<LlmStreamUpdate> stream, CancellationToken token)
        {
            var result = await completion(settings, messages, options, stream, token);
            if (result != null && result.Content != null && options?.ResponseFormat == LlmResponseFormats.JsonSchema &&
                options.ResponseSchemaName == ConversationResponseSchemaBuilder.SchemaName)
                result.Content = "{\"response\":" + result.Content + "}";
            return result;
        }

        private static ConversationRunService CreateConversationRunService(IOfficeApplicationAdapter adapter,
            OfficeToolExecutor executor, LlmCompletionDelegate completion, ContextCompactionService compaction = null,
            Func<IMaterializedModelProtocol> modelProtocolFactory = null)
        {
            if (FixturePaths.Value == null) throw new InvalidOperationException("Conversation tests require WithTempPaths.");
            var store = new ChatStore(FixturePaths.Value);
            return new ConversationRunService(
                adapter,
                executor,
                ConversationStore(store),
                EventStore(store),
                (settings, messages, options, stream, token) => FixtureCompletion(completion, settings, messages, options, stream, token),
                compaction,
                modelProtocolFactory);
        }

        // These fixtures explicitly approve each pending call through the real
        // continuation boundary when the test configuration requires approval.
        private static Task<ChatTurnResult> ExecuteWithApprovalsAsync(this ConversationRunService service,
            string mode, string text, ChatSession session, DocumentContext documentContext,
            AppSettings settings, IReadOnlyList<ToolCatalogEntry> tools,
            Action<string, string, ChatActivity> progress,
            ConversationRunService.PendingToolRegistrar registrar = null,
            IReadOnlyList<SkillDefinition> skills = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            return service.ExecuteWithApprovalsAsync(mode, text, session, documentContext, settings,
                tools, null, progress, registrar, skills, cancellationToken);
        }

        private static async Task<ChatTurnResult> ExecuteWithApprovalsAsync(this ConversationRunService service,
            string mode, string text, ChatSession session, DocumentContext documentContext,
            AppSettings settings, IReadOnlyList<ToolCatalogEntry> tools, IReadOnlyList<ChatAttachment> attachments,
            Action<string, string, ChatActivity> progress, ConversationRunService.PendingToolRegistrar registrar,
            IReadOnlyList<SkillDefinition> skills, CancellationToken cancellationToken, bool appendUserMessage = true)
        {
            registrar = registrar ?? ((pendingSession, command, registration) => Guid.NewGuid().ToString("N"));
            var result = await service.ExecuteAsync(mode, text, session, documentContext, settings,
                tools, attachments, progress, registrar, skills, cancellationToken, appendUserMessage);
            var confirmations = 0;
            while (result.WaitingForConfirmation)
            {
                AssertTrue(++confirmations <= 128, "bounded explicit fixture approvals");
                var pending = session.LastRun.KernelState.Summary.PendingConfirmation;
                result = await service.ConfirmAsync(pending.PendingId, PendingCommand(session), session,
                    new ConversationRunInput(settings, documentContext, tools, skills, attachments),
                    progress, registrar, cancellationToken: cancellationToken);
            }
            return result;
        }

        private static void WithTempPaths(Action<AppDataPaths> action)
        {
            var root = Path.Combine(Path.GetTempPath(), "RNAssistant.Harness." + Guid.NewGuid().ToString("N"));
            var previousPaths = FixturePaths.Value;
            try
            {
                FixturePaths.Value = AppDataPaths.CreateForRoot(root);
                action(FixturePaths.Value);
            }
            finally
            {
                FixturePaths.Value = previousPaths;
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private static void AssertEqual<T>(T expected, T actual, string name)
        {
            if (!object.Equals(expected, actual))
            {
                throw new InvalidOperationException(name + ": expected '" + expected + "', got '" + actual + "'");
            }
        }

        private static void AssertTrue(bool value, string name)
        {
            if (!value)
            {
                throw new InvalidOperationException(name + " was false");
            }
        }

        private static void AssertContains(string value, string expected, string name)
        {
            if ((value ?? string.Empty).IndexOf(expected, StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidOperationException(name + ": expected '" + value + "' to contain '" + expected + "'");
            }
        }
    }
}
