using System;
using System.Collections.Generic;
using System.Globalization;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    public enum ResourcePdfPageSelectionKind { None, Page, Range }

    // The model-facing PDF selectors have one admission rule for Office and workspace reads.
    public static class ResourcePdfPageSelectorContract
    {
        private static readonly HashSet<string> PageKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "type", "target", "representation", "pageIndex", "startChar", "charCount" };
        private static readonly HashSet<string> RangeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "type", "target", "representation", "startPage", "pageCount" };

        public static ResourcePdfPageSelectionKind Validate(IDictionary<string, object> arguments,
            string representation, bool supportsPdf, int maximumTextCharacters)
        {
            if (arguments == null) throw new ArgumentNullException(nameof(arguments));
            var page = arguments.ContainsKey("pageIndex");
            var range = arguments.ContainsKey("startPage") || arguments.ContainsKey("pageCount");
            if (!page && !range) return ResourcePdfPageSelectionKind.None;

            int first, count;
            if (range)
            {
                if (!supportsPdf || representation != ResourceRepresentations.Text || page ||
                    !NonNegative(arguments, "startPage", out first) ||
                    !Positive(arguments, "pageCount", out count) || !OnlyKeys(arguments, RangeKeys))
                    throw new ResourceRequestException(
                        "PDF page ranges require startPage and positive pageCount without other selectors.",
                        "resource_page_selector_invalid", false);
                return ResourcePdfPageSelectionKind.Range;
            }

            if (!supportsPdf || representation != ResourceRepresentations.Text ||
                !NonNegative(arguments, "pageIndex", out first) ||
                arguments.ContainsKey("startChar") != arguments.ContainsKey("charCount") ||
                arguments.ContainsKey("startChar") &&
                    (!NonNegative(arguments, "startChar", out first) ||
                     !Positive(arguments, "charCount", out count) || count > maximumTextCharacters) ||
                !OnlyKeys(arguments, PageKeys))
                throw new ResourceRequestException(
                    "PDF page text requires pageIndex and optional paired startChar/charCount only.",
                    "resource_page_selector_invalid", false);
            return ResourcePdfPageSelectionKind.Page;
        }

        private static bool NonNegative(IDictionary<string, object> arguments, string key, out int value)
        { return Integer(arguments, key, out value) && value >= 0; }

        private static bool Positive(IDictionary<string, object> arguments, string key, out int value)
        { return Integer(arguments, key, out value) && value > 0; }

        private static bool Integer(IDictionary<string, object> arguments, string key, out int value)
        {
            object raw;
            value = 0;
            return arguments.TryGetValue(key, out raw) && raw != null &&
                int.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out value);
        }

        private static bool OnlyKeys(IDictionary<string, object> arguments, HashSet<string> allowed)
        {
            foreach (var key in arguments.Keys)
                if (!allowed.Contains(key)) return false;
            return true;
        }
    }
}
