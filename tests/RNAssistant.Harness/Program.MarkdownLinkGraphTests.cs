using System;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void MarkdownGraphContract()
        {
            var wiki = "\uFEFF# Wiki\r\n\r\n[[Reference|Alias]] and [doc](b.md#target)\r\n\r\n" +
                "\\[[escaped]] `[[ignored]]`\r\n\r\n```md\r\n[[also ignored]]\r\n```\r\n";
            var analysis = MarkdownAnalysisService.Analyze(wiki);
            AssertEqual(2, analysis.Links.Count, "wiki links in literal text, not inside code");
            AssertEqual("wiki", analysis.Links[0].Kind, "wiki link kind preserved");
            AssertEqual("[[Reference|Alias]]", wiki.Substring(analysis.Links[0].Start,
                analysis.Links[0].End - analysis.Links[0].Start), "wiki link exact source span");
            AssertEqual(3, analysis.Links[0].Line, "wiki source line accounts for BOM and CRLF");
            AssertTrue(analysis.Html.Contains("[[Reference|Alias]] and "), "rendered Markdown retains the literal wiki mention and trailing text");
            var a = new MarkdownGraphSource { Id = "docs/a.md", Title = "a.md", Path = "docs/a.md", Analysis = analysis };
            var b = new MarkdownGraphSource { Id = "docs/b.md", Title = "b.md", Path = "docs/b.md",
                Analysis = MarkdownAnalysisService.Analyze("# Target\n", false) };
            var graph = MarkdownLinkGraphService.Build(new[] { a, b }, a.Id, "workspace", true);
            AssertEqual(2, graph.Edges.Count, "both occurrences kept as evidence");
            AssertTrue(graph.Edges.Any(edge => edge.To == b.Id && edge.Status == "resolved"),
                "relative path resolves within workspace");
            AssertTrue(graph.Edges.Any(edge => edge.Status == "missing"),
                "complete corpus can prove a missing target");
            var partial = MarkdownLinkGraphService.Build(new[] { a, b }, a.Id, "workspace", false);
            AssertTrue(!partial.Complete && partial.Edges.Any(edge => edge.Status == "pending"),
                "incomplete corpus cannot prove absence");
            var c = new MarkdownGraphSource { Id = "other/Reference.md", Path = "other/Reference.md",
                Title = "Reference.md", Analysis = b.Analysis };
            var d = new MarkdownGraphSource { Id = "another/Reference.md", Path = "another/Reference.md",
                Title = "Reference.md", Analysis = b.Analysis };
            var ambiguous = MarkdownLinkGraphService.Build(new[] { a, b, c, d }, a.Id, "workspace", true);
            AssertTrue(ambiguous.Edges.Any(edge => edge.Status == "ambiguous"),
                "duplicate wiki names remain ambiguous");
            var outside = MarkdownLinkGraphService.Build(new[] {
                new MarkdownGraphSource { Id = "a.md", Path = "a.md", Title = "a.md",
                    Analysis = MarkdownAnalysisService.Analyze("[escape](../secret.md) [encoded](%2e%2e/secret.md) [root](%2Fsecret.md)", false) }
            }, "a.md", "workspace", true);
            AssertEqual(3, outside.Edges.Count(edge => edge.Status == "outside"),
                "plain and encoded paths cannot leave workspace root");

            var history = new MarkdownGraphSource { Id = "old", Title = "b.md", Historical = true,
                Analysis = MarkdownAnalysisService.Analyze("[Head](./b.md)", false) };
            var head = new MarkdownGraphSource { Id = "current", Title = "b.md", Analysis = b.Analysis };
            var document = MarkdownLinkGraphService.Build(new[] { history, head }, history.Id, "document", true);
            AssertEqual(head.Id, document.Edges.Single().To, "opened history never shadows a current head");

            var oversized = new MarkdownGraphSource { Id = "many", Path = "many.md", Title = "many.md",
                Analysis = MarkdownAnalysisService.Analyze(string.Concat(Enumerable.Repeat("[gone](gone.md) ",
                    MarkdownLinkGraphService.MaximumEdges + 1)), false) };
            var truncated = MarkdownLinkGraphService.Build(new[] { oversized }, oversized.Id, "workspace", true);
            AssertTrue(!truncated.Complete && truncated.Edges.Count == MarkdownLinkGraphService.MaximumEdges &&
                truncated.Edges.All(edge => edge.Status == "pending"), "edge cap never leaves an earlier false missing status");
        }
    }
}
