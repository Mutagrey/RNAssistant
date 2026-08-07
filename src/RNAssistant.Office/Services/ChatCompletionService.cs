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
        private readonly AgentRunService _agentRunService;

        public ChatCompletionService(
            IOfficeApplicationAdapter adapter,
            OfficeToolExecutor toolExecutor,
            LlmCompletionDelegate completeAsync)
            : this(adapter, toolExecutor, completeAsync, null)
        {
        }

        internal ChatCompletionService(
            IOfficeApplicationAdapter adapter,
            OfficeToolExecutor toolExecutor,
            LlmCompletionDelegate completeAsync,
            ContextCompactionService contextCompactionService)
        {
            _agentRunService = new AgentRunService(adapter, toolExecutor, completeAsync, true, contextCompactionService);
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
            return ExecuteAsync(text, session, documentContext, settings, tools, null, progress, pendingToolRegistrar, skills, cancellationToken, true);
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
            CancellationToken cancellationToken = default(CancellationToken),
            bool appendUserMessage = true)
        {
            var routing = AttachmentModelRoutingService.Select(settings, session, attachments);
            if (routing.IsRouted && progress != null)
            {
                progress("routing", routing.ProgressMessage, null);
            }
            return _agentRunService.RunUserTurnAsync(text, session, documentContext, routing.Settings, tools, attachments, progress, pendingToolRegistrar, skills, cancellationToken, appendUserMessage);
        }

        public Task<ChatCompletionResult> ContinueAfterToolAsync(
            ToolCommand confirmedCommand,
            ToolResult confirmedResult,
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
            var routing = AttachmentModelRoutingService.Select(settings, session, attachments);
            if (routing.IsRouted && progress != null)
            {
                progress("routing", routing.ProgressMessage, null);
            }
            return _agentRunService.ContinueAfterToolAsync(confirmedCommand, confirmedResult, session, documentContext, routing.Settings, tools, attachments, progress, pendingToolRegistrar, skills, cancellationToken);
        }
    }
}
