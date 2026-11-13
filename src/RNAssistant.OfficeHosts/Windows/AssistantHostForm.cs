using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using RNAssistant.Office.Contracts;

namespace RNAssistant.OfficeHosts.Windows
{
    internal sealed class AssistantHostForm : Form
    {
        private Rectangle _observedBounds;
        internal bool ShuttingDown;
        internal bool Interacting { get; private set; }
        internal Action<HostWindowCommand> Command;
        internal Action PlacementChanged;
        internal Action NativeStateChanged;

        internal AssistantHostForm()
        {
            Text = "RNAssistant";
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.Sizable;
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false; TopMost = false;
            MinimizeBox = MaximizeBox = true;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var value = base.CreateParams;
                // No system title bar, including the first show and handle recreation.
                // Keep the native sizing frame and minimize/maximize system commands.
                value.Style &= ~0x00C00000; // WS_CAPTION
                return value;
            }
        }

        internal void BeginMove()
        {
            // Ignore a delayed Web message after the mouse was released.
            if ((Control.MouseButtons & MouseButtons.Left) == 0 || !Visible || WindowState == FormWindowState.Minimized) return;
            WindowNative.ReleaseCapture();
            // Posting avoids holding the bridge response inside the native modal move loop.
            if (!WindowNative.PostMessage(Handle, WindowNative.SysCommand, (IntPtr)0xF012, IntPtr.Zero)) // SC_MOVE | HTCAPTION
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!ShuttingDown && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true; Command?.Invoke(HostWindowCommand.Shutdown);
            }
            base.OnFormClosing(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (!ShuttingDown && m.Msg == WindowNative.SysCommand)
            {
                var command = m.WParam.ToInt64() & 0xFFF0;
                HostWindowCommand? action = command == WindowNative.Close ? HostWindowCommand.Shutdown :
                    command == WindowNative.Maximize ? HostWindowCommand.Maximize :
                    command == WindowNative.Restore ? HostWindowCommand.Restore :
                    command == WindowNative.Minimize ? HostWindowCommand.SystemMinimize : (HostWindowCommand?)null;
                if (action.HasValue && Command != null) { Command(action.Value); m.Result = IntPtr.Zero; return; }
            }
            if (m.Msg == 0x83 && m.WParam != IntPtr.Zero) // WM_NCCALCSIZE; retain native resize edges outside WebView HWND
            {
                var rect = (WindowNative.Rect)Marshal.PtrToStructure(m.LParam, typeof(WindowNative.Rect));
                var edge = WindowState == FormWindowState.Maximized ? 0 : Math.Max(1, (int)Math.Round(6 * WindowNative.Scale(Handle)));
                rect.Left += edge; rect.Top += edge; rect.Right -= edge; rect.Bottom -= edge;
                Marshal.StructureToPtr(rect, m.LParam, false); m.Result = IntPtr.Zero; return;
            }
            if (m.Msg == 0x84) // WM_NCHITTEST, signed screen coordinates
            {
                var point = new System.Drawing.Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16)));
                var edge = Math.Max(1, (int)Math.Round(6 * WindowNative.Scale(Handle)));
                var left = point.X < Left + edge; var right = point.X >= Right - edge;
                var top = point.Y < Top + edge; var bottom = point.Y >= Bottom - edge;
                var hit = WindowState == FormWindowState.Maximized ? 1 :
                    top ? (left ? 13 : right ? 14 : 12) : bottom ? (left ? 16 : right ? 17 : 15) : left ? 10 : right ? 11 : 1;
                m.Result = (IntPtr)hit; return;
            }
            if (m.Msg == 0x24) // WM_GETMINMAXINFO, native maximize to the monitor's work area
            {
                base.WndProc(ref m);
                var monitor = new WindowNative.MonitorInfo { Size = Marshal.SizeOf(typeof(WindowNative.MonitorInfo)) };
                if (WindowNative.GetMonitorInfo(WindowNative.MonitorFromWindow(Handle, 2), ref monitor))
                {
                    var info = (WindowNative.MinMaxInfo)Marshal.PtrToStructure(m.LParam, typeof(WindowNative.MinMaxInfo));
                    info.MaxPosition = new WindowNative.Point { X = monitor.Work.Left - monitor.Monitor.Left, Y = monitor.Work.Top - monitor.Monitor.Top };
                    info.MaxSize = new WindowNative.Point { X = monitor.Work.Right - monitor.Work.Left, Y = monitor.Work.Bottom - monitor.Work.Top };
                    info.MinTrack = new WindowNative.Point { X = Math.Min(MinimumSize.Width, info.MaxSize.X), Y = Math.Min(MinimumSize.Height, info.MaxSize.Y) };
                    Marshal.StructureToPtr(info, m.LParam, false);
                }
                return;
            }
            if (m.Msg == 0x231 || m.Msg == 0x211) Interacting = true; // size/move or system menu loop
            base.WndProc(ref m);
            if (m.Msg == 5) NativeStateChanged?.Invoke(); // WM_SIZE: includes native snap / drag restore
            if (m.Msg == 0x47 && WindowState == FormWindowState.Normal && Bounds != _observedBounds) // keyboard Snap / placement
            {
                _observedBounds = Bounds;
                if (!Interacting) PlacementChanged?.Invoke();
            }
            if (m.Msg == 0x232 || m.Msg == 0x212)
            {
                Interacting = false; PlacementChanged?.Invoke();
            }
            if (m.Msg == 0x02E0 && WindowNative.PerMonitorAware(Handle))
            {
                if (WindowState == FormWindowState.Normal)
                    Bounds = ((WindowNative.Rect)Marshal.PtrToStructure(m.LParam, typeof(WindowNative.Rect))).Rectangle;
                PlacementChanged?.Invoke();
            }
        }
    }
}
