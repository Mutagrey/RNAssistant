using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
                    throw new InvalidOperationException("Model protocol requires a complete accepted-run call context: " +
                        (request.CallContext == null ? "missing context" : request.CallContext.Error));
                var budget = new ModelProtocolRetryBudget(settings);
                var fallbackUsed = false;
                string lastError = null;
                for (var attempt = 1; attempt <= budget.ProtocolAttemptLimit; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IReadOnlyList<ChatMessage> attemptMessages = accepted;
                    if (lastError != null)
                    {
                        attemptMessages = new List<ChatMessage>(accepted)
                        {
                            CreateFormatRepairMessage(lastError, attempt, budget.ProtocolAttemptLimit)
                        };
                    }
                    LlmCompletionResult completion;
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string budgetError;
                        if (!TryValidatePromptBudget(attemptMessages, settings, options, out budgetError))
                            return BudgetFailure(budgetError, contextUsage);
                        try
                        {
                            completion = await CompleteAsync(settings, attemptMessages, options, progress, cancellationToken).ConfigureAwait(false);
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

                    contextUsage = ContextUsageEstimator.FromPrompt(attemptMessages, settings, completion.PromptTokens, options);
                    if (!string.IsNullOrWhiteSpace(completion.RefusalContent))
                    {
                        TraceAccepted(options, null, true, progress);
                        return ModelProtocolResult.Refused(completion, contextUsage);
                    }
                    var parsed = ModelProtocolWire.Parse(completion.Content, request.CallableTools, request.RunnableCatalog, request.CallContext);
                    if (parsed.Success)
                    {
                        TraceAccepted(options, parsed.Response, false, progress);
                        return ModelProtocolResult.Accepted(parsed.Response, completion, contextUsage);
                    }
                    lastError = parsed.Error;
                    // Preserve the existing zero-based diagnostic index; the limit and
                    // repair instruction count total protocol responses, starting at one.
                    TraceRejected(options, completion, lastError, attempt - 1);
                    cancellationToken.ThrowIfCancellationRequested();
                }
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
                    "Return a new response to the current user request as exactly one conversation-response-v3 JSON object " +
                    "containing only message (string) and tool_calls (array). Never return status or any other root field. " +
                    "Do not use Markdown, fences, or surrounding prose. Empty tool_calls ends the model loop; wording proves no effect. " +
                    "Every call requires an id not used anywhere in this accepted run, an exact name, and object arguments. " +
                    "Write, external, confirmation-required and unclassified calls must be singleton; batch only independent local reads. " +
                    "Follow the error action exactly. " +
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

        private static void TraceAccepted(LlmRequestOptions options, ConversationResponse response, bool providerRefusal, ModelProtocolProgress progress)
        {
            if (options.TraceSink == null) return;
            try
            {
                options.TraceSink(new LlmTraceRecord
                {
                    Type = "accepted", RequestId = options.TraceRequestId, Purpose = options.TracePurpose,
                    Model = options.TraceSession == null ? null : options.TraceSession.Model,
                    ResponseFormat = options.ResponseFormat, ResponseStatus = providerRefusal ? AgentResponseStatuses.Refused : null,
                    ToolCallIds = response == null ? new string[0] : response.ToolCalls.Select(call => call.Id).ToArray()
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
