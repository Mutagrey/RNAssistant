using System.Collections.Generic;
using Newtonsoft.Json;

namespace RNAssistant.Core.Models
{
    public sealed class SemanticResourceField
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("nullable")] public bool Nullable { get; set; }
        [JsonProperty("unit")] public string Unit { get; set; }
    }
    public sealed class ResourceFieldMapping
    {
        [JsonProperty("field")] public string Field { get; set; }
        [JsonProperty("sourceField")] public string SourceField { get; set; }
    }
    public sealed class SemanticSchemaDefinition
    {
        [JsonProperty("contract")] public string Contract { get; set; } = "resource-schema-v1";
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("state")] public SemanticSchemaState State { get; set; }
        [JsonProperty("fields")] public List<SemanticResourceField> Fields { get; set; }
        [JsonProperty("validationSource")] public ResourceRef ValidationSource { get; set; }
        [JsonProperty("validationCoverage")] public ResourceCoverage ValidationCoverage { get; set; }
        [JsonProperty("validationRows")] public int ValidationRows { get; set; }
        [JsonProperty("validationComplete")] public bool ValidationComplete { get; set; }
    }
    public sealed class ResourceMappingDefinition
    {
        [JsonProperty("contract")] public string Contract { get; set; } = "resource-mapping-v1";
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("source")] public ResourceRef Source { get; set; }
        [JsonProperty("schema")] public ResourceRef Schema { get; set; }
        [JsonProperty("fields")] public List<ResourceFieldMapping> Fields { get; set; }
        [JsonProperty("skipRows")] public int SkipRows { get; set; }
        [JsonProperty("validationCoverage")] public ResourceCoverage ValidationCoverage { get; set; }
        [JsonProperty("sourceDependencies")] public List<ResourceDependency> SourceDependencies { get; set; }
    }
    public sealed class ResourceDerivedDefinition
    {
        [JsonProperty("contract")] public string Contract { get; set; } = "resource-derived-v1";
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("mode")] public DerivedResourceMode Mode { get; set; }
        [JsonProperty("mapping")] public ResourceRef Mapping { get; set; }
        [JsonProperty("source")] public ResourceRef Source { get; set; }
        [JsonProperty("schema")] public ResourceRef Schema { get; set; }
        [JsonProperty("fields")] public List<ResourceFieldMapping> Fields { get; set; }
        [JsonProperty("skipRows")] public int SkipRows { get; set; }
        [JsonProperty("sourceDependencies")] public List<ResourceDependency> SourceDependencies { get; set; }
    }
}
