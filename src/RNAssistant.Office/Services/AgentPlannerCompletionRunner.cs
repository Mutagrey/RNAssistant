using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Services
{
    internal sealed class AgentPlannerCompletionRunner
    {
        private readonly ChatCompletionService.CompletionDelegate _completeAsync;
        private readonly AgentPlannerResponseParser _parser;

        public AgentPlannerCompletionRunner(ChatCompletionService.CompletionDelegate completeAsync)
        {
            _completeAsync = completeAsync;
            _parser = new AgentPlannerResponseParser();
        }

        public async Task<AgentPlannerAttempt> CompleteAsync(
            AppSettings settings,
            IReadOnlyList<ChatMessage> messages,
            AgentRunState state,
            Action<string, string, ChatActivity> progress,
            string progressMessage,
            string repairMessage,
            string repairPrompt,
            CancellationToken cancellationToken)
        {
            var activeMessages = messages;
            var completion = await CompleteWithProgressAsync(settings, activeMessages, progress, progressMessage, cancellationToken).ConfigureAwait(false);
            var text = completion.Content ?? string.Empty;
            var parsed = _parser.Parse(text);
            if (!parsed.Success && !state.FormatRepairUsed)
            {
                state.FormatRepairUsed = true;
                Report(progress, "repairing", repairMessage, null);
                activeMessages = BuildRepairMessages(activeMessages, text, parsed, repairPrompt);
                completion = await CompleteWithProgressAsync(settings, activeMessages, progress, repairMessage, cancellationToken).ConfigureAwait(false);
                text = completion.Content ?? string.Empty;
                parsed = _parser.Parse(text);
            }

            return new AgentPlannerAttempt
            {
                Completion = completion,
                Text = text,
                ParseResult = parsed,
                ContextUsage = ContextUsageEstimator.FromPrompt(activeMessages, settings, completion.PromptTokens)
            };
        }

        private async Task<LlmCompletionResult> CompleteWithProgressAsync(
            AppSettings settings,
            IEnumerable<ChatMessage> messages,
            Action<string, string, ChatActivity> progress,
            string progressMessage,
            CancellationToken cancellationToken)
        {
            var pendingReasoning = new StringBuilder();
            var lastReportUtc = DateTime.UtcNow;
            var reasoningSeen = false;
            var completionReported = false;
            Action<bool> flush = completed =>
            {
                if (completed && completionReported ||
                    pendingReasoning.Length == 0 && (!completed || !reasoningSeen))
                {
                    return;
                }
                Report(progress, "thinking", completed ? "Анализ завершен." : progressMessage, new ChatActivity
                {
                    Kind = "reasoning",
                    Title = completed ? "Анализ завершен" : progressMessage.TrimEnd('.'),
                    Subtitle = "Ход рассуждения",
                    Status = completed ? "completed" : "running",
                    ResultMessage = pendingReasoning.ToString()
                });
                pendingReasoning.Clear();
                lastReportUtc = DateTime.UtcNow;
                completionReported = completed;
            };
            var completion = await _completeAsync(
                settings,
                messages,
                update =>
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
                        pendingReasoning.Length > 0 && DateTime.UtcNow - lastReportUtc >= TimeSpan.FromMilliseconds(100))
                    {
                        flush(update.Completed);
                    }
                },
                cancellationToken).ConfigureAwait(false);
            flush(true);
            return completion ?? new LlmCompletionResult();
        }

        private static List<ChatMessage> BuildRepairMessages(
            IEnumerable<ChatMessage> originalMessages,
            string badText,
            AgentPlannerParseResult parseResult,
            string repairPrompt)
        {
            var messages = new List<ChatMessage>(originalMessages ?? new ChatMessage[0]);
            if (!string.IsNullOrWhiteSpace(badText))
            {
                messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = badText.Length <= 2000
                        ? badText
                        : "Malformed planner response omitted because it is too large for a safe repair prompt."
                });
            }
            messages.Add(new ChatMessage
            {
                Role = "user",
                Content = (repairPrompt ?? string.Empty) +
                    "\nValidation error: " + (parseResult == null ? string.Empty : parseResult.ErrorCode + " " + parseResult.ErrorMessage) +
                    "\nRebuild the response from the original request; do not continue or copy the malformed response." +
                    "\nUse the original request, route and available tools only as input. Do not copy them into the response. Return only kind, intent, message, steps and expectedOutcome." +
                    "\nFor large HTML/CSS/JavaScript output, return one content-bearing workspace upsert step now and continue with other local files after its tool observation."
            });
            return messages;
        }

        private static void Report(Action<string, string, ChatActivity> progress, string phase, string message, ChatActivity activity)
        {
            if (progress != null)
            {
                progress(phase, message, activity);
            }
        }
    }

    internal sealed class AgentPlannerAttempt
    {
        public LlmCompletionResult Completion { get; set; }
        public string Text { get; set; }
        public AgentPlannerParseResult ParseResult { get; set; }
        public object ContextUsage { get; set; }
    }
}
