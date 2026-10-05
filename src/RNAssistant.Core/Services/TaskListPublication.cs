using System;
using System.IO;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Services
{
    // Task lists remain conversation-owned artifacts in ChatStore/CAS. This owns
    // their logical authority publication, shared by Office and workspace hosts.
    public static class TaskListPublication
    {
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
            var body = artifact.InlineText;
            if (body == null && artifact.ContentByteLength.HasValue && artifact.ContentByteLength <= 128 * 1024)
                body = payloads.ReadText(new ChatBlobReference { Sha256 = artifact.ContentSha256,
                    ByteLength = artifact.ContentByteLength.Value, ContentType = artifact.MimeType });
            if (body == null) throw new InvalidDataException("The active task-list body is unavailable.");
            var payload = PayloadRef.FromBlob(payloads.StoreText(body, artifact.MimeType ?? "application/json"));
            return new ResourceMutationReadBack(identity, true, "text", payload.Sha256, payload,
                dependencies: new[] { new ResourceDependency(ChatResourceUri.CreateArtifactRevision(session, artifact), kind: "immutable-snapshot") });
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
            ResourceMutationJournal journal, MutationAttempt attempt, ToolExecutionRecord record, ResourceMutationReadBack captured)
        {
            if (attempt.Operation != TaskListToolCatalog.SetToolId || attempt.ScopeId.Kind != "conversation" ||
                attempt.State != MutationAttemptState.DispatchMayHaveOccurred ||
                attempt.IntendedImpacts.Count != 1 || attempt.Target.Uri != ResourceUri.Create("state", "conversation", attempt.ScopeId.Id, "task-list"))
                throw new InvalidOperationException("An exact prepared task-list mutation is required.");
            var snapshot = authority.Capture(attempt.ScopeId);
            var published = snapshot.Commits.FirstOrDefault(item => item.MutationAttemptId == attempt.AttemptId);
            if (published != null) { journal.Resolve(attempt.AttemptId, published.CommitId); return published; }
            var before = snapshot.GetHead(attempt.Target);
            if (before?.Revision?.Revision != attempt.ExpectedRevision)
                throw new InvalidOperationException("Task-list authority changed before publication.");
            var changed = record?.Evidence?.Effect == ToolEffectEvidence.VerifiedChange;
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
