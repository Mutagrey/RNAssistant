using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace RNAssistant.Application.Contracts
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum BrowserCommandKind { Open, Layout, Navigate, Back, Forward, Reload, Focus, Close, ResumeAgent, Home, Stop, Attach }
    [JsonConverter(typeof(StringEnumConverter))]
    public enum BrowserPageStatus { Starting, Ready, Failed, Closed, Home, Unavailable, Removed }

    public sealed class BrowserBoundsDto
    {
        [JsonProperty("visible")] public bool Visible { get; set; }
        [JsonProperty("x")] public double X { get; set; }
        [JsonProperty("y")] public double Y { get; set; }
        [JsonProperty("width")] public double Width { get; set; }
        [JsonProperty("height")] public double Height { get; set; }
        [JsonProperty("viewportWidth")] public double ViewportWidth { get; set; }
        [JsonProperty("viewportHeight")] public double ViewportHeight { get; set; }
    }

    public sealed class BrowserCommandPayload
    {
        [JsonProperty("browserTabId")] public string BrowserTabId { get; set; }
        [JsonProperty("operationId")] public string OperationId { get; set; }
        [JsonProperty("ownerScope")] public BrowserOwnerScope OwnerScope { get; set; }
        [JsonProperty("target")] public BrowserTargetDescriptor Target { get; set; }
        [JsonProperty("locator")] public string Locator { get; set; }
        [JsonProperty("action", Required = Required.Always)] public BrowserCommandKind Action { get; set; }
        [JsonProperty("sessionId")] public string SessionId { get; set; }
        [JsonProperty("url")] public string Url { get; set; }
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("controlRevision")] public long? ControlRevision { get; set; }
        [JsonProperty("bounds")] public BrowserBoundsDto Bounds { get; set; }
    }

    public sealed class BrowserPageStateDto
    {
        [JsonProperty("browserTabId")] public string BrowserTabId { get; set; }
        [JsonProperty("operationId")] public string OperationId { get; set; }
        [JsonProperty("ownerScope")] public BrowserOwnerScope OwnerScope { get; set; }
        [JsonProperty("target")] public BrowserTargetDescriptor Target { get; set; }
        [JsonProperty("reason")] public BrowserRefusalCode Reason { get; set; }
        [JsonProperty("attached")] public bool Attached { get; set; }
        [JsonProperty("viewportRevision")] public long ViewportRevision { get; set; }
        [JsonProperty("documentGeneration")] public long DocumentGeneration { get; set; }
        [JsonProperty("sessionId")] public string SessionId { get; set; }
        [JsonProperty("revision")] public long Revision { get; set; }
        [JsonProperty("layoutRevision")] public long LayoutRevision { get; set; }
        [JsonProperty("manualControl")] public bool ManualControl { get; set; }
        [JsonProperty("controlRevision")] public long ControlRevision { get; set; }
        [JsonProperty("status")] public BrowserPageStatus Status { get; set; }
        [JsonProperty("url")] public string Url { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("canGoBack")] public bool CanGoBack { get; set; }
        [JsonProperty("canGoForward")] public bool CanGoForward { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
        [JsonProperty("diagnostic")] public string Diagnostic { get; set; }
    }

    public sealed class BrowserPageStateMessage
    {
        [JsonProperty("type")] public string Type { get { return "browserPageState"; } }
        [JsonProperty("payload")] public BrowserPageStateDto Payload { get; set; }
    }

    // Shell placement commands; native handles/profiles never enter layout persistence.
    public interface IBrowserPageHost
    {
        Task<BrowserPageStateDto> ExecuteAsync(BrowserCommandPayload request, CancellationToken cancellationToken);
    }

    public interface IBrowserPageRuntime : IBrowserPageHost, IBrowserActionSource { }

    // Runtime-only binding. Never a model argument or a persisted Dock route.
    public sealed class BrowserPageHandle
    {
        public string ChatId { get; }
        public string BrowserTabId { get; }
        public BrowserOwnerScope OwnerScope { get; }
        public string ContentRevision { get; }
        public string SavedVersionId { get; }
        public string SessionId { get; }
        public long DocumentGeneration { get; }
        public string Name { get; }
        public bool CanNavigate { get; }
        public string Target => "Browser page: " + Name;
        public BrowserPageHandle(string chatId, string sessionId, long documentGeneration, string name = "local", bool canNavigate = true,
            string browserTabId = null, BrowserOwnerScope ownerScope = null, string contentRevision = null, string savedVersionId = null)
        { ChatId = chatId; SessionId = sessionId; DocumentGeneration = documentGeneration; Name = name; CanNavigate = canNavigate;
            BrowserTabId = browserTabId; OwnerScope = ownerScope; ContentRevision = contentRevision; SavedVersionId = savedVersionId; }
        public bool Matches(BrowserPageHandle other)
        { return other != null && ChatId == other.ChatId && SessionId == other.SessionId && DocumentGeneration == other.DocumentGeneration &&
            BrowserTabId == other.BrowserTabId && ContentRevision == other.ContentRevision && SavedVersionId == other.SavedVersionId &&
            (OwnerScope == null ? other.OwnerScope == null : OwnerScope.Matches(other.OwnerScope)); }
    }

    public sealed class BrowserSemanticNode
    {
        [JsonProperty("role")] public string Role { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("disabled")] public bool Disabled { get; set; }
        [JsonProperty("checked")] public bool? Checked { get; set; }
    }

    public sealed class BrowserObservation
    {
        [JsonProperty("observationId")] public string ObservationId { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        // Address/time are assigned by the native owner, not page script.
        [JsonProperty("sourceUrl")] public string SourceUrl { get; set; }
        [JsonProperty("capturedUtc")] public DateTime CapturedUtc { get; set; }
        [JsonProperty("nodes")] public BrowserSemanticNode[] Nodes { get; set; }
        [JsonProperty("truncated")] public bool Truncated { get; set; }
        [JsonProperty("visited")] public int Visited { get; set; }
    }

    public interface IBrowserReadSource
    {
        IReadOnlyList<BrowserPageHandle> TargetsFor(string chatId);
        Task<BrowserObservation> ReadAsync(BrowserPageHandle target, CancellationToken cancellationToken);
    }

    // Native page content only. A qualified owner must verify privacy and freshness;
    // these bytes/coordinates never come from page JS or model tool arguments.
    public sealed class BrowserMediaCapture
    {
        public BrowserPageHandle Page { get; set; }
        public byte[] Png { get; set; }
        public DateTime CapturedUtc { get; set; }
        public string Title { get; set; }
        public string SourceAddress { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public long ViewportRevision { get; set; }
        public long ControlRevision { get; set; }
        public double DeviceScale { get; set; }
        public double Zoom { get; set; }
        public bool PrivacyVerified { get; set; }
    }

    public interface IBrowserMediaSource : IBrowserReadSource
    {
        Task<BrowserMediaCapture> ReadMediaAsync(BrowserPageHandle target, CancellationToken cancellationToken);
        bool IsCaptureCurrent(BrowserMediaCapture capture);
    }

    // Semantic actions use the native site policy and exact observations; no model JS/selectors.
    public sealed class BrowserActionRequest
    {
        [JsonProperty("action")] public string Action { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("value")] public string Value { get; set; }
        [JsonProperty("expectedText")] public string ExpectedText { get; set; }
        [JsonProperty("locator")] public string Locator { get; set; }
        [JsonProperty("button")] public string Button { get; set; } = "left";
        [JsonProperty("count")] public int Count { get; set; } = 1;
        [JsonProperty("modifiers")] public string[] Modifiers { get; set; } = new string[0];
        [JsonProperty("durationMs")] public int DurationMs { get; set; }
        [JsonProperty("toName")] public string ToName { get; set; }
        [JsonProperty("deltaX")] public int DeltaX { get; set; }
        [JsonProperty("deltaY")] public int DeltaY { get; set; }
        [JsonProperty("key")] public string Key { get; set; }
        [JsonProperty("checked")] public bool? Checked { get; set; }
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum BrowserActionEffect { Rejected, VerifiedNoChange, VerifiedChange, Unknown }

    public sealed class BrowserActionResult
    {
        [JsonProperty("effect", Required = Required.Always)] public BrowserActionEffect Effect { get; set; }
        [JsonProperty("code", Required = Required.Always)] public string Code { get; set; }
        // Native evidence only: navigation intentionally advances the document binding.
        [JsonIgnore] public BrowserPageHandle NavigationTarget { get; set; }
    }

    internal sealed class BrowserNavigationDispatch
    {
        [JsonProperty("started")] public bool Started { get; set; }
        [JsonProperty("unchanged")] public bool Unchanged { get; set; }
        [JsonProperty("code")] public string Code { get; set; }
    }

    internal sealed class BrowserLinkPreparation
    {
        [JsonProperty("prepared")] public bool Prepared { get; set; }
        [JsonProperty("url")] public string Url { get; set; }
        [JsonProperty("code")] public string Code { get; set; }
    }

    public interface IBrowserActionSource : IBrowserReadSource
    {
        Task PrepareAsync(BrowserPageHandle target, string observationRevision, string preparationId,
            BrowserActionRequest request, CancellationToken cancellationToken);
        Task<BrowserActionResult> ActAsync(BrowserPageHandle target, string preparationId,
            Action beforeDispatch, CancellationToken cancellationToken, bool navigation = false);
        void BlockActions(BrowserPageHandle target);
        // Best effort after terminal publication; must not throw or change execution state.
        void PresentResult(BrowserPageHandle target, string preparationId, BrowserActionEffect effect);
    }
}
