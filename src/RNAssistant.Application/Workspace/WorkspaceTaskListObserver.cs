using RNAssistant.Core.Persistence;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;

namespace RNAssistant.Runtime
{
    // The surrounding application owns the session lease. This short authority
    // lease covers preparation, durable artifact save, read-back and publication.
    internal sealed class WorkspaceTaskListObserver : IToolMutationObserver
    {
        private readonly ChatSession _session;
        private readonly IConversationStore _chats;
        private readonly ChatBlobStore _payloads;
        private readonly IResourceAuthorityStore _authority;
        private readonly IResourceRevisionStore _revisions;
        private readonly IResourceMutationJournal _journal;
        private readonly Func<ToolExecutionContext, ResourceMutationOrigin> _origin;
        private MutationAttempt _attempt;
        private IDisposable _lease;
        private HashSet<string> _previousArtifacts;

        internal WorkspaceTaskListObserver(AppDataPaths paths, IConversationStore chats, ChatBlobStore payloads, ChatSession session,
            IResourceAuthorityStore authority = null, IResourceMutationJournal journal = null,
            Func<ToolExecutionContext, ResourceMutationOrigin> origin = null)
        {
            _session = session; _chats = chats; _payloads = payloads;
            if ((authority == null) != (journal == null)) throw new ArgumentException("Resource authority and mutation journal must be supplied together.");
            _authority = authority ?? new ResourceAuthorityStore(paths); _journal = journal ?? new ResourceMutationJournal(paths);
            _revisions = _authority as IResourceRevisionStore ?? throw new ArgumentException("Task-list authority must retain its revisions.");
            _origin = origin;
        }

        internal void ReconcileInterrupted()
        {
            if (_session == null) return;
            using (_journal.AcquireScope(TaskListPublication.Scope(_session))) ReconcileUnderLease();
        }

        internal void CommitFork()
        {
            if (_attempt != null || _session.Revision != 0 || string.IsNullOrEmpty(_session.ParentSessionId) ||
                !_session.ParentSessionRevision.HasValue || string.IsNullOrEmpty(_session.ForkedThroughMessageId))
                throw new InvalidOperationException("Task fork publication requires an unpublished child.");
            var scope = TaskListPublication.Scope(_session);
            using (_journal.AcquireScope(scope))
            {
                if (_authority.Capture(scope).Generation != 0)
                    throw new InvalidOperationException("Task fork authority already exists.");
                var expected = TaskListPublication.Capture(_session, _payloads);
                var state = new ForkState { ParentId = _session.ParentSessionId, ParentRevision = _session.ParentSessionRevision.Value,
                    Boundary = _session.ForkedThroughMessageId, Snapshot = expected.Dependencies.SingleOrDefault()?.Resource,
                    ContentSha256 = expected.ContentSha256 };
                var attempt = _journal.Prepare(scope, TaskListPublication.ForkOperation, expected.Identity,
                    payload: PayloadRef.FromBlob(_payloads.StoreText(JsonConvert.SerializeObject(state), "application/json")),
                    intendedImpacts: new[] { new ResourceImpact(expected.Identity, ResourceImpactRelation.ContainerMembership) });
                attempt = _journal.MarkDispatchMayHaveOccurred(attempt.AttemptId);
                _chats.Commit(_session, new ConversationCommit());
                TaskListPublication.PublishFork(_authority, _revisions, _journal, attempt, ReadBackFork(attempt));
            }
        }

        private ResourceMutationReadBack ReadBackFork(MutationAttempt attempt)
        {
            var state = attempt.Payload == null ? null : JsonConvert.DeserializeObject<ForkState>(_payloads.ReadText(attempt.Payload.ToBlobReference()));
            var saved = _chats.Load(_session.Host, _session.DocumentKey, attempt.ScopeId.Id);
            if (state == null || string.IsNullOrEmpty(state.ParentId) || state.ParentRevision < 1 || string.IsNullOrEmpty(state.Boundary) ||
                saved == null || saved.Host != _session.Host || saved.WorkspaceId != _session.WorkspaceId ||
                saved.ParentSessionId != state.ParentId || saved.ParentSessionRevision != state.ParentRevision || saved.ForkedThroughMessageId != state.Boundary)
                throw new InvalidDataException("The saved task fork differs from its prepared source.");
            var captured = TaskListPublication.Capture(saved, _payloads);
            var reference = captured.Dependencies.SingleOrDefault()?.Resource;
            if (reference?.Uri != state.Snapshot?.Uri || reference?.Revision != state.Snapshot?.Revision ||
                captured.ContentSha256 != state.ContentSha256)
                throw new InvalidDataException("The saved task fork differs from its prepared snapshot.");
            return captured;
        }

        private sealed class ForkState
        {
            public string ParentId { get; set; }
            public long ParentRevision { get; set; }
            public string Boundary { get; set; }
            public ResourceRef Snapshot { get; set; }
            public string ContentSha256 { get; set; }
        }

