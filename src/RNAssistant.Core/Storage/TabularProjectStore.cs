using System;
using System.Collections.Generic;
using System.Globalization;
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

        public TabularProjectException(string code, string message, Exception inner = null) : base(message, inner)
        { Code = code; }
    }

    // Domain storage facet over the shared file owner. No direct file I/O, separate
    // CAS, registry, journal, workspace association or Office/session dependency.
    public sealed class TabularProjectStore
    {
        public const string ManifestFileName = "project.json";
        public const int MaximumManifestBytes = 256 * 1024;
        public const int MaximumSources = 500;
        public const int MaximumDraftBytes = 1000000;
        private const int MaximumDraftDirectoryEntries = 1000;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
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

        public static string NewDraftFileName(string name)
        {
            return "draft-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) +
                "-" + Guid.NewGuid().ToString("N") + "-" + DraftLabel(name) + ".draft";
        }

        public TabularProjectDraft SaveDraft(WorkspaceDescriptor workspace, TabularProjectSnapshot expected,
            string fileName, string input)
        {
            var draft = ParseDraftName(fileName);
            var length = DraftLength(input);
            var current = RequireUnchanged(workspace, expected);
            // The shared file owner guards create-only dispatch and verifies the bytes.
            var path = DraftDirectory(current.ManifestPath) + "/" + fileName;
            try { _files.CreateText(workspace, path, input); }
            catch (WorkspaceFileException ex) when (ex.Code == "effect_unknown")
            {
                throw new TabularProjectException("effect_unknown",
                    "Draft write result is uncertain at " + path + ". Inspect that exact file before retrying.", ex);
            }
            return new TabularProjectDraft(fileName, draft.SavedAt, draft.Label, length);
        }

        public IReadOnlyList<TabularProjectDraft> ListDrafts(WorkspaceDescriptor workspace,
            TabularProjectSnapshot expected, CancellationToken token = default(CancellationToken))
        {
            var current = RequireUnchanged(workspace, expected, token);
            var directory = DraftDirectory(current.ManifestPath);
            WorkspaceDirectoryPage first;
            try { first = _files.ListPage(workspace, directory, null, 500, 0, token); }
            catch (DirectoryNotFoundException) { return Array.Empty<TabularProjectDraft>(); }
            if (first.ScannedEntries > MaximumDraftDirectoryEntries || first.ScanLimitReached)
                throw new TabularProjectException("draft_list_limit", "The drafts directory exceeds 1000 entries.");
            var items = new List<TabularProjectDraft>();
            AddDraftEntries(first, items);
            for (var offset = 500; offset < first.MatchedEntries; offset += 500)
            {
                var page = _files.ListPage(workspace, directory, null, 500, offset, token);
                if (page.ScannedEntries > MaximumDraftDirectoryEntries || page.ScanLimitReached)
                    throw new TabularProjectException("draft_list_limit", "The drafts directory exceeds 1000 entries.");
                AddDraftEntries(page, items);
            }
            return items.OrderByDescending(item => item.FileName, StringComparer.Ordinal).Take(50).ToArray();
        }

        public TabularProjectDraft ReadDraft(WorkspaceDescriptor workspace, TabularProjectSnapshot expected,
            string fileName, CancellationToken token = default(CancellationToken))
        {
            var draft = ParseDraftName(fileName);
            var current = RequireUnchanged(workspace, expected, token);
            WorkspaceFileObservation observed;
            try { observed = _files.ReadText(workspace, DraftDirectory(current.ManifestPath) + "/" + fileName, token); }
            catch (FileNotFoundException)
            { throw new TabularProjectException("draft_missing", "The selected draft snapshot is missing."); }
            catch (DirectoryNotFoundException)
            { throw new TabularProjectException("draft_missing", "The selected draft snapshot is missing."); }
            var length = DraftLength(observed.Text);
            return new TabularProjectDraft(fileName, draft.SavedAt, draft.Label, length, observed.Text);
        }

        private static void AddDraftEntries(WorkspaceDirectoryPage page, List<TabularProjectDraft> drafts)
        {
            foreach (var entry in page.Entries)
            {
                if (entry.IsDirectory || !TryParseDraftName(entry.Name, out var savedAt, out var label)) continue;
                drafts.Add(new TabularProjectDraft(entry.Name, savedAt, label, entry.ByteLength.Value));
            }
        }

        private static string DraftDirectory(string manifestPath)
        {
            var slash = manifestPath.LastIndexOf('/');
            return slash < 0 ? "drafts" : manifestPath.Substring(0, slash + 1) + "drafts";
        }

        private static int DraftLength(string input)
        {
            if (string.IsNullOrEmpty(input))
                throw new TabularProjectException("draft_limit", "A draft must contain 1 to 1000000 UTF-8 bytes.");
            if (input[0] == '\uFEFF')
                throw new TabularProjectException("draft_encoding", "A draft cannot start with a UTF-8 BOM.");
            int length;
            try { length = StrictUtf8.GetByteCount(input); }
            catch (EncoderFallbackException)
            { throw new TabularProjectException("draft_encoding", "A draft must contain valid Unicode text."); }
            if (length > MaximumDraftBytes)
                throw new TabularProjectException("draft_limit", "A draft must contain 1 to 1000000 UTF-8 bytes.");
            return length;
        }

        private static string DraftLabel(string name)
        {
            var source = string.IsNullOrEmpty(name) ? "template" : name;
            if (source.Length > 200)
                throw new TabularProjectException("draft_invalid", "A draft name must be at most 200 characters.");
            if (source.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                source = source.Substring(0, source.Length - 5);
            var label = new StringBuilder();
            var replaced = false;
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                if (IsDraftLabelCharacter(source, i))
                {
                    label.Append(c);
                    if (char.IsHighSurrogate(c)) label.Append(source[++i]);
                    replaced = false;
                }
                else if (!replaced) { label.Append('_'); replaced = true; }
            }
            var value = label.ToString().Trim('_', '-', '.');
            if (value.Length == 0) return "template";
            var end = 0;
            for (var count = 0; end < value.Length && count < 48; count++)
                end += char.IsHighSurrogate(value[end]) ? 2 : 1;
            return value.Substring(0, end);
        }

        private static TabularProjectDraft ParseDraftName(string fileName)
        {
            if (!TryParseDraftName(fileName, out var savedAt, out var label))
                throw new TabularProjectException("draft_invalid", "Select a project draft snapshot filename.");
            return new TabularProjectDraft(fileName, savedAt, label, 0);
        }

        private static bool TryParseDraftName(string fileName, out long savedAt, out string label)
        {
            savedAt = 0; label = null;
            if (fileName == null || fileName.Length < 60 || fileName.Length > 155 ||
                !fileName.StartsWith("draft-", StringComparison.Ordinal) ||
                !fileName.EndsWith(".draft", StringComparison.Ordinal) ||
                fileName[19] != '-' || fileName[52] != '-' ||
                !long.TryParse(fileName.Substring(6, 13), NumberStyles.None, CultureInfo.InvariantCulture, out savedAt) ||
                fileName.Skip(20).Take(32).Any(c => !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')))
                return false;
            label = fileName.Substring(53, fileName.Length - 59);
            var count = 0;
            for (var i = 0; i < label.Length; i++)
            {
                if (!IsDraftLabelCharacter(label, i)) return false;
                if (char.IsHighSurrogate(label[i])) i++;
                if (++count > 48) return false;
            }
            return true;
        }

        private static bool IsDraftLabelCharacter(string value, int index)
        {
            var c = value[index];
            if (c == '.' || c == '_' || c == '-') return true;
            if (char.IsHighSurrogate(c) && (index + 1 == value.Length || !char.IsLowSurrogate(value[index + 1])))
                return false;
            var category = CharUnicodeInfo.GetUnicodeCategory(value, index);
            return category == UnicodeCategory.UppercaseLetter || category == UnicodeCategory.LowercaseLetter ||
                category == UnicodeCategory.TitlecaseLetter || category == UnicodeCategory.ModifierLetter ||
                category == UnicodeCategory.OtherLetter || category == UnicodeCategory.DecimalDigitNumber ||
                category == UnicodeCategory.LetterNumber || category == UnicodeCategory.OtherNumber;
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
