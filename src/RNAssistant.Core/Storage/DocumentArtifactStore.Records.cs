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
        private const string AuthoredRecordView = "artifact-authored-record";

        public ResourceMutationReadBack RetainAuthoredSnapshot(ChatSession session, ChatArtifact artifact,
            string attemptId, string operationKey, ResourceRef restoredFrom = null, IEnumerable<ResourceDependency> dependencies = null)
        {
            if (artifact == null || artifact.Kind != ChatArtifactKinds.HtmlWorkspace && artifact.Kind != ChatArtifactKinds.File ||
                artifact.DocumentAuthorityId != Scope(session).Id || !IsAuthored(session, ChatResourceUri.CreateArtifactRevision(session, artifact)))
                throw new InvalidDataException("A document-owned authored snapshot is required.");
            return RetainRecord(session, artifact, artifact.InlineText, AuthoredRecordView,
                "application/vnd.rnassistant.artifact-record+json", attemptId, operationKey, restoredFrom, dependencies);
        }

        public static bool IsAuthored(ChatSession session, ResourceRef reference)
        {
            string owner, id; int revision;
            return Owns(session, reference) && ChatResourceUri.TryParseArtifactRevision(reference, out owner, out id, out revision) &&
                (HtmlWorkspaceIdentity.LogicalId(id) != null || id.StartsWith("artifact_", StringComparison.Ordinal));
        }

        public ResourceRef CurrentSnapshot(ChatSession session, ResourceIdentity logicalIdentity)
        {
            var scope = Scope(session);
            var address = ResourceUri.Parse(logicalIdentity.Uri);
            if (address.Provider != "state" || address.Segments.Count != 3 || address.Segments[0] != scope.Kind || address.Segments[1] != scope.Id)
                throw new InvalidDataException("The logical artifact belongs to another scope.");
            var head = _authority.GetHead(scope, logicalIdentity);
            if (head == null) return null;
            if (head.Knowledge != HeadKnowledge.Known) throw new InvalidDataException("The artifact head is unavailable; reconcile without replay.");
            var refs = _revisions.GetRevision(scope, head.Revision)?.Dependencies.Where(item => item.Kind == "immutable-snapshot").ToArray();
            if (refs == null || refs.Length != 1 || !Owns(session, refs[0].Resource))
                throw new InvalidDataException("The artifact head has no exact document snapshot.");
            return refs[0].Resource.Copy();
        }

        public ResourceRef LogicalRevisionForSnapshot(ChatSession session, ResourceIdentity identity, ResourceRef snapshot)
        {
            var scope = Scope(session);
            if (!Owns(session, snapshot)) throw new InvalidDataException("The restore snapshot belongs to another document.");
            var restored = _authority.Capture(scope).Commits.SelectMany(commit => commit.HeadChanges)
                .Where(change => change.Identity.Equals(identity) && change.After.Knowledge == HeadKnowledge.Known)
                .Select(change => change.After.Revision).FirstOrDefault(reference =>
                    _revisions.GetRevision(scope, reference)?.Dependencies.Any(dependency => dependency.Kind == "immutable-snapshot" &&
                        dependency.Resource.Uri == snapshot.Uri && dependency.Resource.Revision == snapshot.Revision) == true);
            return restored ?? throw new InvalidDataException("The restore source has no logical publication.");
        }

        public IReadOnlyList<ChatArtifact> SnapshotHistory(ChatSession session, string logicalId)
        {
            return _authority.Capture(Scope(session)).Heads.Values.Where(head => head.Knowledge == HeadKnowledge.Known && Owns(session, head.Revision) && IsSnapshotReference(head.Revision))
                .Where(head => { string owner, id; int revision;
                    return ChatResourceUri.TryParseArtifactRevision(head.Revision, out owner, out id, out revision) &&
                        id.StartsWith(logicalId + "_r", StringComparison.Ordinal); })
                .Select(head => InspectMetadata(session, head.Revision)).OrderBy(item => item.Revision).ToArray();
        }

        // Artifact metadata is a retained view of the same canonical revision.
        // This code never publishes heads/effects or creates another inventory.
        private ResourceMutationReadBack RetainRecord(ChatSession session, ChatArtifact artifact, string text,
            string recordView, string recordMimeType, string attemptId, string operationKey,
            ResourceRef restoredFrom = null, IEnumerable<ResourceDependency> dependencies = null)
        {
            var scope = Scope(session);
            if (artifact == null || artifact.Revision < 1 || string.IsNullOrEmpty(artifact.Id) || text == null ||
                string.IsNullOrEmpty(attemptId) || string.IsNullOrEmpty(operationKey))
                throw new InvalidDataException("An exact artifact and prepared operation are required for retention.");
            var item = Clone(artifact);
            item.DocumentAuthorityId = scope.Id;
            var body = PayloadRef.FromBlob(_payloads.StoreText(text, item.MimeType));
            item.ContentSha256 = body.Sha256;
            item.ContentByteLength = body.ByteLength;
            item.InlineText = null;
            item.RelativePath = null;
            var exact = ChatResourceUri.CreateArtifactRevision(session, item);
            var json = JsonConvert.SerializeObject(new ArtifactRecord { Artifact = item, SourceChatId = session.Id,
                MutationAttemptId = attemptId, OperationKey = operationKey });
            if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumRecordBytes)
                throw new InvalidDataException("The artifact metadata exceeds its retention bound.");
            var metadata = PayloadRef.FromBlob(_payloads.StoreText(json, recordMimeType));
            _revisions.RegisterRevision(scope, new ResourceRevisionMetadata(exact, body.Sha256, body,
                restoredFrom: restoredFrom, dependencies: dependencies));
            _revisions.RegisterView(scope, new ResourceRevisionView(exact, recordView, metadata.Sha256, metadata,
                ResourceCoverage.Whole(), new[] { body }));
            artifact.DocumentAuthorityId = scope.Id;
            artifact.ContentSha256 = body.Sha256;
            artifact.ContentByteLength = body.ByteLength;
            return new ResourceMutationReadBack(exact.Identity, true, "text", body.Sha256, body,
                revision: exact, restoredFrom: restoredFrom, dependencies: dependencies);
        }

        private ChatArtifact ReadRecordSnapshot(ChatSession session, ResourceRef reference, string recordView,
            string expectedKind, bool includeBody)
        {
            var scope = Scope(session);
            var head = _authority.GetHead(scope, reference.Identity);
            if (!Owns(session, reference) || head?.Knowledge != HeadKnowledge.Known ||
                head.Revision.Uri != reference.Uri || head.Revision.Revision != reference.Revision)
                throw new InvalidDataException("The artifact snapshot has not crossed its publication barrier.");
            var view = _revisions.GetView(scope, reference, recordView);
            if (view?.Payload == null || view.Payload.ByteLength > MaximumRecordBytes)
                throw new InvalidDataException("The artifact metadata is unavailable.");
            var json = _payloads.ReadText(view.Payload.ToBlobReference());
            var record = json == null ? null : JsonConvert.DeserializeObject<ArtifactRecord>(json);
            var item = record?.Artifact;
            var body = _revisions.GetRevision(scope, reference)?.Payload;
            if (item == null || string.IsNullOrWhiteSpace(record.SourceChatId) || string.IsNullOrWhiteSpace(record.MutationAttemptId) ||
                string.IsNullOrWhiteSpace(record.OperationKey) || body == null || item.Revision < 1 || item.DocumentAuthorityId != scope.Id ||
                item.Kind != expectedKind || ChatResourceUri.CreateArtifactRevision(session, item).Uri != reference.Uri ||
                item.ContentSha256 != body.Sha256 || item.ContentByteLength != body.ByteLength)
                throw new InvalidDataException("The retained artifact does not match its exact snapshot.");
            if (includeBody)
                item.InlineText = _payloads.ReadText(body.ToBlobReference()) ?? throw new InvalidDataException("The artifact body is unavailable.");
            return item;
        }

        private sealed class ArtifactRecord
        {
            public ChatArtifact Artifact { get; set; }
            public string SourceChatId { get; set; }
            public string MutationAttemptId { get; set; }
            public string OperationKey { get; set; }
        }
    }
}
