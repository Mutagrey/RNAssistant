using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Diagnostics;
using RNAssistant.Office.Services;
using RNAssistant.Office.WebView;

namespace RNAssistant.OfficeHosts.Windows
{
    // Created on the launcher's Office STA. The only owner of native presentation and session lifetime.
    public sealed class InProcessWindowHost : IAssistantWindowHost, IDisposable
    {
        private readonly AssistantHostForm _form;
        private readonly CompanionForm _companion;
        private readonly AssistantWindowPolicy _policy = new AssistantWindowPolicy();
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly System.Windows.Forms.Timer _focus = new System.Windows.Forms.Timer { Interval = 150 };
        private readonly System.Windows.Forms.Timer _save = new System.Windows.Forms.Timer { Interval = 350 };
        private readonly int _hostKind;
        private readonly string _root;
        private readonly uint _anchorThread, _anchorProcess;
        private IntPtr _anchor;
        private InProcessPanelSession _session;
        private AssistantWindowPreferenceStore _store;
        private AssistantWindowPreferences _preferences = new AssistantWindowPreferences();
        private volatile HostWindowBootstrapDto _snapshot;
        private bool _applying, _chromeSupported, _chromeReady, _captureProtected = true, _disposed;
        private bool _fullPlaced, _placed, _saveRunning, _saveAgain;
        private string _diagnostic, _deferredRequest;
        private long _deferredRevision;
        private HostWindowCommand? _deferredCommand;
        private long _focusRevision = -1;
        private IntPtr _focusCandidate;
        private DateTime _focusSince, _lastActivity;
        private Point _anchorOffset;
        private Rectangle _lastAnchorBounds;

        public bool IsDisposed { get { return _disposed; } }
        public HostWindowBootstrapDto Snapshot { get { return _snapshot; } }
        public event Action<HostWindowStateDto> StateChanged;
        public event Action ShutdownRequested;

        public static InProcessWindowHost Create(int hostKind, long officeHwnd, string rootPath)
        { return new InProcessWindowHost(hostKind, (IntPtr)officeHwnd, rootPath); }

        private InProcessWindowHost(int hostKind, IntPtr anchor, string root)
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("RNAssistant window must be created on the Office UI STA.");
            _hostKind = hostKind; _root = Path.GetFullPath(root); _anchor = anchor;
            _anchorThread = WindowNative.GetWindowThreadProcessId(anchor, out _anchorProcess);
            if (_anchorThread == 0) throw new ArgumentException("Office HWND is unavailable.");
            _form = new AssistantHostForm(); _companion = new CompanionForm();
            try
            {
                Publish();
                _form.HandleCreated += (s, e) => Protect(_form);
                _companion.HandleCreated += (s, e) => Protect(_companion);
                var handle = _form.Handle; // Stable HWND before attaching the retained pane.
                _form.Command = NativeCommand;
                _form.PlacementChanged = CapturePlacement;
                _form.NativeStateChanged = ObserveNativeState;
                _form.FormClosed += (s, e) => Dispose();
                _companion.OpenRequested = Resume;
                _companion.Command = NativeCommand;
                _companion.PlacementChanged = CaptureCompanion;
                _companion.ShutdownRequested = () => { Resume(); ShutdownRequested?.Invoke(); };
                _session = InProcessPanelSession.Create(hostKind, anchor.ToInt64(), _root, this);
                _session.ScreenCaptureProtectionChanged += SetProtection;
                _session.PanelWindowWidthChanged += SetInitialWidth;
                SetProtection(_session.ScreenCaptureProtectionEnabled);
                _store = new AssistantWindowPreferenceStore(AppDataPaths.CreateDefault(), "inprocess-" + hostKind);
                try
                {
                    _preferences = _store.Load();
                    _policy.Load(_preferences.Last, _preferences.BeforeMaximize, _preferences.Pinned, _preferences.AutoCollapse);
                    _fullPlaced = _preferences.Full != null && _preferences.Full.Valid;
                }
                catch (Exception error) { _preferences = new AssistantWindowPreferences(); Diagnostic("Оконные настройки сброшены", error); }
                _form.Controls.Add(_session.PanelControl);
                _focus.Tick += CheckFocus;
                _save.Tick += (s, e) => { _save.Stop(); SavePreferences(); };
                ApplyPresentation(false);
                _focus.Start();
            }
            catch { Dispose(); throw; }
        }

