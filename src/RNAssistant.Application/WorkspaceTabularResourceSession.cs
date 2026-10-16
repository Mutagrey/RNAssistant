using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Storage;

namespace RNAssistant.Application
{
    public sealed class TabularResourceResponse : IDisposable
    {
        public int StatusCode { get; private set; }
        public string ContentType { get; private set; }
        public Stream Body { get; private set; }
        public IReadOnlyDictionary<string, string> Headers { get; private set; }
        internal TabularResourceResponse(int status, string type, Stream body, Dictionary<string, string> headers)
        { StatusCode = status; ContentType = type; Body = body; Headers = new ReadOnlyDictionary<string, string>(headers); }
        public void Dispose() { Body.Dispose(); }
    }

    // Resource scope for a dedicated execution realm, not an HTTP server or an
    // Office bridge. A host forwards requests here and owns final CloseAsync/drain.
    public sealed class WorkspaceTabularResourceSession : IDisposable
    {
        public const string NativeOrigin = "https://rnassistant.tabular.invalid";
        private readonly object _sync = new object();
        private readonly WorkspaceSourceReader _source;
        private readonly TabularRuntimeBundle _bundle;
        private readonly CancellationTokenSource _cancel;
        private readonly string _origin, _prefix;
        private bool _closed, _reading, _clientClosed;
        private long _sequence;
        private Task _closing;
        public string EntryUri { get { return _prefix + TabularRuntimeBundle.EntryPath; } }
        public bool IsClosed { get { lock (_sync) return _closed || _source.IsClosed; } }
        public string BundleSha256 { get { return _bundle.Sha256; } }
        public bool ClientCloseConfirmed
        { get { lock (_sync) return _clientClosed && _closing != null && _closing.Status == TaskStatus.RanToCompletion; } }
        public Task SourceDrained { get { return _source.Drained; } }
        public bool WasRevoked { get { lock (_sync) return !_clientClosed && _source.IsClosed; } }

        private sealed class Descriptor
        {
            [JsonProperty("version")] public int Version { get; set; }
            [JsonProperty("bundleSha256")] public string BundleSha256 { get; set; }
            [JsonProperty("workerProtocol")] public int WorkerProtocol { get; set; }
            [JsonProperty("engineRevision")] public string EngineRevision { get; set; }
            [JsonProperty("byteLength")] public long ByteLength { get; set; }
            [JsonProperty("label")] public string Label { get; set; }
        }

        private WorkspaceTabularResourceSession(WorkspaceSourceReader source, TabularRuntimeBundle bundle,
            string origin, CancellationTokenSource cancel)
        {
            _source = source; _bundle = bundle; _origin = origin; _cancel = cancel;
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            _prefix = origin + "/r/" + BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant() + "/";
        }

        public static async Task<WorkspaceTabularResourceSession> OpenAsync(WorkspaceApplicationService workspace,
            string queueId, string itemId, TabularRuntimeBundle bundle, CancellationToken token = default(CancellationToken),
            int deadlineMs = 120000, string origin = NativeOrigin, Action<FileTransferProgress> progress = null)
        {
            if (workspace == null || bundle == null) throw new ArgumentNullException();
            Uri uri;
            if (!Uri.TryCreate(origin, UriKind.Absolute, out uri) || uri.GetLeftPart(UriPartial.Authority) != origin ||
                !string.IsNullOrEmpty(uri.UserInfo) || origin != NativeOrigin &&
                !(uri.Scheme == "http" && uri.Host == "127.0.0.1" && !uri.IsDefaultPort))
                throw new ArgumentException("Use the dedicated virtual origin or an explicit loopback test host.", nameof(origin));
            if (deadlineMs < 1 || deadlineMs > 300000) throw new ArgumentOutOfRangeException(nameof(deadlineMs));
            var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
            cancel.CancelAfter(deadlineMs);
            WorkspaceSourceReader source = null;
            try
            {
                source = await workspace.OpenPreparedSourceAsync(queueId, itemId, cancel.Token, progress).ConfigureAwait(false);
                return new WorkspaceTabularResourceSession(source, bundle, origin, cancel);
            }
            catch
            {
                try { if (source != null) await source.CloseAsync().ConfigureAwait(false); }
                finally { cancel.Dispose(); }
                throw;
            }
        }

