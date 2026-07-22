using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    internal sealed class HtmlNetworkService
    {
        private const int MaxRequestBytes = 256 * 1024;
        private const int MaxResponseBytes = 1024 * 1024;
        private const int MaxRedirects = 3;
        private readonly Func<AppSettings> _loadSettings;
        private readonly Action<AppSettings> _saveSettings;

        public HtmlNetworkService(Func<AppSettings> loadSettings, Action<AppSettings> saveSettings)
        {
            _loadSettings = loadSettings;
            _saveSettings = saveSettings;
        }

        public string AllowOrigin(string value)
        {
            var origin = NormalizeOrigin(value);
            var settings = _loadSettings();
            if (!settings.HtmlNetworkAllowedOrigins.Any(item => string.Equals(item, origin, StringComparison.OrdinalIgnoreCase)))
            {
                settings.HtmlNetworkAllowedOrigins.Add(origin);
                _saveSettings(settings);
            }
            return origin;
        }

        public async Task<HtmlFetchResponse> FetchAsync(HtmlFetchRequest request, CancellationToken cancellationToken)
        {
            request = request ?? new HtmlFetchRequest();
            var uri = ParseHttpUri(request.Url);
            EnsureAllowed(uri);
            var method = NormalizeMethod(request.Method);
            var body = request.Body ?? string.Empty;
            if (Encoding.UTF8.GetByteCount(body) > MaxRequestBytes)
            {
                throw new InvalidOperationException("HTML request body exceeds 256 KB.");
            }

            using (var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false })
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) })
            {
                for (var redirect = 0; redirect <= MaxRedirects; redirect++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using (var message = new HttpRequestMessage(new HttpMethod(method), uri))
                    {
                        if (body.Length > 0)
                        {
                            message.Content = new StringContent(body, Encoding.UTF8, "text/plain");
                        }
                        AddHeaders(message, request.Headers);
                        using (var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                        {
                            if (IsRedirect(response) && response.Headers.Location != null)
                            {
                                if (redirect == MaxRedirects) throw new InvalidOperationException("HTML request redirect limit exceeded.");
                                uri = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(uri, response.Headers.Location);
                                uri = ParseHttpUri(uri.AbsoluteUri);
                                EnsureAllowed(uri);
                                var status = (int)response.StatusCode;
                                if (status == 303 || ((status == 301 || status == 302) && string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)))
                                {
                                    method = "GET";
                                    body = string.Empty;
                                }
                                continue;
                            }
                            var bytes = await ReadLimitedAsync(response.Content, cancellationToken).ConfigureAwait(false);
                            return new HtmlFetchResponse
                            {
                                Url = uri.AbsoluteUri,
                                Status = (int)response.StatusCode,
                                StatusText = response.ReasonPhrase ?? string.Empty,
                                Headers = ResponseHeaders(response),
                                Body = Decode(bytes, response.Content.Headers.ContentType == null ? null : response.Content.Headers.ContentType.CharSet)
                            };
                        }
                    }
                }
            }
            throw new InvalidOperationException("HTML request failed.");
        }

        private void EnsureAllowed(Uri uri)
        {
            var origin = NormalizeOrigin(uri.GetLeftPart(UriPartial.Authority));
            var allowed = _loadSettings().HtmlNetworkAllowedOrigins;
            if (!allowed.Any(item => OriginMatches(item, origin)))
            {
                throw new UnauthorizedAccessException("HTML network origin is not allowed: " + origin);
            }
        }

        private static Uri ParseHttpUri(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("Only absolute HTTP(S) URLs are supported.");
            }
            if (!string.IsNullOrEmpty(uri.UserInfo)) throw new InvalidOperationException("Credentials in HTML URLs are not allowed.");
            return uri;
        }

        private static string NormalizeOrigin(string value)
        {
            var uri = ParseHttpUri(value);
            return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        }

        private static bool OriginMatches(string configured, string origin)
        {
            try { return string.Equals(NormalizeOrigin(configured), origin, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private static string NormalizeMethod(string value)
        {
            var method = string.IsNullOrWhiteSpace(value) ? "GET" : value.Trim().ToUpperInvariant();
            var allowed = new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };
            if (!allowed.Contains(method)) throw new InvalidOperationException("HTTP method is not allowed: " + method);
            return method;
        }

        private static void AddHeaders(HttpRequestMessage message, IDictionary<string, string> headers)
        {
            var blocked = new[] { "authorization", "cookie", "proxy-authorization", "host", "content-length", "connection" };
            foreach (var pair in headers ?? new Dictionary<string, string>())
            {
                var name = (pair.Key ?? string.Empty).Trim();
                if (name.Length == 0 || blocked.Contains(name.ToLowerInvariant()))
                    throw new InvalidOperationException("HTML request header is not allowed: " + name);
                if (!message.Headers.TryAddWithoutValidation(name, pair.Value ?? string.Empty))
                {
                    if (message.Content == null) message.Content = new StringContent(string.Empty);
                    message.Content.Headers.Remove(name);
                    if (!message.Content.Headers.TryAddWithoutValidation(name, pair.Value ?? string.Empty))
                        throw new InvalidOperationException("Invalid HTML request header: " + name);
                }
            }
        }

        private static bool IsRedirect(System.Net.Http.HttpResponseMessage response)
        {
            var status = (int)response.StatusCode;
            return status == 301 || status == 302 || status == 303 || status == 307 || status == 308;
        }

        private static async Task<byte[]> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
        {
            if (content.Headers.ContentLength.GetValueOrDefault() > MaxResponseBytes)
                throw new InvalidOperationException("HTML response exceeds 1 MB.");
            using (var input = await content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var output = new MemoryStream())
            {
                var buffer = new byte[8192];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                    if (read <= 0) break;
                    if (output.Length + read > MaxResponseBytes) throw new InvalidOperationException("HTML response exceeds 1 MB.");
                    output.Write(buffer, 0, read);
                }
                return output.ToArray();
            }
        }

        private static Dictionary<string, string> ResponseHeaders(System.Net.Http.HttpResponseMessage response)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in response.Headers) result[header.Key] = string.Join(", ", header.Value);
            foreach (var header in response.Content.Headers) result[header.Key] = string.Join(", ", header.Value);
            return result;
        }

        private static string Decode(byte[] bytes, string charset)
        {
            try { return Encoding.GetEncoding(string.IsNullOrWhiteSpace(charset) ? "utf-8" : charset.Trim('"')).GetString(bytes); }
            catch { return Encoding.UTF8.GetString(bytes); }
        }
    }
}
