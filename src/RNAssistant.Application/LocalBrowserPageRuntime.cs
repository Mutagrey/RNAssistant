using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Application.Contracts;

namespace RNAssistant.Application
{
    // The sole local tab/session/history owner; the native adapter owns rendering/input only.
    public sealed partial class LocalBrowserPageRuntime : IBrowserPageRuntime, IBrowserOpenSource, IBrowserMediaSource, IBrowserAnnotationSource, IDisposable
    {
        private sealed class Visit { public BrowserOwnerScope Owner; public string Locator, NativeController; public int? NativeEntry; }
        private sealed class Page
        {
            public BrowserPageStateDto State;
            public string OpenKey;
            public List<Visit> History = new List<Visit>();
            public int Position = -1;
            public BrowserBoundsDto Bounds;
        }
        private readonly SemaphoreSlim _commands = new SemaphoreSlim(1, 1);
        private readonly object _sync = new object();
        private readonly Dictionary<string, Page> _pages = new Dictionary<string, Page>(StringComparer.Ordinal);
        private readonly Dictionary<string, Page> _operations = new Dictionary<string, Page>(StringComparer.Ordinal);
        private readonly Queue<string> _receipts = new Queue<string>();
        private readonly Func<BrowserResolveRequest, BrowserResolveResult> _resolve;
        private readonly Action<BrowserPageStateDto> _publish;
        private bool _disposed;
        private readonly IBrowserRenderer _renderer;
        private const string NativeUnavailable = "Native Browser недоступен в этом host. Исходники и версии доступны.";
        public LocalBrowserPageRuntime(Func<BrowserResolveRequest, BrowserResolveResult> resolve, Action<BrowserPageStateDto> publish,
            Func<BrowserOwnerScope, IReadOnlyList<BrowserTargetDescriptor>> sourceCatalog = null, Action<BrowserDockOpenRequest> requestDockOpen = null, IBrowserRenderer renderer = null)
        { _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve)); _publish = publish; _sourceCatalog = sourceCatalog; _requestDockOpen = requestDockOpen; _renderer = renderer; if (_renderer != null) _renderer.Changed += NativeChanged; }

        public async Task<BrowserPageStateDto> ExecuteAsync(BrowserCommandPayload request, CancellationToken token)
        {
            await _commands.WaitAsync(token).ConfigureAwait(false);
            try { return await DeliverAsync(request, token).ConfigureAwait(false); }
            finally { _commands.Release(); }
        }
        private async Task<BrowserPageStateDto> DeliverAsync(BrowserCommandPayload request, CancellationToken token)
        {
            if (request != null) { request.NativeHistoryEntryId = null; request.NativeLocator = null; }
            var result = await ExecuteCoreAsync(request, token).ConfigureAwait(false);
            if (_renderer != null) {
                try { result = await _renderer.UpdateAsync(request, result, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) when (request.Action != BrowserCommandKind.ResumeAgent && request.Action != BrowserCommandKind.Focus && request.Action != BrowserCommandKind.Layout && request.Action != BrowserCommandKind.VisualControl) {
                    result.Status = BrowserPageStatus.Unavailable; result.Reason = BrowserRefusalCode.NativeUnavailable; result.Message = error.Message;
                    if (result.Target?.Capabilities != null) { result.Target.Capabilities.Preview = result.Target.Capabilities.Interact = result.Target.Capabilities.Media = false;
                    result.Target.Capabilities.PreviewReason = result.Reason; }
                }
                lock (_sync) {
                    Page page;
                    if (result.Status != BrowserPageStatus.Closed && (!_pages.TryGetValue(result.BrowserTabId, out page) || page.State.SessionId != result.SessionId || page.State.DocumentGeneration > result.DocumentGeneration))
                        throw new InvalidOperationException("Browser changed during native delivery.");
                    if (_pages.TryGetValue(result.BrowserTabId, out page)) result = AcceptNativeState(page, result);
                }
            }
            ObserveAgentOpen(request, result); _publish?.Invoke(Copy(result)); return Copy(result);
        }
        private void NativeChanged(BrowserPageStateDto state)
        {
            BrowserPageStateDto value;
            lock (_sync) {
                Page page;
                if (_disposed || !_pages.TryGetValue(state.BrowserTabId, out page) || page.State.SessionId != state.SessionId || page.State.DocumentGeneration > state.DocumentGeneration) return;
                value = AcceptNativeState(page, state);
            }
            _publish?.Invoke(Copy(value));
        }
        private Task<BrowserPageStateDto> ExecuteCoreAsync(BrowserCommandPayload request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            BrowserPageStateDto result;
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(LocalBrowserPageRuntime));
                if (request == null || !Enum.IsDefined(typeof(BrowserCommandKind), request.Action) || !Key(request.BrowserTabId))
                    throw new InvalidOperationException("Требуется точная вкладка Browser.");
                Page page;
                if (request.Action == BrowserCommandKind.Open)
                {
                    if (!Key(request.OperationId) || request.OwnerScope == null) throw new InvalidOperationException("Требуется владелец и операция открытия Browser.");
                    var openKey = request.BrowserTabId + "\n" + request.OwnerScope.Kind + "\n" + request.OwnerScope.ScopeId + "\n" + request.OwnerScope.ChatId + "\n" + request.Locator;
                    if (_operations.TryGetValue(request.OperationId, out page))
                    {
                        if (page.OpenKey != openKey) throw new InvalidOperationException("Операция открытия уже принадлежит другой цели.");
                        return Task.FromResult(Copy(page.State));
                    }
                    if (_pages.ContainsKey(request.BrowserTabId)) throw new InvalidOperationException("Вкладка уже открыта; используйте её текущую session.");
                    if (_pages.Count >= 16) throw new InvalidOperationException("Открыто 16 страниц. Закройте одну перед открытием новой.");
                    page = new Page { OpenKey = openKey, State = new BrowserPageStateDto { BrowserTabId = request.BrowserTabId,
                        OperationId = request.OperationId, SessionId = Guid.NewGuid().ToString("N") } };
                    Navigate(page, request.OwnerScope, request.Locator ?? "/", true);
                    _pages.Add(request.BrowserTabId, page); _operations.Add(request.OperationId, page); _receipts.Enqueue(request.OperationId);
                    TrimReceipts();
                }
                else
                {
                    if (!_pages.TryGetValue(request.BrowserTabId, out page) || request.SessionId != page.State.SessionId)
                        throw new InvalidOperationException("Browser session закрыта или устарела.");
                    if (request.OwnerScope != null && !request.OwnerScope.Matches(page.State.OwnerScope))
                        throw new InvalidOperationException("Контекст Browser изменился.");
                    switch (request.Action)
                    {
                        case BrowserCommandKind.Attach:
                            if (request.OperationId != page.State.OperationId) throw new InvalidOperationException("Подтверждение относится к другому открытию.");
                            page.State.Attached = true; break;
                        case BrowserCommandKind.Navigate:
                            BrowserLocalLocator next, current;
                            if (page.State.Status == BrowserPageStatus.Ready && page.State.NativeHistory != null &&
                                BrowserLocalLocator.TryParse(request.Locator, out next) && BrowserLocalLocator.TryParse(page.State.Url, out current) &&
                                next.ContentLocator == current.ContentLocator && next.Query == current.Query)
                                request.NativeLocator = next.Canonical;
                            else Navigate(page, page.State.OwnerScope, request.Locator, true);
                            break;
                        case BrowserCommandKind.Home: Navigate(page, page.State.OwnerScope, "/", true); break;
                        case BrowserCommandKind.Back: Move(page, -1, request); break;
                        case BrowserCommandKind.Forward: Move(page, 1, request); break;
                        case BrowserCommandKind.Reload: Navigate(page, page.History[page.Position].Owner, page.History[page.Position].Locator, false); break;
                        case BrowserCommandKind.Close:
                            page.State.Status = BrowserPageStatus.Closed; page.State.Attached = false; _pages.Remove(request.BrowserTabId); break;
                        case BrowserCommandKind.Layout:
                            if (!ValidBounds(request.Bounds)) throw new InvalidOperationException("Некорректная геометрия Browser.");
                            if (!SameBounds(page.Bounds, request.Bounds)) { page.State.ViewportRevision++; page.Bounds = request.Bounds; }
                            break;
                        case BrowserCommandKind.Focus: case BrowserCommandKind.Stop: break;
                        case BrowserCommandKind.VisualControl: case BrowserCommandKind.ResumeAgent: if (_renderer == null) throw Unavailable(); break;
                        default: throw new InvalidOperationException("Unsupported Browser command.");
                    }
                    page.State.Revision++;
                }
                result = Copy(page.State);
            }
            return Task.FromResult(result);
        }
        private void Navigate(Page page, BrowserOwnerScope owner, string locator, bool append)
        {
            var resolved = _resolve(new BrowserResolveRequest { OwnerScope = owner, Locator = locator });
            if (resolved?.Target == null) throw new InvalidOperationException(resolved?.Message ?? "Локальная цель недоступна.");
            var state = page.State;
            state.OwnerScope = owner; state.Target = resolved.Target;
            state.Url = resolved.Locator; state.Title = resolved.Target.Title;
            state.NativeHistory = null;
            if (append) state.VisitRevision++;
            state.DocumentGeneration++; state.ControlRevision++; state.Revision++;
            state.Status = resolved.Target.Kind == BrowserTargetKind.Home ? BrowserPageStatus.Home :
                resolved.Target.State == BrowserTargetState.Removed ? BrowserPageStatus.Removed : BrowserPageStatus.Unavailable;
            state.Reason = state.Status == BrowserPageStatus.Home ? BrowserRefusalCode.None : state.Status == BrowserPageStatus.Removed ? BrowserRefusalCode.Removed :
                resolved.Reason != BrowserRefusalCode.None ? resolved.Reason : BrowserRefusalCode.NativeUnavailable;
            state.Message = state.Status == BrowserPageStatus.Home ? "" : state.Status == BrowserPageStatus.Removed ? "Эта версия удалена из каталога." :
                resolved.Message ?? (_renderer == null ? NativeUnavailable : "Загрузка локальной страницы…");
            if (_renderer == null && state.Target.Capabilities != null) { state.Target.Capabilities.Preview = state.Target.Capabilities.Interact = state.Target.Capabilities.Media = false; state.Target.Capabilities.PreviewReason = BrowserRefusalCode.NativeUnavailable; }
            if (_renderer != null && state.Target.State == BrowserTargetState.Ready && state.Target.Kind != BrowserTargetKind.Home) { state.Status = BrowserPageStatus.Starting; state.Reason = BrowserRefusalCode.None; }
            if (append)
            {
                page.History.RemoveRange(page.Position + 1, page.History.Count - page.Position - 1);
                page.History.Add(new Visit { Owner = owner, Locator = resolved.Locator });
                if (page.History.Count > 64) page.History.RemoveAt(0);
                page.Position = page.History.Count - 1;
            }
            state.CanGoBack = page.Position > 0; state.CanGoForward = page.Position + 1 < page.History.Count;
        }
        private void Move(Page page, int delta, BrowserCommandPayload request)
        {
            var index = page.Position + delta;
            if (index < 0 || index >= page.History.Count) return;
            var visit = page.History[index];
            if (visit.NativeEntry.HasValue && visit.NativeController == page.State.NativeHistory?.ControllerId && page.State.Status == BrowserPageStatus.Ready) {
                request.NativeHistoryEntryId = visit.NativeEntry; return;
            }
            var old = page.Position; page.Position = index;
            try { Navigate(page, visit.Owner, visit.Locator, false); }
            catch { page.Position = old; throw; }
            page.State.VisitRevision++;
        }
        private void TrimReceipts()
        {
            var remaining = _receipts.Count;
            while (_receipts.Count > 128 && remaining-- > 0)
            {
                var key = _receipts.Dequeue(); var item = _operations[key];
                if (item.State.Status == BrowserPageStatus.Closed) _operations.Remove(key); else _receipts.Enqueue(key);
            }
        }
        private static bool Key(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl);
        public static bool ValidBounds(BrowserBoundsDto bounds)
        {
            if (bounds == null) return false;
            if (!bounds.Visible) return true;
            var values = new[] { bounds.X, bounds.Y, bounds.Width, bounds.Height, bounds.ViewportWidth, bounds.ViewportHeight };
            return values.All(value => !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0 && value <= 32768) &&
                bounds.Width >= 1 && bounds.Height >= 1 && bounds.ViewportWidth >= 1 && bounds.ViewportHeight >= 1 &&
                bounds.X + bounds.Width <= bounds.ViewportWidth + 1 && bounds.Y + bounds.Height <= bounds.ViewportHeight + 1;
        }
        private static bool SameBounds(BrowserBoundsDto a, BrowserBoundsDto b) => a != null && a.Visible == b.Visible && a.X == b.X && a.Y == b.Y &&
            a.Width == b.Width && a.Height == b.Height && a.ViewportWidth == b.ViewportWidth && a.ViewportHeight == b.ViewportHeight;
        private static BrowserPageStateDto Copy(BrowserPageStateDto value)
        {
            var copy = Newtonsoft.Json.JsonConvert.DeserializeObject<BrowserPageStateDto>(Newtonsoft.Json.JsonConvert.SerializeObject(value));
            copy.NativeHistory = value.NativeHistory?.Copy(); return copy;
        }
        public IReadOnlyList<BrowserPageHandle> TargetsFor(string chatId) => _renderer?.TargetsFor(chatId) ?? new BrowserPageHandle[0];
        public Task<BrowserObservation> ReadAsync(BrowserPageHandle target, CancellationToken token) => _renderer?.ReadAsync(target, token) ?? throw Unavailable();
        public Task PrepareAsync(BrowserPageHandle target, string revision, string preparation, BrowserActionRequest request, CancellationToken token) => _renderer?.PrepareAsync(target, revision, preparation, request, token) ?? throw Unavailable();
        public Task<BrowserActionResult> ActAsync(BrowserPageHandle target, string preparation, Action dispatch, CancellationToken token, bool navigation = false) => _renderer?.ActAsync(target, preparation, dispatch, token, navigation) ?? throw Unavailable();
        public void BlockActions(BrowserPageHandle target) { _renderer?.BlockActions(target); }
        public void PresentResult(BrowserPageHandle target, string preparation, BrowserActionEffect effect) { _renderer?.PresentResult(target, preparation, effect); }
        public Task<BrowserMediaCapture> ReadMediaAsync(BrowserPageHandle target, CancellationToken token) => _renderer?.ReadMediaAsync(target, token) ?? throw Unavailable();
        public async Task<BrowserAnnotationCapture> CaptureAnnotationAsync(BrowserAnnotationRequest request, CancellationToken token)
        {
            await _commands.WaitAsync(token).ConfigureAwait(false);
            try {
                lock (_sync) {
                    Page page;
                    if (_disposed || request?.BrowserTabId == null || !_pages.TryGetValue(request.BrowserTabId, out page) ||
                        page.State.SessionId != request.SessionId || page.State.DocumentGeneration != request.Generation ||
                        page.State.Status != BrowserPageStatus.Ready || !page.State.Attached)
                        throw new InvalidOperationException("Страница аннотации изменилась. Выберите элемент заново.");
                }
                var source = _renderer as IBrowserAnnotationSource;
                if (source == null) throw Unavailable();
                return await source.CaptureAnnotationAsync(request, token).ConfigureAwait(false);
            } finally { _commands.Release(); }
        }
        public bool IsCaptureCurrent(BrowserMediaCapture capture) => _renderer?.IsCaptureCurrent(capture) == true;
        private static InvalidOperationException Unavailable() => new InvalidOperationException(NativeUnavailable);
        private Task _nativeReset = Task.CompletedTask;
        public void Reset()
        {
            BrowserPageStateDto[] closed;
            lock (_sync) { _openEpoch++;
                foreach (var operation in _agentOpens.Values.Where(o => !o.Completion.Task.IsCompleted)) operation.Completion.TrySetException(new InvalidOperationException("The Browser owner reset."));
                closed = _pages.Values.Select(page => { page.State.Status = BrowserPageStatus.Closed;
                    page.State.Attached = false; page.State.Revision++; return Copy(page.State); }).ToArray(); _pages.Clear(); }
            if (_renderer != null) _nativeReset = _renderer.ResetAsync();
            foreach (var state in closed) {
                ObserveAgentOpen(new BrowserCommandPayload { Action = BrowserCommandKind.Close }, state); _publish?.Invoke(state);
            }
        }
        public async Task ResetAsync() { Reset(); await _nativeReset.ConfigureAwait(false); }
        public void Dispose() { lock (_sync) { if (_disposed) return; _disposed = true; } Reset(); if (_renderer != null) { _renderer.Changed -= NativeChanged; _renderer.Dispose(); } }
    }
}
