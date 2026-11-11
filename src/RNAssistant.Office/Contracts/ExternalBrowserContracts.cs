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
        [JsonProperty("chatId")] public string ChatId { get; set; }
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

    // Runtime-only binding. Never a model argument or a persisted Dock route.
    public sealed class ExternalBrowserReadTarget
    {
        public string ChatId { get; }
        public string SessionId { get; }
        public long DocumentGeneration { get; }
        public ExternalBrowserReadTarget(string chatId, string sessionId, long documentGeneration)
        { ChatId = chatId; SessionId = sessionId; DocumentGeneration = documentGeneration; }
        public bool Matches(ExternalBrowserReadTarget other)
        { return other != null && ChatId == other.ChatId && SessionId == other.SessionId && DocumentGeneration == other.DocumentGeneration; }
    }

    public sealed class ExternalBrowserSemanticNode
    {
        [JsonProperty("role")] public string Role { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("disabled")] public bool Disabled { get; set; }
        [JsonProperty("checked")] public bool? Checked { get; set; }
    }

    public sealed class ExternalBrowserSemanticContent
    {
        [JsonProperty("observationId")] public string ObservationId { get; set; }
        [JsonProperty("nodes")] public ExternalBrowserSemanticNode[] Nodes { get; set; }
        [JsonProperty("truncated")] public bool Truncated { get; set; }
        [JsonProperty("visited")] public int Visited { get; set; }
    }

    public interface IExternalBrowserReadSource
    {
        ExternalBrowserReadTarget TargetFor(string chatId);
        Task<ExternalBrowserSemanticContent> ReadAsync(ExternalBrowserReadTarget target, CancellationToken cancellationToken);
    }

    // P6b is deliberately restricted to the packaged local probe. No model JS/selectors.
    public sealed class ExternalBrowserActionRequest
    {
        [JsonProperty("action")] public string Action { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("value")] public string Value { get; set; }
        [JsonProperty("expectedText")] public string ExpectedText { get; set; }
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum ExternalBrowserEffect { Rejected, VerifiedNoChange, VerifiedChange, Unknown }

    public sealed class ExternalBrowserActionResult
    {
        [JsonProperty("effect", Required = Required.Always)] public ExternalBrowserEffect Effect { get; set; }
        [JsonProperty("code", Required = Required.Always)] public string Code { get; set; }
    }

    public interface IExternalBrowserActionSource : IExternalBrowserReadSource
    {
        Task PrepareAsync(ExternalBrowserReadTarget target, string observationRevision, string preparationId,
            ExternalBrowserActionRequest request, CancellationToken cancellationToken);
        Task<ExternalBrowserActionResult> ActAsync(ExternalBrowserReadTarget target, string preparationId,
            Action beforeDispatch, CancellationToken cancellationToken);
        void BlockActions(ExternalBrowserReadTarget target);
        // Best effort after terminal publication; must not throw or change execution state.
        void PresentResult(ExternalBrowserReadTarget target, string preparationId, ExternalBrowserEffect effect);
    }
}
