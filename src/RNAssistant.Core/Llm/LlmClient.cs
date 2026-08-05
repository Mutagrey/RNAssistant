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
        public List<LlmToolCall> ToolCalls { get; set; }
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
            return await CompleteAsync(settings, messages, null, null, cancellationToken).ConfigureAwait(false);
        }

        public async Task<LlmCompletionResult> CompleteAsync(
            AppSettings settings,
            IEnumerable<ChatMessage> messages,
            Action<LlmStreamUpdate> streamProgress,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return await CompleteAsync(settings, messages, null, streamProgress, cancellationToken).ConfigureAwait(false);
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
                var apiBuild = BuildApiMessages(messageList, settings);
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
                                return await ReadStreamingOrJsonResponseAsync(stream, streamProgress, cancellationToken).ConfigureAwait(false);
                            }
                        }

                        var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        return ParseCompletionResponse(responseJson);
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

        internal static LlmCompletionResult ParseStreamingResponse(string sse)
        {
            return ParseStreamingResponse(sse, null);
        }

        internal static LlmCompletionResult ParseStreamingResponse(string sse, Action<LlmStreamUpdate> streamProgress)
        {
            var state = new StreamingCompletionState(streamProgress);
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

        internal static LlmCompletionResult ParseCompletionResponse(string responseJson)
        {
            if (string.IsNullOrWhiteSpace(responseJson))
            {
                throw new InvalidOperationException("LLM response body is empty.");
            }

            JObject parsed;
            try
            {
                parsed = JObject.Parse(responseJson.TrimStart('\uFEFF'));
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("LLM response is not valid JSON: " + ex.Message, ex);
            }

            var message = parsed.SelectToken("choices[0].message") as JObject;
            if (message == null)
            {
                var error = parsed.SelectToken("error.message");
                var suffix = error != null && error.Type == JTokenType.String
                    ? " Endpoint error: " + error.Value<string>()
                    : string.Empty;
                throw new InvalidOperationException("LLM response has no choices[0].message." + suffix);
            }

            return BuildCompletionResult(message, parsed["usage"] as JObject);
        }

        internal static async Task<LlmCompletionResult> ReadStreamingOrJsonResponseAsync(
            Stream stream,
            Action<LlmStreamUpdate> streamProgress,
            CancellationToken cancellationToken)
        {
            if (stream == null)
            {
                throw new ArgumentNullException("stream");
            }

            var state = new StreamingCompletionState(streamProgress);
            var bufferedJson = new StringBuilder();
            bool? isEventStream = null;
            using (var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, true))
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null)
                    {
                        break;
                    }

                    if (!isEventStream.HasValue && !string.IsNullOrWhiteSpace(line))
                    {
                        var probe = line.TrimStart('\uFEFF').TrimStart();
                        if (!string.IsNullOrWhiteSpace(probe))
                        {
                            isEventStream = IsEventStreamLine(probe);
                        }
                    }

                    if (isEventStream == true)
                    {
                        ProcessStreamingLine(line, state);
                    }
                    else if (isEventStream == false)
                    {
                        if (bufferedJson.Length > 0)
                        {
                            bufferedJson.AppendLine();
                        }
                        bufferedJson.Append(line);
                    }
                }
            }

            if (isEventStream != true)
            {
                return ParseCompletionResponse(bufferedJson.ToString());
            }

            var result = state.ToResult();
            if (streamProgress != null)
            {
                streamProgress(new LlmStreamUpdate { Completed = true });
            }
            return result;
        }

        private static bool IsEventStreamLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            return line.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("event:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("id:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("retry:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith(":", StringComparison.Ordinal);
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

            try
            {
                var chunk = JObject.Parse(data.TrimStart('\uFEFF'));
                state.Add(chunk);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("LLM stream chunk is not valid JSON: " + ex.Message, ex);
            }
        }

        private sealed class StreamingCompletionState
        {
            private readonly StringBuilder _content = new StringBuilder();
            private readonly StringBuilder _reasoning = new StringBuilder();
            private readonly StringBuilder _embeddedReasoning = new StringBuilder();
            private readonly Action<LlmStreamUpdate> _progress;
            private readonly ThinkStreamSplitter _thinkSplitter;
            private readonly IDictionary<int, StreamingToolCallState> _toolCalls = new SortedDictionary<int, StreamingToolCallState>();
            private JObject _usage;
            private bool _reasoningTruncated;
            private bool _embeddedReasoningTruncated;
            private bool _embeddedProgressReported;

            public StreamingCompletionState(Action<LlmStreamUpdate> progress = null)
            {
                _progress = progress;
                _thinkSplitter = new ThinkStreamSplitter(AddContent, AddEmbeddedReasoning);
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

                var content = ReadStringToken(delta["content"], "choices[0].delta.content");
                if (!string.IsNullOrEmpty(content))
                {
                    _thinkSplitter.Add(content);
                }

                var reasoning = ReadStringToken(
                    delta["reasoning_content"] ?? delta["reasoning"],
                    "choices[0].delta.reasoning");
                if (!string.IsNullOrEmpty(reasoning))
                {
                    AddReasoning(reasoning);
                }

                var toolCalls = delta["tool_calls"] as JArray;
                if (toolCalls != null)
                {
                    foreach (var token in toolCalls.OfType<JObject>())
                    {
                        var index = token["index"] == null ? 0 : token["index"].Value<int>();
                        StreamingToolCallState call;
                        if (!_toolCalls.TryGetValue(index, out call))
                        {
                            call = new StreamingToolCallState();
                            _toolCalls[index] = call;
                        }
                        call.Add(token);
                    }
                }
            }

            public LlmCompletionResult ToResult()
            {
                _thinkSplitter.Complete();
                var reasoning = _reasoning.Length > 0 ? _reasoning.ToString() : _embeddedReasoning.ToString();
                var message = new JObject
                {
                    ["content"] = _content.ToString(),
                    ["reasoning_content"] = reasoning
                };
                if (_toolCalls.Count > 0)
                {
                    message["tool_calls"] = new JArray(_toolCalls.Values.Select(call => call.ToJson()));
                }
                var result = BuildCompletionResult(message, _usage);
                result.ReasoningTruncated = _reasoning.Length > 0
                    ? _reasoningTruncated
                    : _embeddedReasoningTruncated;
                return result;
            }

            private void AddContent(string value)
            {
                if (string.IsNullOrEmpty(value))
                {
                    return;
                }
                _content.Append(value);
                if (_progress != null)
                {
                    _progress(new LlmStreamUpdate { ContentDelta = value });
                }
            }

            private void AddReasoning(string value)
            {
                var stored = AppendLimited(_reasoning, value, ref _reasoningTruncated);
                if (!_embeddedProgressReported && _progress != null && stored.Length > 0)
                {
                    _progress(new LlmStreamUpdate { ReasoningDelta = stored });
                }
            }

            private void AddEmbeddedReasoning(string value)
            {
                var stored = AppendLimited(_embeddedReasoning, value, ref _embeddedReasoningTruncated);
                if (_reasoning.Length == 0 && _progress != null && stored.Length > 0)
                {
                    _embeddedProgressReported = true;
                    _progress(new LlmStreamUpdate { ReasoningDelta = stored });
                }
            }

            private static string AppendLimited(StringBuilder target, string value, ref bool truncated)
            {
                if (string.IsNullOrEmpty(value))
                {
                    return string.Empty;
                }
                var remaining = MaxStoredReasoningChars - target.Length;
                if (remaining <= 0)
                {
                    truncated = true;
                    return string.Empty;
                }
                var stored = value.Length <= remaining ? value : value.Substring(0, remaining);
                target.Append(stored);
                if (stored.Length < value.Length)
                {
                    truncated = true;
                }
                return stored;
            }
        }

        private sealed class StreamingToolCallState
        {
            private readonly StringBuilder _name = new StringBuilder();
            private readonly StringBuilder _arguments = new StringBuilder();
            private string _id;
            private string _type;

            public void Add(JObject token)
            {
                if (token == null) return;
                if (token["id"] != null) _id = (string)token["id"];
                if (token["type"] != null) _type = (string)token["type"];
                var function = token["function"] as JObject;
                if (function == null) return;
                if (function["name"] != null) _name.Append((string)function["name"]);
                if (function["arguments"] != null) _arguments.Append((string)function["arguments"]);
            }

            public JObject ToJson()
            {
                return new JObject
                {
                    ["id"] = _id,
                    ["type"] = string.IsNullOrWhiteSpace(_type) ? "function" : _type,
                    ["function"] = new JObject
                    {
                        ["name"] = _name.ToString(),
                        ["arguments"] = _arguments.Length == 0 ? "{}" : _arguments.ToString()
                    }
                };
            }
        }

        private sealed class ThinkStreamSplitter
        {
            private const string OpenTag = "<think>";
            private const string CloseTag = "</think>";
            private readonly Action<string> _content;
            private readonly Action<string> _reasoning;
            private readonly StringBuilder _pending = new StringBuilder();
            private int _state;

            public ThinkStreamSplitter(Action<string> content, Action<string> reasoning)
            {
                _content = content;
                _reasoning = reasoning;
            }

            public void Add(string value)
            {
                if (string.IsNullOrEmpty(value))
                {
                    return;
                }
                if (_state == 2)
                {
                    _content(value);
                    return;
                }
                _pending.Append(value);
                Process(false);
            }

            public void Complete()
            {
                Process(true);
            }

            private void Process(bool completed)
            {
                if (_state == 0)
                {
                    var value = _pending.ToString();
                    var leading = 0;
                    while (leading < value.Length && char.IsWhiteSpace(value[leading]))
                    {
                        leading++;
                    }
                    var candidate = value.Substring(leading);
                    if (candidate.Length < OpenTag.Length &&
                        OpenTag.StartsWith(candidate, StringComparison.OrdinalIgnoreCase) && !completed)
                    {
                        return;
                    }
                    if (candidate.StartsWith(OpenTag, StringComparison.OrdinalIgnoreCase))
                    {
                        _pending.Remove(0, leading + OpenTag.Length);
                        _state = 1;
                    }
                    else
                    {
                        _pending.Clear();
                        _state = 2;
                        _content(value);
                        return;
                    }
                }

                if (_state != 1)
                {
                    return;
                }
                var reasoning = _pending.ToString();
                var closeIndex = reasoning.IndexOf(CloseTag, StringComparison.OrdinalIgnoreCase);
                if (closeIndex >= 0)
                {
                    if (closeIndex > 0)
                    {
                        _reasoning(reasoning.Substring(0, closeIndex));
                    }
                    var remainder = reasoning.Substring(closeIndex + CloseTag.Length);
                    _pending.Clear();
                    _state = 2;
                    if (remainder.Length > 0)
                    {
                        _content(remainder);
                    }
                    return;
                }

                var emitLength = completed ? reasoning.Length : Math.Max(0, reasoning.Length - (CloseTag.Length - 1));
                if (emitLength > 0)
                {
                    _reasoning(reasoning.Substring(0, emitLength));
                    _pending.Remove(0, emitLength);
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

            return ReadStringToken(message["content"], "choices[0].message.content");
        }

        private static string ReadReasoningContent(JObject message)
        {
            if (message == null)
            {
                return string.Empty;
            }
            var token = message["reasoning_content"] ?? message["reasoning"];
            var value = ReadStringToken(token, "choices[0].message.reasoning");
            return value.Length > MaxStoredReasoningChars ? value.Substring(0, MaxStoredReasoningChars) : value;
        }

        private static bool IsReasoningTruncated(JObject message)
        {
            if (message == null)
            {
                return false;
            }
            var token = message["reasoning_content"] ?? message["reasoning"];
            return token != null && token.Type == JTokenType.String &&
                (token.Value<string>() ?? string.Empty).Length > MaxStoredReasoningChars;
        }

        private static int? ReadReasoningTokens(JObject usage)
        {
            var details = usage == null ? null : usage["completion_tokens_details"] as JObject;
            var value = ReadInt(details, "reasoning_tokens");
            if (value.HasValue)
            {
                return value;
            }
            details = usage == null ? null : usage["output_tokens_details"] as JObject;
            return ReadInt(details, "reasoning_tokens") ?? ReadInt(usage, "reasoning_tokens");
        }

        private static LlmCompletionResult BuildCompletionResult(JObject message, JObject usage)
        {
            var content = ReadAssistantContent(message);
            string embeddedReasoning;
            bool embeddedTruncated;
            content = ExtractLeadingThink(content, out embeddedReasoning, out embeddedTruncated);
            var providerReasoning = ReadReasoningContent(message);
            var promptTokens = ReadInt(usage, "prompt_tokens", "input_tokens");
            var completionTokens = ReadInt(usage, "completion_tokens", "output_tokens");
            var totalTokens = ReadInt(usage, "total_tokens");
            if (totalTokens == null && promptTokens != null && completionTokens != null)
            {
                totalTokens = promptTokens.Value + completionTokens.Value;
            }
            return new LlmCompletionResult
            {
                Content = content,
                ToolCalls = ReadToolCalls(message),
                ReasoningContent = providerReasoning.Length > 0 ? providerReasoning : embeddedReasoning,
                ReasoningTokens = ReadReasoningTokens(usage),
                ReasoningTruncated = providerReasoning.Length > 0
                    ? IsReasoningTruncated(message)
                    : embeddedTruncated,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                TotalTokens = totalTokens,
                UsageJson = usage == null ? null : usage.ToString(Formatting.None)
            };
        }

        private static List<LlmToolCall> ReadToolCalls(JObject message)
        {
            var result = new List<LlmToolCall>();
            var calls = message == null ? null : message["tool_calls"] as JArray;
            foreach (var token in calls == null ? new JObject[0] : calls.OfType<JObject>())
            {
                var function = token["function"] as JObject;
                if (function == null) continue;
                result.Add(new LlmToolCall
                {
                    Id = (string)token["id"],
                    Type = (string)token["type"] ?? "function",
                    Name = (string)function["name"],
                    ArgumentsJson = (string)function["arguments"] ?? "{}"
                });
            }
            return result;
        }

        private static string ExtractLeadingThink(string content, out string reasoning, out bool truncated)
        {
            reasoning = string.Empty;
            truncated = false;
            if (string.IsNullOrEmpty(content))
            {
                return content ?? string.Empty;
            }
            var leading = 0;
            while (leading < content.Length && char.IsWhiteSpace(content[leading]))
            {
                leading++;
            }
            const string openTag = "<think>";
            const string closeTag = "</think>";
            if (content.IndexOf(openTag, leading, StringComparison.OrdinalIgnoreCase) != leading)
            {
                return content;
            }
            var reasoningStart = leading + openTag.Length;
            var closeIndex = content.IndexOf(closeTag, reasoningStart, StringComparison.OrdinalIgnoreCase);
            var rawReasoning = closeIndex < 0
                ? content.Substring(reasoningStart)
                : content.Substring(reasoningStart, closeIndex - reasoningStart);
            truncated = rawReasoning.Length > MaxStoredReasoningChars;
            reasoning = truncated ? rawReasoning.Substring(0, MaxStoredReasoningChars) : rawReasoning;
            return closeIndex < 0 ? string.Empty : content.Substring(closeIndex + closeTag.Length);
        }

        private static string ReadStringToken(JToken token, string field)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return string.Empty;
            }
            if (token.Type == JTokenType.String)
            {
                return token.Value<string>() ?? string.Empty;
            }
            throw new InvalidOperationException(field + " must be a string or null.");
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
                ModelContextBudget.EstimateMessagesTokens(messageList, false) -
                EstimatePdfImageTokens(messageList, settings));

            foreach (var message in messageList)
            {
                if (message == null || string.IsNullOrWhiteSpace(message.Role))
                {
                    continue;
                }

                if (message.ToolCalls != null && message.ToolCalls.Count > 0)
                {
                    build.Messages.Add(new JObject
                    {
                        ["role"] = message.Role,
                        ["content"] = string.IsNullOrEmpty(message.Content) ? null : message.Content,
                        ["tool_calls"] = new JArray(message.ToolCalls.Select(call => new JObject
                        {
                            ["id"] = call.Id,
                            ["type"] = string.IsNullOrWhiteSpace(call.Type) ? "function" : call.Type,
                            ["function"] = new JObject
                            {
                                ["name"] = call.Name,
                                ["arguments"] = string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson
                            }
                        }))
                    });
                    build.EstimatedPromptTokens += 12 + ModelContextBudget.EstimateTextTokens(message.Content) +
                        message.ToolCalls.Sum(call => ModelContextBudget.EstimateTextTokens(call.Name) + ModelContextBudget.EstimateTextTokens(call.ArgumentsJson));
                    continue;
                }

                if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(message.ToolCallId))
                    {
                        throw new InvalidOperationException("A role=tool message requires ToolCallId.");
                    }
                    var toolMessage = new JObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = message.ToolCallId,
                        ["content"] = message.Content ?? string.Empty
                    };
                    if (!string.IsNullOrWhiteSpace(message.ToolName)) toolMessage["name"] = message.ToolName;
                    build.Messages.Add(toolMessage);
                    build.EstimatedPromptTokens += 6 + ModelContextBudget.EstimateTextTokens(message.Content);
                    continue;
                }

                var attachments = message.Attachments ?? new List<ChatAttachment>();
                var text = AppendExtractedText(message.Content ?? string.Empty, attachments, ref remainingAttachmentTokens);
                var imageParts = new List<ModelImagePart>();
                var audioAttachments = attachments
                    .Where(attachment => attachment != null && string.Equals(attachment.Kind, "audio", StringComparison.OrdinalIgnoreCase))
                    .ToList();
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
                if (imageParts.Count == 0 && audioAttachments.Count == 0)
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
                    build.EstimatedPromptTokens += 4 + ModelContextBudget.EstimateTextTokens(text);
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
                foreach (var audioAttachment in audioAttachments)
                {
                    var bytes = _attachmentReader == null ? null : _attachmentReader(audioAttachment);
                    if (bytes == null || bytes.Length == 0)
                    {
                        throw new InvalidOperationException("Attachment file is missing: " + (audioAttachment.FileName ?? audioAttachment.Id));
                    }
                    parts.Add(new
                    {
                        type = "input_audio",
                        input_audio = new
                        {
                            data = Convert.ToBase64String(bytes),
                            format = AudioFormat(audioAttachment)
                        }
                    });
                    build.HasAudio = true;
                }
                build.Messages.Add(new { role = message.Role, content = parts });
                build.EstimatedPromptTokens += 4 + ModelContextBudget.EstimateTextTokens(text) +
                    imageParts.Count * ModelContextBudget.EstimatedImageTokens;
            }

            return build;
        }

        internal static JObject BuildRequestBody(AppSettings settings, IList<object> apiMessages, int estimatedPromptTokens, LlmRequestOptions requestOptions)
        {
            settings = settings ?? new AppSettings();
            var body = new JObject
            {
                ["model"] = settings.Model,
                ["messages"] = JArray.FromObject(apiMessages ?? new object[0]),
                ["max_tokens"] = ModelContextBudget.EffectiveOutputTokens(settings, estimatedPromptTokens, settings.Model),
                ["temperature"] = settings.Temperature,
                ["top_p"] = settings.TopP,
                ["stream"] = settings.StreamResponses
            };
            if (settings.StreamResponses) body["stream_options"] = new JObject { ["include_usage"] = true };

            requestOptions = requestOptions ?? new LlmRequestOptions();
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

        private static string AudioFormat(ChatAttachment attachment)
        {
            var contentType = attachment == null ? string.Empty : attachment.ContentType ?? string.Empty;
            var extension = attachment == null ? string.Empty : Path.GetExtension(attachment.FileName ?? string.Empty);
            if (contentType.IndexOf("wav", StringComparison.OrdinalIgnoreCase) >= 0 ||
                string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase))
            {
                return "wav";
            }
            if (contentType.IndexOf("mpeg", StringComparison.OrdinalIgnoreCase) >= 0 ||
                string.Equals(extension, ".mp3", StringComparison.OrdinalIgnoreCase))
            {
                return "mp3";
            }
            throw new InvalidOperationException(
                (attachment == null ? "Audio attachment" : attachment.FileName) + ": supported audio formats are MP3 and WAV.");
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
            public bool HasAudio { get; set; }
            public int EstimatedPromptTokens { get; set; }
        }
    }
}
