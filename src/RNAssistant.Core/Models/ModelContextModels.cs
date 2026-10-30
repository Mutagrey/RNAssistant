using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Newtonsoft.Json;

namespace RNAssistant.Core.Models
{
    public sealed class SkillCatalogSnapshot
    {
        private readonly string _json;
        private readonly string _metadata;
        private readonly Dictionary<string, string> _revisions;
        public string Generation { get; private set; }
        public IReadOnlyList<SkillDefinition> Skills
        { get { return Array.AsReadOnly(JsonConvert.DeserializeObject<SkillDefinition[]>(_json)); } }
        public IReadOnlyList<RNAssistant.Core.Services.ArsenalSkill> Metadata
        { get { return JsonConvert.DeserializeObject<RNAssistant.Core.Services.ArsenalSkill[]>(_metadata); } }
        public string Revision(string id)
        { string revision; return id != null && _revisions.TryGetValue(id, out revision) ? revision : null; }
        public SkillCatalogSnapshot(IEnumerable<SkillDefinition> skills, string publicationRevision = null)
        {
            var values = (skills ?? new SkillDefinition[0]).Where(item => item != null)
                .OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
            _json = JsonConvert.SerializeObject(values);
            _revisions = values.ToDictionary(item => item.Id, SkillRevision.Compute, StringComparer.Ordinal);
            _metadata = JsonConvert.SerializeObject(values.Where(item => item.Enabled).Select(item =>
                new RNAssistant.Core.Services.ArsenalSkill { Id = item.Id, Name = item.Name, Description = item.Description, Host = item.Host,
                    Revision = Revision(item.Id), Status = "available",
                    Tools = (item.Package?.RequiredTools ?? new List<SkillToolDependency>()).Select(tool => tool.Id).ToList(),
                    References = (item.References ?? new List<SkillReferenceMetadata>()).Select(reference => reference.Path).ToList() }));
            Generation = SkillRevision.ComputeMarkdown((publicationRevision ?? "") + "\n" + string.Join("\n", values.Select(item => Revision(item.Id))));
        }
    }

    // Entries are the exact published heads, not draft bodies discovered in storage.
    public sealed class SchemaRegistrySnapshot
    {
        private readonly ResourceRef[] _schemas;
        public string Generation { get; private set; }
        public IReadOnlyList<ResourceRef> Schemas { get { return Array.AsReadOnly(_schemas.Select(item => item.Copy()).ToArray()); } }
        public SchemaRegistrySnapshot(IEnumerable<ResourceRef> schemas)
        {
            _schemas = (schemas ?? new ResourceRef[0]).OrderBy(item => item.Identity.Uri, StringComparer.Ordinal)
                .Select(item => item.IsExact ? item.Copy() : throw new ArgumentException("Published schemas require exact revisions.")).ToArray();
            Generation = SkillRevision.ComputeMarkdown(JsonConvert.SerializeObject(_schemas));
        }
    }

    public sealed class ModelAuthoritySnapshot
    {
        public ResourceAuthoritySnapshotSet Resources { get; private set; }
        public string ToolGeneration { get; private set; }
        public SkillCatalogSnapshot Skills { get; private set; }
        public SchemaRegistrySnapshot Schemas { get; private set; }
        public string SchemaGeneration { get { return Schemas.Generation; } }
        public long ConversationHighWaterMark { get; private set; }
        public ModelAuthoritySnapshot(ResourceAuthoritySnapshotSet resources, string toolGeneration,
            SkillCatalogSnapshot skills, SchemaRegistrySnapshot schemas, long conversationHighWaterMark)
        {
            Resources = resources ?? throw new ArgumentNullException(nameof(resources));
            ToolGeneration = toolGeneration ?? throw new ArgumentNullException(nameof(toolGeneration));
            Skills = skills ?? throw new ArgumentNullException(nameof(skills));
            Schemas = schemas ?? new SchemaRegistrySnapshot(null);
            ConversationHighWaterMark = conversationHighWaterMark;
        }
    }

