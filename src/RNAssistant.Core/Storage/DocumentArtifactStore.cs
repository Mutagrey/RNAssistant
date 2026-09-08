using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Storage
{
    // Owns originals and retained Plan snapshots in the existing authority/CAS.
    // Plan head publication stays with the mutation observer; no library database.
    public sealed partial class DocumentArtifactStore
    {
        private const string RecordView = "artifact-original-record";
        private const int MaximumRecordBytes = 512 * 1024;
        private readonly IResourceAuthorityStore _authority;
        private readonly IResourceRevisionStore _revisions;
        private readonly ChatBlobStore _payloads;

        public DocumentArtifactStore(IResourceAuthorityStore authority, IResourceRevisionStore revisions, ChatBlobStore payloads)
        {
            _authority = authority ?? throw new ArgumentNullException(nameof(authority));
            _revisions = revisions ?? throw new ArgumentNullException(nameof(revisions));
            _payloads = payloads ?? throw new ArgumentNullException(nameof(payloads));
        }

        public ChatArtifact PublishOriginal(ChatSession session, ChatArtifact artifact, ChatAttachment original)
        {
            var scope = Scope(session);
            if (artifact == null || original == null || artifact.Id != "attachment_" + original.Id ||
                artifact.ContentSha256 != original.ContentSha256 || artifact.ContentByteLength != original.ContentByteLength ||
                artifact.Revision != 1 || !string.IsNullOrEmpty(artifact.ParentArtifactId))
                throw new InvalidDataException("An original publication requires matching immutable attachment evidence.");
            var source = Clone(original);
            source.DraftChatId = null;
            source.RelativePath = null;
            source.ExtractedTextPath = null;
            source.ExtractedText = null;
            var item = Clone(artifact);
            item.DocumentAuthorityId = scope.Id;
            item.RelativePath = null;
            item.InlineText = null;
            item.CreatedUtc = source.CreatedUtc;
            var reference = ChatResourceUri.CreateArtifactRevision(session, item);
            var raw = new PayloadRef(source.ContentSha256, source.ContentByteLength ?? -1, source.ContentType);
            var parts = new List<PayloadRef> { raw };
            if (!string.IsNullOrWhiteSpace(source.ExtractedTextSha256) || source.ExtractedTextByteLength.HasValue)
                parts.Add(new PayloadRef(source.ExtractedTextSha256, source.ExtractedTextByteLength ?? -1, "text/plain; charset=utf-8"));
            foreach (var part in parts)
                if (!_payloads.HasStoredReference(part.ToBlobReference()))
                    throw new InvalidDataException("An original publication requires durable CAS payloads.");
            var record = new OriginalRecord { Artifact = item, Original = source, SourceChatId = session.Id };
            var recordJson = JsonConvert.SerializeObject(record);
            if (System.Text.Encoding.UTF8.GetByteCount(recordJson) > MaximumRecordBytes)
                throw new InvalidDataException("The original metadata exceeds its publication bound.");
            // Registration is immutable. Competing claims on the same upload id
            // cannot silently replace its bytes or descriptive metadata.
            if (_revisions.GetView(scope, reference, RecordView) == null)
            {
                var recordPayload = PayloadRef.FromBlob(_payloads.StoreText(recordJson, "application/vnd.rnassistant.artifact-original+json"));
                try
                {
                    _revisions.RegisterRevision(scope, new ResourceRevisionMetadata(reference, raw.Sha256, raw,
                        createdUtc: source.CreatedUtc));
                    _revisions.RegisterView(scope, new ResourceRevisionView(reference, RecordView,
                        recordPayload.Sha256, recordPayload, ResourceCoverage.Whole(), parts));
                }
                catch (InvalidDataException)
                {
                    // Another publisher may have registered this same upload after
                    // our check. Only an exact matching retained record can recover.
                    if (_revisions.GetView(scope, reference, RecordView) == null) throw;
                }
            }
            var retained = ReadRecord(_revisions.GetView(scope, reference, RecordView));
            record.Artifact.SourceMessageId = retained.Artifact.SourceMessageId;
            record.Artifact.RunId = retained.Artifact.RunId;
            if (JsonConvert.SerializeObject(record) != JsonConvert.SerializeObject(retained))
                throw new ResourceAuthorityConflictException("The original id is already bound to different content or metadata.");
            for (var retry = 0; retry < 8; retry++)
            {
                var snapshot = _authority.Capture(scope);
                var before = snapshot.GetHead(reference.Identity);
                if (before != null)
                {
                    if (before.Knowledge != HeadKnowledge.Known || before.Revision.Uri != reference.Uri ||
                        before.Revision.Revision != reference.Revision)
                        throw new ResourceAuthorityConflictException("The original is no longer available for publication.");
                    return Read(session, reference);
                }
                try
                {
                    _authority.Publish(ResourceAuthorityCommit.Create(scope, snapshot.Generation, null,
                        new[] { new ResourceHeadChange(reference.Identity, null,
                            ResourceHeadState.Known(reference, snapshot.Generation + 1, "original-publication")) },
                        AuthorityCommitReason.DerivedPublication));
                    return Read(session, reference);
                }
                catch (ResourceAuthorityConflictException) when (retry < 7)
                { /* Recheck only metadata publication; no external effect is replayed. */ }
            }
            throw new ResourceAuthorityConflictException("The document publication is busy; retry linking the retained original.");
        }

        public IReadOnlyList<ChatArtifact> List(ChatSession session)
        {
            var scope = Scope(session);
            var snapshot = _authority.Capture(scope);
            return snapshot.Heads.Values.Where(head => head.Knowledge == HeadKnowledge.Known &&
                Owns(session, head.Revision) && IsSnapshotReference(head.Revision))
                .Select(head => Read(session, head.Revision, false))
                .OrderBy(item => item.CreatedUtc).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
        }

        public IReadOnlyList<ChatArtifact> InspectMetadataList(ChatSession session)
        {
            // Failure to capture the authority itself is not a per-resource issue.
            var snapshot = _authority.Capture(Scope(session));
            return snapshot.Heads.Values.Where(head => Owns(session, head.Revision) && IsSnapshotReference(head.Revision))
                .Select(head => InspectMetadata(session, head.Revision)).ToArray();
        }

        public ChatArtifact InspectMetadata(ChatSession session, ResourceRef reference)
        {
            if (!Owns(session, reference)) throw new InvalidDataException("The artifact belongs to another document.");
            string owner, id;
            int revision;
            ChatResourceUri.TryParseArtifactRevision(reference, out owner, out id, out revision);
            if (IsPlan(session, reference)) PlanIdFromSnapshot(reference);
            try { return Read(session, reference, false); }
            catch (InvalidDataException) { }
            catch (IOException) { }
            catch (JsonException) { }
            catch (UnauthorizedAccessException) { }
            // Explicit reference-only projection. No recovered title, metadata,
            // body, timestamp or writable head is asserted or persisted.
            return new ChatArtifact
            {
                Id = id, DocumentAuthorityId = owner, Revision = revision,
                Kind = IsPlan(session, reference) ? ChatArtifactKinds.PlanDocument :
                    HtmlWorkspaceIdentity.LogicalId(id) != null ? ChatArtifactKinds.HtmlWorkspace :
                    MarkdownDocumentIdentity.LogicalId(id) != null ? ChatArtifactKinds.Markdown : ChatArtifactKinds.File,
                CreatedUtc = default(DateTime), AvailabilityIssue = "metadata_unavailable"
            };
        }

        public ChatArtifact Read(ChatSession session, ResourceRef reference, bool includeBody = true)
        {
            if (IsPlan(session, reference)) return ReadPlan(session, reference, includeBody);
            if (IsAuthored(session, reference))
            {
                string owner, id; int version;
                ChatResourceUri.TryParseArtifactRevision(reference, out owner, out id, out version);
                return ReadRecordSnapshot(session, reference, AuthoredRecordView,
                    HtmlWorkspaceIdentity.LogicalId(id) != null ? ChatArtifactKinds.HtmlWorkspace :
                    MarkdownDocumentIdentity.LogicalId(id) != null ? ChatArtifactKinds.Markdown : ChatArtifactKinds.File, includeBody);
            }
            var scope = Scope(session);
            if (!Owns(session, reference)) throw new InvalidDataException("The artifact belongs to another document.");
            var head = _authority.GetHead(scope, reference.Identity);
            if (head?.Knowledge != HeadKnowledge.Known || head.Revision.Uri != reference.Uri || head.Revision.Revision != reference.Revision)
                throw new InvalidDataException("The original has not crossed its publication barrier or is unavailable.");
            var record = ReadRecord(_revisions.GetView(scope, reference, RecordView));
            var revision = _revisions.GetRevision(scope, reference);
            if (record.Artifact.DocumentAuthorityId != scope.Id ||
                ChatResourceUri.CreateArtifactRevision(session, record.Artifact).Uri != reference.Uri ||
                record.Artifact.ContentSha256 != record.Original.ContentSha256 ||
                record.Artifact.ContentByteLength != record.Original.ContentByteLength ||
                revision?.Payload == null || revision.Payload.Sha256 != record.Original.ContentSha256 ||
                revision.Payload.ByteLength != record.Original.ContentByteLength)
                throw new InvalidDataException("The original record does not match its exact resource.");
            record.Artifact.OriginalAttachment = record.Original;
            return record.Artifact;
        }

        public static bool Owns(ChatSession session, ResourceRef reference)
        {
            string owner, id;
            int revision;
            return !string.IsNullOrWhiteSpace(session?.DocumentAuthorityId) &&
                ChatResourceUri.TryParseArtifactRevision(reference, out owner, out id, out revision) &&
                owner == session.DocumentAuthorityId && (id.StartsWith("attachment_", StringComparison.Ordinal) ||
                    id.StartsWith("plan_doc_", StringComparison.Ordinal) || HtmlWorkspaceIdentity.LogicalId(id) != null ||
                    id.StartsWith("artifact_", StringComparison.Ordinal));
        }

        private static bool IsSnapshotReference(ResourceRef reference)
        {
            ResourceAddress address;
            return reference != null && ResourceUri.TryParse(reference.Uri, out address) && address.Segments.Count == 5;
        }

        private OriginalRecord ReadRecord(ResourceRevisionView view)
        {
            if (view?.Payload == null || view.Payload.ByteLength > MaximumRecordBytes)
                throw new InvalidDataException("The retained original record is unavailable or exceeds its metadata bound.");
            var json = _payloads.ReadText(view.Payload.ToBlobReference());
            var record = json == null ? null : JsonConvert.DeserializeObject<OriginalRecord>(json);
            if (record?.Artifact == null || record.Original == null || string.IsNullOrWhiteSpace(record.SourceChatId))
                throw new InvalidDataException("The retained original record is invalid or unavailable.");
            return record;
        }

        private static ResourceAuthorityScopeId Scope(ChatSession session)
        {
            if (string.IsNullOrWhiteSpace(session?.DocumentAuthorityId))
                throw new InvalidOperationException("A bound logical document identity is required for artifact access.");
            return ResourceAuthorityScopeId.Document(new DocumentAuthorityId(session.DocumentAuthorityId));
        }

        private static T Clone<T>(T value) { return JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value)); }
        private sealed class OriginalRecord
        {
            public ChatArtifact Artifact { get; set; }
            public ChatAttachment Original { get; set; }
            public string SourceChatId { get; set; }
        }
    }
}
