using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace RNAssistant.Core.Storage
{
    internal sealed class PdfAttachmentTextExtraction
    {
        public string Text { get; set; }
        public int PageCount { get; set; }
        public List<int> PageTextLengths { get; set; }
    }

    internal sealed class PdfAttachmentPageText
    {
        public string Text { get; set; }
        public int PageCount { get; set; }

        public string SelectWindow(int startChar, int charCount, out int endChar)
        {
            if (startChar < 0 || charCount < 1 || startChar > Text.Length)
                throw new System.ArgumentOutOfRangeException(nameof(startChar));
            endChar = (int)System.Math.Min((long)Text.Length, (long)startChar + charCount);
            if ((startChar > 0 && startChar < Text.Length &&
                 char.IsLowSurrogate(Text[startChar]) && char.IsHighSurrogate(Text[startChar - 1])) ||
                (endChar > 0 && endChar < Text.Length &&
                 char.IsHighSurrogate(Text[endChar - 1]) && char.IsLowSurrogate(Text[endChar])))
                throw new System.ArgumentException("The page character range splits a UTF-16 surrogate pair.");
            return Text.Substring(startChar, endChar - startChar);
        }
    }

    internal sealed class PdfAttachmentPageRangeText
    {
        public string Text { get; set; }
        public int PageCount { get; set; }
        public int EndPage { get; set; }
    }

    internal sealed class PdfPageWindowRequiredException : System.Exception
    {
        public PdfPageWindowRequiredException()
            : base("The first selected PDF page exceeds the model read budget; use pageIndex with startChar/charCount.") { }
    }

    internal static class PdfAttachmentTextExtractor
    {
        public const string PageViewProfile = "pdf-page-text:content-order-v1";
        public const string RangeViewProfile = "pdf-page-range-text:content-order-v1";

        public static PdfAttachmentTextExtraction Extract(string path, int maxCharacters)
        {
            using (var document = PdfDocument.Open(path)) return Extract(document, maxCharacters);
        }

        public static PdfAttachmentTextExtraction Extract(byte[] bytes, int maxCharacters)
        {
            using (var document = PdfDocument.Open(bytes)) return Extract(document, maxCharacters);
        }

        public static PdfAttachmentPageText ExtractPage(byte[] bytes, int pageIndex)
        {
            using (var document = PdfDocument.Open(bytes)) return ExtractPage(document, pageIndex);
        }

        public static PdfAttachmentPageText ExtractPage(Stream stream, int pageIndex)
        {
            using (var document = PdfDocument.Open(stream)) return ExtractPage(document, pageIndex);
        }

        public static PdfAttachmentPageRangeText ExtractRange(byte[] bytes, int startPage, int requestedPages,
            int maximumCharacters)
        {
            using (var document = PdfDocument.Open(bytes))
                return ExtractRange(document, startPage, requestedPages, maximumCharacters);
        }

        public static PdfAttachmentPageRangeText ExtractRange(Stream stream, int startPage, int requestedPages,
            int maximumCharacters)
        {
            using (var document = PdfDocument.Open(stream))
                return ExtractRange(document, startPage, requestedPages, maximumCharacters);
        }

        private static PdfAttachmentPageRangeText ExtractRange(PdfDocument document, int startPage,
            int requestedPages, int maximumCharacters)
        {
            if (startPage < 0 || startPage >= document.NumberOfPages || requestedPages < 1 || maximumCharacters < 1)
                throw new System.ArgumentOutOfRangeException(nameof(startPage));
            var requestedEnd = (int)System.Math.Min((long)document.NumberOfPages, (long)startPage + requestedPages);
            var builder = new StringBuilder();
            var end = startPage;
            while (end < requestedEnd)
            {
                var text = ContentOrderTextExtractor.GetText(document.GetPage(end + 1)) ?? string.Empty;
                var heading = "[PDF page " + (end + 1).ToString(CultureInfo.InvariantCulture) + "]\n";
                if ((long)heading.Length + text.Length + 1 > maximumCharacters - builder.Length)
                {
                    if (end == startPage) throw new PdfPageWindowRequiredException();
                    break;
                }
                builder.Append(heading).Append(text).Append('\n');
                end++;
            }
            return new PdfAttachmentPageRangeText { Text = builder.ToString(), PageCount = document.NumberOfPages,
                EndPage = end };
        }

        private static PdfAttachmentPageText ExtractPage(PdfDocument document, int pageIndex)
        {
            if (pageIndex < 0 || pageIndex >= document.NumberOfPages)
                throw new System.ArgumentOutOfRangeException(nameof(pageIndex));
            return new PdfAttachmentPageText {
                Text = ContentOrderTextExtractor.GetText(document.GetPage(pageIndex + 1)) ?? string.Empty,
                PageCount = document.NumberOfPages
            };
        }

        private static PdfAttachmentTextExtraction Extract(PdfDocument document, int maxCharacters)
        {
            var builder = new StringBuilder();
            var result = new PdfAttachmentTextExtraction
            {
                PageTextLengths = new List<int>()
            };
            result.PageCount = document.NumberOfPages;
            foreach (var page in document.GetPages())
            {
                if (builder.Length >= maxCharacters) break;
                var pageText = ContentOrderTextExtractor.GetText(page) ?? string.Empty;
                result.PageTextLengths.Add(pageText.Trim().Length);
                builder.AppendLine("[PDF page " + page.Number + "]");
                builder.AppendLine(pageText);
            }
            result.Text = builder.ToString();
            return result;
        }
    }
}
