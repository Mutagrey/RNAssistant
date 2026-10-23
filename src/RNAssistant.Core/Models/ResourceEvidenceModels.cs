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
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public ResourceReadMetadata ReadMetadata { get; private set; }

        [JsonConstructor]
        public ResourceEvidence(string evidenceId, ResourceAuthorityScopeId scopeId,
            ResourceRef resource, string view, ResourceCoverage coverage, bool complete,
            long authorityGeneration, PayloadRef payload = null,
            IEnumerable<ResourceDependency> dependencies = null, DateTime? observedAt = null,
            string sourceEventId = null, bool immutable = false, string contentSha256 = null,
            ResourceReadMetadata readMetadata = null)
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
            ReadMetadata = readMetadata;
        }
    }

    // Diagnostic facts about an accepted read, separate from the Complete guard.
    // Actual coverage and exact identity stay on ResourceEvidence itself.
    public sealed class ResourceReadMetadata
    {
        [JsonProperty("requestedCoverage", NullValueHandling = NullValueHandling.Ignore)]
        public ResourceCoverage RequestedCoverage { get; private set; }
        [JsonProperty("requestedSection", NullValueHandling = NullValueHandling.Ignore)]
        public string RequestedSection { get; private set; }
        [JsonProperty("requestedPartNumber", NullValueHandling = NullValueHandling.Ignore)]
        public int? RequestedPartNumber { get; private set; }
        [JsonProperty("requestedProcedure", NullValueHandling = NullValueHandling.Ignore)]
        public string RequestedProcedure { get; private set; }
        [JsonProperty("requestedProcedureKind", NullValueHandling = NullValueHandling.Ignore)]
        public string RequestedProcedureKind { get; private set; }
        [JsonProperty("requestedDepth", NullValueHandling = NullValueHandling.Ignore)]
        public int? RequestedDepth { get; private set; }
        [JsonProperty("sourceCoverage", NullValueHandling = NullValueHandling.Ignore)]
        public ResourceCoverage SourceCoverage { get; private set; }
        [JsonProperty("selectionComplete", NullValueHandling = NullValueHandling.Ignore)]
        public bool? SelectionComplete { get; private set; }
        [JsonProperty("representationComplete", NullValueHandling = NullValueHandling.Ignore)]
        public bool? RepresentationComplete { get; private set; }
        [JsonProperty("sourceComplete", NullValueHandling = NullValueHandling.Ignore)]
        public bool? SourceComplete { get; private set; }
        [JsonProperty("descriptorAtRead", NullValueHandling = NullValueHandling.Ignore)]
        public ResourceReadDescriptorSnapshot DescriptorAtRead { get; private set; }
        // Stable extractor/selection profile for derived text. The exact source
        // revision and selected coverage remain on the enclosing evidence.
        [JsonProperty("extractionProfile", NullValueHandling = NullValueHandling.Ignore)]
        public string ExtractionProfile { get; private set; }
        [JsonProperty("sourcePageCount", NullValueHandling = NullValueHandling.Ignore)]
        public int? SourcePageCount { get; private set; }

        [JsonConstructor]
        public ResourceReadMetadata(ResourceCoverage requestedCoverage = null, string requestedSection = null,
            int? requestedPartNumber = null, ResourceCoverage sourceCoverage = null,
            bool? selectionComplete = null, bool? representationComplete = null, bool? sourceComplete = null,
            string requestedProcedure = null, string requestedProcedureKind = null,
            int? requestedDepth = null, ResourceReadDescriptorSnapshot descriptorAtRead = null,
            string extractionProfile = null, int? sourcePageCount = null)
        {
            RequestedCoverage = requestedCoverage; RequestedSection = requestedSection;
            RequestedPartNumber = requestedPartNumber; SourceCoverage = sourceCoverage;
            SelectionComplete = selectionComplete; RepresentationComplete = representationComplete;
            SourceComplete = sourceComplete;
            RequestedProcedure = requestedProcedure; RequestedProcedureKind = requestedProcedureKind;
            RequestedDepth = requestedDepth;
            DescriptorAtRead = descriptorAtRead;
            ExtractionProfile = extractionProfile;
            SourcePageCount = sourcePageCount;
        }
    }

    // Provider claims captured with one accepted read, not a live availability check.
    public sealed class ResourceReadDescriptorSnapshot
    {
        [JsonProperty("provider", NullValueHandling = NullValueHandling.Ignore)]
        public string Provider { get; private set; }
        [JsonProperty("kind", NullValueHandling = NullValueHandling.Ignore)]
        public string Kind { get; private set; }
        [JsonProperty("mimeType", NullValueHandling = NullValueHandling.Ignore)]
        public string MimeType { get; private set; }
        [JsonProperty("byteLength", NullValueHandling = NullValueHandling.Ignore)]
        public long? ByteLength { get; private set; }
        [JsonProperty("representations", NullValueHandling = NullValueHandling.Ignore)]
        public IReadOnlyList<string> Representations { get; private set; }
        [JsonProperty("capabilities", NullValueHandling = NullValueHandling.Ignore)]
        public IReadOnlyList<string> Capabilities { get; private set; }
        [JsonProperty("viewCapabilities", NullValueHandling = NullValueHandling.Ignore)]
        public IReadOnlyList<ResourceViewCapability> ViewCapabilities { get; private set; }

        [JsonConstructor]
        public ResourceReadDescriptorSnapshot(string provider = null, string kind = null,
            string mimeType = null, long? byteLength = null, IEnumerable<string> representations = null,
            IEnumerable<string> capabilities = null, IEnumerable<ResourceViewCapability> viewCapabilities = null)
        {
            Provider = provider;
            Kind = kind;
            MimeType = mimeType;
            ByteLength = byteLength;
            var views = (representations ?? Enumerable.Empty<string>()).Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();
            var operations = (capabilities ?? Enumerable.Empty<string>()).Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();
            var typedViews = (viewCapabilities ?? Enumerable.Empty<ResourceViewCapability>()).Where(item => item != null).ToArray();
            Representations = views.Length == 0 ? null : Array.AsReadOnly(views);
            Capabilities = operations.Length == 0 ? null : Array.AsReadOnly(operations);
            ViewCapabilities = typedViews.Length == 0 ? null : Array.AsReadOnly(typedViews);
        }

        public static ResourceReadDescriptorSnapshot From(ResourceDescriptor descriptor)
        {
            if (descriptor == null || (string.IsNullOrWhiteSpace(descriptor.Provider) &&
                string.IsNullOrWhiteSpace(descriptor.Kind) && string.IsNullOrWhiteSpace(descriptor.MimeType) &&
                !descriptor.ByteLength.HasValue && (descriptor.Representations == null || descriptor.Representations.Count == 0) &&
                (descriptor.Capabilities == null || descriptor.Capabilities.Count == 0) &&
                (descriptor.ViewCapabilities == null || descriptor.ViewCapabilities.Count == 0)))
                return null;
            return new ResourceReadDescriptorSnapshot(descriptor.Provider, descriptor.Kind, descriptor.MimeType,
                descriptor.ByteLength, descriptor.Representations, descriptor.Capabilities, descriptor.ViewCapabilities);
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
        public string Kind { get; set; }
        public List<string> SourceRoles { get; set; } = new List<string>();
        public List<ContextClaimSource> SourceSnapshots { get; set; } = new List<ContextClaimSource>();
        public string Text { get; set; }
        public List<ResourceEvidence> Evidence { get; set; } = new List<ResourceEvidence>();
        public List<string> SourceMessageIds { get; set; } = new List<string>();

        public static bool SupportsKind(string kind)
        {
            return kind == "constraint" || kind == "decision" || kind == "observation" ||
                kind == "interpretation" || kind == "question" || kind == "next_action";
        }

        public bool HasTypedProvenance()
        {
            return SupportsKind(Kind) && !string.IsNullOrWhiteSpace(Text) &&
                SourceMessageIds != null && SourceMessageIds.Count > 0 && SourceMessageIds.All(id => !string.IsNullOrWhiteSpace(id)) &&
                SourceRoles != null && SourceRoles.Count > 0 && SourceRoles.All(role =>
                    role == "user" || role == "assistant" || role == "tool" || role == "system" || role == "developer") &&
                Evidence != null && Evidence.All(item => item != null) &&
                ((Kind != "constraint" && Kind != "decision") || SourceRoles.All(role => role == "user")) &&
                (Kind != "observation" || SourceRoles.All(role => role == "tool") && Evidence.Count > 0);
        }
    }

    public sealed class ContextClaimSource
    {
        public string MessageId { get; set; }
        public string Role { get; set; }
        public string Text { get; set; }
        public string Preview { get; set; }
    }
}
