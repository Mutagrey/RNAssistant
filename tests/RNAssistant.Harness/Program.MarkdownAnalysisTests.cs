using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void MarkdownAnalysisContract()
        {
            var source = "# Root\r\nbefore\r\n### Child\r\n[child](../other.md#part)\r\n\r\nSibling\r\n-------\r\n~~~md\r\n# Fake\r\n[x](fake.md)\r\n~~~\r\n\r\n# Root\r\ntext\r\n";
            var view = MarkdownAnalysisService.Analyze(source);
            AssertEqual(source.Length, view.CharacterCount, "analysis keeps exact UTF-16 length");
            AssertEqual(4, view.Sections.Count, "ATX and Setext headings share one tree; fences stay literal");
            AssertEqual(2, view.Sections[2].Level, "Setext underline determines heading level");
            AssertEqual(view.Sections[0].Id, view.Sections[1].ParentId, "skipped heading levels keep nearest parent");
            AssertEqual(view.Sections[0].Id, view.Sections[2].ParentId, "sibling returns to the enclosing parent");
            AssertEqual(view.Sections[2].Start, view.Sections[1].End, "section ends before next same-or-higher heading");
            AssertEqual(view.Sections[3].Start, view.Sections[0].End, "parent range includes descendant sections");
            AssertEqual(source.Length, view.Sections[3].End, "last section includes the complete tail");
            AssertTrue(view.Sections[0].Anchor != view.Sections[3].Anchor, "duplicate titles have distinct anchors");
            AssertTrue(view.Sections.All(s => view.Html.Contains("id=\"" + s.Anchor + "\"")), "outline uses the rendered AST anchors");
            AssertEqual(1, view.Links.Count, "only parsed link occurrences are extracted, not fenced examples");
            var link = view.Links[0];
            AssertEqual("../other.md#part", link.Destination, "relative destinations are not resolved without source identity");
            AssertEqual("[child](../other.md#part)", source.Substring(link.Start, link.End - link.Start), "link evidence selects original source exactly");

            var parsed = MarkdownAnalysisService.Analyze(source, false);
            AssertTrue(parsed.Html == null, "indexing skips HTML generation");
            AssertEqual(JArray.FromObject(view.Sections).ToString(), JArray.FromObject(parsed.Sections).ToString(), "render and parse-only share structure");
            var formatted = MarkdownAnalysisService.Analyze("# **Bold** &amp; `code`\n");
            AssertEqual("Bold & code", formatted.Sections[0].Title, "titles use readable inline text");
            var boundedTitle = DocumentArtifactStore.OutlineHeadingTitle(new string('a', 199) + "😀 tail");
            AssertTrue(boundedTitle.EndsWith("…", StringComparison.Ordinal) &&
                char.IsHighSurrogate(boundedTitle[199]) && char.IsLowSurrogate(boundedTitle[200]),
                "outline title truncation never splits a surrogate pair");

            var references = MarkdownAnalysisService.Analyze("[label][r]\n\n[r]: ./target.md#one\n\n![alt](image.png)\n\n<https://example.org/>\n");
            AssertEqual(3, references.Links.Count, "reference links, images and autolinks are retained");
            AssertEqual("./target.md#one", references.Links[0].Destination, "reference definition resolves inside the same AST");
            AssertTrue(references.Links.Single(l => l.Destination == "image.png").IsImage, "images are distinguished from references");
            var unicode = "😀\r\n# Русский\r\n[текст](док.md)\r\n";
            var unicodeView = MarkdownAnalysisService.Analyze(unicode);
            AssertEqual(4, unicodeView.Sections[0].Start, "offsets count surrogate pairs as UTF-16 code units");
            AssertEqual(2, unicodeView.Sections[0].Line, "source lines are one-based");
            AssertEqual("[текст](док.md)", unicode.Substring(unicodeView.Links[0].Start,
                unicodeView.Links[0].End - unicodeView.Links[0].Start), "Unicode link ranges are exact");
            var bomSource = "\uFEFF   # Heading\r\n[link](a.md)\r\n";
            var bomView = MarkdownAnalysisService.Analyze(bomSource);
            AssertEqual("Heading", bomView.Sections.Single().Title, "UTF-8 BOM does not hide the first heading");
            AssertEqual(0, bomView.Sections[0].Start, "section range preserves the original BOM and indentation");
            AssertEqual("[link](a.md)", bomSource.Substring(bomView.Links[0].Start,
                bomView.Links[0].End - bomView.Links[0].Start), "BOM offset is restored for link evidence");

            var html = MarkdownAnalysisService.Analyze("<script>alert(1)</script>\n\n[unsafe](javascript:alert(1))\n");
            AssertTrue(!html.Html.Contains("<script>"), "raw HTML is not executed as document markup");
            AssertTrue(html.Links[0].Destination.StartsWith("javascript:", StringComparison.Ordinal), "link metadata is data; renderer sanitization is a separate boundary");
            var empty = MarkdownAnalysisService.Analyze("#\n");
            AssertEqual(1, empty.Sections.Count, "empty headings remain addressable");
            AssertTrue(empty.Html.Contains("id=\"" + empty.Sections[0].Anchor + "\""), "empty heading also shares its HTML anchor");

            ExpectMarkdownAnalysisError(() => MarkdownAnalysisService.Analyze(new string('x',
                MarkdownAnalysisService.MaximumCharacters + 1), false), "markdown_too_large");
            var manyHeadings = new StringBuilder();
            for (var i = 0; i <= MarkdownAnalysisService.MaximumSections; i++) manyHeadings.Append("# Heading\n");
            ExpectMarkdownAnalysisError(() => MarkdownAnalysisService.Analyze(manyHeadings.ToString(), false), "markdown_structure_too_large");
            var manyLinks = new StringBuilder();
            for (var i = 0; i <= MarkdownAnalysisService.MaximumLinks; i++) manyLinks.Append("[x](x) ");
            ExpectMarkdownAnalysisError(() => MarkdownAnalysisService.Analyze(manyLinks.ToString(), false), "markdown_structure_too_large");
        }

        private static void ExpectMarkdownAnalysisError(Action action, string code)
        {
            try { action(); }
            catch (MarkdownAnalysisException error) { AssertEqual(code, error.Code, "bounded Markdown failure is explicit"); return; }
            throw new InvalidOperationException("Expected Markdown analysis failure: " + code);
        }
    }
}
