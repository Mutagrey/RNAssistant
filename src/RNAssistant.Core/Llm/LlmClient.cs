using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Llm
{
    public sealed class LlmClient
    {
        private const int MaxReasoningCustomJsonChars = 32768;
        private readonly Func<string> _apiKeyProvider;
        private readonly LlmMessageBuilder _messageBuilder;
        private readonly Action<string> _debugLog;
        private readonly Action<LlmRequestDiagnosticUpdate> _diagnosticProgress;

        public LlmClient(Func<string> apiKeyProvider, Func<ChatAttachment, byte[]> attachmentReader = null)
            : this(apiKeyProvider, attachmentReader, null, null)
        {
        }

        public LlmClient(
            Func<string> apiKeyProvider,
            Func<ChatAttachment, byte[]> attachmentReader,
            LlmAttachmentTextReader attachmentTextReader,
            LlmModelImageProvider modelImageProvider)
            : this(apiKeyProvider, attachmentReader, attachmentTextReader, modelImageProvider, null)
        {
        }

        public LlmClient(
            Func<string> apiKeyProvider,
            Func<ChatAttachment, byte[]> attachmentReader,
            LlmAttachmentTextReader attachmentTextReader,
            LlmModelImageProvider modelImageProvider,
            Action<string> debugLog)
            : this(apiKeyProvider, attachmentReader, attachmentTextReader, modelImageProvider, debugLog, null)
        {
        }

        public LlmClient(
            Func<string> apiKeyProvider,
            Func<ChatAttachment, byte[]> attachmentReader,
            LlmAttachmentTextReader attachmentTextReader,
            LlmModelImageProvider modelImageProvider,
            Action<string> debugLog,
            Action<LlmRequestDiagnosticUpdate> diagnosticProgress)
        {
            _apiKeyProvider = apiKeyProvider;
            _messageBuilder = new LlmMessageBuilder(attachmentReader, attachmentTextReader, modelImageProvider);
            _debugLog = debugLog;
            _diagnosticProgress = diagnosticProgress;
        }

        public async Task<LlmCompletionResult> CompleteAsync(
            AppSettings settings,
            IEnumerable<ChatMessage> messages,
            LlmRequestOptions requestOptions,
            Action<LlmStreamUpdate> streamProgress,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }
            requestOptions = requestOptions ?? new LlmRequestOptions();
            var requestDiagnostics = new LlmRequestDiagnosticsTracker(
                settings,
                requestOptions.DiagnosticProgress,
                _diagnosticProgress,
                _debugLog,
                requestOptions.ContextPreparation);
            var requestTraceRecorded = false;
            var terminalTraceRecorded = false;
            var traceEndpoint = string.Empty;
            var traceMessageCount = 0;
            int? traceEstimatedPromptTokens = null;
            string failurePayload = null;
            int? responseStatusCode = null;
            LlmStreamTraceBuffer streamTrace = null;
            try
            {
                var apiKey = _apiKeyProvider == null ? null : _apiKeyProvider();
                var url = CombineUrl(settings.BaseUrl, "/v1/chat/completions");
                Uri requestUri;
                if (!Uri.TryCreate(url, UriKind.Absolute, out requestUri))
                {
                    throw new InvalidOperationException("Invalid LLM endpoint URL: " + url);
                }
                traceEndpoint = requestUri.GetLeftPart(UriPartial.Path);

                var messageList = messages as IList<ChatMessage> ??
                    (messages == null ? new List<ChatMessage>() : messages.ToList());
                var apiBuild = _messageBuilder.Build(messageList, settings, requestOptions, cancellationToken);
                var apiMessages = apiBuild.Messages;
                var hasImages = apiBuild.HasImages;
                var hasAudio = apiBuild.HasAudio;
                if (apiMessages.Count == 0)
                {
                    throw new InvalidOperationException("LLM request has no messages.");
                }

                var baseEstimatedRequestTokens = apiBuild.BaseEstimatedPromptTokens +
                    ModelContextBudget.EstimateRequestOptionsTokens(requestOptions);
                var scaledRequestTokens = apiBuild.EstimatedPromptTokens +
                    ModelContextBudget.EstimateRequestOptionsTokens(requestOptions, settings);
                var estimatedRequestTokens = hasImages || hasAudio
                    ? TokenEstimateCalibration.AddPromptIntercept(settings, scaledRequestTokens)
                    : TokenEstimateCalibration.PredictPromptTokens(settings, baseEstimatedRequestTokens);
                var body = BuildRequestBody(settings, apiMessages, estimatedRequestTokens, requestOptions, true);
                var requestPayloadBytes = LlmHttpTransport.SerializeJson(body);
                traceMessageCount = apiMessages.Count;
                traceEstimatedPromptTokens = estimatedRequestTokens;
                var wireMessages = DescribeWireMessages(messageList, requestOptions.TraceContextReceipt,
                    requestOptions.TraceMessageContentChanged, apiBuild.MessageParts,
                    body["messages"] as JArray, settings);
                Trace(requestOptions, new LlmTraceRecord
                {
                    Type = "request",
                    Streaming = settings.StreamResponses,
                    ContextMessages = wireMessages,
                    RequestComposition = LlmRequestCompositionBuilder.Describe(apiBuild, messageList,
                        requestOptions.TraceContextReceipt, requestOptions,
                        requestOptions.TraceMessageContentChanged),
                    RequestId = requestDiagnostics.RequestId,
                    Purpose = requestOptions.TracePurpose,
                    Endpoint = traceEndpoint,
                    Model = settings.Model,
                    ResponseFormat = requestOptions.ResponseFormat,
                    MessageCount = apiMessages.Count,
                    EstimatedPromptTokens = estimatedRequestTokens,
                    PayloadUtf8Bytes = requestPayloadBytes,
                    PayloadContentType = "application/json"
                });
                requestTraceRecorded = true;
                if (settings.StreamResponses && requestOptions.TraceSink != null)
                {
                    streamTrace = new LlmStreamTraceBuffer(
                        requestOptions,
                        requestDiagnostics.RequestId,
                        traceEndpoint,
                        settings.Model,
                        requestOptions.ResponseFormat,
                        traceMessageCount,
                        traceEstimatedPromptTokens);
                }
                var trafficId = settings.DebugModelTraffic ? requestDiagnostics.RequestId : null;
                if (settings.DebugModelTraffic)
                {
                    LogModelJson(settings, trafficId, "REQUEST POST " + requestUri, body.ToString(Formatting.Indented));
                }
                var content = LlmHttpTransport.CreateJsonContent(requestPayloadBytes);
                var diagnostics = CreateDiagnostics(requestUri, settings, apiMessages.Count, !string.IsNullOrWhiteSpace(apiKey));
                var timeout = TimeSpan.FromSeconds(Math.Max(
                    30,
                    settings.RequestTimeoutSeconds <= 0
                        ? AppSettings.DefaultRequestTimeoutSeconds
                        : settings.RequestTimeoutSeconds));
                apiBuild = null;
                apiMessages = null;
                body = null;

                using (content)
                using (var timeoutSource = new CancellationTokenSource())
                using (var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token))
                {
                    timeoutSource.CancelAfter(timeout);
                    try
                    {
                        requestCancellation.Token.ThrowIfCancellationRequested();
                        requestDiagnostics.Sending(content.Headers.ContentLength);
                        var response = await LlmHttpTransport.SendAsync(
                            HttpMethod.Post,
                            requestUri,
                            content,
                            settings,
                            apiKey,
                            settings.StreamResponses ? "text/event-stream" : "application/json",
                            requestCancellation.Token).ConfigureAwait(false);

                        using (response)
                        {
                            responseStatusCode = (int)response.StatusCode;
                            requestDiagnostics.Headers((int)response.StatusCode);
                            if (!response.IsSuccessStatusCode)
                            {
                                var errorBody = await LlmHttpTransport.ReadContentAsStringAsync(
                                    response.Content,
                                    LlmHttpTransport.MaxErrorBodyBytes,
                                    requestCancellation.Token,
                                    requestDiagnostics.FirstChunk).ConfigureAwait(false);
                                failurePayload = errorBody;
                                LogModelJson(settings, trafficId, "RESPONSE HTTP " + (int)response.StatusCode, errorBody);
                                var failureKind = LlmHttpTransport.FailureKind(response.StatusCode, errorBody, requestOptions);
                                if ((hasImages || hasAudio) && (int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
                                {
                                    var inputKind = hasImages && hasAudio
                                        ? "изображения и аудио"
                                        : (hasImages ? "изображения" : "аудио");
                                    throw new LlmRequestException(
                                        failureKind,
                                        "Выбранная модель или endpoint не принял " + inputKind + ". Проверьте capabilities модели и формат мультимодального входа. HTTP " +
                                        (int)response.StatusCode + ". Response: " + errorBody,
                                        null,
                                        (int)response.StatusCode);
                                }
                                throw new LlmRequestException(
                                    failureKind,
                                    "LLM request failed: HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase + ". " + diagnostics + ". Response: " + errorBody,
                                    null,
                                    (int)response.StatusCode);
                            }

                            if (settings.StreamResponses)
                            {
                                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                                using (var capture = requestOptions.CaptureStreamBody ? new LlmResponseCaptureStream(stream) : null)
                                {
                                    Action<string> rawResponseLog = null;
                                    if (settings.DebugModelTraffic || streamTrace != null)
                                    {
                                        rawResponseLog = rawJson =>
                                        {
                                            if (settings.DebugModelTraffic)
                                            {
                                                LogModelJson(
                                                    settings,
                                                    trafficId,
                                                    "RESPONSE HTTP " + (int)response.StatusCode + " SSE CHUNK",
                                                    rawJson);
                                            }
                                            if (streamTrace != null) streamTrace.Add(rawJson);
                                        };
                                    }
                                    LlmCompletionResult streamed;
                                    try
                                    {
                                        streamed = await LlmResponseParser.ReadStreamingOrJsonResponseAsync(
                                            capture ?? stream, streamProgress, requestCancellation.Token,
                                            rawResponseLog, requestDiagnostics.FirstChunk, requestOptions.InspectNativeToolCalls).ConfigureAwait(false);
                                    }
                                    finally
                                    {
                                        if (capture != null)
                                        {
                                            var captured = capture.Snapshot();
                                            Trace(requestOptions, new LlmTraceRecord {
                                                Type = "stream-body", RequestId = requestDiagnostics.RequestId,
                                                Purpose = requestOptions.TracePurpose, Endpoint = traceEndpoint, Model = settings.Model,
                                                Streaming = true, StatusCode = (int)response.StatusCode,
                                                PayloadUtf8Bytes = captured.Bytes,
                                                PayloadContentType = response.Content.Headers.ContentType?.ToString(),
                                                Completed = !captured.Truncated && (captured.EndOfStream ||
                                                    response.Content.Headers.ContentLength == captured.BytesRead),
                                                CaptureTruncated = captured.Truncated, ChunkEncoding = "raw-http-body"
                                            });
                                        }
                                    }
                                    if (streamTrace != null) streamTrace.Flush(true);
                                    AttachPromptEstimate(
                                        streamed,
                                        baseEstimatedRequestTokens,
                                        estimatedRequestTokens,
                                        !hasImages && !hasAudio);
                                    TraceCompletion(requestOptions, requestDiagnostics.RequestId, traceEndpoint,
                                        settings, traceMessageCount, traceEstimatedPromptTokens, (int)response.StatusCode,
                                        streamed, JsonConvert.SerializeObject(streamed, Formatting.None));
                                    terminalTraceRecorded = true;
                                    requestDiagnostics.Completed();
                                    return streamed;
                                }
                            }

                            var responseJson = await LlmHttpTransport.ReadContentAsStringAsync(
                                response.Content,
                                LlmHttpTransport.MaxResponseBodyBytes,
                                requestCancellation.Token,
                                requestDiagnostics.FirstChunk).ConfigureAwait(false);
                            LogModelJson(settings, trafficId, "RESPONSE HTTP " + (int)response.StatusCode, responseJson);
                            // Preserve the actual provider payload even if its envelope cannot be parsed.
                            failurePayload = responseJson;
                            var parsed = LlmResponseParser.ParseCompletionResponse(responseJson, requestOptions.InspectNativeToolCalls);
                            AttachPromptEstimate(
                                parsed,
                                baseEstimatedRequestTokens,
                                estimatedRequestTokens,
                                !hasImages && !hasAudio);
                            TraceCompletion(requestOptions, requestDiagnostics.RequestId, traceEndpoint,
                                settings, traceMessageCount, traceEstimatedPromptTokens, (int)response.StatusCode,
                                parsed, responseJson);
                            terminalTraceRecorded = true;
                            requestDiagnostics.Completed();
                            return parsed;
                        }
                    }
                    catch (OperationCanceledException ex)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            throw new OperationCanceledException("LLM request cancelled.", ex, cancellationToken);
                        }
                        if (timeoutSource.IsCancellationRequested)
                        {
                            throw new LlmRequestException(
                                LlmFailureKind.Timeout,
                                "LLM request timed out after " + timeout.TotalSeconds + " seconds. " + diagnostics + ". " + DeepestMessage(ex),
                                ex);
                        }
                        throw;
                    }
                    catch (LlmRequestException)
                    {
                        throw;
                    }
                    catch (HttpRequestException ex)
                    {
                        throw new LlmRequestException(LlmFailureKind.Network, "LLM request could not be sent. " + diagnostics + ". " + DeepestMessage(ex), ex);
                    }
                    catch (WebException ex)
                    {
                        throw new LlmRequestException(LlmFailureKind.Network, "LLM network error. " + diagnostics + ". " + DeepestMessage(ex), ex);
                    }
                    catch (System.IO.IOException ex)
                    {
                        throw new LlmRequestException(LlmFailureKind.Network, "LLM response stream failed. " + diagnostics + ". " + DeepestMessage(ex), ex);
                    }
                    catch (InvalidOperationException ex)
                    {
                        throw new LlmRequestException(LlmFailureKind.InvalidResponse, ex.Message, ex);
                    }
                }
            }
            catch (Exception ex)
            {
                if (streamTrace != null) streamTrace.Flush(false);
                requestDiagnostics.Failed(ex);
                if (requestTraceRecorded && !terminalTraceRecorded)
                {
                    var requestException = ex as LlmRequestException;
                    Trace(requestOptions, new LlmTraceRecord
                    {
                        Type = "failure",
                        RequestId = requestDiagnostics.RequestId,
                        Purpose = requestOptions.TracePurpose,
                        Endpoint = traceEndpoint,
                        Model = settings.Model,
                        ResponseFormat = requestOptions.ResponseFormat,
                        MessageCount = traceMessageCount,
                        EstimatedPromptTokens = traceEstimatedPromptTokens,
                        StatusCode = requestException == null ? responseStatusCode : requestException.StatusCode ?? responseStatusCode,
                        FailureKind = requestException == null ? ex.GetType().Name : requestException.Kind.ToString(),
                        Error = ex.Message,
                        PayloadJson = failurePayload,
                        PayloadContentType = "application/json"
                    });
                }
                throw;
            }
        }

        public async Task<string> GetModelsConfigJsonAsync(
            AppSettings settings,
            string apiKeyOverride,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }

            var url = BuildModelsConfigUrl(settings);
            Uri requestUri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out requestUri))
            {
                throw new InvalidOperationException("Invalid models config URL: " + url);
            }

            var timeout = TimeSpan.FromSeconds(30);
            var apiKey = apiKeyOverride ?? (_apiKeyProvider == null ? null : _apiKeyProvider());
            using (var timeoutSource = new CancellationTokenSource())
            using (var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token))
            {
                timeoutSource.CancelAfter(timeout);
                try
                {
                    using (var response = await LlmHttpTransport.SendAsync(
                        HttpMethod.Get,
                        requestUri,
                        null,
                        settings,
                        apiKey,
                        "application/json",
                        requestCancellation.Token).ConfigureAwait(false))
                    {
                        var responseJson = await LlmHttpTransport.ReadContentAsStringAsync(
                            response.Content,
                            LlmHttpTransport.MaxModelsConfigBytes,
                            requestCancellation.Token).ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                        {
                            throw new LlmRequestException(
                                LlmHttpTransport.FailureKind(response.StatusCode, responseJson, null),
                                "Models config request failed: HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase + ". Endpoint: " + requestUri + ". Response: " + responseJson,
                                null,
                                (int)response.StatusCode);
                        }

                        return responseJson;
                    }
                }
                catch (OperationCanceledException ex)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException("Models config request cancelled.", ex, cancellationToken);
                    }
                    throw new LlmRequestException(
                        LlmFailureKind.Timeout,
                        "Models config request timed out after " + timeout.TotalSeconds + " seconds. Endpoint: " + requestUri + ". " + DeepestMessage(ex),
                        ex);
                }
                catch (LlmRequestException)
                {
                    throw;
                }
                catch (HttpRequestException ex)
                {
                    throw new LlmRequestException(LlmFailureKind.Network, "Models config request could not be sent. Endpoint: " + requestUri + ". " + DeepestMessage(ex), ex);
                }
                catch (WebException ex)
                {
                    throw new LlmRequestException(LlmFailureKind.Network, "Models config network error. Endpoint: " + requestUri + ". " + DeepestMessage(ex), ex);
                }
                catch (System.IO.IOException ex)
                {
                    throw new LlmRequestException(LlmFailureKind.Network, "Models config response failed. Endpoint: " + requestUri + ". " + DeepestMessage(ex), ex);
                }
            }
        }

        public static string BuildModelsConfigUrl(string baseUrl)
        {
            var url = (baseUrl ?? string.Empty).Trim();
            if (url.Length == 0) return string.Empty;
            var completionsIndex = url.IndexOf("/chat/completions", StringComparison.OrdinalIgnoreCase);
            if (completionsIndex >= 0)
            {
                url = url.Substring(0, completionsIndex);
            }

            url = url.TrimEnd('/');
            if (url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                url = url.Substring(0, url.Length - 3).TrimEnd('/');
            }

            return url + "/v1/models";
        }

        public static string BuildModelsConfigUrl(AppSettings settings)
        {
            if (settings != null && !string.IsNullOrWhiteSpace(settings.ModelsConfigUrl))
            {
                var configured = settings.ModelsConfigUrl.Trim();
                Uri absolute;
                if (Uri.TryCreate(configured, UriKind.Absolute, out absolute) &&
                    (string.Equals(absolute.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(absolute.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
                {
                    return configured;
                }
                var baseModelsUrl = BuildModelsConfigUrl(settings.BaseUrl);
                if (string.IsNullOrWhiteSpace(baseModelsUrl)) return string.Empty;
                var versionIndex = baseModelsUrl.LastIndexOf("/v1/models", StringComparison.OrdinalIgnoreCase);
                var root = versionIndex < 0 ? baseModelsUrl.TrimEnd('/') : baseModelsUrl.Substring(0, versionIndex);
                return root.TrimEnd('/') + "/" + configured.TrimStart('/');
            }
            return BuildModelsConfigUrl(settings == null ? null : settings.BaseUrl);
        }

        private static string CombineUrl(string baseUrl, string path)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return string.Empty;
            }

            if (baseUrl.IndexOf("/chat/completions", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return baseUrl;
            }

            var cleanBaseUrl = baseUrl.TrimEnd('/');
            if (cleanBaseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                cleanBaseUrl = cleanBaseUrl.Substring(0, cleanBaseUrl.Length - 3).TrimEnd('/');
            }

            return cleanBaseUrl + path;
        }

        private static string DeepestMessage(Exception exception)
        {
            var current = exception;
            while (current.InnerException != null)
            {
                current = current.InnerException;
            }

            return current.Message;
        }

        private void LogModelJson(AppSettings settings, string trafficId, string label, string rawJson)
        {
            if (settings == null || !settings.DebugModelTraffic || _debugLog == null)
            {
                return;
            }

            var formatted = rawJson ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(formatted))
            {
                try
                {
                    formatted = JToken.Parse(formatted.TrimStart('\uFEFF')).ToString(Formatting.Indented);
                }
                catch (JsonException)
                {
                }
            }
            try
            {
                _debugLog("MODEL TRAFFIC [" + (trafficId ?? "unknown") + "] " + label + Environment.NewLine + formatted);
            }
            catch
            {
            }
        }

        private static string CreateDiagnostics(Uri requestUri, AppSettings settings, int messageCount, bool hasBearerKey)
        {
            var headerNames = new List<string>();
            if (hasBearerKey)
            {
                headerNames.Add("Authorization: Bearer ***");
            }

            if (settings.CustomHeaders != null)
            {
                foreach (var header in settings.CustomHeaders)
                {
                    if (!string.IsNullOrWhiteSpace(header.Key))
                    {
                        headerNames.Add(header.Key + ": ***");
                    }
                }
            }

            return "Endpoint: " + requestUri +
                ". Model: " + settings.Model +
                ". Messages: " + messageCount +
                ". MaxTokens: " + settings.MaxTokens +
                ". Temperature: " + settings.Temperature +
                ". TopP: " + settings.TopP +
                ". Headers: [" + string.Join(", ", headerNames.ToArray()) + "]";
        }

        internal static JObject BuildRequestBody(
            AppSettings settings,
            IList<object> apiMessages,
            int estimatedPromptTokens,
            LlmRequestOptions requestOptions,
            bool estimateIncludesRequestOptions = false)
        {
            settings = settings ?? new AppSettings();
            requestOptions = requestOptions ?? new LlmRequestOptions();
            var estimatedRequestTokens = estimateIncludesRequestOptions
                ? estimatedPromptTokens
                : estimatedPromptTokens + ModelContextBudget.EstimateRequestOptionsTokens(requestOptions, settings);
            var body = new JObject
            {
                ["model"] = settings.Model,
                ["messages"] = JArray.FromObject(apiMessages ?? new object[0]),
                ["max_tokens"] = ModelContextBudget.EffectiveOutputTokens(settings, estimatedRequestTokens, settings.Model),
                ["temperature"] = settings.Temperature,
                ["top_p"] = settings.TopP,
                ["stream"] = settings.StreamResponses
            };
            if (settings.StreamResponses) body["stream_options"] = new JObject { ["include_usage"] = true };

            AppendReasoningRequest(body, settings, requestOptions);

            if (requestOptions.NativeTools?.Count > 0)
                body["tools"] = new JArray(requestOptions.NativeTools.Select(tool => tool.ToWire()));
            if (requestOptions.RequireNativeToolCall)
            {
                if (requestOptions.NativeTools == null || requestOptions.NativeTools.Count == 0)
                    throw new ArgumentException("A required native call needs function definitions.");
                body["tool_choice"] = "required";
            }

            if (string.Equals(requestOptions.ResponseFormat, LlmResponseFormats.JsonObject, StringComparison.OrdinalIgnoreCase))
            {
                body["response_format"] = new JObject { ["type"] = "json_object" };
            }
            else if (string.Equals(requestOptions.ResponseFormat, LlmResponseFormats.JsonSchema, StringComparison.OrdinalIgnoreCase))
            {
                JObject schema;
                try
                {
                    schema = JObject.Parse(requestOptions.ResponseSchemaJson ?? string.Empty);
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException("Response JSON Schema is invalid: " + ex.Message, ex);
                }
                body["response_format"] = new JObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JObject
                    {
                        ["name"] = string.IsNullOrWhiteSpace(requestOptions.ResponseSchemaName)
                            ? "response"
                            : requestOptions.ResponseSchemaName,
                        ["strict"] = true,
                        ["schema"] = schema
                    }
                };
            }
            return body;
        }

        private static void AttachPromptEstimate(
            LlmCompletionResult result,
            int baseEstimatedPromptTokens,
            int estimatedPromptTokens,
            bool calibrationEligible)
        {
            if (result == null) return;
            result.BaseEstimatedPromptTokens = Math.Max(0, baseEstimatedPromptTokens);
            result.EstimatedPromptTokens = Math.Max(0, estimatedPromptTokens);
            result.TokenEstimateCalibrationEligible = calibrationEligible;
        }

        private static void TraceCompletion(
            LlmRequestOptions requestOptions,
            string requestId,
            string endpoint,
            AppSettings settings,
            int messageCount,
            int? estimatedPromptTokens,
            int statusCode,
            LlmCompletionResult result,
            string payloadJson)
        {
            Trace(requestOptions, new LlmTraceRecord
            {
                Type = "response",
                RequestId = requestId,
                Purpose = requestOptions == null ? null : requestOptions.TracePurpose,
                Endpoint = endpoint,
                Model = settings == null ? null : settings.Model,
                ResponseFormat = requestOptions == null ? null : requestOptions.ResponseFormat,
                MessageCount = messageCount,
                EstimatedPromptTokens = estimatedPromptTokens,
                PromptTokens = result == null ? null : result.PromptTokens,
                CompletionTokens = result == null ? null : result.CompletionTokens,
                TotalTokens = result == null ? null : result.TotalTokens,
                ReasoningTokens = result == null ? null : result.ReasoningTokens,
                UsageJson = result == null ? null : result.UsageJson,
                StatusCode = statusCode,
                PayloadJson = payloadJson ?? JsonConvert.SerializeObject(result, Formatting.None),
                PayloadContentType = "application/json"
            });
        }

        internal static List<ContextMessagePresentation> DescribeWireMessages(IList<ChatMessage> messages,
            ContextReceipt receipt, bool[] changedContent = null,
            IList<List<ContextMessagePresentation>> wireParts = null,
            JArray serializedMessages = null, AppSettings settings = null)
        {
            var entries = new List<ContextMessagePresentation>();
            var visible = messages.Where(m => m != null && !m.ExcludeFromModelContext &&
                !string.IsNullOrWhiteSpace(m.Role)).ToList();
            var visibleCount = visible.Count;
            var sources = LlmContextMessageAttribution.Match(visible, receipt);
            var alignedParts = wireParts != null && wireParts.Count == visibleCount;
            var alignedWire = serializedMessages != null && serializedMessages.Count == visibleCount;
            foreach (var message in visible)
            {
                var source = sources[entries.Count];
                var entry = source == null ? new ContextMessagePresentation { SourceMessageId = message.Id,
                    Role = message.Role, Presentation = ContextPresentationKind.Unknown, Reason = "Нет однозначных сведений сборщика для этого сообщения." }
                    : JsonConvert.DeserializeObject<ContextMessagePresentation>(JsonConvert.SerializeObject(source));
                entry.MessageIndex = entries.Count;
                entry.EstimatedWireMessageTokens = null;
                if (entry.Parts == null) entry.Parts = new List<ContextMessagePresentation>();
                var wire = alignedWire ? serializedMessages[entries.Count] as JObject : null;
                if (changedContent != null && entries.Count < changedContent.Length && changedContent[entries.Count])
                {
                    entry.Presentation = ContextPresentationKind.Unknown;
                    entry.Reason = "Текст сообщения изменён при сборке запроса; точный переданный вариант находится в JSON запроса.";
                    entry.Parts.Clear();
                    foreach (var link in entry.EvidenceLinks ?? new List<ContextEvidenceLink>())
                        link.BodyIncludedInMessage = null;
                }
                if (source != null && wire != null && !WireContainsSourceText(wire, message))
                {
                    entry.Presentation = ContextPresentationKind.Unknown;
                    entry.Reason = "Сохранённый текст запроса не подтверждает доставку исходного сообщения.";
                    entry.Parts.Clear();
                    foreach (var link in entry.EvidenceLinks ?? new List<ContextEvidenceLink>())
                        link.BodyIncludedInMessage = null;
                }
                if ((message.Attachments?.Count ?? 0) > 0 || message.AttachmentAnalysis != null)
                {
                    entry.Parts.Add(new ContextMessagePresentation { Kind = "message", Presentation = entry.Presentation, Reason = entry.Reason });
                    if (alignedParts)
                        foreach (var part in wireParts[entries.Count])
                        {
                            if (part == null) continue;
                            var captured = JsonConvert.DeserializeObject<ContextMessagePresentation>(
                                JsonConvert.SerializeObject(part));
                            if (captured.Presentation != ContextPresentationKind.Excluded &&
                                !WireContainsAttachmentPart(wire, message, captured))
                            {
                                captured.Presentation = ContextPresentationKind.Unknown;
                                captured.Reason = "Сохранённый запрос не подтверждает эту часть вложения.";
                                captured.PartIndex = null;
                                captured.PageNumber = null;
                                captured.SelectedCharacters = null;
                                captured.ContentSha256 = null;
                                captured.ByteLength = null;
                            }
                            entry.Parts.Add(captured);
                        }
                    else
                        entry.Parts.Add(new ContextMessagePresentation { Kind = "attachments", Presentation = ContextPresentationKind.Unknown,
                            Reason = "Текст и медиа выбраны transport; точные переданные части доступны в JSON запроса." });
                    entry.Presentation = ContextPresentationKind.Unknown;
                }
                if (alignedWire && source != null &&
                    changedContent != null && (changedContent.Length == 0 || changedContent.Length == visibleCount) &&
                    (entry.Presentation == ContextPresentationKind.Full ||
                     entry.Presentation == ContextPresentationKind.Fragment) &&
                    entry.EvidenceLinks?.Count == 1 &&
                    entry.EvidenceLinks[0]?.Resource?.IsExact == true &&
                    entry.EvidenceLinks[0].ScopeId != null &&
                    !string.IsNullOrWhiteSpace(entry.EvidenceLinks[0].EvidenceId) &&
                    !string.IsNullOrWhiteSpace(entry.EvidenceLinks[0].View) &&
                    entry.EvidenceLinks[0].ObservedCoverage != null &&
                    string.Equals(message.ToolName, "core.read", StringComparison.Ordinal) &&
                    string.Equals(message.Role, "tool", StringComparison.Ordinal) &&
                    string.Equals(entry.SourceMessageId, message.Id, StringComparison.Ordinal) &&
                    string.Equals(entry.ToolCallId, message.ToolCallId, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(message.ToolCallId) &&
                    (message.Attachments?.Count ?? 0) == 0 && message.AttachmentAnalysis == null &&
                    (changedContent.Length == 0 || !changedContent[entries.Count]))
                {
                    if (wire != null && wire["content"]?.Type == JTokenType.String &&
                        string.Equals((string)wire["role"], "tool", StringComparison.Ordinal) &&
                        string.Equals((string)wire["tool_call_id"], message.ToolCallId, StringComparison.Ordinal) &&
                        string.Equals((string)wire["content"], message.Content ?? string.Empty, StringComparison.Ordinal))
                        entry.EstimatedWireMessageTokens = ModelContextBudget.EstimateMessageTokens(
                            new ChatMessage { Role = "tool", ToolCallId = message.ToolCallId,
                                Content = (string)wire["content"] }, settings, includeExtractedAttachments: false);
                }
                entries.Add(entry);
            }
            foreach (var omitted in (receipt?.Messages ?? new List<ContextMessagePresentation>())
                .Where(m => m != null && !m.MessageIndex.HasValue))
            {
                var entry = JsonConvert.DeserializeObject<ContextMessagePresentation>(JsonConvert.SerializeObject(omitted));
                entry.EstimatedWireMessageTokens = null;
                entries.Add(entry);
            }
            return entries;
        }

        private static bool WireContainsSourceText(JObject wire, ChatMessage message)
        {
            string text;
            if (wire == null || message == null ||
                !string.Equals((string)wire["role"], message.Role, StringComparison.Ordinal) ||
                !TryWireText(wire, out text)) return false;
            if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals((string)wire["tool_call_id"], message.ToolCallId, StringComparison.Ordinal))
                return false;
            var calls = (message.ToolCalls ?? new List<LlmToolCall>()).Where(call => call != null).ToArray();
            var wireCalls = wire["tool_calls"] as JArray;
            if (calls.Length != (wireCalls?.Count ?? 0)) return false;
            for (var index = 0; index < calls.Length; index++)
                if (!string.Equals((string)wireCalls[index]["id"], calls[index].Id, StringComparison.Ordinal) ||
                    !string.Equals((string)wireCalls[index]["type"],
                        string.IsNullOrWhiteSpace(calls[index].Type) ? "function" : calls[index].Type,
                        StringComparison.Ordinal) ||
                    !string.Equals((string)wireCalls[index]["function"]?["name"], calls[index].Name,
                        StringComparison.Ordinal) ||
                    !string.Equals((string)wireCalls[index]["function"]?["arguments"],
                        string.IsNullOrWhiteSpace(calls[index].ArgumentsJson) ? "{}" : calls[index].ArgumentsJson,
                        StringComparison.Ordinal)) return false;
            var source = message.Content ?? string.Empty;
            return (message.Attachments?.Count ?? 0) > 0 || message.AttachmentAnalysis != null
                ? source.Length > 0 && text.StartsWith(source, StringComparison.Ordinal)
                : string.Equals(text, source, StringComparison.Ordinal);
        }

        private static bool TryWireText(JObject wire, out string text)
        {
            text = null;
            var content = wire?["content"];
            if (content?.Type == JTokenType.String)
                text = (string)content;
            else if (content is JArray parts && parts.Count > 0 &&
                string.Equals((string)parts[0]?["type"], "text", StringComparison.Ordinal) &&
                parts[0]?["text"]?.Type == JTokenType.String)
                text = (string)parts[0]["text"];
            return text != null;
        }

        private static bool WireContainsAttachmentPart(JObject wire, ChatMessage message,
            ContextMessagePresentation part)
        {
            if (wire == null || message == null || part == null ||
                !string.Equals((string)wire["role"], message.Role, StringComparison.Ordinal) ||
                string.IsNullOrEmpty(part.SourceAttachmentId)) return false;
            var matches = (message.Attachments ?? new List<ChatAttachment>())
                .Where(item => item != null && item.Id == part.SourceAttachmentId).Take(2).ToArray();
            if (matches.Length != 1) return false;
            if (part.Kind == "attachment_analysis")
            {
                string text;
                var analysis = message.AttachmentAnalysis;
                return analysis?.AttachmentIds?.Contains(part.SourceAttachmentId) == true &&
                    !string.IsNullOrEmpty(analysis.Content) && TryWireText(wire, out text) &&
                    text.IndexOf(analysis.Content, StringComparison.Ordinal) >= 0 &&
                    text.IndexOf(analysis.Content, text.IndexOf(analysis.Content, StringComparison.Ordinal) + 1,
                        StringComparison.Ordinal) < 0;
            }
            if (part.Kind == "attachment_text")
            {
                string text;
                if (!TryWireText(wire, out text) || !part.SelectedCharacters.HasValue ||
                    !part.ByteLength.HasValue || string.IsNullOrEmpty(part.ContentSha256)) return false;
                var array = wire["content"] as JArray;
                if (array == null ? part.PartIndex.HasValue : part.PartIndex != 0) return false;
                var marker = "[Attachment: " + matches[0].FileName + "]" + Environment.NewLine;
                var start = text.IndexOf(marker, StringComparison.Ordinal);
                if (start < 0 || text.IndexOf(marker, start + marker.Length, StringComparison.Ordinal) >= 0)
                    return false;
                start += marker.Length;
                if (part.SelectedCharacters.Value < 0 || part.SelectedCharacters.Value > text.Length - start)
                    return false;
                var selected = text.Substring(start, part.SelectedCharacters.Value);
                var suffix = part.Presentation == ContextPresentationKind.Fragment
                    ? Environment.NewLine + "[Content truncated]" + Environment.NewLine + "[End attachment]"
                    : Environment.NewLine + "[End attachment]";
                if (text.IndexOf(suffix, start + selected.Length, StringComparison.Ordinal) != start + selected.Length)
                    return false;
                var bytes = Encoding.UTF8.GetBytes(selected);
                using (var sha = SHA256.Create())
                    return bytes.Length == part.ByteLength.Value &&
                        string.Equals(HashHex(sha.ComputeHash(bytes)), part.ContentSha256, StringComparison.Ordinal);
            }
            var media = wire["content"] as JArray;
            if (media == null || !part.PartIndex.HasValue || part.PartIndex < 1 ||
                part.PartIndex >= media.Count) return false;
            var selectedPart = media[part.PartIndex.Value] as JObject;
            if (part.Kind == "image_url")
            {
                if (!string.Equals((string)selectedPart?["type"], "image_url", StringComparison.Ordinal))
                    return false;
                var url = (string)selectedPart?["image_url"]?["url"];
                var prefix = "data:" + part.ContentType + ";base64,";
                return url != null && url.StartsWith(prefix, StringComparison.Ordinal) &&
                    MatchesBase64(url, prefix.Length, part);
            }
            return part.Kind == "input_audio" &&
                string.Equals((string)selectedPart?["type"], "input_audio", StringComparison.Ordinal) &&
                MatchesBase64((string)selectedPart?["input_audio"]?["data"], 0, part);
        }

        private static bool MatchesBase64(string encoded, int start, ContextMessagePresentation part)
        {
            if (encoded == null || start < 0 || start > encoded.Length || !part.ByteLength.HasValue ||
                string.IsNullOrEmpty(part.ContentSha256)) return false;
            var count = encoded.Length - start;
            if (count == 0 || count % 4 != 0) return false;
            var padding = encoded[encoded.Length - 1] == '=' ? 1 : 0;
            if (padding == 1 && encoded[encoded.Length - 2] == '=') padding++;
            if ((long)count / 4 * 3 - padding != part.ByteLength.Value) return false;
            var chars = new char[4096];
            using (var sha = SHA256.Create())
            {
                for (var offset = 0; offset < count;)
                {
                    var length = Math.Min(chars.Length, count - offset);
                    encoded.CopyTo(start + offset, chars, 0, length);
                    byte[] bytes;
                    try { bytes = Convert.FromBase64CharArray(chars, 0, length); }
                    catch (FormatException) { return false; }
                    sha.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
                    offset += length;
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return string.Equals(HashHex(sha.Hash), part.ContentSha256, StringComparison.Ordinal);
            }
        }

        private static string HashHex(byte[] bytes)
        { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }

        private static void Trace(LlmRequestOptions requestOptions, LlmTraceRecord record)
        {
            if (requestOptions != null && requestOptions.TraceSink != null)
            {
                requestOptions.TraceSink(record);
            }
        }

        private static void AppendReasoningRequest(JObject body, AppSettings settings, LlmRequestOptions requestOptions)
        {
            if (!requestOptions.ReasoningEnabled.HasValue)
            {
                return;
            }

            var reasoningSupport = ModelContextBudget.ReasoningSupport(settings, settings.Model);
            if (reasoningSupport == false)
            {
                return;
            }

            var capability = ModelContextBudget.Capability(settings, settings.Model);
            var configuredMode = capability == null ? null : capability.ReasoningRequestMode;
            var mode = ReasoningRequestModes.Normalize(string.IsNullOrWhiteSpace(configuredMode)
                ? settings.ReasoningRequestMode
                : configuredMode);
            var enabled = requestOptions.ReasoningEnabled.Value;

            if (string.Equals(mode, ReasoningRequestModes.CustomJson, StringComparison.Ordinal))
            {
                AppendCustomReasoningRequest(body, settings.ReasoningCustomJson, enabled);
                return;
            }

            if (string.Equals(mode, ReasoningRequestModes.EnableThinking, StringComparison.Ordinal))
            {
                body["enable_thinking"] = enabled;
                return;
            }
            if (string.Equals(mode, ReasoningRequestModes.ChatTemplateKwargs, StringComparison.Ordinal))
            {
                body["chat_template_kwargs"] = new JObject { ["enable_thinking"] = enabled };
                return;
            }
            if (string.Equals(mode, ReasoningRequestModes.ReasoningEnabled, StringComparison.Ordinal))
            {
                body["reasoning"] = new JObject { ["enabled"] = enabled };
                return;
            }

            if (enabled || reasoningSupport == true ||
                string.Equals(mode, ReasoningRequestModes.ReasoningEffort, StringComparison.Ordinal))
            {
                body["reasoning_effort"] = enabled ? "medium" : "none";
            }
        }

        private static void AppendCustomReasoningRequest(JObject body, string customJson, bool enabled)
        {
            if (!enabled || string.IsNullOrWhiteSpace(customJson))
            {
                return;
            }
            if (customJson.Length > MaxReasoningCustomJsonChars)
            {
                throw new InvalidOperationException("Custom reasoning JSON is too large. Maximum size is " + MaxReasoningCustomJsonChars + " characters.");
            }

            JObject customBody;
            try
            {
                customBody = JObject.Parse(customJson);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("Custom reasoning JSON must be a valid JSON object: " + ex.Message, ex);
            }

            foreach (var property in customBody.Properties())
            {
                if (IsReservedRequestField(property.Name))
                {
                    throw new InvalidOperationException("Custom reasoning JSON cannot override the reserved request field '" + property.Name + "'.");
                }
                body[property.Name] = property.Value.DeepClone();
            }
        }

        private static bool IsReservedRequestField(string name)
        {
            switch ((name ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "model":
                case "messages":
                case "max_tokens":
                case "temperature":
                case "top_p":
                case "stream":
                case "stream_options":
                case "response_format":
                case "tools":
                case "tool_choice":
                case "parallel_tool_calls":
                    return true;
                default:
                    return false;
            }
        }

    }

    internal sealed class LlmStreamTraceBuffer
    {
        private const int DefaultMaxBufferedChars = 64 * 1024;
        private static readonly TimeSpan DefaultMaxBufferAge = TimeSpan.FromSeconds(1);
        private readonly LlmRequestOptions _options;
        private readonly string _requestId;
        private readonly string _endpoint;
        private readonly string _model;
        private readonly string _responseFormat;
        private readonly int _messageCount;
        private readonly int? _estimatedPromptTokens;
        private readonly int _maxBufferedChars;
        private readonly List<string> _frames = new List<string>();
        private DateTime _bufferStartedUtc;
        private int _bufferedChars;
        private int _nextChunkIndex;
        private bool _completed;

        internal LlmStreamTraceBuffer(
            LlmRequestOptions options,
            string requestId,
            string endpoint,
            string model,
            string responseFormat,
            int messageCount,
            int? estimatedPromptTokens,
            int maxBufferedChars = DefaultMaxBufferedChars)
        {
            _options = options;
            _requestId = requestId;
            _endpoint = endpoint;
            _model = model;
            _responseFormat = responseFormat;
            _messageCount = messageCount;
            _estimatedPromptTokens = estimatedPromptTokens;
            _maxBufferedChars = Math.Max(1, maxBufferedChars);
        }

        internal void Add(string rawJson)
        {
            if (_completed) throw new InvalidOperationException("Cannot append a model chunk after stream completion.");
            if (rawJson == null) return;
            if (_frames.Count == 0) _bufferStartedUtc = DateTime.UtcNow;
            _frames.Add(rawJson);
            _bufferedChars += rawJson.Length;
            if (_bufferedChars >= _maxBufferedChars || DateTime.UtcNow - _bufferStartedUtc >= DefaultMaxBufferAge)
            {
                Flush(false);
            }
        }

        internal void Flush(bool completed)
        {
            if (_completed) return;
            if (_frames.Count == 0)
            {
                if (completed) _completed = true;
                return;
            }

            var count = _frames.Count;
            var payload = JsonConvert.SerializeObject(_frames, Formatting.None);
            var sink = _options == null ? null : _options.TraceSink;
            if (sink != null)
            {
                sink(new LlmTraceRecord
                {
                    Type = "chunk",
                    RequestId = _requestId,
                    Purpose = _options.TracePurpose,
                    Endpoint = _endpoint,
                    Model = _model,
                    ResponseFormat = _responseFormat,
                    MessageCount = _messageCount,
                    EstimatedPromptTokens = _estimatedPromptTokens,
                    ChunkIndex = _nextChunkIndex,
                    ChunkCount = count,
                    Completed = completed,
                    ChunkEncoding = "openai-sse-data-json-array",
                    PayloadJson = payload,
                    PayloadContentType = "application/json"
                });
            }

            _nextChunkIndex += count;
            _frames.Clear();
            _bufferedChars = 0;
            _bufferStartedUtc = default(DateTime);
            if (completed) _completed = true;
        }
    }
}