        private void ReconcileUnderLease()
        {
            foreach (var pending in _journal.Unresolved().Where(item =>
                item.ScopeId.Equals(TaskListPublication.Scope(_session)) &&
                (item.Operation == TaskListToolCatalog.SetToolId || item.Operation == TaskListPublication.ForkOperation)))
            {
                if (pending.State == MutationAttemptState.Prepared) _journal.AbandonBeforeDispatch(pending.AttemptId);
                else if (pending.Operation == TaskListPublication.ForkOperation)
                {
                    ResourceMutationReadBack captured = null;
                    try { captured = ReadBackFork(pending); }
                    catch (Exception error) when (error is IOException || error is InvalidDataException || error is InvalidOperationException || error is JsonException || error is ArgumentException)
                    { /* An unavailable exact read-back remains Unknown; never recreate or edit the fork. */ }
                    TaskListPublication.PublishFork(_authority, _revisions, _journal, pending, captured);
                }
                else TaskListPublication.Publish(_authority, _revisions, _journal, pending, null, null);
            }
        }

        internal TaskListStateProjection Project(ResourceAuthoritySnapshotSet frozen)
        {
            return TaskListPublication.Project(_session, frozen.Get(TaskListPublication.Scope(_session)), _revisions, _payloads);
        }

        internal TaskListStateProjection Inspect()
        {
            var scope = TaskListPublication.Scope(_session);
            if (_journal.Unresolved().Any(item => item.ScopeId.Equals(scope) &&
                (item.Operation == TaskListToolCatalog.SetToolId || item.Operation == TaskListPublication.ForkOperation) &&
                item.State == MutationAttemptState.DispatchMayHaveOccurred))
                return new TaskListStateProjection { Available = false };
            return Project(_authority.CaptureMany(new[] { scope }));
        }

        public string Prepare(ToolExecutionContext context, IDictionary<string, object> arguments, string preparedStateJson = null)
        {
            if (!TaskListToolCatalog.Owns(context.Call.Name)) return null; // File mutations have their own source owner.
            if (_attempt != null) throw new InvalidOperationException("A task-list mutation is already active.");
            _lease = _journal.AcquireScope(TaskListPublication.Scope(_session));
            try
            {
                ReconcileUnderLease();
                var scope = TaskListPublication.Scope(_session);
                var snapshot = _authority.Capture(scope);
                TaskListPublication.RequireCurrent(_session, snapshot, _revisions, _payloads);
                var identity = TaskListPublication.Identity(_session);
                _previousArtifacts = new HashSet<string>(_session.Artifacts.Select(item => item.Id), StringComparer.Ordinal);
                _attempt = _journal.Prepare(scope, context.Call.Name, identity,
                    snapshot.GetHead(identity)?.Revision?.Revision,
                    PayloadRef.FromBlob(_payloads.StoreText(context.Call.ArgumentsJson, "application/json")),
                    intendedImpacts: new[] { new ResourceImpact(identity, ResourceImpactRelation.ContainerMembership) }, origin: _origin?.Invoke(context));
                return _attempt.AttemptId;
            }
            catch { Release(); throw; }
        }

        public void MarkDispatchMayHaveOccurred(string attemptId)
        { RequireAttempt(attemptId); _attempt = _journal.MarkDispatchMayHaveOccurred(attemptId); }

        public ResourceAuthorityCommit Complete(string attemptId, ToolExecutionRecord record, string preparedStateJson = null)
        {
            RequireAttempt(attemptId);
            if (_attempt.State != MutationAttemptState.DispatchMayHaveOccurred)
                throw new InvalidOperationException("Task-list dispatch marker is missing.");
            try
            {
                ResourceMutationReadBack captured = null;
                if (record.Evidence?.Effect == ToolEffectEvidence.VerifiedChange)
                {
                    var added = _session.Artifacts.Where(item => !_previousArtifacts.Contains(item.Id)).ToArray();
                    if (added.Length != 1 || added[0].Kind != ChatArtifactKinds.TaskList)
                        throw new InvalidDataException("Task-list mutation must append exactly one revision.");
                    _chats.Commit(_session, new ConversationCommit());
                    var saved = _chats.Load(_session.Host, _session.DocumentKey, _session.Id);
                    var retained = saved?.Artifacts.SingleOrDefault(item => item.Id == added[0].Id);
                    if (saved == null || retained == null || saved.WorkspaceId != _session.WorkspaceId ||
                        saved.ActiveTaskListArtifactId != _session.ActiveTaskListArtifactId ||
                        retained?.ContentSha256 != added[0].ContentSha256 || TaskListPublication.ReadBody(retained, _payloads) != TaskListPublication.ReadBody(added[0], _payloads) ||
                        retained.Revision != added[0].Revision || retained.ParentArtifactId != added[0].ParentArtifactId)
                        throw new InvalidDataException("Task-list revision failed durable read-back.");
                    captured = TaskListPublication.Capture(saved, _payloads);
                }
                return TaskListPublication.Publish(_authority, _revisions, _journal, _attempt, record, captured);
            }
            finally { Release(); }
        }

        public void AbandonBeforeDispatch(string attemptId)
        { RequireAttempt(attemptId); try { _journal.AbandonBeforeDispatch(attemptId); } finally { Release(); } }

        public void ReleaseUnresolved(string attemptId)
        {
            // Complete can fail after durable save/publication; never repeat its mutation.
            if (_attempt != null) { RequireAttempt(attemptId); Release(); }
        }

        private void RequireAttempt(string id)
        { if (_attempt?.AttemptId != id) throw new InvalidOperationException("Task-list mutation attempt is missing."); }

        private void Release()
        { _attempt = null; _previousArtifacts = null; _lease?.Dispose(); _lease = null; }
    }
}
