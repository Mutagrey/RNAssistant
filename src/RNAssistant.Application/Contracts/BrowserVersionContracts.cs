using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace RNAssistant.Application.Contracts
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum BrowserVersionAction { Save, Activate, Delete }
    [JsonConverter(typeof(StringEnumConverter))]
    public enum BrowserVersionOutcome { Saved, AlreadySaved, Activated, Removed, Rejected, Unknown }

    public sealed class BrowserVersionEntry
    {
        [JsonProperty("versionId")] public string VersionId { get; set; }
        [JsonProperty("ordinal")] public long Ordinal { get; set; }
        [JsonProperty("label")] public string Label { get; set; }
        [JsonProperty("createdUtc")] public DateTime CreatedUtc { get; set; }
        [JsonProperty("manifestVersion")] public string ManifestVersion { get; set; }
        [JsonProperty("releaseId")] public string ReleaseId { get; set; }
        [JsonProperty("contentRevision")] public string ContentRevision { get; set; }
        [JsonProperty("active")] public bool Active { get; set; }
        [JsonProperty("current")] public bool Current { get; set; }
        [JsonProperty("removed")] public bool Removed { get; set; }
        [JsonProperty("canDelete")] public bool CanDelete { get; set; }
        [JsonProperty("deleteBlockedReason")] public BrowserRefusalCode DeleteBlockedReason { get; set; }
    }

    public sealed class BrowserVersionState
    {
        [JsonProperty("target")] public BrowserTargetDescriptor Target { get; set; }
        [JsonProperty("stateRevision")] public string StateRevision { get; set; }
        [JsonProperty("activeStateRevision")] public string ActiveStateRevision { get; set; }
        [JsonProperty("contentRevision")] public string ContentRevision { get; set; }
        [JsonProperty("definitionRevision")] public string DefinitionRevision { get; set; }
        [JsonProperty("dirty")] public bool Dirty { get; set; }
        [JsonProperty("latestAvailableSavedVersion")] public BrowserVersionEntry LatestAvailableSavedVersion { get; set; }
        [JsonProperty("activeSavedVersion")] public BrowserVersionEntry ActiveSavedVersion { get; set; }
        [JsonProperty("capabilities")] public BrowserTargetCapabilities Capabilities { get; set; }
    }

    public sealed class BrowserVersionPage
    {
        [JsonProperty("items")] public IReadOnlyList<BrowserVersionEntry> Items { get; set; }
        [JsonProperty("cursor")] public string Cursor { get; set; }
        [JsonProperty("stateRevision")] public string StateRevision { get; set; }
    }

    public sealed class BrowserVersionRequest
    {
        [JsonProperty("target", Required = Required.Always)] public BrowserTargetDescriptor Target { get; set; }
        [JsonProperty("cursor")] public string Cursor { get; set; }
        [JsonProperty("limit")] public int Limit { get; set; } = 12;
    }

    public sealed class BrowserVersionCommand
    {
        [JsonProperty("action", Required = Required.Always)] public BrowserVersionAction Action { get; set; }
        [JsonProperty("target", Required = Required.Always)] public BrowserTargetDescriptor Target { get; set; }
        [JsonProperty("versionId")] public string VersionId { get; set; }
        [JsonProperty("operationId")] public string OperationId { get; set; }
        [JsonProperty("expectedStateRevision")] public string ExpectedStateRevision { get; set; }
        [JsonProperty("expectedContentRevision")] public string ExpectedContentRevision { get; set; }
        [JsonProperty("expectedDefinitionRevision")] public string ExpectedDefinitionRevision { get; set; }
        [JsonProperty("expectedActiveVersionId")] public string ExpectedActiveVersionId { get; set; }
        [JsonProperty("expectedActiveReleaseId")] public string ExpectedActiveReleaseId { get; set; }
        [JsonProperty("expectedActiveRevision")] public string ExpectedActiveRevision { get; set; }
        [JsonProperty("confirmed")] public bool Confirmed { get; set; }
    }

    public sealed class BrowserVersionResult
    {
        [JsonProperty("outcome")] public BrowserVersionOutcome Outcome { get; set; }
        [JsonProperty("reason")] public BrowserRefusalCode Reason { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
        [JsonProperty("version")] public BrowserVersionEntry Version { get; set; }
        [JsonProperty("state")] public BrowserVersionState State { get; set; }
    }

    public sealed class BrowserVersionStateChanged
    {
        [JsonProperty("type")] public string Type { get { return "browserVersionStateChanged"; } }
        [JsonProperty("target")] public BrowserTargetDescriptor Target { get; set; }
        [JsonProperty("stateRevision")] public string StateRevision { get; set; }
        [JsonProperty("reason")] public BrowserVersionAction Reason { get; set; }
    }
}
