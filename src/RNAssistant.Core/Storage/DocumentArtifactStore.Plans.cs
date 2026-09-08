using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Storage
{
    public sealed partial class DocumentArtifactStore
    {
        private const string PlanRecordView = "artifact-plan-record";

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
            var item = Clone(artifact);
            item.DocumentAuthorityId = scope.Id;
            var body = PayloadRef.FromBlob(_payloads.StoreText(item.InlineText ?? string.Empty, "text/markdown"));
            item.ContentSha256 = body.Sha256;
            item.ContentByteLength = body.ByteLength;
            item.InlineText = null;
            item.RelativePath = null;
            var exact = ChatResourceUri.CreateArtifactRevision(session, item);
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
            var json = JsonConvert.SerializeObject(new PlanRecord { Artifact = item, SourceChatId = session.Id, MutationAttemptId = mutationAttemptId, OperationKey = operationKey });
            if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumRecordBytes)
                throw new InvalidDataException("The Plan metadata exceeds its retention bound.");
            var metadata = PayloadRef.FromBlob(_payloads.StoreText(json, "application/vnd.rnassistant.artifact-plan+json"));
            _revisions.RegisterRevision(scope, new ResourceRevisionMetadata(exact, body.Sha256, body, restoredFrom: restoredFrom));
            _revisions.RegisterView(scope, new ResourceRevisionView(exact, PlanRecordView, metadata.Sha256, metadata,
                ResourceCoverage.Whole(), new[] { body }));
            artifact.DocumentAuthorityId = scope.Id;
            artifact.ContentSha256 = body.Sha256;
            artifact.ContentByteLength = body.ByteLength;
            var link = PayloadRef.FromBlob(_payloads.StoreText(JsonConvert.SerializeObject(exact), "application/json"));
            return new[] {
                new ResourceMutationReadBack(exact.Identity, true, "text", body.Sha256, body, revision: exact, restoredFrom: restoredFrom),
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
            var references = _authority.Capture(Scope(session)).Heads.Values
                .Where(head => head.Knowledge == HeadKnowledge.Known && IsPlan(session, head.Revision))
                .Select(head => head.Revision).Where(reference => ResourceUri.Parse(reference.Uri).Segments[2] == artifactId).Take(2).ToArray();
            if (references.Length != 1) throw new InvalidDataException("The selected document Plan snapshot is unavailable or ambiguous.");
            return ReadPlan(session, references[0], includeBody);
        }

        private static ResourceIdentity PlanOperationIdentity(ChatSession session, string operationKey)
        {
            var scope = Scope(session);
            if (string.IsNullOrWhiteSpace(operationKey)) throw new InvalidDataException("A stable runtime Plan operation key is required.");
            return new ResourceIdentity(ResourceUri.Create("state", scope.Kind, scope.Id, "plan-operation-" + operationKey));
        }

        private PlanRecord ReadPlanRecord(ResourceAuthorityScopeId scope, ResourceRef reference)
        {
            var view = _revisions.GetView(scope, reference, PlanRecordView);
            if (view?.Payload == null || view.Payload.ByteLength > MaximumRecordBytes)
                throw new InvalidDataException("The Plan metadata is unavailable.");
            var json = _payloads.ReadText(view.Payload.ToBlobReference());
            var record = json == null ? null : JsonConvert.DeserializeObject<PlanRecord>(json);
            if (record?.Artifact == null || string.IsNullOrWhiteSpace(record.SourceChatId) ||
                string.IsNullOrWhiteSpace(record.MutationAttemptId) || string.IsNullOrWhiteSpace(record.OperationKey))
                throw new InvalidDataException("The Plan publication provenance is unavailable.");
            return record;
        }

        private ChatArtifact ReadPlan(ChatSession session, ResourceRef reference, bool includeBody)
        {
            var scope = Scope(session);
            if (!IsPlan(session, reference)) throw new InvalidDataException("The Plan belongs to another document.");
            var head = _authority.GetHead(scope, reference.Identity);
            if (head?.Knowledge != HeadKnowledge.Known || head.Revision.Uri != reference.Uri || head.Revision.Revision != reference.Revision)
                throw new InvalidDataException("The Plan snapshot has not crossed its publication barrier.");
            var record = ReadPlanRecord(scope, reference);
            var item = record.Artifact;
            var body = _revisions.GetRevision(scope, reference)?.Payload;
            if (item == null || string.IsNullOrWhiteSpace(record.SourceChatId) || string.IsNullOrWhiteSpace(record.MutationAttemptId) ||
                body == null || item.Revision < 1 || item.DocumentAuthorityId != scope.Id || item.Kind != ChatArtifactKinds.PlanDocument ||
                ChatResourceUri.CreateArtifactRevision(session, item).Uri != reference.Uri ||
                item.ContentSha256 != body.Sha256 || item.ContentByteLength != body.ByteLength)
                throw new InvalidDataException("The retained Plan does not match its exact snapshot.");
            if (includeBody)
                item.InlineText = _payloads.ReadText(body.ToBlobReference()) ?? throw new InvalidDataException("The Plan body is unavailable.");
            return item;
        }

        private sealed class PlanRecord
        {
            public ChatArtifact Artifact { get; set; }
            public string SourceChatId { get; set; }
            public string MutationAttemptId { get; set; }
            public string OperationKey { get; set; }
        }
    }
}
