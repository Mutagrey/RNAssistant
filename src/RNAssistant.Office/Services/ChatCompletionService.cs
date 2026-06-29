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
            Func<AppSettings, IEnumerable<ChatMessage>, CancellationToken, Task<LlmCompletionResult>> completeAsync)
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

        private static void ApplyChatModel(AppSettings settings, ChatSession session)
        {
            if (settings == null || session == null || string.IsNullOrWhiteSpace(session.Model))
            {
                return;
            }

            settings.Model = session.Model.Trim();
        }
    }
}
