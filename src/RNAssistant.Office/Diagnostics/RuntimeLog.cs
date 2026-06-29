using System;
using System.IO;
using System.Text;

namespace RNAssistant.Office.Diagnostics
{
    public static class RuntimeLog
    {
        private static readonly object Sync = new object();
        private static string _logFile;

        public static void Configure(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                return;
            }

            var logDirectory = Path.Combine(Path.GetFullPath(rootPath), "logs");
            Directory.CreateDirectory(logDirectory);
            _logFile = Path.Combine(logDirectory, "rnassistant.log");
            Info("Runtime logging configured.");
        }

        public static void Info(string message)
        {
            Write("INFO", message, null);
        }

        public static void Error(string message)
        {
            Write("ERROR", message, null);
        }

        public static void Error(string message, Exception exception)
        {
            Write("ERROR", message, exception);
        }

        private static void Write(string level, string message, Exception exception)
        {
            var file = _logFile;
            if (string.IsNullOrWhiteSpace(file))
            {
                return;
            }

            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                + " [" + level + "] " + (message ?? string.Empty);
            if (exception != null)
            {
                line += Environment.NewLine + exception;
            }

            try
            {
                lock (Sync)
                {
                    File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
            }
        }
    }
}
