using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    public sealed partial class TabularProjectStore
    {
        public const int MaximumCatalogFileBytes = 1000000;
        private const int MaximumCatalogEntries = 1000;
        private static readonly Regex TemplateFilePattern = new Regex(
            @"^template-([A-Za-z][A-Za-z0-9_.-]{0,127})-(\d{13})-([0-9a-f]{32})\.(json|removed)$",
            RegexOptions.CultureInvariant);
        private static readonly Regex DictionaryFilePattern = new Regex(
            @"^dictionary-(\d{13})-([0-9a-f]{32})\.json$", RegexOptions.CultureInvariant);

        private sealed class CatalogFile
        {
            public string Name;
            public string Id;
            public long SavedAt;
            public bool Archived;
        }

        public IReadOnlyList<TabularProjectTemplateHead> ListTemplateHeads(WorkspaceDescriptor workspace,
            TabularProjectSnapshot expected, CancellationToken token = default(CancellationToken))
        {
            var project = RequireUnchanged(workspace, expected, token);
            var events = ListCatalogFiles(workspace, ProjectDirectory(project.ManifestPath, "templates"),
                "template_catalog_limit", token).Select(entry => {
                    var match = entry.IsDirectory ? Match.Empty : TemplateFilePattern.Match(entry.Name);
                    return match.Success ? new CatalogFile { Name = entry.Name, Id = match.Groups[1].Value,
                        SavedAt = long.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                        Archived = match.Groups[4].Value == "removed" } : null;
                }).Where(item => item != null).ToArray();
            var heads = events.GroupBy(item => item.Id, StringComparer.Ordinal).Select(group => {
                var ordered = group.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
                var head = ordered[ordered.Length - 1];
                return new TabularProjectTemplateHead(group.Key, head.Name, head.SavedAt,
                    head.Archived, ordered.Count(item => !item.Archived));
            }).OrderByDescending(item => item.SavedAt).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
            RequireUnchanged(workspace, expected, token);
            return heads;
        }

        public TabularProjectStoredText ReadTemplate(WorkspaceDescriptor workspace, TabularProjectSnapshot expected,
            string id, string expectedHead, CancellationToken token = default(CancellationToken))
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(expectedHead) ||
                !TemplateFilePattern.IsMatch(expectedHead))
                throw new TabularProjectException("template_invalid", "Select a catalog template ID and observed head.");
            var project = RequireUnchanged(workspace, expected, token);
            var head = ListTemplateHeads(workspace, project, token).SingleOrDefault(item => item.Id == id);
            if (head == null || head.HeadFile != expectedHead)
                throw new TabularProjectException("template_changed", "The template head changed. Refresh the catalog.");
            var directory = ProjectDirectory(project.ManifestPath, "templates");
            var entries = ListCatalogFiles(workspace, directory, "template_catalog_limit", token)
                .Where(item => !item.IsDirectory && TemplateFilePattern.IsMatch(item.Name))
                .Select(item => new { item.Name, Match = TemplateFilePattern.Match(item.Name) })
                .Where(item => item.Match.Groups[1].Value == id && item.Match.Groups[4].Value == "json")
                .OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
            if (entries.Length == 0)
                throw new TabularProjectException("template_missing", "No retained template version exists for this ID.");
            var file = entries[entries.Length - 1].Name;
            var observed = ReadCatalogText(workspace, directory + "/" + file, "template", token);
            var root = ParseObject(observed.Text, "template_invalid");
            var kind = root["kind"]?.Type == JTokenType.String ? (string)root["kind"] : null;
            string format;
            if (kind == "data-templates-project-template" && ExactFields(root,
                "kind", "formatVersion", "projectId", "template", "source", "dataset") &&
                IsInteger(root["formatVersion"], 1) && root["projectId"]?.Type == JTokenType.String &&
                (string)root["projectId"] == project.Manifest.ProjectId &&
                root["source"]?.Type == JTokenType.Object && root["dataset"]?.Type == JTokenType.Object)
                format = "project";
            else if (kind == "xlsx-extraction" && ExactFields(root,
                "version", "kind", "template", "source", "dictionaries", "dataset") &&
                IsInteger(root["version"], 2) && root["dictionaries"]?.Type == JTokenType.Object &&
                root["source"]?.Type == JTokenType.Object && root["dataset"]?.Type == JTokenType.Object)
                format = "legacy";
            else throw new TabularProjectException("template_invalid", "The retained template envelope is invalid.");
            if (root["template"]?.Type != JTokenType.Object || root["template"]["id"]?.Type != JTokenType.String ||
                (string)root["template"]["id"] != id)
                throw new TabularProjectException("template_invalid", "Template ID does not match its catalog filename.");
            var latest = ListTemplateHeads(workspace, project, token).SingleOrDefault(item => item.Id == id);
            if (latest == null || latest.HeadFile != expectedHead)
                throw new TabularProjectException("template_changed", "The template head changed during reading.");
            return new TabularProjectStoredText(file, observed.Text, observed.ContentSha256, format);
        }

        public TabularProjectStoredText ReadDictionary(WorkspaceDescriptor workspace,
            TabularProjectSnapshot expected, CancellationToken token = default(CancellationToken))
        {
            var project = RequireUnchanged(workspace, expected, token);
            var directory = ProjectDirectory(project.ManifestPath, "dictionaries");
            var head = DictionaryHead(workspace, directory, token);
            if (head == null) { RequireUnchanged(workspace, project, token); return null; }
            var observed = ReadCatalogText(workspace, directory + "/" + head, "dictionary", token);
            var root = ParseObject(observed.Text, "dictionary_invalid");
            if (!ExactFields(root, "version", "revision", "parameters", "units", "parameterMappings", "unitMappings") ||
                !IsInteger(root["version"], 1) || root["revision"]?.Type != JTokenType.Integer ||
                !long.TryParse(root["revision"].ToString(Formatting.None),
                    System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
                out var revision) || revision < 1 || revision > 9007199254740991L ||
                new[] { "parameters", "units", "parameterMappings", "unitMappings" }
                    .Any(name => root[name]?.Type != JTokenType.Array))
                throw new TabularProjectException("dictionary_invalid", "The shared dictionary envelope is invalid.");
            if (DictionaryHead(workspace, directory, token) != head)
                throw new TabularProjectException("dictionary_changed", "The shared dictionary head changed during reading.");
            RequireUnchanged(workspace, project, token);
            return new TabularProjectStoredText(head, observed.Text, observed.ContentSha256, "dictionary");
        }

        private string DictionaryHead(WorkspaceDescriptor workspace, string directory, CancellationToken token)
        {
            return ListCatalogFiles(workspace, directory, "dictionary_catalog_limit", token)
                .Where(entry => !entry.IsDirectory && DictionaryFilePattern.IsMatch(entry.Name))
                .Select(entry => entry.Name).OrderBy(name => name, StringComparer.Ordinal).LastOrDefault();
        }

        private IReadOnlyList<WorkspaceDirectoryEntry> ListCatalogFiles(WorkspaceDescriptor workspace,
            string directory, string limitCode, CancellationToken token)
        {
            WorkspaceDirectoryPage first;
            try { first = _files.ListPage(workspace, directory, null, 500, 0, token); }
            catch (DirectoryNotFoundException) { return Array.Empty<WorkspaceDirectoryEntry>(); }
            if (first.ScannedEntries > MaximumCatalogEntries || first.ScanLimitReached)
                throw new TabularProjectException(limitCode, "The project catalog exceeds 1000 entries.");
            if (first.MatchedEntries <= 500) return first.Entries;
            var second = _files.ListPage(workspace, directory, null, 500, 500, token);
            if (second.ScannedEntries != first.ScannedEntries || second.MatchedEntries != first.MatchedEntries ||
                second.ScannedEntries > MaximumCatalogEntries || second.ScanLimitReached)
                throw new TabularProjectException("catalog_changed", "The project catalog changed during listing.");
            return first.Entries.Concat(second.Entries).ToArray();
        }

        private WorkspaceFileObservation ReadCatalogText(WorkspaceDescriptor workspace, string path,
            string kind, CancellationToken token)
        {
            WorkspaceFileObservation observed;
            try { observed = _files.ReadText(workspace, path, token); }
            catch (FileNotFoundException)
            { throw new TabularProjectException(kind + "_changed", "The selected project file disappeared. Refresh the catalog."); }
            var length = StrictUtf8.GetByteCount(observed.Text);
            if (length == 0 || length > MaximumCatalogFileBytes)
                throw new TabularProjectException(kind + "_limit", "The project file must contain 1 to 1000000 UTF-8 bytes.");
            return observed;
        }

        private static JObject ParseObject(string text, string code)
        {
            try
            {
                using (var input = new StringReader(text))
                using (var reader = new JsonTextReader(input) { MaxDepth = 64, DateParseHandling = DateParseHandling.None })
                {
                    var value = JObject.Load(reader, new JsonLoadSettings {
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                    if (reader.Read()) throw new JsonReaderException("Trailing JSON content.");
                    return value;
                }
            }
            catch (JsonException error)
            { throw new TabularProjectException(code, "The project JSON is malformed.", error); }
        }

        private static bool ExactFields(JObject value, params string[] fields)
        { return value.Properties().Count() == fields.Length && fields.All(field => value[field] != null); }

        private static bool IsInteger(JToken value, long expected)
        { return value?.Type == JTokenType.Integer && value.ToString(Formatting.None) ==
            expected.ToString(System.Globalization.CultureInfo.InvariantCulture); }

        private static string ProjectDirectory(string manifestPath, string name)
        { return manifestPath.Substring(0, manifestPath.LastIndexOf('/') + 1) + name; }
    }
}
