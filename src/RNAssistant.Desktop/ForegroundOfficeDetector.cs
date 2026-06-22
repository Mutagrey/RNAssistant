using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using RNAssistant.OfficeHosts;

namespace RNAssistant.Desktop
{
    internal static class ForegroundOfficeDetector
    {
        public static DesktopActivation Detect()
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                throw new InvalidOperationException("No foreground window detected.");
            }

            var currentProcessId = Process.GetCurrentProcess().Id;
            for (var i = 0; hwnd != IntPtr.Zero && i < 64; i++)
            {
                int processId;
                GetWindowThreadProcessId(hwnd, out processId);
                if (processId != 0 && processId != currentProcessId && IsWindowVisible(hwnd))
                {
                    var activation = TryCreateActivation(hwnd, processId);
                    if (activation != null)
                    {
                        return activation;
                    }
                }

                hwnd = GetWindow(hwnd, GW_HWNDNEXT);
            }

            throw new InvalidOperationException("No foreground Office window detected.");
        }

        private static DesktopActivation TryCreateActivation(IntPtr hwnd, int processId)
        {
            try
            {
                var process = Process.GetProcessById(processId);
                var host = HostFromProcess(process.ProcessName);
                if (string.IsNullOrWhiteSpace(host))
                {
                    return null;
                }

                return new DesktopActivation
                {
                    Host = host,
                    Action = "attach",
                    Target = new OfficeTargetDescriptor
                    {
                        Host = host,
                        Hwnd = hwnd.ToInt64(),
                        ProcessId = processId
                    }
                };
            }
            catch
            {
                return null;
            }
        }

        private static string HostFromProcess(string processName)
        {
            var name = (processName ?? string.Empty).Trim();
            if (string.Equals(name, "EXCEL", StringComparison.OrdinalIgnoreCase))
            {
                return "Excel";
            }
            if (string.Equals(name, "WINWORD", StringComparison.OrdinalIgnoreCase))
            {
                return "Word";
            }
            if (string.Equals(name, "POWERPNT", StringComparison.OrdinalIgnoreCase))
            {
                return "PowerPoint";
            }
            if (string.Equals(name, "OUTLOOK", StringComparison.OrdinalIgnoreCase))
            {
                return "Outlook";
            }
            return null;
        }

        private const uint GW_HWNDNEXT = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);
    }
}
