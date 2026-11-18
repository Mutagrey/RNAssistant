using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Markdig;
using Markdig.Helpers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    public sealed class MarkdownAnalysisException : InvalidOperationException
    {
        public string Code { get; private set; }
        public MarkdownAnalysisException(string message, string code) : base(message) { Code = code; }
    }

    // One pipeline and AST define document HTML, section reads and explicit links.
    // There is no I/O, resource resolution, cache, or user-editable state here.
    public static class MarkdownAnalysisService
    {
        public const string Profile = "markdown-markdig-0.37.0-v1";
        public const int MaximumCharacters = 1024 * 1024;
        public const int MaximumSections = 4096;
        public const int MaximumLinks = 16384;

        private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
            .UsePipeTables()
            .UseEmphasisExtras()
            .UseTaskLists()
            .UseAutoLinks()
            .UsePreciseSourceLocation()
            .DisableHtml()
            .Build();

        public static MarkdownDocumentAnalysis Analyze(string text, bool includeHtml = true)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            if (text.Length > MaximumCharacters)
                throw new MarkdownAnalysisException("The complete Markdown source exceeds the analysis bound. Read bounded source ranges instead.", "markdown_too_large");

            // The resource decoder deliberately retains a UTF-8 BOM as U+FEFF.
            // It is not Markdown content. Parse after it, then map every source
            // coordinate back; never normalize or rewrite the retained source.
            var sourceOffset = text.Length > 0 && text[0] == '\uFEFF' ? 1 : 0;
            var document = Markdown.Parse(sourceOffset == 0 ? text : text.Substring(1), Pipeline);
            var headings = document.Descendants<HeadingBlock>().Take(MaximumSections + 1).ToList();
            if (headings.Count > MaximumSections)
                throw new MarkdownAnalysisException("The Markdown heading tree exceeds the analysis bound.", "markdown_structure_too_large");

            var sections = BuildSections(text, headings, sourceOffset);
            var links = new List<MarkdownLink>();
            foreach (var node in document.Descendants())
            {
                var link = node as LinkInline;
                var autoLink = node as AutolinkInline;
                if (link == null && autoLink == null) continue;
                if (links.Count == MaximumLinks)
                    throw new MarkdownAnalysisException("The Markdown link list exceeds the analysis bound.", "markdown_structure_too_large");

                var destination = link != null ? link.Url ?? string.Empty :
                    (autoLink.IsEmail ? "mailto:" : string.Empty) + autoLink.Url;
                var label = link != null ? InlineText(link) : autoLink.Url;
                // Reference-style links retain the occurrence span, not the
                // separate definition span. Destinations stay relative here.
                links.Add(new MarkdownLink(destination, label, node.Span.Start + sourceOffset,
                    node.Span.End + 1 + sourceOffset, node.Line + 1, link != null && link.IsImage));
            }

            return new MarkdownDocumentAnalysis(Profile, text.Length,
                includeHtml ? document.ToHtml(Pipeline) : null, sections.AsReadOnly(), links.AsReadOnly());
        }

        private static List<MarkdownSection> BuildSections(string text, List<HeadingBlock> headings, int sourceOffset)
        {
            var starts = new int[headings.Count];
            var ends = new int[headings.Count];
            var parents = new int[headings.Count];
            var lines = new int[headings.Count];
            var stack = new Stack<int>();
            var anchors = new HashSet<string>(StringComparer.Ordinal);
            var suffixes = new Dictionary<string, int>(StringComparer.Ordinal);
            var lineCursor = 0;
            var line = 1;
            for (var index = 0; index < headings.Count; index++)
            {
                var heading = headings[index];
                var contentStart = heading.Inline != null && heading.Inline.Span.Start >= 0
                    ? heading.Inline.Span.Start
                    : heading.Span.Start;
                var start = contentStart + sourceOffset;
                // Include indentation/container markers at the beginning of the
                // source line. Setext headings begin on their title line.
                while (start > 0 && text[start - 1] != '\n' && text[start - 1] != '\r') start--;
                starts[index] = start;
                while (lineCursor < start)
                {
                    if (text[lineCursor] == '\r')
                    {
                        lineCursor++;
                        if (lineCursor < start && text[lineCursor] == '\n') lineCursor++;
                        line++;
                    }
                    else
                    {
                        if (text[lineCursor] == '\n') line++;
                        lineCursor++;
                    }
                }
                lines[index] = line;
                ends[index] = text.Length;
                while (stack.Count != 0 && headings[stack.Peek()].Level >= heading.Level)
                    ends[stack.Pop()] = start;
                parents[index] = stack.Count == 0 ? -1 : stack.Peek();
                stack.Push(index);
            }

            var sections = new List<MarkdownSection>(headings.Count);
            for (var index = 0; index < headings.Count; index++)
            {
                var heading = headings[index];
                var attributes = heading.GetAttributes();
                var title = InlineText(heading.Inline);
                // Use Markdig's GFM slug function after the heading-count guard.
                // Cached suffixes avoid restarting at 1 for every duplicate.
                // The id is assigned on the same AST that produces the HTML.
                var baseAnchor = LinkHelper.UrilizeAsGfm(title);
                if (baseAnchor.Length == 0) baseAnchor = "section";
                var anchor = baseAnchor;
                if (!anchors.Add(anchor))
                {
                    int suffix;
                    if (!suffixes.TryGetValue(baseAnchor, out suffix)) suffix = 0;
                    do { anchor = baseAnchor + "-" + (++suffix).ToString(CultureInfo.InvariantCulture); }
                    while (!anchors.Add(anchor));
                    suffixes[baseAnchor] = suffix;
                }
                attributes.Id = anchor;
                sections.Add(new MarkdownSection(SectionId(index),
                    parents[index] < 0 ? null : SectionId(parents[index]),
                    title, heading.Level, attributes.Id,
                    starts[index], ends[index], heading.Span.End + 1 + sourceOffset, lines[index]));
            }
            return sections;
        }

        private static string SectionId(int index)
        {
            return "section-" + (index + 1).ToString(CultureInfo.InvariantCulture);
        }

        private static string InlineText(ContainerInline root)
        {
            if (root == null) return string.Empty;
            var result = new StringBuilder();
            foreach (var node in root.Descendants())
            {
                var literal = node as LiteralInline;
                if (literal != null) { result.Append(literal.Content.ToString()); continue; }
                var code = node as CodeInline;
                if (code != null) { result.Append(code.Content); continue; }
                var entity = node as HtmlEntityInline;
                if (entity != null) { result.Append(entity.Transcoded.ToString()); continue; }
                var autoLink = node as AutolinkInline;
                if (autoLink != null) { result.Append(autoLink.Url); continue; }
                if (node is LineBreakInline) result.Append(' ');
            }
            return result.ToString().Trim();
        }
    }
}