        public async Task<TabularResourceResponse> HandleAsync(string method, string url, string requestOrigin = null)
        {
            // Compare the raw canonical URL, not a normalized/decoded alias.
            if (url == null || url.Length > 1024 || !url.StartsWith(_prefix, StringComparison.Ordinal) ||
                requestOrigin != null && requestOrigin != _origin)
                return Response(404);
            var route = url.Substring(_prefix.Length);
            if (method == "DELETE" && route == "source")
            {
                // Expiry/native revocation cannot become a successful completion
                // receipt merely because the browser later requests cleanup.
                lock (_sync)
                {
                    if (!_closed && !_source.IsClosed && !_cancel.IsCancellationRequested) _clientClosed = true;
                }
                try { await CloseAsync().ConfigureAwait(false); return Response(_clientClosed ? 204 : 410); }
                catch { return Response(500); }
            }
            if (IsClosed) return Response(410);
            if (method != "GET") return Response(405);
            if (route == "descriptor.json")
            {
                var descriptor = new Descriptor { Version = 1, BundleSha256 = _bundle.Sha256,
                    WorkerProtocol = TabularRuntimeBundle.WorkerProtocol, EngineRevision = TabularRuntimeBundle.EngineRevision,
                    ByteLength = _source.ByteLength, Label = _source.Label };
                return Response(200, "application/json", new MemoryStream(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(descriptor)), false));
            }
            if (route.StartsWith("source?", StringComparison.Ordinal)) return await ReadRangeAsync(route).ConfigureAwait(false);
            var asset = _bundle.OpenAsset(route);
            return asset == null ? Response(404) : Response(200, route == TabularRuntimeBundle.EntryPath ? "text/html" : "text/javascript", asset,
                document: route == TabularRuntimeBundle.EntryPath);
        }

        private async Task<TabularResourceResponse> ReadRangeAsync(string route)
        {
            var parts = route.Substring(7).Split('&');
            long sequence, offset, count;
            if (parts.Length != 3 || !Number(parts[0], "sequence=", out sequence) ||
                !Number(parts[1], "offset=", out offset) || !Number(parts[2], "count=", out count) ||
                count < 1 || count > WorkspaceSourceReader.MaximumReadBytes || offset > _source.ByteLength - count)
                return Response(400);
            lock (_sync)
            {
                if (_closed || _source.IsClosed) return Response(410);
                if (_reading || sequence != _sequence + 1) return Response(409);
                _reading = true; _sequence = sequence;
            }
            byte[] bytes = null;
            try
            {
                bytes = await _source.ReadRangeAsync(offset, (int)count).ConfigureAwait(false);
                lock (_sync)
                {
                    if (_closed || _source.IsClosed) { Array.Clear(bytes, 0, bytes.Length); return Response(410); }
                    var response = Response(200, "application/octet-stream", new MemoryStream(bytes, false), sequence: sequence, offset: offset);
                    bytes = null;
                    return response;
                }
            }
            catch
            {
                try { await CloseAsync().ConfigureAwait(false); }
                catch { return Response(500); }
                return Response(410);
            }
            finally
            {
                if (bytes != null) Array.Clear(bytes, 0, bytes.Length);
                lock (_sync) _reading = false;
            }
        }

        private TabularResourceResponse Response(int status, string contentType = "application/octet-stream", Stream body = null,
            bool document = false, long sequence = 0, long offset = 0)
        {
            body = body ?? new MemoryStream(Array.Empty<byte>(), false);
            var headers = new Dictionary<string, string>(StringComparer.Ordinal) {
                ["Cache-Control"] = "no-store", ["X-Content-Type-Options"] = "nosniff",
                ["Content-Length"] = body.Length.ToString(CultureInfo.InvariantCulture),
                ["Cross-Origin-Resource-Policy"] = "same-origin", ["Referrer-Policy"] = "no-referrer",
                ["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; " +
                    (document ? "worker-src " + _prefix + "data-templates/core/worker.js; connect-src " + _prefix + "descriptor.json " + _prefix + "source; " : "worker-src 'none'; connect-src 'none'; ") +
                    "base-uri 'none'; form-action 'none'; frame-src 'none'; frame-ancestors 'none'; object-src 'none'"
            };
            if (sequence != 0)
            {
                headers.Add("X-RNA-Source-Sequence", sequence.ToString(CultureInfo.InvariantCulture));
                headers.Add("X-RNA-Source-Offset", offset.ToString(CultureInfo.InvariantCulture));
            }
            return new TabularResourceResponse(status, contentType, body, headers);
        }

        private static bool Number(string value, string prefix, out long number)
        {
            number = 0;
            return value.StartsWith(prefix, StringComparison.Ordinal) &&
                long.TryParse(value.Substring(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out number) &&
                number.ToString(CultureInfo.InvariantCulture) == value.Substring(prefix.Length);
        }

        public Task CloseAsync()
        {
            lock (_sync)
            {
                if (_closing != null) return _closing;
                _closed = true;
                _cancel.Cancel();
                return _closing = DrainAsync();
            }
        }
        private async Task DrainAsync()
        { try { await _source.CloseAsync().ConfigureAwait(false); } finally { _cancel.Dispose(); } }
        public void Dispose() { CloseAsync(); }
    }
}
