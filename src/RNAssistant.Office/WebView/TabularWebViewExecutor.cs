using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;

namespace RNAssistant.Office.WebView
{
    // Application-owned, lazy, one-job realm. Never navigates the main UI WebView,
    // binds an Office controller, or executes caller-provided scripts.
    public sealed class TabularWebViewExecutor : IDisposable
    {
        private readonly CoreWebView2Environment _environment;
        private readonly IntPtr _parent;
        private readonly int _thread = Thread.CurrentThread.ManagedThreadId;
        private readonly SynchronizationContext _ui;
        private Run _active;
        private bool _disposed;
        public Task Drained { get; private set; } = Task.CompletedTask;

        public TabularWebViewExecutor(CoreWebView2Environment environment, IntPtr parent)
        {
            _environment = environment ?? throw new ArgumentNullException(nameof(environment));
            if (parent == IntPtr.Zero) throw new ArgumentException("An application window is required.", nameof(parent));
            _parent = parent;
            _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("A UI dispatcher is required.");
        }

        // Admission transfers ownership of resources. All results remain provisional.
        // The queue's future parser dispatcher supplies the exact source/job/sink.
        public Task<TabularOutputResult> ExecuteAsync(WorkspaceTabularResourceSession resources, string inputJson,
            ITabularResultSink sink, CancellationToken token, Action<TabularExecutionProgress> progress = null, int deadlineMs = 120000)
        {
            RequireUi();
            if (_disposed) throw new ObjectDisposedException(nameof(TabularWebViewExecutor));
            if (Drained.IsFaulted) throw new WorkspaceApplicationException("tabular_executor_unavailable", "Previous executor cleanup was not confirmed. Recreate the application pane.");
            if (_active != null) throw new WorkspaceApplicationException("tabular_executor_busy", "The executor is still running or draining.");
            if (resources == null || resources.IsClosed || !resources.EntryUri.StartsWith(WorkspaceTabularResourceSession.NativeOrigin + "/r/", StringComparison.Ordinal))
                throw new ArgumentException("An open native resource session is required.", nameof(resources));
            token.ThrowIfCancellationRequested();
            var run = new Run(_environment, _parent, _ui, resources, inputJson, sink, progress, deadlineMs);
            _active = run;
            var execution = ExecuteOwnedAsync(run, token);
            // Drained describes cleanup, not success. The execution task carries errors.
            Drained = run.Drained;
            return execution;
        }

        private async Task<TabularOutputResult> ExecuteOwnedAsync(Run run, CancellationToken token)
        {
            try { return await run.ExecuteAsync(token); }
            finally { _active = null; }
        }
        private void RequireUi()
        { if (Thread.CurrentThread.ManagedThreadId != _thread) throw new InvalidOperationException("Use the application UI dispatcher."); }
        public void Dispose()
        {
            RequireUi();
            if (_disposed) return;
            _disposed = true;
            if (_active != null) _active.CloseOnUi();
        }

        private sealed class Run
        {
            private readonly CoreWebView2Environment _environment;
            private readonly IntPtr _parent;
            private readonly SynchronizationContext _ui;
            private readonly WorkspaceTabularResourceSession _resources;
            private readonly TabularExecutionChannel _channel;
            private readonly Task _sourceMonitor;
            private readonly CancellationTokenSource _deadline = new CancellationTokenSource();
            private readonly TaskCompletionSource<bool> _drained = NewSignal(), _requestsDrained = NewSignal();
            private readonly TabularResponseBodyPool _bodies = new TabularResponseBodyPool();
            private Task _messages = Task.CompletedTask;
            private int _pendingMessages;
            private CoreWebView2Controller _controller;
            private bool _closing, _navigated;
            private Exception _failure, _closeFailure;
            private int _requests;
            public Task Drained { get { return _drained.Task; } }

