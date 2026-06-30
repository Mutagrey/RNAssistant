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
            Height = 82;
            MinimumSize = new Size(0, 82);
            Dock = DockStyle.Top;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = Color.FromArgb(247, 248, 250),
                Padding = new Padding(8, 6, 8, 6)
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            var row = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 8,
                RowCount = 1,
                Margin = new Padding(0)
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38f));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112f));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48f));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110f));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66f));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90f));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78f));
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            _modeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(0, 3, 6, 3) };
            _modeCombo.Items.Add("Manual");
            _modeCombo.Items.Add("Auto follow");
            _modeCombo.SelectedIndex = 1;
            _modeCombo.SelectedIndexChanged += delegate
            {
                if (_updating) return;
                var handler = ModeChanged;
                if (handler != null)
                {
                    handler(_modeCombo.SelectedIndex == 1 ? TargetSelectionMode.AutoFollow : TargetSelectionMode.Manual);
                }
            };

            _hostCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(0, 3, 6, 3) };
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

            _targetCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(0, 3, 6, 3) };
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

            var activeButton = new Button { Text = "Use active", Dock = DockStyle.Fill, Margin = new Padding(0, 2, 6, 3) };
            activeButton.Click += delegate { UseActiveRequested?.Invoke(); };

            var refreshButton = new Button { Text = "Refresh", Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 3) };
            refreshButton.Click += delegate { RefreshRequested?.Invoke(); };

            row.Controls.Add(HeaderLabel("Mode"), 0, 0);
            row.Controls.Add(_modeCombo, 1, 0);
            row.Controls.Add(HeaderLabel("Type"), 2, 0);
            row.Controls.Add(_hostCombo, 3, 0);
            row.Controls.Add(HeaderLabel("Document"), 4, 0);
            row.Controls.Add(_targetCombo, 5, 0);
            row.Controls.Add(activeButton, 6, 0);
            row.Controls.Add(refreshButton, 7, 0);

            _targetStatus = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Manual target mode. Choose a document or use the active Office window.",
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(77, 86, 100),
                AutoEllipsis = true
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

        private static Label HeaderLabel(string text)
        {
            return new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                MinimumSize = new Size(0, 26),
                Margin = new Padding(0, 0, 2, 0)
            };
        }
    }
}
