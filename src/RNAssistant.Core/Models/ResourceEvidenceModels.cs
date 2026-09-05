using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace RNAssistant.Core.Models
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum EvidenceState { Current, Superseded, Unknown, Unavailable }

    // Immutable observation. Currentness is a projection of authority, never a stored flag.
    public sealed class ResourceEvidence
    {
        public string EvidenceId { get; private set; }
        public ResourceAuthorityScopeId ScopeId { get; private set; }
        public ResourceRef Resource { get; private set; }
        public string View { get; private set; }
        public ResourceCoverage Coverage { get; private set; }
        public bool Complete { get; private set; }
        public PayloadRef Payload { get; private set; }
        public IReadOnlyList<ResourceDependency> Dependencies { get; private set; }
        public long AuthorityGeneration { get; private set; }
        public DateTime ObservedAt { get; private set; }
        public string SourceEventId { get; private set; }
        public bool Immutable { get; private set; }
        public string ContentSha256 { get; private set; }

        [JsonConstructor]
        public ResourceEvidence(string evidenceId, ResourceAuthorityScopeId scopeId,
            ResourceRef resource, string view, ResourceCoverage coverage, bool complete,
            long authorityGeneration, PayloadRef payload = null,
            IEnumerable<ResourceDependency> dependencies = null, DateTime? observedAt = null,
            string sourceEventId = null, bool immutable = false, string contentSha256 = null)
        {
            if (string.IsNullOrWhiteSpace(evidenceId) || scopeId == null || resource == null || !resource.IsExact)
                throw new ArgumentException("Evidence requires an id, authority scope and exact resource revision.");
            if (string.IsNullOrWhiteSpace(view) || authorityGeneration < 0) throw new ArgumentException("Invalid evidence view/generation.");
            EvidenceId = evidenceId;
            ScopeId = scopeId;
            Resource = resource.Copy();
            View = view;
            Coverage = coverage ?? ResourceCoverage.Whole();
            Complete = complete;
            Payload = payload;
            Dependencies = Array.AsReadOnly((dependencies ?? new ResourceDependency[0]).ToArray());
            AuthorityGeneration = authorityGeneration;
            ObservedAt = (observedAt ?? DateTime.UtcNow).ToUniversalTime();
            SourceEventId = sourceEventId;
            Immutable = immutable;
            ContentSha256 = contentSha256;
        }
    }

    public sealed class EvidenceProjection
    {
        public ResourceEvidence Evidence { get; private set; }
        public EvidenceState State { get; private set; }
        public string Reason { get; private set; }
        public EvidenceProjection(ResourceEvidence evidence, EvidenceState state, string reason)
        {
            Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
            State = state;
            Reason = reason;
        }
    }

    public sealed class StructuredContextClaim
    {
        public string ClaimId { get; set; }
        public string Text { get; set; }
        public List<ResourceEvidence> Evidence { get; set; } = new List<ResourceEvidence>();
        public List<string> SourceMessageIds { get; set; } = new List<string>();
        public string ToolGeneration { get; set; }
        public string SkillGeneration { get; set; }
        public string SchemaGeneration { get; set; }
    }
}
