using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RNAssistant.OfficeHosts.Windows
{
    internal static class WindowNative
    {
        internal const int SysCommand = 0x112, Close = 0xF060, Maximize = 0xF030, Restore = 0xF120, Minimize = 0xF020;
        [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct Rect
        {
            public int Left, Top, Right, Bottom;
            public Rectangle Rectangle { get { return System.Drawing.Rectangle.FromLTRB(Left, Top, Right, Bottom); } }
        }
        [StructLayout(LayoutKind.Sequential)] internal struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack; }
        [StructLayout(LayoutKind.Sequential)] internal struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] internal static extern bool IsWindowEnabled(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool IsChild(IntPtr parent, IntPtr child);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] internal static extern bool ReleaseCapture();
        [DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

        internal static double Scale(IntPtr hwnd)
        {
            try { var dpi = GetDpiForWindow(hwnd); return dpi == 0 ? 1 : dpi / 96.0; }
            catch (EntryPointNotFoundException) { return 1; }
        }
        internal static bool PerMonitorAware(IntPtr hwnd)
        {
            try { return GetAwarenessFromDpiAwarenessContext(GetWindowDpiAwarenessContext(hwnd)) == 2; }
            catch (EntryPointNotFoundException) { return false; }
        }
        internal static Rectangle Clamp(Rectangle bounds, Rectangle area)
        {
            var width = Math.Min(Math.Max(1, bounds.Width), area.Width);
            var height = Math.Min(Math.Max(1, bounds.Height), area.Height);
            return new Rectangle(Math.Max(area.Left, Math.Min(bounds.Left, area.Right - width)),
                Math.Max(area.Top, Math.Min(bounds.Top, area.Bottom - height)), width, height);
        }
        internal static bool BelongsTo(IntPtr window, IntPtr root)
        {
            if (window == IntPtr.Zero || root == IntPtr.Zero) return false;
            if (window == root || IsChild(root, window)) return true;
            // Explicit owned-window chain, not same-PID or GA_ROOTOWNER equivalence.
            for (var i = 0; i < 16 && window != IntPtr.Zero; i++)
            {
                window = GetWindow(window, 4); // GW_OWNER
                if (window == root) return true;
            }
            return false;
        }
    }
}
