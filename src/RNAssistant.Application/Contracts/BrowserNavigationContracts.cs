using System.Collections.Generic;
using Newtonsoft.Json;

namespace RNAssistant.Application.Contracts
{
    public sealed class BrowserResolveRequest
    {
        [JsonProperty("ownerScope")] public BrowserOwnerScope OwnerScope { get; set; }
        [JsonProperty("locator")] public string Locator { get; set; }
        [JsonProperty("browserTabId")] public string BrowserTabId { get; set; }
    }

    public sealed class BrowserResolveResult
    {
        [JsonProperty("locator")] public string Locator { get; set; }
        [JsonProperty("target")] public BrowserTargetDescriptor Target { get; set; }
        [JsonProperty("reason")] public BrowserRefusalCode Reason { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
    }

    public sealed class BrowserSuggestionRequest
    {
        [JsonProperty("ownerScope")] public BrowserOwnerScope OwnerScope { get; set; }
        [JsonProperty("prefix")] public string Prefix { get; set; }
        [JsonProperty("requestGeneration")] public long RequestGeneration { get; set; }
        [JsonProperty("limit")] public int Limit { get; set; } = 12;
    }

    public sealed class BrowserNavigationSuggestion
    {
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("kind")] public BrowserTargetKind Kind { get; set; }
        [JsonProperty("displayLocator")] public string DisplayLocator { get; set; }
        [JsonProperty("sourceLabel")] public string SourceLabel { get; set; }
        [JsonProperty("disabledReason")] public BrowserRefusalCode DisabledReason { get; set; }
    }

    public sealed class BrowserSuggestionPage
    {
        [JsonProperty("items")] public IReadOnlyList<BrowserNavigationSuggestion> Items { get; set; }
        [JsonProperty("requestGeneration")] public long RequestGeneration { get; set; }
    }
}