    [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public enum ContextPresentationKind { Unknown, Full, Fragment, Summary, Excluded }

    [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public enum ContextExclusionKind
    {
        Unknown, Explicit, SupersededEvidence, UnknownEvidence, UnavailableEvidence,
        DuplicateBody, Budget, SelectionLimit, OperationBudget, OperationSelectionLimit,
        OperationReceipt
    }

    // Observed coverage belongs to the source evidence. The nullable body flag
    // describes attribution to this exact message, not delivery elsewhere.
    public sealed class ContextEvidenceLink
    {
        public string EvidenceId { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public ResourceAuthorityScopeId ScopeId { get; set; }
        public ResourceRef Resource { get; set; }
        public string View { get; set; }
        public ResourceCoverage ObservedCoverage { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public bool? ObservationComplete { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public ResourceReadMetadata ReadMetadata { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public bool? BodyIncludedInMessage { get; set; }
    }

    // Diagnostic provenance only; never serialized into provider messages.
    public sealed class ContextMessagePresentation
    {
        // The compiler carries the issued terminal alias; roles alone cannot distinguish a call from a folded receipt.
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public RNAssistant.Core.ModelProtocol.ModelReference EvidenceReference { get; set; }
        public int? MessageIndex { get; set; }
        public string SourceMessageId { get; set; }
        public string Role { get; set; }
        public string Kind { get; set; }
        public ContextPresentationKind Presentation { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public ContextExclusionKind? ExclusionKind { get; set; }
        public string Reason { get; set; }
        public PayloadRef OriginalPayload { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string ToolCallId { get; set; }
        public List<ContextEvidenceLink> EvidenceLinks { get; set; } = new List<ContextEvidenceLink>();
        // Estimate of the entire serialized read-result message, not its source
        // body or measured provider usage. Set only on an exact wire request.
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public int? EstimatedWireMessageTokens { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public int? PartIndex { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string SourceAttachmentId { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public int? PageNumber { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public int? OmittedCount { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public int? SelectedCharacters { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public int? ByteLength { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string ContentSha256 { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string ContentType { get; set; }
        public List<ContextMessagePresentation> Parts { get; set; } = new List<ContextMessagePresentation>();
    }

    public sealed class ContextReceipt
    {
        public RNAssistant.Core.Services.AgentArsenal Arsenal { get; set; }
        public List<ContextMessagePresentation> Messages { get; set; } = new List<ContextMessagePresentation>();
        public string SnapshotId { get; set; }
        public string ToolGeneration { get; set; }
        public string SkillGeneration { get; set; }
        public string SchemaGeneration { get; set; }
        public long ConversationHighWaterMark { get; set; }
        public Dictionary<string, long> ResourceGenerations { get; set; } = new Dictionary<string, long>();
        public Dictionary<string, int> AtomCounts { get; set; } = new Dictionary<string, int>();
        public int ExcludedSuperseded { get; set; }
        public int ExcludedUnknown { get; set; }
        public int ExcludedUnavailable { get; set; }
        public int Deduplicated { get; set; }
        public int HydratedPayloads { get; set; }
        public long HydratedBytes { get; set; }
        public int EstimatedTokens { get; set; }
        public bool CompactionApplied { get; set; }
        public int RejectedClaims { get; set; }
        public int OperationReceipts { get; set; }
        public int RetainedBodies { get; set; }
        public int EvictedBodies { get; set; }
    }

    // Historical outcome and observation currency are independent. This detached
    // projection is reconstructed from terminal events, never inferred by an LLM.
    public sealed class CompletedToolOperation
    {
        public string ToolCallId { get; set; }
        public string ToolName { get; set; }
        public RNAssistant.Core.Tools.Contracts.ToolResultStatus Status { get; set; }
        public string Message { get; set; }
        public string DataJson { get; set; }
        public List<string> Targets { get; set; } = new List<string>();
    }

    public sealed class ResourceObservationNotice
    {
        [JsonProperty("target")]
        public string Target { get; set; }
        [JsonProperty("state")]
        public EvidenceState State { get; set; }
        [JsonProperty("bodyIncluded")]
        public bool BodyIncluded { get; set; }
        [JsonProperty("reason")]
        public string Reason { get; set; }
        [JsonProperty("nextAction")]
        public string NextAction { get; set; }
    }

    public sealed class ModelContextSnapshot
    {
        private readonly string _messages;
        private readonly string _receipt;
        private readonly string _deliveredObservations;
        public string Id { get; private set; }
        public ModelAuthoritySnapshot Authority { get; private set; }
        // Consumers get detached values: repairing a request cannot change its frozen source.
        public IReadOnlyList<ChatMessage> Messages
        { get { return Array.AsReadOnly(JsonConvert.DeserializeObject<ChatMessage[]>(_messages)); } }
        // Current observations whose bodies survived materialization and the final budget.
        // Presentation (native result or synthetic archive view) does not grant authority.
        public IReadOnlyList<ResourceEvidence> DeliveredObservations
        { get { return Array.AsReadOnly(JsonConvert.DeserializeObject<ResourceEvidence[]>(_deliveredObservations)); } }
        public ContextReceipt Receipt { get { return JsonConvert.DeserializeObject<ContextReceipt>(_receipt); } }
        public ModelContextSnapshot(ModelAuthoritySnapshot authority, IEnumerable<ChatMessage> messages,
            ContextReceipt receipt, IEnumerable<ResourceEvidence> deliveredObservations)
        {
            Id = receipt.SnapshotId;
            Authority = authority;
            _messages = JsonConvert.SerializeObject(messages);
            _receipt = JsonConvert.SerializeObject(receipt);
            _deliveredObservations = JsonConvert.SerializeObject(deliveredObservations);
        }
    }
}
