using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace RNAssistant.Application.Contracts
{
    public sealed class BrowserOpenableTarget
    {
        public string SemanticTarget { get; set; }
        public BrowserTargetDescriptor Target { get; set; }
    }
    // Runtime identities; never model arguments or persisted Dock preferences.
    public sealed class BrowserAgentOpenRequest
    {
        public BrowserOwnerScope OwnerScope { get; set; }
        public BrowserTargetDescriptor Target { get; set; }
        public string OperationId { get; set; }
        public string RunId { get; set; }
        public string CallId { get; set; }
    }
    [JsonConverter(typeof(StringEnumConverter))]
    public enum BrowserDockOpenOrigin { Agent, PageLink, AgentLink }

    public sealed class BrowserDockOpenRequest
    {
        [JsonProperty("type")] public string Type { get { return "browserOpenRequested"; } }
        [JsonProperty("origin")] public BrowserDockOpenOrigin Origin { get; set; }
        [JsonProperty("browserTabId")] public string BrowserTabId { get; set; }
        [JsonProperty("operationId")] public string OperationId { get; set; }
        [JsonProperty("ownerScope")] public BrowserOwnerScope OwnerScope { get; set; }
        [JsonProperty("locator")] public string Locator { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("page", NullValueHandling = NullValueHandling.Ignore)] public BrowserPageStateDto Page { get; set; }
        [JsonProperty("sourceBrowserTabId", NullValueHandling = NullValueHandling.Ignore)] public string SourceBrowserTabId { get; set; }
        [JsonProperty("sourceSessionId", NullValueHandling = NullValueHandling.Ignore)] public string SourceSessionId { get; set; }
        [JsonProperty("sourceGeneration", NullValueHandling = NullValueHandling.Ignore)] public long? SourceGeneration { get; set; }
    }
    public interface IBrowserOpenSource
    {
        IReadOnlyList<BrowserOpenableTarget> OpenableTargetsFor(BrowserOwnerScope owner);
        void BlockAgentOpen(string operationId);
        Task<BrowserPageStateDto> OpenAgentAsync(BrowserAgentOpenRequest request, Action beforeDispatch, CancellationToken token);
    }
}
