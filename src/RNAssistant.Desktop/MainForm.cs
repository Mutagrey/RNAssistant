using System;
using System.Drawing;
using System.Windows.Forms;
using RNAssistant.Office;
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
                if (_targetRegistry.Mode != TargetSelectionMode.AutoFollow)
                {
                    return;
                }
                try
                {
                    var activation = ForegroundOfficeDetector.Detect();
                    if (activation != null && !string.IsNullOrWhiteSpace(activation.Host))
                    {
                        ApplyActivation(activation, false);
                    }
                }
                catch
                {
                }
            };
            _autoFollowTimer.Start();
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
                RefreshTargetUi(null);
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
            try
            {
                DesktopLog.Info("Attach requested. Target=" + entry.DisplayName + ", hwnd=" + entry.Target.Hwnd + ", pid=" + entry.Target.ProcessId);
                var target = CloneTarget(entry.Target);
                adapter = new DispatchedOfficeApplicationAdapter(delegate
                {
                    return _adapterProvider.Create(target.Host, target);
                });
                runtime = new AssistantRuntime(adapter);
                DisposeCurrentRuntime();
                ClearContent();
                DisposeCurrentAdapter();
                _runtime = runtime;
                runtime = null;
                _currentAdapter = adapter;
                adapter = null;
                var pane = _runtime.CreatePaneControl();
                pane.Dock = DockStyle.Fill;
                _content.Controls.Add(pane);
                Text = "RN Assistant - " + _runtime.Controller.HostName;
                if (!string.IsNullOrWhiteSpace(action))
                {
                    _runtime.RunQuickAction(action);
                }
                RefreshTargetUi("Attached: " + entry.DisplayName);
                Show();
                WindowState = FormWindowState.Normal;
                Activate();
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
            var entry = _targetRegistry.Select(id);
            AttachTarget(entry, null);
        }

        private void RefreshOpenTargets()
        {
            try
            {
                var host = _targetBar == null ? "All" : _targetBar.SelectedHost;
                _targetRegistry.UpsertMany(_adapterProvider.ListOpenTargets(host));
                RefreshTargetUi("Open document list refreshed.");
            }
            catch (Exception ex)
            {
                DesktopLog.Error("Could not refresh Office targets.", ex);
                RefreshTargetUi("Refresh failed: " + ex.Message);
            }
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
                RowCount = showAttach ? 2 : 1,
                Padding = new Padding(24)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
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
                layout.Controls.Add(button, 0, 1);
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
                FolderPath = source.FolderPath,
                Selection = source.Selection,
                Action = source.Action,
                Hwnd = source.Hwnd,
                ProcessId = source.ProcessId
            };
        }

    }
}
