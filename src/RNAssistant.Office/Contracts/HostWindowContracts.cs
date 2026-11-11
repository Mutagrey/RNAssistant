using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace RNAssistant.Office.Contracts
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum HostWindowPresentation { FullNormal, FullMaximized, Compact, Companion }
    [JsonConverter(typeof(StringEnumConverter))]
    public enum HostWindowVisibility { Visible, ContextHidden, Dismissed, SystemMinimized, ShuttingDown }
    [JsonConverter(typeof(StringEnumConverter))]
    public enum HostWindowCommand
    {
        ShowFull, ShowCompact, Park, Maximize, Restore, SetPinned, SetAutoCollapse,
        Dismiss, SystemMinimize, ResetPlacement, ChromeReady, Shutdown
    }

    public sealed class HostWindowCapabilitiesDto
    {
        [JsonProperty("integratedChrome")] public bool IntegratedChrome { get; set; }
        [JsonProperty("compactPresentation")] public bool CompactPresentation { get; set; }
        [JsonProperty("companion")] public bool Companion { get; set; }
        [JsonProperty("pin")] public bool Pin { get; set; }
    }

    public sealed class HostWindowStateDto
    {
        [JsonProperty("revision")] public long Revision { get; set; }
        [JsonProperty("presentation")] public HostWindowPresentation Presentation { get; set; }
        [JsonProperty("visibility")] public HostWindowVisibility Visibility { get; set; }
        [JsonProperty("pinned")] public bool Pinned { get; set; }
        [JsonProperty("autoCollapse")] public bool AutoCollapse { get; set; }
        [JsonProperty("chromeReady")] public bool ChromeReady { get; set; }
        [JsonProperty("anchorAvailable")] public bool AnchorAvailable { get; set; }
        [JsonProperty("diagnostic")] public string Diagnostic { get; set; }
        [JsonProperty("canMaximize")] public bool CanMaximize { get; set; }
        [JsonProperty("canRestore")] public bool CanRestore { get; set; }
        [JsonProperty("canCompact")] public bool CanCompact { get; set; }
        [JsonProperty("canDismiss")] public bool CanDismiss { get; set; }
    }

    public sealed class HostWindowCommandPayload
    {
        [JsonProperty("command", Required = Required.Always)] public HostWindowCommand Command { get; set; }
        [JsonProperty("value")] public bool? Value { get; set; }
    }
    public sealed class HostWindowCommandResponse
    {
        [JsonProperty("accepted")] public bool Accepted { get; set; }
        [JsonProperty("state")] public HostWindowStateDto State { get; set; }
    }
    public sealed class HostWindowBootstrapDto
    {
        [JsonProperty("capabilities")] public HostWindowCapabilitiesDto Capabilities { get; set; }
        [JsonProperty("state")] public HostWindowStateDto State { get; set; }
    }
    public sealed class HostWindowStateMessage
    {
        [JsonProperty("type")] public string Type { get { return "hostWindowState"; } }
        [JsonProperty("payload")] public HostWindowStateDto Payload { get; set; }
    }
    public sealed class HostWindowShutdownMessage
    {
        [JsonProperty("type")] public string Type { get { return "hostWindowShutdownRequested"; } }
    }
    public sealed class HostWindowActivity
    {
        public int Running { get; set; }
        public int Waiting { get; set; }
    }

    // One host/session. No HWNDs, Office targets or model authority cross this boundary.
    public interface IAssistantWindowHost
    {
        HostWindowBootstrapDto Snapshot { get; }
        event Action<HostWindowStateDto> StateChanged;
        event Action ShutdownRequested;
        Task<HostWindowCommandResponse> ExecuteAsync(HostWindowCommandPayload command, string requestId);
        void ResponsePosted(string requestId);
        void ConfigureChrome(bool supported);
        void InvalidateChrome();
    }
}
