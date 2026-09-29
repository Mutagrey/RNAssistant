using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.Outlook;

namespace RNAssistant.Office.Services
{
    internal sealed partial class LiveDocumentResourceProvider
    {
        private static string ArchiveAttachmentKey(string id, int page, int row, int attachment)
        {
            return "archive-attachment-" + id + "-" + page.ToString(CultureInfo.InvariantCulture) +
                "-" + row.ToString(CultureInfo.InvariantCulture) + "-" +
                attachment.ToString(CultureInfo.InvariantCulture);
        }

        private static bool TryOutlookArchiveAttachmentKey(string key, out string id, out int page,
            out int row, out int attachment)
        {
            id = null; page = -1; row = -1; attachment = -1;
            if (key == null || !key.StartsWith("archive-attachment-", StringComparison.Ordinal) ||
                key.Length < 89 || key[83] != '-') return false;
            id = key.Substring(19, 64);
            if (!id.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) return false;
            var parts = key.Substring(84).Split('-');
            return parts.Length == 3 &&
                int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out page) && page >= 0 &&
                int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out row) && row > 0 &&
                int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out attachment) &&
                attachment > 0 && attachment <= OutlookService.MaxAttachments &&
                ArchiveAttachmentKey(id, page, row, attachment) == key;
        }

        private static string ArchiveAttachmentTarget(OutlookArchiveIndexManifest manifest, int page,
            int row, int attachment)
        {
            return "Outlook attachment: archive " + ArchiveTitlePrefix(manifest) +
                " / page " + (page + 1).ToString(CultureInfo.InvariantCulture) +
                " / row " + row.ToString(CultureInfo.InvariantCulture) +
                " / attachment " + attachment.ToString(CultureInfo.InvariantCulture);
        }

        private ResourceDescriptor DescribeOutlookArchiveAttachment(ChatSession session, string id,
            int page, int row, int attachment)
        {
            var manifest = ArchiveManifest(id);
            var records = _archiveIndex.ReadPage(manifest, page);
            if (row < 1 || row > records.Count || attachment < 1 ||
                attachment > records[row - 1].AttachmentCount || attachment > OutlookService.MaxAttachments)
                throw new ResourceRequestException("Indexed attachment is unavailable.",
                    "outlook_archive_attachment_invalid", false);
            var descriptor = new ResourceDescriptor {
                Reference = new ResourceRef(CreateUri(session, ArchiveAttachmentKey(id, page, row, attachment))),
                Provider = ProviderName, Kind = OutlookAttachmentKind,
                Title = ArchiveAttachmentTarget(manifest, page, row, attachment)
                    .Substring("Outlook attachment: ".Length),
                Mutable = true, Tracking = "externally-observed" };
            descriptor.Representations.AddRange(new[] { "metadata", "text", "media" });
            descriptor.Metadata["host"] = "Outlook";
            descriptor.Metadata["subject"] = records[row - 1].Subject ?? string.Empty;
            descriptor.Metadata["coverage"] = "Exact indexed mail and attachment slot; bytes read on demand, up to 20 MiB. A changed or detached source fails explicitly.";
            return descriptor;
        }

        private ResourceDescriptor ResolveOutlookArchiveAttachment(ChatSession session, string target)
        {
            return _scope.Read(session, () =>
            {
                foreach (var manifest in _archiveIndex.List(_adapter.DocumentKey))
                {
                    var prefix = "Outlook attachment: archive " + ArchiveTitlePrefix(manifest) + " / page ";
                    if (!target.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var parts = target.Substring(prefix.Length).Split(new[] { " / row ", " / attachment " },
                        StringSplitOptions.None);
                    int page, row, attachment;
                    if (parts.Length != 3 || !int.TryParse(parts[0], out page) ||
                        !int.TryParse(parts[1], out row) || !int.TryParse(parts[2], out attachment)) break;
                    var descriptor = DescribeOutlookArchiveAttachment(session, manifest.Id, page - 1,
                        row, attachment);
                    if ("Outlook attachment: " + descriptor.Title == target) return descriptor;
                }
                throw new ResourceRequestException("Archive attachment target is unavailable.",
                    "outlook_archive_attachment_invalid", false);
            });
        }

        private ResourceReadSelection CaptureOutlookArchiveAttachmentSource(ChatSession session,
            string id, int page, int row, int attachment)
        {
            if (_payloads == null || !IsOutlookMailbox)
                throw new ResourceRequestException("Archive attachment storage is unavailable.",
                    "RESOURCE_PROVIDER_UNAVAILABLE", false);
            var backend = _outlook as IOutlookArchiveBackend;
            if (backend == null)
                throw new ResourceRequestException("The bound archive reader is unavailable.",
                    "RESOURCE_PROVIDER_UNAVAILABLE", false);
            var manifest = ArchiveManifest(id);
            var records = _archiveIndex.ReadPage(manifest, page);
            var descriptor = DescribeOutlookArchiveAttachment(session, id, page, row, attachment);
            var indexed = records[row - 1];
            OutlookArchiveAttachmentContent capture;
            try
            {
                capture = backend.ReadArchiveAttachment(new OutlookArchiveAttachmentRequest {
                    StoreId = indexed.StoreId, EntryId = indexed.EntryId, Index = attachment,
                    ExpectedCount = indexed.AttachmentCount,
                    ExpectedModificationUtc = indexed.LastModificationUtc,
                    IncludePst = manifest.IncludePst }, CancellationToken.None);
            }
            catch (OutlookBackendException error)
            { throw new ResourceRequestException(error.Message, error.ErrorCode, error.Retryable); }
            if (capture == null || capture.Attachment == null || capture.Attachment.Index != attachment ||
                capture.Bytes == null)
                throw new ResourceRequestException("Archive attachment capture returned no exact file.",
                    "outlook_archive_attachment_invalid", false);
            AttachmentContent content;
            try { content = AttachmentStore.ReadContent(capture.Attachment.FileName, capture.Bytes); }
            catch (Exception error) when (error is InvalidOperationException || error is IOException || error is ArgumentException)
            { throw new ResourceRequestException("Attachment content cannot be read: " + error.Message,
                "outlook_attachment_content_unavailable", false); }
            if (!string.IsNullOrEmpty(content.Warning) && content.Text != null)
                content.Text = "[PDF extraction warning: " + content.Warning + "]\n\n" + content.Text;
            if (content.Text != null && content.Text.Length > MaximumMaterializedCharacters)
                throw new ResourceRequestException("Complete attachment text exceeds the capture limit.",
                    "outlook_attachment_content_unavailable", false);
            var source = new OutlookAttachmentSource {
                Title = descriptor.Title,
                FileName = capture.Attachment.FileName, Kind = content.Kind,
                MimeType = content.ContentType, Warning = content.Warning,
                PageCount = content.PageCount, PageTextLengths = content.PageTextLengths,
                Original = PayloadRef.FromBlob(_payloads.StoreBytes(capture.Bytes, content.ContentType)),
                Text = content.Text == null ? null : PayloadRef.FromBlob(
                    _payloads.StoreText(content.Text, "text/plain; charset=utf-8")) };
            var json = JsonConvert.SerializeObject(source);
            if (json.Length > 32000)
                throw new ResourceRequestException("Attachment metadata exceeds the snapshot limit.",
                    "outlook_attachment_content_unavailable", false);
            var hash = TextPatternEngine.Sha256(json);
            descriptor.MimeType = "application/json";
            return new ResourceReadSelection { Result = new ResourceReadResult {
                Resource = descriptor, Representation = OutlookAttachmentSourceView,
                Text = json, Complete = true, ContentSha256 = hash,
                ReturnedCharacters = json.Length, TotalCharacters = json.Length,
                CompleteViewPayload = PayloadRef.FromBlob(_payloads.StoreText(json, "application/json")),
                CompleteViewParts = new[] { source.Original, source.Text }
                    .Where(item => item != null).ToArray() },
                ResourceRefs = new[] { descriptor.Reference } };
        }
    }
}
