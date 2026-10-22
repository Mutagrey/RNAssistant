using Newtonsoft.Json;

namespace RNAssistant.Core.Models
{
    // Exact source coordinates, never a compiler decision about the active VBA branch.
    public sealed class ResourceProcedureSelection
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("kind")] public string Kind { get; set; }
        [JsonProperty("startChar")] public int StartChar { get; set; }
        [JsonProperty("endChar")] public int EndChar { get; set; }
        [JsonProperty("startLine")] public int StartLine { get; set; }
        [JsonProperty("endLine")] public int EndLine { get; set; }
        [JsonProperty("conditionalCompilation")] public bool ConditionalCompilation { get; set; }
    }
}
