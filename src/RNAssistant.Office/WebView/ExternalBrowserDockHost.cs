using RNAssistant.Core.Models;
using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.WebView
{
    // The replaceable placement owner. No browser policy, navigation history or durable store.
    internal sealed class ExternalBrowserDockHost : IExternalBrowserHost, IExternalBrowserActionSource, IDisposable
    {
        private readonly Control _parent;
        private readonly WebView2 _shell;
        private readonly string _assets;
        private readonly Action<ExternalBrowserStateDto> _publish;
        private readonly Action<string, BrowserAccessKind> _requireAccess;
        private readonly Func<string, string, Task<Action>> _pauseRun;
        private ExternalBrowserPage _page;
        private Task _initializing = Task.CompletedTask;
        private bool _disposed;
        private string _placement = "hidden";
        private long _layoutRevision;
        private volatile ExternalBrowserReadTarget _readTarget;

        public ExternalBrowserReadTarget TargetFor(string chatId)
        { var target = _readTarget; return target != null && target.ChatId == chatId ? target : null; }

        // One short native operation at a time. No gate is held across model/confirmation waits.
        private SemaphoreSlim _semanticGate = new SemaphoreSlim(1, 1);
        public Task<ExternalBrowserSemanticContent> ReadAsync(ExternalBrowserReadTarget target, CancellationToken token)
        { return InvokeSemanticAsync(target, (page, dispatch) => page.ReadAsync(target), token); }

        public async Task PrepareAsync(ExternalBrowserReadTarget target, string revision, string preparationId,
            ExternalBrowserActionRequest request, CancellationToken token)
        { await InvokeSemanticAsync(target, (page, dispatch) => page.PrepareAsync(target, revision, preparationId, request), token).ConfigureAwait(false); }

        public Task<ExternalBrowserActionResult> ActAsync(ExternalBrowserReadTarget target, string preparationId,
            Action beforeDispatch, CancellationToken token, bool navigation = false)
        { return InvokeSemanticAsync(target, (page, dispatch) => page.ActAsync(target, preparationId, dispatch), token, beforeDispatch,
            navigation ? 15000 : 3000, navigation ? new Func<ExternalBrowserActionResult, ExternalBrowserReadTarget>(result => result.NavigationTarget) : null); }

        public void BlockActions(ExternalBrowserReadTarget target)
        { var page = _page; if (page?.SessionId == target?.SessionId) page?.BlockActions(); }

        public void PresentResult(ExternalBrowserReadTarget target, string preparationId, ExternalBrowserEffect effect)
        {
            if (_disposed || _parent.IsDisposed || target == null) return;
            try { _parent.BeginInvoke(new Action(() => _page?.PresentResult(target, preparationId, effect))); }
            catch (Exception error) { Diagnostics.RuntimeLog.Error("Browser presentation was unavailable after the terminal.", error); }
        }

        private async Task<T> InvokeSemanticAsync<T>(ExternalBrowserReadTarget target,
            Func<ExternalBrowserPage, Action, Task<T>> operation, CancellationToken token, Action beforeDispatch = null,
            int timeoutMs = 3000, Func<T, ExternalBrowserReadTarget> resultTarget = null)
        {
            token.ThrowIfCancellationRequested();
            if (!_parent.InvokeRequired) throw ExternalBrowserResourceProvider.Error("Browser operations require a background caller.", "browser_dispatch_invalid");
            var gate = _semanticGate;
            if (!await gate.WaitAsync(3000, token).ConfigureAwait(false))
                throw ExternalBrowserResourceProvider.Error("A browser operation is still pending.", "browser_operation_busy");
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var dispatchSync = new object(); bool dispatched = false; ExternalBrowserPage page = null;
            Action dispatch = () => {
                lock (dispatchSync)
                {
                    if (completion.Task.IsCompleted) throw new OperationCanceledException();
                    token.ThrowIfCancellationRequested();
                    beforeDispatch(); dispatched = true;
                }
            };
            try
            {
                _parent.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        if (completion.Task.IsCompleted) return;
                        token.ThrowIfCancellationRequested();
                        if (_disposed || !target.Matches(_readTarget)) throw ExternalBrowserResourceProvider.Error("The browser binding is no longer current.", "browser_document_stale");
                        page = _page;
                        var value = await operation(page, dispatch);
                        var after = resultTarget?.Invoke(value) ?? target;
                        if (after.ChatId != target.ChatId || after.SessionId != target.SessionId || !after.Matches(_readTarget))
                            throw ExternalBrowserResourceProvider.Error("The page changed during the operation.", "browser_document_stale");
                        completion.TrySetResult(value);
                    }
                    catch (Exception error)
                    {
                        if (dispatched) page?.BlockActions();
                        if (error is OperationCanceledException) completion.TrySetCanceled();
                        else completion.TrySetException(error is RNAssistant.Core.Models.ResourceRequestException ? error :
                            ExternalBrowserResourceProvider.Error("The native browser operation failed.", "browser_operation_failed"));
                    }
                    finally { gate.Release(); }
                }));
            }
            catch { gate.Release(); throw; }
            if (await Task.WhenAny(completion.Task, Task.Delay(timeoutMs, token)).ConfigureAwait(false) != completion.Task)
            {
                // Queued work cannot dispatch later. In-flight work retains the gate until it ends.
                bool abandoned;
                lock (dispatchSync)
                {
                    abandoned = completion.TrySetCanceled();
                    if (abandoned && dispatched) page.BlockActions();
                }
                if (abandoned)
                {
                    token.ThrowIfCancellationRequested();
                    throw ExternalBrowserResourceProvider.Error("The browser operation timed out. A dispatched effect may be unknown.", "browser_operation_timeout");
                }
            }
            return await completion.Task.ConfigureAwait(false);
        }

        public ExternalBrowserDockHost(Control parent, WebView2 shell, string assets, Action<ExternalBrowserStateDto> publish,
            Action<string, BrowserAccessKind> requireAccess, Func<string, string, Task<Action>> pauseRun)
        {
            _parent = parent; _shell = shell; _assets = assets; _publish = publish; _requireAccess = requireAccess; _pauseRun = pauseRun;
            _shell.SizeChanged += InvalidatePlacement;
            _shell.LocationChanged += InvalidatePlacement;
            _shell.ZoomFactorChanged += InvalidatePlacement;
            _shell.VisibleChanged += InvalidatePlacement;
        }

        public Task<ExternalBrowserStateDto> ExecuteAsync(ExternalBrowserCommandPayload request, CancellationToken cancellationToken)
        {
            if (_disposed || _parent.IsDisposed) throw new ObjectDisposedException(nameof(ExternalBrowserDockHost));
            if (!_parent.InvokeRequired) return ExecuteOnUiAsync(request, cancellationToken);
            var result = new TaskCompletionSource<ExternalBrowserStateDto>(TaskCreationOptions.RunContinuationsAsynchronously);
            _parent.BeginInvoke(new Action(async () =>
            {
                try { result.TrySetResult(await ExecuteOnUiAsync(request, cancellationToken)); }
                catch (Exception error) { result.TrySetException(error); }
            }));
            return result.Task;
        }

        private Task<ExternalBrowserStateDto> ExecuteOnUiAsync(ExternalBrowserCommandPayload request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed) throw new ObjectDisposedException(nameof(ExternalBrowserDockHost));
            if (request == null || !Enum.IsDefined(typeof(ExternalBrowserAction), request.Action)) throw new InvalidOperationException("Недопустимая команда браузера.");
            if (request.Action == ExternalBrowserAction.Open)
            {
                if (_page != null) throw new InvalidOperationException("Пилот поддерживает одну Browser-вкладку.");
                if (!_initializing.IsCompleted) throw new InvalidOperationException("Предыдущий WebView2 ещё завершает запуск. Повторите открытие через несколько секунд.");
                if (request.ChatId != null && request.ChatId.Length > 256) throw new InvalidOperationException("Некорректный владелец Browser-вкладки.");
                var page = new ExternalBrowserPage(_assets, request.ChatId, _requireAccess, _pauseRun);
                _semanticGate = new SemaphoreSlim(1, 1);
                _page = page; page.Control.Bounds = new Rectangle(0, 0, 1, 1);
                _parent.Controls.Add(page.Control); page.Control.BringToFront();
                page.Changed += Publish;
                // Return the runtime identity before waiting for Runtime creation. Close remains addressable during startup.
                _initializing = InitializeAsync(page);
                return Task.FromResult(Snapshot());
            }
            if (_page == null || request.SessionId != _page.SessionId) throw new InvalidOperationException("Browser session закрыта или устарела.");
            if (request.Action == ExternalBrowserAction.Close)
            {
                var sessionId = _page.SessionId; Reset();
                return Task.FromResult(new ExternalBrowserStateDto { SessionId = sessionId, Status = ExternalBrowserStatus.Closed });
            }
            if (request.Action == ExternalBrowserAction.Layout) Place(request.Bounds);
            else if (request.Action == ExternalBrowserAction.ResumeAgent) return ResumeAgentAsync(request, _page, cancellationToken);
            else _page.Execute(request);
            return Task.FromResult(Snapshot());
        }

        private async Task<ExternalBrowserStateDto> ResumeAgentAsync(ExternalBrowserCommandPayload request, ExternalBrowserPage page, CancellationToken token)
        {
            var gate = _semanticGate;
            if (!await gate.WaitAsync(3000, token)) throw new InvalidOperationException("Операция браузера ещё выполняется. Повторите продолжение после её завершения.");
            try
            {
                if (_page != page) throw new InvalidOperationException("Browser session закрыта или изменена.");
                await page.ResumeAgentAsync(request.ControlRevision);
                if (_page != page) throw new InvalidOperationException("Browser session закрыта или изменена.");
                return Snapshot();
            }
            finally { gate.Release(); }
        }

        private async Task InitializeAsync(ExternalBrowserPage page)
        {
            try { await page.InitializeAsync(); }
            catch (Exception error) { if (!_disposed && _page == page) page.Fail(error); }
        }

        private void Place(ExternalBrowserBoundsDto bounds)
        {
            _placement = "hidden";
            if (!ExternalBrowserPolicy.ValidBounds(bounds)) { _page.Control.Visible = false; throw new InvalidOperationException("Некорректная геометрия Browser."); }
            if (!bounds.Visible || !_parent.Visible || !_shell.Visible || _page.Snapshot("").Status == ExternalBrowserStatus.Failed) { _page.Control.Visible = false; return; }
            var scale = _shell.ClientSize.Width / bounds.ViewportWidth;
            var vertical = _shell.ClientSize.Height / bounds.ViewportHeight;
            if (scale <= 0 || Math.Abs(scale - vertical) > 0.05)
            { _page.Control.Visible = false; _placement = "hidden: viewport changed"; return; }
            var rectangle = Rectangle.FromLTRB(
                _shell.Left + (int)Math.Ceiling(bounds.X * scale), _shell.Top + (int)Math.Ceiling(bounds.Y * scale),
                _shell.Left + (int)Math.Floor((bounds.X + bounds.Width) * scale), _shell.Top + (int)Math.Floor((bounds.Y + bounds.Height) * scale));
            if (rectangle.Width < 1 || rectangle.Height < 1 || !_shell.Bounds.Contains(rectangle)) { _page.Control.Visible = false; return; }
            _page.Control.Bounds = rectangle;
            _page.Control.BringToFront();
            _page.Control.Visible = true;
            _placement = "HWND 0x" + _parent.Handle.ToInt64().ToString("X") + " · native " + rectangle +
                " · scale " + scale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        }
        private void InvalidatePlacement(object sender, EventArgs e)
        { if (_page != null) { _page.Control.Visible = false; _placement = "hidden: layout pending"; _layoutRevision++; Publish(); } }
        private ExternalBrowserStateDto Snapshot()
        {
            var value = _page.Snapshot(_placement + " · DPI " + _parent.DeviceDpi + " · controls 1 · focus " + _page.Control.ContainsFocus);
            value.LayoutRevision = _layoutRevision; return value;
        }
        private void Publish() { _readTarget = _page?.ReadTarget; if (_page != null && !_disposed) _publish(Snapshot()); }

        public void Reset()
        {
            var page = _page; _page = null; _readTarget = null; _placement = "hidden";
            if (page == null) return;
            page.Changed -= Publish; page.Dispose();
            _publish(page.Snapshot("controls 0; scope closed"));
        }
        public Task ResetAsync()
        {
            if (!_parent.InvokeRequired) { Reset(); return Task.CompletedTask; }
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _parent.BeginInvoke(new Action(() => { try { Reset(); completion.TrySetResult(true); } catch (Exception error) { completion.TrySetException(error); } }));
            return completion.Task;
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _shell.SizeChanged -= InvalidatePlacement; _shell.LocationChanged -= InvalidatePlacement; _shell.ZoomFactorChanged -= InvalidatePlacement;
            _shell.VisibleChanged -= InvalidatePlacement;
            Reset();
        }
    }
}
