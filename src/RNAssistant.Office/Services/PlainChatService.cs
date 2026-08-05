using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal sealed class PlainChatService
    {
        private readonly LlmCompletionDelegate _completeAsync;
        private readonly ChatContextWindowBuilder _contextBuilder;

        public PlainChatService(LlmCompletionDelegate completeAsync)
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
            CancellationToken cancellationToken,
            bool appendUserMessage = true)
        {
            var routing = AttachmentModelRoutingService.Select(settings, session, attachments);
            settings = routing.Settings;
            if (routing.IsRouted)
            {
                Report(progress, "routing", routing.ProgressMessage);
            }
            if (appendUserMessage)
            {
                session.Messages.Add(new ChatMessage
                {
                    Role = "user",
                    Content = text ?? string.Empty,
                    Attachments = attachments == null
                        ? new List<ChatAttachment>()
                        : new List<ChatAttachment>(attachments)
                });
            }
            var messages = _contextBuilder.BuildPlainMessages(text, session, context, settings, attachments);
            Report(progress, "thinking", "Модель готовит ответ...");
            var completion = await CompleteBufferedAsync(settings, messages, progress, cancellationToken).ConfigureAwait(false);
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

        private async Task<LlmCompletionResult> CompleteBufferedAsync(
            AppSettings settings,
            IEnumerable<ChatMessage> messages,
            Action<string, string, ChatActivity> progress,
            CancellationToken cancellationToken)
        {
            var pendingReasoning = new StringBuilder();
            var reasoningSeen = false;
            var reasoningCompleted = false;
            var lastReasoningReportUtc = DateTime.UtcNow;
            Action<bool> flushReasoning = completed =>
            {
                if (completed && reasoningCompleted ||
                    pendingReasoning.Length == 0 && (!completed || !reasoningSeen))
                {
                    return;
                }
                Report(progress, "thinking", completed ? "Анализ завершен." : "Модель анализирует запрос...", new ChatActivity
                {
                    Kind = "reasoning",
                    Title = completed ? "Анализ завершен" : "Модель анализирует запрос",
                    Subtitle = "Ход рассуждения",
                    Status = completed ? "completed" : "running",
                    ResultMessage = pendingReasoning.ToString()
                });
                pendingReasoning.Clear();
                lastReasoningReportUtc = DateTime.UtcNow;
                if (completed)
                {
                    reasoningCompleted = true;
                }
            };
            var completion = await _completeAsync(settings, messages, null, update =>
            {
                if (update == null)
                {
                    return;
                }

                if (!string.IsNullOrEmpty(update.ReasoningDelta))
                {
                    reasoningSeen = true;
                    pendingReasoning.Append(update.ReasoningDelta);
                }
                if (update.Completed || pendingReasoning.Length >= 256 ||
                    pendingReasoning.Length > 0 && DateTime.UtcNow - lastReasoningReportUtc >= TimeSpan.FromMilliseconds(100))
                {
                    flushReasoning(update.Completed);
                }
                if (string.IsNullOrEmpty(update.ContentDelta))
                {
                    return;
                }
                Report(progress, "streaming", update.ContentDelta);
            }, cancellationToken).ConfigureAwait(false);
            flushReasoning(true);
            return completion;
        }

        private static void Report(Action<string, string, ChatActivity> progress, string phase, string message)
        {
            Report(progress, phase, message, null);
        }

        private static void Report(Action<string, string, ChatActivity> progress, string phase, string message, ChatActivity activity)
        {
            if (progress != null)
            {
                progress(phase, message ?? string.Empty, activity);
            }
        }
    }
}