            public Run(CoreWebView2Environment environment, IntPtr parent, SynchronizationContext ui,
                WorkspaceTabularResourceSession resources, string input, ITabularResultSink sink,
                Action<TabularExecutionProgress> progress, int deadlineMs)
            {
                _environment = environment; _parent = parent; _ui = ui; _resources = resources;
                _channel = new TabularExecutionChannel(input, resources.BundleSha256, deadlineMs, sink, Send,
                    () => resources.ClientCloseConfirmed, progress);
                _sourceMonitor = resources.SourceDrained.ContinueWith(ignored => {
                    if (resources.WasRevoked) Stop(new OperationCanceledException("Native tabular source was revoked."));
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                _deadline.CancelAfter(deadlineMs);
            }

            public async Task<TabularOutputResult> ExecuteAsync(CancellationToken token)
            {
                try
                {
                    using (token.Register(() => Stop(new OperationCanceledException("Tabular execution was cancelled.", token))))
                    using (_deadline.Token.Register(() => Stop(new WorkspaceApplicationException("tabular_deadline", "Tabular execution deadline expired."))))
                    {
                        TabularOutputResult result;
                        try
                        {
                            if (_channel.Completion.IsCompleted) await _channel.Completion;
                            var options = _environment.CreateCoreWebView2ControllerOptions();
                            options.ProfileName = "RNAssistantTabular";
                            options.IsInPrivateModeEnabled = true;
                            _controller = await _environment.CreateCoreWebView2ControllerAsync(_parent, options);
                            // Creation has no cancellation API. Close late creation
                            // before navigation; keep the owner busy until it drains.
                            if (_channel.Completion.IsCompleted) await _channel.Completion;
                            _controller.IsVisible = false;
                            _controller.Bounds = new Rectangle(0, 0, 1, 1);
                            Configure(_controller.CoreWebView2);
                            _controller.CoreWebView2.Navigate(_resources.EntryUri);
                            result = await _channel.Completion;
                        }
                        catch (Exception error) { _channel.Stop(error); throw; }
                        finally
                        {
                            // Native stop does not wait for JS acknowledgements.
                            TryCloseController();
                            try
                            {
                                await Task.WhenAll(_resources.CloseAsync(), _sourceMonitor, _requestsDrained.Task, _channel.Drained, _messages);
                                if (_closeFailure != null) throw _closeFailure;
                                _drained.TrySetResult(true);
                            }
                            catch (Exception error) { _channel.Stop(error); _drained.TrySetException(error); throw; }
                        }
                        if (_failure != null) throw _failure;
                        token.ThrowIfCancellationRequested();
                        return result;
                    }
                }
                catch (Exception error) { _channel.Stop(error); throw; }
                finally { _channel.Dispose(); _deadline.Dispose(); }
            }

            private void Configure(CoreWebView2 core)
            {
                // All request sources, including Worker imports. Unsupported runtime
                // versions fail here; there is no two-argument/filter fallback.
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
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
                core.Settings.IsScriptEnabled = true;
                core.Settings.IsWebMessageEnabled = true;
                core.Settings.AreHostObjectsAllowed = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDefaultScriptDialogsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsPasswordAutosaveEnabled = false;
                core.Settings.IsGeneralAutofillEnabled = false;
            }

            private void Send(string json)
            {
                _ui.Post(ignored => {
                    if (_closing || _channel.Completion.IsCompleted) return;
                    try { _controller.CoreWebView2.PostWebMessageAsJson(json); }
                    catch (Exception error) { Stop(error); }
                }, null);
            }
            private async void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
            {
                var queued = false;
                try
                {
                    if (_closing) return;
                    if (e.Source != _resources.EntryUri) throw Refused("Foreign execution message.");
                    var json = e.TryGetWebMessageAsString();
                    if (json.Length > TabularExecutionChannel.MaximumMessageCharacters || _pendingMessages >= 32)
                        throw Refused("Execution message budget exceeded.");
                    _pendingMessages++; queued = true;
                    // Result consumers and JSON validation never run on Office's STA.
                    // Preserve delivery order across asynchronous pool scheduling.
                    _messages = _messages.ContinueWith(ignored => _channel.ReceiveAsync(json), CancellationToken.None,
                        TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
                    await _messages;
                }
                catch (Exception error) { Stop(error); }
                finally { if (queued) _pendingMessages--; }
            }
            private async void OnResource(object sender, CoreWebView2WebResourceRequestedEventArgs e)
            {
                if (_closing) return;
                var deferral = e.GetDeferral();
                _requests++;
                TabularResourceResponse response = null;
                try
                {
                    var uri = e.Request.Uri;
                    var method = e.Request.Content == null ? e.Request.Method : "POST";
                    var origin = e.Request.Headers.Contains("Origin") ? e.Request.Headers.GetHeader("Origin") : null;
                    response = await Task.Run(() => _resources.HandleAsync(method, uri, origin));
                    if (_closing) return;
                    var body = _bodies.Take(response);
                    var headers = "Content-Type: " + response.ContentType + "\r\n" +
                        string.Join("\r\n", response.Headers.Select(pair => pair.Key + ": " + pair.Value));
                    e.Response = _environment.CreateWebResourceResponse(body, response.StatusCode, "Tabular resource", headers);
                    response = null; // The bounded response body now owns disposal.
                }
                catch (Exception error) { Stop(error); }
                finally
                {
                    if (response != null) response.Dispose();
                    try { deferral.Complete(); } catch (Exception error) { if (!_closing) Stop(error); }
                    _requests--;
                    if (_closing && _requests == 0) _requestsDrained.TrySetResult(true);
                }
            }
            private void OnNavigation(object sender, CoreWebView2NavigationStartingEventArgs e)
            {
                if (_closing) { e.Cancel = true; return; }
                if (!_closing && !_navigated && !e.IsRedirected && e.Uri == _resources.EntryUri) { _navigated = true; return; }
                e.Cancel = true; Stop(Refused("Execution navigation was refused."));
            }
            private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
            { if (!e.IsSuccess && !_closing) Stop(Refused("Execution document did not load.")); }
            private void OnFrame(object sender, CoreWebView2NavigationStartingEventArgs e)
            { e.Cancel = true; Stop(Refused("Execution frames are forbidden.")); }
            private void OnWindow(object sender, CoreWebView2NewWindowRequestedEventArgs e)
            { e.Handled = true; Stop(Refused("Execution windows are forbidden.")); }
            private void OnPermission(object sender, CoreWebView2PermissionRequestedEventArgs e)
            { e.State = CoreWebView2PermissionState.Deny; e.Handled = true; Stop(Refused("Execution permissions are forbidden.")); }
            private void OnDownload(object sender, CoreWebView2DownloadStartingEventArgs e)
            { e.Cancel = true; e.Handled = true; Stop(Refused("Execution downloads are forbidden.")); }
            private void OnProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
            { if (!_closing) Stop(Refused("Execution renderer failed or became unresponsive.")); }
            private void OnDialog(object sender, CoreWebView2ScriptDialogOpeningEventArgs e)
            { Stop(Refused("Execution dialogs are forbidden.")); }

            private void Stop(Exception error)
            {
                Interlocked.CompareExchange(ref _failure, error, null);
                _channel.Stop(error); // Immediate native staging revocation on any thread.
                _ = _resources.CloseAsync();
                _ui.Post(ignored => TryCloseController(), null);
            }
            public void CloseOnUi()
            { Stop(new OperationCanceledException("Application execution owner was closed.")); TryCloseController(); }
            private void TryCloseController()
            {
                try { CloseController(); }
                catch (Exception error) { _closeFailure = error; _channel.Stop(error); }
            }
            private void CloseController()
            {
                _closing = true;
                var controller = _controller; _controller = null;
                try { if (controller != null) controller.Close(); }
                finally
                {
                    _bodies.Dispose();
                    if (_requests == 0) _requestsDrained.TrySetResult(true);
                }
            }
            private static WorkspaceApplicationException Refused(string message)
            { return new WorkspaceApplicationException("tabular_host_refused", message); }
            private static TaskCompletionSource<bool> NewSignal()
            { return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); }
        }

    }
}
