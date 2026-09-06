using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace RNAssistant.Office.Diagnostics
{
    public static class RuntimeLog
    {
        private static readonly object Sync = new object();
        private const int MaximumQueuedRecords = 1024;
        private static readonly Queue<string> Queue = new Queue<string>();
        private static readonly AutoResetEvent Signal = new AutoResetEvent(false);
        private static string _logFile;
        private static Thread _writerThread;
        private static bool _shutdown;
        private static int _droppedRecords;
        private static bool _exitHookRegistered;

        public static void Configure(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                return;
            }

            var logDirectory = Path.Combine(Path.GetFullPath(rootPath), "logs");
            Directory.CreateDirectory(logDirectory);
            lock (Sync)
            {
                _logFile = Path.Combine(logDirectory, "rnassistant.log");
                EnsureWriterLocked();
                if (!_exitHookRegistered)
                {
                    AppDomain.CurrentDomain.ProcessExit += (sender, args) => Shutdown();
                    AppDomain.CurrentDomain.DomainUnload += (sender, args) => Shutdown();
                    _exitHookRegistered = true;
                }
            }
            Info("Runtime logging configured.");
        }

        public static void Info(string message)
        {
            Write("INFO", message, null);
        }

        public static void Debug(string message)
        {
#if DEBUG
            Write("DEBUG", message, null);
#endif
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

            lock (Sync)
            {
                if (string.IsNullOrWhiteSpace(_logFile) || _shutdown)
                {
                    return;
                }

                EnsureWriterLocked();
                if (Queue.Count >= MaximumQueuedRecords)
                {
                    _droppedRecords++;
                    return;
                }

                if (_droppedRecords > 0 && Queue.Count < MaximumQueuedRecords - 1)
                {
                    Queue.Enqueue(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                        + " [WARN] Runtime log dropped " + _droppedRecords + " record(s).");
                    _droppedRecords = 0;
                }

                Queue.Enqueue(line);
            }
            Signal.Set();
        }

        private static void EnsureWriterLocked()
        {
            if (_writerThread != null)
            {
                return;
            }

            _writerThread = new Thread(WriterLoop);
            _writerThread.IsBackground = true;
            _writerThread.Name = "RNAssistant RuntimeLog";
            _writerThread.Start();
        }

        private static void WriterLoop()
        {
            StreamWriter writer = null;
            string openedFile = null;
            try
            {
                while (true)
                {
                    string line;
                    string file;
                    lock (Sync)
                    {
                        if (Queue.Count == 0)
                        {
                            if (_shutdown)
                            {
                                break;
                            }

                            line = null;
                            file = null;
                        }
                        else
                        {
                            line = Queue.Dequeue();
                            file = _logFile;
                        }
                    }

                    if (line == null)
                    {
                        Signal.WaitOne(TimeSpan.FromSeconds(2));
                        Flush(writer);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(file))
                    {
                        continue;
                    }

                    try
                    {
                        if (!string.Equals(openedFile, file, StringComparison.OrdinalIgnoreCase))
                        {
                            Close(writer);
                            writer = new StreamWriter(new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8);
                            openedFile = file;
                        }

                        writer.WriteLine(line);
                    }
                    catch
                    {
                        Close(writer);
                        writer = null;
                        openedFile = null;
                    }
                }
            }
            finally
            {
                Flush(writer);
                Close(writer);
            }
        }

        private static void Shutdown()
        {
            Thread writerThread;
            lock (Sync)
            {
                _shutdown = true;
                writerThread = _writerThread;
            }
            Signal.Set();
            try
            {
                if (writerThread != null && writerThread != Thread.CurrentThread)
                {
                    writerThread.Join(500);
                }
            }
            catch
            {
            }
        }

        private static void Flush(StreamWriter writer)
        {
            try
            {
                if (writer != null)
                {
                    writer.Flush();
                }
            }
            catch
            {
            }
        }

        private static void Close(StreamWriter writer)
        {
            try
            {
                if (writer != null)
                {
                    writer.Dispose();
                }
            }
            catch
            {
            }
        }
    }
}
