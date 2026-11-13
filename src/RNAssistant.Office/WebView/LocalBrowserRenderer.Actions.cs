using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Application.Contracts;

namespace RNAssistant.Office.WebView
{
    internal sealed partial class LocalBrowserRenderer
    {
        private readonly SemaphoreSlim _actions = new SemaphoreSlim(1, 1);
        internal Func<BrowserCommandPayload, CancellationToken, Task<BrowserPageStateDto>> Navigate { get; set; }
        private async Task<T> Action<T>(BrowserPageHandle target, Func<LocalBrowserPage, Task<T>> action, CancellationToken token)
        {
            if (!await _actions.WaitAsync(3000, token).ConfigureAwait(false)) throw new InvalidOperationException("Browser input is busy.");
            try { return await OnUi(async () => { token.ThrowIfCancellationRequested(); return await action(Exact(target)); }).ConfigureAwait(false); }
            finally { _actions.Release(); }
        }
        public Task<BrowserObservation> ReadAsync(BrowserPageHandle target, CancellationToken token)
        { return Action(target, page => page.ReadAsync(token), token); }
        public Task PrepareAsync(BrowserPageHandle target, string revision, string preparation, BrowserActionRequest request, CancellationToken token)
        { return Action(target, async page => {
            if (request.Action == "navigate" && request.Locator != null) page.PrepareNavigation(revision, preparation, request);
            else await page.PrepareAsync(revision, preparation, request, token);
            Exact(target); return true;
        }, token); }
        public Task<BrowserActionResult> ActAsync(BrowserPageHandle target, string preparation, System.Action dispatch, CancellationToken token, bool navigation = false)
        { return Action(target, async page => {
            if (navigation) {
                var locator = await page.AdmitNavigationAsync(preparation, token);
                token.ThrowIfCancellationRequested(); Exact(target); dispatch();
                try {
                    var result = await Navigate(new BrowserCommandPayload { Action = BrowserCommandKind.Navigate, BrowserTabId = target.BrowserTabId,
                        SessionId = target.SessionId, OwnerScope = target.OwnerScope, Locator = locator }, token);
                    if (result.Status != BrowserPageStatus.Ready || result.SessionId != target.SessionId || !result.Attached)
                        throw new InvalidOperationException("Local navigation did not become ready.");
                    var next = _pages[target.BrowserTabId].Handle;
                    return new BrowserActionResult { Effect = BrowserActionEffect.VerifiedChange, Code = "browser_navigation_verified", NavigationTarget = next };
                } catch { LocalBrowserPage current; if (_pages.TryGetValue(target.BrowserTabId, out current)) current.BlockActions(); throw; }
            }
            var value = await page.ActAsync(preparation, dispatch, token);
            if (!page.Handle.Matches(target)) { page.BlockActions(); throw new InvalidOperationException("The Browser changed during input."); }
            return value;
        }, token); }
        public void BlockActions(BrowserPageHandle target)
        { _ui.Post(ignored => { LocalBrowserPage page; if (target != null && _pages.TryGetValue(target.BrowserTabId, out page) && page.State.SessionId == target.SessionId) page.BlockActions(); }, null); }
        public void PresentResult(BrowserPageHandle target, string preparation, BrowserActionEffect effect)
        { _ui.Post(async ignored => { try { await Exact(target).PresentResult(preparation, effect); } catch { } }, null); }
        public Task<BrowserMediaCapture> ReadMediaAsync(BrowserPageHandle target, CancellationToken token)
        { return Action(target, page => page.ReadMediaAsync(token), token); }
        public async Task<BrowserMediaCapture> CaptureScreenshotAsync(BrowserScreenshotRequest request, CancellationToken token)
        {
            await _dispatch.WaitAsync(token).ConfigureAwait(false);
            try { return await OnUi(async () => {
                token.ThrowIfCancellationRequested(); LocalBrowserPage page;
                if (_closed || request?.BrowserTabId == null || !_pages.TryGetValue(request.BrowserTabId, out page) || !page.Ready ||
                    page.State.SessionId != request.SessionId || page.State.DocumentGeneration != request.Generation)
                    throw new InvalidOperationException("Страница снимка изменилась. Повторите действие.");
                return await page.ReadMediaAsync(token);
            }).ConfigureAwait(false); } finally { _dispatch.Release(); }
        }
        public bool IsCaptureCurrent(BrowserMediaCapture capture)
        { return capture?.Page != null && _captures.Any(value => value.Page.Matches(capture.Page) && value.ViewportRevision == capture.ViewportRevision && value.ControlRevision == capture.ControlRevision && value.Zoom == capture.Zoom); }
        private volatile BrowserMediaCapture[] _captures = new BrowserMediaCapture[0];
        private volatile BrowserPageHandle[] _handles = new BrowserPageHandle[0];
        private void RefreshHandles()
        {
            var pages = _pages.Values.Where(page => page.Ready).ToArray();
            _handles = pages.Where(page => page.State.ChatBound).Select(page => page.Handle).ToArray();
            _captures = pages.Where(page => page.Control.Visible).Select(page => new BrowserMediaCapture { Page = page.Handle,
                ViewportRevision = page.State.ViewportRevision, ControlRevision = page.State.ControlRevision, Zoom = page.Controller.ZoomFactor }).ToArray();
        }
    }
}
