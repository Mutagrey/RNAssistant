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

        public static void Debug(string message)
        {
            Write("DEBUG", message, null);
        }

        public static void Error(string message)
        {
            Write("ERROR", message, null);
        }

        public static void Error(string message, Exception exception)
        {
            Write("ERROR", message, exception);
        }

        public static string FilePath
        {
            get { return _logFile ?? string.Empty; }
        }

        public static string ReadTail(int maxChars)
        {
            var file = _logFile;
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
            {
                return string.Empty;
            }

            maxChars = Math.Max(1024, Math.Min(4 * 1024 * 1024, maxChars));
            try
            {
                lock (Sync)
                {
                    using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        var maxBytes = (long)maxChars * 4;
                        if (stream.Length > maxBytes)
                        {
                            stream.Seek(-maxBytes, SeekOrigin.End);
                        }
                        using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                        {
                            var value = reader.ReadToEnd();
                            return value.Length <= maxChars ? value : value.Substring(value.Length - maxChars);
                        }
                    }
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        public static void Clear()
        {
            var file = _logFile;
            if (string.IsNullOrWhiteSpace(file))
            {
                return;
            }

            try
            {
                lock (Sync)
                {
                    File.WriteAllText(file, string.Empty, Encoding.UTF8);
                }
            }
            catch
            {
            }
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
