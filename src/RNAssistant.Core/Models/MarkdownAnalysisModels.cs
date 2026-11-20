using System.Collections.Generic;
using Newtonsoft.Json;

namespace RNAssistant.Core.Models
{
    // These are derived observations of the caller's exact text snapshot, not
    // resource identities or read/mutation authority. Ranges are UTF-16 [start,end).
    public sealed class MarkdownDocumentAnalysis
    {
        [JsonProperty("profile")] public string Profile { get; private set; }
        [JsonProperty("characterCount")] public int CharacterCount { get; private set; }
        [JsonProperty("html", NullValueHandling = NullValueHandling.Ignore)] public string Html { get; private set; }
        [JsonProperty("sections")] public IReadOnlyList<MarkdownSection> Sections { get; private set; }
        [JsonProperty("links")] public IReadOnlyList<MarkdownLink> Links { get; private set; }

        internal MarkdownDocumentAnalysis(string profile, int characterCount, string html,
            IReadOnlyList<MarkdownSection> sections, IReadOnlyList<MarkdownLink> links)
        {
            Profile = profile; CharacterCount = characterCount; Html = html;
            Sections = sections; Links = links;
        }
    }

    public sealed class MarkdownSection
    {
        [JsonProperty("id")] public string Id { get; private set; }
        [JsonProperty("parentId", NullValueHandling = NullValueHandling.Ignore)] public string ParentId { get; private set; }
        [JsonProperty("title")] public string Title { get; private set; }
        [JsonProperty("level")] public int Level { get; private set; }
        [JsonProperty("anchor")] public string Anchor { get; private set; }
        [JsonProperty("start")] public int Start { get; private set; }
        [JsonProperty("end")] public int End { get; private set; }
        [JsonProperty("headingEnd")] public int HeadingEnd { get; private set; }
        [JsonProperty("line")] public int Line { get; private set; }

        internal MarkdownSection(string id, string parentId, string title, int level,
            string anchor, int start, int end, int headingEnd, int line)
        {
            Id = id; ParentId = parentId; Title = title; Level = level;
            Anchor = anchor; Start = start; End = end; HeadingEnd = headingEnd; Line = line;
        }
    }

    public sealed class MarkdownLink
    {
        [JsonProperty("destination")] public string Destination { get; private set; }
        [JsonProperty("text")] public string Text { get; private set; }
        [JsonProperty("start")] public int Start { get; private set; }
        [JsonProperty("end")] public int End { get; private set; }
        [JsonProperty("line")] public int Line { get; private set; }
        [JsonProperty("isImage")] public bool IsImage { get; private set; }
        [JsonProperty("kind")] public string Kind { get; private set; }

        internal MarkdownLink(string destination, string text, int start, int end, int line, bool isImage, string kind = "markdown")
        {
            Destination = destination; Text = text; Start = start; End = end; Line = line; IsImage = isImage;
            Kind = kind;
        }
    }

    // Derived data only: exact source identity comes from an existing resource owner.
    // No storage, filesystem traversal, model inference or implicit cross-scope links.
    public sealed class MarkdownGraphSource
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Path { get; set; }
        public string Revision { get; set; }
        public bool? Historical { get; set; } = false;
        public MarkdownDocumentAnalysis Analysis { get; set; }
        public string Issue { get; set; }
    }

    public sealed class MarkdownGraphNode
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("path", NullValueHandling = NullValueHandling.Ignore)] public string Path { get; set; }
        [JsonProperty("issue", NullValueHandling = NullValueHandling.Ignore)] public string Issue { get; set; }
        [JsonProperty("historical", DefaultValueHandling = DefaultValueHandling.Ignore)] public bool Historical { get; set; }
        [JsonProperty("revision", NullValueHandling = NullValueHandling.Ignore)] public string Revision { get; set; }
        [JsonProperty("state")] public string State { get; set; }
        [JsonProperty("kind")] public string Kind { get; set; }
    }

    public sealed class MarkdownGraphEdge
    {
        [JsonProperty("from")] public string From { get; set; }
        [JsonProperty("to")] public string To { get; set; }
        [JsonProperty("kind")] public string Kind { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("destination")] public string Destination { get; set; }
        [JsonProperty("label")] public string Label { get; set; }
        [JsonProperty("anchor", NullValueHandling = NullValueHandling.Ignore)] public string Anchor { get; set; }
        [JsonProperty("line")] public int Line { get; set; }
        [JsonProperty("start")] public int Start { get; set; }
    }

    public sealed class MarkdownGraphSnapshot
    {
        [JsonProperty("scope")] public string Scope { get; set; }
        [JsonProperty("focus")] public string Focus { get; set; }
        [JsonProperty("complete")] public bool Complete { get; set; }
        [JsonProperty("scanned")] public int Scanned { get; set; }
        [JsonProperty("notice")] public string Notice { get; set; }
        [JsonProperty("nodes")] public List<MarkdownGraphNode> Nodes { get; set; }
        [JsonProperty("edges")] public List<MarkdownGraphEdge> Edges { get; set; }
    }

}
