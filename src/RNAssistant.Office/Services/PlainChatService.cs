using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal sealed class PlainChatService
    {
        private readonly ChatCompletionService.CompletionDelegate _completeAsync;
        private readonly ChatContextWindowBuilder _contextBuilder;

        public PlainChatService(ChatCompletionService.CompletionDelegate completeAsync)
        {
            _completeAsync = completeAsync;
            _contextBuilder = new ChatContextWindowBuilder();
        }

        public async Task<ChatCompletionResult> ExecuteAsync(
            string text,
            ChatSession session,
            DocumentContext context,
            AppSettings settings,
            IReadOnlyList<ChatAttachment> attachments,
            Action<string, string, ChatActivity> progress,
            CancellationToken cancellationToken)
        {
            session.Messages.Add(new ChatMessage
            {
                Role = "user",
                Content = text ?? string.Empty,
                Attachments = attachments == null
                    ? new List<ChatAttachment>()
                    : new List<ChatAttachment>(attachments)
            });
            var messages = _contextBuilder.BuildPlainMessages(text, session, context, settings, attachments);
            Report(progress, "thinking", "Модель готовит ответ...");
            var completion = await _completeAsync(settings, messages, update =>
            {
                if (update != null)
                {
                    Report(progress, "streaming", update.ContentDelta);
                }
            }, cancellationToken).ConfigureAwait(false);
            if (completion == null)
            {
                throw new InvalidOperationException("Model returned no completion.");
            }
            var assistantText = completion.Content ?? string.Empty;
            session.Messages.Add(AgentTranscript.CreateAssistantMessage(assistantText, completion));
            return new ChatCompletionResult
            {
                AssistantText = assistantText,
                ToolResults = new object[0],
                ContextUsage = ContextUsageEstimator.FromPrompt(messages, settings, completion.PromptTokens)
            };
        }

        private static void Report(Action<string, string, ChatActivity> progress, string phase, string message)
        {
            if (progress != null)
            {
                progress(phase, message ?? string.Empty, null);
            }
        }
    }
}
