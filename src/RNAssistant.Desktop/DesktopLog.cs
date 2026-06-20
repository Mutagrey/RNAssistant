using System;
using System.IO;

namespace RNAssistant.Desktop
{
    internal static class DesktopLog
    {
        private static readonly object SyncRoot = new object();

        public static void Info(string message)
        {
            Write("INFO", message, null);
        }

        public static void Error(string message, Exception exception)
        {
            Write("ERROR", message, exception);
        }

        private static void Write(string level, string message, Exception exception)
        {
            try
            {
                var directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "OfficeAssistant",
                    "logs");
                Directory.CreateDirectory(directory);

                var path = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd") + ".log");
                var line = DateTime.UtcNow.ToString("o") + " [" + level + "] " + (message ?? string.Empty);
                if (exception != null)
                {
                    line += Environment.NewLine + exception;
                }

                lock (SyncRoot)
                {
                    File.AppendAllText(path, line + Environment.NewLine);
                }
            }
            catch
            {
            }
        }
    }
}
