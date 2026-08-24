using System.Collections.Generic;
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

    internal static class PdfAttachmentTextExtractor
    {
        public static PdfAttachmentTextExtraction Extract(string path, int maxCharacters)
        {
            var builder = new StringBuilder();
            var result = new PdfAttachmentTextExtraction
            {
                PageTextLengths = new List<int>()
            };
            using (var document = PdfDocument.Open(path))
            {
                result.PageCount = document.NumberOfPages;
                foreach (var page in document.GetPages())
                {
                    if (builder.Length >= maxCharacters) break;
                    var pageText = ContentOrderTextExtractor.GetText(page) ?? string.Empty;
                    result.PageTextLengths.Add(pageText.Trim().Length);
                    builder.AppendLine("[PDF page " + page.Number + "]");
                    builder.AppendLine(pageText);
                }
            }
            result.Text = builder.ToString();
            return result;
        }
    }
}
