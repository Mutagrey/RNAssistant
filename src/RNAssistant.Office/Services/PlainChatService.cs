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
            ChatCompletionService.ApplyChatModel(settings, session);
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
            var completion = await CompleteBufferedAsync(settings, messages, progress, cancellationToken).ConfigureAwait(false);
            if (completion == null)
            {
                throw new InvalidOperationException("Model returned no completion.");
            }
            var assistantText = completion.Content ?? string.Empty;
            string extractedAnswer;
            if (PlainChatResponseNormalizer.TryGetUserFacingText(assistantText, out extractedAnswer))
            {
                if (!string.IsNullOrWhiteSpace(extractedAnswer))
                {
                    assistantText = extractedAnswer;
                }
                else
                {
                    Report(progress, "repairing", "Модель вернула внутренний JSON, запрашиваю обычный ответ...");
                    var repairMessages = new List<ChatMessage>(messages)
                    {
                        new ChatMessage { Role = "assistant", Content = assistantText },
                        new ChatMessage
                        {
                            Role = "user",
                            Content = "The previous response exposed internal reasoning or planner JSON instead of answering. Return only the user-facing answer to the original request in natural language. Do not return JSON, code fences, a thought/reasoning field, or commentary about this correction."
                        }
                    };
                    completion = await CompleteBufferedAsync(settings, repairMessages, progress, cancellationToken).ConfigureAwait(false);
                    if (completion == null)
                    {
                        throw new InvalidOperationException("Model returned no completion.");
                    }

                    assistantText = completion.Content ?? string.Empty;
                    if (PlainChatResponseNormalizer.TryGetUserFacingText(assistantText, out extractedAnswer))
                    {
                        assistantText = string.IsNullOrWhiteSpace(extractedAnswer)
                            ? "Модель не вернула пользовательский ответ. Повторите запрос или выберите другую модель."
                            : extractedAnswer;
                    }
                    messages = repairMessages;
                }
            }
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
            var prefix = new StringBuilder();
            var streamDecisionMade = false;
            var suppressStream = false;
            return await _completeAsync(settings, messages, update =>
            {
                if (update == null || string.IsNullOrEmpty(update.ContentDelta))
                {
                    return;
                }

                if (streamDecisionMade)
                {
                    if (!suppressStream)
                    {
                        Report(progress, "streaming", update.ContentDelta);
                    }
                    return;
                }

                prefix.Append(update.ContentDelta);
                var trimmed = prefix.ToString().TrimStart();
                if (trimmed.Length == 0)
                {
                    return;
                }

                suppressStream = trimmed.StartsWith("{", StringComparison.Ordinal) ||
                    trimmed.StartsWith("[", StringComparison.Ordinal) ||
                    trimmed.StartsWith("`", StringComparison.Ordinal);
                streamDecisionMade = true;
                if (!suppressStream)
                {
                    Report(progress, "streaming", prefix.ToString());
                }
            }, cancellationToken).ConfigureAwait(false);
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
