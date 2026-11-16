using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    // Diagnostic text only. This owner never traverses directories or opens a chat/CAS journal.
    public sealed class DiagnosticLogStore
    {
        public const int MaximumReadBytes = 128 * 1024;
        public const int MaximumRecordBytes = 64 * 1024;
        private const long MiB = 1024L * 1024L;
        private static readonly Encoding Utf8 = new UTF8Encoding(false);
        private static readonly Regex ArchiveName = new Regex(
            @"^(rnassistant|[0-9]{8})\.archive-[0-9]{19}-[a-f0-9]{32}\.log$", RegexOptions.CultureInvariant);
        private readonly string _root;
        private readonly string _directory;
        private readonly string _epochPath;
        private readonly string _mutexName;
        private DiagnosticLogOptions _cachedOptions;
        private DateTime _optionsReadUtc;
        private DiagnosticLogIssue _optionsIssue;

        public DiagnosticLogStore(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath)) throw new ArgumentException("A diagnostic root is required.", "rootPath");
            _root = Path.GetFullPath(rootPath);
            _directory = Path.Combine(_root, "logs");
            _epochPath = Path.Combine(_directory, ".generation");
            using (var hash = SHA256.Create())
                _mutexName = "RNAssistant.DiagnosticLogs." + BitConverter.ToString(hash.ComputeHash(
                    Utf8.GetBytes(_directory.ToUpperInvariant()))).Replace("-", string.Empty);
        }

        public DiagnosticLogInventory List(CancellationToken cancellationToken = default(CancellationToken))
        {
            return Locked(() => Inventory(ReadOptions(true), new List<DiagnosticLogIssue>(), cancellationToken), cancellationToken);
        }

        public DiagnosticLogPage Read(string fileId, long? beforeOffset = null, string snapshotToken = null, int maxBytes = 65536,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (maxBytes < 4 || maxBytes > MaximumReadBytes) throw new ArgumentOutOfRangeException("maxBytes");
            return Locked(() => ReadLocked(fileId, beforeOffset, snapshotToken, maxBytes, cancellationToken), cancellationToken);
        }

        public DiagnosticLogMutationResult Clear(string fileId) { return Mutate(fileId, false, false); }
        public DiagnosticLogMutationResult Delete(string fileId) { return Mutate(fileId, true, false); }
        public DiagnosticLogMutationResult ClearAll() { return Mutate(null, true, true); }

        public DiagnosticLogMutationResult Prune()
        {
            return Locked(() => PruneLocked(ReadOptions(true)));
        }

        public DiagnosticLogMutationResult SaveOptions(DiagnosticLogOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            options.Validate();
            return Locked(() =>
            {
                var settings = new SettingsService(AppDataPaths.CreateForRoot(_root));
                settings.SaveDiagnosticLogOptions(options);
                _cachedOptions = options.Clone();
                _optionsReadUtc = DateTime.UtcNow;
                _optionsIssue = null;
                return PruneLocked(_cachedOptions);
            });
        }

        // All appenders and maintenance share one cross-process gate; no open writer survives a batch.
        public void Append(string fileId, IEnumerable<string> records)
        {
            if (fileId != "rnassistant.log" && fileId != DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log")
                throw new ArgumentException("Only the current runtime/desktop log can be appended.", "fileId");
            if (records == null) return;
            Locked(() =>
            {
                var options = ReadOptions(false);
                var path = Resolve(fileId);
                FileStream stream = null;
                try
                {
                    foreach (var record in records)
                    {
                        var bytes = Utf8.GetBytes(BoundRecord(record) + Environment.NewLine);
                        if (stream == null) stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                        if (stream.Length + bytes.Length > options.MaxFileMiB * MiB)
                        {
                            stream.Dispose(); stream = null;
                            Rotate(path);
                            stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                        }
                        stream.Write(bytes, 0, bytes.Length);
                    }
                }
                finally { if (stream != null) stream.Dispose(); }
                // At most MaxFiles small metadata reads in steady state. Enforced even with no open UI.
                PruneLocked(options);
                return true;
            });
        }

        public static string BoundRecord(string value)
        {
            value = value ?? string.Empty;
            const string marker = "\n[WARN] Diagnostic record truncated at 64 KiB.";
            var budget = MaximumRecordBytes - Utf8.GetByteCount(marker) - 2;
            // Bound the input before encoding; a UTF-16 code unit occupies at most three UTF-8 bytes.
            if (value.Length <= budget / 3 && Utf8.GetByteCount(value) <= budget) return value;
            var characters = value.ToCharArray(0, Math.Min(value.Length, budget));
            var end = characters.Length;
            while (end > 0 && Utf8.GetByteCount(characters, 0, end) > budget) end -= Math.Max(1, (end - budget / 3) / 2);
            if (end == value.Length) return value;
            if (end > 0 && char.IsHighSurrogate(value[end - 1])) end--;
            return value.Substring(0, end) + marker;
        }

        private DiagnosticLogPage ReadLocked(string fileId, long? beforeOffset, string snapshotToken, int maxBytes, CancellationToken cancellationToken)
        {
            var path = Resolve(fileId);
            var epoch = Epoch();
            if (!File.Exists(path)) return new DiagnosticLogPage { FileId = fileId, ResetRequired = true };
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var length = stream.Length;
                long snapshotLength = length;
                if (snapshotToken != null)
                {
                    var parts = snapshotToken.Split(':');
                    if (parts.Length != 3 || parts[0] != epoch || parts[2] != fileId ||
                        !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out snapshotLength) ||
                        snapshotLength < 0 || snapshotLength > length)
                        return new DiagnosticLogPage { FileId = fileId, ResetRequired = true };
                }
                else if (beforeOffset.HasValue) throw new ArgumentException("An earlier page requires its snapshot token.");
                var end = beforeOffset ?? snapshotLength;
                if (end < 0 || end > snapshotLength) throw new ArgumentOutOfRangeException("beforeOffset");
                var start = Math.Max(0, end - maxBytes);
                // Keep split characters on the preceding page; no skipped bytes and bounded allocation.
                if (start > 0)
                {
                    var originalStart = start;
                    stream.Position = start;
                    while (start < end && start - originalStart < 4)
                    {
                        var next = stream.ReadByte();
                        if (next < 0 || (next & 0xc0) != 0x80) break;
                        start++;
                    }
                    // Invalid UTF-8 is displayed with replacement characters, never an empty non-advancing page.
                    if (start == end || start - originalStart == 4) start = originalStart;
                }
                var partial = false;
                if (start > 0) { stream.Position = start - 1; partial = stream.ReadByte() != '\n'; }
                stream.Position = start;
                var data = new byte[(int)(end - start)];
                var read = 0;
                while (read < data.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = stream.Read(data, read, data.Length - read);
                    if (count == 0) break;
                    read += count;
                }
                cancellationToken.ThrowIfCancellationRequested();
                var text = Utf8.GetString(data, 0, read);
                if (start == 0 && text.Length > 0 && text[0] == '\ufeff') text = text.Substring(1);
                return new DiagnosticLogPage
                {
                    FileId = fileId, Text = text,
                    SnapshotToken = epoch + ":" + snapshotLength.ToString(CultureInfo.InvariantCulture) + ":" + fileId,
                    StartOffset = start, EndOffset = end, SnapshotLength = snapshotLength,
                    HasEarlier = start > 0, PartialFirstLine = partial
                };
            }
        }

        private DiagnosticLogMutationResult Mutate(string fileId, bool delete, bool all)
        {
            return Locked(() =>
            {
                var options = ReadOptions(true);
                var result = new DiagnosticLogMutationResult();
                var files = all ? Scan(result.Issues) : new List<DiagnosticLogFile> { Describe(Resolve(fileId)) };
                foreach (var file in files)
                {
                    try
                    {
                        var path = Resolve(file.Id);
                        if (!File.Exists(path)) continue;
                        ChangeEpoch();
                        if (delete) File.Delete(path);
                        else using (var stream = new FileStream(path, FileMode.Truncate, FileAccess.Write, FileShare.Read)) { }
                        result.AffectedFiles++;
                        result.ReclaimedBytes += file.ByteLength;
                    }
                    catch (Exception ex) when (IsFileFailure(ex))
                    { result.Issues.Add(Issue(file.Id, delete ? "delete" : "clear", ex)); }
                }
                result.Inventory = Inventory(options, result.Issues);
                return result;
            });
        }

        private DiagnosticLogMutationResult PruneLocked(DiagnosticLogOptions options)
        {
            var result = new DiagnosticLogMutationResult();
            var files = Scan(result.Issues);
            // One-time handling of a large existing active log is a rename, never a whole-file read/copy.
            foreach (var file in files.Where(f => f.IsActive && f.ByteLength > options.MaxFileMiB * MiB))
            {
                try { Rotate(Resolve(file.Id)); }
                catch (Exception ex) when (IsFileFailure(ex)) { result.Issues.Add(Issue(file.Id, "rotate", ex)); }
            }
            files = Scan(result.Issues).OrderBy(f => f.LastWriteUtc).ThenBy(f => f.Id, StringComparer.Ordinal).ToList();
            var total = files.Sum(f => f.ByteLength);
            var count = files.Count;
            var cutoff = DateTime.UtcNow.AddDays(-options.RetentionDays);
            foreach (var file in files)
            {
                var expired = file.LastWriteUtc < cutoff;
                if (!expired && (file.IsActive || (total <= options.MaxTotalMiB * MiB && count <= options.MaxFiles))) continue;
                try
                {
                    var path = Resolve(file.Id);
                    ChangeEpoch();
                    File.Delete(path);
                    count--; total -= file.ByteLength;
                    result.AffectedFiles++; result.ReclaimedBytes += file.ByteLength;
                }
                catch (Exception ex) when (IsFileFailure(ex)) { result.Issues.Add(Issue(file.Id, "prune", ex)); }
            }
            result.Inventory = Inventory(options, result.Issues);
            return result;
        }

        private void Rotate(string path)
        {
            ChangeEpoch();
            var name = Path.GetFileNameWithoutExtension(path) + ".archive-" + DateTime.UtcNow.Ticks.ToString("D19", CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N") + ".log";
            File.Move(path, Resolve(name));
        }

        private DiagnosticLogInventory Inventory(DiagnosticLogOptions options, List<DiagnosticLogIssue> issues,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var files = Scan(issues, cancellationToken).OrderByDescending(f => f.LastWriteUtc).ThenBy(f => f.Id, StringComparer.Ordinal).ToList();
            var bytes = files.Sum(f => f.ByteLength);
            if (_optionsIssue != null) issues.Add(_optionsIssue);
            if (files.Count > 200) issues.Add(new DiagnosticLogIssue { Operation = "list", Message = "Показаны 200 последних логов. Примените лимиты хранения для остальных файлов." });
            return new DiagnosticLogInventory
            {
                Files = files.Take(200).ToList(), Options = options.Clone(), TotalBytes = bytes,
                OverLimit = bytes > options.MaxTotalMiB * MiB || files.Count > options.MaxFiles ||
                    files.Any(f => (f.IsActive && f.ByteLength > options.MaxFileMiB * MiB) ||
                        f.LastWriteUtc < DateTime.UtcNow.AddDays(-options.RetentionDays)),
                Issues = issues.GroupBy(i => (i.FileId ?? "") + "|" + i.Operation + "|" + i.Message).Select(g => g.First()).ToList()
            };
        }

        private List<DiagnosticLogFile> Scan(List<DiagnosticLogIssue> issues, CancellationToken cancellationToken = default(CancellationToken))
        {
            var result = new List<DiagnosticLogFile>();
            foreach (var path in Directory.EnumerateFiles(_directory, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(path);
                if (!IsManagedName(name)) continue;
                try { result.Add(Describe(Resolve(name))); }
                catch (Exception ex) when (IsFileFailure(ex)) { issues.Add(Issue(name, "list", ex)); }
            }
            return result;
        }

        private static DiagnosticLogFile Describe(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("Лог уже удалён.", info.Name);
            return new DiagnosticLogFile
            {
                Id = info.Name, Name = info.Name, Source = info.Name.StartsWith("rnassistant", StringComparison.Ordinal) ? "runtime" : "desktop",
                ByteLength = info.Length, LastWriteUtc = info.LastWriteTimeUtc,
                IsActive = info.Name == "rnassistant.log" || info.Name == DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log"
            };
        }

        private string Resolve(string fileId)
        {
            if (string.IsNullOrWhiteSpace(fileId) || !IsManagedName(fileId))
                throw new ArgumentException("Неизвестный диагностический лог.", "fileId");
            var path = Path.Combine(_directory, fileId);
            RejectReparse(path);
            return path;
        }

        private static bool IsManagedName(string name)
        {
            if (name == "rnassistant.log") return true;
            if (ArchiveName.IsMatch(name)) return true;
            DateTime date;
            return name.Length == 12 && name.EndsWith(".log", StringComparison.Ordinal) &&
                DateTime.TryParseExact(name.Substring(0, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }

        private DiagnosticLogOptions ReadOptions(bool refresh)
        {
            if (refresh || _cachedOptions == null || DateTime.UtcNow - _optionsReadUtc > TimeSpan.FromSeconds(30))
            {
                try
                {
                    var path = Path.Combine(_root, "settings.json");
                    RejectReparse(path);
                    var settings = new JsonFileStore().Load(path, new AppSettings());
                    _cachedOptions = DiagnosticLogOptions.Normalize(settings.DiagnosticLogs);
                    _optionsIssue = null;
                }
                catch (Exception ex) when (IsFileFailure(ex) || ex is InvalidDataException)
                {
                    // Diagnostics must survive malformed/inaccessible application settings.
                    if (_cachedOptions == null) _cachedOptions = new DiagnosticLogOptions();
                    _optionsIssue = new DiagnosticLogIssue { Operation = "settings", Message =
                        "Настройки хранения недоступны; действуют последние известные лимиты или значения по умолчанию. " + ex.Message };
                }
                _optionsReadUtc = DateTime.UtcNow;
            }
            return _cachedOptions;
        }

        private string Epoch()
        {
            RejectReparse(_epochPath);
            if (!File.Exists(_epochPath)) ChangeEpoch();
            var info = new FileInfo(_epochPath);
            if (info.Length != 32) throw new InvalidDataException("Invalid diagnostic log generation.");
            var result = File.ReadAllText(_epochPath, Utf8);
            Guid ignored;
            if (!Guid.TryParseExact(result, "N", out ignored)) throw new InvalidDataException("Invalid diagnostic log generation.");
            return result;
        }

        private void ChangeEpoch()
        {
            RejectReparse(_epochPath);
            StorageFileSystem.WriteAtomic(_epochPath, temp => File.WriteAllText(temp, Guid.NewGuid().ToString("N"), Utf8));
        }

        private T Locked<T>(Func<T> action, CancellationToken cancellationToken = default(CancellationToken))
        {
            using (var mutex = new Mutex(false, _mutexName))
            {
                var acquired = false;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (!cancellationToken.CanBeCanceled) acquired = mutex.WaitOne(TimeSpan.FromSeconds(5));
                        else
                        {
                            var timer = Stopwatch.StartNew();
                            do
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                acquired = mutex.WaitOne(50);
                            } while (!acquired && timer.Elapsed < TimeSpan.FromSeconds(5));
                        }
                    }
                    catch (AbandonedMutexException) { acquired = true; }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!acquired) throw new IOException("Журнал занят другим процессом. Повторите операцию.");
                    RejectReparse(_root);
                    RejectReparse(_directory);
                    Directory.CreateDirectory(_directory);
                    return action();
                }
                finally { if (acquired) mutex.ReleaseMutex(); }
            }
        }

        private static void RejectReparse(string path)
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Диагностический путь не может быть ссылкой или reparse point.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }

        private static bool IsFileFailure(Exception ex)
        { return ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException; }

        private static DiagnosticLogIssue Issue(string fileId, string operation, Exception ex)
        { return new DiagnosticLogIssue { FileId = fileId, Operation = operation, Message = ex.Message }; }
    }
}
