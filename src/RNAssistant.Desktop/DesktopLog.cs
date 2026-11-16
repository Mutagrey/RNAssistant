using System;
using RNAssistant.Office.Diagnostics;

namespace RNAssistant.Desktop
{
    internal static class DesktopLog
    {
        // Both diagnostic sources use the same bounded background writer and canonical root.
        // Old %LOCALAPPDATA%/OfficeAssistant/logs are deliberately left untouched.
        public static void Info(string message) { try { RuntimeLog.DesktopInfo(message); } catch { } }
        public static void Error(string message, Exception exception) { try { RuntimeLog.DesktopError(message, exception); } catch { } }
    }
}
