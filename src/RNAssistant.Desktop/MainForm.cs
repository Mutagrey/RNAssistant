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
        private readonly Panel _content;
        private Label _placeholder;
        private AssistantRuntime _runtime;

        public MainForm()
        {
            _adapterProvider = new OfficeComAdapterProvider();
            Text = "RN Assistant";
            Width = 560;
            Height = 820;
            StartPosition = FormStartPosition.CenterScreen;
            _content = new Panel { Dock = DockStyle.Fill };
            Controls.Add(_content);
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
            ApplyActivation(activation);
        }

        private void ApplyActivation(DesktopActivation activation)
        {
            if (string.IsNullOrWhiteSpace(activation.Host))
            {
                ShowPlaceholder("No Office attached.", true);
                return;
            }

            try
            {
                DesktopLog.Info("Attach requested. Host=" + activation.Host + ", hwnd=" + activation.Target.Hwnd + ", pid=" + activation.Target.ProcessId);
                var adapter = _adapterProvider.Create(activation.Host, activation.Target);
                _runtime = new AssistantRuntime(adapter);
                ClearContent();
                var pane = _runtime.CreatePaneControl();
                pane.Dock = DockStyle.Fill;
                _content.Controls.Add(pane);
                Text = "RN Assistant - " + adapter.HostName;
                if (!string.IsNullOrWhiteSpace(activation.Action))
                {
                    _runtime.RunQuickAction(activation.Action);
                }
                Show();
                WindowState = FormWindowState.Normal;
                Activate();
            }
            catch (Exception ex)
            {
                DesktopLog.Error("Attach failed.", ex);
                ShowPlaceholder(ex.Message, true);
                MessageBox.Show(this, ex.Message, "RN Assistant", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void AttachForegroundOffice()
        {
            try
            {
                ApplyActivation(ForegroundOfficeDetector.Detect());
            }
            catch (Exception ex)
            {
                DesktopLog.Error("Foreground attach failed.", ex);
                ShowPlaceholder(ex.Message, true);
                MessageBox.Show(this, ex.Message, "RN Assistant", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
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
