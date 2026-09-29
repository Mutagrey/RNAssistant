using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    public sealed class ResourceMutationJournal
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private readonly string _path;
        private readonly object _sync = new object();
        private Dictionary<string, MutationAttempt> _latest;
        private long _readLength;
        private long _lastWriteTicks;
        private long _creationTicks;
        private int _readLineCount;
        private long _projectionVersion;
        private long _unresolvedVersion = -1;
        private IReadOnlyList<MutationAttempt> _unresolved;

        public ResourceMutationJournal(AppDataPaths paths)
        {
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            _path = Path.Combine(paths.ResourceAuthorityDirectory, "mutation-attempts.jsonl");
        }

        public MutationAttempt Prepare(ResourceAuthorityScopeId scope, string operation,
            ResourceIdentity target, string expectedRevision = null, PayloadRef payload = null,
            string semanticHash = null, IEnumerable<ResourceImpact> intendedImpacts = null)
        {
            var attempt = MutationAttempt.Prepare(scope, operation, target,
                expectedRevision, payload, semanticHash, intendedImpacts);
            lock (_sync)
            using (StorageFileSystem.AcquireWriteLock(_path + ".lck")) Append(attempt);
            return attempt;
        }

        public MutationAttempt MarkDispatchMayHaveOccurred(string attemptId)
        {
            lock (_sync)
            using (StorageFileSystem.AcquireWriteLock(_path + ".lck"))
            {
                var current = Require(attemptId);
                if (current.State == MutationAttemptState.DispatchMayHaveOccurred) return current;
                var next = current.Transition(MutationAttemptState.DispatchMayHaveOccurred);
                Append(next);
                return next;
            }
        }

        public MutationAttempt Resolve(string attemptId, string authorityCommitId)
        {
            lock (_sync)
            using (StorageFileSystem.AcquireWriteLock(_path + ".lck"))
            {
                var current = Require(attemptId);
                if (current.State == MutationAttemptState.Resolved)
                {
                    if (!string.Equals(current.LinkedAuthorityCommitId, authorityCommitId, StringComparison.Ordinal))
                        throw new InvalidOperationException("Mutation attempt is linked to a different authority commit.");
                    return current;
                }
                var next = current.Transition(MutationAttemptState.Resolved, authorityCommitId);
                Append(next);
                return next;
            }
        }

        public MutationAttempt AbandonBeforeDispatch(string attemptId)
        {
            lock (_sync)
            using (StorageFileSystem.AcquireWriteLock(_path + ".lck"))
            {
                var current = Require(attemptId);
                if (current.State == MutationAttemptState.AbandonedBeforeDispatch) return current;
                var next = current.Transition(MutationAttemptState.AbandonedBeforeDispatch);
                Append(next);
                return next;
            }
        }

        public IReadOnlyList<MutationAttempt> Unresolved()
        {
            lock (_sync)
            using (StorageFileSystem.AcquireWriteLock(_path + ".lck"))
            {
                var latest = ReadLatest();
                if (_unresolvedVersion != _projectionVersion)
                {
                    _unresolved = latest.Values.Where(item => item.State == MutationAttemptState.Prepared ||
                        item.State == MutationAttemptState.DispatchMayHaveOccurred)
                        .OrderBy(item => item.PreparedAt)
                        .ToArray();
                    _unresolvedVersion = _projectionVersion;
                }
                return _unresolved;
            }
        }

        // A live mutation owns this short scope lease after confirmation and until
        // publication. Process death releases it; recovery never races a live writer.
        public IDisposable AcquireScope(ResourceAuthorityScopeId scope, bool waitForOwner = false)
        {
            var directory = Path.GetDirectoryName(_path);
            StorageFileSystem.EnsureRegularDirectory(directory);
            var path = Path.Combine(directory, "mutation-" +
                RNAssistant.Core.Tools.TextPatternEngine.Sha256(scope.ToString()) + ".lck");
            if (waitForOwner) return StorageFileSystem.AcquireWriteLock(path);
            return new FileStream(path,
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        private MutationAttempt Require(string attemptId)
        {
            MutationAttempt value;
            if (string.IsNullOrWhiteSpace(attemptId) || !ReadLatest().TryGetValue(attemptId, out value))
                throw new KeyNotFoundException("Mutation attempt was not found: " + attemptId);
            return value;
        }

        internal void ScanCasReferences(CasReachabilityScan scan)
        {
            lock (_sync)
            using (StorageFileSystem.AcquireWriteLock(_path + ".lck"))
            {
                try
                {
                    foreach (var attempt in ReadLatest().Values)
                        if (attempt.Payload != null) scan.AddReference(attempt.Payload.ToBlobReference(),
                            "resource-mutation", attempt.AttemptId, "prepared.payload");
                }
                catch (Exception ex) when (ex is IOException || ex is JsonException || ex is ArgumentException || ex is InvalidOperationException)
                { scan.AddSourceIssue("resource_mutation_invalid", "resource-mutation", Path.GetFileName(_path), ex.Message); }
            }
        }

        private Dictionary<string, MutationAttempt> ReadLatest()
        {
            // Callers hold both the instance lock and the journal's cross-process writer
            // lock. Replaying the entire journal for every authority capture makes each
            // model step proportional to every mutation ever recorded.
            var file = new FileInfo(_path);
            if (!file.Exists)
            {
                if (_latest != null && _readLength == 0 && _lastWriteTicks == 0) return _latest;
                _latest = new Dictionary<string, MutationAttempt>(StringComparer.Ordinal);
                _readLength = _lastWriteTicks = _creationTicks = 0;
                _readLineCount = 0;
                _projectionVersion++;
                return _latest;
            }
            var length = file.Length;
            var written = file.LastWriteTimeUtc.Ticks;
            var created = file.CreationTimeUtc.Ticks;
            if (_latest != null && length == _readLength && written == _lastWriteTicks && created == _creationTicks)
                return _latest;
            var append = _latest != null && length > _readLength && created == _creationTicks;
            var result = append ? _latest : new Dictionary<string, MutationAttempt>(StringComparer.Ordinal);
            var updates = append ? new Dictionary<string, MutationAttempt>(StringComparer.Ordinal) : null;
            var offset = append ? _readLength : 0;
            var lineNumber = append ? _readLineCount : 0;
            using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (stream.Length != length) throw new InvalidDataException("Mutation attempt journal changed during replay.");
                if (length > 0)
                {
                    stream.Seek(-1, SeekOrigin.End);
                    if (stream.ReadByte() != '\n')
                        throw new InvalidDataException("Mutation attempt journal has an incomplete terminal record.");
                }
                stream.Seek(offset, SeekOrigin.Begin);
                using (var reader = new StreamReader(stream, Utf8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        lineNumber++;
                        MutationAttempt attempt;
                        try { attempt = JsonConvert.DeserializeObject<MutationAttempt>(line); }
                        catch (JsonException ex)
                        {
                            throw new InvalidDataException("Mutation attempt journal contains an invalid record at line " + lineNumber + ".", ex);
                        }
                        if (attempt == null || string.IsNullOrWhiteSpace(attempt.AttemptId))
                            throw new InvalidDataException("Mutation attempt journal contains an incomplete record.");
                        MutationAttempt previous;
                        if (updates != null && updates.TryGetValue(attempt.AttemptId, out previous) ||
                            result.TryGetValue(attempt.AttemptId, out previous)) ValidateTransition(previous, attempt);
                        else if (attempt.State != MutationAttemptState.Prepared)
                            throw new InvalidDataException("Mutation attempt journal does not start with Prepared.");
                        if (updates == null) result[attempt.AttemptId] = attempt;
                        else updates[attempt.AttemptId] = attempt;
                    }
                }
            }
            if (updates != null)
                foreach (var update in updates) result[update.Key] = update.Value;
            _latest = result;
            _readLength = length;
            _lastWriteTicks = written;
            _creationTicks = created;
            _readLineCount = lineNumber;
            _projectionVersion++;
            return _latest;
        }

        private static void ValidateTransition(MutationAttempt previous, MutationAttempt next)
        {
            if (!previous.ScopeId.Equals(next.ScopeId) || !previous.Target.Equals(next.Target) ||
                JsonConvert.SerializeObject(previous.IntendedImpacts) != JsonConvert.SerializeObject(next.IntendedImpacts) ||
                JsonConvert.SerializeObject(previous.Payload) != JsonConvert.SerializeObject(next.Payload) ||
                previous.ExpectedRevision != next.ExpectedRevision || previous.IntendedSemanticHash != next.IntendedSemanticHash ||
                !string.Equals(previous.Operation, next.Operation, StringComparison.Ordinal))
                throw new InvalidDataException("Mutation attempt identity changed during replay.");
            if (previous.State == MutationAttemptState.Prepared &&
                next.State != MutationAttemptState.DispatchMayHaveOccurred &&
                next.State != MutationAttemptState.AbandonedBeforeDispatch ||
                previous.State == MutationAttemptState.DispatchMayHaveOccurred &&
                next.State != MutationAttemptState.Resolved ||
                (previous.State == MutationAttemptState.Resolved || previous.State == MutationAttemptState.AbandonedBeforeDispatch))
                throw new InvalidDataException("Mutation attempt journal contains an invalid transition.");
        }

        private void Append(MutationAttempt attempt)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            var bytes = Utf8.GetBytes(JsonConvert.SerializeObject(attempt, Formatting.None) + "\n");
            using (var stream = new FileStream(_path, FileMode.Append, FileAccess.Write,
                FileShare.Read, 8192, FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }
    }
}
