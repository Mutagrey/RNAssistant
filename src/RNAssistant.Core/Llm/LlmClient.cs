using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
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
        {
            _apiKeyProvider = apiKeyProvider;
            _messageBuilder = new LlmMessageBuilder(attachmentReader, attachmentTextReader, modelImageProvider);
            _debugLog = debugLog;
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

            var apiKey = _apiKeyProvider == null ? null : _apiKeyProvider();
            var url = CombineUrl(settings.BaseUrl, "/v1/chat/completions");
            Uri requestUri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out requestUri))
            {
                throw new InvalidOperationException("Invalid LLM endpoint URL: " + url);
            }

            requestOptions = requestOptions ?? new LlmRequestOptions();
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

            var body = BuildRequestBody(settings, apiMessages, apiBuild.EstimatedPromptTokens, requestOptions);
            var trafficId = settings.DebugModelTraffic ? Guid.NewGuid().ToString("N").Substring(0, 12) : null;
            if (settings.DebugModelTraffic)
            {
                LogModelJson(settings, trafficId, "REQUEST POST " + requestUri, body.ToString(Formatting.Indented));
            }
            var content = LlmHttpTransport.CreateJsonContent(body);
            var diagnostics = CreateDiagnostics(requestUri, settings, apiMessages.Count, !string.IsNullOrWhiteSpace(apiKey));
            var timeout = TimeSpan.FromSeconds(Math.Max(30, settings.RequestTimeoutSeconds <= 0 ? 300 : settings.RequestTimeoutSeconds));
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
                        if (!response.IsSuccessStatusCode)
                        {
                            var errorBody = await LlmHttpTransport.ReadContentAsStringAsync(
                                response.Content,
                                LlmHttpTransport.MaxErrorBodyBytes,
                                requestCancellation.Token).ConfigureAwait(false);
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
                            {
                                Action<string> rawResponseLog = null;
                                if (settings.DebugModelTraffic)
                                {
                                    rawResponseLog = rawJson => LogModelJson(
                                        settings,
                                        trafficId,
                                        "RESPONSE HTTP " + (int)response.StatusCode + " SSE CHUNK",
                                        rawJson);
                                }
                                return await LlmResponseParser.ReadStreamingOrJsonResponseAsync(
                                    stream,
                                    streamProgress,
                                    requestCancellation.Token,
                                    rawResponseLog).ConfigureAwait(false);
                            }
                        }

                        var responseJson = await LlmHttpTransport.ReadContentAsStringAsync(
                            response.Content,
                            LlmHttpTransport.MaxResponseBodyBytes,
                            requestCancellation.Token).ConfigureAwait(false);
                        LogModelJson(settings, trafficId, "RESPONSE HTTP " + (int)response.StatusCode, responseJson);
                        return LlmResponseParser.ParseCompletionResponse(responseJson);
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
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                baseUrl = "https://api.openai.com";
            }

            var url = baseUrl.Trim();
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

            return url + "/config/models.json";
        }

        public static string BuildModelsConfigUrl(AppSettings settings)
        {
            if (settings != null && !string.IsNullOrWhiteSpace(settings.ModelsConfigUrl))
            {
                return settings.ModelsConfigUrl.Trim();
            }
            return BuildModelsConfigUrl(settings == null ? null : settings.BaseUrl);
        }

        private static string CombineUrl(string baseUrl, string path)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                baseUrl = "https://api.openai.com";
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

        internal static JObject BuildRequestBody(AppSettings settings, IList<object> apiMessages, int estimatedPromptTokens, LlmRequestOptions requestOptions)
        {
            settings = settings ?? new AppSettings();
            requestOptions = requestOptions ?? new LlmRequestOptions();
            var estimatedRequestTokens = estimatedPromptTokens + ModelContextBudget.EstimateRequestOptionsTokens(requestOptions);
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

            if (string.Equals(requestOptions.ResponseFormat, LlmResponseFormats.JsonObject, StringComparison.OrdinalIgnoreCase))
            {
                body["response_format"] = new JObject { ["type"] = "json_object" };
            }
            else if (string.Equals(requestOptions.ResponseFormat, LlmResponseFormats.JsonSchema, StringComparison.OrdinalIgnoreCase))
            {
                JObject schema;
                try { schema = JObject.Parse(requestOptions.ResponseSchemaJson ?? string.Empty); }
                catch (JsonException ex) { throw new InvalidOperationException("Response JSON Schema is invalid: " + ex.Message, ex); }
                body["response_format"] = new JObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JObject
                    {
                        ["name"] = string.IsNullOrWhiteSpace(requestOptions.ResponseSchemaName) ? "response" : requestOptions.ResponseSchemaName,
                        ["strict"] = true,
                        ["schema"] = schema
                    }
                };
            }

            if (requestOptions.NativeTools && requestOptions.Tools != null && requestOptions.Tools.Count > 0)
            {
                body["tools"] = new JArray(requestOptions.Tools.Select(tool => new JObject
                {
                    ["type"] = "function",
                    ["function"] = new JObject
                    {
                        ["name"] = tool.ApiName,
                        ["description"] = tool.Description ?? string.Empty,
                        ["parameters"] = JObject.Parse(tool.ParametersSchemaJson ?? "{}"),
                        ["strict"] = true
                    }
                }));
                body["tool_choice"] = "auto";
                body["parallel_tool_calls"] = true;
            }
            return body;
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

            if (enabled || reasoningSupport == true)
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
}
