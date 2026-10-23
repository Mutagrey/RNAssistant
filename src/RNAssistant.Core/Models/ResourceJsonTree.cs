using System.Collections.Generic;
using Newtonsoft.Json;

namespace RNAssistant.Core.Models
{
    // Navigation over one exact JSON source revision. Nodes are pre-order and
    // describe values, not raw source lexemes or mutation authority.
    public sealed class ResourceJsonTree
    {
        [JsonProperty("pointer")] public string Pointer { get; set; }
        [JsonProperty("depth")] public int Depth { get; set; }
        [JsonProperty("offset")] public int Offset { get; set; }
        [JsonProperty("limit")] public int Limit { get; set; }
        [JsonProperty("nodeType")] public string NodeType { get; set; }
        [JsonProperty("totalChildren")] public long TotalChildren { get; set; }
        [JsonProperty("returnedChildren")] public int ReturnedChildren { get; set; }
        [JsonProperty("nextOffset", NullValueHandling = NullValueHandling.Ignore)] public long? NextOffset { get; set; }
        [JsonProperty("nodes")] public List<ResourceJsonTreeNode> Nodes { get; set; } = new List<ResourceJsonTreeNode>();

        public ResourceCoverage Coverage()
        {
            return new ResourceCoverage(ResourceCoverageKinds.JsonPath,
                address: "depth=" + Depth + ";limit=" + Limit, start: Offset,
                end: (long)Offset + ReturnedChildren, path: Pointer);
        }
    }

    public sealed class ResourceJsonTreeNode
    {
        [JsonProperty("pointer")] public string Pointer { get; set; }
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("value", NullValueHandling = NullValueHandling.Ignore)] public string Value { get; set; }
        // Numbers are navigation previews: Json.NET can normalize floating lexemes.
        [JsonProperty("valueExact", NullValueHandling = NullValueHandling.Ignore)] public bool? ValueExact { get; set; }
        [JsonProperty("childCount", NullValueHandling = NullValueHandling.Ignore)] public long? ChildCount { get; set; }
    }
}
