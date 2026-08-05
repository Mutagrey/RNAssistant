using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
        private readonly Func<string> _apiKeyProvider;
        private readonly LlmMessageBuilder _messageBuilder;

        public LlmClient(Func<string> apiKeyProvider, Func<ChatAttachment, byte[]> attachmentReader = null)
            : this(apiKeyProvider, attachmentReader, null, null)
        {
        }

        public LlmClient(
            Func<string> apiKeyProvider,
            Func<ChatAttachment, byte[]> attachmentReader,
            Func<ChatAttachment, string> attachmentTextReader,
            Func<AppSettings, ChatAttachment, IReadOnlyList<ModelImagePart>> modelImageProvider)
        {
            _apiKeyProvider = apiKeyProvider;
            _messageBuilder = new LlmMessageBuilder(attachmentReader, attachmentTextReader, modelImageProvider);
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

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(Math.Max(30, settings.RequestTimeoutSeconds <= 0 ? 300 : settings.RequestTimeoutSeconds));
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(
                    settings.StreamResponses ? "text/event-stream" : "application/json"));
                client.DefaultRequestHeaders.UserAgent.ParseAdd("RNAssistant/0.1");
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }

                string contentTypeOverride = null;
                if (settings.CustomHeaders != null)
                {
                    foreach (var header in settings.CustomHeaders)
                    {
                        if (!string.IsNullOrWhiteSpace(header.Key) && !string.IsNullOrWhiteSpace(header.Value))
                        {
                            if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                            {
                                contentTypeOverride = header.Value;
                                continue;
                            }

                            if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
                            {
                                throw new InvalidOperationException("Custom header cannot be set manually: " + header.Key);
                            }

                            client.DefaultRequestHeaders.Remove(header.Key);
                            if (!client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value))
                            {
                                throw new InvalidOperationException("Custom header is not valid for request headers: " + header.Key);
                            }
                        }
                    }
                }

                var messageList = messages == null ? new List<ChatMessage>() : messages.ToList();
                var apiBuild = _messageBuilder.Build(messageList, settings);
                var apiMessages = apiBuild.Messages;
                var hasImages = apiBuild.HasImages;
                var hasAudio = apiBuild.HasAudio;
                if (apiMessages.Count == 0)
                {
                    throw new InvalidOperationException("LLM request has no messages.");
                }

                var body = BuildRequestBody(settings, apiMessages, apiBuild.EstimatedPromptTokens, requestOptions);
                var json = body.ToString(Formatting.None);
                var diagnostics = CreateDiagnostics(requestUri, settings, apiMessages.Count, !string.IsNullOrWhiteSpace(apiKey));
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    HttpResponseMessage response;
                    using (var request = new HttpRequestMessage(HttpMethod.Post, requestUri))
                    {
                        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                        if (!string.IsNullOrWhiteSpace(contentTypeOverride))
                        {
                            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentTypeOverride);
                        }
                        response = await client.SendAsync(
                            request,
                            settings.StreamResponses ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
                            cancellationToken).ConfigureAwait(false);
                    }

                    using (response)
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            var errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if ((hasImages || hasAudio) && (int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
                            {
                                var inputKind = hasImages && hasAudio
                                    ? "изображения и аудио"
                                    : (hasImages ? "изображения" : "аудио");
                                throw new InvalidOperationException(
                                    "Выбранная модель или endpoint не принял " + inputKind + ". Проверьте capabilities модели и формат мультимодального входа. HTTP " +
                                    (int)response.StatusCode + ". Response: " + errorBody);
                            }
                            throw new InvalidOperationException("LLM request failed: HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase + ". " + diagnostics + ". Response: " + errorBody);
                        }

                        if (settings.StreamResponses)
                        {
                            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            {
                                return await LlmResponseParser.ReadStreamingOrJsonResponseAsync(stream, streamProgress, cancellationToken).ConfigureAwait(false);
                            }
                        }

                        var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        return LlmResponseParser.ParseCompletionResponse(responseJson);
                    }
                }
                catch (TaskCanceledException ex)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException("LLM request cancelled.", ex, cancellationToken);
                    }

                    throw new InvalidOperationException("LLM request timed out after " + client.Timeout.TotalSeconds + " seconds. " + diagnostics + ". " + DeepestMessage(ex), ex);
                }
                catch (HttpRequestException ex)
                {
                    throw new InvalidOperationException("LLM request could not be sent. " + diagnostics + ". " + DeepestMessage(ex), ex);
                }
                catch (WebException ex)
                {
                    throw new InvalidOperationException("LLM network error. " + diagnostics + ". " + DeepestMessage(ex), ex);
                }
            }
        }

        public async Task<string> GetModelsConfigJsonAsync(AppSettings settings, string apiKeyOverride)
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

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                client.DefaultRequestHeaders.UserAgent.ParseAdd("RNAssistant/0.1");

                var apiKey = apiKeyOverride ?? (_apiKeyProvider == null ? null : _apiKeyProvider());
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }

                if (settings.CustomHeaders != null)
                {
                    foreach (var header in settings.CustomHeaders)
                    {
                        if (!string.IsNullOrWhiteSpace(header.Key) && !string.IsNullOrWhiteSpace(header.Value))
                        {
                            if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
                            {
                                throw new InvalidOperationException("Custom header cannot be set manually: " + header.Key);
                            }

                            client.DefaultRequestHeaders.Remove(header.Key);
                            if (!client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value))
                            {
                                throw new InvalidOperationException("Custom header is not valid for request headers: " + header.Key);
                            }
                        }
                    }
                }

                try
                {
                    using (var response = await client.GetAsync(requestUri).ConfigureAwait(false))
                    {
                        var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                        {
                            throw new InvalidOperationException("Models config request failed: HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase + ". Endpoint: " + requestUri + ". Response: " + responseJson);
                        }

                        return responseJson;
                    }
                }
                catch (TaskCanceledException ex)
                {
                    throw new InvalidOperationException("Models config request timed out after " + client.Timeout.TotalSeconds + " seconds. Endpoint: " + requestUri + ". " + DeepestMessage(ex), ex);
                }
                catch (HttpRequestException ex)
                {
                    throw new InvalidOperationException("Models config request could not be sent. Endpoint: " + requestUri + ". " + DeepestMessage(ex), ex);
                }
                catch (WebException ex)
                {
                    throw new InvalidOperationException("Models config network error. Endpoint: " + requestUri + ". " + DeepestMessage(ex), ex);
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
                body["parallel_tool_calls"] = false;
            }
            return body;
        }

    }
}
