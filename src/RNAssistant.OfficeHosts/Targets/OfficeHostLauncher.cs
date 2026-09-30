using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace RNAssistant.OfficeHosts
{
    public static class OfficeHostLauncher
    {
        public static void OpenOrActivate(string host)
        {
            var executable = ExecutableName(host);
            int sessionId;
            using (var current = Process.GetCurrentProcess()) sessionId = current.SessionId;
            var running = Process.GetProcessesByName(
                executable.Substring(0, executable.Length - 4));
            var hasRunningProcess = false;
            try
            {
                foreach (var process in running)
                {
                    try
                    {
                        if (process.SessionId != sessionId) continue;
                        if (process.HasExited) continue;
                        hasRunningProcess = true;
                        var hwnd = process.MainWindowHandle;
                        if (hwnd == IntPtr.Zero) continue;
                        NativeWindowInfo.BringToForeground(hwnd.ToInt64());
                        return;
                    }
                    catch (InvalidOperationException)
                    {
                    }
                    catch (Win32Exception)
                    {
                    }
                }
            }
            finally
            {
                foreach (var process in running) process.Dispose();
            }

            // Outlook is single-profile state. A running process may still be
            // registering its COM object or may have no visible Explorer yet.
            if (host == "Outlook" && hasRunningProcess)
            {
                ActivateRunningOutlookWindow();
                return;
            }

            var start = new ProcessStartInfo(executable) { UseShellExecute = true };
            var started = Process.Start(start);
            if (started != null) started.Dispose();
        }

        private static void ActivateRunningOutlookWindow()
        {
            try
            {
                var application = Marshal.GetActiveObject("Outlook.Application") as Outlook.Application;
                if (application == null) return;
                var explorer = application.ActiveExplorer();
                var hwnd = NativeWindowInfo.ReadOutlookWindowHandle(explorer);
                if (hwnd == 0)
                {
                    foreach (Outlook.Explorer openExplorer in application.Explorers)
                    {
                        hwnd = NativeWindowInfo.ReadOutlookWindowHandle(openExplorer);
                        if (hwnd != 0) break;
                    }
                }
                if (hwnd == 0)
                {
                    foreach (Outlook.Inspector inspector in application.Inspectors)
                    {
                        hwnd = NativeWindowInfo.ReadOutlookWindowHandle(inspector);
                        if (hwnd != 0) break;
                    }
                }
                NativeWindowInfo.BringToForeground(hwnd);
            }
            catch (COMException)
            {
                // Outlook may still be starting; Desktop waits for its mailbox.
            }
        }

        private static string ExecutableName(string host)
        {
            switch (host)
            {
                case "Excel": return "EXCEL.EXE";
                case "Word": return "WINWORD.EXE";
                case "PowerPoint": return "POWERPNT.EXE";
                case "Outlook": return "OUTLOOK.EXE";
                default: throw new ArgumentOutOfRangeException("host", host, "Unsupported Office host.");
            }
        }
    }
}
