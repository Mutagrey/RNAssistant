using Newtonsoft.Json;

namespace RNAssistant.Office.Contracts
{
    // The unprivileged page supplies only a known binding name or its own lease.
    public sealed class BrowserHtmlExportResponse
    {
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("target")] public RNAssistant.Application.Contracts.BrowserTargetDescriptor Target { get; set; }
        [JsonProperty("source")] public ResourceDownloadOpenResponse Source { get; set; }
        [JsonProperty("resourceExport")] public HtmlResourceExport ResourceExport { get; set; }
    }
    public sealed class BrowserResourceChangedMessage
    {
        [JsonProperty("type")] public string Type => "rnassistant-resource-changed";
        [JsonProperty("names")] public string[] Names { get; set; }
        [JsonProperty("generation")] public long Generation { get; set; }
    }
    public sealed class BrowserResourceControlRequest
    {
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("requestId")] public string RequestId { get; set; }
        [JsonProperty("operation")] public string Operation { get; set; }
        [JsonProperty("bindingName")] public string BindingName { get; set; }
        [JsonProperty("leaseId")] public string LeaseId { get; set; }
    }
    public sealed class BrowserResourceControlResponse
    {
        [JsonProperty("type")] public string Type => "rnassistant-resource-result";
        [JsonProperty("requestId")] public string RequestId { get; set; }
        [JsonProperty("ok")] public bool Ok { get; set; }
        [JsonProperty("error")] public string Error { get; set; }
        [JsonProperty("value", NullValueHandling = NullValueHandling.Ignore)] public ResourceDataOpenResponse Value { get; set; }
    }
}
