using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.ModelProtocol
{
    public sealed class ModelProtocolClient : IMaterializedModelProtocol
    {
        private readonly LlmCompletionDelegate _completeAsync;
        private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
        private bool _useJsonObject;

        public ModelProtocolClient(LlmCompletionDelegate completeAsync)
            : this(completeAsync, (delay, token) => Task.Delay(delay, token))
        {
        }

        internal ModelProtocolClient(LlmCompletionDelegate completeAsync, Func<TimeSpan, CancellationToken, Task> delayAsync)
        {
            _completeAsync = completeAsync ?? throw new ArgumentNullException(nameof(completeAsync));
            _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
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
                if (request.CallContext == null || !request.CallContext.IsComplete)
                    throw new InvalidOperationException("Model protocol requires a complete local sequential-batch context: " +
                        (request.CallContext == null ? "missing context" : request.CallContext.Error));
                var budget = new ModelProtocolRetryBudget(settings);
                var fallbackUsed = false;
                var truncatedResponses = 0;
                var lastTruncated = false;
                string lastError = null;
                var lastErrorKind = ConversationResponseError.None;
                for (var attempt = 1; attempt <= budget.ProtocolAttemptLimit; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IReadOnlyList<ChatMessage> attemptMessages = accepted;
                    if (lastError != null)
                    {
                        if (request.CompileRepair == null)
                            throw new InvalidOperationException("Protocol repair requires the owning frozen context compiler.");
                        attemptMessages = request.CompileRepair(CreateFormatRepairMessage(lastErrorKind, lastError, attempt, budget.ProtocolAttemptLimit));
                    }
                    LlmCompletionResult completion;
                    IReadOnlyList<ChatMessage> wireMessages;
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var strict = string.Equals(options.ResponseFormat, LlmResponseFormats.JsonSchema, StringComparison.Ordinal);
                        wireMessages = strict && request.ProjectJsonSchemaMessages != null
                            ? request.ProjectJsonSchemaMessages(attemptMessages) : attemptMessages;
                        if (strict) wireMessages = ModelProtocolWire.ProjectSchemaGuidance(wireMessages);
                        options.TraceMessageContentChanged = ChangedWireMessages(attemptMessages, wireMessages);
                        string budgetError;
                        var repairReserve = lastError == null
                            ? EstimateFormatRepairOverheadTokens(settings)
                            : 0;
                        if (!TryValidatePromptBudget(
                            wireMessages,
                            settings,
                            options,
                            repairReserve,
                            ModelContextBudget.ContinuationReserveTokens(settings),
                            out budgetError))
                            return BudgetFailure(budgetError, contextUsage);
                        try
                        {
                            completion = await CompleteAsync(settings, wireMessages, options, progress, cancellationToken).ConfigureAwait(false);
                            break;
                        }
                        catch (LlmRequestException ex) when (
                            ex.Kind == LlmFailureKind.ResponseFormatUnsupported && !fallbackUsed &&
                            string.Equals(options.ResponseFormat, LlmResponseFormats.JsonSchema, StringComparison.Ordinal) &&
                            settings.FallbackToJsonObject)
                        {
                            // Compatibility fallback is separate from both retry budgets.
                            // Reuse this exact prompt/options instance, including during repair.
                            cancellationToken.ThrowIfCancellationRequested();
                            fallbackUsed = true;
                            _useJsonObject = true;
                            UseJsonObject(options);
                            if (progress != null && progress.JsonObjectFallback != null) progress.JsonObjectFallback();
                        }
                        catch (LlmRequestException ex)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            TimeSpan delay;
                            if (!budget.TryTakeProviderRetry(ex, out delay)) throw;
                            await _delayAsync(delay, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    var sourceModelAttemptId = options.TraceModelAttemptId;
                    contextUsage = ContextUsageEstimator.FromPrompt(wireMessages, settings, completion.PromptTokens,
                        options, completion.CompletionTokens, completion.TotalTokens);
                    if (!string.IsNullOrWhiteSpace(completion.RefusalContent))
                    {
                        TraceAccepted(options, true, progress);
                        return ModelProtocolResult.Refused(completion, contextUsage);
                    }
                    lastTruncated = completion.Termination == LlmCompletionTermination.OutputLimit;
                    if (lastTruncated)
                    {
                        truncatedResponses++;
                        lastErrorKind = ConversationResponseError.InvalidJson;
                        lastError = "OUTPUT_LIMIT: the provider cut off the response at its output-token limit. " +
                            "Return a shorter complete response: keep reasoning and message concise, choose one next action, " +
                            "and split large changes into smaller tool calls. Do not omit required fields. " +
                            "Nothing from the truncated response was accepted or executed.";
                        TraceRejected(options, completion, lastError, attempt - 1, progress);
                        // One bounded correction within the protocol budget. Repeating
                        // the same token-limited generation ten times cannot make progress.
                        cancellationToken.ThrowIfCancellationRequested();
                        if (truncatedResponses >= 2) break;
                        continue;
                    }
                    if (completion.Termination == LlmCompletionTermination.Filtered)
                        return ModelProtocolResult.Failed(new ModelProtocolFailure(ModelProtocolFailureKind.Provider,
                            "Провайдер остановил ответ фильтром содержимого."), contextUsage);
                    var parsed = ModelProtocolWire.Parse(completion.Content, request.CallableTools, request.RunnableCatalog, request.CallContext,
                        string.Equals(options.ResponseFormat, LlmResponseFormats.JsonSchema, StringComparison.Ordinal));
                    if (parsed.Success)
                    {
                        TraceAccepted(options, false, progress);
                        return ModelProtocolResult.Accepted(parsed.Response, completion, contextUsage, sourceModelAttemptId);
                    }
                    lastError = parsed.Error;
                    lastErrorKind = parsed.ErrorKind;
                    // Preserve the existing zero-based diagnostic index; the limit and
                    // repair instruction count total protocol responses, starting at one.
                    TraceRejected(options, completion, parsed.ErrorCode + ": " + lastError, attempt - 1, progress);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (lastTruncated)
                    return ModelProtocolResult.Failed(new ModelProtocolFailure(ModelProtocolFailureKind.OutputTruncated,
                        "Провайдер обрезал ответ по лимиту выходных токенов. Полный ответ получить не удалось; " +
                        "обрезанные действия не выполнялись. Увеличьте лимит ответа или уменьшите объём следующего изменения."), contextUsage);
                return ModelProtocolResult.Failed(new ModelProtocolFailure(ModelProtocolFailureKind.ProtocolExhausted,
                    "Ответ модели не выполнен после " + budget.ProtocolAttemptLimit + " попыток получить корректный ответ: " + lastError), contextUsage);
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
            finally
            {
                options.TraceMessageContentChanged = null;
            }
        }

        private static bool[] ChangedWireMessages(IReadOnlyList<ChatMessage> accepted,
            IReadOnlyList<ChatMessage> wire)
        {
            if (ReferenceEquals(accepted, wire)) return new bool[0];
            var sources = (accepted ?? new ChatMessage[0]).Where(VisibleMessage).ToArray();
            var targets = (wire ?? new ChatMessage[0]).Where(VisibleMessage).ToArray();
            var changed = new bool[targets.Length];
            for (var index = 0; index < targets.Length; index++)
                changed[index] = index >= sources.Length || sources.Length != targets.Length ||
                    !string.Equals(sources[index].Id, targets[index].Id, StringComparison.Ordinal) ||
                    !string.Equals(sources[index].Role, targets[index].Role, StringComparison.Ordinal) ||
                    !string.Equals(sources[index].Content, targets[index].Content, StringComparison.Ordinal);
            return changed;
        }

        private static bool VisibleMessage(ChatMessage message)
        { return message != null && !message.ExcludeFromModelContext && !string.IsNullOrWhiteSpace(message.Role); }

        private async Task<LlmCompletionResult> CompleteAsync(AppSettings settings, IReadOnlyList<ChatMessage> messages,
            LlmRequestOptions options, ModelProtocolProgress progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            options.TraceModelAttemptId = Guid.NewGuid().ToString("N");
            options.TraceRequestId = null;
            if (progress != null && progress.AttemptStarted != null) progress.AttemptStarted(settings.StreamResponses,
                string.Equals(options.ResponseFormat, LlmResponseFormats.JsonSchema, StringComparison.Ordinal));
            var completion = await _completeAsync(settings, messages, options,
                progress == null ? null : progress.StreamUpdate, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
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

        private static bool TryValidatePromptBudget(
            IReadOnlyList<ChatMessage> messages,
            AppSettings settings,
            LlmRequestOptions options,
            int repairReserveTokens,
            int continuationReserveTokens,
            out string error)
        {
            var inputBudget = ModelContextBudget.InputBudgetTokens(settings);
            var requestTokens = ModelContextBudget.EstimateRequestTokens(messages, options, settings);
            var estimated = ModelContextBudget.EstimateAdmittedRequestTokens(
                messages,
                options,
                settings,
                repairReserveTokens,
                continuationReserveTokens);
            if (estimated <= inputBudget) { error = null; return true; }
            error = "Выполнение остановлено до следующего запроса модели: материализованный запрос занимает ≈" +
                requestTokens + " токенов, обязательные резервы repair/continuation — ≈" +
                (Math.Max(0, repairReserveTokens) + Math.Max(0, continuationReserveTokens)) +
                ", доступный входной лимит — " + inputBudget +
                ". Сузьте диапазон/объём результата, сожмите контекст или начните новый чат.";
            return false;
        }

        private static ChatMessage CreateFormatRepairMessage(ConversationResponseError kind, string error, int attempt, int maxAttempts)
        {
            return new ChatMessage
            {
                Role = "user", ProtocolMessage = true,
                Content = ModelProtocolWire.FormatRepairContent(kind, error, attempt, maxAttempts)
            };
        }

        public static int EstimateFormatRepairOverheadTokens(AppSettings settings)
        {
            var attempts = ModelProtocolRetryBudget.GetProtocolAttemptLimit(settings);
            if (attempts <= 1) return 0;
            // Three-byte BMP characters are the maximum UTF-8 cost per valid
            // UTF-16 code unit; include the truncation suffix used at the bound.
            var maximumError = new string('\u4e00', ModelProtocolWire.MaximumFormatRepairErrorCharacters) + "...[truncated]";
            return ModelContextBudget.EstimateMessageTokens(
                CreateFormatRepairMessage(ConversationResponseError.MissingFinishResults, maximumError, attempts, attempts), settings);
        }

        private static void TraceRejected(LlmRequestOptions options, LlmCompletionResult completion, string error, int attempt, ModelProtocolProgress progress)
        {
            WriteOptionalTrace(options, new LlmTraceRecord
            {
                Type = "rejected", RequestId = options.TraceRequestId, Purpose = options.TracePurpose,
                Model = options.TraceSession == null ? null : options.TraceSession.Model,
                ResponseFormat = options.ResponseFormat, Attempt = attempt, FailureKind = "invalid_model_response",
                Error = error, PayloadJson = completion.Content, PayloadContentType = "application/json"
            }, progress);
        }

        private static void TraceAccepted(LlmRequestOptions options, bool providerRefusal, ModelProtocolProgress progress)
        {
            WriteOptionalTrace(options, new LlmTraceRecord
            {
                Type = "accepted", RequestId = options.TraceRequestId, Purpose = options.TracePurpose,
                Model = options.TraceSession == null ? null : options.TraceSession.Model,
                ResponseFormat = options.ResponseFormat, ResponseStatus = providerRefusal ? AgentResponseStatuses.Refused : null,
                // Parser diagnostics precede durable runtime acceptance and ID allocation.
                ToolCallIds = new string[0]
            }, progress);
        }

        private static void WriteOptionalTrace(LlmRequestOptions options, LlmTraceRecord record, ModelProtocolProgress progress)
        {
            if (options.TraceSink == null) return;
            try
            {
                options.TraceSink(record);
            }
            catch (Exception)
            {
                try { if (progress != null && progress.OptionalTraceFailed != null) progress.OptionalTraceFailed(); }
                catch (Exception) { /* Parser diagnostic failure cannot change a verdict or consume repair budget. */ }
            }
        }
    }
}
