using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    public sealed class TabularProjectException : IOException
    {
        public string Code { get; private set; }

        public TabularProjectException(string code, string message) : base(message)
        { Code = code; }
    }

    // Domain storage facet over the shared file owner. No direct file I/O, separate
    // CAS, registry, journal, workspace association or Office/session dependency.
    public sealed class TabularProjectStore
    {
        public const string ManifestFileName = "project.json";
        public const int MaximumManifestBytes = 256 * 1024;
        public const int MaximumSources = 500;
        private readonly WorkspaceFileService _files;

        public TabularProjectStore(WorkspaceFileService files)
        { _files = files ?? throw new ArgumentNullException(nameof(files)); }

        public TabularProjectSnapshot Create(WorkspaceDescriptor workspace, string manifestPath, string name)
        {
            if (workspace == null) throw new ArgumentNullException(nameof(workspace));
            var path = ManifestPath(manifestPath);
            var manifest = new TabularProjectManifest(TabularProjectManifest.ProjectKind,
                TabularProjectManifest.CurrentFormatVersion, "tp_" + Guid.NewGuid().ToString("N"), name,
                Array.Empty<TabularProjectSource>());
            var text = JsonConvert.SerializeObject(manifest, Formatting.Indented) + "\n";
            ParseManifest(text); // Validate before any file effect.
            // CreateText owns prepared/dispatch/read-back. Never retry or convert an
            // unknown effect to success by opening the possibly-created file.
            return Snapshot(workspace, _files.CreateText(workspace, path, text));
        }

        public TabularProjectSnapshot Open(WorkspaceDescriptor workspace, string manifestPath)
        {
            if (workspace == null) throw new ArgumentNullException(nameof(workspace));
            return Snapshot(workspace, _files.ReadText(workspace, ManifestPath(manifestPath)));
        }

        public TabularProjectSnapshot SaveSources(WorkspaceDescriptor workspace, TabularProjectSnapshot expected,
            IReadOnlyList<string> paths)
        {
            if (paths == null || paths.Count > MaximumSources) throw Invalid("Select at most 500 source paths.");
            var names = paths.ToArray();
            ValidateSourcePaths(names);
            var current = RequireUnchanged(workspace, expected);
            var previous = current.Manifest.Sources.ToDictionary(source => source.Path, StringComparer.OrdinalIgnoreCase);
            var sources = names.Select(path => new TabularProjectSource(previous.TryGetValue(path, out var source)
                ? source.SourceId : "src_" + Guid.NewGuid().ToString("N"), path)).ToArray();
            var manifest = new TabularProjectManifest(current.Manifest.Kind, current.Manifest.FormatVersion,
                current.Manifest.ProjectId, current.Manifest.Name, sources);
            var text = JsonConvert.SerializeObject(manifest, Formatting.Indented) + "\n";
            ParseManifest(text); // Check the complete bound before dispatch; no source file I/O here.
            return Snapshot(workspace, _files.ReplaceText(workspace, current.ManifestPath, current.ManifestReference, text));
        }

        public TabularProjectSnapshot RequireUnchanged(WorkspaceDescriptor workspace, TabularProjectSnapshot expected,
            CancellationToken token = default(CancellationToken))
        {
            if (workspace == null) throw new ArgumentNullException(nameof(workspace));
            if (expected == null) throw new ArgumentNullException(nameof(expected));
            if (!string.Equals(workspace.WorkspaceId, expected.WorkspaceId, StringComparison.Ordinal))
                throw new TabularProjectException("project_workspace_mismatch",
                    "The opened project belongs to another workspace. Open it explicitly in this workspace.");
            var observed = _files.ReadText(workspace, ManifestPath(expected.ManifestPath), token);
            if (observed.Reference.Uri != expected.ManifestReference.Uri ||
                observed.Reference.Revision != expected.ManifestReference.Revision ||
                observed.ContentSha256 != expected.ContentSha256)
                throw new TabularProjectException("project_changed",
                    "The project manifest changed. Reopen and inspect it before continuing.");
            return Snapshot(workspace, observed);
        }

        private static TabularProjectSnapshot Snapshot(WorkspaceDescriptor workspace, WorkspaceFileObservation observed)
        {
            return new TabularProjectSnapshot(workspace, observed.RelativePath, ParseManifest(observed.Text),
                observed.Reference, observed.ContentSha256);
        }

        private static string ManifestPath(string path)
        {
            var normalized = (path ?? string.Empty).Replace('\\', '/');
            if (normalized != ManifestFileName && !normalized.EndsWith("/" + ManifestFileName, StringComparison.Ordinal))
                throw Invalid("Select a workspace-relative project.json path.");
            // Containment, protected paths, case collisions and links belong to the file owner.
            return normalized;
        }

        private static TabularProjectManifest ParseManifest(string text)
        {
            if (text == null || Encoding.UTF8.GetByteCount(text) > MaximumManifestBytes)
                throw Invalid("The project manifest exceeds the 256 KiB limit.");
            try
            {
                JObject root;
                using (var input = new StringReader(text))
                using (var reader = new JsonTextReader(input) { MaxDepth = 4, DateParseHandling = DateParseHandling.None })
                {
                    root = JObject.Load(reader, new JsonLoadSettings {
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                    if (reader.Read()) throw Invalid("Unexpected content after the project manifest.");
                }
                var fields = new[] { "kind", "formatVersion", "projectId", "name", "sources" };
                if (root.Properties().Count() != fields.Length ||
                    root.Properties().Any(property => !fields.Contains(property.Name, StringComparer.Ordinal)) ||
                    fields.Any(field => root[field] == null || root[field].Type !=
                        (field == "formatVersion" ? JTokenType.Integer : field == "sources" ? JTokenType.Array : JTokenType.String)))
                    throw Invalid("Expected a version 2 manifest with kind, formatVersion, projectId, name and sources.");
                if ((string)root["kind"] != TabularProjectManifest.ProjectKind ||
                    root["formatVersion"].ToString(Formatting.None) != TabularProjectManifest.CurrentFormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    throw Invalid("Unsupported project kind or formatVersion.");
                var entries = (JArray)root["sources"];
                if (entries.Count > MaximumSources || entries.Any(entry => !(entry is JObject item) ||
                    item.Properties().Count() != 2 || item["sourceId"]?.Type != JTokenType.String || item["path"]?.Type != JTokenType.String))
                    throw Invalid("Each of at most 500 sources must contain only sourceId and path strings.");
                var manifest = root.ToObject<TabularProjectManifest>();
                var id = manifest.ProjectId;
                if (!IsId(id, "tp_"))
                    throw Invalid("projectId must be tp_ followed by 32 lowercase hexadecimal characters.");
                if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 200 || manifest.Name.Any(char.IsControl))
                    throw Invalid("Project name must contain 1–200 characters without control characters.");
                if (manifest.Sources.Any(source => !IsId(source.SourceId, "src_")) ||
                    manifest.Sources.Select(source => source.SourceId).Distinct(StringComparer.Ordinal).Count() != manifest.Sources.Count)
                    throw Invalid("Source identities must be distinct src_ identifiers with 32 lowercase hexadecimal characters.");
                ValidateSourcePaths(manifest.Sources.Select(source => source.Path).ToArray());
                return manifest;
            }
            catch (JsonException)
            { throw Invalid("Malformed project manifest JSON."); }
        }

        private static bool IsId(string value, string prefix)
        { return value != null && value.Length == prefix.Length + 32 && value.StartsWith(prefix, StringComparison.Ordinal) &&
            value.Skip(prefix.Length).All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f'); }

        private static void ValidateSourcePaths(string[] paths)
        {
            // Portable declarations only. Existence, permissions, containment and links
            // are revalidated by WorkspaceFileService when a source is actually prepared.
            if (paths.Any(path => string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Any(char.IsControl) ||
                path.IndexOfAny(new[] { '\\', ':' }) >= 0 || path.Split('/').Any(part => part.Length == 0 || part == "." || part == "..")) ||
                paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
                throw Invalid("Source paths must be distinct workspace-relative paths using forward slashes, without traversal.");
        }

        private static TabularProjectException Invalid(string message)
        { return new TabularProjectException("project_invalid", message); }
    }
}
