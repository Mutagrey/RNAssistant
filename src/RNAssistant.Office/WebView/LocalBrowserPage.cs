using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.WebView
{
    internal sealed partial class LocalBrowserPage : IDisposable
    {
        private readonly LocalBrowserEnvironment _environment;
        private readonly PluginApplicationService _plugins;
        private readonly string _assets;
        private readonly Func<BrowserTargetDescriptor, CancellationToken, BrowserContentSession> _open;
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        private readonly HashSet<Stream> _streams = new HashSet<Stream>();
        private readonly TaskCompletionSource<bool> _loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _drained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private PluginWebViewHost.Transport _pluginTransport;
        private IDisposable _route;
        private LocalBrowserEnvironment.Lease _environmentLease;
        private readonly object _streamSync = new object();
        private int _pending;
        private bool _closed;
        private ulong _navigation;
        private int _rpcPending;
        internal bool HasLoaded { get; private set; }
        internal Panel Control { get; }
        internal CoreWebView2Controller Controller { get; private set; }
        internal CoreWebView2 Core => Controller.CoreWebView2;
        internal BrowserContentSession Content { get; private set; }
        internal BrowserPageStateDto State { get; set; }
        internal BrowserBoundsDto Bounds { get; set; }
        internal bool Ready => HasLoaded && !_closed && Controller != null && State.Status == BrowserPageStatus.Ready && State.Attached;
        internal BrowserPageHandle Handle => new BrowserPageHandle(State.OwnerScope.ChatId, State.SessionId, State.DocumentGeneration,
            State.Title + " · " + State.Target.RelativePath, true,
            State.BrowserTabId, State.OwnerScope, State.Target.ContentRevision, State.Target.SavedVersionId);
        internal Task Drained { get; private set; } = Task.CompletedTask;
        internal event Action<BrowserPageStateDto> Changed;
        internal LocalBrowserPage(Control parent, LocalBrowserEnvironment environment, PluginApplicationService plugins, string assets,
            BrowserPageStateDto state, Func<BrowserTargetDescriptor, CancellationToken, BrowserContentSession> open,
            Func<BrowserContentSession, BrowserResourceBinding> bindings, Func<string, string, Task<Action>> pauseRun)
        {
            _bindings = bindings; _pauseRun = pauseRun; _environment = environment; _plugins = plugins; _assets = assets; State = state; _open = open;
            Control = new Panel { Visible = false, TabStop = true }; parent.Controls.Add(Control);
        }
        internal async Task InitializeAsync(CancellationToken token)
        {
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _cancel.Token)) {
                var content = await Task.Run(() => _open(State.Target, linked.Token), linked.Token);
                if (_closed || linked.IsCancellationRequested) { content.Dispose(); await content.Drained; linked.Token.ThrowIfCancellationRequested(); }
                Content = content;
                var lease = await _environment.AcquireAsync();
                if (_closed || linked.IsCancellationRequested) { lease.Dispose(); linked.Token.ThrowIfCancellationRequested(); }
                _environmentLease = lease; var environment = lease.Environment;
                var options = environment.CreateCoreWebView2ControllerOptions(); options.ProfileName = "RNAssistantLocal"; options.IsInPrivateModeEnabled = true;
                var controller = await environment.CreateCoreWebView2ControllerAsync(Control.Handle, options);
                if (_closed || linked.IsCancellationRequested) { controller.Close(); linked.Token.ThrowIfCancellationRequested(); }
                Controller = controller;
                Controller.MoveFocusRequested += (sender, args) => { if (AgentOwnsInput) args.Handled = true; };
                Controller.IsVisible = true;
                Controller.GotFocus += (sender, args) => NativeFocused = true; Controller.LostFocus += (sender, args) => NativeFocused = false;
                Core.Settings.AreHostObjectsAllowed = false; Core.Settings.AreDevToolsEnabled = false;
                Core.Settings.AreDefaultContextMenusEnabled = false; Core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                Core.Settings.IsPasswordAutosaveEnabled = false; Core.Settings.IsGeneralAutofillEnabled = false;
                Core.Settings.IsStatusBarEnabled = false; Core.Settings.IsScriptEnabled = true;
                Core.Settings.IsWebMessageEnabled = Content.Plugin != null || Content.HtmlWorkspace != null;
                if (Content.Plugin != null) {
                    _pluginTransport = new PluginWebViewHost.Transport(_plugins, Core, Path.Combine(_assets, "plugin-host"), Content.Plugin, _environment);
                    Core.WebMessageReceived += PluginMessage;
                } else { _route = _environment.Route(Core, Content.Origin, Resource); Core.WebMessageReceived += ResourceControl; }
                Core.NavigationStarting += NavigationStarting; Core.NavigationCompleted += NavigationCompleted;
                Core.SourceChanged += SourceChanged;
                Core.DocumentTitleChanged += (sender, args) => { if (!_closed) { State.Title = Core.DocumentTitle; Publish(); } };
                Core.FrameNavigationStarting += (sender, args) => { string path; if (!Content.TryMember(args.Uri, out path) && args.Uri != "about:blank" && !args.Uri.StartsWith("blob:" + Content.Origin + "/", StringComparison.Ordinal)) args.Cancel = true; };
                Core.NewWindowRequested += (sender, args) => {
                    args.Handled = true; string member;
                    if (_closed || !Content.TryMember(args.Uri, out member)) return;
                    try { Content.Validate(); var address = new Uri(args.Uri); NewTabRequested?.Invoke(LocalLocator(member) + address.Query + address.Fragment); } catch { }
                };
                Core.PermissionRequested += (sender, args) => args.State = CoreWebView2PermissionState.Deny;
                Core.DownloadStarting += (sender, args) => {
                    string member; var uri = args.DownloadOperation.Uri;
                    args.Cancel = AgentOwnsInput || !(Content.TryMember(uri, out member) || uri.StartsWith("blob:" + Content.Origin + "/", StringComparison.Ordinal) ||
                        Content.Plugin != null && uri.StartsWith(Content.Origin + "/.result/download/", StringComparison.Ordinal));
                };
                Core.Settings.AreDefaultScriptDialogsEnabled = true;
                Core.ScriptDialogOpening += (sender, args) => { /* Agent mode dismisses dialogs; it cannot silently accept a confirmation. */ };
                Core.ProcessFailed += (sender, args) => { HasLoaded = false; State.Status = BrowserPageStatus.Failed; State.Message = "Процесс локальной страницы завершился. Обновите страницу."; _loaded.TrySetException(new InvalidOperationException(State.Message)); Publish(); };
                await Core.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
                await BuildHtmlAsync(linked.Token);
                Core.Navigate(Content.Url(Content.EntryPath));
                using (linked.Token.Register(() => _loaded.TrySetCanceled())) {
                    if (await Task.WhenAny(_loaded.Task, Task.Delay(15000, linked.Token)) != _loaded.Task) throw new TimeoutException("Local Browser page did not become ready.");
                    await _loaded.Task;
                }
            }
        }
        private void NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs args)
        {
            string path;
            if (_closed || !Content.TryMember(args.Uri, out path)) { args.Cancel = true; return; }
            try { Content.Validate(); } catch { args.Cancel = true; return; }
            if (_loaded.Task.IsCompleted && args.NavigationId != _navigation) State.DocumentGeneration++;
            _navigation = args.NavigationId; HasLoaded = false; State.Status = BrowserPageStatus.Starting;
            InvalidateAutomation(); Publish();
        }
        private async void NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (_closed || args.NavigationId != _navigation) return;
            try {
                if (!args.IsSuccess) throw new InvalidOperationException("Локальная страница не загрузилась: " + args.WebErrorStatus);
                await InitializeAutomationAsync();
                if (_closed || args.NavigationId != _navigation) return;
                HasLoaded = true; State.Status = BrowserPageStatus.Ready; State.Message = null; State.Reason = BrowserRefusalCode.None;
                _loaded.TrySetResult(true); Publish();
            } catch (Exception error) { if (!_closed) { State.Status = BrowserPageStatus.Failed; State.Message = error.Message; _loaded.TrySetException(error); Publish(); } }
        }
        private async void SourceChanged(object sender, CoreWebView2SourceChangedEventArgs args)
        {
            if (_closed || !_loaded.Task.IsCompleted) return; string member;
            if (!Content.TryMember(Core.Source, out member)) return;
            var address = new Uri(Core.Source); var target = State.Target;
            var locator = LocalLocator(member);
            locator += address.Query + address.Fragment;
            if (State.Url != locator) { State.Url = locator; if (!args.IsNewDocument) State.DocumentGeneration++; target.RelativePath = member; InvalidateAutomation();
                if (!args.IsNewDocument) try { await InitializeVisualAsync(); } catch { State.Visual = null; State.AnnotationContext = null; } }
            Publish();
        }
        internal string LocalLocator(string member)
        {
            var target = State.Target;
            if (target.PluginId != null) return BrowserLocalLocator.ForPlugin(target.PluginId, member, target.SavedVersionId);
            if (target.Kind == BrowserTargetKind.WorkspaceHtmlProject) return BrowserLocalLocator.ForProject(target.WorkspaceId, member, target.SavedVersionId);
            BrowserLocalLocator locator;
            if (!BrowserLocalLocator.TryParse(target.DisplayLocator, out locator)) throw new InvalidOperationException("The document locator is invalid.");
            return BrowserLocalLocator.ForResource(locator.Key, locator.VersionId, member);
        }
        internal event Action<string> NewTabRequested;
        private async void PluginMessage(object sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            if (_rpcPending >= 32) return; _rpcPending++;
            try {
                if (_closed || Content.Plugin == null) return;
                var source = new Uri(args.Source); if (source.GetLeftPart(UriPartial.Authority) != Content.Origin) return;
                var json = args.TryGetWebMessageAsString(); if (json == null || json.Length > 1024 * 1024) return;
                var request = JsonConvert.DeserializeObject<PluginFrameRequest>(json, new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error, MaxDepth = 32 });
                if (AgentOwnsInput && request?.Method == PluginMethod.PickProject) {
                    Core.PostWebMessageAsJson(JsonConvert.SerializeObject(new PluginFrameResponse { RequestId = request.RequestId, ErrorCode = "manual_selection_required", Message = "Select plugin data manually before agent input." })); return;
                }
                var response = await _plugins.DispatchAsync(Content.Plugin, request, _cancel.Token, message => {
                    if (!_closed) Control.BeginInvoke(new Action(() => { if (!_closed) Core.PostWebMessageAsJson(JsonConvert.SerializeObject(new PluginFrameResponse { RequestId = request.RequestId, Progress = message })); }));
                });
                if (!_closed) Core.PostWebMessageAsJson(JsonConvert.SerializeObject(response));
            } catch (Exception) { /* The scoped SDK timeout reports unknown; a lost RPC is never replayed. */ }
            finally { _rpcPending--; }
        }
        private async void Resource(CoreWebView2WebResourceRequestedEventArgs args)
        {
            var deferral = args.GetDeferral(); _pending++;
            try {
                string path;
                lock (_streamSync) { if (_closed || _pending > 16 || _streams.Count >= 32) throw new InvalidOperationException("Local response limit."); }
                if (!_closed && args.Request.Method == "GET" && _resources != null && _resources.IsRoute(args.Request.Uri)) {
                    var response = await Task.Run(() => _resources.Read(args.Request.Uri, _cancel.Token), _cancel.Token);
                    if (_closed) { response.Body.Dispose(); return; }
                    var stream = Track(response.Body);
                    args.Response = Core.Environment.CreateWebResourceResponse(stream, response.StatusCode, response.Reason, BrowserNetworkPolicy.Headers(response.ContentType) + response.AllowedHeaders); return;
                }
                if (_closed || args.Request.Method != "GET" || !Content.TryMember(args.Request.Uri, out path)) throw new InvalidOperationException("Local member denied.");
                byte[] assembled;
                var input = _assembledHtml.TryGetValue(path, out assembled) ? new MemoryStream(assembled, false) :
                    await Task.Run(() => Content.Open(path, _cancel.Token), _cancel.Token);
                if (_closed) { input.Dispose(); return; }
                var body = Track(input);
                args.Response = Core.Environment.CreateWebResourceResponse(body, 200, "OK", BrowserNetworkPolicy.Headers(PluginStore.Mime(path)));
            } catch (Exception) {
                if (!_closed) args.Response = Core.Environment.CreateWebResourceResponse(new MemoryStream(), 403, "Local resource only", BrowserNetworkPolicy.Headers("text/plain"));
            } finally { deferral.Complete(); _pending--; if (_closed && _pending == 0) _drained.TrySetResult(true); }
        }
        private Stream Track(Stream input)
        {
            var body = new Body(input, stream => { lock (_streamSync) _streams.Remove(stream); });
            lock (_streamSync) { if (_closed) { body.Dispose(); throw new ObjectDisposedException(nameof(LocalBrowserPage)); } _streams.Add(body); }
            return body;
        }
        private void Publish() { if (!_closed) { State.Revision++; Changed?.Invoke(State); } }
        internal void Focus() { Controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic); }
        public void Dispose()
        {
            if (_closed) return; _closed = true; _cancel.Cancel(); _loaded.TrySetCanceled();
            _resources?.Dispose(); _route?.Dispose(); _pluginTransport?.Dispose(); Controller?.Close(); Control.Dispose();
            lock (_streamSync) { foreach (var stream in _streams.ToArray()) stream.Dispose(); _streams.Clear(); } Content?.Dispose();
            if (_pending == 0) _drained.TrySetResult(true);
            Drained = DrainAsync();
        }
        private async Task DrainAsync()
        {
            try { await Task.WhenAll(_drained.Task, _resources?.Drained ?? Task.CompletedTask, _pluginTransport?.Drained ?? Task.CompletedTask, Content?.Drained ?? Task.CompletedTask); }
            finally { _environmentLease?.Dispose(); _environmentLease = null; }
        }
        private sealed class Body : Stream
        {
            private Stream _source; private readonly Action<Stream> _release; private long _read;
            internal Body(Stream source, Action<Stream> release) { _source = source; _release = release; }
            public override bool CanRead => _source != null; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => _source.Length; public override long Position { get => _read; set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) { var read = _source?.Read(buffer, offset, count) ?? 0; _read += read; if (_read > WorkspaceFileService.MaximumCaptureSetBytes) throw new IOException("Browser member exceeds its byte bound."); if (read == 0) Dispose(); return read; }
            protected override void Dispose(bool disposing) { var source = Interlocked.Exchange(ref _source, null); if (source != null) { source.Dispose(); _release(this); } base.Dispose(disposing); }
            public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
