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
        private readonly ChatCompletionService.AgentCompletionDelegate _completeAsync;
        private readonly AgentPlannerResponseParser _parser;

        public AgentPlannerCompletionRunner(ChatCompletionService.AgentCompletionDelegate completeAsync)
        {
            _completeAsync = completeAsync;
            _parser = new AgentPlannerResponseParser();
        }

        public async Task<AgentPlannerAttempt> CompleteAsync(
            AppSettings settings,
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition> tools,
            AgentRunState state,
            Action<string, string, ChatActivity> progress,
            string progressMessage,
            string repairMessage,
            string repairPrompt,
            CancellationToken cancellationToken)
        {
            settings = settings ?? new AppSettings();
            var activeMessages = messages;
            var mode = NormalizeMode(string.IsNullOrWhiteSpace(state == null ? null : state.ResponseMode)
                ? settings.AgentResponseMode
                : state.ResponseMode);
            var options = BuildOptions(mode, tools);
            LlmCompletionResult completion;
            try
            {
                completion = await CompleteWithProgressAsync(settings, activeMessages, options, progress, progressMessage, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException) when (CanFallback(settings, state, mode))
            {
                mode = AgentResponseModes.JsonObject;
                RememberFallback(settings, state, mode);
                options = BuildOptions(mode, tools);
                Report(progress, "fallback", "Endpoint не принял json_schema; повторяю через json_object.", null);
                completion = await CompleteWithProgressAsync(settings, activeMessages, options, progress, progressMessage, cancellationToken).ConfigureAwait(false);
            }

            var parsed = ParseCompletion(completion, mode, tools, options.Tools);
            if (!parsed.Success && CanFallback(settings, state, mode))
            {
                mode = AgentResponseModes.JsonObject;
                RememberFallback(settings, state, mode);
                options = BuildOptions(mode, tools);
                Report(progress, "fallback", "Ответ json_schema не прошёл проверку; повторяю через json_object.", null);
                completion = await CompleteWithProgressAsync(settings, activeMessages, options, progress, progressMessage, cancellationToken).ConfigureAwait(false);
                parsed = ParseCompletion(completion, mode, tools, options.Tools);
            }

            if (!parsed.Success && !state.FormatRepairUsed)
            {
                state.FormatRepairUsed = true;
                Report(progress, "repairing", repairMessage, null);
                activeMessages = BuildRepairMessages(activeMessages, completion == null ? string.Empty : completion.Content, parsed, repairPrompt, mode);
                completion = await CompleteWithProgressAsync(settings, activeMessages, options, progress, repairMessage, cancellationToken).ConfigureAwait(false);
                parsed = ParseCompletion(completion, mode, tools, options.Tools);
            }

            return new AgentPlannerAttempt
            {
                Completion = completion ?? new LlmCompletionResult(),
                Text = completion == null ? string.Empty : completion.Content ?? string.Empty,
                ParseResult = parsed,
                ResponseMode = mode,
                RequestOptions = options,
                ContextUsage = ContextUsageEstimator.FromPrompt(activeMessages, settings, completion == null ? null : completion.PromptTokens)
            };
        }

        private AgentPlannerParseResult ParseCompletion(LlmCompletionResult completion, string mode, IEnumerable<ToolDefinition> tools, IEnumerable<LlmToolDefinition> apiTools)
        {
            return string.Equals(mode, AgentResponseModes.NativeToolCalls, StringComparison.OrdinalIgnoreCase)
                ? _parser.ParseNative(completion, tools, apiTools)
                : _parser.Parse(completion == null ? null : completion.Content, tools);
        }

        private static LlmRequestOptions BuildOptions(string mode, IEnumerable<ToolDefinition> tools)
        {
            var native = string.Equals(mode, AgentResponseModes.NativeToolCalls, StringComparison.OrdinalIgnoreCase);
            var apiTools = ToolSchemaSupport.BuildApiTools(tools);
            return new LlmRequestOptions
            {
                ResponseFormat = string.Equals(mode, AgentResponseModes.JsonObject, StringComparison.OrdinalIgnoreCase)
                    ? LlmResponseFormats.JsonObject
                    : LlmResponseFormats.JsonSchema,
                ResponseSchemaName = AgentDecisionProtocol.SchemaName,
                ResponseSchemaJson = AgentDecisionSchemaBuilder.Build(tools, !native),
                NativeTools = native,
                Tools = apiTools
            };
        }

        private static bool CanFallback(AppSettings settings, AgentRunState state, string mode)
        {
            return state != null && state.TotalToolSteps == 0 &&
                string.Equals(mode, AgentResponseModes.JsonSchema, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(settings == null ? null : settings.AgentResponseFallbackMode, AgentResponseModes.JsonObject, StringComparison.OrdinalIgnoreCase);
        }

        private static void RememberFallback(AppSettings settings, AgentRunState state, string mode)
        {
            if (state != null) state.ResponseMode = mode;
            if (settings != null) settings.AgentResponseMode = mode;
        }

        private async Task<LlmCompletionResult> CompleteWithProgressAsync(
            AppSettings settings,
            IEnumerable<ChatMessage> messages,
            LlmRequestOptions requestOptions,
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
                if (completed && completionReported || pendingReasoning.Length == 0 && (!completed || !reasoningSeen)) return;
                Report(progress, "thinking", completed ? "Анализ завершен." : progressMessage, new ChatActivity
                {
                    Kind = "reasoning",
                    Title = completed ? "Анализ завершен" : progressMessage.TrimEnd('.'),
                    Subtitle = "Provider reasoning",
                    Status = completed ? "completed" : "running",
                    ResultMessage = pendingReasoning.ToString()
                });
                pendingReasoning.Clear();
                lastReportUtc = DateTime.UtcNow;
                completionReported = completed;
            };
            var completion = await _completeAsync(settings, messages, requestOptions, update =>
            {
                if (update == null) return;
                if (!string.IsNullOrEmpty(update.ReasoningDelta))
                {
                    reasoningSeen = true;
                    pendingReasoning.Append(update.ReasoningDelta);
                }
                if (update.Completed || pendingReasoning.Length >= 256 || pendingReasoning.Length > 0 && DateTime.UtcNow - lastReportUtc >= TimeSpan.FromMilliseconds(100)) flush(update.Completed);
            }, cancellationToken).ConfigureAwait(false);
            flush(true);
            return completion ?? new LlmCompletionResult();
        }

        private static List<ChatMessage> BuildRepairMessages(IEnumerable<ChatMessage> originalMessages, string badText, AgentPlannerParseResult parseResult, string repairPrompt, string mode)
        {
            var messages = new List<ChatMessage>(originalMessages ?? new ChatMessage[0]);
            if (!string.IsNullOrWhiteSpace(badText))
            {
                messages.Add(new ChatMessage { Role = "assistant", Content = badText.Length <= 2000 ? badText : "Invalid response omitted because it is too large." });
            }
            messages.Add(new ChatMessage
            {
                Role = "user",
                Content = (repairPrompt ?? string.Empty) + "\nValidation error: " + (parseResult == null ? string.Empty : parseResult.ErrorCode + " " + parseResult.ErrorMessage) +
                    (string.Equals(mode, AgentResponseModes.NativeToolCalls, StringComparison.OrdinalIgnoreCase)
                        ? "\nFor a tool action, return exactly one native function call. Use AgentDecision v1 content only for plan, clarify, final, or cannot_complete."
                        : "\nReturn AgentDecision v1 only. A tool decision contains exactly one tool object.")
            });
            return messages;
        }

        private static string NormalizeMode(string mode)
        {
            if (string.Equals(mode, AgentResponseModes.NativeToolCalls, StringComparison.OrdinalIgnoreCase)) return AgentResponseModes.NativeToolCalls;
            if (string.Equals(mode, AgentResponseModes.JsonObject, StringComparison.OrdinalIgnoreCase)) return AgentResponseModes.JsonObject;
            return AgentResponseModes.JsonSchema;
        }

        private static void Report(Action<string, string, ChatActivity> progress, string phase, string message, ChatActivity activity)
        {
            if (progress != null) progress(phase, message, activity);
        }
    }

    internal sealed class AgentPlannerAttempt
    {
        public LlmCompletionResult Completion { get; set; }
        public string Text { get; set; }
        public AgentPlannerParseResult ParseResult { get; set; }
        public string ResponseMode { get; set; }
        public LlmRequestOptions RequestOptions { get; set; }
        public object ContextUsage { get; set; }
    }
}
