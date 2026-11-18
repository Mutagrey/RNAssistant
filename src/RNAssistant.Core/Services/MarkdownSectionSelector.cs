using System;
using System.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    public sealed class MarkdownSectionException : InvalidOperationException
    {
        public string Code { get; private set; }
        public MarkdownSectionException(string message, string code) : base(message) { Code = code; }
    }

    // Selection is derived from the same exact-source parse used by the viewer.
    // A matching title is a semantic selector, never whole-resource read evidence.
    public static class MarkdownSectionSelector
    {
        public static MarkdownSection Select(string text, string section, int maximumCharacters)
        {
            if (string.IsNullOrWhiteSpace(section) || section.Length > 200 || maximumCharacters < 1)
                throw new MarkdownSectionException("Use a complete visible heading title of at most 200 characters.", "resource_section_invalid");
            MarkdownDocumentAnalysis analysis;
            try { analysis = MarkdownAnalysisService.Analyze(text, false); }
            catch (MarkdownAnalysisException error)
            { throw new MarkdownSectionException(error.Message, "resource_section_incomplete"); }
            return Select(analysis, section, maximumCharacters);
        }

        public static MarkdownSection Select(MarkdownDocumentAnalysis analysis, string section, int maximumCharacters)
        {
            if (analysis == null || string.IsNullOrWhiteSpace(section) || section.Length > 200 || maximumCharacters < 1)
                throw new MarkdownSectionException("Use a complete visible heading title of at most 200 characters.", "resource_section_invalid");
            var matches = analysis.Sections.Where(item => string.Equals(item.Title, section.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
                throw new MarkdownSectionException(matches.Length == 0
                    ? "Heading not found. Read the outline or source; do not guess a section title."
                    : "The heading is repeated. Choose a unique nested heading or use source line/character selectors.",
                    matches.Length == 0 ? "resource_section_not_found" : "resource_section_ambiguous");
            var selected = matches[0];
            if (selected.End - selected.Start > maximumCharacters)
                throw new MarkdownSectionException("This section exceeds the bounded read size. Choose a narrower nested heading or use source line/character selectors.", "resource_section_too_large");
            return selected;
        }
    }
}
