using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Office.Core;
using Excel = Microsoft.Office.Interop.Excel;
using PowerPoint = Microsoft.Office.Interop.PowerPoint;
using Word = Microsoft.Office.Interop.Word;

namespace RNAssistant.OfficeHosts
{
    public static class OfficeHostLauncher
    {
        public static object OpenOrActivate(string host)
        {
            var executable = ExecutableName(host);
            var application = TryGetRunningApplication(host);
            if (application != null) return application;
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
                        break;
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

            // A visible process can still be absent from the ROT. Do not start
            // a second instance, or replace its intended document with a blank one.
            if (hasRunningProcess)
            {
                return null;
            }

            // Retain the object returned by COM activation. Shell launch followed
            // by GetActiveObject depends on Office losing focus to register in ROT.
            if (host == "Excel")
            {
                var excel = new Excel.Application();
                excel.Visible = true;
                return excel;
            }
            if (host == "Word")
            {
                var word = new Word.Application();
                word.Visible = true;
                return word;
            }
            if (host == "PowerPoint")
            {
                var powerPoint = new PowerPoint.Application();
                powerPoint.Visible = MsoTriState.msoTrue;
                return powerPoint;
            }

            // Outlook owns profile selection; wait for its running object after
            // launching the UI instead of creating an automation-only profile.
            var start = new ProcessStartInfo(executable) { UseShellExecute = true };
            var started = Process.Start(start);
            if (started != null) started.Dispose();
            return null;
        }

        internal static object TryGetRunningApplication(string host)
        {
            try
            {
                return Marshal.GetActiveObject(host + ".Application");
            }
            catch (COMException ex) when (ex.ErrorCode == unchecked((int)0x800401E3))
            {
                return null; // MK_E_UNAVAILABLE: not registered yet.
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
