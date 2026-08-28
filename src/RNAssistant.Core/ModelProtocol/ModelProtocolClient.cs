using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.ModelProtocol
{
    public sealed class ModelProtocolClient : IModelProtocol
    {
        private readonly LlmCompletionDelegate _completeAsync;
        private readonly AgentResponseParser _parser = new AgentResponseParser();
        private bool _useJsonObject;

        public ModelProtocolClient(LlmCompletionDelegate completeAsync)
        {
            _completeAsync = completeAsync ?? throw new ArgumentNullException(nameof(completeAsync));
        }

        public async Task<ModelProtocolResult> GetResponseAsync(
            ModelProtocolRequest request,
            ModelProtocolProgress progress,
            CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var settings = request.Settings ?? new AppSettings();
            var accepted = (request.AcceptedMessages ?? new ChatMessage[0]).ToArray();
            var options = request.Options ?? new LlmRequestOptions { ResponseFormat = LlmResponseFormats.JsonObject };
            if (_useJsonObject) UseJsonObject(options);
            object contextUsage = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                string budgetError;
                if (!TryValidatePromptBudget(accepted, settings, options, out budgetError))
                    return BudgetFailure(budgetError, contextUsage);

                LlmCompletionResult completion;
                try
                {
                    completion = await CompleteAsync(settings, accepted, options, progress, cancellationToken).ConfigureAwait(false);
                }
                catch (LlmRequestException ex) when (
                    ex.Kind == LlmFailureKind.ResponseFormatUnsupported &&
                    string.Equals(options.ResponseFormat, LlmResponseFormats.JsonSchema, StringComparison.Ordinal) &&
                    settings.FallbackToJsonObject)
                {
                    // The existing run-local compatibility fallback, not a protocol retry.
                    _useJsonObject = true;
                    UseJsonObject(options);
                    if (progress != null && progress.JsonObjectFallback != null) progress.JsonObjectFallback();
                    if (!TryValidatePromptBudget(accepted, settings, options, out budgetError))
                        return BudgetFailure(budgetError, contextUsage);
                    completion = await CompleteAsync(settings, accepted, options, progress, cancellationToken).ConfigureAwait(false);
                }
                contextUsage = ContextUsageEstimator.FromPrompt(accepted, settings, completion.PromptTokens, options);
                var parsed = Parse(completion, request);
                if (!parsed.Success) TraceRejected(options, completion, parsed.Error, 0);

                // Phase 2A preserves the old setting's retry semantics. R20 (total
                // attempts vs retries) is an explicit remaining Phase 2 task.
                var configuredRetries = settings.MaxAgentFormatRetries > 0
                    ? settings.MaxAgentFormatRetries : new AppSettings().MaxAgentFormatRetries;
                var maxRetries = Math.Max(1, Math.Min(AppSettings.MaximumAgentFormatRetries, configuredRetries));
                for (var retry = 1; !parsed.Success && retry <= maxRetries; retry++)
                {
                    var repairMessages = new List<ChatMessage>(accepted)
                    {
                        CreateFormatRepairMessage(parsed.Error, retry, maxRetries)
                    };
                    if (!TryValidatePromptBudget(repairMessages, settings, options, out budgetError))
                        return BudgetFailure(budgetError, contextUsage);
                    completion = await CompleteAsync(settings, repairMessages, options, progress, cancellationToken).ConfigureAwait(false);
                    contextUsage = ContextUsageEstimator.FromPrompt(repairMessages, settings, completion.PromptTokens, options);
                    parsed = Parse(completion, request);
                    if (!parsed.Success) TraceRejected(options, completion, parsed.Error, retry);
                }
                if (!parsed.Success)
                    return ModelProtocolResult.Failed(new ModelProtocolFailure(ModelProtocolFailureKind.ProtocolExhausted,
                        "Ответ модели не выполнен после " + maxRetries + " попыток исправить формат: " + parsed.Error), contextUsage);

                TraceAccepted(options, parsed.Response, progress);
                return ModelProtocolResult.Accepted(parsed.Response, completion, contextUsage);
            }
            catch (OperationCanceledException ex)
            {
                return ModelProtocolResult.Failed(new ModelProtocolFailure(ModelProtocolFailureKind.Cancelled, ex.Message, ex), contextUsage);
            }
            catch (LlmRequestException ex)
            {
                // Transport failures never consume the format-repair budget or become tool errors.
                return ModelProtocolResult.Failed(new ModelProtocolFailure(ModelProtocolFailureKind.Provider, ex.Message, ex), contextUsage);
            }
            catch (Exception ex)
            {
                return ModelProtocolResult.Failed(new ModelProtocolFailure(ModelProtocolFailureKind.Infrastructure, ex.Message, ex), contextUsage);
            }
        }

        private AgentResponseParseResult Parse(LlmCompletionResult completion, ModelProtocolRequest request)
        {
            if (string.IsNullOrWhiteSpace(completion.Content) && !string.IsNullOrWhiteSpace(completion.RefusalContent))
                return AgentResponseParseResult.Ok(new AgentResponse
                {
                    Status = AgentResponseStatuses.Refused,
                    Message = completion.RefusalContent
                });
            var parsed = _parser.Parse(completion.Content, request.CallableTools, request.RunnableCatalog);
            if (parsed.Success) parsed.Response.Message = parsed.Response.Message.Trim();
            return parsed;
        }

        private async Task<LlmCompletionResult> CompleteAsync(AppSettings settings, IReadOnlyList<ChatMessage> messages,
            LlmRequestOptions options, ModelProtocolProgress progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            options.TraceModelAttemptId = Guid.NewGuid().ToString("N");
            options.TraceRequestId = null;
            if (progress != null && progress.AttemptStarted != null) progress.AttemptStarted(settings.StreamResponses);
            var completion = await _completeAsync(settings, messages, options,
                progress == null ? null : progress.StreamUpdate, cancellationToken).ConfigureAwait(false);
            if (progress != null && progress.AttemptCompleted != null) progress.AttemptCompleted();
            if (completion == null) throw new InvalidOperationException("Model returned no completion.");
            return completion;
        }

        private static void UseJsonObject(LlmRequestOptions options)
        {
            options.ResponseFormat = LlmResponseFormats.JsonObject;
            options.ResponseSchemaName = null;
            options.ResponseSchemaJson = null;
        }

        private static ModelProtocolResult BudgetFailure(string message, object contextUsage)
        {
            return ModelProtocolResult.Failed(new ModelProtocolFailure(ModelProtocolFailureKind.PromptBudgetExceeded, message), contextUsage);
        }

        private static bool TryValidatePromptBudget(IReadOnlyList<ChatMessage> messages, AppSettings settings,
            LlmRequestOptions options, out string error)
        {
            var inputBudget = ModelContextBudget.InputBudgetTokens(settings);
            var estimated = ModelContextBudget.EstimateMessagesTokens(messages, settings) +
                ModelContextBudget.EstimateRequestOptionsTokens(options, settings);
            if (estimated <= inputBudget) { error = null; return true; }
            error = "Выполнение остановлено до следующего запроса модели: контекст занимает ≈" + estimated +
                " токенов при доступном лимите " + inputBudget +
                ". Сузьте диапазон/объём результата или начните новый чат.";
            return false;
        }

        private static ChatMessage CreateFormatRepairMessage(string error, int attempt, int maxAttempts)
        {
            var root = new JObject
            {
                ["error"] = string.IsNullOrWhiteSpace(error) ? "Invalid Agent JSON response." : error.Trim(),
                ["attempt"] = attempt,
                ["max_attempts"] = maxAttempts,
                ["instruction"] =
                    "Return a new response to the current user request as exactly one conversation-response-v2 JSON object " +
                    "with required status, message, and tool_calls. Do not use Markdown, fences, or surrounding prose. " +
                    "Choose tool_calls before status. If tool_calls is empty, never use in_progress; use completed, " +
                    "awaiting_user, blocked, or refused. If tool_calls is non-empty, use in_progress. planned is unavailable. " +
                    "Message wording never determines status. " +
                    "Every call requires a unique id, exact name, and object arguments. Follow the error action exactly. " +
                    "If a known tool schema is not loaded, replace the rejected call with common.capabilities_read for that exact id, " +
                    "wait for its successful complete tool-schema result, and call the loaded tool only in a later response."
            };
            return new ChatMessage { Role = "user", Content = "FORMAT_REPAIR:\n" + root.ToString(Formatting.None), ProtocolMessage = true };
        }

        private static void TraceRejected(LlmRequestOptions options, LlmCompletionResult completion, string error, int attempt)
        {
            if (options.TraceSink == null) return;
            options.TraceSink(new LlmTraceRecord
            {
                Type = "rejected", RequestId = options.TraceRequestId, Purpose = options.TracePurpose,
                Model = options.TraceSession == null ? null : options.TraceSession.Model,
                ResponseFormat = options.ResponseFormat, Attempt = attempt, FailureKind = "invalid_model_response",
                Error = error, PayloadJson = completion.Content, PayloadContentType = "application/json"
            });
        }

        private static void TraceAccepted(LlmRequestOptions options, AgentResponse response, ModelProtocolProgress progress)
        {
            if (options.TraceSink == null) return;
            try
            {
                options.TraceSink(new LlmTraceRecord
                {
                    Type = "accepted", RequestId = options.TraceRequestId, Purpose = options.TracePurpose,
                    Model = options.TraceSession == null ? null : options.TraceSession.Model,
                    ResponseFormat = options.ResponseFormat, ResponseStatus = response.Status,
                    ToolCallIds = response.ToolCalls.Select(call => call.Id).ToArray()
                });
            }
            catch (Exception)
            {
                try { if (progress != null && progress.OptionalTraceFailed != null) progress.OptionalTraceFailed(); }
                catch (Exception) { /* Optional diagnostic failure cannot change an accepted result. */ }
            }
        }
    }
}
