using System;
using System.Collections.Generic;
using System.IO;
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
    internal static class LlmHttpTransport
    {
        public const int MaxResponseBodyBytes = 32 * 1024 * 1024;
        public const int MaxErrorBodyBytes = 128 * 1024;
        public const int MaxModelsConfigBytes = 8 * 1024 * 1024;
        private const int MaxRequestBodyBytes = 96 * 1024 * 1024;
        private static readonly HttpClient Client = CreateClient();

        static LlmHttpTransport()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        public static async Task<HttpResponseMessage> SendAsync(
            HttpMethod method,
            Uri uri,
            HttpContent content,
            AppSettings settings,
            string apiKey,
            string accept,
            CancellationToken cancellationToken)
        {
            using (var request = new HttpRequestMessage(method, uri))
            {
                request.Content = content;
                ApplyHeaders(request, settings, apiKey, accept);
#pragma warning disable SYSLIB0014
                ServicePointManager.FindServicePoint(uri).ConnectionLeaseTimeout = 5 * 60 * 1000;
#pragma warning restore SYSLIB0014
                return await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
        }

        public static HttpContent CreateJsonContent(JObject body)
        {
            using (var output = new MemoryStream())
            {
                using (var textWriter = new StreamWriter(output, new UTF8Encoding(false), 8192, true))
                using (var jsonWriter = new JsonTextWriter(textWriter) { Formatting = Formatting.None })
                {
                    (body ?? new JObject()).WriteTo(jsonWriter);
                    jsonWriter.Flush();
                }

                if (output.Length > MaxRequestBodyBytes)
                {
                    throw new LlmRequestException(
                        LlmFailureKind.RequestTooLarge,
                        "LLM request body exceeds the 96 MB safety limit.");
                }

                var result = new ByteArrayContent(output.GetBuffer(), 0, (int)output.Length);
                result.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                return result;
            }
        }

        public static async Task<string> ReadContentAsStringAsync(
            HttpContent content,
            int maxBytes,
            CancellationToken cancellationToken)
        {
            if (content == null) return string.Empty;
            if (content.Headers.ContentLength.HasValue && content.Headers.ContentLength.Value > maxBytes)
            {
                throw TooLarge();
            }

            using (var stream = await content.ReadAsStreamAsync().ConfigureAwait(false))
            using (cancellationToken.Register(state => ((Stream)state).Dispose(), stream))
            using (var output = new MemoryStream())
            {
                var buffer = new byte[8192];
                try
                {
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                        if (read <= 0) break;
                        if (output.Length + read > maxBytes) throw TooLarge();
                        output.Write(buffer, 0, read);
                    }
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                catch (IOException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                var encoding = ResponseEncoding(content);
                return encoding.GetString(output.GetBuffer(), 0, (int)output.Length);
            }
        }

        public static LlmFailureKind FailureKind(HttpStatusCode statusCode, string body, LlmRequestOptions options)
        {
            var status = (int)statusCode;
            if (status == 429) return LlmFailureKind.RateLimited;
            if (status >= 500) return LlmFailureKind.TransientServer;
            return LlmFailureKind.Http;
        }

        private static HttpClient CreateClient()
        {
            return new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        }

        private static void ApplyHeaders(HttpRequestMessage request, AppSettings settings, string apiKey, string accept)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            request.Headers.UserAgent.ParseAdd("RNAssistant/0.1");
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            foreach (var header in settings.CustomHeaders ?? new Dictionary<string, string>())
            {
                if (string.IsNullOrWhiteSpace(header.Key) || string.IsNullOrWhiteSpace(header.Value)) continue;
                if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    if (request.Content != null) request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(header.Value);
                    continue;
                }
                if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Custom header cannot be set manually: " + header.Key);
                }
                request.Headers.Remove(header.Key);
                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
                {
                    throw new InvalidOperationException("Custom header is not valid for request headers: " + header.Key);
                }
            }
        }

        private static Encoding ResponseEncoding(HttpContent content)
        {
            var charset = content.Headers.ContentType == null ? null : content.Headers.ContentType.CharSet;
            if (!string.IsNullOrWhiteSpace(charset))
            {
                try { return Encoding.GetEncoding(charset.Trim('"')); }
                catch (ArgumentException) { }
            }
            return Encoding.UTF8;
        }

        private static LlmRequestException TooLarge()
        {
            return new LlmRequestException(LlmFailureKind.ResponseTooLarge, "LLM response exceeds the configured safety limit.");
        }

    }
}
