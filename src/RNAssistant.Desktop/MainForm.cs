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
            ShowPlaceholder("Open RN Assistant from an Office wrapper ribbon.");
        }

        public void ApplyActivation(string[] args)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => ApplyActivation(args)));
                return;
            }

            var activation = DesktopActivation.Parse(args);
            if (string.IsNullOrWhiteSpace(activation.Host))
            {
                ShowPlaceholder("Office host was not specified.");
                return;
            }

            try
            {
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
                ShowPlaceholder(ex.Message);
                MessageBox.Show(this, ex.Message, "RN Assistant", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ShowPlaceholder(string text)
        {
            ClearContent();
            _placeholder = new Label
            {
                Dock = DockStyle.Fill,
                Text = text,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 10f),
                Padding = new Padding(24)
            };
            _content.Controls.Add(_placeholder);
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
