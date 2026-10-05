using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Services
{
    // One Plan preparation/retention/publication owner for both bound documents
    // and workspaces. Host composition supplies serialization and chat linking.
    public static class PlanDocumentPublication
    {
        public static IReadOnlyList<ResourceImpact> Prepare(ChatSession session, ToolExecutionContext context,
            IDictionary<string, object> arguments, DocumentArtifactStore owner)
        {
            if (session == null || !PlanDocumentToolCatalog.Owns(context.Call.Name))
                throw new InvalidOperationException("A bound session and exact Plan tool are required.");
            try
            {
                var operationKey = PlanDocumentService.CreationId(session, context);
                if (owner.FindPlanOperation(session, operationKey) != null)
                    throw new ToolMutationPreparationException("plan_attempt_already_published", "This Plan operation is already published; recover its exact result without replay.");
                var candidates = session.Artifacts.Where(item => item.Id == session.ActivePlanDocumentArtifactId).Take(2).ToArray();
                if (candidates.Length > 1) throw new ToolMutationPreparationException("plan_active_revision_invalid", "The selected Plan is ambiguous.");
                var selected = candidates.SingleOrDefault();
                var intent = PlanDocumentArguments.Read(arguments);
                string planId;
                if (string.IsNullOrWhiteSpace(session.ActivePlanDocumentArtifactId) || context.Call.Name == PlanDocumentToolCatalog.SaveToolId && intent.StartNew)
                {
                    if (context.Call.Name != PlanDocumentToolCatalog.SaveToolId)
                        throw new ToolMutationPreparationException("plan_not_found", "Select a current Plan before restoring or removing it.");
                    planId = operationKey;
                    if (owner.CurrentPlan(session, planId) != null)
                        throw new ToolMutationPreparationException("plan_attempt_already_published", "This Plan creation is already published; do not repeat it.");
                }
                else
                {
                    if (selected == null || !DocumentArtifactStore.IsPlan(session, ChatResourceUri.CreateArtifactRevision(session, selected)))
                        throw new ToolMutationPreparationException("plan_active_revision_invalid", "The selected Plan is unavailable or belongs to another authority.");
                    planId = PlanDocumentService.PlanId(selected);
                    var current = owner.CurrentPlan(session, planId);
                    var expected = ChatResourceUri.CreateArtifactRevision(session, selected);
                    if (current == null || current.Uri != expected.Uri || current.Revision != expected.Revision)
                        throw new ToolMutationPreparationException("stale_plan_revision", "The selected Plan changed. Read and select its current revision before editing; this write was not dispatched.");
                    var history = owner.PlanHistory(session, planId, false).ToList();
                    var bodyVersion = context.Call.Name == PlanDocumentToolCatalog.RestoreToolId ? intent.Version : selected.Revision;
                    for (var index = 0; index < history.Count; index++)
                        if (history[index].Revision == bodyVersion)
                            history[index] = owner.Read(session, ChatResourceUri.CreateArtifactRevision(session, history[index]));
                    session.Artifacts.RemoveAll(item => PlanDocumentService.PlanId(item) == planId);
                    session.Artifacts.AddRange(history);
                }
                return new[] { new ResourceImpact(DocumentArtifactStore.PlanIdentity(session, planId), ResourceImpactRelation.Exact) };
            }
            catch (InvalidDataException ex)
            { throw new ToolMutationPreparationException("plan_snapshot_unavailable", ex.Message); }
        }

        public static IReadOnlyList<ResourceMutationReadBack> Retain(ChatSession session, MutationAttempt attempt,
            string operationKey, DocumentArtifactStore owner)
        {
            if (!DocumentArtifactStore.PlanScope(session).Equals(attempt.ScopeId))
                throw new InvalidOperationException("The Plan attempt belongs to another authority.");
            var planId = ResourceUri.Parse(attempt.Target.Uri).Segments.Last();
            var artifact = session.Artifacts.Where(item => PlanDocumentService.PlanId(item) == planId)
                .OrderByDescending(item => item.Revision).First();
            ResourceRef restoredFrom = null;
            if (attempt.Operation == PlanDocumentToolCatalog.RestoreToolId)
            {
                var sourceId = (string)JObject.Parse(artifact.MetadataJson)["restoredFromArtifactId"];
                restoredFrom = ChatResourceUri.CreateArtifactRevision(session, session.Artifacts.Single(item => item.Id == sourceId));
            }
            return owner.RetainPlan(session, planId, artifact, attempt.AttemptId, operationKey,
                PlanDocumentService.IsTombstone(artifact), restoredFrom);
        }

        public static ResourceAuthorityCommit Publish(IResourceAuthorityStore authority, IResourceRevisionStore revisions,
            ResourceMutationJournal journal, MutationAttempt attempt, ToolExecutionRecord record,
            IReadOnlyList<ResourceMutationReadBack> readBack = null)
        {
            if (!PlanDocumentToolCatalog.Owns(attempt.Operation) || attempt.State != MutationAttemptState.DispatchMayHaveOccurred)
                throw new InvalidOperationException("A dispatched Plan attempt is required.");
            var address = ResourceUri.Parse(attempt.Target.Uri);
            if ((attempt.ScopeId.Kind != "document" && attempt.ScopeId.Kind != "workspace") ||
                address.Provider != "state" || address.Segments.Count != 3 ||
                address.Segments[0] != attempt.ScopeId.Kind || address.Segments[1] != attempt.ScopeId.Id ||
                !address.Segments[2].StartsWith("plan_doc_", StringComparison.Ordinal) ||
                attempt.IntendedImpacts.Count != 1 || !attempt.IntendedImpacts[0].Identity.Equals(attempt.Target))
                throw new InvalidOperationException("An exact Plan target in its owning authority is required.");
            var snapshot = authority.Capture(attempt.ScopeId);
            var prior = snapshot.Commits.FirstOrDefault(item => item.MutationAttemptId == attempt.AttemptId);
            if (prior != null) { journal.Resolve(attempt.AttemptId, prior.CommitId); return prior; }
            if (snapshot.GetHead(attempt.Target)?.Revision?.Revision != attempt.ExpectedRevision)
                throw new InvalidOperationException("The prepared Plan head changed before publication; reconcile without replay.");
            var changed = record?.Evidence?.Effect == ToolEffectEvidence.VerifiedChange;
            var restored = changed && attempt.Operation == PlanDocumentToolCatalog.RestoreToolId;
            if (changed && (readBack?.Count != 3 || !readBack.Any(item => item.Identity.Equals(attempt.Target))))
                throw new InvalidOperationException("Plan publication requires snapshot, logical head and operation read-back.");
            var changes = new List<ResourceHeadChange>();
            var impacts = new List<ResourceImpact>();
            foreach (var identity in attempt.IntendedImpacts.Select(item => item.Identity)
                .Concat((readBack ?? new ResourceMutationReadBack[0]).Select(item => item.Identity)).Distinct())
            {
                var before = snapshot.GetHead(identity);
                var captured = changed ? readBack.SingleOrDefault(item => item.Identity.Equals(identity)) : null;
                ResourceHeadState after;
                if (changed)
                {
                    if (captured == null || !captured.Exists || captured.Payload == null)
                        throw new InvalidOperationException("Plan read-back is incomplete.");
                    var exact = captured.Revision ?? new ResourceRef(identity.Uri, "r_" + Guid.NewGuid().ToString("N"));
                    if (revisions.GetRevision(attempt.ScopeId, exact) == null)
                        revisions.RegisterRevision(attempt.ScopeId, new ResourceRevisionMetadata(exact, captured.ContentSha256,
                            captured.Payload, before?.Revision, restoredFrom: restored ? captured.RestoredFrom : null,
                            dependencies: captured.Dependencies));
                    revisions.RegisterView(attempt.ScopeId, new ResourceRevisionView(exact, captured.View,
                        captured.ContentSha256, captured.Payload, captured.Coverage, captured.Parts));
                    after = ResourceHeadState.Known(exact, snapshot.Generation + 1);
                }
                else after = ResourceHeadState.Unknown(identity, snapshot.Generation + 1, "mutation:" + attempt.AttemptId);
                changes.Add(new ResourceHeadChange(identity, before, after));
                impacts.Add(new ResourceImpact(identity, ResourceImpactRelation.Exact,
                    before: before?.Revision, after: after.Revision, changeKind: "tool-mutation"));
            }
            var outcome = restored ? ResourceEffectOutcome.Restored : changed ? ResourceEffectOutcome.VerifiedChanged : ResourceEffectOutcome.UnknownAfterDispatch;
            var effect = new ResourceEffect("re_" + Guid.NewGuid().ToString("N"), attempt.Operation, outcome,
                impacts, changed ? "VerifiedChange" : "unknown");
            var commit = ResourceAuthorityCommit.Create(attempt.ScopeId, snapshot.Generation, effect, changes,
                restored ? AuthorityCommitReason.Restore : AuthorityCommitReason.MutationEffect, attempt.AttemptId);
            authority.Publish(commit);
            journal.Resolve(attempt.AttemptId, commit.CommitId);
            return commit;
        }
    }
}
