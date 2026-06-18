using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Llm
{
    public sealed class LlmCompletionResult
    {
        public string Content { get; set; }
        public int? PromptTokens { get; set; }
        public int? CompletionTokens { get; set; }
        public int? TotalTokens { get; set; }
        public string UsageJson { get; set; }
    }

    public sealed class LlmClient
    {
        private readonly Func<string> _apiKeyProvider;

        public LlmClient(Func<string> apiKeyProvider)
        {
            _apiKeyProvider = apiKeyProvider;
        }

        public async Task<LlmCompletionResult> CompleteAsync(AppSettings settings, IEnumerable<ChatMessage> messages)
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
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
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

                var apiMessages = ToApiMessages(messages);
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
                    stream = false
                };

                var json = JsonConvert.SerializeObject(body, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                var diagnostics = CreateDiagnostics(requestUri, settings, apiMessages.Count, !string.IsNullOrWhiteSpace(apiKey));
                try
                {
                    var requestContent = new StringContent(json, Encoding.UTF8, "application/json");
                    if (!string.IsNullOrWhiteSpace(contentTypeOverride))
                    {
                        requestContent.Headers.ContentType = MediaTypeHeaderValue.Parse(contentTypeOverride);
                    }

                    var response = await client.PostAsync(requestUri, requestContent).ConfigureAwait(false);
                    var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException("LLM request failed: HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase + ". " + diagnostics + ". Response: " + responseJson);
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
                        PromptTokens = promptTokens,
                        CompletionTokens = completionTokens,
                        TotalTokens = totalTokens,
                        UsageJson = usage == null ? null : usage.ToString(Formatting.None)
                    };
                }
                catch (TaskCanceledException ex)
                {
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

            var content = message["content"] == null || message["content"].Type == JTokenType.Null
                ? string.Empty
                : message["content"].Value<string>() ?? string.Empty;
            var toolCalls = message["tool_calls"] as JArray;
            if (toolCalls == null || toolCalls.Count == 0)
            {
                return content;
            }

            var block = "```rnassistant-agent\n" +
                new JObject { ["tool_calls"] = toolCalls.DeepClone() }.ToString(Formatting.None) +
                "\n```";
            return string.IsNullOrWhiteSpace(content)
                ? block
                : content + "\n\n" + block;
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

        private static List<object> ToApiMessages(IEnumerable<ChatMessage> messages)
        {
            var result = new List<object>();
            if (messages == null)
            {
                return result;
            }

            foreach (var message in messages)
            {
                if (message == null || string.IsNullOrWhiteSpace(message.Role))
                {
                    continue;
                }

                result.Add(new
                {
                    role = message.Role,
                    content = message.Content ?? string.Empty
                });
            }

            return result;
        }
    }
}
