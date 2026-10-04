using System;
using System.Collections.Generic;
using Newtonsoft.Json;

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
}
