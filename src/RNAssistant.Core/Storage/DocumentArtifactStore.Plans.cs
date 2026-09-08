using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using System.Text.RegularExpressions;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Storage
{
    public sealed partial class DocumentArtifactStore
    {
        private const string PlanRecordView = "artifact-plan-record";

        // The snapshot id is runtime-generated; recovery never guesses a title,
        // body or current head from it.
        public static string PlanIdFromArtifact(ChatArtifact artifact)
        {
            return ParsePlanSnapshotId(artifact.Id, artifact.Revision);
        }

        public static string PlanIdFromSnapshot(ResourceRef reference)
        {
            string owner, id;
            int revision;
            if (!ChatResourceUri.TryParseArtifactRevision(reference, out owner, out id, out revision))
                throw new InvalidDataException("An exact Plan snapshot reference is required.");
            return ParsePlanSnapshotId(id, revision);
        }

        private static string ParsePlanSnapshotId(string id, int revision)
        {
            var match = Regex.Match(id ?? string.Empty, @"^(plan_doc_[0-9a-f]{64})_r([1-9][0-9]*)_[0-9a-f]{8}$",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            if (!match.Success || match.Groups[2].Value != revision.ToString(System.Globalization.CultureInfo.InvariantCulture))
                throw new InvalidDataException("The Plan snapshot identity is invalid.");
            return match.Groups[1].Value;
        }

        public static ResourceRef PlanSelectionReference(ChatSession session, string artifactId)
        {
            var marker = (artifactId ?? string.Empty).LastIndexOf("_r", StringComparison.Ordinal);
            var end = (artifactId ?? string.Empty).LastIndexOf('_');
            int revision;
            if (marker < 0 || end <= marker + 2 || !int.TryParse(artifactId.Substring(marker + 2, end - marker - 2), out revision))
                throw new InvalidDataException("The selected Plan identity is invalid.");
            ParsePlanSnapshotId(artifactId, revision);
            return ChatResourceUri.CreateArtifactRevision(session, new ChatArtifact
                { Id = artifactId, Revision = revision, DocumentAuthorityId = Scope(session).Id });
        }

        public static ResourceIdentity PlanIdentity(ChatSession session, string planId)
        {
            var scope = Scope(session);
            if (string.IsNullOrWhiteSpace(planId) || !planId.StartsWith("plan_doc_", StringComparison.Ordinal))
                throw new InvalidDataException("A logical Plan identity is required.");
            return new ResourceIdentity(ResourceUri.Create("state", scope.Kind, scope.Id, planId));
        }

        public static bool IsPlan(ChatSession session, ResourceRef reference)
        {
            string owner, id;
            int revision;
            return Owns(session, reference) && ChatResourceUri.TryParseArtifactRevision(reference, out owner, out id, out revision) &&
                id.StartsWith("plan_doc_", StringComparison.Ordinal);
        }

        // Retention precedes publication. Only the mutation observer publishes these
        // read-backs together with the logical head in one authority commit.
        public IReadOnlyList<ResourceMutationReadBack> RetainPlan(ChatSession session, string planId, ChatArtifact artifact, string mutationAttemptId, string operationKey,
            bool removed, ResourceRef restoredFrom = null)
        {
            var scope = Scope(session);
            var identity = PlanIdentity(session, planId);
            if (string.IsNullOrWhiteSpace(mutationAttemptId) || string.IsNullOrWhiteSpace(operationKey)) throw new InvalidDataException("The prepared Plan attempt is required.");
            if (artifact?.Kind != ChatArtifactKinds.PlanDocument || string.IsNullOrWhiteSpace(artifact.Id) || artifact.Revision < 1 ||
                !artifact.Id.StartsWith(planId + "_r", StringComparison.Ordinal) || !removed && artifact.InlineText == null)
                throw new InvalidDataException("The Plan snapshot does not belong to its prepared logical identity.");
            ResourceRef logicalRestore = null;
            if (restoredFrom != null)
            {
                if (!IsPlan(session, restoredFrom) || !ReadPlan(session, restoredFrom, false).Id.StartsWith(planId + "_r", StringComparison.Ordinal))
                    throw new InvalidDataException("The Plan restore source belongs to another lineage.");
                logicalRestore = _authority.Capture(scope).Commits.SelectMany(commit => commit.HeadChanges)
                    .Where(change => change.Identity.Equals(identity) && change.After.Knowledge == HeadKnowledge.Known)
                    .Select(change => change.After.Revision).FirstOrDefault(reference =>
                        _revisions.GetRevision(scope, reference)?.Dependencies.Any(dependency => dependency.Kind == "immutable-snapshot" &&
                            dependency.Resource.Uri == restoredFrom.Uri && dependency.Resource.Revision == restoredFrom.Revision) == true);
                if (logicalRestore == null) throw new InvalidDataException("The Plan restore source has no logical publication.");
            }
            var retained = RetainRecord(session, artifact, artifact.InlineText ?? string.Empty,
                PlanRecordView, "application/vnd.rnassistant.artifact-plan+json", mutationAttemptId, operationKey, restoredFrom);
            var exact = retained.Revision;
            var link = PayloadRef.FromBlob(_payloads.StoreText(JsonConvert.SerializeObject(exact), "application/json"));
            return new[] {
                retained,
                new ResourceMutationReadBack(identity, true, "text", link.Sha256, link,
                    dependencies: new[] { new ResourceDependency(exact, kind: "immutable-snapshot") }, restoredFrom: logicalRestore),
                new ResourceMutationReadBack(PlanOperationIdentity(session, operationKey), true, "text", link.Sha256, link,
                    dependencies: new[] { new ResourceDependency(exact, kind: "operation-result") })
            };
        }

        public ResourceRef CurrentPlan(ChatSession session, string planId)
        {
            var scope = Scope(session);
            var head = _authority.GetHead(scope, PlanIdentity(session, planId));
            if (head == null) return null;
            if (head.Knowledge != HeadKnowledge.Known)
                throw new InvalidDataException("The Plan head is unavailable; reconcile its mutation before editing.");
            var revision = _revisions.GetRevision(scope, head.Revision);
            var references = revision?.Dependencies.Where(item => item.Kind == "immutable-snapshot").ToArray();
            if (references == null || references.Length != 1 || !IsPlan(session, references[0].Resource))
                throw new InvalidDataException("The Plan head has no exact snapshot.");
            return references[0].Resource.Copy();
        }

        public IReadOnlyList<ChatArtifact> PlanHistory(ChatSession session, string planId, bool includeBodies = true)
        {
            var scope = Scope(session);
            var result = new List<ChatArtifact>();
            var current = CurrentPlan(session, planId);
            if (current == null) return result;
            // Follow only the committed head's parent chain, never speculative CAS
            // registrations or all chats. Each parent is an immutable exact snapshot.
            var item = ReadPlan(session, current, includeBodies);
            while (item != null)
            {
                if (!item.Id.StartsWith(planId + "_r", StringComparison.Ordinal)) throw new InvalidDataException("The Plan parent belongs to another lineage.");
                if (result.Any(prior => prior.Id == item.Id)) throw new InvalidDataException("Cyclic Plan lineage.");
                result.Add(item);
                if (item.Revision == 1)
                {
                    if (!string.IsNullOrEmpty(item.ParentArtifactId)) throw new InvalidDataException("Invalid Plan root.");
                    break;
                }
                if (string.IsNullOrEmpty(item.ParentArtifactId)) throw new InvalidDataException("Missing Plan parent.");
                var parent = new ChatArtifact { Id = item.ParentArtifactId, Revision = item.Revision - 1, DocumentAuthorityId = scope.Id };
                item = ReadPlan(session, ChatResourceUri.CreateArtifactRevision(session, parent), includeBodies);
            }
            return result.OrderBy(value => value.Revision).ToArray();
        }

        public ResourceRef FindPlanOperation(ChatSession session, string operationKey)
        {
            var scope = Scope(session);
            var head = _authority.GetHead(scope, PlanOperationIdentity(session, operationKey));
            if (head == null) return null;
            if (head.Knowledge != HeadKnowledge.Known) throw new InvalidDataException("The Plan operation receipt is unavailable; reconcile without replay.");
            var results = _revisions.GetRevision(scope, head.Revision)?.Dependencies.Where(item => item.Kind == "operation-result").ToArray();
            if (results == null || results.Length != 1 || !IsPlan(session, results[0].Resource))
                throw new InvalidDataException("The Plan operation receipt has no exact result.");
            return results[0].Resource.Copy();
        }

        public ChatArtifact ReadPlanSelection(ChatSession session, string artifactId, bool includeBody = true)
        {
            return ReadPlan(session, PlanSelectionReference(session, artifactId), includeBody);
        }

        private static ResourceIdentity PlanOperationIdentity(ChatSession session, string operationKey)
        {
            var scope = Scope(session);
            if (string.IsNullOrWhiteSpace(operationKey)) throw new InvalidDataException("A stable runtime Plan operation key is required.");
            return new ResourceIdentity(ResourceUri.Create("state", scope.Kind, scope.Id, "plan-operation-" + operationKey));
        }

        private ChatArtifact ReadPlan(ChatSession session, ResourceRef reference, bool includeBody)
        {
            if (!IsPlan(session, reference)) throw new InvalidDataException("The Plan belongs to another document.");
            return ReadRecordSnapshot(session, reference, PlanRecordView, ChatArtifactKinds.PlanDocument, includeBody);
        }
    }
}
