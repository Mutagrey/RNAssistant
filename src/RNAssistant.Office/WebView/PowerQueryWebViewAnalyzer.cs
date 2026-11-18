using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Office.WebView
{
    // A fixed, local language parser. This realm has no Shell bridge, Office
    // object, resource RPC or caller-authored JavaScript capability.
    internal sealed class PowerQueryWebViewAnalyzer : IPowerQuerySyntaxAnalyzer, IDisposable
    {
        private readonly object _sync = new object();
        private readonly LocalBrowserEnvironment _environment;
        private readonly IntPtr _parent;
        private readonly string _webRoot;
        private readonly SynchronizationContext _ui;
        private readonly bool _assetsPresent;
        private Run _active;
        private bool _closed;
        private Task _drained = Task.CompletedTask;

        internal PowerQueryWebViewAnalyzer(LocalBrowserEnvironment environment, IntPtr parent, string webRoot)
        {
            _environment = environment ?? throw new ArgumentNullException(nameof(environment));
            if (parent == IntPtr.Zero) throw new ArgumentException("An application window is required.", nameof(parent));
            _parent = parent;
            _webRoot = webRoot ?? throw new ArgumentNullException(nameof(webRoot));
            _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("A UI dispatcher is required.");
            _assetsPresent = Run.AssetsPresent(webRoot);
        }

        public bool IsAvailable
        { get { lock (_sync) return !_closed && _assetsPresent && !_drained.IsFaulted; } }

        internal Task Drained { get { lock (_sync) return _drained; } }

        public Task<PowerQuerySyntaxAnalysis> AnalyzeAsync(PowerQuerySyntaxRequest request, CancellationToken token)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            token.ThrowIfCancellationRequested();
            Run run;
            lock (_sync)
            {
                if (_closed) throw new ObjectDisposedException(nameof(PowerQueryWebViewAnalyzer));
                if (!_assetsPresent || _drained.IsFaulted)
                    throw new InvalidOperationException("The local Power Query parser is unavailable.");
                if (_active != null)
                    throw new InvalidOperationException("The local Power Query parser is still running or draining.");
                run = new Run(_environment, _parent, _webRoot, _ui, request, token);
                _active = run;
                _drained = run.Drained;
            }
            try
            {
                _ui.Post(async ignored => {
                    try { await run.ExecuteOnUi(); }
                    finally { lock (_sync) if (ReferenceEquals(_active, run)) _active = null; }
                }, null);
            }
            catch (Exception error)
            {
                run.FailBeforeStart(error);
                lock (_sync) if (ReferenceEquals(_active, run)) _active = null;
            }
            return run.Completion;
        }

        public void Dispose()
        {
            Run run;
            lock (_sync)
            {
                if (_closed) return;
                _closed = true;
                run = _active;
            }
            run?.Stop(new OperationCanceledException("The Power Query parser owner was closed."));
        }

        private sealed class Run
        {
            private const int DeadlineMilliseconds = 15000;
            private const int MaximumAssetBytes = 4 * 1024 * 1024;
            private const int MaximumTotalAssetBytes = 8 * 1024 * 1024;
            private const int MaximumRequests = 32;
            private const long MaximumServedBytes = 16 * 1024 * 1024;
            private const string Policy = "default-src 'none'; script-src 'self'; worker-src 'self'; connect-src 'none'; img-src 'none'; style-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
            private static readonly string[] AssetNames = { "index.html", "host.js", "worker.js", "analyze.js", "parser.bundle.js" };
            private static readonly JsonSerializerSettings MessageSettings = new JsonSerializerSettings {
                MissingMemberHandling = MissingMemberHandling.Error, MaxDepth = 16
            };
            private readonly LocalBrowserEnvironment _owner;
            private readonly IntPtr _parent;
            private readonly string _webRoot;
            private readonly SynchronizationContext _ui;
            private readonly PowerQuerySyntaxRequest _request;
            private readonly string _origin = "https://pq-" + Guid.NewGuid().ToString("N") + ".rnassistant.local";
            private readonly TaskCompletionSource<PowerQuerySyntaxAnalysis> _completion =
                new TaskCompletionSource<PowerQuerySyntaxAnalysis>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _drained =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly CancellationTokenSource _deadline = new CancellationTokenSource();
            private readonly CancellationTokenRegistration _cancel;
            private readonly CancellationTokenRegistration _expired;
            private readonly List<AssetBody> _bodies = new List<AssetBody>();
            private Dictionary<string, byte[]> _assets;
            private CoreWebView2Controller _controller;
            private LocalBrowserEnvironment.Lease _lease;
            private IDisposable _route;
            private Exception _closeFailure;
            private int _stopRequested, _requests;
            private long _servedBytes;
            private bool _ready;

            internal Run(LocalBrowserEnvironment owner, IntPtr parent, string webRoot, SynchronizationContext ui,
                PowerQuerySyntaxRequest request, CancellationToken token)
            {
                _owner = owner; _parent = parent; _webRoot = webRoot; _ui = ui; _request = request;
                _cancel = token.Register(() => Stop(new OperationCanceledException("Power Query analysis was cancelled.", token)));
                _expired = _deadline.Token.Register(() => Stop(new TimeoutException("Power Query analysis exceeded its deadline.")));
                _deadline.CancelAfter(DeadlineMilliseconds);
            }

            internal Task<PowerQuerySyntaxAnalysis> Completion { get { return _completion.Task; } }
            internal Task Drained { get { return _drained.Task; } }
            private string Entry { get { return _origin + "/index.html"; } }

            internal static bool AssetsPresent(string webRoot)
            {
                foreach (var name in AssetNames) if (!File.Exists(AssetPath(webRoot, name))) return false;
                return true;
            }

            private static string AssetPath(string webRoot, string name)
            {
                return name == "parser.bundle.js"
                    ? Path.Combine(webRoot, "js", "vendor", "powerquery-parser-5.0.0.js")
                    : Path.Combine(webRoot, "powerquery", name);
            }

            private Dictionary<string, byte[]> CaptureAssets()
            {
                var assets = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                var total = 0;
                foreach (var name in AssetNames)
                {
                    if (_completion.Task.IsCompleted) throw new OperationCanceledException();
                    using (var stream = new FileStream(AssetPath(_webRoot, name), FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (stream.Length == 0 || stream.Length > MaximumAssetBytes || total + stream.Length > MaximumTotalAssetBytes)
                            throw new InvalidDataException("A fixed Power Query parser asset exceeds its bound.");
                        var bytes = new byte[(int)stream.Length];
                        var offset = 0;
                        while (offset < bytes.Length)
                        {
                            var read = stream.Read(bytes, offset, bytes.Length - offset);
                            if (read == 0) throw new EndOfStreamException("A Power Query parser asset changed while reading.");
                            offset += read;
                        }
                        assets.Add("/" + name, bytes); total += bytes.Length;
                    }
                }
                return assets;
            }

            internal async Task ExecuteOnUi()
            {
                try
                {
                    if (_completion.Task.IsCompleted) return;
                    _assets = await Task.Run(() => CaptureAssets());
                    if (_completion.Task.IsCompleted) return;
                    _lease = await _owner.AcquireAsync();
                    if (_completion.Task.IsCompleted) return;
                    var environment = _lease.Environment;
                    var options = environment.CreateCoreWebView2ControllerOptions();
                    options.ProfileName = "RNAssistantLocal"; options.IsInPrivateModeEnabled = true;
                    _controller = await environment.CreateCoreWebView2ControllerAsync(_parent, options);
                    // Controller creation cannot be cancelled. A late controller
                    // is closed in finally before any navigation or source transfer.
                    if (_completion.Task.IsCompleted) return;
                    _controller.IsVisible = false; _controller.Bounds = new Rectangle(0, 0, 1, 1);
                    Configure(_controller.CoreWebView2);
                    _controller.CoreWebView2.Navigate(Entry);
                    await _completion.Task;
                }
                catch (OperationCanceledException) { _completion.TrySetCanceled(); }
                catch (Exception error) { _completion.TrySetException(error); }
                finally
                {
                    // A caller deadline releases its wait. Drained still owns a
                    // pending native creation and is not complete until cleanup.
                    CloseControllerOnUi();
                    foreach (var body in _bodies) body.Dispose();
                    _bodies.Clear(); _assets = null;
                    try { _lease?.Dispose(); } catch (Exception error) { _closeFailure = _closeFailure ?? error; }
                    _cancel.Dispose(); _expired.Dispose(); _deadline.Dispose();
                    if (_closeFailure == null) _drained.TrySetResult(true);
                    else _drained.TrySetException(_closeFailure);
                }
            }

            private void Configure(CoreWebView2 core)
            {
                _route = _owner.Route(core, _origin, args => OnResource(core, args));
                core.NavigationStarting += (sender, args) => {
                    if (args.Uri != Entry) { args.Cancel = true; Stop(new InvalidOperationException("Power Query parser navigation was refused.")); }
                };
                core.NavigationCompleted += (sender, args) => {
                    if (!args.IsSuccess && !_completion.Task.IsCompleted) Stop(new InvalidOperationException("Power Query parser did not load."));
                };
                core.ProcessFailed += (sender, args) => Stop(new InvalidOperationException("Power Query parser process failed."));
                core.FrameNavigationStarting += (sender, args) => args.Cancel = true;
                core.NewWindowRequested += (sender, args) => args.Handled = true;
                core.PermissionRequested += (sender, args) => { args.State = CoreWebView2PermissionState.Deny; args.Handled = true; };
                core.DownloadStarting += (sender, args) => { args.Cancel = true; args.Handled = true; };
                core.WebMessageReceived += (sender, args) => {
                    if (_completion.Task.IsCompleted || args.Source != Entry) return;
                    try
                    {
                        var json = args.TryGetWebMessageAsString();
                        if (json == null || json.Length > PowerQuerySyntaxAnalysis.MaximumResultCharacters)
                            throw new InvalidDataException("Power Query parser message exceeds its bound.");
                        var message = JsonConvert.DeserializeObject<WorkerMessage>(json, MessageSettings);
                        if (message == null) throw new InvalidDataException("Power Query parser returned no message.");
                        switch (message.Type)
                        {
                            case WorkerMessageKind.Ready:
                                if (_ready || message.Result != null || message.Message != null)
                                    throw new InvalidDataException("Power Query parser repeated its handshake.");
                                _ready = true;
                                core.PostWebMessageAsJson(JsonConvert.SerializeObject(new StartMessage { Request = _request }));
                                break;
                            case WorkerMessageKind.Complete:
                                if (!_ready || message.Result == null || message.Message != null)
                                    throw new InvalidDataException("Power Query parser result arrived outside its request.");
                                message.Result.RequireMatches(_request);
                                _completion.TrySetResult(message.Result);
                                break;
                            case WorkerMessageKind.Failed:
                                if (message.Result != null || message.Message == null || message.Message.Length > 2048)
                                    throw new InvalidDataException("Power Query parser failure was malformed.");
                                Stop(new InvalidOperationException("Power Query parser did not complete its analysis."));
                                break;
                            default: throw new InvalidDataException("Unknown Power Query parser message.");
                        }
                    }
                    catch (Exception error) { Stop(error); }
                };
                core.Settings.IsWebMessageEnabled = true;
                core.Settings.AreHostObjectsAllowed = false;
                core.Settings.AreDefaultScriptDialogsEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsPasswordAutosaveEnabled = false;
                core.Settings.IsGeneralAutofillEnabled = false;
            }

            private void OnResource(CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs args)
            {
                try
                {
                    byte[] bytes; Uri uri;
                    if (++_requests > MaximumRequests) throw new InvalidDataException("Power Query parser exceeded its asset request limit.");
                    if (_completion.Task.IsCompleted || _assets == null || args.Request.Method != "GET" ||
                        !Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out uri) ||
                        uri.GetLeftPart(UriPartial.Authority) != _origin || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                        !_assets.TryGetValue(uri.AbsolutePath, out bytes))
                    {
                        args.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(), 403,
                            "Fixed parser assets only", Headers("text/plain; charset=utf-8"));
                        return;
                    }
                    _servedBytes += bytes.Length;
                    if (_servedBytes > MaximumServedBytes) throw new InvalidDataException("Power Query parser exceeded its transfer limit.");
                    var body = new AssetBody(bytes); _bodies.Add(body);
                    args.Response = core.Environment.CreateWebResourceResponse(body, 200, "OK",
                        Headers(uri.AbsolutePath == "/index.html" ? "text/html; charset=utf-8" : "application/javascript; charset=utf-8"));
                }
                catch (Exception error)
                {
                    args.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(), 403,
                        "Parser request refused", Headers("text/plain; charset=utf-8"));
                    Stop(error);
                }
            }

            private static string Headers(string mime)
            {
                return "Content-Type: " + mime + "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n" +
                    "Referrer-Policy: no-referrer\r\nCross-Origin-Resource-Policy: same-origin\r\nContent-Security-Policy: " + Policy + "\r\n";
            }

            internal void Stop(Exception error)
            {
                if (error is OperationCanceledException) _completion.TrySetCanceled();
                else _completion.TrySetException(error);
                if (Interlocked.Exchange(ref _stopRequested, 1) != 0) return;
                try { _ui.Post(ignored => CloseControllerOnUi(), null); }
                catch (Exception) { /* The creation/cleanup owner still retains Drained. */ }
            }

            private void CloseControllerOnUi()
            {
                var route = _route;
                _route = null;
                try { route?.Dispose(); }
                catch (Exception error) { _closeFailure = _closeFailure ?? error; }
                var controller = _controller;
                if (controller == null) return;
                try { controller.Close(); _controller = null; }
                catch (Exception error) { _closeFailure = _closeFailure ?? error; }
            }

            internal void FailBeforeStart(Exception error)
            {
                _completion.TrySetException(error);
                _cancel.Dispose(); _expired.Dispose(); _deadline.Dispose();
                _drained.TrySetResult(true);
            }
        }

        // WebView2 may consume response streams on another thread. Closing the
        // realm revokes them; a late reader gets EOF instead of a dispose race.
        private sealed class AssetBody : Stream
        {
            private readonly object _sync = new object();
            private readonly int _length;
            private byte[] _bytes;
            private int _position;
            internal AssetBody(byte[] bytes) { _bytes = bytes; _length = bytes.Length; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (buffer == null) throw new ArgumentNullException(nameof(buffer));
                if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException();
                lock (_sync)
                {
                    if (_bytes == null) return 0;
                    var available = Math.Min(count, _length - _position);
                    Buffer.BlockCopy(_bytes, _position, buffer, offset, available);
                    _position += available;
                    if (_position == _length) _bytes = null;
                    return available;
                }
            }
            protected override void Dispose(bool disposing) { lock (_sync) _bytes = null; base.Dispose(disposing); }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return _length; } }
            public override long Position { get { lock (_sync) return _position; } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }

        [JsonConverter(typeof(WorkerMessageKindConverter))]
        private enum WorkerMessageKind { Ready, Complete, Failed }
        private sealed class WorkerMessageKindConverter : StringEnumConverter
        { public WorkerMessageKindConverter() { AllowIntegerValues = false; } }
        private sealed class WorkerMessage
        {
            [JsonProperty("type", Required = Required.Always)] public WorkerMessageKind Type { get; set; }
            [JsonProperty("result")] public PowerQuerySyntaxAnalysis Result { get; set; }
            [JsonProperty("message")] public string Message { get; set; }
        }
        private sealed class StartMessage
        {
            [JsonProperty("type")] public string Type { get { return "analyze"; } }
            [JsonProperty("request")] public PowerQuerySyntaxRequest Request { get; set; }
        }
    }
}
