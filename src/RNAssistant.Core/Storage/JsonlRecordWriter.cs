using System;
using System.IO;

namespace RNAssistant.Core.Storage
{
    internal static class JsonlRecordWriter
    {
        public static void RepairTail(string path, long validByteLength, long originalByteLength)
        {
            if (validByteLength < 0 || validByteLength > originalByteLength)
                throw new ArgumentOutOfRangeException("validByteLength");
            if (StorageFileSystem.IsReparsePoint(path))
                throw new IOException("JSONL storage file cannot be a reparse point.");

            if (validByteLength == originalByteLength)
            {
                // The final record is valid; only its line separator is missing.
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                {
                    if (stream.Length != originalByteLength)
                        throw new IOException("JSONL storage changed before tail repair.");
                    stream.Position = stream.Length;
                    stream.WriteByte((byte)'\n');
                    stream.Flush(true);
                }
                return;
            }

            // Preserve the exact validated bytes without materializing or reserializing the log.
            StorageFileSystem.WriteAtomic(path, tempPath =>
            {
                using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var target = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (source.Length != originalByteLength)
                        throw new IOException("JSONL storage changed before tail repair.");
                    var buffer = new byte[81920];
                    var remaining = validByteLength;
                    while (remaining > 0)
                    {
                        var count = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                        if (count <= 0) throw new IOException("JSONL storage changed during tail repair.");
                        target.Write(buffer, 0, count);
                        remaining -= count;
                    }
                    target.Flush(true);
                }
            });
        }
    }
}
