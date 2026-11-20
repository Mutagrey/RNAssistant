using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RNAssistant.Core.Llm;

namespace RNAssistant.Core.Storage
{
    // Optional profile diagnostics. Only ciphertext is written, outside the journal and CAS.
    internal sealed class ModelDiagnosticCaptureStore
    {
        internal const string DirectoryName = "model-diagnostics";
        private const int BodyLimit = 8 * 1024 * 1024;
        private readonly string _directory;
        private readonly Func<byte[], byte[]> _protect, _unprotect;
        private readonly Func<DateTime> _now;
        private readonly int _count;
        private readonly long _bytes;

        internal ModelDiagnosticCaptureStore(AppDataPaths paths, Func<byte[], byte[]> protect = null,
            Func<byte[], byte[]> unprotect = null, Func<DateTime> now = null,
            long bytes = 64L * 1024 * 1024, int count = 256)
        {
            _directory = paths.ModelDiagnosticDirectory;
#if RNA_DPAPI_SECRETS
            _protect = protect ?? ProtectedSecretStore.ProtectBytes;
            _unprotect = unprotect ?? ProtectedSecretStore.UnprotectBytes;
#else
            _protect = protect ?? UnavailableProtection;
            _unprotect = unprotect ?? UnavailableProtection;
#endif
            _now = now ?? (() => DateTime.UtcNow);
            _bytes = bytes; _count = count;
        }

#if !RNA_DPAPI_SECRETS
        private static byte[] UnavailableProtection(byte[] value)
        { throw new PlatformNotSupportedException("Protected model diagnostics require the Windows profile DPAPI provider."); }
#endif

        internal void Prune()
        {
            if (!Directory.Exists(_directory)) return;
            using (StorageFileSystem.AcquireWriteLock(Path.Combine(_directory, ".lock"))) PruneLocked(0, 0);
        }

        internal string Retain(LlmTraceRecord trace, byte[] body)
        {
            if (body.Length > BodyLimit || body.LongLength > _bytes) return null;
            var expires = _now().AddHours(24);
            var id = expires.Ticks.ToString("x16", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
            byte[] encoded;
            using (var buffer = new MemoryStream())
            using (var writer = new BinaryWriter(buffer, Encoding.UTF8, true))
            {
                writer.Write(id); writer.Write(trace.SessionId); writer.Write(trace.RunId ?? "");
                writer.Write(trace.RequestId); writer.Write(trace.Type); writer.Write(trace.WireSha256);
                writer.Write(body.Length); writer.Write(body); writer.Flush();
                encoded = _protect(buffer.ToArray());
            }
            if (encoded.LongLength > _bytes) return null;
            using (StorageFileSystem.AcquireWriteLock(Path.Combine(_directory, ".lock")))
            {
                PruneLocked(1, encoded.LongLength);
                StorageFileSystem.WriteAtomic(FileFor(id), temporary => File.WriteAllBytes(temporary, encoded));
            }
            trace.DiagnosticExpiresUtc = expires;
            return id;
        }

        private void PruneLocked(int incoming, long incomingBytes)
        {
            var directory = new DirectoryInfo(_directory);
            foreach (var temp in directory.EnumerateFiles("*.capture.*.tmp")) temp.Delete();
            var files = new Queue<FileInfo>();
            long size = incomingBytes;
            foreach (var file in directory.GetFiles("*.capture").OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                DateTime expires;
                if (!TryExpiry(Path.GetFileNameWithoutExtension(file.Name), out expires) || expires <= _now())
                { file.Delete(); continue; }
                files.Enqueue(file); size += file.Length;
            }
            while (files.Count + incoming > _count || size > _bytes)
            {
                var oldest = files.Dequeue(); size -= oldest.Length; oldest.Delete();
            }
        }

        private string FileFor(string id) { return Path.Combine(_directory, id + ".capture"); }

        private static bool TryExpiry(string id, out DateTime expires)
        {
            expires = default(DateTime);
            Guid guid; long ticks;
            if (id == null || id.Length != 49 || id[16] != '-' || !Guid.TryParseExact(id.Substring(17), "N", out guid) ||
                !long.TryParse(id.Substring(0, 16), System.Globalization.NumberStyles.AllowHexSpecifier,
                    System.Globalization.CultureInfo.InvariantCulture, out ticks) || ticks < 0 || ticks > DateTime.MaxValue.Ticks) return false;
            expires = new DateTime(ticks, DateTimeKind.Utc);
            return true;
        }

        internal bool Contains(string session, LlmTraceRecord trace)
        {
            DateTime expires;
            return session != null && trace?.SessionId == session && TryExpiry(trace.DiagnosticCaptureId, out expires) &&
                expires > _now() && File.Exists(FileFor(trace.DiagnosticCaptureId));
        }

        internal byte[] Read(string session, LlmTraceRecord trace)
        {
            DateTime expires;
            if (session == null || trace?.SessionId != session || !TryExpiry(trace.DiagnosticCaptureId, out expires) ||
                !File.Exists(FileFor(trace.DiagnosticCaptureId))) return null;
            using (StorageFileSystem.AcquireWriteLock(Path.Combine(_directory, ".lock")))
            {
                var path = FileFor(trace.DiagnosticCaptureId);
                if (expires <= _now()) { File.Delete(path); return null; }
                if (!File.Exists(path)) return null;
                if (StorageFileSystem.IsReparsePoint(path) || new FileInfo(path).Length > BodyLimit + 65536)
                    throw new InvalidDataException("Invalid protected diagnostic body.");
                using (var reader = new BinaryReader(new MemoryStream(_unprotect(File.ReadAllBytes(path)), false)))
                {
                    if (reader.ReadString() != trace.DiagnosticCaptureId || reader.ReadString() != session ||
                        reader.ReadString() != (trace.RunId ?? "") || reader.ReadString() != trace.RequestId ||
                        reader.ReadString() != trace.Type || reader.ReadString() != trace.WireSha256) return null;
                    var length = reader.ReadInt32();
                    if (length < 0 || length > BodyLimit || length != trace.WireBytes ||
                        length != reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("Diagnostic size mismatch.");
                    var body = reader.ReadBytes(length);
                    using (var hash = SHA256.Create())
                        if (!string.Equals(BitConverter.ToString(hash.ComputeHash(body)).Replace("-", ""), trace.WireSha256,
                            StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Diagnostic hash mismatch.");
                    return body;
                }
            }
        }
    }
}
