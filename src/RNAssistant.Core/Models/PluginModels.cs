using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace RNAssistant.Core.Models
{
    // Portable declarations. Neither a manifest nor a content digest grants authority.
    public sealed class PluginManifestV1
    {
        [JsonProperty("formatVersion", Required = Required.Always)] public int FormatVersion { get; set; }
        [JsonProperty("id", Required = Required.Always)] public string Id { get; set; }
        [JsonProperty("name", Required = Required.Always)] public string Name { get; set; }
        [JsonProperty("version", Required = Required.Always)] public string Version { get; set; }
        [JsonProperty("hostProtocol", Required = Required.Always)] public int HostProtocol { get; set; }
        [JsonProperty("entry", Required = Required.Always)] public string Entry { get; set; }
        [JsonProperty("icon")] public string Icon { get; set; }
        [JsonProperty("files", Required = Required.Always)] public string[] Files { get; set; }
        [JsonProperty("capabilities")] public string[] Capabilities { get; set; } = new string[0];
        [JsonProperty("commands")] public PluginCommandDefinition[] Commands { get; set; } = new PluginCommandDefinition[0];
        [JsonProperty("vendors")] public PluginVendorDependency[] Vendors { get; set; } = new PluginVendorDependency[0];
    }

    public sealed class PluginCommandDefinition
    {
        [JsonProperty("id", Required = Required.Always)] public string Id { get; set; }
        [JsonProperty("description", Required = Required.Always)] public string Description { get; set; }
        [JsonProperty("entry", Required = Required.Always)] public string Entry { get; set; }
        // Domain JSON is validated by the existing schema owner; transport stays typed.
        [JsonProperty("argumentsSchema", Required = Required.Always)] public Newtonsoft.Json.Linq.JObject ArgumentsSchema { get; set; }
        [JsonProperty("resultSchema", Required = Required.Always)] public Newtonsoft.Json.Linq.JObject ResultSchema { get; set; }
        [JsonProperty("writesData")] public bool WritesData { get; set; }
    }

    public sealed class PluginVendorDependency
    {
        [JsonProperty("id", Required = Required.Always)] public string Id { get; set; }
        [JsonProperty("version", Required = Required.Always)] public string Version { get; set; }
        [JsonProperty("mount", Required = Required.Always)] public string Mount { get; set; }
    }

    public sealed class VendorManifestV1
    {
        [JsonProperty("formatVersion", Required = Required.Always)] public int FormatVersion { get; set; }
        [JsonProperty("id", Required = Required.Always)] public string Id { get; set; }
        [JsonProperty("version", Required = Required.Always)] public string Version { get; set; }
        [JsonProperty("description", Required = Required.Always)] public string Description { get; set; }
        [JsonProperty("source", Required = Required.Always)] public string Source { get; set; }
        [JsonProperty("license", Required = Required.Always)] public string License { get; set; }
        [JsonProperty("licenses", Required = Required.Always)] public string[] Licenses { get; set; }
        [JsonProperty("files", Required = Required.Always)] public VendorFileDefinition[] Files { get; set; }
        [JsonProperty("entrypoints", Required = Required.Always)] public VendorEntryPoint[] EntryPoints { get; set; }
        [JsonProperty("dependencies")] public VendorDependency[] Dependencies { get; set; } = new VendorDependency[0];
    }

    public sealed class VendorDependency
    {
        [JsonProperty("id", Required = Required.Always)] public string Id { get; set; }
        [JsonProperty("version", Required = Required.Always)] public string Version { get; set; }
    }

    public sealed class VendorFileDefinition
    {
        [JsonProperty("path", Required = Required.Always)] public string Path { get; set; }
        [JsonProperty("sha256", Required = Required.Always)] public string Sha256 { get; set; }
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum VendorEntryKind { Script, Style, Module, Worker, Asset }

    public sealed class VendorEntryPoint
    {
        [JsonProperty("path", Required = Required.Always)] public string Path { get; set; }
        [JsonProperty("kind", Required = Required.Always)] public VendorEntryKind Kind { get; set; }
    }

    // Distinct role objects prevent passing a code workspace as a data grant or release.
    public sealed class PluginSourceWorkspace
    {
        public WorkspaceDescriptor Workspace { get; private set; }
        public string SourceWorkspaceId { get { return Workspace.WorkspaceId; } }
        public PluginSourceWorkspace(WorkspaceDescriptor workspace)
        { Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace)); }
    }

    public sealed class PluginReleaseIdentity
    {
        public string PluginId { get; private set; }
        public string ReleaseId { get; private set; }
        public PluginReleaseIdentity(string pluginId, string releaseId)
        {
            if (string.IsNullOrEmpty(pluginId) || releaseId == null || !releaseId.StartsWith("rel_", StringComparison.Ordinal))
                throw new ArgumentException("A published plugin release identity is required.");
            PluginId = pluginId; ReleaseId = releaseId;
        }
    }

    public sealed class PluginPackageFile
    {
        public string Path { get; set; }
        public PayloadRef Payload { get; set; }
    }

    public sealed class PluginReleaseRecord
    {
        public int FormatVersion { get; set; } = 1;
        public string PluginId { get; set; }
        public string ReleaseId { get; set; }
        public string SourceWorkspaceId { get; set; }
        public string ManifestJson { get; set; }
        public List<PluginPackageFile> Files { get; set; }
        public string PackageSha256 { get; set; }
    }

    public sealed class PluginRegistration
    {
        public string PluginId { get; set; }
        public string Name { get; set; }
        public string Icon { get; set; }
        public string SourceWorkspaceId { get; set; }
        public string SourceRoot { get; set; }
        public string ActiveReleaseId { get; set; }
        public bool Enabled { get; set; }
    }
}
