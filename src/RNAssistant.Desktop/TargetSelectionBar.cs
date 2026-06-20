using System;
using System.Drawing;
using System.Windows.Forms;

namespace RNAssistant.Desktop
{
    internal sealed class TargetSelectionBar : UserControl
    {
        private readonly ComboBox _modeCombo;
        private readonly ComboBox _hostCombo;
        private readonly ComboBox _targetCombo;
        private readonly Label _targetStatus;
        private bool _updating;

        public TargetSelectionBar()
        {
            Height = 76;
            Dock = DockStyle.Top;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = Color.FromArgb(247, 248, 250),
                Padding = new Padding(8, 6, 8, 4)
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));

            var row = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoScroll = true
            };

            _modeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 108 };
            _modeCombo.Items.Add("Manual");
            _modeCombo.Items.Add("Auto follow");
            _modeCombo.SelectedIndex = 0;
            _modeCombo.SelectedIndexChanged += delegate
            {
                if (_updating) return;
                var handler = ModeChanged;
                if (handler != null)
                {
                    handler(_modeCombo.SelectedIndex == 1 ? TargetSelectionMode.AutoFollow : TargetSelectionMode.Manual);
                }
            };

            _hostCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
            _hostCombo.Items.AddRange(new object[] { "All", "Excel", "Word", "PowerPoint", "Outlook" });
            _hostCombo.SelectedIndex = 0;
            _hostCombo.SelectedIndexChanged += delegate
            {
                if (!_updating)
                {
                    var handler = HostFilterChanged;
                    if (handler != null)
                    {
                        handler();
                    }
                }
            };

            _targetCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
            _targetCombo.SelectedIndexChanged += delegate
            {
                if (_updating) return;
                var item = _targetCombo.SelectedItem as TargetComboItem;
                if (item != null)
                {
                    var handler = TargetSelected;
                    if (handler != null)
                    {
                        handler(item.Id);
                    }
                }
            };

            var activeButton = new Button { Text = "Use active", Width = 86, Height = 26 };
            activeButton.Click += delegate { UseActiveRequested?.Invoke(); };

            var refreshButton = new Button { Text = "Refresh", Width = 74, Height = 26 };
            refreshButton.Click += delegate { RefreshRequested?.Invoke(); };

            row.Controls.Add(new Label { Text = "Mode", Width = 38, TextAlign = ContentAlignment.MiddleLeft, Height = 26 });
            row.Controls.Add(_modeCombo);
            row.Controls.Add(new Label { Text = "Type", Width = 34, TextAlign = ContentAlignment.MiddleLeft, Height = 26 });
            row.Controls.Add(_hostCombo);
            row.Controls.Add(new Label { Text = "Document", Width = 62, TextAlign = ContentAlignment.MiddleLeft, Height = 26 });
            row.Controls.Add(_targetCombo);
            row.Controls.Add(activeButton);
            row.Controls.Add(refreshButton);

            _targetStatus = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Manual target mode. Choose a document or use the active Office window.",
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(77, 86, 100)
            };

            root.Controls.Add(row, 0, 0);
            root.Controls.Add(_targetStatus, 0, 1);
            Controls.Add(root);
        }

        public event Action UseActiveRequested;
        public event Action RefreshRequested;
        public event Action HostFilterChanged;
        public event Action<string> TargetSelected;
        public event Action<TargetSelectionMode> ModeChanged;

        public string SelectedHost
        {
            get { return Convert.ToString(_hostCombo.SelectedItem ?? "All"); }
        }

        public void RefreshFrom(OfficeTargetRegistry registry, string status)
        {
            if (registry == null)
            {
                return;
            }

            _updating = true;
            try
            {
                _modeCombo.SelectedIndex = registry.Mode == TargetSelectionMode.AutoFollow ? 1 : 0;
                var entries = registry.ForHost(SelectedHost);
                _targetCombo.Items.Clear();
                foreach (var entry in entries)
                {
                    _targetCombo.Items.Add(new TargetComboItem(entry.Id, entry.DisplayName));
                }

                for (var i = 0; i < _targetCombo.Items.Count; i++)
                {
                    var item = _targetCombo.Items[i] as TargetComboItem;
                    if (item != null && string.Equals(item.Id, registry.SelectedTargetId, StringComparison.OrdinalIgnoreCase))
                    {
                        _targetCombo.SelectedIndex = i;
                        break;
                    }
                }

                var selected = registry.SelectedTarget;
                var mode = registry.Mode == TargetSelectionMode.AutoFollow ? "Auto follow" : "Manual";
                _targetStatus.Text = string.IsNullOrWhiteSpace(status)
                    ? mode + ". " + (selected == null ? "No working document selected." : "Working target: " + selected.DisplayName)
                    : status;
            }
            finally
            {
                _updating = false;
            }
        }

        private sealed class TargetComboItem
        {
            public TargetComboItem(string id, string text)
            {
                Id = id;
                Text = text;
            }

            public string Id { get; private set; }
            private string Text { get; set; }

            public override string ToString()
            {
                return Text;
            }
        }
    }
}
