using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace RNAssistant.Office.Contracts
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum ExternalBrowserAction { Open, Layout, Navigate, Back, Forward, Reload, Focus, Close }
    [JsonConverter(typeof(StringEnumConverter))]
    public enum ExternalBrowserStatus { Starting, Ready, Failed, Closed }

    public sealed class ExternalBrowserBoundsDto
    {
        [JsonProperty("visible")] public bool Visible { get; set; }
        [JsonProperty("x")] public double X { get; set; }
        [JsonProperty("y")] public double Y { get; set; }
        [JsonProperty("width")] public double Width { get; set; }
        [JsonProperty("height")] public double Height { get; set; }
        [JsonProperty("viewportWidth")] public double ViewportWidth { get; set; }
        [JsonProperty("viewportHeight")] public double ViewportHeight { get; set; }
    }

    public sealed class ExternalBrowserCommandPayload
    {
        [JsonProperty("action", Required = Required.Always)] public ExternalBrowserAction Action { get; set; }
        [JsonProperty("sessionId")] public string SessionId { get; set; }
        [JsonProperty("url")] public string Url { get; set; }
        [JsonProperty("bounds")] public ExternalBrowserBoundsDto Bounds { get; set; }
    }

    public sealed class ExternalBrowserStateDto
    {
        [JsonProperty("sessionId")] public string SessionId { get; set; }
        [JsonProperty("revision")] public long Revision { get; set; }
        [JsonProperty("layoutRevision")] public long LayoutRevision { get; set; }
        [JsonProperty("status")] public ExternalBrowserStatus Status { get; set; }
        [JsonProperty("url")] public string Url { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("canGoBack")] public bool CanGoBack { get; set; }
        [JsonProperty("canGoForward")] public bool CanGoForward { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
        [JsonProperty("diagnostic")] public string Diagnostic { get; set; }
    }

    public sealed class ExternalBrowserStateMessage
    {
        [JsonProperty("type")] public string Type { get { return "externalBrowserState"; } }
        [JsonProperty("payload")] public ExternalBrowserStateDto Payload { get; set; }
    }

    // Manual P5 pilot only. Native handles, profiles and page lifetime never enter layout persistence.
    public interface IExternalBrowserHost
    {
        Task<ExternalBrowserStateDto> ExecuteAsync(ExternalBrowserCommandPayload request, CancellationToken cancellationToken);
    }
}
