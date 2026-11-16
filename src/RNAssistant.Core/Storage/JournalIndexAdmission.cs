using System;
using System.IO;
using System.Threading;

namespace RNAssistant.Core.Storage
{
    // Cross-process admission for short journal/index sections. It never owns a
    // run, a CAS activity or a resource effect gate. Repair drains only sections.
    internal static class JournalIndexAdmission
    {
        internal static IDisposable Enter(AppDataPaths paths, CancellationToken token,
            TimeSpan? timeout = null)
        {
            using (WaitForLock(Intent(paths), token, timeout, "index_sections_busy"))
                return WaitForLock(Sections(paths), token, timeout, "index_sections_busy");
        }

        internal static IDisposable Repair(AppDataPaths paths, CancellationToken token, TimeSpan? timeout = null)
        {
            var admission = timeout ?? TimeSpan.FromMinutes(5);
            var intent = WaitForLock(Intent(paths), token, admission, "index_repair_busy");
            try { return new RepairLease(intent, WaitForLock(Sections(paths), token, admission, "index_repair_busy")); }
            catch { intent.Dispose(); throw; }
        }

        private static string Intent(AppDataPaths paths) { return Path.Combine(paths.Root, "locks", "index-repair.lck"); }
        private static string Sections(AppDataPaths paths) { return Path.Combine(paths.Root, "locks", "index-sections.lck"); }

        private static IDisposable WaitForLock(string path, CancellationToken token, TimeSpan? timeout, string busyCode)
        {
            StorageFileSystem.EnsureRegularDirectory(Path.GetDirectoryName(path));
            var deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(30));
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException error) when (IsSharingConflict(error))
                {
                    if (DateTime.UtcNow >= deadline)
                        throw new WorkspaceFileException(busyCode,
                            "Не удалось получить доступ к индексу. Повторите после завершения другой операции.");
                    // A full streaming repair may exceed the short append-lock
                    // timeout. Park with cancellation; unrelated I/O errors surface.
                    if (token.CanBeCanceled) token.WaitHandle.WaitOne(20); else Thread.Sleep(20);
                }
            }
        }

        private static bool IsSharingConflict(IOException error)
        {
            var code = error.HResult & 0xffff;
            return code == 32 || code == 33 || Environment.OSVersion.Platform != PlatformID.Win32NT && (code == 11 || code == 13 || code == 35);
        }

        private sealed class RepairLease : IDisposable
        {
            private IDisposable _intent, _sections;
            internal RepairLease(IDisposable intent, IDisposable sections) { _intent = intent; _sections = sections; }
            public void Dispose()
            {
                Interlocked.Exchange(ref _sections, null)?.Dispose();
                Interlocked.Exchange(ref _intent, null)?.Dispose();
            }
        }
    }
}
