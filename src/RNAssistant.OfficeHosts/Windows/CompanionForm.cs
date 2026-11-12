using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using RNAssistant.Office.Contracts;

namespace RNAssistant.OfficeHosts.Windows
{
    internal sealed class CompanionForm : Form
    {
        private readonly ToolTip _tip = new ToolTip();
        private readonly ContextMenuStrip _menu = new ContextMenuStrip();
        private Point? _press;
        private bool _dragged;
        private string _status = "Открыть RNAssistant";
        internal bool Interacting { get { return _press.HasValue || _menu.Visible; } }
        internal Action OpenRequested, ShutdownRequested, PlacementChanged;
        internal Action<HostWindowCommand> Command;

        internal CompanionForm()
        {
            Text = "Помощник RNAssistant"; AccessibleName = Text;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = false;
            StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true; BackColor = SystemColors.Window; ForeColor = SystemColors.WindowText;
            ContextMenuStrip = _menu;
            _menu.Items.Add("Открыть", null, (s, e) => OpenRequested?.Invoke());
            Add("Полное окно", HostWindowCommand.ShowFull); Add("Компактная панель", HostWindowCommand.ShowCompact);
            Add("Вернуть на экран", HostWindowCommand.ResetPlacement);
            _menu.Items.Add("Закрыть RNAssistant…", null, (s, e) => ShutdownRequested?.Invoke());
            HandleCreated += (s, e) => { var side = (int)Math.Round(48 * WindowNative.Scale(Handle)); Size = new Size(side, side); };
            _tip.SetToolTip(this, _status);
        }
        private void Add(string label, HostWindowCommand command) { _menu.Items.Add(label, null, (s, e) => Command?.Invoke(command)); }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var value = base.CreateParams; value.ExStyle |= 0x08000000 | 0x80; return value; } // NOACTIVATE / TOOLWINDOW
        }
        internal void SetStatus(HostWindowActivity activity, string diagnostic)
        {
            var status = activity.Waiting > 0 ? "Ожидают ответа: " + activity.Waiting :
                activity.Running > 0 ? "Работают: " + activity.Running : "Открыть RNAssistant";
            if (!string.IsNullOrEmpty(diagnostic)) status += " · " + diagnostic;
            if (status == _status) return;
            _status = status; AccessibleDescription = status; _tip.SetToolTip(this, status); Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            ControlPaint.DrawBorder(e.Graphics, ClientRectangle, SystemColors.ActiveBorder, ButtonBorderStyle.Solid);
            TextRenderer.DrawText(e.Graphics, "RN", Font, ClientRectangle, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (_status != "Открыть RNAssistant") e.Graphics.FillEllipse(SystemBrushes.Highlight, Width - 12, 5, 6, 6);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) { _press = PointToScreen(e.Location); _dragged = false; Capture = true; }
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_press.HasValue || _dragged) return;
            var point = PointToScreen(e.Location); var start = _press.Value;
            if (Math.Abs(point.X - start.X) < SystemInformation.DragSize.Width / 2 &&
                Math.Abs(point.Y - start.Y) < SystemInformation.DragSize.Height / 2) return;
            _dragged = true;
            WindowNative.ReleaseCapture();
            WindowNative.SendMessage(Handle, WindowNative.SysCommand, (IntPtr)0xF012, IntPtr.Zero); // native move loop
            _press = null; PlacementChanged?.Invoke();
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var open = e.Button == MouseButtons.Left && _press.HasValue && !_dragged;
            _press = null; Capture = false;
            if (open) OpenRequested?.Invoke();
        }
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture && !_dragged) _press = null;
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; } // MA_NOACTIVATE
            base.WndProc(ref m);
            if (m.Msg == 0x02E0 && WindowNative.PerMonitorAware(Handle))
            {
                var suggested = ((WindowNative.Rect)Marshal.PtrToStructure(m.LParam, typeof(WindowNative.Rect))).Rectangle;
                var side = (int)Math.Round(48 * WindowNative.Scale(Handle));
                Bounds = new Rectangle(suggested.Location, new Size(side, side));
            }
        }
        protected override void Dispose(bool disposing)
        { if (disposing) { _tip.Dispose(); _menu.Dispose(); } base.Dispose(disposing); }
    }
}
