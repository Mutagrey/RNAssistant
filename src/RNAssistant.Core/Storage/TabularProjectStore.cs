using System;
using System.IO;
using System.Linq;
using System.Text;
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
        public const int MaximumManifestBytes = 16 * 1024;
        private readonly WorkspaceFileService _files;

        public TabularProjectStore(WorkspaceFileService files)
        { _files = files ?? throw new ArgumentNullException(nameof(files)); }

        public TabularProjectSnapshot Create(WorkspaceDescriptor workspace, string manifestPath, string name)
        {
            if (workspace == null) throw new ArgumentNullException(nameof(workspace));
            var path = ManifestPath(manifestPath);
            var manifest = new TabularProjectManifest(TabularProjectManifest.ProjectKind,
                TabularProjectManifest.CurrentFormatVersion, "tp_" + Guid.NewGuid().ToString("N"), name);
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

        public TabularProjectSnapshot RequireUnchanged(WorkspaceDescriptor workspace, TabularProjectSnapshot expected)
        {
            if (workspace == null) throw new ArgumentNullException(nameof(workspace));
            if (expected == null) throw new ArgumentNullException(nameof(expected));
            if (!string.Equals(workspace.WorkspaceId, expected.WorkspaceId, StringComparison.Ordinal))
                throw new TabularProjectException("project_workspace_mismatch",
                    "The opened project belongs to another workspace. Open it explicitly in this workspace.");
            var observed = _files.ReadText(workspace, ManifestPath(expected.ManifestPath));
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
                throw Invalid("The project manifest exceeds the 16 KiB limit.");
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
                var fields = new[] { "kind", "formatVersion", "projectId", "name" };
                if (root.Properties().Count() != fields.Length ||
                    root.Properties().Any(property => !fields.Contains(property.Name, StringComparer.Ordinal)) ||
                    fields.Any(field => root[field] == null || root[field].Type !=
                        (field == "formatVersion" ? JTokenType.Integer : JTokenType.String)))
                    throw Invalid("The project manifest must contain only kind, formatVersion, projectId and name with their declared types.");
                if ((string)root["kind"] != TabularProjectManifest.ProjectKind ||
                    root["formatVersion"].ToString(Formatting.None) != TabularProjectManifest.CurrentFormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    throw Invalid("Unsupported project kind or formatVersion.");
                var manifest = root.ToObject<TabularProjectManifest>();
                var id = manifest.ProjectId;
                if (id.Length != 35 || !id.StartsWith("tp_", StringComparison.Ordinal) ||
                    id.Skip(3).Any(c => !(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')))
                    throw Invalid("projectId must be tp_ followed by 32 lowercase hexadecimal characters.");
                if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 200 || manifest.Name.Any(char.IsControl))
                    throw Invalid("Project name must contain 1–200 characters without control characters.");
                return manifest;
            }
            catch (JsonException)
            { throw Invalid("Malformed project manifest JSON."); }
        }

        private static TabularProjectException Invalid(string message)
        { return new TabularProjectException("project_invalid", message); }
    }
}
