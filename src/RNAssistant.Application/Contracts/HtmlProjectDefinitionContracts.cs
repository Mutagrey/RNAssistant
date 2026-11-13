using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Application.Contracts
{
    public sealed class HtmlProjectDefinitionRequest
    {
        [JsonProperty("ownerScope", Required = Required.Always)] public BrowserOwnerScope OwnerScope { get; set; }
        [JsonProperty("workspaceId", Required = Required.Always)] public string WorkspaceId { get; set; }
        [JsonProperty("expectedFingerprint")] public string ExpectedFingerprint { get; set; }
        [JsonProperty("definition")] public HtmlProjectDefinition Definition { get; set; }
    }
}
