using System.Collections.Generic;
using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Runtime.Contracts
{
    public sealed class WorkspaceFindResourcesRequest
    {
        public string Directory { get; set; }
        public string Query { get; set; }
        public int Limit { get; set; } = 50;
    }

    public sealed class WorkspaceResourcePage
    {
        public string Directory { get; set; }
        public string Query { get; set; }
        public IReadOnlyList<ResourceDescriptor> Items { get; set; }
        public bool Complete { get; set; }
        public int? ScannedEntries { get; set; }
        public string AvailabilityHint { get; set; }
        public IReadOnlyList<string> UnavailableScopes { get; set; }
    }

    public sealed class WorkspaceOpenResourceRequest
    {
        public string Target { get; set; }
        public string Representation { get; set; } = ResourceRepresentations.Text;
        // Exact references are application transport metadata, never model arguments.
        public ResourceRef Exact { get; set; }
        public string SnapshotId { get; set; }
        public int Offset { get; set; }
        public int MaxCharacters { get; set; } = 4096;
    }

    public sealed class WorkspaceResourceView
    {
        public string Target { get; set; }
        public ResourceDescriptor Descriptor { get; set; }
        public ResourceReadProjection Content { get; set; }
        public int? NextOffset { get; set; }
        public bool Retained { get; set; }
    }

    public sealed class WorkspacePreviewRequest
    {
        public string EntryPath { get; set; }
        public string SnapshotId { get; set; }
        public int LifetimeSeconds { get; set; } = 300;
        public bool Headless { get; set; }
    }

    [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public enum WorkspacePreviewStatus { Opened, Closed, NotAvailable, Failed }

    [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public enum WorkspacePreviewCloseReason { LifetimeExpired, Cancelled }

    public sealed class WorkspacePreviewReport
    {
        public WorkspacePreviewStatus Status { get; set; }
        public WorkspacePreviewCloseReason? CloseReason { get; set; }
        public string EntryPath { get; set; }
        public string SnapshotId { get; set; }
        public string SnapshotSha256 { get; set; }
        public string SourceMode { get; set; } = "snapshot";
        public bool Historical { get; set; }
        public string Browser { get; set; }
        public string Url { get; set; }
        public bool Headless { get; set; }
        public IReadOnlyList<string> Files { get; set; }
        public IReadOnlyList<string> Diagnostics { get; set; }
    }
}
