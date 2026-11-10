using System;
using System.IO;
using System.Threading;

namespace RNAssistant.Core.Storage
{
    // Cross-process admission for short journal/index sections. It never owns a
    // run, a CAS activity or a resource effect gate. Repair drains only sections.
    internal static class JournalIndexAdmission
    {
        internal static IDisposable Enter(AppDataPaths paths, CancellationToken token)
        {
            using (WaitForLock(Intent(paths), token))
                return new FileStream(Sections(paths), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        }

        internal static IDisposable Repair(AppDataPaths paths, CancellationToken token)
        {
            var intent = WaitForLock(Intent(paths), token);
            try { return new RepairLease(intent, WaitForLock(Sections(paths), token)); }
            catch { intent.Dispose(); throw; }
        }

        private static string Intent(AppDataPaths paths) { return Path.Combine(paths.Root, "locks", "index-repair.lck"); }
        private static string Sections(AppDataPaths paths) { return Path.Combine(paths.Root, "locks", "index-sections.lck"); }

        private static IDisposable WaitForLock(string path, CancellationToken token)
        {
            StorageFileSystem.EnsureRegularDirectory(Path.GetDirectoryName(path));
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException error) when (IsSharingConflict(error))
                {
                    // A full streaming repair may exceed the short append-lock
                    // timeout. Park with cancellation; unrelated I/O errors surface.
                    if (token.CanBeCanceled) token.WaitHandle.WaitOne(20); else Thread.Sleep(20);
                }
            }
        }

        private static bool IsSharingConflict(IOException error)
        {
            var code = error.HResult & 0xffff;
            return code == 32 || code == 33 || Environment.OSVersion.Platform != PlatformID.Win32NT && (code == 11 || code == 35);
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
