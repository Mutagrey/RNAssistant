using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Application.Contracts;

namespace RNAssistant.Application
{
    // The host's sole local tab/session owner. No renderer is created while B02 is open.
    // Source-only navigation still has exact scope, bounded history and attach receipts.
    public sealed class LocalBrowserPageRuntime : IBrowserPageRuntime, IDisposable
    {
        private sealed class Visit { public BrowserOwnerScope Owner; public string Locator; }
        private sealed class Page
        {
            public BrowserPageStateDto State;
            public string OpenKey;
            public List<Visit> History = new List<Visit>();
            public int Position = -1;
            public BrowserBoundsDto Bounds;
        }
        private readonly object _sync = new object();
        private readonly Dictionary<string, Page> _pages = new Dictionary<string, Page>(StringComparer.Ordinal);
        private readonly Dictionary<string, Page> _operations = new Dictionary<string, Page>(StringComparer.Ordinal);
        private readonly Queue<string> _receipts = new Queue<string>();
        private readonly Func<BrowserResolveRequest, BrowserResolveResult> _resolve;
        private readonly Action<BrowserPageStateDto> _publish;
        private bool _disposed;
        public LocalBrowserPageRuntime(Func<BrowserResolveRequest, BrowserResolveResult> resolve, Action<BrowserPageStateDto> publish)
        { _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve)); _publish = publish; }

        public Task<BrowserPageStateDto> ExecuteAsync(BrowserCommandPayload request, CancellationToken token)
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
                        case BrowserCommandKind.Navigate: Navigate(page, page.State.OwnerScope, request.Locator, true); break;
                        case BrowserCommandKind.Home: Navigate(page, page.State.OwnerScope, "/", true); break;
                        case BrowserCommandKind.Back: Move(page, -1); break;
                        case BrowserCommandKind.Forward: Move(page, 1); break;
                        case BrowserCommandKind.Reload: Navigate(page, page.History[page.Position].Owner, page.History[page.Position].Locator, false); break;
                        case BrowserCommandKind.Close:
                            page.State.Status = BrowserPageStatus.Closed; page.State.Attached = false; _pages.Remove(request.BrowserTabId); break;
                        case BrowserCommandKind.Layout:
                            if (!ValidBounds(request.Bounds)) throw new InvalidOperationException("Некорректная геометрия Browser.");
                            if (!SameBounds(page.Bounds, request.Bounds)) { page.State.ViewportRevision++; page.Bounds = request.Bounds; }
                            break;
                        case BrowserCommandKind.Focus: case BrowserCommandKind.Stop: break;
                        default: throw new InvalidOperationException(BrowserIsolationPolicy.Message);
                    }
                    page.State.Revision++;
                }
                result = Copy(page.State);
            }
            _publish?.Invoke(result);
            return Task.FromResult(result);
        }
        private void Navigate(Page page, BrowserOwnerScope owner, string locator, bool append)
        {
            var resolved = _resolve(new BrowserResolveRequest { OwnerScope = owner, Locator = locator });
            if (resolved?.Target == null) throw new InvalidOperationException(resolved?.Message ?? "Локальная цель недоступна.");
            var state = page.State;
            state.OwnerScope = owner; state.Target = BrowserIsolationPolicy.SourceOnly(resolved.Target);
            state.Url = resolved.Locator; state.Title = resolved.Target.Title;
            state.DocumentGeneration++; state.ControlRevision++; state.Revision++;
            state.Status = resolved.Target.Kind == BrowserTargetKind.Home ? BrowserPageStatus.Home :
                resolved.Target.State == BrowserTargetState.Removed ? BrowserPageStatus.Removed : BrowserPageStatus.Unavailable;
            state.Reason = state.Status == BrowserPageStatus.Home ? BrowserRefusalCode.None : state.Status == BrowserPageStatus.Removed ? BrowserRefusalCode.Removed :
                resolved.Reason != BrowserRefusalCode.None ? resolved.Reason : BrowserIsolationPolicy.ExecutionRefusal;
            state.Message = state.Status == BrowserPageStatus.Home ? "" : state.Status == BrowserPageStatus.Removed ? "Эта версия удалена из каталога." :
                resolved.Message ?? BrowserIsolationPolicy.Message;
            if (append)
            {
                page.History.RemoveRange(page.Position + 1, page.History.Count - page.Position - 1);
                page.History.Add(new Visit { Owner = owner, Locator = resolved.Locator });
                if (page.History.Count > 64) page.History.RemoveAt(0);
                page.Position = page.History.Count - 1;
            }
            state.CanGoBack = page.Position > 0; state.CanGoForward = page.Position + 1 < page.History.Count;
        }
        private void Move(Page page, int delta)
        {
            var index = page.Position + delta;
            if (index < 0 || index >= page.History.Count) return;
            var visit = page.History[index]; var old = page.Position; page.Position = index;
            try { Navigate(page, visit.Owner, visit.Locator, false); }
            catch { page.Position = old; throw; }
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
        private static BrowserPageStateDto Copy(BrowserPageStateDto value) => Newtonsoft.Json.JsonConvert.DeserializeObject<BrowserPageStateDto>(Newtonsoft.Json.JsonConvert.SerializeObject(value));
        public IReadOnlyList<BrowserPageHandle> TargetsFor(string chatId) => new BrowserPageHandle[0];
        public Task<BrowserObservation> ReadAsync(BrowserPageHandle target, CancellationToken token) { throw Unavailable(); }
        public Task PrepareAsync(BrowserPageHandle target, string revision, string preparation, BrowserActionRequest request, CancellationToken token) { throw Unavailable(); }
        public Task<BrowserActionResult> ActAsync(BrowserPageHandle target, string preparation, Action dispatch, CancellationToken token, bool navigation = false) { throw Unavailable(); }
        public void BlockActions(BrowserPageHandle target) { }
        public void PresentResult(BrowserPageHandle target, string preparation, BrowserActionEffect effect) { }
        private static InvalidOperationException Unavailable() => new InvalidOperationException(BrowserIsolationPolicy.Message);
        public void Reset()
        {
            BrowserPageStateDto[] closed;
            lock (_sync) { closed = _pages.Values.Select(page => { page.State.Status = BrowserPageStatus.Closed;
                    page.State.Attached = false; page.State.Revision++; return Copy(page.State); }).ToArray(); _pages.Clear(); }
            foreach (var state in closed) _publish?.Invoke(state);
        }
        public Task ResetAsync() { Reset(); return Task.CompletedTask; }
        public void Dispose() { lock (_sync) { if (_disposed) return; _disposed = true; } Reset(); }
    }
}
