using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Application;

namespace RNAssistant.Office.WebView
{
    // Untrusted JS runs only in a dedicated Web Worker behind a hidden, unprivileged
    // WebView2 controller. It never has the Shell WebView, Office bridge, or browser profile.
    // A cleanup failure poisons the executor until the host process restarts.
    internal static class JsWebViewExecutor
    {
        private const string DocumentUri = "https://js-executor.rnassistant.invalid/index.html";
        private const string WorkerUri = "https://js-executor.rnassistant.invalid/worker.js";
        private const int MaximumResultBytes = 1024 * 1024;
        private const int MaximumMessageCharacters = 2 * 1024 * 1024;
        private static readonly SemaphoreSlim Admission = new SemaphoreSlim(1, 1);
        private static readonly byte[] DocumentBytes = Embedded("RNAssistant.Office.WebView.JsExecutorHost.html");
        private static readonly byte[] WorkerBytes = Embedded("RNAssistant.Office.WebView.JsExecutorWorker.js");
        private static int _unavailable;

        internal static async Task<string> ExecuteAsync(string code, JObject args, string[] names,
            Func<JObject, CancellationToken, JObject> resourceRequest, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(code) || Encoding.UTF8.GetByteCount(code) > MaximumResultBytes)
                throw new InvalidDataException("JS source must contain 1 byte to 1 MiB of UTF-8.");
            if (resourceRequest == null) throw new ArgumentNullException(nameof(resourceRequest));
            await Admission.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _unavailable) != 0)
                    throw new InvalidOperationException("JS WebView2 executor cleanup was not confirmed; restart the host.");
                try
                {
                    return await new Invocation(code, args ?? new JObject(), names ?? new string[0],
                        resourceRequest).ExecuteAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (CleanupUnconfirmedException)
                {
                    Volatile.Write(ref _unavailable, 1);
                    throw;
                }
            }
            finally { Admission.Release(); }
        }

        private static byte[] Embedded(string name)
        {
            using (var stream = typeof(JsWebViewExecutor).Assembly.GetManifestResourceStream(name))
            {
                if (stream == null) throw new FileNotFoundException("Missing embedded JS execution asset: " + name);
                using (var output = new MemoryStream()) { stream.CopyTo(output); return output.ToArray(); }
            }
        }

        private static string ResponseHeaders(string mime)
        {
            // More restrictive than interactive Browser policy. No active network
            // capability is required: the sole worker asset is served in memory.
            return "Content-Type: " + mime + "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n" +
                "Referrer-Policy: no-referrer\r\nX-DNS-Prefetch-Control: off\r\n" +
                "Connection-Allowlist: " + BrowserNetworkPolicy.ConnectionAllowlist + "\r\n" +
                "Content-Security-Policy: default-src 'none'; script-src 'self' 'unsafe-inline' 'unsafe-eval'; " +
                "worker-src 'self'; connect-src 'none'; img-src 'none'; frame-src 'none'; object-src 'none'; " +
                "base-uri 'none'; form-action 'none'\r\n";
        }

        private sealed class CleanupUnconfirmedException : InvalidOperationException
        {
            internal CleanupUnconfirmedException() : base("JS WebView2 shutdown or resource requests did not drain. The executor is disabled until restart.") { }
        }

        private sealed class Invocation
        {
            private readonly string _code;
            private readonly JObject _args;
            private readonly string[] _names;
            private readonly Func<JObject, CancellationToken, JObject> _resourceRequest;
            private readonly string _profile = Path.Combine(Path.GetTempPath(), "RNAssistant", "JsExecution", Guid.NewGuid().ToString("N"));
            private readonly TaskCompletionSource<string> _result = Signal<string>();
            private readonly TaskCompletionSource<bool> _threadDrained = Signal<bool>();
            private readonly object _jobsLock = new object(), _requestLock = new object();
            private readonly List<Task> _jobs = new List<Task>();
            private CancellationTokenSource _cancellation;
            private CancellationToken _callerToken;
            private volatile SynchronizationContext _ui;
            private ApplicationContext _app;
            private Form _form;
            private CoreWebView2Environment _environment;
            private CoreWebView2Controller _controller;
            private bool _initializing = true, _stopping, _navigated, _started, _browserCreated;
            private int _stopRequested, _pendingRequests;

            internal Invocation(string code, JObject args, string[] names,
                Func<JObject, CancellationToken, JObject> resourceRequest)
            { _code = code; _args = args; _names = names; _resourceRequest = resourceRequest; }

            internal async Task<string> ExecuteAsync(CancellationToken token)
            {
                _callerToken = token;
                using (_cancellation = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    _cancellation.CancelAfter(TimeSpan.FromSeconds(15));
                    using (_cancellation.Token.Register(OnCancelled))
                    {
                        var thread = new Thread(ThreadMain) { IsBackground = true, Name = "RNAssistant JS WebView2" };
                        thread.SetApartmentState(ApartmentState.STA);
                        thread.Start();
                        try { return await _result.Task.ConfigureAwait(false); }
                        finally
                        {
                            RequestStop();
                            if (await Task.WhenAny(_threadDrained.Task, Task.Delay(5000)).ConfigureAwait(false) != _threadDrained.Task)
                                throw new CleanupUnconfirmedException();
                            await _threadDrained.Task.ConfigureAwait(false);
                            Task[] jobs;
                            lock (_jobsLock) jobs = _jobs.ToArray();
                            var drained = Task.WhenAll(jobs);
                            if (await Task.WhenAny(drained, Task.Delay(5000)).ConfigureAwait(false) != drained)
                                throw new CleanupUnconfirmedException();
                            await drained.ConfigureAwait(false);
                        }
                    }
                }
            }

            private static TaskCompletionSource<T> Signal<T>()
            { return new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously); }

            private void OnCancelled()
            {
                if (_callerToken.IsCancellationRequested)
                    _result.TrySetCanceled();
                else
                    _result.TrySetException(new TimeoutException("JS WebView2 deadline exceeded (15 seconds)."));
                RequestStop();
            }

            private void RequestStop()
            {
                if (Interlocked.Exchange(ref _stopRequested, 1) != 0) return;
                var ui = _ui;
                if (ui != null)
                {
                    try { ui.Post(ignored => CloseOnUi(), null); }
                    catch (InvalidAsynchronousStateException) { }
                    catch (ObjectDisposedException) { }
                }
            }

            private void ThreadMain()
            {
                try
                {
                    using (var form = new Form { ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None })
                    {
                        _form = form;
                        var handle = form.Handle;
                        _ui = new WindowsFormsSynchronizationContext();
                        SynchronizationContext.SetSynchronizationContext(_ui);
                        _app = new ApplicationContext();
                        if (Volatile.Read(ref _stopRequested) == 0)
                        {
                            form.BeginInvoke(new Action(InitializeOnUi));
                            System.Windows.Forms.Application.Run(_app);
                        }
                    }
                }
                catch (Exception error) { _result.TrySetException(error); }
                finally
                {
                    // Emergency STA-side close if the WinForms message pump itself
                    // failed before the normal controller shutdown callback ran.
                    var abandoned = _controller;
                    _controller = null;
                    if (abandoned != null)
                    {
                        try { abandoned.Close(); }
                        catch (Exception error)
                        {
                            Volatile.Write(ref _unavailable, 1);
                            _result.TrySetException(error);
                        }
                    }
                    // Profile removal on normal browser exit is handled separately.
                    if (!_browserCreated) TryDeleteProfile(_profile);
                    _threadDrained.TrySetResult(true);
                }
            }

            private async void InitializeOnUi()
            {
                try
                {
                    if (_stopping || Volatile.Read(ref _stopRequested) != 0) return;
                    Directory.CreateDirectory(_profile);
                    var environmentOptions = new CoreWebView2EnvironmentOptions {
                        AdditionalBrowserArguments = BrowserNetworkPolicy.BrowserArguments };
                    var fixedRuntime = FindFixedRuntimeFolder();
                    Exception firstError = null;
                    // The Shell can use an offline fixed Runtime. Preserve that
                    // deployment capability without sharing the Shell profile.
                    foreach (var candidate in string.IsNullOrEmpty(fixedRuntime)
                        ? new string[] { null } : new[] { fixedRuntime, null })
                    {
                        var profile = Path.Combine(_profile, candidate == null ? "installed" : "fixed");
                        try
                        {
                            Directory.CreateDirectory(profile);
                            var environment = await CoreWebView2Environment.CreateAsync(candidate, profile, environmentOptions);
                            environment.BrowserProcessExited += (sender, args) => Task.Run(() => {
                                TryDeleteProfile(profile);
                                TryDeleteEmptyFolder(_profile);
                            });
                            BrowserNetworkPolicy.RequireSupportedRuntime(environment.BrowserVersionString);
                            _environment = environment;
                            break;
                        }
                        catch (Exception error)
                        {
                            if (firstError == null) firstError = error;
                        }
                    }
                    if (_environment == null)
                        throw new InvalidOperationException("No supported WebView2 Runtime is available for JS execution.", firstError);
                    if (_stopping || Volatile.Read(ref _stopRequested) != 0) return;
                    var controllerOptions = _environment.CreateCoreWebView2ControllerOptions();
                    controllerOptions.ProfileName = "RNAssistantJs";
                    controllerOptions.IsInPrivateModeEnabled = true;
                    // Creating a controller has no cancellation overload. Close it
                    // even if cancellation arrived while the await was in flight.
                    _controller = await _environment.CreateCoreWebView2ControllerAsync(_form.Handle, controllerOptions);
                    _browserCreated = true;
                    if (_stopping || Volatile.Read(ref _stopRequested) != 0) return;
                    _controller.IsVisible = false;
                    _controller.Bounds = new Rectangle(0, 0, 1, 1);
                    var core = _controller.CoreWebView2;
                    core.Settings.AreHostObjectsAllowed = false;
                    core.Settings.IsWebMessageEnabled = true;
                    core.Settings.IsScriptEnabled = true;
                    core.Settings.AreDevToolsEnabled = false;
                    core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                    core.Settings.AreDefaultContextMenusEnabled = false;
                    core.Settings.AreDefaultScriptDialogsEnabled = false;
                    core.Settings.IsPasswordAutosaveEnabled = false;
                    core.Settings.IsGeneralAutofillEnabled = false;
                    core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All,
                        CoreWebView2WebResourceRequestSourceKinds.All);
                    core.WebResourceRequested += OnResource;
                    core.WebMessageReceived += OnMessage;
                    core.NavigationStarting += OnNavigation;
                    core.NavigationCompleted += OnNavigationCompleted;
                    core.FrameNavigationStarting += OnFrame;
                    core.NewWindowRequested += OnWindow;
                    core.PermissionRequested += OnPermission;
                    core.DownloadStarting += OnDownload;
                    core.ProcessFailed += OnProcessFailed;
                    core.ScriptDialogOpening += OnDialog;
                    core.Navigate(DocumentUri);
                }
                catch (Exception error) { Fail(error); }
                finally
                {
                    _initializing = false;
                    if (_stopping || Volatile.Read(ref _stopRequested) != 0) CloseOnUi();
                }
            }

            private void OnResource(object sender, CoreWebView2WebResourceRequestedEventArgs e)
            {
                try
                {
                    byte[] body;
                    string mime;
                    var validGet = string.Equals(e.Request.Method, "GET", StringComparison.OrdinalIgnoreCase);
                    if (validGet && string.Equals(e.Request.Uri, DocumentUri, StringComparison.Ordinal))
                    { body = DocumentBytes; mime = "text/html; charset=utf-8"; }
                    else if (validGet && string.Equals(e.Request.Uri, WorkerUri, StringComparison.Ordinal))
                    { body = WorkerBytes; mime = "text/javascript; charset=utf-8"; }
                    else
                    {
                        // Block every nonlocal URL, redirects, imports, fetch and
                        // any attempt to call a local data-plane route directly.
                        e.Response = _environment.CreateWebResourceResponse(new MemoryStream(), 403,
                            "JS executor resource denied", ResponseHeaders("text/plain; charset=utf-8"));
                        return;
                    }
                    e.Response = _environment.CreateWebResourceResponse(new MemoryStream(body, false), 200,
                        "JS executor resource", ResponseHeaders(mime));
                }
                catch (Exception error) { Fail(error); }
            }

            private void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
            {
                if (_stopping) return;
                try
                {
                    if (!string.Equals(e.Source, DocumentUri, StringComparison.Ordinal))
                        throw new InvalidDataException("Foreign JS execution message.");
                    var json = e.TryGetWebMessageAsString();
                    if (json == null || json.Length > MaximumMessageCharacters)
                        throw new InvalidDataException("JS execution message exceeded its bound.");
                    var message = JObject.Parse(json);
                    var type = (string)message["type"];
                    if (type == "ready")
                    {
                        if (_started) throw new InvalidDataException("Duplicate JS executor ready message.");
                        _started = true;
                        Send(new JObject { ["type"] = "start", ["code"] = _code, ["args"] = _args,
                            ["names"] = new JArray(_names) });
                    }
                    else if (type == "request" && _started) ProcessResourceRequest(message);
                    else if (type == "result" && _started)
                    {
                        var result = (string)message["json"];
                        if (result == null || Encoding.UTF8.GetByteCount(result) > MaximumResultBytes)
                            throw new InvalidDataException("JS result exceeds 1 MiB.");
                        JToken.Parse(result);
                        _result.TrySetResult(result);
                        RequestStop();
                    }
                    else if (type == "error") throw new InvalidOperationException(
                        ((string)message["message"] ?? "JS failed.").Substring(0,
                            Math.Min(((string)message["message"] ?? "JS failed.").Length, 4096)));
                    else throw new InvalidDataException("Unexpected JS executor message.");
                }
                catch (Exception error) { Fail(error); }
            }

            private void ProcessResourceRequest(JObject message)
            {
                var seq = (int?)message["seq"] ?? -1;
                var body = message["body"] as JObject;
                if (seq < 1 || body == null || body.ToString(Formatting.None).Length > 65536)
                    throw new InvalidDataException("Invalid JS resource request.");
                if (Interlocked.Increment(ref _pendingRequests) > 16)
                {
                    Interlocked.Decrement(ref _pendingRequests);
                    throw new InvalidDataException("Too many pending JS resource requests.");
                }
                var job = Task.Run(() =>
                {
                    JObject response;
                    try
                    {
                        _cancellation.Token.ThrowIfCancellationRequested();
                        // Existing worker pipe serialized requests. Preserve that
                        // order across the Web Worker RPC boundary.
                        lock (_requestLock) response = _resourceRequest(body, _cancellation.Token);
                    }
                    catch (Exception error)
                    {
                        response = new JObject { ["ok"] = false,
                            ["error"] = (error.Message ?? "Resource operation failed.").Substring(0,
                                Math.Min((error.Message ?? "Resource operation failed.").Length, 4096)) };
                    }
                    finally { Interlocked.Decrement(ref _pendingRequests); }
                    var ui = _ui;
                    if (ui != null)
                    {
                        try
                        {
                            ui.Post(ignored => {
                                if (!_stopping && Volatile.Read(ref _stopRequested) == 0)
                                {
                                    try { Send(new JObject { ["type"] = "reply", ["seq"] = seq, ["response"] = response }); }
                                    catch (Exception error) { Fail(error); }
                                }
                            }, null);
                        }
                        catch (InvalidAsynchronousStateException) { }
                        catch (ObjectDisposedException) { }
                    }
                });
                lock (_jobsLock) _jobs.Add(job);
            }

            private void Send(JObject message)
            { _controller.CoreWebView2.PostWebMessageAsJson(message.ToString(Formatting.None)); }
            private void OnNavigation(object sender, CoreWebView2NavigationStartingEventArgs e)
            {
                if (!_stopping && !_navigated && !e.IsRedirected && string.Equals(e.Uri, DocumentUri, StringComparison.Ordinal))
                { _navigated = true; return; }
                e.Cancel = true;
                if (!_stopping) Fail(new InvalidDataException("JS executor navigation was denied."));
            }
            private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
            { if (!e.IsSuccess && !_stopping) Fail(new InvalidDataException("JS executor document failed to load.")); }
            private void OnFrame(object sender, CoreWebView2NavigationStartingEventArgs e)
            { e.Cancel = true; Fail(new InvalidDataException("JS executor frames are forbidden.")); }
            private void OnWindow(object sender, CoreWebView2NewWindowRequestedEventArgs e)
            { e.Handled = true; Fail(new InvalidDataException("JS executor windows are forbidden.")); }
            private void OnPermission(object sender, CoreWebView2PermissionRequestedEventArgs e)
            { e.State = CoreWebView2PermissionState.Deny; e.Handled = true; Fail(new InvalidDataException("JS executor permissions are forbidden.")); }
            private void OnDownload(object sender, CoreWebView2DownloadStartingEventArgs e)
            { e.Cancel = true; e.Handled = true; Fail(new InvalidDataException("JS executor downloads are forbidden.")); }
            private void OnProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
            { Fail(new InvalidOperationException("JS WebView2 process failed or became unresponsive.")); }
            private void OnDialog(object sender, CoreWebView2ScriptDialogOpeningEventArgs e)
            { Fail(new InvalidDataException("JS executor dialogs are forbidden.")); }

            private void Fail(Exception error)
            {
                if (_stopping) return;
                _result.TrySetException(error);
                RequestStop();
            }

            private void CloseOnUi()
            {
                if (_stopping && _controller == null && _initializing) return;
                _stopping = true;
                if (_controller != null)
                {
                    var controller = _controller;
                    _controller = null;
                    try { controller.Close(); }
                    catch (Exception error)
                    {
                        Volatile.Write(ref _unavailable, 1);
                        _result.TrySetException(error);
                    }
                }
                if (!_initializing) _app?.ExitThread();
            }

            private static string FindFixedRuntimeFolder()
            {
                var assemblyFolder = Path.GetDirectoryName(typeof(JsWebViewExecutor).Assembly.Location);
                var bases = new List<string> { AppDomain.CurrentDomain.BaseDirectory, assemblyFolder };
                foreach (var start in bases.ToArray())
                {
                    var parent = Directory.GetParent(start);
                    if (parent != null) bases.Add(parent.FullName);
                }
                foreach (var folder in bases.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var root = Path.Combine(folder, "vendor", "webview2-runtime");
                    if (!Directory.Exists(root)) continue;
                    if (File.Exists(Path.Combine(root, "msedgewebview2.exe"))) return root;
                    foreach (var child in Directory.GetDirectories(root))
                        if (File.Exists(Path.Combine(child, "msedgewebview2.exe"))) return child;
                }
                return null;
            }

            private static void TryDeleteEmptyFolder(string folder)
            {
                try { if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }

            private static void TryDeleteProfile(string folder)
            {
                try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
