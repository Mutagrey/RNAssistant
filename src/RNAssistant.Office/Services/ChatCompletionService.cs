using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    public sealed class ChatCompletionResult
    {
        public string AssistantText { get; set; }
        public IReadOnlyList<object> ToolResults { get; set; }
        public object ContextUsage { get; set; }
    }

    public sealed class ChatCompletionService
    {
        public delegate string PendingToolRegistrar(ChatSession session, ToolCommand command, ToolResult result);
        public delegate Task<LlmCompletionResult> CompletionDelegate(
            AppSettings settings,
            IEnumerable<ChatMessage> messages,
            Action<LlmStreamUpdate> streamProgress,
            CancellationToken cancellationToken);

        private readonly AgentRunService _agentRunService;

        public ChatCompletionService(
            IOfficeApplicationAdapter adapter,
            OfficeToolExecutor toolExecutor,
            Func<AppSettings, IEnumerable<ChatMessage>, CancellationToken, Task<LlmCompletionResult>> completeAsync)
        {
            _agentRunService = new AgentRunService(adapter, toolExecutor, completeAsync);
        }

        public ChatCompletionService(
            IOfficeApplicationAdapter adapter,
            OfficeToolExecutor toolExecutor,
            CompletionDelegate completeAsync)
        {
            _agentRunService = new AgentRunService(adapter, toolExecutor, completeAsync);
        }

        internal ChatCompletionService(AgentRunService agentRunService)
        {
            _agentRunService = agentRunService;
        }

        public Task<ChatCompletionResult> ExecuteAsync(
            string text,
            ChatSession session,
            DocumentContext documentContext,
            AppSettings settings,
            IReadOnlyList<ToolDefinition> tools,
            Action<string, string, ChatActivity> progress,
            PendingToolRegistrar pendingToolRegistrar = null,
            IReadOnlyList<SkillDefinition> skills = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return ExecuteAsync(text, session, documentContext, settings, tools, null, progress, pendingToolRegistrar, skills, cancellationToken);
        }

        public Task<ChatCompletionResult> ExecuteAsync(
            string text,
            ChatSession session,
            DocumentContext documentContext,
            AppSettings settings,
            IReadOnlyList<ToolDefinition> tools,
            IReadOnlyList<ChatAttachment> attachments,
            Action<string, string, ChatActivity> progress,
            PendingToolRegistrar pendingToolRegistrar = null,
            IReadOnlyList<SkillDefinition> skills = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            ApplyChatModel(settings, session);
            return _agentRunService.RunUserTurnAsync(text, session, documentContext, settings, tools, attachments, progress, pendingToolRegistrar, skills, cancellationToken);
        }

        public Task<ChatCompletionResult> ContinueAfterToolAsync(
            ToolCommand confirmedCommand,
            ChatSession session,
            DocumentContext documentContext,
            AppSettings settings,
            IReadOnlyList<ToolDefinition> tools,
            Action<string, string, ChatActivity> progress,
            PendingToolRegistrar pendingToolRegistrar = null,
            IReadOnlyList<SkillDefinition> skills = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            ApplyChatModel(settings, session);
            return _agentRunService.ContinueAfterToolAsync(confirmedCommand, session, documentContext, settings, tools, progress, pendingToolRegistrar, skills, cancellationToken);
        }

        public bool CommandMutates(ToolCommand command, IReadOnlyList<ToolDefinition> tools)
        {
            return _agentRunService.CommandMutates(command, tools);
        }

        internal static void ApplyChatModel(AppSettings settings, ChatSession session)
        {
            if (settings == null || session == null || string.IsNullOrWhiteSpace(session.Model))
            {
                return;
            }

            settings.Model = session.Model.Trim();
        }

        internal static bool EnsureImageCompatibleModel(
            AppSettings settings,
            ChatSession session,
            IReadOnlyList<ChatAttachment> attachments)
        {
            if (settings == null || session == null || !HasImageAttachments(session, attachments))
            {
                return false;
            }

            var defaultModel = settings.Model;
            ApplyChatModel(settings, session);
            if (ImageSupport(settings, settings.Model) != false)
            {
                return false;
            }

            var replacement = FindImageCompatibleModel(settings, defaultModel);
            if (string.IsNullOrWhiteSpace(replacement))
            {
                throw new InvalidOperationException(
                    "Выбранная модель не поддерживает изображения, а модель с поддержкой изображений не настроена.");
            }

            settings.Model = replacement;
            session.Model = replacement;
            return true;
        }

        private static bool HasImageAttachments(ChatSession session, IReadOnlyList<ChatAttachment> attachments)
        {
            foreach (var attachment in attachments ?? new ChatAttachment[0])
            {
                if (attachment != null && string.Equals(attachment.Kind, "image", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            foreach (var message in session.Messages ?? new List<ChatMessage>())
            {
                foreach (var attachment in message == null
                    ? new List<ChatAttachment>()
                    : message.Attachments ?? new List<ChatAttachment>())
                {
                    if (attachment != null && string.Equals(attachment.Kind, "image", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static string FindImageCompatibleModel(AppSettings settings, string preferredModel)
        {
            if (!string.IsNullOrWhiteSpace(preferredModel) &&
                ImageSupport(settings, preferredModel) == true)
            {
                return preferredModel.Trim();
            }
            if (settings.ModelImageSupportOverrides != null)
            {
                foreach (var item in settings.ModelImageSupportOverrides)
                {
                    if (item.Value == true)
                    {
                        return item.Key;
                    }
                }
            }
            if (settings.ModelCapabilities != null)
            {
                foreach (var item in settings.ModelCapabilities)
                {
                    if (item.Value != null && ImageSupport(settings, item.Key) == true)
                    {
                        return item.Key;
                    }
                }
            }
            return null;
        }

        private static bool? ImageSupport(AppSettings settings, string model)
        {
            if (settings == null || string.IsNullOrWhiteSpace(model))
            {
                return null;
            }
            bool? value;
            if (settings.ModelImageSupportOverrides != null &&
                settings.ModelImageSupportOverrides.TryGetValue(model, out value) &&
                value.HasValue)
            {
                return value.Value;
            }
            var capability = ModelContextBudget.Capability(settings, model);
            return capability == null ? null : capability.SupportsImages;
        }
    }
}
