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
    internal sealed class ExternalBrowserDockHost : IExternalBrowserHost, IExternalBrowserReadSource, IDisposable
    {
        private readonly Control _parent;
        private readonly WebView2 _shell;
        private readonly string _assets;
        private readonly Action<ExternalBrowserStateDto> _publish;
        private ExternalBrowserPage _page;
        private Task _initializing = Task.CompletedTask;
        private bool _disposed;
        private string _placement = "hidden";
        private long _layoutRevision;
        private volatile ExternalBrowserReadTarget _readTarget;

        public ExternalBrowserReadTarget TargetFor(string chatId)
        { var target = _readTarget; return target != null && target.ChatId == chatId ? target : null; }

        public async Task<ExternalBrowserSemanticContent> ReadAsync(ExternalBrowserReadTarget target, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_parent.InvokeRequired) throw ExternalBrowserResourceProvider.Error("Browser reads must be dispatched asynchronously off the UI thread.", "browser_dispatch_invalid");
            if (_disposed || !target.Matches(_readTarget)) throw ExternalBrowserResourceProvider.Error("The browser binding is no longer current.", "browser_document_stale");
            var completion = new TaskCompletionSource<ExternalBrowserSemanticContent>(TaskCreationOptions.RunContinuationsAsynchronously);
            _parent.BeginInvoke(new Action(async () =>
            {
                try
                {
                    if (completion.Task.IsCompleted) return;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_disposed || !target.Matches(_readTarget)) throw ExternalBrowserResourceProvider.Error("The browser binding is no longer current.", "browser_document_stale");
                    var value = await _page.ReadAsync(target);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!target.Matches(_readTarget)) throw ExternalBrowserResourceProvider.Error("The page changed during capture.", "browser_document_stale");
                    completion.TrySetResult(value);
                }
                catch (OperationCanceledException) { completion.TrySetCanceled(); }
                catch (RNAssistant.Core.Models.ResourceRequestException error) { completion.TrySetException(error); }
                catch (Exception) { completion.TrySetException(ExternalBrowserResourceProvider.Error("The native semantic capture failed.", "browser_capture_failed")); }
            }));
            if (await Task.WhenAny(completion.Task, Task.Delay(3000, cancellationToken)).ConfigureAwait(false) != completion.Task)
            {
                completion.TrySetCanceled();
                cancellationToken.ThrowIfCancellationRequested();
                throw ExternalBrowserResourceProvider.Error("Browser capture timed out; no observation was accepted.", "browser_read_timeout");
            }
            return await completion.Task.ConfigureAwait(false);
        }

        public ExternalBrowserDockHost(Control parent, WebView2 shell, string assets, Action<ExternalBrowserStateDto> publish)
        {
            _parent = parent; _shell = shell; _assets = assets; _publish = publish;
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
                var page = new ExternalBrowserPage(_assets, request.ChatId);
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
            else _page.Execute(request);
            return Task.FromResult(Snapshot());
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
