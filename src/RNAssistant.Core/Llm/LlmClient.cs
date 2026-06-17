using System;
using System.Collections.Generic;
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

            using (var client = new HttpClient())
            {
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
                var response = await client.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json")).ConfigureAwait(false);
                var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException("LLM request failed: " + (int)response.StatusCode + " " + responseJson);
                }

                var parsed = JObject.Parse(responseJson);
                var content = parsed.SelectToken("choices[0].message.content");
                return content == null ? string.Empty : content.Value<string>();
            }
        }

        private static string CombineUrl(string baseUrl, string path)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                baseUrl = "https://api.openai.com/v1";
            }

            return baseUrl.TrimEnd('/') + path;
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
