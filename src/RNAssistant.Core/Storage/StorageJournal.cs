using System;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Core.Storage
{
    // One codec/append primitive for the conversation and resource owners. It has
    // no policy, dispatcher, cache or independent authority pointer.
    internal sealed partial class StorageJournal
    {
        internal const int MaximumFrameBytes = 256 * 1024;
        internal const long RotationBytes = 16 * 1024 * 1024;
        internal const int RotationRecords = 4096;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly string _directory;
        private readonly string _stream;
        private readonly StorageProtector _protector;
        private readonly long _rotationBytes;
        private readonly int _rotationRecords;
        internal long ReadRecords, ReadBytes, WrittenBytes, Flushes;
        internal Action<string> Fault;

        internal StorageJournal(string directory, string stream, StorageProtector protector = null,
            long rotationBytes = RotationBytes, int rotationRecords = RotationRecords)
        {
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(stream) ||
                rotationBytes < MaximumFrameBytes || rotationRecords < 1) throw new ArgumentException("Invalid journal identity/bounds.");
            _directory = directory; _stream = stream; _protector = protector ?? StorageProtector.None;
            _rotationBytes = rotationBytes; _rotationRecords = rotationRecords;
            StorageFileSystem.EnsureRegularDirectory(directory);
        }

        internal string SegmentPath(int segment)
        { return Path.Combine(_directory, segment.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + ".jsonl"); }

        // Inventory reads the bounded header only. The returned owner must still
        // validate that header's identity, protection and chain before use.
        internal static string ReadStreamIdentity(string directory)
        {
            var first = Path.Combine(directory, "00000001.jsonl");
            if (StorageFileSystem.IsReparsePoint(directory) || StorageFileSystem.IsReparsePoint(first))
                throw new InvalidDataException("Journal inventory cannot follow links.");
            using (var reader = new JsonlByteReader(first))
            {
                var line = reader.ReadLine(MaximumFrameBytes);
                if (line == null || !line.Terminated) throw new InvalidDataException("Journal identity header is incomplete.");
                return (string)JObject.Parse(line.Text)["Stream"];
            }
        }

        internal IDisposable AcquireAppendLock(CancellationToken token = default(CancellationToken))
        { return StorageFileSystem.AcquireWriteLock(Path.Combine(_directory, "append.lck"), token); }

        // Caller holds this stream's append lock through catch-up, guards, append
        // and index apply. A cursor is a derived locator, never an accepted fact.
        internal JournalCursor CatchUp(JournalCursor cursor, Action<JournalRecord, JournalCursor> apply,
            Action<JournalRecord> validateReferences, CancellationToken token = default(CancellationToken), bool recover = true)
        {
            if (cursor == null) ValidateLayout();
            var current = cursor == null ? OpenHeader(1, null, recover) : VerifyCursor(cursor);
            while (true)
            {
                var path = SegmentPath(current.Segment);
                using (var reader = new JsonlByteReader(path, current.End))
                {
                    // A former writer may have died after Write but before Flush.
                    // Only this lock owner may adopt its complete suffix.
                    if (recover && reader.Length > current.End)
                    {
                        using (var durable = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) durable.Flush(true);
                        Flushes++;
                    }
                    JsonlByteLine line;
                    while ((line = reader.ReadLine(MaximumFrameBytes)) != null)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!recover && !line.Terminated) throw new InvalidDataException("Maintenance requires a complete, recovered journal prefix.");
                        JournalRecord record;
                        try { record = Decode(line.Text); }
                        catch (JsonException ex)
                        {
                            if (!line.Terminated && JsonlRecordReader.IsIncompleteJson(line.Text, ex) &&
                                !File.Exists(SegmentPath(current.Segment + 1)))
                            {
                                using (var tail = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                                { tail.SetLength(line.Offset); tail.Flush(true); Flushes++; }
                                return current;
                            }
                            throw new InvalidDataException("Invalid journal record; accepted bytes were preserved.", ex);
                        }
                        ValidateNext(record, current);
                        var end = line.NextOffset;
                        if (!line.Terminated)
                        {
                            validateReferences?.Invoke(record);
                            if (File.Exists(SegmentPath(current.Segment + 1))) throw new InvalidDataException("Closed segment has an unterminated frame.");
                            JsonlRecordWriter.RepairTail(path, end, end); Flushes++; end++;
                        }
                        else if (Utf8.GetByteCount(line.Text) + 1 != line.NextOffset - line.Offset)
                            throw new InvalidDataException("Journal requires one LF after each UTF-8 frame.");
                        current = new JournalCursor { Segment = current.Segment, Start = line.Offset, End = end,
                            Sequence = record.Sequence, Hash = record.Hash, SegmentFirstSequence = current.SegmentFirstSequence };
                        ReadRecords++; ReadBytes += line.NextOffset - line.Offset;
                        apply(record, current.Copy());
                    }
                }
                if (!File.Exists(SegmentPath(current.Segment + 1))) return current;
                current = OpenHeader(current.Segment + 1, current, recover);
            }
        }

        internal JournalCursor Append(JournalCursor cursor, string operation, string fingerprint, string kind, JToken data)
        {
            if (cursor == null || string.IsNullOrWhiteSpace(operation) || string.IsNullOrWhiteSpace(fingerprint) ||
                string.IsNullOrWhiteSpace(kind) || kind == "header") throw new ArgumentException("An identified fact and cursor are required.");
            var current = cursor;
            if (current.End >= _rotationBytes || current.Sequence - current.SegmentFirstSequence >= _rotationRecords)
                current = OpenHeader(checked(current.Segment + 1), current);
            var record = NewRecord(current.Segment, current.Sequence + 1, current.Hash, kind, operation, fingerprint, data);
            var bytes = Encode(record);
            Fault?.Invoke("before_append");
            using (var output = new FileStream(SegmentPath(current.Segment), FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                if (output.Length != current.End) throw new InvalidDataException("Journal cursor is stale.");
                output.Position = current.End;
                output.Write(bytes, 0, bytes.Length);
                Fault?.Invoke("after_write");
                output.Flush(true); Flushes++; WrittenBytes += bytes.Length;
                Fault?.Invoke("after_flush");
            }
            return new JournalCursor { Segment = current.Segment, Start = current.End, End = current.End + bytes.Length,
                Sequence = record.Sequence, Hash = record.Hash, SegmentFirstSequence = current.SegmentFirstSequence };
        }

        internal JournalRecord Read(JournalCursor locator)
        {
            using (var reader = new JsonlByteReader(SegmentPath(locator.Segment), locator.Start))
            {
                var line = reader.ReadLine(MaximumFrameBytes);
                if (line == null || !line.Terminated || line.NextOffset != locator.End ||
                    Utf8.GetByteCount(line.Text) + 1 != line.NextOffset - line.Offset)
                    throw new InvalidDataException("Invalid journal locator framing.");
                var record = Decode(line.Text);
                if (record.Segment != locator.Segment || record.Sequence != locator.Sequence || record.Hash != locator.Hash)
                    throw new InvalidDataException("Journal locator identity mismatch.");
                ReadRecords++; ReadBytes += line.NextOffset - line.Offset;
                return record;
            }
        }

        private JournalCursor VerifyCursor(JournalCursor cursor)
        {
            var record = Read(cursor);
            if (cursor.SegmentFirstSequence < 0 || cursor.SegmentFirstSequence > record.Sequence ||
                record.Kind == "header" && (cursor.Start != 0 || cursor.SegmentFirstSequence != record.Sequence))
                throw new InvalidDataException("Invalid derived segment boundary.");
            return cursor.Copy();
        }

        private void ValidateLayout()
        {
            var count = 0; var maximum = 0;
            foreach (var path in Directory.EnumerateFileSystemEntries(_directory))
            {
                if (StorageFileSystem.IsReparsePoint(path) || Directory.Exists(path)) throw new InvalidDataException("Unexpected journal entry or link.");
                if (Path.GetFileName(path) == "append.lck") continue;
                if (UnpublishedHeader(path)) continue;
                int number;
                if (!int.TryParse(Path.GetFileNameWithoutExtension(path), out number) || number < 1 ||
                    path != SegmentPath(number) || StorageFileSystem.IsReparsePoint(path))
                    throw new InvalidDataException("Unexpected journal segment identity.");
                count++; maximum = Math.Max(maximum, number);
            }
            if (count != maximum) throw new InvalidDataException("Journal contains a missing segment; no new authority was created.");
        }

        private JournalCursor OpenHeader(int segment, JournalCursor predecessor, bool create = true)
        {
            var path = SegmentPath(segment);
            var data = new JObject { ["predecessorLength"] = predecessor?.End ?? 0,
                ["predecessorSegment"] = predecessor?.Segment ?? 0 };
            var expected = NewRecord(segment, predecessor?.Sequence ?? 0, predecessor?.Hash, "header", null, null, data);
            if (!File.Exists(path))
            {
                if (!create) throw new InvalidDataException("Canonical journal header is missing.");
                Fault?.Invoke("before_header");
                if (StorageFileSystem.PublishNewFile(path, Encode(expected))) Flushes++;
                Fault?.Invoke("after_header");
            }
            if (StorageFileSystem.IsReparsePoint(path)) throw new IOException("Journal segment cannot be a link.");
            using (var reader = new JsonlByteReader(path))
            {
                var line = reader.ReadLine(MaximumFrameBytes);
                if (line == null || !line.Terminated || Utf8.GetByteCount(line.Text) + 1 != line.NextOffset)
                    throw new InvalidDataException("Incomplete journal header.");
                var actual = Decode(line.Text);
                if (actual.Kind != "header" || actual.Segment != segment || actual.Sequence != expected.Sequence ||
                    actual.PreviousHash != expected.PreviousHash || actual.Operation != null || actual.Fingerprint != null ||
                    !JToken.DeepEquals(actual.Data, data)) throw new InvalidDataException("Journal predecessor/header mismatch.");
                ReadRecords++; ReadBytes += line.NextOffset;
                return new JournalCursor { Segment = segment, Start = 0, End = line.NextOffset, Sequence = actual.Sequence,
                    Hash = actual.Hash, SegmentFirstSequence = actual.Sequence };
            }
        }

        private void ValidateNext(JournalRecord record, JournalCursor current)
        {
            if (record.Kind == "header" || record.Segment != current.Segment || record.Sequence != current.Sequence + 1 ||
                record.PreviousHash != current.Hash || string.IsNullOrWhiteSpace(record.Operation) || string.IsNullOrWhiteSpace(record.Fingerprint))
                throw new InvalidDataException("Journal sequence/identity chain mismatch.");
        }

        private JournalRecord NewRecord(int segment, long sequence, string previous, string kind, string operation, string fingerprint, JToken data)
        {
            return new JournalRecord { Version = 1, Stream = _stream, Segment = segment, Sequence = sequence,
                PreviousHash = previous, Kind = kind, Operation = operation, Fingerprint = fingerprint,
                Data = data, Algorithm = _protector.CurrentHashAlgorithm, KeyId = _protector.KeyId };
        }

        private byte[] Encode(JournalRecord record)
        {
            if (_protector.Encrypts)
            {
                record.EncryptedData = EventProtectionSupport.ProtectPayload(record.Data, _protector, Purpose(record), Utf8);
                record.Data = null;
            }
            record.Hash = Hash(record);
            var bytes = Utf8.GetBytes(JsonConvert.SerializeObject(record, Formatting.None) + "\n");
            if (bytes.Length > MaximumFrameBytes) throw new InvalidDataException("Journal metadata exceeds its byte bound; retain bodies in CAS.");
            return bytes;
        }

        private JournalRecord Decode(string json)
        {
            JObject root;
            using (var text = new StringReader(json))
            using (var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None })
            {
                root = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) throw new JsonSerializationException("Extra JSON content in journal frame.");
            }
            var record = root.ToObject<JournalRecord>(JsonSerializer.Create(new JsonSerializerSettings {
                MissingMemberHandling = MissingMemberHandling.Error, DateParseHandling = DateParseHandling.None }));
            if (record.Version != 1 || record.Stream != _stream || record.Segment < 1 || record.Sequence < 0 || string.IsNullOrWhiteSpace(record.Kind))
                throw new InvalidDataException("Journal format/identity mismatch.");
            if (!EventProtectionSupport.Matches(_protector, record.Algorithm, record.KeyId, record.EncryptedData) ||
                record.EncryptedData != null && record.Data != null && record.Data.Type != JTokenType.Null)
                throw new InvalidDataException("Journal protection mismatch.");
            if (record.Hash != Hash(record)) throw new InvalidDataException("Journal integrity mismatch.");
            if (_protector.Encrypts) record.Data = EventProtectionSupport.UnprotectPayload(record.EncryptedData, _protector, Purpose(record), Utf8);
            if (record.Data == null) throw new InvalidDataException("Journal fact payload is missing.");
            return record;
        }

        private string Hash(JournalRecord record)
        {
            var value = JObject.FromObject(record); value.Remove("Hash");
            return _protector.ComputeEventHash(Utf8.GetBytes(value.ToString(Formatting.None)), record.Algorithm, record.KeyId);
        }
        private static string Purpose(JournalRecord record)
        { return "journal/v1/" + record.Stream + "/" + record.Segment.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            "/" + record.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture); }
    }

    internal sealed class JournalRecord
    {
        public int Version { get; set; }
        public string Stream { get; set; }
        public int Segment { get; set; }
        public long Sequence { get; set; }
        public string PreviousHash { get; set; }
        public string Operation { get; set; }
        public string Fingerprint { get; set; }
        public string Kind { get; set; }
        public JToken Data { get; set; }
        public string EncryptedData { get; set; }
        public string Algorithm { get; set; }
        public string KeyId { get; set; }
        public string Hash { get; set; }
    }

    internal sealed class JournalCursor
    {
        public int Segment { get; set; }
        public long Start { get; set; }
        public long End { get; set; }
        public long Sequence { get; set; }
        public string Hash { get; set; }
        public long SegmentFirstSequence { get; set; }
        public JournalCursor Copy() { return (JournalCursor)MemberwiseClone(); }
    }
}
