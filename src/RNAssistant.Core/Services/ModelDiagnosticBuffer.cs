using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Llm;

namespace RNAssistant.Core.Services
{
    // Optional exact bytes live only in this bounded process buffer. No CAS roots,
    // filesystem queue, semantic acknowledgement or restore dependency.
    internal sealed class ModelDiagnosticBuffer
    {
        internal static readonly ModelDiagnosticBuffer Shared = new ModelDiagnosticBuffer(64 * 1024 * 1024, 8 * 1024 * 1024,
            256, TimeSpan.FromMinutes(10), () => DateTime.UtcNow);
        private readonly object _sync = new object();
        private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new Dictionary<string, LinkedListNode<Entry>>(StringComparer.Ordinal);
        private readonly LinkedList<Entry> _order = new LinkedList<Entry>();
        private readonly long _maximumBytes;
        private readonly int _maximumBody, _maximumEntries;
        private readonly TimeSpan _ttl;
        private readonly Func<DateTime> _now;
        private long _bytes;
        private long _dropped, _evicted;
        internal Action Fault;

        internal ModelDiagnosticBuffer(long maximumBytes, int maximumBody, int maximumEntries, TimeSpan ttl, Func<DateTime> now)
        {
            if (maximumBytes < 1 || maximumBody < 1 || maximumEntries < 1 || ttl <= TimeSpan.Zero || now == null) throw new ArgumentException("Invalid diagnostic buffer bounds.");
            _maximumBytes = maximumBytes; _maximumBody = maximumBody; _maximumEntries = maximumEntries; _ttl = ttl; _now = now;
        }

        internal string Retain(LlmTraceRecord record, byte[] bytes)
        {
            Fault?.Invoke();
            if (record == null || bytes == null || bytes.LongLength > _maximumBytes || bytes.Length > _maximumBody)
            { lock (_sync) _dropped++; return null; }
            var entry = new Entry { Id = Guid.NewGuid().ToString("N"), Session = record.SessionId, Request = record.RequestId,
                Type = record.Type, Hash = record.WireSha256, Bytes = bytes.ToArray(), Expires = _now().Add(_ttl) };
            lock (_sync)
            {
                Expire();
                while (_order.Count >= _maximumEntries || _bytes + entry.Bytes.LongLength > _maximumBytes) RemoveFirst();
                _entries.Add(entry.Id, _order.AddLast(entry)); _bytes += entry.Bytes.LongLength;
            }
            return entry.Id;
        }

        internal bool Contains(string session, LlmTraceRecord record)
        { lock (_sync) { Expire(); return Find(session, record) != null; } }

        internal byte[] Read(string session, LlmTraceRecord record)
        {
            Entry entry;
            lock (_sync) { Expire(); entry = Find(session, record); }
            return entry?.Bytes.ToArray();
        }

        internal long RetainedBytes { get { lock (_sync) { Expire(); return _bytes; } } }
        internal long Dropped { get { lock (_sync) return _dropped; } }
        internal long Evicted { get { lock (_sync) { Expire(); return _evicted; } } }
        private Entry Find(string session, LlmTraceRecord record)
        {
            LinkedListNode<Entry> node;
            if (record?.DiagnosticCaptureId == null || !_entries.TryGetValue(record.DiagnosticCaptureId, out node)) return null;
            var entry = node.Value;
            return entry.Session == session && entry.Request == record.RequestId && entry.Type == record.Type &&
                entry.Hash == record.WireSha256 && entry.Bytes.LongLength == record.WireBytes ? entry : null;
        }
        private void Expire()
        { var now = _now(); while (_order.First != null && _order.First.Value.Expires <= now) RemoveFirst(); }
        private void RemoveFirst()
        { var entry = _order.First.Value; _order.RemoveFirst(); _entries.Remove(entry.Id); _bytes -= entry.Bytes.LongLength; _evicted++; }
        private sealed class Entry
        { internal string Id, Session, Request, Type, Hash; internal byte[] Bytes; internal DateTime Expires; }
    }
}
