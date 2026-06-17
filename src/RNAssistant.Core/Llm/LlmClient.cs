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
    public sealed class LlmClient
    {
        private readonly Func<string> _apiKeyProvider;

        public LlmClient(Func<string> apiKeyProvider)
        {
            _apiKeyProvider = apiKeyProvider;
        }

        public async Task<string> CompleteAsync(AppSettings settings, IEnumerable<ChatMessage> messages)
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }

            var apiKey = _apiKeyProvider == null ? null : _apiKeyProvider();
            var url = CombineUrl(settings.BaseUrl, "/chat/completions");
            Uri requestUri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out requestUri))
            {
                throw new InvalidOperationException("Invalid LLM endpoint URL: " + url);
            }

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(120);
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }

                foreach (var header in settings.CustomHeaders)
                {
                    if (!string.IsNullOrWhiteSpace(header.Key) && !string.IsNullOrWhiteSpace(header.Value))
                    {
                        client.DefaultRequestHeaders.Remove(header.Key);
                        client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }

                var body = new
                {
                    model = settings.Model,
                    messages = ToApiMessages(messages),
                    max_tokens = settings.MaxTokens,
                    temperature = settings.Temperature,
                    stream = false
                };

                var json = JsonConvert.SerializeObject(body, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                try
                {
                    var response = await client.PostAsync(requestUri, new StringContent(json, Encoding.UTF8, "application/json")).ConfigureAwait(false);
                    var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException("LLM request failed: HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase + ". Endpoint: " + requestUri + ". Response: " + responseJson);
                    }

                    var parsed = JObject.Parse(responseJson);
                    var content = parsed.SelectToken("choices[0].message.content");
                    return content == null ? string.Empty : content.Value<string>();
                }
                catch (TaskCanceledException ex)
                {
                    throw new InvalidOperationException("LLM request timed out after " + client.Timeout.TotalSeconds + " seconds. Endpoint: " + requestUri + ". " + DeepestMessage(ex), ex);
                }
                catch (HttpRequestException ex)
                {
                    throw new InvalidOperationException("LLM request could not be sent. Endpoint: " + requestUri + ". " + DeepestMessage(ex), ex);
                }
                catch (WebException ex)
                {
                    throw new InvalidOperationException("LLM network error. Endpoint: " + requestUri + ". " + DeepestMessage(ex), ex);
                }
            }
        }

        private static string CombineUrl(string baseUrl, string path)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                baseUrl = "https://api.openai.com/v1";
            }

            if (baseUrl.IndexOf("/chat/completions", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return baseUrl;
            }

            return baseUrl.TrimEnd('/') + path;
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

        private static List<object> ToApiMessages(IEnumerable<ChatMessage> messages)
        {
            var result = new List<object>();
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
