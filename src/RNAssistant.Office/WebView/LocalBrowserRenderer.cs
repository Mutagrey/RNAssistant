using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;

namespace RNAssistant.Office.WebView
{
    internal sealed partial class LocalBrowserRenderer : IBrowserRenderer, IBrowserAnnotationSource
    {
        private readonly Control _parent;
        private readonly WebView2 _shell;
        private readonly LocalBrowserEnvironment _environment;
        private readonly PluginApplicationService _plugins;
        private readonly string _assets;
        private readonly Func<BrowserTargetDescriptor, CancellationToken, BrowserContentSession> _content;
        private readonly SynchronizationContext _ui;
        private readonly Dictionary<string, LocalBrowserPage> _pages = new Dictionary<string, LocalBrowserPage>(StringComparer.Ordinal);
        private readonly Dictionary<string, LocalBrowserPage.ManualOwner> _manual = new Dictionary<string, LocalBrowserPage.ManualOwner>(StringComparer.Ordinal);
        private readonly SemaphoreSlim _dispatch = new SemaphoreSlim(1, 1);
        private bool _closed;
        private readonly LocalBrowserPhysicalInput _physical;
        private readonly Func<BrowserContentSession, BrowserResourceBinding> _bindings;
        private readonly Func<string, string, Task<Action>> _pauseRun;
        public event Action<BrowserPageStateDto> Changed;
        internal Action<BrowserDockOpenRequest> OpenTab { get; set; }
        internal LocalBrowserRenderer(Control parent, WebView2 shell, LocalBrowserEnvironment environment,
            PluginApplicationService plugins, string assets, Func<BrowserTargetDescriptor, CancellationToken, BrowserContentSession> content,
            Func<BrowserContentSession, BrowserResourceBinding> bindings, Func<string, string, Task<Action>> pauseRun)
        {
            _bindings = bindings; _pauseRun = pauseRun;
            try { _physical = new LocalBrowserPhysicalInput((x, y, keyboard) => {
                foreach (var page in _pages.Values) if (page.Ready && page.Control.Visible &&
                    LocalBrowserPhysicalInput.GetAncestor(page.Control.Handle, 2) == LocalBrowserPhysicalInput.GetForegroundWindow() &&
                    (keyboard ? page.NativeFocused : page.Control.RectangleToScreen(page.Control.ClientRectangle).Contains(x, y))) { _captures = new BrowserMediaCapture[0]; page.PhysicalInput(); }
            }); } catch (InvalidOperationException) { /* Manual page execution remains available; agent input requires physical attribution. */ }
            _parent = parent; _shell = shell; _environment = environment; _plugins = plugins; _assets = assets; _content = content;
            _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Browser UI dispatcher is required.");
            parent.Resize += InvalidatePlacement; shell.SizeChanged += InvalidatePlacement; shell.LocationChanged += InvalidatePlacement; shell.ZoomFactorChanged += InvalidatePlacement; shell.VisibleChanged += InvalidatePlacement;
        }
        private Task<T> OnUi<T>(Func<Task<T>> action)
        {
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ui.Post(async ignored => { try { result.TrySetResult(await action()); } catch (OperationCanceledException) { result.TrySetCanceled(); } catch (Exception error) { result.TrySetException(error); } }, null);
            return result.Task;
        }
        public async Task<BrowserPageStateDto> UpdateAsync(BrowserCommandPayload command, BrowserPageStateDto state, CancellationToken token)
        {
            await _dispatch.WaitAsync(token).ConfigureAwait(false);
            try { return await OnUi(async () => {
                token.ThrowIfCancellationRequested();
                if (_closed) throw new ObjectDisposedException(nameof(LocalBrowserRenderer));
                LocalBrowserPage page; _pages.TryGetValue(state.BrowserTabId, out page);
                if (page != null && (!page.State.OwnerScope.Matches(state.OwnerScope) || page.State.ChatBound != state.ChatBound))
                    _manual.Remove(state.SessionId); // A previous chat's paused run is never resumed by the new binding.
                if (page != null && page.State.SessionId == state.SessionId && page.State.DocumentGeneration > state.DocumentGeneration) {
                    if (command.Action != BrowserCommandKind.Layout && command.Action != BrowserCommandKind.Focus && command.Action != BrowserCommandKind.Stop)
                        throw new InvalidOperationException("Browser changed before native delivery.");
                    state = page.State;
                }
                if (page != null && page.State.SessionId == state.SessionId &&
                    (command.Action == BrowserCommandKind.Layout || command.Action == BrowserCommandKind.Focus || command.Action == BrowserCommandKind.Stop ||
                     command.Action == BrowserCommandKind.VisualControl || command.Action == BrowserCommandKind.ResumeAgent)) {
                    page.State.ViewportRevision = Math.Max(page.State.ViewportRevision, state.ViewportRevision);
                    state = page.State;
                }
                if (command.NativeHistoryEntryId.HasValue || command.NativeLocator != null) {
                    if (page == null || page.State.SessionId != state.SessionId) throw new InvalidOperationException("Native history is unavailable.");
                    page.State = state;
                    await page.NavigateHistoryAsync(command.NativeHistoryEntryId, command.NativeLocator, token);
                    state = page.State;
                }
                if (page != null && (state.Status == BrowserPageStatus.Closed || state.Status == BrowserPageStatus.Home ||
                    state.Target?.State != BrowserTargetState.Ready || page.State.SessionId != state.SessionId ||
                    command.Action == BrowserCommandKind.Reload || page.State.DocumentGeneration != state.DocumentGeneration)) {
                    _pages.Remove(state.BrowserTabId); page.Dispose(); await page.Drained; page = null;
                }
                if (state.Status == BrowserPageStatus.Closed) _manual.Remove(state.SessionId);
                if (state.Status == BrowserPageStatus.Closed || state.Status == BrowserPageStatus.Home || state.Target?.State != BrowserTargetState.Ready) { RefreshHandles(); return state; }
                if (page == null && state.Status == BrowserPageStatus.Unavailable && command.Action != BrowserCommandKind.Attach &&
                    command.Action != BrowserCommandKind.Reload && command.Action != BrowserCommandKind.Navigate && command.Action != BrowserCommandKind.NavigateUser && command.Action != BrowserCommandKind.Bind && command.Action != BrowserCommandKind.Back && command.Action != BrowserCommandKind.Forward) return state;
                if (page == null && state.Attached) {
                    page = new LocalBrowserPage(_parent, _environment, _plugins, _assets, state, _content, _bindings, _pauseRun) { InputAvailable = _physical != null };
                    LocalBrowserPage.ManualOwner manual;
                    if (!_manual.TryGetValue(state.SessionId, out manual)) _manual.Add(state.SessionId, manual = new LocalBrowserPage.ManualOwner());
                    page.Manual = manual;
                    _pages.Add(state.BrowserTabId, page); page.Changed += Publish;
                    page.FindRequested += () => { if (!_closed && _shell.Visible) _shell.Focus(); };
                    page.NewTabRequested += locator => OpenTab?.Invoke(new BrowserDockOpenRequest { BrowserTabId = Guid.NewGuid().ToString("N"), OperationId = Guid.NewGuid().ToString("N"), OwnerScope = state.OwnerScope, Locator = locator, Title = state.Title });
                    try { await page.InitializeAsync(token); }
                    catch { _pages.Remove(state.BrowserTabId); page.Dispose(); await page.Drained; throw; }
                }
                if (page != null) {
                    page.State = state;
                    if (command.Action == BrowserCommandKind.Stop) page.Core.Stop();
                    if (command.Action == BrowserCommandKind.Focus) page.Focus();
                    if (command.Action == BrowserCommandKind.ResumeAgent) page.ResumeAgent(command.ControlRevision);
                    if (command.Action == BrowserCommandKind.VisualControl) await page.VisualControlAsync(command.Visual);
                    if (command.Bounds != null) Place(page, command.Bounds);
                    if (page.HasLoaded) state.Status = BrowserPageStatus.Ready; state.Reason = BrowserRefusalCode.None; state.Message = _physical == null ? "Страница работает. Ввод агента недоступен: не установлен контроль физического ввода." : null;
                    state.Target.Capabilities.Preview = true; state.Target.Capabilities.Interact = _physical != null && page.HasLoaded;
                    state.Target.Capabilities.Media = page.HasLoaded; state.Target.Capabilities.PreviewReason = BrowserRefusalCode.None;
                    state.AnnotationContext = page.AnnotationContext(); state.Target.Capabilities.Annotations = page.HasLoaded && state.AnnotationContext != null;
                    state.Target.ContentRevision = page.Content.Target.ContentRevision;
                    if (state.Target.RelativePath == null) state.Target.RelativePath = page.Content.EntryPath;
                    state.Target.EntryPath = state.Target.RelativePath;
                }
                RefreshHandles(); return state;
            }).ConfigureAwait(false); }
            finally { _dispatch.Release(); }
        }
        private void Place(LocalBrowserPage page, BrowserBoundsDto bounds)
        {
            var previous = page.Bounds;
            if (previous != null && previous.Visible == bounds.Visible && previous.X == bounds.X && previous.Y == bounds.Y && previous.Width == bounds.Width && previous.Height == bounds.Height &&
                previous.ViewportWidth == bounds.ViewportWidth && previous.ViewportHeight == bounds.ViewportHeight && page.Control.Visible == bounds.Visible) return;
            page.Bounds = bounds; page.State.ViewportRevision++;
            page.Control.Visible = false; page.HideAction();
            if (!bounds.Visible || !_parent.Visible || !_shell.Visible || bounds.ViewportWidth <= 0 || bounds.ViewportHeight <= 0) return;
            var scale = _shell.ClientSize.Width / bounds.ViewportWidth;
            var vertical = _shell.ClientSize.Height / bounds.ViewportHeight;
            if (scale <= 0 || Math.Abs(scale - vertical) > 0.05) return;
            var rectangle = Rectangle.FromLTRB(_shell.Left + (int)Math.Ceiling(bounds.X * scale), _shell.Top + (int)Math.Ceiling(bounds.Y * scale),
                _shell.Left + (int)Math.Floor((bounds.X + bounds.Width) * scale), _shell.Top + (int)Math.Floor((bounds.Y + bounds.Height) * scale));
            if (rectangle.Width < 1 || rectangle.Height < 1 || !_shell.Bounds.Contains(rectangle)) return;
            page.Control.Bounds = rectangle; page.Controller.Bounds = new Rectangle(Point.Empty, rectangle.Size);
            page.Controller.ZoomFactor = _shell.ZoomFactor; page.Control.BringToFront(); page.Control.Visible = true;
        }
        private void InvalidatePlacement(object sender, EventArgs args)
        { foreach (var page in _pages.Values) { page.Control.Visible = false; page.State.ViewportRevision++; Publish(page.State); } }
        internal void ResourceChanged(RNAssistant.Office.Contracts.ResourceChangedMessage change)
        { _ui.Post(ignored => { if (!_closed) foreach (var page in _pages.Values) page.ResourceChanged(change); }, null); }
        private void Publish(BrowserPageStateDto state) { RefreshHandles(); if (!_closed) Changed?.Invoke(state); }
        public async Task<BrowserAnnotationCapture> CaptureAnnotationAsync(BrowserAnnotationRequest request, CancellationToken token)
        {
            await _dispatch.WaitAsync(token).ConfigureAwait(false);
            try { return await OnUi(() => {
                token.ThrowIfCancellationRequested();
                LocalBrowserPage page;
                if (_closed || !_pages.TryGetValue(request.BrowserTabId, out page))
                    throw new InvalidOperationException("Страница аннотации закрыта.");
                return Task.FromResult(page.CaptureAnnotation(request));
            }).ConfigureAwait(false); } finally { _dispatch.Release(); }
        }
        public IReadOnlyList<BrowserPageHandle> TargetsFor(string chatId)
        { return _handles.Where(handle => handle.ChatId == chatId).ToArray(); }
        private LocalBrowserPage Exact(BrowserPageHandle handle)
        {
            LocalBrowserPage page;
            if (_closed || handle == null || !_pages.TryGetValue(handle.BrowserTabId, out page) || !page.Ready || !page.State.ChatBound || !page.Handle.Matches(handle))
                throw new InvalidOperationException("The native Browser binding changed.");
            page.Content.Validate(); return page;
        }
        public Task ResetAsync() => OnUi(async () => { var pages = _pages.Values.ToArray(); _pages.Clear(); _manual.Clear(); RefreshHandles(); foreach (var page in pages) page.Dispose(); await Task.WhenAll(pages.Select(page => page.Drained)); return true; });
        public void Dispose()
        {
            if (_closed) return; _closed = true; _physical?.Dispose();
            _parent.Resize -= InvalidatePlacement; _shell.SizeChanged -= InvalidatePlacement; _shell.LocationChanged -= InvalidatePlacement; _shell.ZoomFactorChanged -= InvalidatePlacement; _shell.VisibleChanged -= InvalidatePlacement;
            foreach (var page in _pages.Values) page.Dispose(); _pages.Clear(); _manual.Clear();
        }
    }
}
