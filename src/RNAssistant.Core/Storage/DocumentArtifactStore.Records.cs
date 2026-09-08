using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Storage
{
    public sealed partial class DocumentArtifactStore
    {
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
