using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using PDFtoImage;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Services
{
    public static class ModelAttachmentService
    {
        private const int MaxPdfPageDimension = 2048;

        public static IReadOnlyList<ModelImagePart> ReadForModel(
            AttachmentStore store,
            AppSettings settings,
            ChatAttachment attachment,
            int maxImages,
            CancellationToken cancellationToken)
        {
            if (attachment == null)
            {
                return new ModelImagePart[0];
            }
            if (string.Equals(attachment.Kind, "image", StringComparison.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = AttachmentImageService.ReadForModel(store, attachment);
                return bytes == null || bytes.Length == 0
                    ? (IReadOnlyList<ModelImagePart>)new ModelImagePart[0]
                    : new[]
                    {
                        new ModelImagePart
                        {
                            Bytes = bytes,
                            ContentType = attachment.ContentType,
                            Label = attachment.FileName
                        }
                    };
            }
            if (!string.Equals(attachment.Kind, "pdf", StringComparison.OrdinalIgnoreCase) ||
                !ModelContextBudget.SupportsImages(settings))
            {
                return new ModelImagePart[0];
            }

            var pdf = store == null ? null : store.ReadBytes(attachment);
            if (pdf == null || pdf.Length == 0)
            {
                throw new InvalidOperationException("Attachment file is missing: " + (attachment.FileName ?? attachment.Id));
            }

            var pageCount = attachment.PageCount > 0 ? attachment.PageCount : Conversion.GetPageCount(pdf);
            var requested = maxImages <= 0 ? ModelContextBudget.MaxImagesPerPrompt(settings) : maxImages;
            var limit = Math.Min(pageCount, Math.Min(ModelContextBudget.MaxImagesPerPrompt(settings), requested));
            var indexes = SelectPages(attachment.PageTextLengths, pageCount, limit);
            var result = new List<ModelImagePart>();
            foreach (var pageIndex in indexes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pageSize = Conversion.GetPageSize(pdf, new Index(pageIndex));
                var options = pageSize.Width >= pageSize.Height
                    ? new RenderOptions(Width: MaxPdfPageDimension, Height: null, WithAspectRatio: true)
                    : new RenderOptions(Width: null, Height: MaxPdfPageDimension, WithAspectRatio: true);
                using (var output = new MemoryStream())
                {
                    Conversion.SaveJpeg(output, pdf, new Index(pageIndex), null, options);
                    result.Add(new ModelImagePart
                    {
                        Bytes = output.ToArray(),
                        ContentType = "image/jpeg",
                        Label = (attachment.FileName ?? "PDF") + " page " + (pageIndex + 1)
                    });
                }
            }
            return result;
        }

        internal static IReadOnlyList<int> SelectPages(IReadOnlyList<int> textLengths, int pageCount, int limit)
        {
            var result = new List<int>();
            var lengths = textLengths ?? new int[0];
            for (var index = 0; index < pageCount && result.Count < limit; index++)
            {
                if (index >= lengths.Count || lengths[index] < 20)
                {
                    result.Add(index);
                }
            }
            for (var index = 0; index < pageCount && result.Count < limit; index++)
            {
                if (!result.Contains(index))
                {
                    result.Add(index);
                }
            }
            return result;
        }
    }
}
