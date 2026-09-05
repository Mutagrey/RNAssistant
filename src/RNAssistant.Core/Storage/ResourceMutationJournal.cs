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
                return ReadLatest().Values.Where(item => item.State == MutationAttemptState.Prepared ||
                    item.State == MutationAttemptState.DispatchMayHaveOccurred)
                    .OrderBy(item => item.PreparedAt)
                    .ToArray();
            }
        }

        // A live mutation owns this short scope lease after confirmation and until
        // publication. Process death releases it; recovery never races a live writer.
        public IDisposable AcquireScope(ResourceAuthorityScopeId scope)
        {
            var directory = Path.GetDirectoryName(_path);
            StorageFileSystem.EnsureRegularDirectory(directory);
            return new FileStream(Path.Combine(directory, "mutation-" +
                RNAssistant.Core.Tools.TextPatternEngine.Sha256(scope.ToString()) + ".lck"),
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
            var result = new Dictionary<string, MutationAttempt>(StringComparer.Ordinal);
            if (!File.Exists(_path)) return result;
            var lineNumber = 0;
            foreach (var line in File.ReadLines(_path, Utf8))
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
                if (result.TryGetValue(attempt.AttemptId, out previous)) ValidateTransition(previous, attempt);
                else if (attempt.State != MutationAttemptState.Prepared)
                    throw new InvalidDataException("Mutation attempt journal does not start with Prepared.");
                result[attempt.AttemptId] = attempt;
            }
            return result;
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
