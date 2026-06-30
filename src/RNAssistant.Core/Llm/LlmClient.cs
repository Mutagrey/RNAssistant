using System;
using System.Collections.Generic;
using System.IO;
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
    public sealed class LlmCompletionResult
    {
        public string Content { get; set; }
        public string ReasoningContent { get; set; }
        public int? ReasoningTokens { get; set; }
        public bool ReasoningTruncated { get; set; }
        public int? PromptTokens { get; set; }
        public int? CompletionTokens { get; set; }
        public int? TotalTokens { get; set; }
        public string UsageJson { get; set; }
    }

    public sealed class LlmStreamUpdate
    {
        public string ContentDelta { get; set; }
        public string ReasoningDelta { get; set; }
        public bool Completed { get; set; }
    }

    public sealed class ModelImagePart
    {
        public string ContentType { get; set; }
        public byte[] Bytes { get; set; }
        public string Label { get; set; }
    }

    public sealed class LlmClient
    {
        private const int MaxStoredReasoningChars = 100000;
        private readonly Func<string> _apiKeyProvider;
        private readonly Func<ChatAttachment, byte[]> _attachmentReader;
        private readonly Func<ChatAttachment, string> _attachmentTextReader;
        private readonly Func<AppSettings, ChatAttachment, IReadOnlyList<ModelImagePart>> _modelImageProvider;

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
            _attachmentReader = attachmentReader;
            _attachmentTextReader = attachmentTextReader;
            _modelImageProvider = modelImageProvider;
        }

        public async Task<LlmCompletionResult> CompleteAsync(AppSettings settings, IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default(CancellationToken))
        {
            return await CompleteAsync(settings, messages, null, cancellationToken).ConfigureAwait(false);
        }

        public async Task<LlmCompletionResult> CompleteAsync(
            AppSettings settings,
            IEnumerable<ChatMessage> messages,
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
                var apiBuild = BuildApiMessages(messageList, settings);
                var apiMessages = apiBuild.Messages;
                var hasImages = apiBuild.HasImages;
                if (apiMessages.Count == 0)
                {
                    throw new InvalidOperationException("LLM request has no messages.");
                }

                var body = new
                {
                    model = settings.Model,
                    messages = apiMessages,
                    max_tokens = settings.MaxTokens,
                    temperature = settings.Temperature,
                    top_p = settings.TopP,
                    stream = settings.StreamResponses,
                    stream_options = settings.StreamResponses ? new { include_usage = true } : null
                };

                var json = JsonConvert.SerializeObject(body, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                var diagnostics = CreateDiagnostics(requestUri, settings, apiMessages.Count, !string.IsNullOrWhiteSpace(apiKey));
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var requestContent = new StringContent(json, Encoding.UTF8, "application/json");
                    if (!string.IsNullOrWhiteSpace(contentTypeOverride))
                    {
                        requestContent.Headers.ContentType = MediaTypeHeaderValue.Parse(contentTypeOverride);
                    }

                    HttpResponseMessage response;
                    if (settings.StreamResponses)
                    {
                        var request = new HttpRequestMessage(HttpMethod.Post, requestUri) { Content = requestContent };
                        response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        response = await client.PostAsync(requestUri, requestContent, cancellationToken).ConfigureAwait(false);
                    }
                    var responseMediaType = response.Content.Headers.ContentType == null
                        ? string.Empty
                        : response.Content.Headers.ContentType.MediaType ?? string.Empty;
                    var isEventStream = responseMediaType.IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase) >= 0;
                    var responseJson = response.IsSuccessStatusCode && settings.StreamResponses && isEventStream
                        ? null
                        : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        if (hasImages && (int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
                        {
                            throw new InvalidOperationException(
                                "Выбранная модель или endpoint не принял изображения. Выберите мультимодальную модель с supports_images/input_modalities=image. HTTP " +
                                (int)response.StatusCode + ". Response: " + responseJson);
                        }
                        throw new InvalidOperationException("LLM request failed: HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase + ". " + diagnostics + ". Response: " + responseJson);
                    }

                    if (settings.StreamResponses && isEventStream)
                    {
                        using (response)
                        using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        {
                            return await ReadStreamingResponseAsync(stream, streamProgress, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    if (settings.StreamResponses && !string.IsNullOrWhiteSpace(responseJson) &&
                        responseJson.TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        return ParseStreamingResponse(responseJson);
                    }

                    var parsed = JObject.Parse(responseJson);
                    var message = parsed.SelectToken("choices[0].message") as JObject;
                    var usage = parsed["usage"] as JObject;
                    var promptTokens = ReadInt(usage, "prompt_tokens", "input_tokens");
                    var completionTokens = ReadInt(usage, "completion_tokens", "output_tokens");
                    var totalTokens = ReadInt(usage, "total_tokens");
                    if (totalTokens == null && promptTokens != null && completionTokens != null)
                    {
                        totalTokens = promptTokens.Value + completionTokens.Value;
                    }
                    return new LlmCompletionResult
                    {
                        Content = ReadAssistantContent(message),
                        ReasoningContent = ReadReasoningContent(message),
                        ReasoningTokens = ReadReasoningTokens(usage),
                        ReasoningTruncated = IsReasoningTruncated(message),
                        PromptTokens = promptTokens,
                        CompletionTokens = completionTokens,
                        TotalTokens = totalTokens,
                        UsageJson = usage == null ? null : usage.ToString(Formatting.None)
                    };
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

        internal static LlmCompletionResult ParseStreamingResponse(string sse)
        {
            var state = new StreamingCompletionState();
            using (var reader = new StringReader(sse ?? string.Empty))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    ProcessStreamingLine(line, state);
                }
            }
            return state.ToResult();
        }

        private static async Task<LlmCompletionResult> ReadStreamingResponseAsync(
            Stream stream,
            Action<LlmStreamUpdate> streamProgress,
            CancellationToken cancellationToken)
        {
            var state = new StreamingCompletionState(streamProgress);
            using (var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, true))
            {
                while (!reader.EndOfStream)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    ProcessStreamingLine(line, state);
                }
            }
            var result = state.ToResult();
            if (streamProgress != null)
            {
                streamProgress(new LlmStreamUpdate { Completed = true });
            }
            return result;
        }

        private static void ProcessStreamingLine(string line, StreamingCompletionState state)
        {
            if (state == null || string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            var trimmed = line.Trim();
            if (!trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var data = trimmed.Substring(5).Trim();
            if (string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase) || data.Length == 0)
            {
                return;
            }

            var chunk = JObject.Parse(data);
            state.Add(chunk);
        }

        private sealed class StreamingCompletionState
        {
            private readonly StringBuilder _content = new StringBuilder();
            private readonly StringBuilder _reasoning = new StringBuilder();
            private readonly SortedDictionary<int, StreamingToolCall> _toolCalls = new SortedDictionary<int, StreamingToolCall>();
            private readonly Action<LlmStreamUpdate> _progress;
            private JObject _usage;

            public StreamingCompletionState(Action<LlmStreamUpdate> progress = null)
            {
                _progress = progress;
            }

            public void Add(JObject chunk)
            {
                if (chunk == null)
                {
                    return;
                }

                var usage = chunk["usage"] as JObject;
                if (usage != null)
                {
                    _usage = usage;
                }

                var delta = chunk.SelectToken("choices[0].delta") as JObject;
                if (delta == null)
                {
                    return;
                }

                var content = delta["content"];
                if (content != null && content.Type == JTokenType.String)
                {
                    var value = content.Value<string>() ?? string.Empty;
                    _content.Append(value);
                    if (_progress != null && value.Length > 0)
                    {
                        _progress(new LlmStreamUpdate { ContentDelta = value });
                    }
                }

                var reasoning = delta["reasoning_content"] ?? delta["reasoning"];
                if (reasoning != null && reasoning.Type == JTokenType.String)
                {
                    var value = reasoning.Value<string>() ?? string.Empty;
                    _reasoning.Append(value);
                    if (_progress != null && value.Length > 0)
                    {
                        _progress(new LlmStreamUpdate { ReasoningDelta = value });
                    }
                }

                var calls = delta["tool_calls"] as JArray;
                if (calls == null)
                {
                    return;
                }

                foreach (var token in calls.OfType<JObject>())
                {
                    var index = token["index"] == null ? 0 : token["index"].Value<int>();
                    StreamingToolCall call;
                    if (!_toolCalls.TryGetValue(index, out call))
                    {
                        call = new StreamingToolCall();
                        _toolCalls[index] = call;
                    }

                    if (token["id"] != null)
                    {
                        call.Id = AppendFragment(call.Id, token["id"].Value<string>());
                    }
                    var function = token["function"] as JObject;
                    if (function != null)
                    {
                        if (function["name"] != null)
                        {
                            call.Name = AppendFragment(call.Name, function["name"].Value<string>());
                        }
                        if (function["arguments"] != null)
                        {
                            call.Arguments.Append(function["arguments"].Value<string>() ?? string.Empty);
                        }
                    }
                }
            }

            public LlmCompletionResult ToResult()
            {
                var message = new JObject
                {
                    ["content"] = _content.ToString(),
                    ["reasoning_content"] = _reasoning.ToString()
                };
                if (_toolCalls.Count > 0)
                {
                    var calls = new JArray();
                    foreach (var pair in _toolCalls)
                    {
                        calls.Add(new JObject
                        {
                            ["id"] = pair.Value.Id,
                            ["type"] = "function",
                            ["function"] = new JObject
                            {
                                ["name"] = pair.Value.Name,
                                ["arguments"] = pair.Value.Arguments.ToString()
                            }
                        });
                    }
                    message["tool_calls"] = calls;
                }

                var promptTokens = ReadInt(_usage, "prompt_tokens", "input_tokens");
                var completionTokens = ReadInt(_usage, "completion_tokens", "output_tokens");
                var totalTokens = ReadInt(_usage, "total_tokens");
                if (totalTokens == null && promptTokens != null && completionTokens != null)
                {
                    totalTokens = promptTokens.Value + completionTokens.Value;
                }
                return new LlmCompletionResult
                {
                    Content = ReadAssistantContent(message),
                    ReasoningContent = ReadReasoningContent(message),
                    ReasoningTokens = ReadReasoningTokens(_usage),
                    ReasoningTruncated = IsReasoningTruncated(message),
                    PromptTokens = promptTokens,
                    CompletionTokens = completionTokens,
                    TotalTokens = totalTokens,
                    UsageJson = _usage == null ? null : _usage.ToString(Formatting.None)
                };
            }

            private static string AppendFragment(string current, string fragment)
            {
                if (string.IsNullOrEmpty(fragment))
                {
                    return current ?? string.Empty;
                }
                if (!string.IsNullOrEmpty(current) && string.Equals(current, fragment, StringComparison.Ordinal))
                {
                    return current;
                }
                return (current ?? string.Empty) + fragment;
            }
        }

        private sealed class StreamingToolCall
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public StringBuilder Arguments { get; private set; } = new StringBuilder();
        }

        public async Task<string> GetModelsConfigJsonAsync(AppSettings settings, string apiKeyOverride)
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }

            var url = BuildModelsConfigUrl(settings.BaseUrl);
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
                    var response = await client.GetAsync(requestUri).ConfigureAwait(false);
                    var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException("Models config request failed: HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase + ". Endpoint: " + requestUri + ". Response: " + responseJson);
                    }

                    return responseJson;
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

        private static int? ReadInt(JObject obj, params string[] names)
        {
            if (obj == null || names == null)
            {
                return null;
            }

            foreach (var name in names)
            {
                var token = obj[name];
                if (token != null && token.Type != JTokenType.Null)
                {
                    return token.Value<int>();
                }
            }

            return null;
        }

        private static string ReadAssistantContent(JObject message)
        {
            if (message == null)
            {
                return string.Empty;
            }

            NormalizeReasoningFields(message);
            var content = message["content"] == null || message["content"].Type == JTokenType.Null
                ? string.Empty
                : message["content"].Value<string>() ?? string.Empty;
            var toolCalls = message["tool_calls"] as JArray;
            if (toolCalls == null || toolCalls.Count == 0)
            {
                return content;
            }

            var steps = new JArray();
            foreach (var call in toolCalls.OfType<JObject>())
            {
                var function = call["function"] as JObject;
                if (function == null)
                {
                    continue;
                }

                var arguments = ParseArgumentsObject((string)function["arguments"]);
                steps.Add(new JObject
                {
                    ["toolId"] = (string)function["name"] ?? string.Empty,
                    ["arguments"] = arguments,
                    ["reason"] = "Native tool call converted to RNAssistant planner step."
                });
            }

            return new JObject
            {
                ["kind"] = "tool_plan",
                ["intent"] = "mutate",
                ["message"] = string.IsNullOrWhiteSpace(content) ? null : content,
                ["steps"] = steps,
                ["expectedOutcome"] = "Execute converted native tool call steps."
            }.ToString(Formatting.None);
        }

        private static string ReadReasoningContent(JObject message)
        {
            if (message == null)
            {
                return string.Empty;
            }
            NormalizeReasoningFields(message);
            var token = message["reasoning_content"] ?? message["reasoning"];
            var value = token == null || token.Type == JTokenType.Null ? string.Empty : token.Value<string>() ?? string.Empty;
            return value.Length > MaxStoredReasoningChars ? value.Substring(0, MaxStoredReasoningChars) : value;
        }

        private static bool IsReasoningTruncated(JObject message)
        {
            if (message == null)
            {
                return false;
            }
            NormalizeReasoningFields(message);
            var token = message["reasoning_content"] ?? message["reasoning"];
            return token != null && token.Type == JTokenType.String &&
                (token.Value<string>() ?? string.Empty).Length > MaxStoredReasoningChars;
        }

        private static int? ReadReasoningTokens(JObject usage)
        {
            var details = usage == null ? null : usage["completion_tokens_details"] as JObject;
            return ReadInt(details, "reasoning_tokens");
        }

        private static void NormalizeReasoningFields(JObject message)
        {
            if (message == null)
            {
                return;
            }
            var reasoning = message["reasoning_content"] ?? message["reasoning"];
            if (reasoning != null && reasoning.Type == JTokenType.String && !string.IsNullOrWhiteSpace(reasoning.Value<string>()))
            {
                return;
            }
            var contentToken = message["content"];
            var content = contentToken == null || contentToken.Type != JTokenType.String
                ? string.Empty
                : contentToken.Value<string>() ?? string.Empty;
            if (!content.StartsWith("<think>", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            var close = content.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
            if (close < 0)
            {
                return;
            }
            message["reasoning_content"] = content.Substring(7, close - 7).Trim();
            message["content"] = content.Substring(close + 8).TrimStart();
        }

        private static JObject ParseArgumentsObject(string arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments))
            {
                return new JObject();
            }

            try
            {
                return JObject.Parse(arguments);
            }
            catch (JsonException)
            {
                return new JObject { ["rawArguments"] = arguments };
            }
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

        private List<object> ToApiMessages(IEnumerable<ChatMessage> messages)
        {
            return BuildApiMessages(messages, null).Messages;
        }

        private ApiMessageBuildResult BuildApiMessages(IEnumerable<ChatMessage> messages, AppSettings settings)
        {
            var build = new ApiMessageBuildResult();
            if (messages == null)
            {
                return build;
            }
            var messageList = messages.ToList();
            var remainingAttachmentTokens = Math.Max(
                0,
                ModelContextBudget.InputBudgetTokens(settings) -
                ModelContextBudget.EstimateMessagesTokens(messageList) -
                EstimatePdfImageTokens(messageList, settings));

            foreach (var message in messageList)
            {
                if (message == null || string.IsNullOrWhiteSpace(message.Role))
                {
                    continue;
                }

                var attachments = message.Attachments ?? new List<ChatAttachment>();
                var text = AppendExtractedText(message.Content ?? string.Empty, attachments, ref remainingAttachmentTokens);
                var imageParts = new List<ModelImagePart>();
                var imageLimit = ModelContextBudget.MaxImagesPerPrompt(settings);
                foreach (var attachment in attachments.Where(a => a != null && a.Kind == "image"))
                {
                    imageParts.AddRange(ReadModelImages(settings, attachment));
                }
                foreach (var attachment in attachments.Where(a => a != null && a.Kind == "pdf"))
                {
                    if (imageParts.Count >= imageLimit)
                    {
                        break;
                    }
                    imageParts.AddRange(ReadModelImages(settings, attachment).Take(imageLimit - imageParts.Count));
                }
                if (imageParts.Count > imageLimit)
                {
                    imageParts = imageParts.Take(imageLimit).ToList();
                }
                if (imageParts.Count == 0)
                {
                    var unreadablePdf = attachments.FirstOrDefault(a =>
                        a != null && a.Kind == "pdf" &&
                        (a.PageTextLengths == null || a.PageTextLengths.Count == 0 || a.PageTextLengths.All(length => length < 20)));
                    if (unreadablePdf != null)
                    {
                        throw new InvalidOperationException(
                            unreadablePdf.FileName + ": PDF contains no usable text and the selected model does not support visual PDF pages.");
                    }
                    build.Messages.Add(new { role = message.Role, content = text });
                    continue;
                }

                var parts = new List<object> { new { type = "text", text = text } };
                foreach (var image in imageParts)
                {
                    if (image == null || image.Bytes == null || image.Bytes.Length == 0)
                    {
                        continue;
                    }
                    parts.Add(new
                    {
                        type = "image_url",
                        image_url = new { url = "data:" + image.ContentType + ";base64," + Convert.ToBase64String(image.Bytes) }
                    });
                    build.HasImages = true;
                }
                build.Messages.Add(new { role = message.Role, content = parts });
            }

            return build;
        }

        private static int EstimatePdfImageTokens(IEnumerable<ChatMessage> messages, AppSettings settings)
        {
            if (!ModelContextBudget.SupportsImages(settings))
            {
                return 0;
            }
            var maxImages = ModelContextBudget.MaxImagesPerPrompt(settings);
            var count = 0;
            foreach (var message in messages ?? new ChatMessage[0])
            {
                var attachments = message == null ? null : message.Attachments;
                var ordinary = (attachments ?? new List<ChatAttachment>()).Count(attachment => attachment != null && attachment.Kind == "image");
                var remaining = Math.Max(0, maxImages - ordinary);
                foreach (var pdf in (attachments ?? new List<ChatAttachment>()).Where(attachment => attachment != null && attachment.Kind == "pdf"))
                {
                    if (remaining <= 0) break;
                    var pages = Math.Min(remaining, Math.Max(1, pdf.PageCount));
                    count += pages;
                    remaining -= pages;
                }
            }
            return count * ModelContextBudget.EstimatedImageTokens;
        }

        private IEnumerable<ModelImagePart> ReadModelImages(AppSettings settings, ChatAttachment attachment)
        {
            var supplied = _modelImageProvider == null ? null : _modelImageProvider(settings, attachment);
            if (supplied != null && supplied.Count > 0)
            {
                return supplied.Where(part => part != null).ToList();
            }
            if (attachment == null || attachment.Kind != "image")
            {
                return new ModelImagePart[0];
            }
            var bytes = _attachmentReader == null ? null : _attachmentReader(attachment);
            if (bytes == null || bytes.Length == 0)
            {
                throw new InvalidOperationException("Attachment file is missing: " + (attachment.FileName ?? attachment.Id));
            }
            return new[]
            {
                new ModelImagePart { Bytes = bytes, ContentType = attachment.ContentType, Label = attachment.FileName }
            };
        }

        private string AppendExtractedText(
            string content,
            IEnumerable<ChatAttachment> attachments,
            ref int remainingTokens)
        {
            var builder = new StringBuilder(content ?? string.Empty);
            foreach (var attachment in attachments ?? new ChatAttachment[0])
            {
                var extracted = attachment == null
                    ? string.Empty
                    : (_attachmentTextReader == null ? attachment.ExtractedText : _attachmentTextReader(attachment));
                if (string.IsNullOrWhiteSpace(extracted))
                {
                    continue;
                }
                var selected = TruncateToEstimatedTokens(extracted, remainingTokens);
                var selectedTokens = ModelContextBudget.EstimateTextTokens(selected);
                remainingTokens = Math.Max(0, remainingTokens - selectedTokens);
                builder.AppendLine();
                builder.AppendLine();
                builder.AppendLine("[Attachment: " + attachment.FileName + "]");
                builder.Append(selected);
                if (attachment.TextTruncated || selected.Length < extracted.Length)
                {
                    builder.AppendLine();
                    builder.Append("[Content truncated]");
                }
                builder.AppendLine();
                builder.Append("[End attachment]");
            }
            return builder.ToString();
        }

        private static string TruncateToEstimatedTokens(string text, int maxTokens)
        {
            if (string.IsNullOrEmpty(text) || maxTokens <= 0)
            {
                return string.Empty;
            }
            if (ModelContextBudget.EstimateTextTokens(text) <= maxTokens)
            {
                return text;
            }
            var low = 0;
            var high = text.Length;
            while (low < high)
            {
                var middle = low + (high - low + 1) / 2;
                if (ModelContextBudget.EstimateTextTokens(text.Substring(0, middle)) <= maxTokens)
                {
                    low = middle;
                }
                else
                {
                    high = middle - 1;
                }
            }
            return text.Substring(0, low);
        }

        private sealed class ApiMessageBuildResult
        {
            public List<object> Messages { get; private set; } = new List<object>();
            public bool HasImages { get; set; }
        }
    }
}
