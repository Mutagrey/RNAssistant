using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    public sealed class AttachmentStore
    {
        public const int MaxFilesPerMessage = 10;
        public const long MaxFileBytes = 20L * 1024L * 1024L;
        public const long MaxMessageBytes = 50L * 1024L * 1024L;
        private const int MaxExtractedChars = 1000000;
        private const int MaxInlinePreviewChars = 4000;
        private static readonly HashSet<string> TextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".markdown", ".mdx", ".json", ".jsonl", ".ndjson", ".csv", ".tsv",
            ".xml", ".xaml", ".svg", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".config",
            ".env", ".properties", ".log", ".sql", ".html", ".htm", ".css", ".scss", ".sass", ".less",
            ".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".vue", ".svelte", ".cs", ".vb", ".fs",
            ".fsx", ".java", ".kt", ".kts", ".c", ".h", ".cpp", ".hpp", ".cc", ".go", ".rs", ".py",
            ".rb", ".php", ".swift", ".r", ".sh", ".bash", ".zsh", ".ps1", ".bat", ".cmd", ".vba",
            ".bas", ".cls", ".frm", ".tex", ".rst", ".adoc", ".diff", ".patch", ".rtf", ".eml",
            ".ics", ".vcf", ".csproj", ".vbproj", ".fsproj", ".props", ".targets", ".sln", ".gradle",
            ".gitignore", ".gitattributes", ".editorconfig", ".dockerfile"
        };
        private readonly AppDataPaths _paths;
        private readonly ChatBlobStore _blobs;

        public AttachmentStore(AppDataPaths paths)
            : this(paths, null)
        {
        }

        public AttachmentStore(AppDataPaths paths, Func<StorageProtector> protectionProvider)
        {
            _paths = paths ?? throw new ArgumentNullException("paths");
            _blobs = new ChatBlobStore(paths, protectionProvider);
            CleanupExpiredDrafts(DateTime.UtcNow.AddDays(-1));
        }

        public ChatAttachment Import(string fileName, string contentType, string base64)
        {
            if (string.IsNullOrWhiteSpace(base64) || base64.Length > ((MaxFileBytes + 2) / 3) * 4 + 16)
            {
                throw new InvalidOperationException("Attachment must be between 1 byte and 20 MB.");
            }
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(base64 ?? string.Empty);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException("Attachment data is not valid base64.", ex);
            }

            if (bytes.LongLength == 0 || bytes.LongLength > MaxFileBytes)
            {
                throw new InvalidOperationException("Attachment must be between 1 byte and 20 MB.");
            }

            fileName = SafeDisplayName(fileName);
            var kind = DetectKind(fileName, contentType, bytes);
            if (kind == null)
            {
                throw new InvalidOperationException("Unsupported or binary attachment type. Use images, PDF, MP3, WAV or a text-based file.");
            }

            contentType = NormalizeContentType(kind, contentType, bytes);
            var attachment = new ChatAttachment
            {
                FileName = fileName,
                ContentType = contentType,
                Size = bytes.LongLength,
                Kind = kind
            };
            var extension = SafeExtension(fileName, kind);
            attachment.RelativePath = Path.Combine("staging", attachment.Id + extension);
            var path = AbsolutePath(attachment.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, bytes);

            try
            {
                ExtractText(attachment, path);
            }
            catch (Exception ex)
            {
                attachment.Status = "error";
                attachment.Error = "Не удалось прочитать содержимое: " + ex.Message;
            }

            return attachment;
        }

        public List<ChatAttachment> LoadDrafts(IEnumerable<string> ids)
        {
            var requested = (ids ?? new string[0]).Where(IsSafeId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (requested.Count > MaxFilesPerMessage)
            {
                throw new InvalidOperationException("No more than 10 attachments are allowed per message.");
            }

            var result = new List<ChatAttachment>();
            foreach (var id in requested)
            {
                var metadata = LoadMetadata(id);
                if (metadata == null || !IsSafeId(metadata.Id) ||
                    !string.Equals(metadata.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Attachment metadata is no longer available: " + id);
                }
                if (!File.Exists(AbsolutePath(metadata.RelativePath)))
                {
                    throw new InvalidOperationException("Attachment is no longer available: " + id);
                }
                result.Add(metadata);
            }

            if (result.Sum(a => a.Size) > MaxMessageBytes)
            {
                throw new InvalidOperationException("Attachments exceed the 50 MB total limit.");
            }
            return result;
        }

        public void SaveDraftMetadata(ChatAttachment attachment)
        {
            if (attachment == null || !IsSafeId(attachment.Id))
            {
                return;
            }
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(attachment);
            StorageFileSystem.WriteAllTextAtomic(
                Path.Combine(StagingDirectory(), attachment.Id + ".meta.json"),
                json,
                new UTF8Encoding(false));
        }

        public void DeleteDraft(string id)
        {
            if (!IsSafeId(id) || !Directory.Exists(StagingDirectory()))
            {
                return;
            }
            foreach (var path in Directory.GetFiles(StagingDirectory(), id + ".*"))
            {
                SafeDeleteFile(path);
            }
        }

        public void Commit(ChatMessage message)
        {
            Commit(message, true);
        }

        public void Commit(ChatMessage message, bool deleteDrafts)
        {
            if (message == null || message.Attachments == null || message.Attachments.Count == 0)
            {
                return;
            }
            foreach (var attachment in message.Attachments.Where(item => item != null))
            {
                if (!File.Exists(AbsolutePath(attachment.RelativePath)))
                {
                    throw new InvalidOperationException("Attachment file is missing: " + (attachment.FileName ?? attachment.Id));
                }
            }
            foreach (var attachment in message.Attachments.Where(item => item != null))
            {
                var source = AbsolutePath(attachment.RelativePath);
                var content = _blobs.StoreFile(source, attachment.ContentType);
                attachment.ContentSha256 = content.Sha256;
                attachment.ContentByteLength = content.ByteLength;
                var extractedSource = ExtractedTextAbsolutePath(attachment);
                if (!string.IsNullOrWhiteSpace(extractedSource) && File.Exists(extractedSource))
                {
                    var extracted = _blobs.StoreFile(extractedSource, "text/plain; charset=utf-8");
                    attachment.ExtractedTextSha256 = extracted.Sha256;
                    attachment.ExtractedTextByteLength = extracted.ByteLength;
                }
                attachment.RelativePath = null;
                attachment.ExtractedTextPath = null;
                if (deleteDrafts) DeleteDraft(attachment.Id);
            }
        }

        public void DeleteDrafts(ChatMessage message)
        {
            foreach (var attachment in message == null || message.Attachments == null
                ? new List<ChatAttachment>()
                : message.Attachments.Where(item => item != null))
            {
                try
                {
                    DeleteDraft(attachment.Id);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        public void DeleteMessage(ChatMessage message)
        {
            // Committed content is immutable and may be shared by other messages/sessions.
            // Orphaned blobs are removed only by runtime reset or a future reachability GC.
        }

        public void CloneMessageAttachments(ChatMessage message)
        {
            if (message == null || message.Attachments == null)
            {
                return;
            }
            foreach (var attachment in message.Attachments.Where(item => item != null))
            {
                var cloneId = IsSafeId(attachment.Id) ? attachment.Id : Guid.NewGuid().ToString("N");
                attachment.Id = cloneId;
                if (!string.IsNullOrWhiteSpace(attachment.ContentSha256))
                {
                    attachment.RelativePath = null;
                    attachment.ExtractedTextPath = null;
                    continue;
                }
                attachment.RelativePath = null;
                attachment.ExtractedTextPath = null;
                attachment.Status = "missing";
                attachment.Error = "У вложения нет content-addressed payload.";
            }
        }

        public byte[] ReadBytes(ChatAttachment attachment)
        {
            if (attachment == null)
            {
                return null;
            }
            if (!string.IsNullOrWhiteSpace(attachment.ContentSha256) && attachment.ContentByteLength.HasValue)
            {
                return _blobs.ReadBytes(new ChatBlobReference
                {
                    Sha256 = attachment.ContentSha256,
                    ByteLength = attachment.ContentByteLength.Value,
                    ContentType = attachment.ContentType
                });
            }
            var path = AbsolutePath(attachment.RelativePath);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        public string ReadExtractedText(ChatAttachment attachment)
        {
            return ReadExtractedText(attachment, int.MaxValue);
        }

        public string ReadExtractedText(ChatAttachment attachment, int maxChars)
        {
            if (attachment == null || maxChars <= 0)
            {
                return string.Empty;
            }
            if (!string.IsNullOrWhiteSpace(attachment.ExtractedTextSha256) && attachment.ExtractedTextByteLength.HasValue)
            {
                var text = _blobs.ReadText(new ChatBlobReference
                {
                    Sha256 = attachment.ExtractedTextSha256,
                    ByteLength = attachment.ExtractedTextByteLength.Value,
                    ContentType = "text/plain; charset=utf-8"
                }) ?? string.Empty;
                return text.Length <= maxChars ? text : text.Substring(0, maxChars);
            }
            var path = ExtractedTextAbsolutePath(attachment);
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                if (maxChars == int.MaxValue)
                {
                    return File.ReadAllText(path, Encoding.UTF8);
                }

                var builder = new StringBuilder(Math.Min(maxChars, 16384));
                var buffer = new char[Math.Min(maxChars, 4096)];
                using (var reader = new StreamReader(path, Encoding.UTF8, true))
                {
                    while (builder.Length < maxChars)
                    {
                        var read = reader.Read(buffer, 0, Math.Min(buffer.Length, maxChars - builder.Length));
                        if (read <= 0) break;
                        builder.Append(buffer, 0, read);
                    }
                }
                return builder.ToString();
            }
            var inline = attachment.ExtractedText ?? string.Empty;
            return inline.Length <= maxChars ? inline : inline.Substring(0, maxChars);
        }

        private void ExtractText(ChatAttachment attachment, string path)
        {
            if (attachment.Kind == "text")
            {
                string text;
                if (!TryDecodeText(File.ReadAllBytes(path), true, out text))
                {
                    throw new InvalidOperationException("The file does not contain supported text.");
                }
                SetExtractedText(attachment, path, text);
                return;
            }
            if (attachment.Kind != "pdf")
            {
                return;
            }

            var extracted = PdfAttachmentTextExtractor.Extract(path, MaxExtractedChars);
            attachment.PageCount = extracted.PageCount;
            attachment.PageTextLengths = extracted.PageTextLengths;
            SetExtractedText(attachment, path, extracted.Text);
            if (attachment.PageTextLengths.Count == 0 || attachment.PageTextLengths.All(length => length < 20))
            {
                attachment.ExtractionWarning = "PDF contains little or no extractable text; a vision model is required for scanned pages.";
            }
        }

        private void SetExtractedText(ChatAttachment attachment, string sourcePath, string text)
        {
            text = text ?? string.Empty;
            attachment.TextTruncated = text.Length > MaxExtractedChars;
            if (attachment.TextTruncated)
            {
                text = text.Substring(0, MaxExtractedChars);
                attachment.ExtractionWarning = "Extracted text was truncated at 1,000,000 characters.";
            }
            attachment.ExtractedCharCount = text.Length;
            attachment.ExtractedText = text.Length > MaxInlinePreviewChars
                ? text.Substring(0, MaxInlinePreviewChars)
                : text;
            var sidecarPath = Path.Combine(Path.GetDirectoryName(sourcePath), attachment.Id + ".extracted.txt");
            File.WriteAllText(sidecarPath, text, new UTF8Encoding(false));
            attachment.ExtractedTextPath = RelativePath(sidecarPath);
        }

        private ChatAttachment LoadMetadata(string id)
        {
            var path = Path.Combine(StagingDirectory(), id + ".meta.json");
            if (!File.Exists(path))
            {
                return null;
            }
            return Newtonsoft.Json.JsonConvert.DeserializeObject<ChatAttachment>(File.ReadAllText(path, Encoding.UTF8));
        }

        private void CleanupExpiredDrafts(DateTime cutoffUtc)
        {
            var directory = StagingDirectory();
            if (!Directory.Exists(directory))
            {
                return;
            }
            try
            {
                foreach (var path in Directory.GetFiles(directory))
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoffUtc)
                    {
                        SafeDeleteFile(path);
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private string StagingDirectory() { return Path.Combine(_paths.AttachmentDirectory, "staging"); }
        private string RelativePath(string path) { return path.Substring(_paths.AttachmentDirectory.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }

        private string AbsolutePath(string relative)
        {
            var root = Path.GetFullPath(_paths.AttachmentDirectory + Path.DirectorySeparatorChar);
            var path = Path.GetFullPath(Path.Combine(root, relative ?? string.Empty));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Invalid attachment path.");
            }
            return path;
        }

        private string ExtractedTextAbsolutePath(ChatAttachment attachment)
        {
            return attachment == null || string.IsNullOrWhiteSpace(attachment.ExtractedTextPath)
                ? null
                : AbsolutePath(attachment.ExtractedTextPath);
        }

        private static string DetectKind(string name, string contentType, byte[] bytes)
        {
            var extension = Path.GetExtension(name).ToLowerInvariant();
            if (IsImageSignature(bytes)) return "image";
            if (bytes.Length >= 5 && Encoding.ASCII.GetString(bytes, 0, 5) == "%PDF-") return "pdf";
            if (IsWavSignature(bytes) || IsMp3Signature(bytes)) return "audio";
            if (extension == ".mp3" || extension == ".wav" ||
                !string.IsNullOrWhiteSpace(contentType) && contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            if (IsKnownBinarySignature(bytes)) return null;
            var likelyText = TextExtensions.Contains(extension)
                || string.IsNullOrWhiteSpace(extension)
                || (!string.IsNullOrWhiteSpace(contentType) && contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase));
            string ignored;
            if (TryDecodeText(bytes, likelyText, out ignored)) return "text";
            return null;
        }

        private static bool IsKnownBinarySignature(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 2) return false;
            if (bytes[0] == (byte)'M' && bytes[1] == (byte)'Z') return true;
            if (bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4b && bytes[2] >= 0x03 && bytes[2] <= 0x07) return true;
            if (bytes.Length >= 8 && bytes[0] == 0xd0 && bytes[1] == 0xcf && bytes[2] == 0x11 && bytes[3] == 0xe0) return true;
            if (bytes.Length >= 16 && Encoding.ASCII.GetString(bytes, 0, 16) == "SQLite format 3\0") return true;
            if (bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b) return true;
            if (bytes.Length >= 6 && bytes[0] == 0x37 && bytes[1] == 0x7a && bytes[2] == 0xbc && bytes[3] == 0xaf) return true;
            if (bytes.Length >= 4 && bytes[0] == 0x7f && bytes[1] == (byte)'E' && bytes[2] == (byte)'L' && bytes[3] == (byte)'F') return true;
            return false;
        }

        private static bool TryDecodeText(byte[] bytes, bool allowWindows1251, out string text)
        {
            text = string.Empty;
            if (bytes == null || bytes.Length == 0)
            {
                return false;
            }
            try
            {
                if (bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xfe && bytes[2] == 0 && bytes[3] == 0)
                    text = Encoding.UTF32.GetString(bytes, 4, bytes.Length - 4);
                else if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xfe && bytes[3] == 0xff)
                    text = new UTF32Encoding(true, true, true).GetString(bytes, 4, bytes.Length - 4);
                else if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe)
                    text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
                else if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff)
                    text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
                else
                {
                    var offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
                    text = new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
                }
            }
            catch (DecoderFallbackException)
            {
                if (!allowWindows1251)
                {
                    return false;
                }
                text = DecodeWindows1251(bytes);
            }
            return LooksLikeText(text);
        }

        private static bool LooksLikeText(string text)
        {
            if (text == null) return false;
            var sampleLength = Math.Min(text.Length, 32768);
            if (sampleLength == 0) return true;
            var controls = 0;
            for (var i = 0; i < sampleLength; i++)
            {
                var ch = text[i];
                if (ch == '\0') return false;
                if (char.IsControl(ch) && ch != '\r' && ch != '\n' && ch != '\t' && ch != '\f') controls++;
            }
            return controls * 100 <= sampleLength;
        }

        private static string DecodeWindows1251(byte[] bytes)
        {
            const string high =
                "\u0402\u0403\u201A\u0453\u201E\u2026\u2020\u2021\u20AC\u2030\u0409\u2039\u040A\u040C\u040B\u040F" +
                "\u0452\u2018\u2019\u201C\u201D\u2022\u2013\u2014\u0098\u2122\u0459\u203A\u045A\u045C\u045B\u045F" +
                "\u00A0\u040E\u045E\u0408\u00A4\u0490\u00A6\u00A7\u0401\u00A9\u0404\u00AB\u00AC\u00AD\u00AE\u0407" +
                "\u00B0\u00B1\u0406\u0456\u0491\u00B5\u00B6\u00B7\u0451\u2116\u0454\u00BB\u0458\u0405\u0455\u0457" +
                "\u0410\u0411\u0412\u0413\u0414\u0415\u0416\u0417\u0418\u0419\u041A\u041B\u041C\u041D\u041E\u041F" +
                "\u0420\u0421\u0422\u0423\u0424\u0425\u0426\u0427\u0428\u0429\u042A\u042B\u042C\u042D\u042E\u042F" +
                "\u0430\u0431\u0432\u0433\u0434\u0435\u0436\u0437\u0438\u0439\u043A\u043B\u043C\u043D\u043E\u043F" +
                "\u0440\u0441\u0442\u0443\u0444\u0445\u0446\u0447\u0448\u0449\u044A\u044B\u044C\u044D\u044E\u044F";
            var chars = new char[bytes.Length];
            for (var i = 0; i < bytes.Length; i++)
            {
                chars[i] = bytes[i] < 128 ? (char)bytes[i] : high[bytes[i] - 128];
            }
            return new string(chars);
        }

        private static bool IsImageSignature(byte[] b)
        {
            return b.Length >= 3 && b[0] == 0xff && b[1] == 0xd8 && b[2] == 0xff
                || b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4e && b[3] == 0x47
                || b.Length >= 6 && Encoding.ASCII.GetString(b, 0, 3) == "GIF"
                || b.Length >= 12 && Encoding.ASCII.GetString(b, 0, 4) == "RIFF" && Encoding.ASCII.GetString(b, 8, 4) == "WEBP";
        }

        private static bool IsWavSignature(byte[] bytes)
        {
            return bytes != null && bytes.Length >= 12 &&
                Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" &&
                Encoding.ASCII.GetString(bytes, 8, 4) == "WAVE";
        }

        private static bool IsMp3Signature(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 4)
            {
                return false;
            }
            var frameOffset = 0;
            if (bytes.Length >= 10 && Encoding.ASCII.GetString(bytes, 0, 3) == "ID3")
            {
                if (bytes[3] == 0xff || bytes[4] == 0xff ||
                    (bytes[6] & 0x80) != 0 || (bytes[7] & 0x80) != 0 ||
                    (bytes[8] & 0x80) != 0 || (bytes[9] & 0x80) != 0)
                {
                    return false;
                }
                var tagSize = bytes[6] << 21 | bytes[7] << 14 | bytes[8] << 7 | bytes[9];
                frameOffset = 10 + tagSize + ((bytes[3] == 4 && (bytes[5] & 0x10) != 0) ? 10 : 0);
            }
            return IsMp3Frame(bytes, frameOffset);
        }

        private static bool IsMp3Frame(byte[] bytes, int offset)
        {
            if (offset < 0 || bytes == null || bytes.Length - offset < 4 ||
                bytes[offset] != 0xff || (bytes[offset + 1] & 0xe0) != 0xe0)
            {
                return false;
            }
            var version = bytes[offset + 1] >> 3 & 0x03;
            var layer = bytes[offset + 1] >> 1 & 0x03;
            var bitrateIndex = bytes[offset + 2] >> 4 & 0x0f;
            var sampleRateIndex = bytes[offset + 2] >> 2 & 0x03;
            if (version == 1 || layer == 0 || bitrateIndex == 0 || bitrateIndex == 15 || sampleRateIndex == 3)
            {
                return false;
            }

            var bitrate = Mp3BitrateKbps(version, layer, bitrateIndex) * 1000;
            var sampleRates = new[] { 44100, 48000, 32000 };
            var sampleRate = sampleRates[sampleRateIndex] / (version == 3 ? 1 : (version == 2 ? 2 : 4));
            var padding = bytes[offset + 2] >> 1 & 0x01;
            var frameLength = layer == 3
                ? (12 * bitrate / sampleRate + padding) * 4
                : (version == 3 || layer == 2 ? 144 : 72) * bitrate / sampleRate + padding;
            return frameLength > 4 && bytes.Length - offset >= frameLength;
        }

        private static int Mp3BitrateKbps(int version, int layer, int index)
        {
            if (version == 3 && layer == 3)
                return new[] { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448, 0 }[index];
            if (version == 3 && layer == 2)
                return new[] { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 0 }[index];
            if (version == 3)
                return new[] { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 }[index];
            if (layer == 3)
                return new[] { 0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256, 0 }[index];
            return new[] { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0 }[index];
        }

        private static string NormalizeContentType(string kind, string supplied, byte[] bytes)
        {
            if (kind == "pdf") return "application/pdf";
            if (kind == "text") return string.IsNullOrWhiteSpace(supplied) ? "text/plain" : supplied;
            if (kind == "audio") return IsWavSignature(bytes) ? "audio/wav" : "audio/mpeg";
            if (bytes[0] == 0xff) return "image/jpeg";
            if (bytes[0] == 0x89) return "image/png";
            if (Encoding.ASCII.GetString(bytes, 0, 3) == "GIF") return "image/gif";
            return "image/webp";
        }

        private static string SafeDisplayName(string value)
        {
            var name = Path.GetFileName((value ?? string.Empty).Trim());
            return string.IsNullOrWhiteSpace(name) ? "attachment" : name.Length > 180 ? name.Substring(0, 180) : name;
        }
        private static string SafeExtension(string name, string kind)
        {
            var extension = Path.GetExtension(name).ToLowerInvariant();
            return extension.Length <= 10 && extension.All(c => char.IsLetterOrDigit(c) || c == '.') ? extension : (kind == "pdf" ? ".pdf" : ".bin");
        }
        private static bool IsSafeId(string id) { return !string.IsNullOrWhiteSpace(id) && id.Length <= 64 && id.All(char.IsLetterOrDigit); }
        private static void SafeDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        private static void SafeDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
