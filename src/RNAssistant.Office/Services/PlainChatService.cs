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
        private readonly ContextCompactionService _contextCompactionService;

        public PlainChatService(LlmCompletionDelegate completeAsync, ContextCompactionService contextCompactionService = null)
        {
            _completeAsync = completeAsync;
            _contextBuilder = new ChatContextWindowBuilder();
            _contextCompactionService = contextCompactionService;
        }

        public async Task<ChatTurnResult> ExecuteAsync(
            string text,
            ChatSession session,
            DocumentContext context,
            AppSettings settings,
            IReadOnlyList<ChatAttachment> attachments,
            Action<string, string, ChatActivity> progress,
            CancellationToken cancellationToken,
            bool appendUserMessage = true)
        {
            settings = settings ?? new AppSettings();
            if (appendUserMessage)
            {
                session.Messages.Add(new ChatMessage
                {
                    Role = "user",
                    Content = text ?? string.Empty,
                    HtmlWorkspaceCheckpointId = session.ActiveHtmlArtifactId,
                    Attachments = attachments == null
                        ? new List<ChatAttachment>()
                        : new List<ChatAttachment>(attachments)
                });
            }
            List<ChatMessage> messages;
            try
            {
                messages = _contextBuilder.BuildPlainMessages(text, session, context, settings, attachments);
            }
            catch (PromptBudgetExceededException ex) when (
                ex.CanCompact &&
                settings.AutoCompressContext &&
                _contextCompactionService != null)
            {
                var checkpoint = await _contextCompactionService.EnsureWithinBudgetAsync(
                    session,
                    settings,
                    string.Empty,
                    true,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                if (checkpoint == null) throw;
                messages = _contextBuilder.BuildPlainMessages(text, session, context, settings, attachments);
            }
            Report(progress, "thinking", "Модель готовит ответ...");
            var completion = await CompleteBufferedAsync(settings, messages, session, progress, cancellationToken).ConfigureAwait(false);
            if (completion == null)
            {
                throw new InvalidOperationException("Model returned no completion.");
            }
            var assistantText = string.IsNullOrWhiteSpace(completion.Content) && !string.IsNullOrWhiteSpace(completion.RefusalContent)
                ? completion.RefusalContent
                : completion.Content ?? string.Empty;
            if (string.IsNullOrWhiteSpace(assistantText))
            {
                throw new InvalidOperationException("Model returned an empty response.");
            }
            session.Messages.Add(AgentTranscript.CreateAssistantMessage(assistantText, completion));
            return new ChatTurnResult
            {
                AssistantText = assistantText,
                ToolResults = new object[0],
                ContextUsage = ContextUsageEstimator.FromPrompt(messages, settings, completion.PromptTokens)
            };
        }

        private async Task<LlmCompletionResult> CompleteBufferedAsync(
            AppSettings settings,
            IEnumerable<ChatMessage> messages,
            ChatSession session,
            Action<string, string, ChatActivity> progress,
            CancellationToken cancellationToken)
        {
            var pendingReasoning = new StringBuilder();
            var pendingContent = new StringBuilder();
            var reasoningSeen = false;
            var reasoningCompleted = false;
            var lastReasoningReportUtc = DateTime.UtcNow;
            var lastContentReportUtc = DateTime.UtcNow;
            Action flushContent = () =>
            {
                if (pendingContent.Length == 0) return;
                Report(progress, "streaming", pendingContent.ToString());
                pendingContent.Clear();
                lastContentReportUtc = DateTime.UtcNow;
            };
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
            var completion = await _completeAsync(settings, messages, new LlmRequestOptions
            {
                ReasoningEnabled = session == null ? (bool?)null : session.ReasoningEnabled
            }, update =>
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
                if (!string.IsNullOrEmpty(update.ContentDelta))
                {
                    pendingContent.Append(update.ContentDelta);
                }
                if (update.Completed || pendingContent.Length >= 256 ||
                    pendingContent.Length > 0 && DateTime.UtcNow - lastContentReportUtc >= TimeSpan.FromMilliseconds(50))
                {
                    flushContent();
                }
            }, cancellationToken).ConfigureAwait(false);
            flushReasoning(true);
            flushContent();
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
