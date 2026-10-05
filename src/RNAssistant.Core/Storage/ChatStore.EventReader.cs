using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;

namespace RNAssistant.Core.Storage
{
    public sealed partial class ChatStore
    {
        private long _eventReaderFullValidationCount;
        internal long EventReaderFullValidationCount { get { return Interlocked.Read(ref _eventReaderFullValidationCount); } }

        internal ISessionEventReader OpenEventReader(string host, string documentKey, string sessionId)
        { return new EventReader(this, host, documentKey, sessionId); }

        // Validates changed streams without retaining their history. Unchanged
        // streams reuse exact byte boundaries for paging/polling. Bookmarks live
        // only for this consumer; external append is not proof of prefix integrity.
        private sealed class EventReader : ISessionEventReader
        {
            private readonly ChatStore _store;
            private readonly string _host, _documentKey, _sessionId, _path;
            private readonly object _gate = new object();
            private Boundary _tail, _pageEnd, _highWater;
            private StorageFileState _state;

            internal EventReader(ChatStore store, string host, string documentKey, string sessionId)
            {
                _store = store; _host = host; _documentKey = documentKey; _sessionId = sessionId;
                _path = store.GetSessionPath(host, documentKey, sessionId);
            }

            public SessionEventSlice Read(SessionEventPosition after, SessionEventPosition through, int limit,
                CancellationToken cancellationToken)
            {
                if (after == null) throw new ArgumentNullException(nameof(after));
                if (limit < 1 || limit > 100) throw new ArgumentOutOfRangeException(nameof(limit));
                cancellationToken.ThrowIfCancellationRequested();
                if (after.SessionId != _sessionId || through != null && through.SessionId != _sessionId)
                    return new SessionEventSlice { Reason = ConversationResyncReason.DifferentSession };
                if (after.Sequence < 1 || through != null && after.Sequence > through.Sequence)
                    return new SessionEventSlice { Reason = ConversationResyncReason.InvalidCursor };
                lock (_gate)
                using (_store.AcquirePersistenceRead())
                using (_store.AcquireDocumentLock(_host, _documentKey))
                {
                    try
                    {
                        var current = CaptureStorageFileState(_path);
                        if (current == null) return Failure(ConversationResyncReason.HistoryUnavailable);
                        SessionEventSlice result;
                        var oldTail = _tail;
                        var start = Matches(_pageEnd, after) ? _pageEnd : Matches(oldTail, after) ? oldTail : null;
                        var end = through == null ? null : Matches(_highWater, through) ? _highWater :
                            Matches(oldTail, through) ? oldTail : null;
                        // As with the chat's in-memory projection cache, an external
                        // append cannot vouch for an unchanged, valid prefix.
                        var warm = _state != null && current.ByteLength == _state.ByteLength &&
                            current.LastWriteUtcTicks == _state.LastWriteUtcTicks &&
                            start != null && (through == null || end != null) &&
                            ValidateBoundary(oldTail, current) && ValidateBoundary(start, current) &&
                            (end == null || ValidateBoundary(end, current));
                        if (!warm)
                            result = ReadCold(after, through, limit, cancellationToken);
                        else
                        {
                            end = end ?? _tail;
                            var items = new List<SessionEvent>();
                            var count = (int)Math.Min(limit, end.Position.Sequence - start.Position.Sequence);
                            _pageEnd = start;
                            if (count > 0)
                                Scan(start.NextOffset, start, count, (item, boundary) =>
                                {
                                    HydrateEventData(item, _store.Protection());
                                    items.Add(item); _pageEnd = boundary;
                                }, cancellationToken);
                            _highWater = end;
                            result = new SessionEventSlice { Events = items, HighWater = end.Position, Next = _pageEnd.Position };
                        }
                        var final = CaptureStorageFileState(_path);
                        if (final == null || final.ByteLength != current.ByteLength ||
                            final.LastWriteUtcTicks != current.LastWriteUtcTicks)
                            return Failure(ConversationResyncReason.HistoryUnavailable);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (result.Reason == ConversationResyncReason.None) _state = final;
                        else Reset();
                        return result;
                    }
                    catch { Reset(); throw; }
                }
            }

            private SessionEventSlice ReadCold(SessionEventPosition after, SessionEventPosition through, int limit,
                CancellationToken token)
            {
                Reset();
                Interlocked.Increment(ref _store._eventReaderFullValidationCount);
                Boundary start = null, end = null;
                var items = new List<SessionEvent>();
                Scan(0, null, null, (item, boundary) =>
                {
                    _tail = boundary;
                    if (Matches(boundary, after)) start = boundary;
                    if (through != null && Matches(boundary, through)) end = boundary;
                    if (item.Sequence > after.Sequence && (through == null || item.Sequence <= through.Sequence) && items.Count < limit)
                    {
                        HydrateEventData(item, _store.Protection());
                        items.Add(item); _pageEnd = boundary;
                    }
                }, token);
                if (_tail == null) return Failure(ConversationResyncReason.HistoryUnavailable);
                if (start == null || through != null && end == null) return Failure(ConversationResyncReason.HistoryChanged);
                _pageEnd = _pageEnd ?? start;
                _highWater = end ?? _tail;
                return new SessionEventSlice { Events = items, HighWater = _highWater.Position, Next = _pageEnd.Position };
            }

            private void Scan(long offset, Boundary previous, int? count, Action<SessionEvent, Boundary> accept,
                CancellationToken token)
            {
                var predecessor = previous == null ? null : new SessionEvent
                { SessionId = _sessionId, Sequence = previous.Position.Sequence, Hash = previous.Position.Hash };
                var protector = _store.Protection();
                try
                {
                    var summary = JsonlRecordReader.Read(_path, offset, ParseSessionEvent, (item, line) =>
                    {
                        ValidateEvent(predecessor, item, protector);
                        if (item.SessionId != _sessionId) throw new ChatConcurrencyException("The event stream belongs to another session.");
                        item.StorageByteOffset = line.Offset;
                        accept(item, new Boundary { Position = new SessionEventPosition(item.SessionId, item.Sequence, item.Hash),
                            Offset = line.Offset, NextOffset = line.NextOffset });
                        predecessor = item;
                    }, count, token);
                    if (summary.HasIncompleteTail) throw new ChatConcurrencyException("The event stream has an incomplete tail.");
                }
                catch (JsonlRecordException) { throw new ChatConcurrencyException("The event stream contains an invalid record."); }
                catch (DecoderFallbackException) { throw new ChatConcurrencyException("The event stream contains invalid UTF-8."); }
            }

            private bool ValidateBoundary(Boundary boundary, StorageFileState state)
            {
                return boundary != null && _store.ReadValidatedEventAtOffset(_path, _sessionId, boundary.Position.Sequence,
                    boundary.Position.Hash, boundary.Offset, boundary.NextOffset, state.ByteLength) != null;
            }

            private static bool Matches(Boundary boundary, SessionEventPosition position)
            { return boundary != null && position != null && boundary.Position.Sequence == position.Sequence && boundary.Position.Hash == position.Hash; }

            private SessionEventSlice Failure(ConversationResyncReason reason)
            { Reset(); return new SessionEventSlice { Reason = reason }; }

            private void Reset() { _state = null; _tail = null; _pageEnd = null; _highWater = null; }

            private sealed class Boundary
            {
                internal SessionEventPosition Position;
                internal long Offset, NextOffset;
            }
        }
    }
}
