using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace RNAssistant.Core.Models
{
    // Navigation over one exact structured-text revision. Nodes are pre-order and
    // describe values, not raw source lexemes or mutation authority.
    public sealed class ResourceTree
    {
        [JsonProperty("format")] public string Format { get; set; }
        [JsonProperty("pointer")] public string Pointer { get; set; }
        [JsonProperty("depth")] public int Depth { get; set; }
        [JsonProperty("offset")] public int Offset { get; set; }
        [JsonProperty("limit")] public int Limit { get; set; }
        [JsonProperty("nodeType")] public string NodeType { get; set; }
        [JsonProperty("totalChildren")] public long TotalChildren { get; set; }
        [JsonProperty("returnedChildren")] public int ReturnedChildren { get; set; }
        [JsonProperty("nextOffset", NullValueHandling = NullValueHandling.Ignore)] public long? NextOffset { get; set; }
        [JsonProperty("nodes")] public List<ResourceTreeNode> Nodes { get; set; } = new List<ResourceTreeNode>();

        public ResourceCoverage Coverage()
        {
            if (Format != "json" && Format != "yaml")
                throw new InvalidOperationException("Unsupported structured tree format.");
            return new ResourceCoverage(Format == "yaml" ? ResourceCoverageKinds.YamlPath : ResourceCoverageKinds.JsonPath,
                address: "depth=" + Depth + ";limit=" + Limit, start: Offset,
                end: (long)Offset + ReturnedChildren, path: Pointer);
        }
    }

    public sealed class ResourceTreeNode
    {
        [JsonProperty("pointer")] public string Pointer { get; set; }
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("value", NullValueHandling = NullValueHandling.Ignore)] public string Value { get; set; }
        // JSON numbers and YAML scalars may normalize source lexemes.
        [JsonProperty("valueExact", NullValueHandling = NullValueHandling.Ignore)] public bool? ValueExact { get; set; }
        [JsonProperty("childCount", NullValueHandling = NullValueHandling.Ignore)] public long? ChildCount { get; set; }
    }
}
