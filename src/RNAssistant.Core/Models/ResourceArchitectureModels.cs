using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace RNAssistant.Core.Models
{
    public sealed class ResourceIdentity : IEquatable<ResourceIdentity>
    {
        [JsonProperty("uri", Required = Required.Always)]
        public string Uri { get; private set; }

        [JsonConstructor]
        public ResourceIdentity(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri))
                throw new ArgumentException("A logical resource URI is required.", nameof(uri));
            var address = RNAssistant.Core.Services.ResourceUri.Parse(uri.Trim());
            var segments = address.Segments.ToList();
            // Exact chat addresses retain their addressable revision component;
            // authority keys intentionally name the logical resource across revisions.
            if (address.Provider == "chat" && segments.Count >= 5 && segments[1] == "artifact" && segments[3] == "revision")
                segments.RemoveRange(3, 2);
            Uri = RNAssistant.Core.Services.ResourceUri.Create(address.Provider, segments.ToArray());
        }

        public bool Equals(ResourceIdentity other)
        {
            return other != null && string.Equals(Uri, other.Uri, StringComparison.Ordinal);
        }

        public override bool Equals(object obj) { return Equals(obj as ResourceIdentity); }
        public override int GetHashCode() { return StringComparer.Ordinal.GetHashCode(Uri); }
        public override string ToString() { return Uri; }
    }

    public sealed class PayloadRef
    {
        [JsonProperty("sha256", Required = Required.Always)]
        public string Sha256 { get; private set; }
        [JsonProperty("byteLength", Required = Required.Always)]
        public long ByteLength { get; private set; }
        [JsonProperty("contentType")]
        public string ContentType { get; private set; }
        [JsonProperty("encryption", NullValueHandling = NullValueHandling.Ignore)]
        public string Encryption { get; private set; }
        [JsonProperty("protectionKeyId", NullValueHandling = NullValueHandling.Ignore)]
        public string ProtectionKeyId { get; private set; }

        [JsonConstructor]
        public PayloadRef(string sha256, long byteLength, string contentType = null,
            string encryption = null, string protectionKeyId = null)
        {
            if (!IsSha256(sha256)) throw new ArgumentException("A canonical SHA-256 payload id is required.", nameof(sha256));
            if (byteLength < 0) throw new ArgumentOutOfRangeException(nameof(byteLength));
            Sha256 = sha256.ToLowerInvariant();
            ByteLength = byteLength;
            ContentType = contentType;
            Encryption = encryption;
            ProtectionKeyId = protectionKeyId;
        }

        public static PayloadRef FromBlob(ChatBlobReference value)
        {
            return value == null ? null : new PayloadRef(value.Sha256, value.ByteLength,
                value.ContentType, value.Encryption, value.ProtectionKeyId);
        }

        public ChatBlobReference ToBlobReference()
        {
            return new ChatBlobReference
            {
                Sha256 = Sha256,
                ByteLength = ByteLength,
                ContentType = ContentType,
                Encryption = Encryption,
                ProtectionKeyId = ProtectionKeyId
            };
        }

        private static bool IsSha256(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64) return false;
            return value.All(character => character >= '0' && character <= '9' ||
                character >= 'a' && character <= 'f' || character >= 'A' && character <= 'F');
        }
    }

    // Exact copy provenance carried by the conversation event stream. This is not
    // a current-head map; only ResourceAuthority commits establish currentness.
    public sealed class ResourceCopyLink
    {
        public ResourceRef Source { get; private set; }
        public ResourceRef Copy { get; private set; }
        public IReadOnlyList<long> SourcePublicationPath { get; private set; }
        [JsonConstructor]
        public ResourceCopyLink(ResourceRef source, ResourceRef copy, IEnumerable<long> sourcePublicationPath)
        {
            if (source?.IsExact != true || copy?.IsExact != true)
                throw new ArgumentException("Resource copy provenance requires exact references.");
            Source = source.Copy(); Copy = copy.Copy();
            var path = (sourcePublicationPath ?? new long[0]).ToArray();
            if (path.Length == 0 || path.Length > 64 || path.Any(item => item <= 0))
                throw new ArgumentException("Bounded source publication order is required.");
            SourcePublicationPath = Array.AsReadOnly(path);
        }
    }

    public static class ResourceCoverageKinds
    {
        public const string Whole = "whole";
        public const string LineRange = "line-range";
        public const string CharacterRange = "character-range";
        public const string CellRange = "cell-range";
        public const string PageRange = "page-range";
        public const string TimeRange = "time-range";
        public const string JsonPath = "json-path";
        public const string RecordRange = "record-range";
        public const string FieldSet = "field-set";
    }

    public sealed class ResourceCoverage
    {
        [JsonProperty("kind", Required = Required.Always)]
        public string Kind { get; private set; }
        [JsonProperty("address", NullValueHandling = NullValueHandling.Ignore)]
        public string Address { get; private set; }
        [JsonProperty("start", NullValueHandling = NullValueHandling.Ignore)]
        public long? Start { get; private set; }
        [JsonProperty("end", NullValueHandling = NullValueHandling.Ignore)]
        public long? End { get; private set; }
        [JsonProperty("path", NullValueHandling = NullValueHandling.Ignore)]
        public string Path { get; private set; }
        [JsonProperty("fields", NullValueHandling = NullValueHandling.Ignore)]
        public IReadOnlyList<string> Fields { get; private set; }

        [JsonConstructor]
        public ResourceCoverage(string kind, string address = null, long? start = null,
            long? end = null, string path = null, IEnumerable<string> fields = null)
        {
            Kind = NormalizeKind(kind);
            Address = string.IsNullOrWhiteSpace(address) ? null : address.Trim();
            Start = start;
            End = end;
            Path = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
            Fields = Array.AsReadOnly((fields ?? new string[0])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
            if (Start.HasValue && Start.Value < 0 || End.HasValue && End.Value < 0 ||
                Start.HasValue && End.HasValue && End.Value < Start.Value)
                throw new ArgumentException("Resource coverage bounds are invalid.");
        }

        public static ResourceCoverage Whole() { return new ResourceCoverage(ResourceCoverageKinds.Whole); }

        private static string NormalizeKind(string value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            switch (value)
            {
                case ResourceCoverageKinds.Whole:
                case ResourceCoverageKinds.LineRange:
                case ResourceCoverageKinds.CharacterRange:
                case ResourceCoverageKinds.CellRange:
                case ResourceCoverageKinds.PageRange:
                case ResourceCoverageKinds.TimeRange:
                case ResourceCoverageKinds.JsonPath:
                case ResourceCoverageKinds.RecordRange:
                case ResourceCoverageKinds.FieldSet:
                    return value;
                default:
                    throw new ArgumentException("Unsupported resource coverage kind: " + value, nameof(value));
            }
        }
    }

    public sealed class ResourceViewCapability
    {
        [JsonProperty("view", Required = Required.Always)]
        public string View { get; private set; }
        [JsonProperty("supportsOffset")]
        public bool SupportsOffset { get; private set; }
        [JsonProperty("supportsFields")]
        public bool SupportsFields { get; private set; }
        [JsonProperty("supportsStream")]
        public bool SupportsStream { get; private set; }
        [JsonProperty("maxItemsPerBatch", NullValueHandling = NullValueHandling.Ignore)]
        public int? MaxItemsPerBatch { get; private set; }
        [JsonProperty("maxBatchBytes", NullValueHandling = NullValueHandling.Ignore)]
        public int? MaxBatchBytes { get; private set; }

        [JsonConstructor]
        public ResourceViewCapability(string view, bool supportsOffset = false,
            bool supportsFields = false, bool supportsStream = false,
            int? maxItemsPerBatch = null, int? maxBatchBytes = null)
        {
            if (string.IsNullOrWhiteSpace(view)) throw new ArgumentException("A resource view is required.", nameof(view));
            if (maxItemsPerBatch.HasValue && maxItemsPerBatch.Value <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxItemsPerBatch));
            if (maxBatchBytes.HasValue && maxBatchBytes.Value <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxBatchBytes));
            View = view.Trim().ToLowerInvariant();
            SupportsOffset = supportsOffset;
            SupportsFields = supportsFields;
            SupportsStream = supportsStream;
            MaxItemsPerBatch = maxItemsPerBatch;
            MaxBatchBytes = maxBatchBytes;
        }
    }

    public sealed class ResourceDependency
    {
        [JsonProperty("resource", Required = Required.Always)]
        public ResourceRef Resource { get; private set; }
        [JsonProperty("view")]
        public string View { get; private set; }
        [JsonProperty("coverage")]
        public ResourceCoverage Coverage { get; private set; }
        [JsonProperty("kind")]
        public string Kind { get; private set; }

        [JsonConstructor]
        public ResourceDependency(ResourceRef resource, string view = null,
            ResourceCoverage coverage = null, string kind = null)
        {
            if (resource == null || !resource.IsExact)
                throw new ArgumentException("A dependency requires an exact resource revision.", nameof(resource));
            Resource = resource.Copy();
            View = view;
            Coverage = coverage ?? ResourceCoverage.Whole();
            Kind = kind;
        }
    }

    public sealed class ResourceRevisionMetadata
    {
        [JsonProperty("reference", Required = Required.Always)]
        public ResourceRef Reference { get; private set; }
        [JsonProperty("contentSha256", NullValueHandling = NullValueHandling.Ignore)]
        public string ContentSha256 { get; private set; }
        [JsonProperty("payload", NullValueHandling = NullValueHandling.Ignore)]
        public PayloadRef Payload { get; private set; }
        [JsonProperty("parent", NullValueHandling = NullValueHandling.Ignore)]
        public ResourceRef Parent { get; private set; }
        [JsonProperty("restoredFrom", NullValueHandling = NullValueHandling.Ignore)]
        public ResourceRef RestoredFrom { get; private set; }
        [JsonProperty("dependencies")]
        public IReadOnlyList<ResourceDependency> Dependencies { get; private set; }
        [JsonProperty("createdUtc")]
        public DateTime CreatedUtc { get; private set; }

        [JsonConstructor]
        public ResourceRevisionMetadata(ResourceRef reference, string contentSha256 = null,
            PayloadRef payload = null, ResourceRef parent = null, ResourceRef restoredFrom = null,
            IEnumerable<ResourceDependency> dependencies = null, DateTime? createdUtc = null)
        {
            if (reference == null || !reference.IsExact)
                throw new ArgumentException("Revision metadata requires one exact resource reference.", nameof(reference));
            Reference = reference.Copy();
            ContentSha256 = string.IsNullOrWhiteSpace(contentSha256) ? null : contentSha256.ToLowerInvariant();
            Payload = payload;
            Parent = parent == null ? null : parent.Copy();
            RestoredFrom = restoredFrom == null ? null : restoredFrom.Copy();
            Dependencies = Array.AsReadOnly((dependencies ?? new ResourceDependency[0]).ToArray());
            CreatedUtc = (createdUtc ?? DateTime.UtcNow).ToUniversalTime();
        }
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum ResourceEffectOutcome
    {
        VerifiedChanged,
        VerifiedNoChange,
        FailedNoEffect,
        UnknownAfterDispatch,
        ExternalDriftObserved,
        Restored
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum ResourceImpactRelation
    {
        Exact,
        Intersects,
        Subtree,
        ContainerMembership,
        DependsOn,
        CatalogGeneration
    }

    public sealed class ResourceImpact
    {
        [JsonProperty("identity", Required = Required.Always)]
        public ResourceIdentity Identity { get; private set; }
        [JsonProperty("relation", Required = Required.Always)]
        public ResourceImpactRelation Relation { get; private set; }
        [JsonProperty("coverage")]
        public ResourceCoverage Coverage { get; private set; }
        [JsonProperty("before", NullValueHandling = NullValueHandling.Ignore)]
        public ResourceRef Before { get; private set; }
        [JsonProperty("after", NullValueHandling = NullValueHandling.Ignore)]
        public ResourceRef After { get; private set; }
        [JsonProperty("changeKind", NullValueHandling = NullValueHandling.Ignore)]
        public string ChangeKind { get; private set; }

        [JsonConstructor]
        public ResourceImpact(ResourceIdentity identity, ResourceImpactRelation relation,
            ResourceCoverage coverage = null, ResourceRef before = null,
            ResourceRef after = null, string changeKind = null)
        {
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
            if (!Enum.IsDefined(typeof(ResourceImpactRelation), relation))
                throw new ArgumentOutOfRangeException(nameof(relation));
            Relation = relation;
            Coverage = coverage ?? ResourceCoverage.Whole();
            Before = before == null ? null : before.Copy();
            After = after == null ? null : after.Copy();
            ChangeKind = changeKind;
        }
    }

    public sealed class ResourceRevisionView
    {
        public ResourceRef Reference { get; private set; }
        public string View { get; private set; }
        public string ContentSha256 { get; private set; }
        public PayloadRef Payload { get; private set; }
        public ResourceCoverage Coverage { get; private set; }
        public IReadOnlyList<PayloadRef> Parts { get; private set; }
        [JsonConstructor]
        public ResourceRevisionView(ResourceRef reference, string view, string contentSha256,
            PayloadRef payload, ResourceCoverage coverage, IEnumerable<PayloadRef> parts = null)
        {
            if (reference == null || !reference.IsExact || string.IsNullOrWhiteSpace(view))
                throw new ArgumentException("An exact revision and view are required.");
            Reference = reference.Copy(); View = view; ContentSha256 = contentSha256;
            Payload = payload; Coverage = coverage ?? ResourceCoverage.Whole();
            Parts = Array.AsReadOnly((parts ?? new PayloadRef[0]).ToArray());
        }
    }

    // A domain owner's read-back, not evidence that any consumer read these bytes.
    public sealed class ResourceMutationReadBack
    {
        public ResourceIdentity Identity { get; private set; }
        public bool Exists { get; private set; }
        public string View { get; private set; }
        public string ContentSha256 { get; private set; }
        public PayloadRef Payload { get; private set; }
        public ResourceCoverage Coverage { get; private set; }
        public IReadOnlyList<ResourceDependency> Dependencies { get; private set; }
        public ResourceMutationReadBack(ResourceIdentity identity, bool exists, string view = null,
            string contentSha256 = null, PayloadRef payload = null, ResourceCoverage coverage = null,
            IEnumerable<ResourceDependency> dependencies = null, IEnumerable<PayloadRef> parts = null, ResourceRef restoredFrom = null,
            ResourceRef revision = null)
        {
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
            if (restoredFrom != null && !restoredFrom.IsExact) throw new ArgumentException("Restore origin must be exact.", nameof(restoredFrom));
            Exists = exists; View = view; ContentSha256 = contentSha256; Payload = payload;
            RestoredFrom = restoredFrom?.Copy();
            if (revision != null && (!revision.IsExact || !identity.Equals(revision.Identity)))
                throw new ArgumentException("Prepared revision must be exact and belong to the read-back identity.", nameof(revision));
            Revision = revision?.Copy();
            Coverage = coverage ?? ResourceCoverage.Whole();
            Dependencies = Array.AsReadOnly((dependencies ?? new ResourceDependency[0]).ToArray());
            Parts = Array.AsReadOnly((parts ?? new PayloadRef[0]).ToArray());
        }
        public IReadOnlyList<PayloadRef> Parts { get; private set; }
        public ResourceRef RestoredFrom { get; private set; }
        public ResourceRef Revision { get; private set; }
    }

    public sealed class ResourceEffect
    {
        [JsonProperty("effectId", Required = Required.Always)]
        public string EffectId { get; private set; }
        [JsonProperty("operation", Required = Required.Always)]
        public string Operation { get; private set; }
        [JsonProperty("outcome", Required = Required.Always)]
        public ResourceEffectOutcome Outcome { get; private set; }
        [JsonProperty("impacts")]
        public IReadOnlyList<ResourceImpact> Impacts { get; private set; }
        [JsonProperty("verification", NullValueHandling = NullValueHandling.Ignore)]
        public string Verification { get; private set; }

        [JsonConstructor]
        public ResourceEffect(string effectId, string operation, ResourceEffectOutcome outcome,
            IEnumerable<ResourceImpact> impacts = null, string verification = null)
        {
            if (string.IsNullOrWhiteSpace(effectId)) throw new ArgumentException("An effect id is required.", nameof(effectId));
            if (string.IsNullOrWhiteSpace(operation)) throw new ArgumentException("An operation is required.", nameof(operation));
            if (!Enum.IsDefined(typeof(ResourceEffectOutcome), outcome)) throw new ArgumentOutOfRangeException(nameof(outcome));
            EffectId = effectId.Trim();
            Operation = operation.Trim();
            Outcome = outcome;
            Impacts = Array.AsReadOnly((impacts ?? new ResourceImpact[0]).ToArray());
            Verification = verification;
        }
    }

    public sealed class ResourceLease
    {
        [JsonProperty("leaseId", Required = Required.Always)]
        public string LeaseId { get; private set; }
        [JsonProperty("resource", Required = Required.Always)]
        public ResourceRef Resource { get; private set; }
        [JsonProperty("views")]
        public IReadOnlyList<string> Views { get; private set; }
        [JsonProperty("coverage")]
        public ResourceCoverage Coverage { get; private set; }
        [JsonProperty("owner", Required = Required.Always)]
        public string Owner { get; private set; }
        [JsonProperty("expiresUtc")]
        public DateTime ExpiresUtc { get; private set; }

        [JsonConstructor]
        public ResourceLease(string leaseId, ResourceRef resource, IEnumerable<string> views,
            ResourceCoverage coverage, string owner, DateTime expiresUtc)
        {
            if (string.IsNullOrWhiteSpace(leaseId) || string.IsNullOrWhiteSpace(owner))
                throw new ArgumentException("Lease identity and owner are required.");
            if (resource == null || !resource.IsExact)
                throw new ArgumentException("A lease must pin one exact resource revision.", nameof(resource));
            LeaseId = leaseId.Trim();
            Resource = resource.Copy();
            Views = Array.AsReadOnly((views ?? new string[0])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim().ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToArray());
            Coverage = coverage ?? ResourceCoverage.Whole();
            Owner = owner.Trim();
            ExpiresUtc = expiresUtc.ToUniversalTime();
        }

        [JsonIgnore]
        public bool IsExpired { get { return DateTime.UtcNow >= ExpiresUtc; } }
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum SemanticSchemaState { Draft, Validated, Published, Deprecated }
    [JsonConverter(typeof(StringEnumConverter))]
    public enum DerivedResourceMode { Virtual, Materialized }

    public sealed class SemanticSchemaRevision
    {
        public ResourceRef Reference { get; private set; }
        public string Name { get; private set; }
        public string DefinitionJson { get; private set; }
        public SemanticSchemaState State { get; private set; }

        [JsonConstructor]
        public SemanticSchemaRevision(ResourceRef reference, string name, string definitionJson,
            SemanticSchemaState state)
        {
            if (reference == null || !reference.IsExact) throw new ArgumentException("An exact schema revision is required.", nameof(reference));
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A schema name is required.", nameof(name));
            Reference = reference.Copy();
            Name = name.Trim();
            DefinitionJson = definitionJson ?? "{}";
            State = state;
        }
    }

    public sealed class ResourceSchemaMapping
    {
        public ResourceRef Reference { get; private set; }
        public ResourceRef Source { get; private set; }
        public ResourceRef Schema { get; private set; }
        public string MappingJson { get; private set; }

        [JsonConstructor]
        public ResourceSchemaMapping(ResourceRef reference, ResourceRef source,
            ResourceRef schema, string mappingJson)
        {
            if (reference == null || !reference.IsExact || source == null || !source.IsExact ||
                schema == null || !schema.IsExact)
                throw new ArgumentException("Mapping, source and schema revisions must be exact.");
            Reference = reference.Copy();
            Source = source.Copy();
            Schema = schema.Copy();
            MappingJson = mappingJson ?? "{}";
        }
    }

    public sealed class DerivedResourceRevision
    {
        public ResourceRevisionMetadata Revision { get; private set; }
        public DerivedResourceMode Mode { get; private set; }
        public ResourceRef Schema { get; private set; }
        public ResourceRef Mapping { get; private set; }

        [JsonConstructor]
        public DerivedResourceRevision(ResourceRevisionMetadata revision, DerivedResourceMode mode,
            ResourceRef schema = null, ResourceRef mapping = null)
        {
            Revision = revision ?? throw new ArgumentNullException(nameof(revision));
            Mode = mode;
            Schema = schema == null ? null : schema.Copy();
            Mapping = mapping == null ? null : mapping.Copy();
        }
    }
}
