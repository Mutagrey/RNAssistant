using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace RNAssistant.Core.Models
{
    // Portable domain declarations. Workspace identity, grants, queue outcomes and resource heads never live here.
    public sealed class TabularProjectManifest
    {
        public const int CurrentFormatVersion = 2;
        public const string ProjectKind = "data-templates";

        [JsonProperty("kind", Required = Required.Always)]
        public string Kind { get; private set; }
        [JsonProperty("formatVersion", Required = Required.Always)]
        public int FormatVersion { get; private set; }
        [JsonProperty("projectId", Required = Required.Always)]
        public string ProjectId { get; private set; }
        [JsonProperty("name", Required = Required.Always)]
        public string Name { get; private set; }
        [JsonProperty("sources", Required = Required.Always)]
        public IReadOnlyList<TabularProjectSource> Sources { get; private set; }

        [JsonConstructor]
        public TabularProjectManifest(string kind, int formatVersion, string projectId, string name,
            IReadOnlyList<TabularProjectSource> sources)
        {
            Kind = kind;
            FormatVersion = formatVersion;
            ProjectId = projectId;
            Name = name;
            Sources = sources == null ? null : Array.AsReadOnly(sources.ToArray());
        }
    }

    public sealed class TabularProjectSource
    {
        [JsonProperty("sourceId", Required = Required.Always)] public string SourceId { get; private set; }
        [JsonProperty("path", Required = Required.Always)] public string Path { get; private set; }

        [JsonConstructor]
        public TabularProjectSource(string sourceId, string path) { SourceId = sourceId; Path = path; }
    }

    // An observed manifest, not a persistent project registry or permission token.
    // A later mutation must still use WorkspaceFileService's exact write guard.
    public sealed class TabularProjectSnapshot
    {
        public string WorkspaceId { get; private set; }
        public string ManifestPath { get; private set; }
        public TabularProjectManifest Manifest { get; private set; }
        public ResourceRef ManifestReference { get; private set; }
        public string ContentSha256 { get; private set; }
        public bool ReadOnly { get; private set; }

        internal TabularProjectSnapshot(WorkspaceDescriptor workspace, string manifestPath,
            TabularProjectManifest manifest, ResourceRef reference, string contentSha256)
        {
            WorkspaceId = workspace.WorkspaceId;
            ManifestPath = manifestPath;
            Manifest = manifest;
            ManifestReference = reference.Copy();
            ContentSha256 = contentSha256;
            ReadOnly = workspace.ReadOnly;
        }
    }

    // Project-local, immutable raw template snapshot. Input is populated only by
    // an explicit open; listing never reads draft bodies.
    public sealed class TabularProjectDraft
    {
        public string FileName { get; private set; }
        public long SavedAt { get; private set; }
        public string Label { get; private set; }
        public long ByteLength { get; private set; }
        public string Input { get; private set; }

        public TabularProjectDraft(string fileName, long savedAt, string label, long byteLength, string input = null)
        {
            FileName = fileName;
            SavedAt = savedAt;
            Label = label;
            ByteLength = byteLength;
            Input = input;
        }
    }

    // Project file heads are derived from immutable filenames, never mirrored in project.json.
    public sealed class TabularProjectTemplateHead
    {
        public string Id { get; private set; }
        public string HeadFile { get; private set; }
        public long SavedAt { get; private set; }
        public bool Archived { get; private set; }
        public int Versions { get; private set; }

        public TabularProjectTemplateHead(string id, string headFile, long savedAt, bool archived, int versions)
        { Id = id; HeadFile = headFile; SavedAt = savedAt; Archived = archived; Versions = versions; }
    }

    public sealed class TabularProjectStoredText
    {
        public string FileName { get; private set; }
        public string Input { get; private set; }
        public string Sha256 { get; private set; }
        public string Format { get; private set; }

        public TabularProjectStoredText(string fileName, string input, string sha256, string format)
        { FileName = fileName; Input = input; Sha256 = sha256; Format = format; }
    }
}
