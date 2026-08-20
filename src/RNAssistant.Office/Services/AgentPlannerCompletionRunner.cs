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
        private readonly LlmCompletionDelegate _completeAsync;
        private readonly AgentPlannerResponseParser _parser;

        public AgentPlannerCompletionRunner(LlmCompletionDelegate completeAsync)
        {
            _completeAsync = completeAsync;
            _parser = new AgentPlannerResponseParser();
        }

        public async Task<AgentPlannerAttempt> CompleteAsync(
            AppSettings settings,
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition> tools,
            AgentRunState state,
            LlmRequestOptions preparedOptions,
            Action<string, string, ChatActivity> progress,
            string progressMessage,
            string repairMessage,
            string repairPrompt,
            CancellationToken cancellationToken)
        {
            settings = settings ?? new AppSettings();
            var mode = NormalizeMode(string.IsNullOrWhiteSpace(state == null ? null : state.ResponseMode)
                ? settings.AgentResponseMode
                : state.ResponseMode);
            var activeMessages = WithResponseMode(messages, mode);
            var baseMessages = activeMessages;
            var options = preparedOptions ?? BuildOptions(mode, tools);
            var runCache = options.RunCache;
            var rejectedResponses = new List<AgentPlannerRejectedResponse>();
            var maxFormatRetries = Math.Max(1, settings.MaxAgentFormatRetries);
            LlmCompletionResult completion;
            try
            {
                completion = await CompleteResilientAsync(settings, activeMessages, options, progress, progressMessage, cancellationToken).ConfigureAwait(false);
            }
            catch (LlmRequestException ex) when (
                ex.Kind == LlmFailureKind.ResponseFormatUnsupported &&
                CanFallback(settings, state, mode))
            {
                mode = AgentResponseModes.JsonObject;
                RememberFallback(settings, state, mode);
                options = RebuildOptions(mode, tools, runCache, options);
                baseMessages = WithResponseMode(messages, mode);
                activeMessages = baseMessages;
                Report(progress, "fallback", "Endpoint не принял json_schema; повторяю через json_object.", null);
                completion = await CompleteResilientAsync(settings, activeMessages, options, progress, progressMessage, cancellationToken).ConfigureAwait(false);
            }

            var parsed = ParseCompletion(completion, mode, tools, options);
            if (!parsed.Success && CanFallback(settings, state, mode))
            {
                var rejected = CreateRejectedResponse(
                    completion,
                    parsed,
                    mode,
                    "json_object_fallback",
                    0,
                    maxFormatRetries);
                rejectedResponses.Add(rejected);
                mode = AgentResponseModes.JsonObject;
                RememberFallback(settings, state, mode);
                options = RebuildOptions(mode, tools, runCache, options);
                baseMessages = WithResponseMode(messages, mode);
                activeMessages = baseMessages;
                Report(progress, "fallback", "Ответ json_schema не прошёл проверку; повторяю через json_object.", rejected.Activity);
                completion = await CompleteResilientAsync(settings, activeMessages, options, progress, progressMessage, cancellationToken).ConfigureAwait(false);
                parsed = ParseCompletion(completion, mode, tools, options);
            }

            for (var retry = 1; !parsed.Success && retry <= maxFormatRetries; retry++)
            {
                var rejected = CreateRejectedResponse(
                    completion,
                    parsed,
                    mode,
                    "format_retry",
                    retry,
                    maxFormatRetries);
                rejectedResponses.Add(rejected);
                var retryMessage = (repairMessage ?? "Исправляю формат ответа...") + " (" + retry + "/" + maxFormatRetries + ")";
                Report(progress, "repairing", retryMessage, rejected.Activity);
                activeMessages = BuildRepairMessages(baseMessages, parsed, repairPrompt, mode, retry, maxFormatRetries);
                completion = await CompleteResilientAsync(settings, activeMessages, options, progress, repairMessage, cancellationToken).ConfigureAwait(false);
                parsed = ParseCompletion(completion, mode, tools, options);
                if (retry == maxFormatRetries) break;
            }

            return new AgentPlannerAttempt
            {
                Completion = completion ?? new LlmCompletionResult(),
                Text = completion == null ? string.Empty : completion.Content ?? string.Empty,
                ParseResult = parsed,
                ResponseMode = mode,
                RequestOptions = options,
                RejectedResponses = rejectedResponses,
                ContextUsage = ContextUsageEstimator.FromPrompt(activeMessages, settings, completion == null ? null : completion.PromptTokens, options)
            };
        }

        private AgentPlannerParseResult ParseCompletion(
            LlmCompletionResult completion,
            string mode,
            IEnumerable<ToolDefinition> tools,
            LlmRequestOptions requestOptions)
        {
            if (completion != null &&
                !string.IsNullOrWhiteSpace(completion.RefusalContent) &&
                string.IsNullOrWhiteSpace(completion.Content) &&
                (completion.ToolCalls == null || completion.ToolCalls.Count == 0))
            {
                return AgentPlannerParseResult.Fail(
                    "provider_refusal",
                    "The provider returned a refusal instead of an AgentDecision. Re-evaluate the original request and return cannot_complete only when a required capability is genuinely unavailable.");
            }

            var parsed = string.Equals(mode, AgentResponseModes.NativeToolCalls, StringComparison.OrdinalIgnoreCase)
                ? _parser.ParseNative(completion, tools, requestOptions == null ? null : requestOptions.Tools)
                : _parser.Parse(completion == null ? null : completion.Content, tools);
            if (parsed.Success && requestOptions != null && !requestOptions.PlanDecisionAllowed &&
                (string.Equals(parsed.Response.Kind, AgentResponseKinds.Plan, StringComparison.OrdinalIgnoreCase) ||
                 parsed.Response.Plan != null && parsed.Response.Plan.Count > 0))
            {
                return AgentPlannerParseResult.Fail(
                    "plan_not_allowed",
                    "kind=plan is unavailable for the rest of this run. Keep the current plan and choose tool, clarify, final, or cannot_complete.");
            }
            return parsed;
        }

        internal static LlmRequestOptions BuildOptions(
            string mode,
            IEnumerable<ToolDefinition> tools,
            LlmRunCache runCache = null,
            bool includePlanDecision = true)
        {
            var native = string.Equals(mode, AgentResponseModes.NativeToolCalls, StringComparison.OrdinalIgnoreCase);
            var jsonObject = string.Equals(mode, AgentResponseModes.JsonObject, StringComparison.OrdinalIgnoreCase);
            var apiTools = native
                ? ToolSchemaSupport.BuildApiTools(tools)
                : ToolSchemaSupport.BuildApiToolNames(tools);
            return new LlmRequestOptions
            {
                ResponseFormat = jsonObject ? LlmResponseFormats.JsonObject : LlmResponseFormats.JsonSchema,
                ResponseSchemaName = jsonObject ? null : AgentDecisionProtocol.SchemaName,
                ResponseSchemaJson = jsonObject ? null : AgentDecisionSchemaBuilder.Build(tools, !native, includePlanDecision),
                NativeTools = native,
                Tools = apiTools,
                RunCache = runCache,
                PlanDecisionAllowed = includePlanDecision
            };
        }

        private static LlmRequestOptions RebuildOptions(
            string mode,
            IEnumerable<ToolDefinition> tools,
            LlmRunCache runCache,
            LlmRequestOptions previous)
        {
            var options = BuildOptions(
                mode,
                tools,
                runCache,
                previous == null || previous.PlanDecisionAllowed);
            options.ReasoningEnabled = previous == null ? (bool?)null : previous.ReasoningEnabled;
            return options;
        }

        private static bool CanFallback(AppSettings settings, AgentRunState state, string mode)
        {
            return state != null && state.TotalToolSteps == 0 &&
                string.Equals(mode, AgentResponseModes.JsonSchema, StringComparison.OrdinalIgnoreCase) &&
                (settings == null || settings.FallbackToJsonObject);
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

        private async Task<LlmCompletionResult> CompleteResilientAsync(
            AppSettings settings,
            IEnumerable<ChatMessage> messages,
            LlmRequestOptions requestOptions,
            Action<string, string, ChatActivity> progress,
            string progressMessage,
            CancellationToken cancellationToken)
        {
            try
            {
                return await CompleteWithProgressAsync(
                    settings,
                    messages,
                    requestOptions,
                    progress,
                    progressMessage,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (LlmRequestException ex) when (IsRetryableTransportFailure(ex))
            {
                Report(progress, "retrying", "Временная ошибка ответа модели; повторяю тот же запрос один раз.", new ChatActivity
                {
                    Kind = "diagnostic",
                    Title = "Повтор запроса модели",
                    Subtitle = ex.Kind.ToString(),
                    Status = "running",
                    ExecutionStatus = "llm_transport_retry",
                    Retryable = true,
                    ResultMessage = ex.Message
                });
                return await CompleteWithProgressAsync(
                    settings,
                    messages,
                    requestOptions,
                    progress,
                    progressMessage,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private static bool IsRetryableTransportFailure(LlmRequestException error)
        {
            return error != null &&
                (error.Kind == LlmFailureKind.Network ||
                 error.Kind == LlmFailureKind.TransientServer ||
                 error.Kind == LlmFailureKind.InvalidResponse);
        }

        private static List<ChatMessage> BuildRepairMessages(
            IEnumerable<ChatMessage> originalMessages,
            AgentPlannerParseResult parseResult,
            string repairPrompt,
            string mode,
            int retry,
            int retryLimit)
        {
            var messages = new List<ChatMessage>(originalMessages ?? new ChatMessage[0]);
            var refusalGuidance = parseResult != null && string.Equals(parseResult.ErrorCode, "provider_refusal", StringComparison.OrdinalIgnoreCase)
                ? "\nThe upstream refusal is not executable output. Re-evaluate the original user request under the runtime instructions; return cannot_complete JSON only if a required capability is genuinely unavailable."
                : string.Empty;
            messages.Add(new ChatMessage
            {
                Role = "user",
                Content = (repairPrompt ?? string.Empty) +
                    "\nActive responseMode: " + mode +
                    "\nFormat retry: " + retry + " of " + retryLimit +
                    "\nThe rejected response is intentionally omitted from model context." +
                    refusalGuidance +
                    "\nValidation error: " + (parseResult == null ? string.Empty : parseResult.ErrorCode + " " + parseResult.ErrorMessage)
            });
            return messages;
        }

        private static AgentPlannerRejectedResponse CreateRejectedResponse(
            LlmCompletionResult completion,
            AgentPlannerParseResult parseResult,
            string mode,
            string recoveryAction,
            int retry,
            int retryLimit)
        {
            var rejected = new AgentPlannerRejectedResponse
            {
                Completion = completion ?? new LlmCompletionResult(),
                RawText = CompletionDiagnosticText(completion),
                ParseResult = parseResult,
                ResponseMode = mode,
                RecoveryAction = recoveryAction,
                RetryNumber = retry,
                RetryLimit = retryLimit
            };
            rejected.Activity = AgentRunPresentation.CreatePlannerRecoveryActivity(rejected);
            return rejected;
        }

        private static string CompletionDiagnosticText(LlmCompletionResult completion)
        {
            if (completion == null) return string.Empty;
            return !string.IsNullOrWhiteSpace(completion.Content)
                ? completion.Content
                : completion.RefusalContent ?? string.Empty;
        }

        private static IReadOnlyList<ChatMessage> WithResponseMode(IReadOnlyList<ChatMessage> source, string mode)
        {
            var messages = new List<ChatMessage>(source ?? new ChatMessage[0]);
            for (var index = 0; index < messages.Count; index++)
            {
                var message = messages[index];
                var content = message == null ? null : message.Content;
                var marker = content == null ? -1 : content.IndexOf("\nresponseMode:", StringComparison.Ordinal);
                if (marker < 0 || content.IndexOf("\nROUTE:", StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                var lineStart = marker + 1;
                var lineEnd = content.IndexOf('\n', lineStart);
                if (lineEnd < 0) lineEnd = content.Length;
                var replaced = content.Substring(0, lineStart) +
                    "responseMode: " + mode +
                    content.Substring(lineEnd);
                messages[index] = new ChatMessage
                {
                    Id = message.Id,
                    Role = message.Role,
                    Content = replaced,
                    ExcludeFromModelContext = message.ExcludeFromModelContext,
                    Attachments = message.Attachments == null
                        ? new List<ChatAttachment>()
                        : new List<ChatAttachment>(message.Attachments),
                    CreatedUtc = message.CreatedUtc
                };
                break;
            }
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
        public IReadOnlyList<AgentPlannerRejectedResponse> RejectedResponses { get; set; }
        public object ContextUsage { get; set; }
    }

    internal sealed class AgentPlannerRejectedResponse
    {
        public LlmCompletionResult Completion { get; set; }
        public string RawText { get; set; }
        public AgentPlannerParseResult ParseResult { get; set; }
        public string ResponseMode { get; set; }
        public string RecoveryAction { get; set; }
        public int RetryNumber { get; set; }
        public int RetryLimit { get; set; }
        public ChatActivity Activity { get; set; }
    }
}
