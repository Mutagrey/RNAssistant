using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Storage
{
    public sealed class SharedContextDocument
    {
        public string Version { get; set; }
        public string SourceChatId { get; set; }
        public string CheckpointId { get; set; }
        public List<StructuredContextClaim> Claims { get; set; }
        public List<ContextClaimSource> Sources { get; set; }
    }

    public sealed partial class DocumentArtifactStore
    {
        public const string SharedContextKind = "shared-context";
        public static string ContextLogicalId(string id)
        {
            var match = System.Text.RegularExpressions.Regex.Match(id ?? "", @"^(artifact_ctx_[0-9a-f]{64})_r[1-9][0-9]*_[0-9a-f]{8}$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            return match.Success ? match.Groups[1].Value : null;
        }

        public ChatArtifact PublishContext(ChatSession session, ContextCheckpoint checkpoint, long expectedGeneration)
        {
            var scope = Scope(session);
            if (checkpoint == null || checkpoint.PromptVersion != ContextCheckpoint.CurrentPromptVersion || checkpoint.Claims == null ||
                checkpoint.Claims.Count == 0 || checkpoint.Claims.Count > 64 || checkpoint.Claims.Any(claim => claim == null || !claim.HasTypedProvenance()) ||
                checkpoint.Claims.SelectMany(claim => claim.Evidence).Any(e => e.ScopeId.Kind == "document" && !e.ScopeId.Equals(scope)))
                throw new InvalidDataException("Only typed claims belonging to this document can be shared.");
            var sourceIds = checkpoint.Claims.SelectMany(claim => claim.SourceMessageIds).Distinct(StringComparer.Ordinal).ToArray();
            var sources = new List<ContextClaimSource>();
            foreach (var id in sourceIds)
            {
                var matches = session.Messages.Where(message => message.Id == id).ToArray();
                var retainedSources = checkpoint.Claims.SelectMany(claim => claim.SourceSnapshots ?? new List<ContextClaimSource>())
                    .Where(source => source != null && source.MessageId == id).GroupBy(source => JsonConvert.SerializeObject(source)).ToArray();
                if (matches.Length > 1 || retainedSources.Length > 1 || matches.Length == 0 && retainedSources.Length == 0)
                    throw new InvalidDataException("An exact claim source is missing or ambiguous; shared context was not published.");
                sources.Add(retainedSources.Length == 1 ? retainedSources[0].First() : new ContextClaimSource {
                    MessageId = id, Role = matches[0].Role, Text = matches[0].Content ?? "",
                    Preview = matches[0].ProtocolMessage ? null : matches[0].Content });
            }
            var document = new SharedContextDocument { Version = ContextCheckpoint.CurrentPromptVersion, SourceChatId = session.Id,
                CheckpointId = checkpoint.Id, Claims = JsonConvert.DeserializeObject<List<StructuredContextClaim>>(JsonConvert.SerializeObject(checkpoint.Claims)), Sources = sources };
            foreach (var claim in document.Claims)
            {
                claim.SourceSnapshots = new List<ContextClaimSource>();
                claim.Evidence = claim.Evidence.Select(e => BindContextSource(session, e)).ToList();
            }
            var json = JsonConvert.SerializeObject(document);
            if (json.Length > 512000) throw new InvalidDataException("Shared context exceeds its publication bound.");
            var logicalId = "artifact_ctx_" + TextPatternEngine.Sha256(session.Id);
            var identity = new ResourceIdentity(ResourceUri.Create("state", scope.Kind, scope.Id, logicalId));
            var before = _authority.Capture(scope);
            if (before.Generation != expectedGeneration) throw new ResourceAuthorityConflictException("The document changed during compaction; shared context was not published.");
            var parent = CurrentSnapshot(session, identity);
            var previous = parent == null ? null : Read(session, parent, false);
            var revision = checked((previous?.Revision ?? 0) + 1);
            var title = session.Title ?? "Conversation";
            if (title.Length > 180) title = title.Substring(0, 180);
            var artifact = new ChatArtifact { Id = ChatResourceUri.CreateSnapshotId(logicalId, revision), Revision = revision,
                DocumentAuthorityId = scope.Id, Kind = SharedContextKind, Title = "Shared context: " + title,
                MimeType = "application/vnd.rnassistant.context-claims+json", ParentArtifactId = previous?.Id,
                InlineText = json, MetadataJson = "{}", CreatedUtc = checkpoint.CreatedUtc };
            var dependencies = document.Claims.SelectMany(claim => claim.Evidence)
                .SelectMany(e => new[] { new ResourceDependency(e.Resource, e.View, e.Coverage, "claim-source") }.Concat(e.Dependencies)).ToArray();
            var retained = RetainAuthoredSnapshot(session, artifact, checkpoint.Id, checkpoint.Id, dependencies: dependencies);
            var payloads = checkpoint.Claims.SelectMany(claim => claim.Evidence).Select(e => e.Payload).Where(p => p != null).ToArray();
            if (payloads.Any(p => !_payloads.HasStoredReference(p.ToBlobReference())))
                throw new InvalidDataException("An exact claim source payload is unavailable.");
            _revisions.RegisterView(scope, new ResourceRevisionView(retained.Revision, "claim-provenance", retained.ContentSha256,
                retained.Payload, ResourceCoverage.Whole(), payloads));
            var link = PayloadRef.FromBlob(_payloads.StoreText(JsonConvert.SerializeObject(retained.Revision), "application/json"));
            var logical = new ResourceRef(identity.Uri, "r_" + Guid.NewGuid().ToString("N"));
            _revisions.RegisterRevision(scope, new ResourceRevisionMetadata(logical, link.Sha256, link,
                dependencies: new[] { new ResourceDependency(retained.Revision, kind: "immutable-snapshot") }));
            _authority.Publish(ResourceAuthorityCommit.Create(scope, before.Generation, null, new[] {
                new ResourceHeadChange(retained.Revision.Identity, null, ResourceHeadState.Known(retained.Revision, before.Generation + 1, "shared-context")),
                new ResourceHeadChange(identity, before.GetHead(identity), ResourceHeadState.Known(logical, before.Generation + 1, "shared-context")) },
                AuthorityCommitReason.DerivedPublication));
            return artifact;
        }

        private ResourceEvidence BindContextSource(ChatSession session, ResourceEvidence evidence)
        {
            if (!Owns(session, evidence.Resource)) return evidence;
            string owner, id; int version;
            if (!ChatResourceUri.TryParseArtifactRevision(evidence.Resource, out owner, out id, out version)) return evidence;
            var logicalId = MarkdownDocumentIdentity.LogicalId(id) ?? HtmlWorkspaceIdentity.LogicalId(id) ?? ContextLogicalId(id);
            if (IsPlan(session, evidence.Resource)) logicalId = PlanIdFromSnapshot(evidence.Resource);
            if (logicalId == null) return evidence;
            var logical = new ResourceIdentity(ResourceUri.Create("state", "document", session.DocumentAuthorityId, logicalId));
            var revision = LogicalRevisionForSnapshot(session, logical, evidence.Resource);
            return new ResourceEvidence(evidence.EvidenceId, evidence.ScopeId, evidence.Resource, evidence.View, evidence.Coverage,
                evidence.Complete, evidence.AuthorityGeneration, evidence.Payload,
                evidence.Dependencies.Concat(new[] { new ResourceDependency(revision, "text", ResourceCoverage.Whole(), "current-state") }),
                evidence.ObservedAt, evidence.SourceEventId, evidence.Immutable, evidence.ContentSha256);
        }
    }
}
