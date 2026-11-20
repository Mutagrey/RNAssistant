using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
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
                if (!_payloads.HasVerifiedReference(part.ToBlobReference()))
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
                    return Read(ConversationMetadata.From(session), reference);
                }
                try
                {
                    _authority.Publish(ResourceAuthorityCommit.Create(scope, snapshot.Generation, null,
                        new[] { new ResourceHeadChange(reference.Identity, null,
                            ResourceHeadState.Known(reference, snapshot.Generation + 1, "original-publication")) },
                        AuthorityCommitReason.DerivedPublication));
                    return Read(ConversationMetadata.From(session), reference);
                }
                catch (ResourceAuthorityConflictException) when (retry < 7)
                { /* Recheck only metadata publication; no external effect is replayed. */ }
            }
            throw new ResourceAuthorityConflictException("The document publication is busy; retry linking the retained original.");
        }

        public IReadOnlyList<ChatArtifact> List(ConversationMetadata session)
        {
            var scope = Scope(session);
            var snapshot = _authority.Capture(scope);
            return snapshot.Heads.Values.Where(head => head.Knowledge == HeadKnowledge.Known &&
                Owns(session, head.Revision) && IsSnapshotReference(head.Revision))
                .Select(head => Read(session, head.Revision, false))
                .OrderBy(item => item.CreatedUtc).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
        }

        public IReadOnlyList<ChatArtifact> InspectMetadataList(ConversationMetadata session)
        {
            // Failure to capture the authority itself is not a per-resource issue.
            var snapshot = _authority.Capture(Scope(session));
            return snapshot.Heads.Values.Where(head => Owns(session, head.Revision) && IsSnapshotReference(head.Revision))
                .Select(head => InspectMetadata(session, head.Revision)).ToArray();
        }

        public ChatArtifact InspectMetadata(ConversationMetadata session, ResourceRef reference)
        {
            if (!Owns(session, reference)) throw new InvalidDataException("The artifact belongs to another document.");
            string owner, id;
            int revision;
            ChatResourceUri.TryParseArtifactRevision(reference, out owner, out id, out revision);
            if (IsPlan(session, reference)) PlanIdFromSnapshot(reference);
            try
            {
                var artifact = Read(session, reference, false);
                artifact.RemovedFromDocument = IsRemoved(session, artifact);
                return artifact;
            }
            catch (InvalidDataException) { }
            catch (IOException) { }
            catch (JsonException) { }
            catch (UnauthorizedAccessException) { }
            // Explicit reference-only projection. No recovered title, metadata,
            // body, timestamp or writable head is asserted or persisted.
            var unavailable = new ChatArtifact
            {
                Id = id, DocumentAuthorityId = string.IsNullOrWhiteSpace(session.DocumentAuthorityId) ? null : owner,
                WorkspaceId = string.IsNullOrWhiteSpace(session.DocumentAuthorityId) ? owner : null, Revision = revision,
                Kind = IsWorkspaceHtml(session, reference) ? ChatArtifactKinds.HtmlProjectVersion : IsPlan(session, reference) ? ChatArtifactKinds.PlanDocument :
                    HtmlWorkspaceIdentity.LogicalId(id) != null ? ChatArtifactKinds.HtmlWorkspace :
                    MarkdownDocumentIdentity.LogicalId(id) != null ? ChatArtifactKinds.Markdown : ContextLogicalId(id) != null ? SharedContextKind : ChatArtifactKinds.File,
                CreatedUtc = default(DateTime), AvailabilityIssue = "metadata_unavailable"
            };
            unavailable.RemovedFromDocument = IsRemoved(session, unavailable);
            return unavailable;
        }

        public ChatArtifact Read(ConversationMetadata session, ResourceRef reference, bool includeBody = true)
        {
            if (IsWorkspaceHtml(session, reference)) return ReadRecordSnapshot(session, reference, WorkspaceHtmlRecordView,
                ChatArtifactKinds.HtmlProjectVersion, includeBody, WorkspaceHtmlScope(session.WorkspaceId));
            if (IsPlan(session, reference)) return ReadPlan(session, reference, includeBody);
            if (IsAuthored(session, reference))
            {
                string owner, id; int version;
                ChatResourceUri.TryParseArtifactRevision(reference, out owner, out id, out version);
                return ReadRecordSnapshot(session, reference, AuthoredRecordView,
                    HtmlWorkspaceIdentity.LogicalId(id) != null ? ChatArtifactKinds.HtmlWorkspace :
                    MarkdownDocumentIdentity.LogicalId(id) != null ? ChatArtifactKinds.Markdown : ContextLogicalId(id) != null ? SharedContextKind : ChatArtifactKinds.File, includeBody);
            }
            var scope = Scope(session);
            if (!Owns(session, reference)) throw new InvalidDataException("The artifact belongs to another document.");
            if (!HasPublishedSnapshot(scope, reference))
                throw new InvalidDataException("The original has not crossed its publication barrier or is unavailable.");
            return ReadOriginal(session, reference, scope, _revisions.GetRevision(scope, reference), _revisions.GetView(scope, reference, RecordView));
        }

        private sealed class OriginalMetadata
        {
            internal ChatArtifact Artifact;
            internal PayloadRef Record;
        }

        // One bounded, disposable proof of immutable original registrations. The
        // current authority receipt is re-read and every metadata CAS is verified
        // on use. Active mutable selections always go through their publication owner.
        private sealed class OriginalProof
        {
            internal string Scope, Commit;
            internal long Generation;
            internal Dictionary<string, OriginalMetadata> Originals;
        }
        private volatile OriginalProof _originalProof;
        internal long OriginalBindingBatches { get; private set; }

        internal IEnumerable<ChatArtifact> ReadPublishedMetadata(ConversationMetadata session, IReadOnlyList<ResourceRef> references)
        {
            var native = _authority as ResourceJournal;
            if (native == null || !ReferenceEquals(native, _revisions) || session.DocumentAuthorityId == null || references.Count == 0)
                return references.Select(reference => Read(session, reference, false));
            var originals = references.Where(reference => Owns(session, reference) && !IsPlan(session, reference) &&
                !IsAuthored(session, reference)).ToArray();
            if (originals.Length == 0) return references.Select(reference => Read(session, reference, false));
            var scope = Scope(session);
            var frozen = native.CaptureAddressed(scope, new ResourceIdentity[0]);
            var proof = _originalProof;
            var reusable = proof != null && proof.Scope == scope.Id && proof.Generation == frozen.Generation && proof.Commit == frozen.HighWaterCommitId;
            var selected = new Dictionary<string, OriginalMetadata>(StringComparer.Ordinal);
            var missing = new List<ResourceRef>();
            foreach (var reference in originals)
            {
                OriginalMetadata prior;
                if (!reusable || !proof.Originals.TryGetValue(reference.Uri, out prior)) { missing.Add(reference); continue; }
                if (!_payloads.HasVerifiedReference(prior.Record.ToBlobReference()))
                    throw new InvalidDataException("The retained original record is missing or corrupt.");
                selected.Add(reference.Uri, prior);
            }
            for (var offset = 0; offset < missing.Count; offset += 32)
            {
                var group = missing.GetRange(offset, Math.Min(32, missing.Count - offset));
                var bindings = native.ReadArtifactBindings(scope, group, RecordView);
                OriginalBindingBatches++;
                for (var i = 0; i < group.Count; i++)
                {
                    var reference = group[i];
                    var binding = bindings[i];
                    var head = binding.Item1;
                    // Removed snapshots need proof of their earlier publication.
                    if (head?.Knowledge != HeadKnowledge.Known) continue;
                    if (head.Revision.Uri != reference.Uri || head.Revision.Revision != reference.Revision)
                        throw new InvalidDataException("The original has not crossed its publication barrier.");
                    selected.Add(reference.Uri, new OriginalMetadata { Artifact = ReadOriginal(session, reference, scope, binding.Item2, binding.Item3),
                        Record = binding.Item3.Payload });
                }
            }
            if (missing.Count != 0 && selected.Count <= 128 && selected.Values.Sum(item => item.Record.ByteLength) <= 4 * 1024 * 1024)
            {
                var after = native.CaptureAddressed(scope, new ResourceIdentity[0]);
                if (frozen.Generation == after.Generation && frozen.HighWaterCommitId == after.HighWaterCommitId)
                    _originalProof = new OriginalProof { Scope = scope.Id, Commit = frozen.HighWaterCommitId, Generation = frozen.Generation, Originals = selected };
            }
            return references.Select(reference => {
                OriginalMetadata value;
                return selected.TryGetValue(reference.Uri, out value) ? value.Artifact : Read(session, reference, false);
            });
        }

        private ChatArtifact ReadOriginal(ConversationMetadata session, ResourceRef reference, ResourceAuthorityScopeId scope,
            ResourceRevisionMetadata revision, ResourceRevisionView view)
        {
            var record = ReadRecord(view);
            if (record.Artifact.DocumentAuthorityId != scope.Id ||
                ChatResourceUri.CreateArtifactRevision(session.Id, record.Artifact).Uri != reference.Uri ||
                record.Artifact.ContentSha256 != record.Original.ContentSha256 ||
                record.Artifact.ContentByteLength != record.Original.ContentByteLength ||
                revision?.Payload == null || revision.Payload.Sha256 != record.Original.ContentSha256 ||
                revision.Payload.ByteLength != record.Original.ContentByteLength)
                throw new InvalidDataException("The original record does not match its exact resource.");
            record.Artifact.OriginalAttachment = record.Original;
            record.Artifact.SourceChatId = record.SourceChatId;
            return record.Artifact;
        }

        public static bool Owns(ChatSession session, ResourceRef reference)
        {
            return Owns(session?.DocumentAuthorityId, session?.WorkspaceId, reference);
        }

        public static bool Owns(ConversationMetadata session, ResourceRef reference)
        {
            return Owns(session?.DocumentAuthorityId, session?.WorkspaceId, reference);
        }

        public static bool Owns(string documentAuthorityId, string workspaceId, ResourceRef reference)
        {
            string owner, id;
            int revision;
            if (!ChatResourceUri.TryParseArtifactRevision(reference, out owner, out id, out revision)) return false;
            if (string.IsNullOrWhiteSpace(documentAuthorityId))
                return !string.IsNullOrWhiteSpace(workspaceId) && owner == workspaceId &&
                    (id.StartsWith("plan_doc_", StringComparison.Ordinal) || IsWorkspaceHtmlId(id));
            return owner == documentAuthorityId && (id.StartsWith("attachment_", StringComparison.Ordinal) ||
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
            return Scope(session?.DocumentAuthorityId);
        }

        private static ResourceAuthorityScopeId Scope(ConversationMetadata session)
        {
            return Scope(session?.DocumentAuthorityId);
        }

        private static ResourceAuthorityScopeId Scope(string documentAuthorityId)
        {
            if (string.IsNullOrWhiteSpace(documentAuthorityId))
                throw new InvalidOperationException("A bound logical document identity is required for artifact access.");
            return ResourceAuthorityScopeId.Document(new DocumentAuthorityId(documentAuthorityId));
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
