using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Diagnostics
{
    public static class RuntimeLog
    {
        private static readonly object Sync = new object();
        private const int MaximumQueuedRecords = 1024;
        private const int MaximumQueuedCharacters = 2 * 1024 * 1024;
        private static readonly Queue<QueuedRecord> Queue = new Queue<QueuedRecord>();
        private static readonly AutoResetEvent Signal = new AutoResetEvent(false);
        private static DiagnosticLogStore _store;
        private static DiagnosticLogStore _desktopStore;
        private static Thread _writerThread;
        private static bool _shutdown;
        private static int _droppedRecords;
        private static int _queuedCharacters;
        private static bool _exitHookRegistered;

        private sealed class QueuedRecord
        {
            public DiagnosticLogStore Store;
            public string Text;
            public bool Desktop;
        }

        public static void Configure(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath)) return;
            var store = new DiagnosticLogStore(rootPath);
            lock (Sync)
            {
                _store = store;
                EnsureWriterLocked();
            }
            Info("Runtime logging configured.");
        }

        public static void Info(string message) { Write("INFO", message, null); }
        public static void Debug(string message)
        {
#if DEBUG
            Write("DEBUG", message, null);
#endif
        }
        public static void Error(string message) { Write("ERROR", message, null); }
        public static void Error(string message, Exception exception) { Write("ERROR", message, exception); }
        public static void DesktopInfo(string message) { Write("INFO", message, null, true); }
        public static void DesktopError(string message, Exception exception) { Write("ERROR", message, exception, true); }

        private static void Write(string level, string message, Exception exception, bool desktop = false)
        {
            var line = (desktop ? DateTime.UtcNow.ToString("o") : DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                + " [" + level + "] " + (message ?? string.Empty);
            if (exception != null) line += Environment.NewLine + exception;
            // The existing count bound now also implies a byte bound before data enters the queue.
            line = DiagnosticLogStore.BoundRecord(line);
            lock (Sync)
            {
                if (_shutdown) return;
                if (desktop && _desktopStore == null) _desktopStore = new DiagnosticLogStore(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RNAssistant"));
                var store = desktop ? _desktopStore : _store;
                if (store == null) return;
                EnsureWriterLocked();
                if (Queue.Count >= MaximumQueuedRecords || _queuedCharacters + line.Length > MaximumQueuedCharacters)
                { _droppedRecords++; return; }
                if (_droppedRecords > 0 && Queue.Count < MaximumQueuedRecords - 1)
                {
                    var warning = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [WARN] Runtime log dropped " + _droppedRecords + " record(s).";
                    if (_queuedCharacters + warning.Length + line.Length <= MaximumQueuedCharacters)
                    {
                        Queue.Enqueue(new QueuedRecord { Store = store, Desktop = desktop, Text = warning });
                        _queuedCharacters += warning.Length;
                        _droppedRecords = 0;
                    }
                }
                Queue.Enqueue(new QueuedRecord { Store = store, Desktop = desktop, Text = line });
                _queuedCharacters += line.Length;
            }
            Signal.Set();
        }

        private static void EnsureWriterLocked()
        {
            if (_writerThread != null) return;
            if (!_exitHookRegistered)
            {
                AppDomain.CurrentDomain.ProcessExit += (sender, args) => Shutdown();
                AppDomain.CurrentDomain.DomainUnload += (sender, args) => Shutdown();
                _exitHookRegistered = true;
            }
            _writerThread = new Thread(WriterLoop) { IsBackground = true, Name = "RNAssistant RuntimeLog" };
            _writerThread.Start();
        }

        private static void WriterLoop()
        {
            var maintenanceUtc = DateTime.MinValue;
            while (true)
            {
                var batch = new List<string>();
                DiagnosticLogStore store;
                bool desktop;
                lock (Sync)
                {
                    if (_shutdown && Queue.Count == 0) return;
                    store = Queue.Count == 0 ? (_store ?? _desktopStore) : Queue.Peek().Store;
                    desktop = Queue.Count != 0 && Queue.Peek().Desktop;
                    while (Queue.Count > 0 && batch.Count < 64 && ReferenceEquals(Queue.Peek().Store, store) && Queue.Peek().Desktop == desktop)
                    {
                        var text = Queue.Dequeue().Text;
                        _queuedCharacters -= text.Length;
                        batch.Add(text);
                    }
                }
                try
                {
                    if (store != null && batch.Count > 0) store.Append(desktop
                        ? DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log" : "rnassistant.log", batch);
                    if (store != null && DateTime.UtcNow - maintenanceUtc >= TimeSpan.FromSeconds(30))
                    {
                        store.Prune();
                        maintenanceUtc = DateTime.UtcNow;
                    }
                }
                catch
                {
                    // Logging cannot fault the application. The next successful write exposes the loss.
                    lock (Sync) _droppedRecords += batch.Count;
                }
                if (batch.Count == 0) Signal.WaitOne(TimeSpan.FromSeconds(2));
            }
        }

        private static void Shutdown()
        {
            Thread writerThread;
            lock (Sync) { _shutdown = true; writerThread = _writerThread; }
            Signal.Set();
            try { if (writerThread != null && writerThread != Thread.CurrentThread) writerThread.Join(500); }
            catch { }
        }
    }
}
