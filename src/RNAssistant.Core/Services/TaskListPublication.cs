using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Services
{
    // Task lists remain conversation-owned immutable snapshots and CAS bodies. This owns
    // their logical authority publication, shared by Office and workspace hosts.
    public static class TaskListPublication
    {
        public const int MaxBodyBytes = 128 * 1024;
        public const string ForkOperation = "common.chat_fork";
        public const string EditOperation = "common.chat_edit";
        public const string DeleteMessageOperation = "common.chat_delete_message";

        public static bool IsHistoryReset(string operation)
        { return operation == EditOperation || operation == DeleteMessageOperation; }

        public static void PrepareFork(ChatSession source, ChatSession target, Func<ChatSession, string, bool> loadArtifactBody)
        {
            if (source == null || target == null || source.Id == target.Id || target.ParentSessionId != source.Id)
                throw new InvalidOperationException("An explicit source and child chat are required.");
            var tasks = source.Artifacts.Where(item => item.Kind == ChatArtifactKinds.TaskList)
                .ToDictionary(item => item.Id, StringComparer.Ordinal);
            var retained = new HashSet<string>(StringComparer.Ordinal);
            foreach (var reference in target.Messages.SelectMany(item => item.ResourceRefs ?? new List<ResourceRef>()))
            {
                string owner, id; int revision; ChatArtifact artifact;
                if (ChatResourceUri.TryParseArtifactRevision(reference, out owner, out id, out revision) &&
                    (owner == source.Id || owner == target.Id) && tasks.TryGetValue(id, out artifact) && artifact.Revision == revision)
                    retained.Add(id);
            }
            var pending = new Queue<string>(retained);
            while (pending.Count != 0)
            {
                var parent = tasks[pending.Dequeue()].ParentArtifactId;
                if (parent == null || !retained.Add(parent)) continue;
                if (!tasks.ContainsKey(parent)) throw new InvalidOperationException("The task-list parent is unavailable.");
                pending.Enqueue(parent);
            }
            target.Artifacts.RemoveAll(item => item.Kind == ChatArtifactKinds.TaskList);
            target.Artifacts.AddRange(source.Artifacts.Where(item => retained.Contains(item.Id)).Select(item => item.CopyMetadata()));
            target.ActiveTaskListArtifactId = TaskListService.SelectForMessages(target.Messages, target.Artifacts);
            if (target.ActiveTaskListArtifactId == null) return;
            var selected = tasks[target.ActiveTaskListArtifactId]; var previousBody = selected.InlineText;
            try
            {
                if (loadArtifactBody == null || !loadArtifactBody(source, selected.Id))
                    throw new InvalidOperationException("RESOURCE_SNAPSHOT_UNAVAILABLE: the selected task list is unavailable.");
                target.Artifacts.Single(item => item.Id == selected.Id).InlineText = selected.InlineText;
            }
            finally { selected.InlineText = previousBody; }
        }

        public static string ReadBody(ChatArtifact artifact, ChatBlobStore payloads)
        {
            if (artifact?.Kind != ChatArtifactKinds.TaskList) throw new InvalidDataException("Task-list snapshot is unavailable.");
            var body = artifact.InlineText;
            if (body == null && artifact.ContentByteLength.HasValue && artifact.ContentByteLength <= MaxBodyBytes)
                body = payloads.ReadText(new ChatBlobReference { Sha256 = artifact.ContentSha256,
                    ByteLength = artifact.ContentByteLength.Value, ContentType = artifact.MimeType });
            if (body == null || System.Text.Encoding.UTF8.GetByteCount(body) > MaxBodyBytes ||
                artifact.ContentSha256 != null && (TextPatternEngine.Sha256(body) != artifact.ContentSha256 ||
                    System.Text.Encoding.UTF8.GetByteCount(body) != artifact.ContentByteLength))
                throw new InvalidDataException("Task-list body is unavailable or differs from its snapshot.");
            return body;
        }

        public static void HydrateActive(ChatSession session, ChatBlobStore payloads)
        {
            if (string.IsNullOrEmpty(session.ActiveTaskListArtifactId)) return;
            var artifact = session.Artifacts.SingleOrDefault(item => item.Id == session.ActiveTaskListArtifactId);
            var body = ReadBody(artifact, payloads);
            artifact.InlineText = body;
        }

        public static ResourceAuthorityScopeId Scope(ChatSession session)
        {
            if (string.IsNullOrWhiteSpace(session?.Id)) throw new InvalidOperationException("A persisted chat is required.");
            return new ResourceAuthorityScopeId("conversation", session.Id);
        }

        public static ResourceIdentity Identity(ChatSession session)
        { return new ResourceIdentity(ResourceUri.Create("state", "conversation", Scope(session).Id, "task-list")); }

        public static ResourceMutationReadBack Capture(ChatSession session, ChatBlobStore payloads)
        {
            var identity = Identity(session);
            if (string.IsNullOrWhiteSpace(session.ActiveTaskListArtifactId))
                return new ResourceMutationReadBack(identity, false);
            var artifact = session.Artifacts.SingleOrDefault(item => item.Id == session.ActiveTaskListArtifactId);
            if (artifact?.Kind != ChatArtifactKinds.TaskList)
                throw new InvalidDataException("The active task-list snapshot is unavailable.");
            HydrateActive(session, payloads);
            var body = artifact.InlineText;
            var payload = PayloadRef.FromBlob(payloads.StoreText(body, artifact.MimeType ?? "application/json"));
            return new ResourceMutationReadBack(identity, true, "text", payload.Sha256, payload,
                dependencies: new[] { new ResourceDependency(ChatResourceUri.CreateArtifactRevision(session, artifact), kind: "immutable-snapshot") });
        }

        public static TaskListStateProjection Project(ChatSession session, ResourceAuthoritySnapshot snapshot,
            IResourceRevisionStore revisions, ChatBlobStore payloads)
        {
            try { RequireCurrent(session, snapshot, revisions, payloads); }
            catch (Exception error) when (error is InvalidOperationException || error is IOException)
            { return new TaskListStateProjection { Available = false }; }
            return TaskListService.ProjectCurrent(session);
        }

        public static void RequireCurrent(ChatSession session, ResourceAuthoritySnapshot snapshot,
            IResourceRevisionStore revisions, ChatBlobStore payloads)
        {
            if (!Scope(session).Equals(snapshot?.ScopeId))
                throw new InvalidOperationException("Task-list authority belongs to another conversation.");
            var captured = Capture(session, payloads);
            var head = snapshot.GetHead(captured.Identity);
            if (!captured.Exists && (head == null || head.Knowledge == HeadKnowledge.Unavailable)) return;
            var prior = head?.Knowledge == HeadKnowledge.Known ? revisions.GetRevision(Scope(session), head.Revision) : null;
            if (captured.Exists && prior?.ContentSha256 == captured.ContentSha256 &&
                prior.Dependencies.Count == 1 &&
                prior.Dependencies[0].Resource.Uri == captured.Dependencies[0].Resource.Uri &&
                prior.Dependencies[0].Resource.Revision == captured.Dependencies[0].Resource.Revision) return;
            throw new ToolMutationPreparationException("task_list_authority_unavailable",
                "The task list does not match its published authority; reconcile the interrupted mutation before editing.");
        }

        public static ResourceAuthorityCommit Publish(IResourceAuthorityStore authority, IResourceRevisionStore revisions,
            IResourceMutationJournal journal, MutationAttempt attempt, ToolExecutionRecord record, ResourceMutationReadBack captured)
        { return PublishState(authority, revisions, journal, attempt, TaskListToolCatalog.SetToolId,
            record?.Evidence?.Effect == ToolEffectEvidence.VerifiedChange, captured); }

        public static ResourceAuthorityCommit PublishFork(IResourceAuthorityStore authority, IResourceRevisionStore revisions,
            IResourceMutationJournal journal, MutationAttempt attempt, ResourceMutationReadBack captured)
        { return PublishState(authority, revisions, journal, attempt, ForkOperation, captured != null, captured); }

        public static ResourceAuthorityCommit PublishHistoryReset(IResourceAuthorityStore authority, IResourceRevisionStore revisions,
            IResourceMutationJournal journal, MutationAttempt attempt, ResourceMutationReadBack captured)
        {
            if (!IsHistoryReset(attempt.Operation) || captured?.Exists == true)
                throw new InvalidOperationException("History reset may only remove the selected task state.");
            return PublishState(authority, revisions, journal, attempt, attempt.Operation, captured != null, captured);
        }

        private static ResourceAuthorityCommit PublishState(IResourceAuthorityStore authority, IResourceRevisionStore revisions,
            IResourceMutationJournal journal, MutationAttempt attempt, string operation, bool changed, ResourceMutationReadBack captured)
        {
            if (attempt.Operation != operation || attempt.ScopeId.Kind != "conversation" ||
                attempt.State != MutationAttemptState.DispatchMayHaveOccurred ||
                attempt.IntendedImpacts.Count != 1 || attempt.Target.Uri != ResourceUri.Create("state", "conversation", attempt.ScopeId.Id, "task-list"))
                throw new InvalidOperationException("An exact prepared task-list mutation is required.");
            var snapshot = authority.Capture(attempt.ScopeId);
            var published = snapshot.FindMutationCommit(attempt.AttemptId);
            if (published != null) { journal.Resolve(attempt.AttemptId, published.CommitId); return published; }
            var before = snapshot.GetHead(attempt.Target);
            if (before?.Revision?.Revision != attempt.ExpectedRevision || operation == ForkOperation && snapshot.Generation != 0)
                throw new InvalidOperationException("Task-list authority changed before publication.");
            if (changed && (captured == null || !captured.Identity.Equals(attempt.Target)))
                throw new InvalidOperationException("Task-list publication requires matching source read-back.");
            ResourceHeadState after;
            if (changed && captured.Exists)
            {
                var exact = new ResourceRef(attempt.Target.Uri, "r_" + Guid.NewGuid().ToString("N"));
                revisions.RegisterRevision(attempt.ScopeId, new ResourceRevisionMetadata(exact,
                    captured.ContentSha256, captured.Payload, before?.Revision, dependencies: captured.Dependencies));
                revisions.RegisterView(attempt.ScopeId, new ResourceRevisionView(exact, captured.View,
                    captured.ContentSha256, captured.Payload, captured.Coverage, captured.Parts));
                after = ResourceHeadState.Known(exact, snapshot.Generation + 1);
            }
            else if (changed) after = ResourceHeadState.Unavailable(attempt.Target, snapshot.Generation + 1, "verified removal");
            else after = ResourceHeadState.Unknown(attempt.Target, snapshot.Generation + 1, "mutation:" + attempt.AttemptId);
            var effect = new ResourceEffect("re_" + Guid.NewGuid().ToString("N"), attempt.Operation,
                changed ? ResourceEffectOutcome.VerifiedChanged : ResourceEffectOutcome.UnknownAfterDispatch,
                new[] { new ResourceImpact(attempt.Target, ResourceImpactRelation.ContainerMembership,
                    before: before?.Revision, after: after.Revision, changeKind: "tool-mutation") },
                changed ? "VerifiedChange" : "unknown");
            var commit = ResourceAuthorityCommit.Create(attempt.ScopeId, snapshot.Generation, effect,
                new[] { new ResourceHeadChange(attempt.Target, before, after) }, AuthorityCommitReason.MutationEffect, attempt.AttemptId);
            authority.Publish(commit);
            journal.Resolve(attempt.AttemptId, commit.CommitId);
            return commit;
        }
    }
}
