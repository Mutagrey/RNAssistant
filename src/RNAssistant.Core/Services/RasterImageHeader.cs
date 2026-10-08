using System;
using System.IO;
using System.Linq;
using System.Text;

namespace RNAssistant.Core.Services
{
    // Pure format admission for inert image delivery. Decoding stays with the viewer.
    // No resizing, second body store or platform drawing dependency.
    public sealed class RasterImageHeader
    {
        public string MimeType { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }

        public static bool IsSupportedExtension(string path)
        { return new[] { ".png", ".jpg", ".jpeg" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase); }

        public static RasterImageHeader Read(byte[] bytes)
        {
            if (bytes == null) return null;
            if (bytes.Length >= 33 && bytes.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
                bytes[8] == 0 && bytes[9] == 0 && bytes[10] == 0 && bytes[11] == 13 &&
                Encoding.ASCII.GetString(bytes, 12, 4) == "IHDR")
            {
                if (bytes[16] != 0 || bytes[17] != 0 || bytes[20] != 0 || bytes[21] != 0) return null;
                return Bounded("image/png", BigEndian(bytes, 18), BigEndian(bytes, 22));
            }
            if (bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8) return null;
            // Walk length-delimited JPEG segments only, never scan compressed pixels.
            for (var offset = 2; offset + 3 < bytes.Length;)
            {
                if (bytes[offset++] != 0xff) return null;
                while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
                if (offset >= bytes.Length) return null;
                var marker = bytes[offset++];
                if (marker == 0xda || marker == 0xd9 || marker == 0 || offset + 2 > bytes.Length) return null;
                var length = BigEndian(bytes, offset);
                if (length < 2 || length > bytes.Length - offset) return null;
                if (marker == 0xc0 || marker == 0xc1 || marker == 0xc2)
                {
                    if (length < 8 || bytes[offset + 2] != 8 || length != 8 + 3 * bytes[offset + 7]) return null;
                    return Bounded("image/jpeg", BigEndian(bytes, offset + 5), BigEndian(bytes, offset + 3));
                }
                offset += length;
            }
            return null;
        }

        private static int BigEndian(byte[] bytes, int offset) { return bytes[offset] * 256 + bytes[offset + 1]; }
        private static RasterImageHeader Bounded(string mime, int width, int height)
        { return width > 0 && height > 0 && width <= 4096 && height <= 4096
            ? new RasterImageHeader { MimeType = mime, Width = width, Height = height } : null; }
    }
}
