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
        public string Generation { get; private set; }
        public IReadOnlyList<SkillDefinition> Skills
        { get { return Array.AsReadOnly(JsonConvert.DeserializeObject<SkillDefinition[]>(_json)); } }
        public SkillCatalogSnapshot(IEnumerable<SkillDefinition> skills, string publicationRevision = null)
        {
            var values = (skills ?? new SkillDefinition[0]).Where(item => item != null)
                .OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
            _json = JsonConvert.SerializeObject(values);
            Generation = SkillRevision.ComputeMarkdown((publicationRevision ?? "") + "\n" + string.Join("\n", values.Select(SkillRevision.Compute)));
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

    public sealed class ContextReceipt
    {
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
    }

    public sealed class ModelContextSnapshot
    {
        private readonly string _messages;
        private readonly string _receipt;
        public string Id { get; private set; }
        public ModelAuthoritySnapshot Authority { get; private set; }
        // Consumers get detached values: repairing a request cannot change its frozen source.
        public IReadOnlyList<ChatMessage> Messages
        { get { return Array.AsReadOnly(JsonConvert.DeserializeObject<ChatMessage[]>(_messages)); } }
        public ContextReceipt Receipt { get { return JsonConvert.DeserializeObject<ContextReceipt>(_receipt); } }
        public ModelContextSnapshot(ModelAuthoritySnapshot authority, IEnumerable<ChatMessage> messages, ContextReceipt receipt)
        {
            Id = receipt.SnapshotId;
            Authority = authority;
            _messages = JsonConvert.SerializeObject(messages);
            _receipt = JsonConvert.SerializeObject(receipt);
        }
    }
}
