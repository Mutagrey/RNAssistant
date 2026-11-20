using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Markdig.Helpers;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    public static class MarkdownLinkGraphService
    {
        public const int MaximumDocuments = 160;
        public const int MaximumEdges = 4096;
        public const int MaximumUnresolvedNodes = 240;
        private static readonly StringComparer IgnoreCase = StringComparer.OrdinalIgnoreCase;
        private static readonly string[] WorkspaceExtensions = { ".md", ".markdown" };
        private static readonly string[] MarkdownExtensions = { ".md", ".markdown", ".mdx" };

        private sealed class Document
        {
            public MarkdownGraphNode Node;
            public IReadOnlyList<MarkdownSection> Sections;
        }

        public static MarkdownGraphSnapshot Build(IEnumerable<MarkdownGraphSource> sources, string focus,
            string scope, bool corpusComplete, string directory = null, CancellationToken token = default(CancellationToken))
        {
            var snapshot = new MarkdownGraphSnapshot { Scope = scope, Focus = focus ?? string.Empty,
                Nodes = new List<MarkdownGraphNode>(), Edges = new List<MarkdownGraphEdge>() };
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var byPath = new Dictionary<string, List<Document>>(IgnoreCase);
            var byName = new Dictionary<string, List<Document>>(IgnoreCase);
            var byTitle = new Dictionary<string, List<Document>>(IgnoreCase);
            var mentions = new List<KeyValuePair<Document, MarkdownLink>>();
            var linksComplete = true;
            foreach (var source in sources)
            {
                token.ThrowIfCancellationRequested();
                if (source == null || string.IsNullOrWhiteSpace(source.Id) || source.Id.StartsWith("unresolved:", StringComparison.Ordinal) ||
                    ids.Count == MaximumDocuments || !ids.Add(source.Id))
                    throw new InvalidOperationException("Graph sources must be unique and within the document bound.");

                var available = source.Analysis != null && string.IsNullOrEmpty(source.Issue);
                var item = new MarkdownGraphNode { Id = source.Id, Title = source.Title ?? source.Path ?? "Документ",
                    Path = source.Path, Revision = source.Revision, Kind = "document", Historical = source.Historical == true,
                    State = available ? "resolved" : "unavailable", Issue = source.Issue };
                var document = new Document { Node = item, Sections = available ? source.Analysis.Sections : null };
                snapshot.Nodes.Add(item);
                if (snapshot.Focus.Length == 0) snapshot.Focus = item.Id;
                if (source.Historical == false)
                {
                    var path = CleanPath(source.Path);
                    var name = path == null ? source.Title?.Trim() : Path.GetFileName(path);
                    Index(byPath, path, document);
                    Index(byName, name, document);
                    Index(byTitle, Stem(name), document);
                }
                if (!available)
                {
                    linksComplete = false;
                    continue;
                }
                foreach (var link in source.Analysis.Links)
                {
                    if (link.IsImage || string.IsNullOrWhiteSpace(link.Destination)) continue;
                    if (mentions.Count == MaximumEdges) { linksComplete = false; break; }
                    mentions.Add(new KeyValuePair<Document, MarkdownLink>(document, link));
                }
            }

            snapshot.Scanned = ids.Count;
            var unresolved = new Dictionary<Tuple<string, string, string>, MarkdownGraphNode>();
            foreach (var mention in mentions)
            {
                token.ThrowIfCancellationRequested();
                var source = mention.Key;
                var link = mention.Value;
                string anchor, state;
                var target = Resolve(source, link, byPath, byName, byTitle, directory, corpusComplete, out anchor, out state);
                string destination = Display(link.Destination);
                string to;
                if (target != null)
                {
                    to = target.Node.Id;
                    if (target.Sections == null) state = "pending";
                    else if (!string.IsNullOrEmpty(anchor))
                        ResolveSection(target.Sections, link.Kind == "wiki", ref anchor, ref state);
                }
                else
                {
                    var key = Tuple.Create(source.Node.Id, link.Destination, state);
                    MarkdownGraphNode placeholder;
                    if (!unresolved.TryGetValue(key, out placeholder))
                    {
                        if (unresolved.Count == MaximumUnresolvedNodes) { linksComplete = false; continue; }
                        placeholder = new MarkdownGraphNode { Id = "unresolved:" + unresolved.Count,
                            Title = destination, Kind = "reference", State = state };
                        unresolved.Add(key, placeholder);
                        snapshot.Nodes.Add(placeholder);
                    }
                    to = placeholder.Id;
                }
                snapshot.Edges.Add(new MarkdownGraphEdge { From = source.Node.Id, To = to, Kind = "reference",
                    Status = state, Destination = destination, Label = Display(link.Text), Anchor = Display(anchor),
                    Line = link.Line, Start = link.Start });
            }

            snapshot.Complete = corpusComplete && linksComplete && (snapshot.Focus.Length == 0 || ids.Contains(snapshot.Focus));
            if (!snapshot.Complete)
            {
                foreach (var item in unresolved.Values.Where(item => item.State == "missing")) item.State = "pending";
                foreach (var edge in snapshot.Edges.Where(edge => edge.Status == "missing")) edge.Status = "pending";
                snapshot.Notice = "Проверена часть области. Список связей может быть неполным.";
            }
            return snapshot;
        }

        private static Document Resolve(Document source, MarkdownLink link,
            Dictionary<string, List<Document>> byPath, Dictionary<string, List<Document>> byName,
            Dictionary<string, List<Document>> byTitle, string directory, bool corpusComplete, out string anchor, out string status)
        {
            var target = link.Destination.Trim();
            anchor = null;
            status = "missing";
            var hash = target.IndexOf('#');
            if (hash >= 0)
            {
                anchor = Uri.UnescapeDataString(target.Substring(hash + 1));
                target = target.Substring(0, hash);
            }
            if (target.Length == 0) { status = "resolved"; return source; }
            if (target.IndexOf('?') >= 0 || Uri.TryCreate(target, UriKind.Absolute, out var uri) && !uri.IsFile)
            { status = "external"; return null; }

            target = Uri.UnescapeDataString(target).Replace('\\', '/');
            if (target.StartsWith("/", StringComparison.Ordinal) || target.IndexOf(':') >= 0 || target.Any(char.IsControl))
            { status = "outside"; return null; }
            var wiki = link.Kind == "wiki";
            List<Document> matches;
            if (source.Node.Path != null)
            {
                var path = source.Node.Path;
                var candidate = CleanPath(path.Substring(0, path.LastIndexOf('/') + 1) + target);
                if (candidate == null || !string.IsNullOrEmpty(directory) &&
                    !candidate.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase))
                { status = "outside"; return null; }
                if (byPath.TryGetValue(candidate, out matches))
                    return Select(matches, true, out status);
                if (candidate.LastIndexOf('.') <= candidate.LastIndexOf('/'))
                {
                    matches = new List<Document>();
                    foreach (var extension in WorkspaceExtensions)
                        if (byPath.TryGetValue(candidate + extension, out var paths)) matches.AddRange(paths);
                    if (matches.Count > 0) return Select(matches, true, out status);
                }
                if (!wiki || target.Contains("/") || target.StartsWith(".", StringComparison.Ordinal)) return null;
            }
            else
            {
                while (target.StartsWith("./", StringComparison.Ordinal)) target = target.Substring(2);
                if (CleanPath(target) == null) { status = "outside"; return null; }
                // Document materials have names and exact revision identities, without a filesystem directory.
                if (target.Contains("/")) return null;
                if (byName.TryGetValue(target, out matches)) return Select(matches, corpusComplete, out status);
                if (!wiki && Stem(target) != target)
                {
                    if (byName.TryGetValue(Stem(target), out matches)) return Select(matches, corpusComplete, out status);
                    return null;
                }
            }
            return byTitle.TryGetValue(Stem(target), out matches) ? Select(matches, corpusComplete, out status) : null;
        }

        private static void ResolveSection(IReadOnlyList<MarkdownSection> sections, bool wiki,
            ref string anchor, ref string status)
        {
            var fragment = anchor;
            if (sections.Any(section => section.Anchor == fragment)) return;
            if (wiki)
            {
                var matches = sections.Where(section => IgnoreCase.Equals(section.Title, fragment)).Take(2).ToArray();
                if (matches.Length > 1) { status = "ambiguous"; return; }
                if (matches.Length == 1) { anchor = matches[0].Anchor; return; }
                var slug = LinkHelper.UrilizeAsGfm(fragment);
                if (sections.Any(section => section.Anchor == slug)) { anchor = slug; return; }
            }
            status = "missing-section";
        }

        private static Document Select(List<Document> matches, bool complete, out string status)
        {
            if (matches.Count > 1) { status = "ambiguous"; return null; }
            status = complete ? "resolved" : "pending";
            return complete ? matches[0] : null;
        }

        private static void Index(Dictionary<string, List<Document>> index, string key, Document document)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!index.TryGetValue(key, out var documents)) index[key] = documents = new List<Document>();
            documents.Add(document);
        }

        private static string Stem(string value)
        {
            foreach (var extension in MarkdownExtensions)
                if (value != null && value.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    return value.Substring(0, value.Length - extension.Length).TrimEnd();
            return value;
        }

        private static string CleanPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var parts = new List<string>();
            foreach (var part in path.Split('/'))
            {
                if (part.Length == 0 || part == ".") continue;
                if (part == "..") { if (parts.Count == 0) return null; parts.RemoveAt(parts.Count - 1); }
                else parts.Add(part);
            }
            return parts.Count == 0 ? null : string.Join("/", parts);
        }

        private static string Display(string value)
        {
            const int max = 512;
            if (value == null || value.Length <= max) return value;
            return value.Substring(0, char.IsHighSurrogate(value[max - 2]) ? max - 2 : max - 1) + "…";
        }
    }
}
