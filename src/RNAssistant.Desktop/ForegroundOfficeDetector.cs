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

            int processId;
            GetWindowThreadProcessId(hwnd, out processId);
            if (processId == 0)
            {
                throw new InvalidOperationException("Could not detect foreground process.");
            }

            var process = Process.GetProcessById(processId);
            var host = HostFromProcess(process.ProcessName);
            if (string.IsNullOrWhiteSpace(host))
            {
                throw new InvalidOperationException("Foreground window is not Excel, Word, PowerPoint or Outlook.");
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

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);
    }
}
