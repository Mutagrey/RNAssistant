using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    public sealed partial class ChatStore
    {
        private HeaderReadResult ReadHeader(string path)
        {
            HeaderReadResult cached;
            if (TryReadHeaderCache(path, out cached)) return cached;

            var result = ReadHeaderLog(path, 0, null, new ChatHeaderReducer(_blobs));
            if (result != null && result.Tail != null)
            {
                Interlocked.Increment(ref _headerFullReplayCount);
                if (CanCacheHeader(result)) StoreHeaderCache(path, result);
            }
            return result;
        }

        private HeaderReadResult ReadHeaderLog(
            string path,
            long startByteOffset,
            SessionEvent previousEvent,
            ChatHeaderReducer reducer)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var result = new HeaderReadResult
            {
                Reducer = reducer ?? new ChatHeaderReducer(_blobs),
                Tail = previousEvent,
                TailNextByteOffset = startByteOffset
            };
            var protector = Protection();
            var before = CaptureStorageFileState(path);
            try
            {
                var summary = JsonlRecordReader.Read(
                    path,
                    startByteOffset,
                    ParseSessionEvent,
                    (sessionEvent, line) =>
                    {
                        ValidateEvent(previousEvent, sessionEvent, protector);
                        HydrateEventData(sessionEvent, protector);
                        sessionEvent.StorageByteOffset = line.Offset;
                        result.Reducer.Apply(sessionEvent);
                        result.Tail = sessionEvent;
                        previousEvent = sessionEvent;
                    });
                result.ByteLength = summary.ByteLength;
                result.TailNextByteOffset = summary.TailNextByteOffset;
                result.HasIncompleteTail = summary.HasIncompleteTail;
                var after = CaptureStorageFileState(path);
                result.IsStableSnapshot = before != null && after != null &&
                    before.ByteLength == result.ByteLength && after.ByteLength == result.ByteLength &&
                    before.LastWriteUtcTicks == after.LastWriteUtcTicks;
                result.LastWriteUtcTicks = result.IsStableSnapshot ? after.LastWriteUtcTicks : 0;
            }
            catch (JsonlRecordException ex)
            {
                throw new ChatConcurrencyException(ex.Kind == JsonlRecordErrorKind.BlankRecord
                    ? "The chat event log contains a blank record."
                    : "The chat event log contains an invalid record.");
            }
            catch (DecoderFallbackException)
            {
                throw new ChatConcurrencyException("The chat event log contains invalid UTF-8.");
            }
            return result;
        }

        private static bool CanCacheHeader(HeaderReadResult result)
        {
            return result != null && result.Reducer != null && result.Reducer.IsValid &&
                result.Tail != null && !result.HasIncompleteTail && result.IsStableSnapshot &&
                result.TailNextByteOffset == result.ByteLength;
        }

        private bool TryReadHeaderCache(string path, out HeaderReadResult result)
        {
            result = null;
            HeaderCacheEntry cached;
            if (!TryGetHeaderCache(path, out cached)) return false;

            var current = CaptureStorageFileState(path);
            if (current == null || current.ByteLength != cached.ByteLength ||
                current.LastWriteUtcTicks != cached.LastWriteUtcTicks)
            {
                RemoveHeaderCache(path);
                return false;
            }

            var boundary = ReadValidatedEventAtOffset(
                path,
                cached.SessionId,
                cached.Sequence,
                cached.HeadHash,
                cached.TailByteOffset,
                cached.ByteLength,
                current.ByteLength);
            if (boundary == null)
            {
                RemoveHeaderCache(path);
                return false;
            }

            result = new HeaderReadResult
            {
                Reducer = cached.Reducer,
                Tail = boundary,
                ByteLength = cached.ByteLength,
                LastWriteUtcTicks = cached.LastWriteUtcTicks,
                TailNextByteOffset = cached.ByteLength,
                IsStableSnapshot = true
            };
            return true;
        }

        private bool TryGetHeaderCache(string path, out HeaderCacheEntry entry)
        {
            return _headerCache.TryGet(ProjectionCacheKey(path), out entry);
        }

        private HeaderCacheEntry StoreHeaderCache(string path, HeaderReadResult result)
        {
            if (!CanCacheHeader(result)) return null;
            var estimatedCharacters = result.Reducer.EstimatedCharacters;
            var key = ProjectionCacheKey(path);
            var entry = new HeaderCacheEntry
            {
                SessionId = result.Tail.SessionId,
                Sequence = result.Tail.Sequence,
                HeadHash = result.Tail.Hash,
                TailByteOffset = result.Tail.StorageByteOffset,
                ByteLength = result.ByteLength,
                LastWriteUtcTicks = result.LastWriteUtcTicks,
                Reducer = result.Reducer,
                EstimatedCharacters = estimatedCharacters
            };
            _headerCache.Set(key, entry);
            return entry;
        }

        private void RemoveHeaderCache(string path)
        {
            _headerCache.Remove(ProjectionCacheKey(path));
        }

        private void ClearHeaderCache()
        {
            _headerCache.Clear();
        }

        private void MoveHeaderCache(string oldPath, string newPath)
        {
            _headerCache.Move(ProjectionCacheKey(oldPath), ProjectionCacheKey(newPath));
        }

        private void AdvanceHeaderCache(
            string path,
            string sessionId,
            long expectedRevision,
            string expectedHeadHash,
            long expectedByteLength,
            IReadOnlyList<SessionEvent> appended)
        {
            if (appended == null || appended.Count == 0) return;
            HeaderCacheEntry cached;
            if (!TryGetHeaderCache(path, out cached) ||
                cached.Sequence != expectedRevision || cached.ByteLength != expectedByteLength ||
                !string.Equals(cached.SessionId, sessionId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(cached.HeadHash, expectedHeadHash, StringComparison.OrdinalIgnoreCase)) return;

            var previous = new SessionEvent
            {
                SessionId = cached.SessionId,
                Sequence = cached.Sequence,
                Hash = cached.HeadHash,
                StorageByteOffset = cached.TailByteOffset
            };
            HeaderReadResult suffix;
            try
            {
                suffix = ReadHeaderLog(path, cached.ByteLength, previous, cached.Reducer.Clone());
            }
            catch
            {
                RemoveHeaderCache(path);
                return;
            }

            var expectedTail = appended[appended.Count - 1];
            if (!CanCacheHeader(suffix) || suffix.Tail == null ||
                suffix.Tail.Sequence != expectedTail.Sequence ||
                !string.Equals(suffix.Tail.Hash, expectedTail.Hash, StringComparison.OrdinalIgnoreCase))
            {
                RemoveHeaderCache(path);
                return;
            }
            StoreHeaderCache(path, suffix);
            Interlocked.Increment(ref _headerIncrementalReplayCount);
        }

        private SessionEvent ReadValidatedTail(
            string path,
            string sessionId,
            long expectedRevision,
            string expectedHeadHash,
            long expectedByteLength,
            long expectedLastWriteUtcTicks,
            long expectedTailByteOffset)
        {
            try
            {
                if (expectedByteLength <= 0 || expectedLastWriteUtcTicks <= 0 ||
                    expectedTailByteOffset < 0 || expectedTailByteOffset >= expectedByteLength) return null;
                var file = new FileInfo(path);
                if (!file.Exists || file.Length != expectedByteLength ||
                    file.LastWriteTimeUtc.Ticks != expectedLastWriteUtcTicks) return null;
                return ReadValidatedEventAtOffset(path, sessionId, expectedRevision, expectedHeadHash,
                    expectedTailByteOffset, expectedByteLength, expectedByteLength);
            }
            catch (Exception ex) when (
                ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentOutOfRangeException ||
                ex is JsonException || ex is DecoderFallbackException || ex is CryptographicException)
            {
                return null;
            }
        }

        private SessionEvent ReadValidatedEventAtOffset(
            string path,
            string sessionId,
            long expectedRevision,
            string expectedHeadHash,
            long expectedByteOffset,
            long expectedNextByteOffset,
            long expectedSnapshotLength)
        {
            try
            {
                JsonlByteLine line;
                using (var reader = new JsonlByteReader(path, expectedByteOffset))
                {
                    if (reader.Length != expectedSnapshotLength) return null;
                    line = reader.ReadLine();
                    if (line == null || !line.Terminated || line.NextOffset != expectedNextByteOffset ||
                        string.IsNullOrWhiteSpace(line.Text)) return null;
                }

                var sessionEvent = ParseSessionEvent(line.Text);
                var protector = Protection();
                if (sessionEvent == null || sessionEvent.SchemaVersion != SessionEvent.CurrentSchemaVersion ||
                    sessionEvent.Sequence != expectedRevision ||
                    !string.Equals(sessionEvent.SessionId, sessionId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(sessionEvent.Hash, expectedHeadHash, StringComparison.OrdinalIgnoreCase) ||
                    !EventProtectionSupport.IsSupportedHashAlgorithm(sessionEvent.HashAlgorithm) ||
                    !string.IsNullOrWhiteSpace(sessionEvent.EncryptedData) && sessionEvent.Data != null ||
                    !EventProtectionSupport.Matches(
                        protector,
                        sessionEvent.HashAlgorithm,
                        sessionEvent.ProtectionKeyId,
                        sessionEvent.EncryptedData) ||
                    !string.Equals(sessionEvent.Hash, ComputeHash(sessionEvent, protector), StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
                sessionEvent.StorageByteOffset = line.Offset;
                return sessionEvent;
            }
            catch (Exception ex) when (
                ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentOutOfRangeException ||
                ex is JsonException || ex is DecoderFallbackException || ex is CryptographicException)
            {
                return null;
            }
        }

        private static void CaptureStorageState(ChatSession session, string path)
        {
            if (session == null) return;
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                session.StorageByteLength = 0;
                session.StorageLastWriteUtcTicks = 0;
                return;
            }
            file.Refresh();
            session.StorageByteLength = file.Length;
            session.StorageLastWriteUtcTicks = file.LastWriteTimeUtc.Ticks;
        }

        private static StorageFileState CaptureStorageFileState(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var file = new FileInfo(path);
            file.Refresh();
            return file.Exists
                ? new StorageFileState
                {
                    ByteLength = file.Length,
                    LastWriteUtcTicks = file.LastWriteTimeUtc.Ticks
                }
                : null;
        }

        private static bool CanCacheProjection(EventLogReadResult log)
        {
            return log != null && log.Events.Count > 0 && !log.HasIncompleteTail &&
                log.IsStableSnapshot && log.TailNextByteOffset == log.ByteLength;
        }

        private bool TryReadProjectionCache(string path, out ProjectionCacheEntry result)
        {
            result = null;
            ProjectionCacheEntry cached;
            if (!TryGetProjectionCache(path, out cached)) return false;

            var current = CaptureStorageFileState(path);
            if (current == null || current.ByteLength != cached.ByteLength ||
                current.LastWriteUtcTicks != cached.LastWriteUtcTicks)
            {
                RemoveProjectionCache(path);
                return false;
            }

            var boundary = ReadValidatedEventAtOffset(
                path,
                cached.SessionId,
                cached.Sequence,
                cached.HeadHash,
                cached.TailByteOffset,
                cached.ByteLength,
                current.ByteLength);
            if (boundary == null)
            {
                RemoveProjectionCache(path);
                return false;
            }

            result = cached;
            return true;
        }

        private static bool IsProjectionEvent(SessionEvent sessionEvent)
        {
            return sessionEvent != null &&
                (string.Equals(sessionEvent.Type, SessionEventTypes.SessionCreated, StringComparison.Ordinal) ||
                 string.Equals(sessionEvent.Type, SessionEventTypes.SessionForked, StringComparison.Ordinal) ||
                 string.Equals(sessionEvent.Type, SessionEventTypes.SessionCommit, StringComparison.Ordinal));
        }

        private bool TryGetProjectionCache(string path, out ProjectionCacheEntry entry)
        {
            return _projectionCache.TryGet(ProjectionCacheKey(path), out entry);
        }

        private void StoreProjectionCache(string path, JObject root, ChatSession session)
        {
            if (session == null) return;
            StoreProjectionCache(path, root, session.Id, session.Revision, session.StorageHeadHash,
                session.StorageTailByteOffset, session.StorageByteLength, session.StorageLastWriteUtcTicks);
        }

        private ProjectionCacheEntry StoreProjectionCache(
            string path,
            JObject root,
            string sessionId,
            long sequence,
            string headHash,
            long tailByteOffset,
            long byteLength,
            long lastWriteUtcTicks)
        {
            if (root == null || string.IsNullOrWhiteSpace(sessionId) || sequence <= 0 ||
                string.IsNullOrWhiteSpace(headHash) || tailByteOffset < 0 ||
                byteLength <= tailByteOffset || lastWriteUtcTicks <= 0) return null;
            var key = ProjectionCacheKey(path);
            var estimatedCharacters = EstimateProjectionCharacters(root, MaxProjectionCacheCharacters + 1);
            var entry = new ProjectionCacheEntry
            {
                SessionId = sessionId,
                Sequence = sequence,
                HeadHash = headHash,
                TailByteOffset = tailByteOffset,
                ByteLength = byteLength,
                LastWriteUtcTicks = lastWriteUtcTicks,
                Root = root,
                EstimatedCharacters = estimatedCharacters
            };
            _projectionCache.Set(key, entry);
            return entry;
        }

        private static long EstimateProjectionCharacters(JToken root, long stopAfter)
        {
            long total = 0;
            var pending = new Stack<JToken>();
            pending.Push(root);
            while (pending.Count > 0 && total <= stopAfter)
            {
                var token = pending.Pop();
                var objectValue = token as JObject;
                if (objectValue != null)
                {
                    foreach (var property in objectValue.Properties())
                    {
                        total += property.Name.Length + 4L;
                        pending.Push(property.Value);
                    }
                    continue;
                }
                var arrayValue = token as JArray;
                if (arrayValue != null)
                {
                    total += arrayValue.Count;
                    foreach (var value in arrayValue) pending.Push(value);
                    continue;
                }
                var scalar = token as JValue;
                var text = scalar == null || scalar.Value == null ? null : scalar.Value as string;
                total += text == null ? 32L : text.Length + 2L;
            }
            return total;
        }

        private void AdvanceProjectionCache(
            string path,
            string sessionId,
            long expectedRevision,
            string expectedHeadHash,
            long expectedByteLength,
            IReadOnlyList<SessionEvent> appended)
        {
            if (appended == null || appended.Count == 0) return;
            ProjectionCacheEntry cached;
            if (!TryGetProjectionCache(path, out cached) ||
                cached.Sequence != expectedRevision || cached.ByteLength != expectedByteLength ||
                !string.Equals(cached.SessionId, sessionId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(cached.HeadHash, expectedHeadHash, StringComparison.OrdinalIgnoreCase)) return;
            var state = CaptureStorageFileState(path);
            if (state == null) return;
            var root = appended.Any(IsProjectionEvent)
                ? ReplayProjectionRoot(appended, cached.Root)
                : cached.Root;
            var tail = appended[appended.Count - 1];
            if (root == null || tail.StorageByteOffset >= state.ByteLength)
            {
                RemoveProjectionCache(path);
                return;
            }
            StoreProjectionCache(path, root, tail.SessionId, tail.Sequence, tail.Hash,
                tail.StorageByteOffset, state.ByteLength, state.LastWriteUtcTicks);
            Interlocked.Increment(ref _projectionIncrementalReplayCount);
        }

        private void RemoveProjectionCache(string path)
        {
            _projectionCache.Remove(ProjectionCacheKey(path));
        }

        private void ClearProjectionCache()
        {
            _projectionCache.Clear();
        }

        private void MoveProjectionCache(string oldPath, string newPath)
        {
            _projectionCache.Move(ProjectionCacheKey(oldPath), ProjectionCacheKey(newPath));
        }

        private static string ProjectionCacheKey(string path)
        {
            return Path.GetFullPath(path ?? string.Empty);
        }

    }
}
