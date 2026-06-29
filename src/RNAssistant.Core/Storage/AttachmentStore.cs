using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RNAssistant.Core.Models;
using UglyToad.PdfPig;

namespace RNAssistant.Core.Storage
{
    public sealed class AttachmentStore
    {
        public const int MaxFilesPerMessage = 10;
        public const long MaxFileBytes = 20L * 1024L * 1024L;
        public const long MaxMessageBytes = 50L * 1024L * 1024L;
        private const int MaxExtractedChars = 100000;
        private readonly AppDataPaths _paths;

        public AttachmentStore(AppDataPaths paths)
        {
            _paths = paths ?? throw new ArgumentNullException("paths");
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
                throw new InvalidOperationException("Unsupported attachment type. Use PNG, JPEG, WebP, GIF, PDF, TXT, MD, JSON or CSV.");
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
                if (metadata == null)
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
            File.WriteAllText(Path.Combine(StagingDirectory(), attachment.Id + ".meta.json"), json, Encoding.UTF8);
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

        public void Commit(string sessionId, ChatMessage message)
        {
            if (message == null || message.Attachments == null || message.Attachments.Count == 0)
            {
                return;
            }
            var directory = MessageDirectory(sessionId, message.Id);
            Directory.CreateDirectory(directory);
            foreach (var attachment in message.Attachments)
            {
                var source = AbsolutePath(attachment.RelativePath);
                if (!File.Exists(source))
                {
                    continue;
                }
                var target = Path.Combine(directory, attachment.Id + Path.GetExtension(source));
                File.Copy(source, target, true);
                attachment.RelativePath = RelativePath(target);
                DeleteDraft(attachment.Id);
            }
        }

        public void DeleteMessage(ChatMessage message)
        {
            var attachments = message == null || message.Attachments == null
                ? (IEnumerable<ChatAttachment>)new ChatAttachment[0]
                : message.Attachments;
            foreach (var attachment in attachments)
            {
                SafeDeleteFile(AbsolutePath(attachment.RelativePath));
            }
        }

        public void DeleteSession(string sessionId)
        {
            SafeDeleteDirectory(SessionDirectory(sessionId));
        }

        public void CloneMessageAttachments(string targetSessionId, ChatMessage message)
        {
            if (message == null || message.Attachments == null)
            {
                return;
            }
            foreach (var attachment in message.Attachments)
            {
                var source = AbsolutePath(attachment.RelativePath);
                var cloneId = Guid.NewGuid().ToString("N");
                attachment.Id = cloneId;
                if (!File.Exists(source))
                {
                    attachment.Status = "missing";
                    attachment.Error = "Файл вложения отсутствует.";
                    continue;
                }
                var directory = MessageDirectory(targetSessionId, message.Id);
                Directory.CreateDirectory(directory);
                var target = Path.Combine(directory, cloneId + Path.GetExtension(source));
                File.Copy(source, target, true);
                attachment.RelativePath = RelativePath(target);
            }
        }

        public byte[] ReadBytes(ChatAttachment attachment)
        {
            if (attachment == null)
            {
                return null;
            }
            var path = AbsolutePath(attachment.RelativePath);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        private void ExtractText(ChatAttachment attachment, string path)
        {
            if (attachment.Kind == "text")
            {
                SetExtractedText(attachment, File.ReadAllText(path, Encoding.UTF8));
                return;
            }
            if (attachment.Kind != "pdf")
            {
                return;
            }

            var builder = new StringBuilder();
            using (var document = PdfDocument.Open(path))
            {
                foreach (var page in document.GetPages())
                {
                    if (builder.Length >= MaxExtractedChars)
                    {
                        break;
                    }
                    builder.AppendLine(page.Text);
                }
            }
            SetExtractedText(attachment, builder.ToString());
        }

        private static void SetExtractedText(ChatAttachment attachment, string text)
        {
            text = text ?? string.Empty;
            attachment.TextTruncated = text.Length > MaxExtractedChars;
            attachment.ExtractedText = attachment.TextTruncated ? text.Substring(0, MaxExtractedChars) : text;
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

        private string StagingDirectory() { return Path.Combine(_paths.AttachmentDirectory, "staging"); }
        private string SessionDirectory(string id) { return Path.Combine(_paths.AttachmentDirectory, "sessions", AppDataPaths.SafeFileName(id ?? string.Empty)); }
        private string MessageDirectory(string sessionId, string messageId) { return Path.Combine(SessionDirectory(sessionId), AppDataPaths.SafeFileName(messageId ?? string.Empty)); }
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

        private static string DetectKind(string name, string contentType, byte[] bytes)
        {
            var extension = Path.GetExtension(name).ToLowerInvariant();
            if (IsImageSignature(bytes)) return "image";
            if (bytes.Length >= 5 && Encoding.ASCII.GetString(bytes, 0, 5) == "%PDF-") return "pdf";
            if (new[] { ".txt", ".md", ".json", ".csv" }.Contains(extension)) return "text";
            return null;
        }

        private static bool IsImageSignature(byte[] b)
        {
            return b.Length >= 3 && b[0] == 0xff && b[1] == 0xd8 && b[2] == 0xff
                || b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4e && b[3] == 0x47
                || b.Length >= 6 && Encoding.ASCII.GetString(b, 0, 3) == "GIF"
                || b.Length >= 12 && Encoding.ASCII.GetString(b, 0, 4) == "RIFF" && Encoding.ASCII.GetString(b, 8, 4) == "WEBP";
        }

        private static string NormalizeContentType(string kind, string supplied, byte[] bytes)
        {
            if (kind == "pdf") return "application/pdf";
            if (kind == "text") return string.IsNullOrWhiteSpace(supplied) ? "text/plain" : supplied;
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
