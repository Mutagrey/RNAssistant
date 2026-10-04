using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace RNAssistant.Core.Models
{
    // Project configuration is portable. It contains no grants or current resource heads.
    public sealed class WorkspaceManifest
    {
        [JsonProperty("schemaVersion", Required = Required.Always)]
        public int SchemaVersion { get; set; }
        [JsonProperty("workspaceId", Required = Required.Always)]
        public string WorkspaceId { get; set; }
        [JsonProperty("name", Required = Required.Always)]
        public string Name { get; set; }
        [JsonProperty("root", Required = Required.Always)]
        public string Root { get; set; }
    }

    public sealed class WorkspaceDescriptor
    {
        public string WorkspaceId { get; internal set; }
        public string Name { get; internal set; }
        public string RootPath { get; internal set; }
        public bool ReadOnly { get; internal set; }
        public IReadOnlyList<WorkspaceMountDescriptor> Mounts { get; internal set; }
    }

    public sealed class WorkspaceMountDescriptor
    {
        public string Id { get; internal set; }
        public string Kind { get; internal set; }
        public string RootPath { get; internal set; }
        public bool ReadOnly { get; internal set; }
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum WorkspaceAcceptanceState
    {
        NotRequested,
        Pending,
        Passed,
        Failed,
        Unknown
    }

    // The accepted CLI task contract is part of the run record, not a model claim.
    // Assessment is separate from the kernel's lifecycle and tool effect evidence.
    public sealed class WorkspaceRunAcceptance
    {
        public List<string> ExpectedFiles { get; set; } = new List<string>();
        public int MinimumVerifiedReads { get; set; }
        public int MinimumVerifiedWrites { get; set; }
        public int AcceptedCompleteFileReads { get; set; }
        public int VerifiedFileChanges { get; set; }
        public WorkspaceAcceptanceState State { get; set; }
        public List<string> MissingFiles { get; set; } = new List<string>();
        public string Error { get; set; }

        [JsonIgnore]
        public bool Requested { get { return ExpectedFiles.Count > 0 || MinimumVerifiedReads > 0 || MinimumVerifiedWrites > 0; } }
    }
}
