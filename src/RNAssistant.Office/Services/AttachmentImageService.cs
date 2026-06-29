using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Services
{
    public static class AttachmentImageService
    {
        private const int MaxModelDimension = 2048;

        public static byte[] ReadForModel(AttachmentStore store, ChatAttachment attachment)
        {
            var bytes = store == null ? null : store.ReadBytes(attachment);
            if (bytes == null || attachment == null || attachment.Kind != "image" ||
                attachment.ContentType == "image/webp" || attachment.ContentType == "image/gif")
            {
                return bytes;
            }

            using (var input = new MemoryStream(bytes, false))
            using (var source = Image.FromStream(input, true, true))
            {
                if (source.Width <= MaxModelDimension && source.Height <= MaxModelDimension)
                {
                    return bytes;
                }

                var scale = Math.Min((double)MaxModelDimension / source.Width, (double)MaxModelDimension / source.Height);
                var width = Math.Max(1, (int)Math.Round(source.Width * scale));
                var height = Math.Max(1, (int)Math.Round(source.Height * scale));
                using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                {
                    bitmap.SetResolution(source.HorizontalResolution > 0 ? source.HorizontalResolution : 96, source.VerticalResolution > 0 ? source.VerticalResolution : 96);
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.CompositingMode = CompositingMode.SourceCopy;
                        graphics.CompositingQuality = CompositingQuality.HighQuality;
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.SmoothingMode = SmoothingMode.HighQuality;
                        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        graphics.DrawImage(source, 0, 0, width, height);
                    }

                    using (var output = new MemoryStream())
                    {
                        if (attachment.ContentType == "image/jpeg")
                        {
                            var encoder = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
                            using (var parameters = new EncoderParameters(1))
                            {
                                parameters.Param[0] = new EncoderParameter(Encoder.Quality, 88L);
                                bitmap.Save(output, encoder, parameters);
                            }
                        }
                        else
                        {
                            bitmap.Save(output, ImageFormat.Png);
                        }
                        return output.ToArray();
                    }
                }
            }
        }
    }
}
