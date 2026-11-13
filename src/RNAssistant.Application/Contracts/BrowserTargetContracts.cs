using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace RNAssistant.Application.Contracts
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum BrowserTargetKind { HtmlArtifact, HtmlResource, HtmlWorkspace, PluginSource, PluginRelease, WorkspaceHtmlProject, Home }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum BrowserTargetState { Ready, Empty, Unavailable, Removed }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum BrowserOwnerKind { Document, Workspace, User }

    public sealed class BrowserOwnerScope
    {
        [JsonProperty("kind")] public BrowserOwnerKind Kind { get; set; }
        [JsonProperty("scopeId")] public string ScopeId { get; set; }
        [JsonProperty("chatId")] public string ChatId { get; set; }
        public bool Matches(BrowserOwnerScope other)
        { return other != null && Kind == other.Kind && ScopeId == other.ScopeId && ChatId == other.ChatId; }
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum BrowserRefusalCode
    {
        None, InvalidLocator, OutsideScope, TargetUnavailable, AmbiguousTarget, Removed,
        ReadOnly, ProjectDefinitionRequired, IsolationUnqualified, NativeUnavailable,
        StaleBinding, SavedVersionRequired, ActiveVersion, VersionInUse, OperationUnknown
    }

    public sealed class BrowserTargetCapabilities
    {
        [JsonProperty("preview")] public bool Preview { get; set; }
        [JsonProperty("source")] public bool Source { get; set; }
        [JsonProperty("edit")] public bool Edit { get; set; }
        [JsonProperty("versions")] public bool Versions { get; set; }
        [JsonProperty("restore")] public bool Restore { get; set; }
        [JsonProperty("exportProject")] public bool ExportProject { get; set; }
        [JsonProperty("createProject")] public bool CreateProject { get; set; }
        [JsonProperty("annotations")] public bool Annotations { get; set; }
        [JsonProperty("saveVersion")] public bool SaveVersion { get; set; }
        [JsonProperty("activateVersion")] public bool ActivateVersion { get; set; }
        [JsonProperty("interact")] public bool Interact { get; set; }
        [JsonProperty("media")] public bool Media { get; set; }
        [JsonProperty("previewReason")] public BrowserRefusalCode PreviewReason { get; set; }
        [JsonProperty("saveReason")] public BrowserRefusalCode SaveReason { get; set; }
    }

    public sealed class BrowserTargetDescriptor
    {
        [JsonProperty("kind")] public BrowserTargetKind Kind { get; set; }
        [JsonProperty("state")] public BrowserTargetState State { get; set; }
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("documentId")] public string DocumentId { get; set; }
        [JsonProperty("resourceUri")] public string ResourceUri { get; set; }
        [JsonProperty("artifactId")] public string ArtifactId { get; set; }
        [JsonProperty("revision")] public int Revision { get; set; }
        [JsonProperty("ownerScope")] public BrowserOwnerScope OwnerScope { get; set; }
        [JsonProperty("workspaceId")] public string WorkspaceId { get; set; }
        [JsonProperty("pluginId")] public string PluginId { get; set; }
        [JsonProperty("releaseId")] public string ReleaseId { get; set; }
        [JsonProperty("savedVersionId")] public string SavedVersionId { get; set; }
        [JsonProperty("contentRevision")] public string ContentRevision { get; set; }
        [JsonProperty("entryPath")] public string EntryPath { get; set; }
        [JsonProperty("relativePath")] public string RelativePath { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("displayLocator")] public string DisplayLocator { get; set; }
        [JsonProperty("immutable")] public bool Immutable { get; set; }
        [JsonProperty("reason")] public BrowserRefusalCode Reason { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
        [JsonProperty("capabilities")] public BrowserTargetCapabilities Capabilities { get; set; }
    }
}