        // A different launcher HWND is invocation context, never an instruction to dispose/rebind a run.
        public void Show(int hostKind, long officeHwnd, string root)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(InProcessWindowHost));
            if (!_policy.Alive) throw new InvalidOperationException("Сеанс RNAssistant завершается.");
            if (hostKind != _hostKind || !string.Equals(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Уже открыт другой профиль/host RNAssistant. Сначала явно завершите прежний сеанс.");
            EnsureOwnerThread(); Resume();
        }
        public void SetVisible(bool visible)
        {
            EnsureOwnerThread();
            if (_disposed || !_policy.Alive) throw new InvalidOperationException("Сеанс RNAssistant завершается.");
            if (visible) Resume(); else Execute(new HostWindowCommandPayload { Command = HostWindowCommand.Dismiss });
        }
        public void Shutdown() { EnsureOwnerThread(); Dispose(); }
        private void EnsureOwnerThread()
        { if (!_form.IsDisposed && _form.InvokeRequired) throw new InvalidOperationException("Call the window host on its Office UI thread."); }
        private void OnUi(Action action)
        {
            if (_form.IsDisposed) return;
            if (_form.InvokeRequired) _form.BeginInvoke(action); else action();
        }
        private void Resume()
        {
            if (_disposed || !_policy.Alive) return;
            _policy.Resume(); _focusRevision = -1; ApplyPresentation(true);
        }

        public Task<HostWindowCommandResponse> ExecuteAsync(HostWindowCommandPayload request, string requestId)
        {
            var completion = new TaskCompletionSource<HostWindowCommandResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_disposed) { completion.SetException(new ObjectDisposedException(nameof(InProcessWindowHost))); return completion.Task; }
            var cancellation = _lifetime.Token.Register(() => completion.TrySetCanceled());
            completion.Task.ContinueWith(ignored => cancellation.Dispose(), TaskContinuationOptions.ExecuteSynchronously);
            try { OnUi(() =>
            {
                try
                {
                    if (_disposed) throw new ObjectDisposedException(nameof(InProcessWindowHost));
                    if (request == null || !Enum.IsDefined(typeof(HostWindowCommand), request.Command))
                        throw new ArgumentException("Unknown window command.");
                    if (_deferredCommand.HasValue) throw new InvalidOperationException("Предыдущая оконная команда ещё выполняется.");
                    if (request.Command == HostWindowCommand.ChromeReady && !_chromeSupported)
                        throw new InvalidOperationException("WebView Runtime не поддерживает встроенный заголовок.");
                    if (request.Command == HostWindowCommand.Shutdown)
                    {
                        var pane = _session.PanelControl as AssistantPaneControl;
                        if (pane == null || !pane.CanShutdownWindow)
                            throw new InvalidOperationException("Сначала остановите активные запросы и дождитесь завершения операций.");
                        _session.Runtime.Controller.EnsureHostSwitchReady();
                    }
                    if (request.Command == HostWindowCommand.Dismiss || request.Command == HostWindowCommand.Shutdown)
                    {
                        if (string.IsNullOrEmpty(requestId) || request.Value.HasValue) throw new ArgumentException("Invalid window command envelope.");
                        // The response is posted by AssistantPaneControl before we hide/dispose either HWND.
                        _deferredRequest = requestId; _deferredCommand = request.Command; _policy.Touch(); Publish();
                        if (request.Command == HostWindowCommand.Shutdown) { RecordPlacement(false); _policy.Apply(request); Publish(); }
                        _deferredRevision = _policy.Revision;
                    }
                    else Execute(request);
                    completion.TrySetResult(new HostWindowCommandResponse { Accepted = true, State = _snapshot.State });
                }
                catch (Exception error) { completion.TrySetException(error); }
            }); }
            catch (Exception error) { completion.TrySetException(error); }
            return completion.Task;
        }
        public void ResponsePosted(string requestId)
        {
            if (_disposed || requestId != _deferredRequest) return;
            _form.BeginInvoke(new Action(() =>
            {
                if (_disposed || requestId != _deferredRequest || !_deferredCommand.HasValue) return;
                var command = _deferredCommand.Value; _deferredCommand = null; _deferredRequest = null;
                if (command == HostWindowCommand.Shutdown) Dispose();
                else if (_deferredRevision == _policy.Revision) NativeCommand(command);
            }));
        }
        private void NativeCommand(HostWindowCommand command)
        {
            if (_disposed || !_policy.Alive) return;
            try { Execute(new HostWindowCommandPayload { Command = command }); }
            catch (Exception error) { Diagnostic("Оконная команда не выполнена", error); Publish(); }
        }
        private void Execute(HostWindowCommandPayload request)
        {
            RecordPlacement(false);
            _policy.Apply(request); _focusRevision = -1;
            if (request.Command == HostWindowCommand.Shutdown) { Dispose(); return; }
            if (request.Command == HostWindowCommand.ChromeReady) { _chromeReady = true; _form.SetIntegratedChrome(true); }
            if (request.Command == HostWindowCommand.ResetPlacement)
            {
                _preferences.Full = _preferences.Compact = _preferences.Companion = null; _fullPlaced = false;
                PositionCompanion(true);
            }
            ApplyPresentation(request.Command == HostWindowCommand.ShowFull || request.Command == HostWindowCommand.ShowCompact ||
                request.Command == HostWindowCommand.Restore || request.Command == HostWindowCommand.Maximize);
            ScheduleSave();
        }

        public void ConfigureChrome(bool supported)
        { OnUi(() => { _chromeSupported = supported; _chromeReady = false; _form.SetIntegratedChrome(false); _policy.Touch(); Publish(); }); }
        public void InvalidateChrome()
        { OnUi(() => { if (_disposed) return; _chromeReady = false; _form.SetIntegratedChrome(false); _policy.Touch(); Publish(); }); }

        private void ApplyPresentation(bool activate)
        {
            if (_disposed) return;
            _applying = true;
            try
            {
                var visibility = _policy.Visibility;
                if (visibility == HostWindowVisibility.Dismissed)
                {
                    _form.Hide(); UpdateCompanion();
                }
                else if (_policy.Presentation == HostWindowPresentation.Companion)
                {
                    // Remove the surface, retaining its taskbar/Alt+Tab entry as a standard return path.
                    // The policy distinguishes parking from an explicit system minimize (no capsule).
                    if (WindowNative.BelongsTo(WindowNative.GetForegroundWindow(), _form.Handle)) _form.WindowState = FormWindowState.Minimized;
                    else WindowNative.ShowWindow(_form.Handle, 7); // SW_SHOWMINNOACTIVE: automatic parking never steals foreground
                    UpdateCompanion();
                }
                else
                {
                    _companion.Hide();
                    var isCompact = _policy.Presentation == HostWindowPresentation.Compact;
                    UpdateMinimumSize();
                    if (!_placed && _policy.Presentation == HostWindowPresentation.FullMaximized)
                    {
                        var fromCompact = _policy.RestoreBeforeMaximize == HostWindowPresentation.Compact;
                        _form.Bounds = RestoreRectangle(fromCompact ? _preferences.Compact : _preferences.Full, fromCompact);
                    }
                    if (_policy.Presentation != HostWindowPresentation.FullMaximized)
                    {
                        _form.WindowState = FormWindowState.Normal;
                        _form.Bounds = RestoreRectangle(isCompact ? _preferences.Compact : _preferences.Full, isCompact);
                    }
                    else if (_form.WindowState != FormWindowState.Maximized) _form.WindowState = FormWindowState.Maximized;
                    if (!_form.Visible) _form.Show(); // deliberately unowned, ordinary z-order and taskbar
                    _placed = true;
                    if (visibility == HostWindowVisibility.SystemMinimized) _form.WindowState = FormWindowState.Minimized;
                    else if (activate) { _form.BringToFront(); _form.Activate(); }
                }
            }
            finally { _applying = false; }
            Publish();
        }

        private Rectangle RestoreRectangle(AssistantWindowRectangle saved, bool compact)
        {
            var screen = saved == null ? null : Screen.AllScreens.FirstOrDefault(s => s.DeviceName == saved.Monitor);
            screen = screen ?? Screen.FromHandle(!_placed && _anchor != IntPtr.Zero ? _anchor : _form.Handle);
            var area = screen.WorkingArea;
            // Establish the destination DPI context before converting logical saved bounds.
            // Unaware/system-aware Office keeps its existing virtualized coordinate space.
            if (WindowNative.PerMonitorAware(_form.Handle) && Screen.FromControl(_form).DeviceName != screen.DeviceName)
                _form.Location = area.Location;
            var scale = WindowNative.Scale(_form.Handle);
            var width = compact ? (int)(420 * scale) : _session == null ? 1100 : _session.PanelWindowWidth;
            var height = (int)((compact ? 520 : 720) * scale);
            var bounds = new Rectangle(area.Right - width - (int)(20 * scale), area.Top + (int)(80 * scale), width, height);
            if (saved != null && saved.Valid) bounds = new Rectangle(area.Left + (int)(saved.X * scale), area.Top + (int)(saved.Y * scale),
                Math.Max(_form.MinimumSize.Width, (int)(saved.Width * scale)), Math.Max(_form.MinimumSize.Height, (int)(saved.Height * scale)));
            return WindowNative.Clamp(bounds, area);
        }
        private AssistantWindowRectangle Record(Form form)
        {
            var screen = Screen.FromControl(form); var area = screen.WorkingArea; var scale = WindowNative.Scale(form.Handle);
            return new AssistantWindowRectangle { Monitor = screen.DeviceName, X = (form.Left - area.Left) / scale,
                Y = (form.Top - area.Top) / scale, Width = form.Width / scale, Height = form.Height / scale };
        }
        private void CapturePlacement()
        { if (!_applying && !_disposed) UpdateMinimumSize(); RecordPlacement(true); }
        private void UpdateMinimumSize()
        {
            var area = Screen.FromHandle(_form.Handle).WorkingArea; var scale = WindowNative.Scale(_form.Handle);
            _form.MinimumSize = new Size(Math.Min(area.Width, (int)((_policy.Presentation == HostWindowPresentation.Compact ? 360 : 420) * scale)),
                Math.Min(area.Height, (int)(320 * scale)));
        }
        private void RecordPlacement(bool userPlaced)
        {
            if (_disposed || _applying || !_form.Visible || _form.WindowState != FormWindowState.Normal ||
                _policy.Visibility != HostWindowVisibility.Visible) return;
            if (_policy.Presentation == HostWindowPresentation.Compact) _preferences.Compact = Record(_form);
            else if (_policy.Presentation == HostWindowPresentation.FullNormal) { _preferences.Full = Record(_form); _fullPlaced |= userPlaced; }
            _policy.Touch(); Publish(); ScheduleSave();
        }
        private void CaptureCompanion()
        {
            if (_disposed) return;
            _companion.Bounds = WindowNative.Clamp(_companion.Bounds, Screen.FromControl(_companion).WorkingArea);
            _preferences.Companion = Record(_companion); UpdateAnchorOffset(); ScheduleSave(); _policy.Touch(); Publish();
        }
        private void ObserveNativeState()
        {
            if (_disposed || _applying || !_policy.Alive || !_form.Visible) return;
            var state = _form.WindowState;
            if (_policy.Presentation == HostWindowPresentation.Companion && state != FormWindowState.Minimized)
            { Resume(); return; }
            if (state == FormWindowState.Maximized && _policy.Presentation != HostWindowPresentation.FullMaximized) NativeCommand(HostWindowCommand.Maximize);
            else if (state == FormWindowState.Minimized && _policy.Visibility != HostWindowVisibility.SystemMinimized) NativeCommand(HostWindowCommand.SystemMinimize);
            else if (state == FormWindowState.Normal && (_policy.Presentation == HostWindowPresentation.FullMaximized ||
                _policy.Visibility == HostWindowVisibility.SystemMinimized)) NativeCommand(HostWindowCommand.Restore);
        }
        private void SetInitialWidth(int width)
        {
            OnUi(() =>
            {
                if (_disposed || _fullPlaced || _policy.Presentation != HostWindowPresentation.FullNormal) return;
                _preferences.Full = null;
                ApplyPresentation(false);
            });
        }

        private bool AnchorAvailable()
        {
            uint process;
            if (_anchor != IntPtr.Zero && (!WindowNative.IsWindow(_anchor) ||
                WindowNative.GetWindowThreadProcessId(_anchor, out process) != _anchorThread || process != _anchorProcess))
            {
                _anchor = IntPtr.Zero; _policy.Touch(); Publish(); // never rebind by PID or recycled HWND
            }
            return _anchor != IntPtr.Zero;
        }
        private bool OwnInteraction(IntPtr foreground)
        {
            return _form.Interacting || _companion.Interacting || !WindowNative.IsWindowEnabled(_form.Handle) ||
                WindowNative.BelongsTo(foreground, _form.Handle) || WindowNative.BelongsTo(foreground, _companion.Handle);
        }
        private void CheckFocus(object sender, EventArgs args)
        {
            if (_disposed || !_policy.Alive || _deferredCommand.HasValue) return;
            var foreground = WindowNative.GetForegroundWindow();
            if (foreground == IntPtr.Zero) { _focusRevision = -1; return; }
            var own = OwnInteraction(foreground);
            if (own || _policy.Pinned || !_policy.AutoCollapse || _policy.Visibility != HostWindowVisibility.Visible ||
                _policy.Presentation == HostWindowPresentation.Companion) _focusRevision = -1;
            else if (_focusRevision != _policy.Revision || _focusCandidate != foreground)
            { _focusRevision = _policy.Revision; _focusCandidate = foreground; _focusSince = DateTime.UtcNow; }
            else if ((DateTime.UtcNow - _focusSince).TotalMilliseconds >= 300 && _policy.Collapse(_focusRevision, true, own))
            { ApplyPresentation(false); _focusRevision = -1; }
            UpdateCompanion();
            if (_companion.Visible && (DateTime.UtcNow - _lastActivity).TotalMilliseconds >= 900)
            {
                _lastActivity = DateTime.UtcNow;
                var pane = _session.PanelControl as AssistantPaneControl;
                _companion.SetStatus(pane == null ? new HostWindowActivity() : pane.WindowActivity, _diagnostic);
            }
        }
        private void UpdateCompanion()
        {
            if (_disposed) return;
            var foreground = WindowNative.GetForegroundWindow();
            var show = _policy.Presentation == HostWindowPresentation.Companion &&
                _policy.Visibility != HostWindowVisibility.Dismissed && _policy.Visibility != HostWindowVisibility.SystemMinimized &&
                AnchorAvailable() && !WindowNative.IsIconic(_anchor) &&
                (_companion.Interacting || WindowNative.BelongsTo(foreground, _anchor));
            var revision = _policy.Revision; _policy.CompanionVisibility(show);
            if (show)
            {
                PositionCompanion(!_companion.Visible);
                if (!_companion.Visible)
                {
                    _companion.Show();
                    WindowNative.SetWindowPos(_companion.Handle, IntPtr.Zero, 0, 0, 0, 0, 0x13); // TOP, no activate/move/size; never TOPMOST
                }
            }
            else if (!_companion.Interacting) _companion.Hide();
            if (revision != _policy.Revision) Publish();
        }
        private void PositionCompanion(bool restore)
        {
            if (!AnchorAvailable() || _companion.Interacting) return;
            WindowNative.Rect rect;
            if (!WindowNative.GetWindowRect(_anchor, out rect)) return;
            var anchor = rect.Rectangle;
            var screen = Screen.FromHandle(_anchor); var area = screen.WorkingArea;
            if (restore)
            {
                var saved = _preferences.Companion; var scale = WindowNative.Scale(_companion.Handle);
                var savedScreen = saved == null ? null : Screen.AllScreens.FirstOrDefault(s => s.DeviceName == saved.Monitor);
                if (saved != null && saved.Valid && savedScreen != null)
                    _companion.Location = new Point(savedScreen.WorkingArea.Left + (int)(saved.X * scale), savedScreen.WorkingArea.Top + (int)(saved.Y * scale));
                else _companion.Location = new Point(anchor.Right - _companion.Width - 20, anchor.Bottom - _companion.Height - 40);
                _companion.Bounds = WindowNative.Clamp(_companion.Bounds, savedScreen == null ? area : savedScreen.WorkingArea);
                UpdateAnchorOffset();
            }
            else if (anchor != _lastAnchorBounds)
            {
                _companion.Location = new Point(anchor.Left + _anchorOffset.X, anchor.Top + _anchorOffset.Y);
                _companion.Bounds = WindowNative.Clamp(_companion.Bounds, area);
            }
            _lastAnchorBounds = anchor;
        }
        private void UpdateAnchorOffset()
        {
            WindowNative.Rect rect;
            if (_anchor != IntPtr.Zero && WindowNative.GetWindowRect(_anchor, out rect))
                _anchorOffset = new Point(_companion.Left - rect.Left, _companion.Top - rect.Top);
        }
        private void SetProtection(bool enabled)
        { OnUi(() => { _captureProtected = enabled; Protect(_form); Protect(_companion); }); }
        private void Protect(Form form)
        {
            if (!form.IsHandleCreated || form.IsDisposed) return;
            if (!WindowNative.SetWindowDisplayAffinity(form.Handle, _captureProtected ? 0x11U : 0U))
                Diagnostic("Защита снимков окна недоступна", new System.ComponentModel.Win32Exception());
        }
        private void Publish()
        {
            var state = _policy.Snapshot(); state.ChromeReady = _chromeReady; state.AnchorAvailable = _anchor != IntPtr.Zero; state.Diagnostic = _diagnostic;
            _snapshot = new HostWindowBootstrapDto { State = state, Capabilities = new HostWindowCapabilitiesDto {
                IntegratedChrome = _chromeSupported, CompactPresentation = true, Companion = true, Pin = true } };
            StateChanged?.Invoke(state);
        }
        private void Diagnostic(string message, Exception error)
        { _diagnostic = message + ": " + error.Message; RuntimeLog.Error(message, error); }
        private void ScheduleSave() { if (_disposed || _store == null) return; _save.Stop(); _save.Start(); }
        private AssistantWindowPreferences SavedPreferences()
        {
            return new AssistantWindowPreferences { Full = _preferences.Full, Compact = _preferences.Compact, Companion = _preferences.Companion,
                Last = _policy.ReturnFromCompanion, BeforeMaximize = _policy.RestoreBeforeMaximize, Pinned = _policy.Pinned, AutoCollapse = _policy.AutoCollapse };
        }
        private async void SavePreferences()
        {
            if (_store == null) return;
            if (_saveRunning) { _saveAgain = true; return; }
            _saveRunning = true;
            try
            {
                do { _saveAgain = false; var saved = SavedPreferences(); await Task.Run(() => _store.Save(saved)); } while (_saveAgain);
            }
            catch (Exception error) { Diagnostic("Оконные настройки не сохранены", error); if (!_disposed) { _policy.Touch(); Publish(); } }
            finally { _saveRunning = false; }
        }
        public void Dispose()
        {
            if (_disposed) return;
            RecordPlacement(false);
            if (_policy.Alive) _policy.Apply(new HostWindowCommandPayload { Command = HostWindowCommand.Shutdown });
            _disposed = true; _lifetime.Cancel(); Publish(); _focus.Stop(); _save.Stop(); SavePreferences();
            _focus.Dispose(); _save.Dispose();
            try
            {
                if (_session != null)
                {
                    _session.ScreenCaptureProtectionChanged -= SetProtection; _session.PanelWindowWidthChanged -= SetInitialWidth;
                    _session.Dispose();
                }
            }
            finally
            {
                _session = null; _lifetime.Dispose();
                StateChanged = null; ShutdownRequested = null;
                try { _companion.Dispose(); }
                finally { _form.ShuttingDown = true; _form.Dispose(); }
            }
        }
    }
}
