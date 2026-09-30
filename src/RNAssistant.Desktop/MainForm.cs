using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;
using RNAssistant.OfficeHosts;

namespace RNAssistant.Desktop
{
    internal sealed class MainForm : Form
    {
        private readonly OfficeComAdapterProvider _adapterProvider;
        private readonly OfficeTargetRegistry _targetRegistry;
        private readonly TargetSelectionBar _targetBar;
        private readonly Panel _content;
        private readonly Timer _autoFollowTimer;
        private Label _placeholder;
        private AssistantRuntime _runtime;
        private IDisposable _currentAdapter;
        private Rectangle _restoreBounds;
        private FormWindowState _restoreWindowState;
        private bool _fullScreen;
        private volatile OpenOfficeDocumentDto[] _openMailboxDocuments = new OpenOfficeDocumentDto[0];
        private DateTime _nextMailboxRefreshUtc;
        private string _pendingLaunchHost;
        private string _emptyLaunchHost;
        private DateTime _pendingLaunchDeadlineUtc;
        private DateTime _nextLaunchProbeUtc;

        public MainForm()
        {
            _adapterProvider = new OfficeComAdapterProvider();
            _targetRegistry = new OfficeTargetRegistry();
            _targetBar = new TargetSelectionBar();
            Text = "RN Assistant";
            Width = 1200;
            Height = 820;
            MinimumSize = new Size(900, 640);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            _content = new Panel { Dock = DockStyle.Fill };
            _autoFollowTimer = new Timer { Interval = 750 };
            _autoFollowTimer.Tick += delegate
            {
                if (_pendingLaunchHost != null) TryAttachLaunchedHost();
                if (DateTime.UtcNow >= _nextMailboxRefreshUtc)
                {
                    _nextMailboxRefreshUtc = DateTime.UtcNow.AddMinutes(1);
                    RefreshMailboxTargets();
                }
                if (_pendingLaunchHost != null ||
                    _targetRegistry.Mode != TargetSelectionMode.AutoFollow || ContainsFocus)
                {
                    return;
                }
                try
                {
                    var activation = ForegroundOfficeDetector.Detect();
                    if (activation != null && !string.IsNullOrWhiteSpace(activation.Host))
                    {
                        if (string.Equals(activation.Host, _emptyLaunchHost, StringComparison.OrdinalIgnoreCase))
                            return;
                        _emptyLaunchHost = null;
                        ApplyActivation(activation, false);
                    }
                }
                catch
                {
                }
            };
            _autoFollowTimer.Start();
            Shown += delegate { RefreshMailboxTargets(); };
            Controls.Add(_content);
            Controls.Add(_targetBar);
            _targetBar.UseActiveRequested += AttachForegroundOffice;
            _targetBar.RefreshRequested += RefreshOpenTargets;
            _targetBar.HostFilterChanged += delegate { RefreshTargetUi(null); };
            _targetBar.TargetSelected += SelectTarget;
            _targetBar.ModeChanged += SetTargetMode;
            ShowPlaceholder("No Office attached.", true);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F11)
            {
                ToggleFullScreen();
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        private void ToggleFullScreen()
        {
            if (_fullScreen)
            {
                _fullScreen = false;
                FormBorderStyle = FormBorderStyle.Sizable;
                WindowState = FormWindowState.Normal;
                Bounds = _restoreBounds;
                WindowState = _restoreWindowState;
                return;
            }

            _restoreBounds = Bounds;
            _restoreWindowState = WindowState;
            _fullScreen = true;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
        }

        public void ApplyActivation(string[] args)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => ApplyActivation(args)));
                return;
            }

            var activation = DesktopActivation.Parse(args);
            ApplyActivation(activation, false);
        }

        private void ApplyActivation(DesktopActivation activation, bool forceSelect)
        {
            if (activation == null || string.IsNullOrWhiteSpace(activation.Host))
            {
                ShowPlaceholder("No Office attached.", true);
                return;
            }

            var entry = _targetRegistry.Upsert(activation.Target);
            var selected = _targetRegistry.SelectedTarget;
            if (!forceSelect && string.IsNullOrWhiteSpace(activation.Action) &&
                selected != null && selected.Target != null &&
                !string.IsNullOrWhiteSpace(selected.Target.StoreId) &&
                string.Equals(activation.Host, "Outlook", StringComparison.OrdinalIgnoreCase))
                return;
            var shouldSwitch = forceSelect
                || selected == null
                || _targetRegistry.Mode == TargetSelectionMode.AutoFollow
                || (entry != null && string.Equals(entry.Id, _targetRegistry.SelectedTargetId, StringComparison.OrdinalIgnoreCase));

            if (!shouldSwitch)
            {
                RefreshTargetUi("Target added. Manual mode keeps current document locked.");
                Show();
                WindowState = FormWindowState.Normal;
                Activate();
                return;
            }

            if (entry == null)
            {
                ShowPlaceholder("Office target was not detected.", true);
                return;
            }

            if (!forceSelect && _runtime != null &&
                string.Equals(entry.Id, _targetRegistry.SelectedTargetId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _targetRegistry.Select(entry.Id);
            AttachTarget(entry, activation.Action);
        }

        private void AttachTarget(OfficeTargetEntry entry, string action)
        {
            if (entry == null || entry.Target == null)
            {
                ShowPlaceholder("No Office target selected.", true);
                return;
            }

            DispatchedOfficeApplicationAdapter adapter = null;
            AssistantRuntime runtime = null;
            var attachTimer = Stopwatch.StartNew();
            try
            {
                DesktopLog.Info("Attach requested. Target=" + entry.DisplayName + ", hwnd=" + entry.Target.Hwnd + ", pid=" + entry.Target.ProcessId);
                var target = CloneTarget(entry.Target);
                adapter = new DispatchedOfficeApplicationAdapter(delegate(IOfficeStaDispatcher dispatcher)
                {
                    return _adapterProvider.Create(target.Host, target, dispatcher);
                });
                var adapterMs = attachTimer.ElapsedMilliseconds;
                runtime = new AssistantRuntime(adapter);
                runtime.Controller.ExternalDocumentsProvider = () => _openMailboxDocuments;
                runtime.MailboxNavigationRequested = NavigateMailbox;
                runtime.OfficeHostLaunchRequested = RequestOfficeHostLaunch;
                var runtimeMs = attachTimer.ElapsedMilliseconds - adapterMs;
                DisposeCurrentRuntime();
                ClearContent();
                DisposeCurrentAdapter();
                var replaceMs = attachTimer.ElapsedMilliseconds - adapterMs - runtimeMs;
                _runtime = runtime;
                runtime = null;
                _currentAdapter = adapter;
                adapter = null;
                var pane = _runtime.CreatePaneControl();
                pane.Dock = DockStyle.Fill;
                _content.Controls.Add(pane);
                if (string.Equals(_pendingLaunchHost, entry.Target.Host, StringComparison.OrdinalIgnoreCase))
                    _pendingLaunchHost = null;
                _emptyLaunchHost = null;
                var paneMs = attachTimer.ElapsedMilliseconds - adapterMs - runtimeMs - replaceMs;
                Text = "RN Assistant - " + _runtime.Controller.HostName;
                if (!string.IsNullOrWhiteSpace(action))
                {
                    _runtime.RunQuickAction(action);
                }
                RefreshTargetUi("Attached: " + entry.DisplayName);
                Show();
                WindowState = FormWindowState.Normal;
                Activate();
                if (attachTimer.ElapsedMilliseconds >= 500)
                    DesktopLog.Info("Attach timing: adapter=" + adapterMs + "ms, runtime=" +
                        runtimeMs + "ms, replace=" + replaceMs + "ms, pane=" + paneMs +
                        "ms, ui=" + (attachTimer.ElapsedMilliseconds - adapterMs - runtimeMs -
                            replaceMs - paneMs) + "ms.");
            }
            catch (Exception ex)
            {
                if (runtime != null)
                {
                    runtime.Dispose();
                }
                if (adapter != null)
                {
                    adapter.Dispose();
                }
                DesktopLog.Error("Attach failed.", ex);
                RefreshTargetUi("Attach failed: " + ex.Message);
                DisposeCurrentRuntime();
                ClearContent();
                ShowPlaceholder(ex.Message, true);
                DisposeCurrentAdapter();
                MessageBox.Show(this, ex.Message, "RN Assistant", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _autoFollowTimer.Stop();
            _autoFollowTimer.Dispose();
            DisposeCurrentRuntime();
            ClearContent();
            DisposeCurrentAdapter();
            base.OnFormClosed(e);
        }

        private void AttachForegroundOffice()
        {
            _pendingLaunchHost = null;
            _emptyLaunchHost = null;
            try
            {
                ApplyActivation(ForegroundOfficeDetector.Detect(), true);
            }
            catch (Exception ex)
            {
                DesktopLog.Error("Foreground attach failed.", ex);
                if (TryAttachSingleOpenTarget())
                {
                    return;
                }

                var message = "No active Office window detected. Select a document from the list or bring Office to the front and try again.";
                RefreshTargetUi(message);
                if (_runtime == null)
                {
                    ShowPlaceholder(message, true);
                }
            }
        }

        private void SetTargetMode(TargetSelectionMode mode)
        {
            _targetRegistry.Mode = mode;
            RefreshTargetUi(null);
        }

        private void SelectTarget(string id)
        {
            _emptyLaunchHost = null;
            _pendingLaunchHost = null;
            var entry = _targetRegistry.Select(id);
            AttachTarget(entry, null);
        }

        private void RefreshOpenTargets()
        {
            try
            {
                var host = _targetBar == null ? "All" : _targetBar.SelectedHost;
                _targetRegistry.UpsertMany(_adapterProvider.ListOpenTargets(host));
                RefreshMailboxTargets();
                RefreshTargetUi("Open document list refreshed.");
            }
            catch (Exception ex)
            {
                DesktopLog.Error("Could not refresh Office targets.", ex);
                RefreshTargetUi("Refresh failed: " + ex.Message);
            }
        }

        private void RefreshMailboxTargets()
        {
            _nextMailboxRefreshUtc = DateTime.UtcNow.AddMinutes(1);
            try
            {
                var targets = _adapterProvider.ListOpenTargets("Outlook")
                    .Where(item => !string.IsNullOrWhiteSpace(item.StoreId)).ToArray();
                _targetRegistry.UpsertMany(targets);
                if (_runtime == null && targets.Length > 0 && _targetRegistry.SelectedTarget == null &&
                    (_pendingLaunchHost == null || _pendingLaunchHost == "Outlook"))
                {
                    var first = _targetRegistry.Select(targets[0]);
                    AttachTarget(first, null);
                }
                var selected = _targetRegistry.SelectedTarget;
                _openMailboxDocuments = targets.Select(item => new OpenOfficeDocumentDto {
                    Host = "Outlook", DocumentKey = item.DocumentKey, Title = item.Name,
                    IsActive = _runtime != null &&
                        string.Equals(_runtime.Controller.HostName, "Outlook", StringComparison.OrdinalIgnoreCase) &&
                        selected != null && selected.Target != null &&
                        string.Equals(selected.Target.DocumentKey, item.DocumentKey, StringComparison.Ordinal)
                }).ToArray();
                _runtime?.RefreshState();
                RefreshTargetUi(null);
            }
            catch (Exception ex) { DesktopLog.Error("Outlook mailbox discovery failed.", ex); }
        }

        private void NavigateMailbox(string documentKey, string chatId, bool createNew)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => NavigateMailbox(documentKey, chatId, createNew)));
                return;
            }
            try
            {
                _pendingLaunchHost = null;
                _emptyLaunchHost = null;
                var target = _adapterProvider.ListOpenTargets("Outlook").FirstOrDefault(item =>
                    !string.IsNullOrWhiteSpace(item.StoreId) &&
                    string.Equals(item.DocumentKey, documentKey, StringComparison.Ordinal));
                if (target == null) throw new InvalidOperationException("Outlook mailbox is not connected.");
                var entry = _targetRegistry.Upsert(target);
                if (_runtime == null || !string.Equals(_targetRegistry.SelectedTargetId, entry.Id, StringComparison.OrdinalIgnoreCase))
                {
                    _targetRegistry.Select(entry.Id);
                    AttachTarget(entry, null);
                }
                if (_runtime == null || !string.Equals(_runtime.Controller.HostName, "Outlook", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Outlook mailbox attachment failed.");
                if (createNew) _runtime.Controller.CreateChat("Новый чат");
                else if (!string.IsNullOrWhiteSpace(chatId)) _runtime.Controller.SelectChat(chatId);
                _runtime.RefreshState();
                RefreshMailboxTargets();
            }
            catch (Exception ex)
            {
                DesktopLog.Error("Outlook mailbox navigation failed.", ex);
                MessageBox.Show(this, ex.Message, "RN Assistant", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void RequestOfficeHostLaunch(string host)
        {
            if (IsDisposed || !IsHandleCreated)
                throw new InvalidOperationException("RN Assistant window is unavailable.");
            BeginInvoke(new Action(() => LaunchOfficeHost(host)));
        }

        private void LaunchOfficeHost(string host)
        {
            try
            {
                OfficeHostLauncher.OpenOrActivate(host);
                _emptyLaunchHost = null;
                _pendingLaunchHost = host;
                _pendingLaunchDeadlineUtc = DateTime.UtcNow.AddSeconds(20);
                _nextLaunchProbeUtc = DateTime.MinValue;
                RefreshTargetUi("Opening " + host + "…");
                if (_placeholder != null && !_placeholder.IsDisposed)
                    _placeholder.Text = "Opening " + host + "…";
                TryAttachLaunchedHost();
            }
            catch (Exception ex)
            {
                _pendingLaunchHost = null;
                DesktopLog.Error("Office launch failed.", ex);
                RefreshTargetUi("Could not open " + host + ": " + ex.Message);
                MessageBox.Show(this, ex.Message, "RN Assistant", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void TryAttachLaunchedHost()
        {
            var host = _pendingLaunchHost;
            if (host == null) return;
            if (DateTime.UtcNow < _nextLaunchProbeUtc) return;
            _nextLaunchProbeUtc = DateTime.UtcNow.AddSeconds(2);
            var entries = _adapterProvider.ListOpenTargets(host)
                .Where(item => host != "Outlook" || !string.IsNullOrWhiteSpace(item.StoreId))
                .Select(item => _targetRegistry.Upsert(item)).ToArray();
            var entry = entries.FirstOrDefault(item => string.Equals(
                item.Id, _targetRegistry.SelectedTargetId, StringComparison.OrdinalIgnoreCase))
                ?? entries.FirstOrDefault();
            if (entry != null)
            {
                _pendingLaunchHost = null;
                _emptyLaunchHost = null;
                if (_runtime == null ||
                    !string.Equals(_runtime.Controller.HostName, host, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(_targetRegistry.SelectedTargetId, entry.Id, StringComparison.OrdinalIgnoreCase))
                {
                    _targetRegistry.Select(entry.Id);
                    AttachTarget(entry, null);
                    if (_runtime == null) _emptyLaunchHost = host;
                }
                else RefreshTargetUi("Attached: " + entry.DisplayName);
                if (host == "Outlook") RefreshMailboxTargets();
                return;
            }

            if (DateTime.UtcNow < _pendingLaunchDeadlineUtc) return;
            _pendingLaunchHost = null;
            _emptyLaunchHost = host;
            var message = host == "Outlook"
                ? "Outlook is running, but no mailbox is available yet. Refresh after it finishes loading."
                : host + " is open. Open a document there, then select it in RN Assistant.";
            RefreshTargetUi(message);
            if (_placeholder != null && !_placeholder.IsDisposed)
                _placeholder.Text = message;
        }

        private bool TryAttachSingleOpenTarget()
        {
            try
            {
                var host = _targetBar == null ? "All" : _targetBar.SelectedHost;
                _targetRegistry.UpsertMany(_adapterProvider.ListOpenTargets(host));
                var entries = _targetRegistry.ForHost(host);
                if (entries.Count == 1)
                {
                    _targetRegistry.Select(entries[0].Id);
                    AttachTarget(entries[0], null);
                    return true;
                }

                RefreshTargetUi(entries.Count == 0
                    ? "No open Office documents found."
                    : "Multiple Office documents found. Choose one from the document list.");
            }
            catch (Exception ex)
            {
                DesktopLog.Error("Open target fallback failed.", ex);
                RefreshTargetUi("Could not refresh Office targets: " + ex.Message);
            }

            return false;
        }

        private void RefreshTargetUi(string status)
        {
            if (_targetBar == null)
            {
                return;
            }

            _targetBar.RefreshFrom(_targetRegistry, status);
        }

        private void ShowPlaceholder(string text, bool showAttach = false)
        {
            ClearContent();
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = showAttach ? 3 : 2,
                Padding = new Padding(24)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 168f));
            if (showAttach)
            {
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48f));
            }

            _placeholder = new Label
            {
                Dock = DockStyle.Fill,
                Text = text,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 10f),
                Padding = new Padding(24)
            };
            layout.Controls.Add(_placeholder, 0, 0);

            var launchRows = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true
            };
            foreach (var host in new[] { "Excel", "Word", "PowerPoint", "Outlook" })
            {
                var selectedHost = host;
                var launchButton = new Button
                {
                    Width = 210,
                    Height = 32,
                    Text = "Open " + host,
                    Font = new Font("Segoe UI", 9f)
                };
                launchButton.Click += delegate { RequestOfficeHostLaunch(selectedHost); };
                launchRows.Controls.Add(launchButton);
            }
            layout.Controls.Add(launchRows, 0, 1);

            if (showAttach)
            {
                var button = new Button
                {
                    Dock = DockStyle.Top,
                    Height = 36,
                    Text = "Attach to active Office",
                    Font = new Font("Segoe UI", 9f)
                };
                button.Click += delegate { AttachForegroundOffice(); };
                layout.Controls.Add(button, 0, 2);
            }

            _content.Controls.Add(layout);
        }

        private void ClearContent()
        {
            while (_content.Controls.Count > 0)
            {
                var control = _content.Controls[0];
                _content.Controls.RemoveAt(0);
                control.Dispose();
            }
        }

        private void DisposeCurrentAdapter()
        {
            if (_currentAdapter == null)
            {
                return;
            }

            _currentAdapter.Dispose();
            _currentAdapter = null;
        }

        private void DisposeCurrentRuntime()
        {
            if (_runtime == null)
            {
                return;
            }

            _runtime.Dispose();
            _runtime = null;
        }

        private static OfficeTargetDescriptor CloneTarget(OfficeTargetDescriptor source)
        {
            return new OfficeTargetDescriptor
            {
                Host = source.Host,
                FullName = source.FullName,
                Path = source.Path,
                Name = source.Name,
                DocumentKey = source.DocumentKey,
                EntryId = source.EntryId,
                StoreId = source.StoreId,
                FolderPath = source.FolderPath,
                Selection = source.Selection,
                Action = source.Action,
                Hwnd = source.Hwnd,
                ProcessId = source.ProcessId
            };
        }

    }
}
