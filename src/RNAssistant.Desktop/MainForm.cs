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
        private Label _placeholder;
        private AssistantRuntime _runtime;

        public MainForm()
        {
            _adapterProvider = new OfficeComAdapterProvider();
            _targetRegistry = new OfficeTargetRegistry();
            _targetBar = new TargetSelectionBar();
            Text = "RN Assistant";
            Width = 720;
            Height = 820;
            StartPosition = FormStartPosition.CenterScreen;
            _content = new Panel { Dock = DockStyle.Fill };
            Controls.Add(_content);
            Controls.Add(_targetBar);
            _targetBar.UseActiveRequested += AttachForegroundOffice;
            _targetBar.RefreshRequested += RefreshOpenTargets;
            _targetBar.HostFilterChanged += delegate { RefreshTargetUi(null); };
            _targetBar.TargetSelected += SelectTarget;
            _targetBar.ModeChanged += SetTargetMode;
            ShowPlaceholder("No Office attached.", true);
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

            try
            {
                DesktopLog.Info("Attach requested. Target=" + entry.DisplayName + ", hwnd=" + entry.Target.Hwnd + ", pid=" + entry.Target.ProcessId);
                var adapter = _adapterProvider.Create(entry.Target.Host, entry.Target);
                _runtime = new AssistantRuntime(adapter);
                ClearContent();
                var pane = _runtime.CreatePaneControl();
                pane.Dock = DockStyle.Fill;
                _content.Controls.Add(pane);
                Text = "RN Assistant - " + adapter.HostName;
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
                DesktopLog.Error("Attach failed.", ex);
                RefreshTargetUi("Attach failed: " + ex.Message);
                ShowPlaceholder(ex.Message, true);
                MessageBox.Show(this, ex.Message, "RN Assistant", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
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
                ShowPlaceholder(ex.Message, true);
                MessageBox.Show(this, ex.Message, "RN Assistant", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

    }
}
