using System;
using System.Collections.Generic;
using System.Globalization;
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
    // Private source manifest: runtime-owned payload references never enter model text.
    internal sealed class OutlookAttachmentSource
    {
        public string Title { get; set; }
        public string FileName { get; set; }
        public string Kind { get; set; }
        public string MimeType { get; set; }
        public string Warning { get; set; }
        public int PageCount { get; set; }
        public List<int> PageTextLengths { get; set; }
        public PayloadRef Original { get; set; }
        public PayloadRef Text { get; set; }
    }

    internal sealed partial class LiveDocumentResourceProvider
    {
        internal const string OutlookAttachmentKind = "outlook-attachment";
        internal const string OutlookAttachmentSourceView = "outlook-attachment-source";

        private static string AttachmentKey(string mailKey, OutlookAttachmentSnapshot attachment)
        { return "attachment-" + attachment.Index.ToString(CultureInfo.InvariantCulture) + "-" +
            TextPatternEngine.Sha256(JsonConvert.SerializeObject(attachment)) + "-" + mailKey; }

        private static bool TryAttachmentKey(string key, out string mailKey, out int index)
        {
            mailKey = null; index = 0;
            if (key == null || !key.StartsWith("attachment-", StringComparison.Ordinal)) return false;
            var separator = key.IndexOf('-', 11);
            string entryId;
            if (separator < 0 || !int.TryParse(key.Substring(11, separator - 11), NumberStyles.None,
                CultureInfo.InvariantCulture, out index) || index < 1 || index > OutlookService.MaxAttachments) return false;
            if (key.Length < separator + 67 || key[separator + 65] != '-' ||
                key.Substring(separator + 1, 64).Any(c => c < '0' || c > '9' && c < 'a' || c > 'f') ||
                key.Substring(11, separator - 11) != index.ToString(CultureInfo.InvariantCulture)) return false;
            mailKey = key.Substring(separator + 66);
            return TryOutlookMailKey(mailKey, out entryId);
        }

        internal bool IsOutlookAttachment(string uri)
        {
            ResourceAddress address;
            string mailKey; int index;
            return IsOutlook && ResourceUri.TryParse(uri, out address) && address.Provider == ProviderName &&
                address.Segments.Count == 2 && TryAttachmentKey(address.Segments[1], out mailKey, out index);
        }

        private static string MailTitle(OutlookMailSnapshot mail)
        { return mail.Subject + " — " + mail.Sender + " — " + mail.Received.ToString("s", CultureInfo.InvariantCulture); }

        private static string AttachmentTitle(OutlookMailSnapshot mail, OutlookAttachmentSnapshot attachment)
        { return MailTitle(mail) + " / " + attachment.Index.ToString(CultureInfo.InvariantCulture) + ": " + attachment.FileName; }

        private ResourceDescriptor DescribeAttachment(ChatSession session, string key, OutlookMailSnapshot mail, OutlookAttachmentSnapshot attachment)
        {
            var descriptor = new ResourceDescriptor { Reference = new ResourceRef(CreateUri(session, key)),
                Provider = ProviderName, Kind = OutlookAttachmentKind, Title = AttachmentTitle(mail, attachment),
                Mutable = true, Tracking = "externally-observed" };
            descriptor.Representations.AddRange(new[] { "metadata", "text", "media" });
            descriptor.Metadata["host"] = "Outlook";
            descriptor.Metadata["fileName"] = attachment.FileName;
            descriptor.Metadata["attachmentType"] = attachment.Type;
            descriptor.Metadata["size"] = attachment.Size.ToString(CultureInfo.InvariantCulture);
            descriptor.Metadata["coverage"] = "One file attachment, up to 20 MiB. Text/PDF: text; image or visual PDF: media. Unsupported views fail explicitly.";
            return descriptor;
        }

        private ResourceDescriptor DescribeAttachment(ChatSession session, string key)
        {
            string mailKey; int index;
            if (!TryAttachmentKey(key, out mailKey, out index)) throw AttachmentError("Invalid attachment target.");
            var snapshot = CaptureOutlookMail(mailKey, false);
            var attachment = snapshot.Attachments.SingleOrDefault(item => item.Index == index);
            if (attachment == null || AttachmentKey(mailKey, attachment) != key) throw AttachmentError("Attachment was removed or replaced; rediscover its current target.");
            return DescribeAttachment(session, key, snapshot.Mail, attachment);
        }

        // Discovery is deliberately selected-mail only. Other mails advertise exact attachment targets in structure/source.
        private ResourceListPage ListOutlookAttachments(ChatSession session, string cursor, int limit)
        {
            var discovered = OutlookReader().DiscoverMail(CancellationToken.None);
            OutlookMailReadSnapshot snapshot;
            try { snapshot = CaptureOutlookMail("selection", false); }
            catch (ResourceRequestException error) when (error.ErrorCode == "outlook_mail_not_found")
            { return new ResourceListPage { Items = new List<ResourceDescriptor>(), Total = 0 }; }
            var key = OutlookMailKey(snapshot.Mail.EntryId);
            if (key == "mail-bound" && !discovered.BoundMail) throw AttachmentError("Unsaved mail requires its bound Inspector.");
            var items = snapshot.Attachments.Select(item => DescribeAttachment(session, AttachmentKey(key, item), snapshot.Mail, item)).ToList();
            var binding = ResourceReadCursor.ListBinding(ProviderName, OutlookAttachmentKind);
            var position = ResourceReadCursor.ParseRevisionBound(cursor, binding);
            var revision = ResourceReadCursor.CollectionRevision(items);
            ResourceReadCursor.ValidateContinuation(position, revision);
            ResourceReadCursor.ValidateCollectionOffset(position, items.Count);
            var selected = items.Skip(position.Offset).Take(limit).ToList();
            var next = position.Offset + selected.Count;
            return new ResourceListPage { Items = selected, Total = items.Count,
                NextCursor = next < items.Count ? ResourceReadCursor.CreateRevisionBound(next, revision, binding) : null,
                Truncated = next < items.Count || !discovered.BoundMail };
        }

        internal ResourceDescriptor ResolveOutlookAttachmentTarget(ChatSession session, string target)
        {
            return _scope.Read(session, () =>
            {
                var matches = new List<ResourceDescriptor>();
                var discovery = OutlookReader().DiscoverMail(CancellationToken.None);
                foreach (var mail in discovery.Items)
                {
                    var title = mail.Subject + " — " + mail.Sender + " — " + mail.Received.ToString("s", CultureInfo.InvariantCulture);
                    if (!target.StartsWith("Outlook attachment: " + title + " / ", StringComparison.Ordinal)) continue;
                    var key = OutlookMailKey(mail.EntryId);
                    var snapshot = CaptureOutlookMail(key, false);
                    foreach (var attachment in snapshot.Attachments)
                    {
                        var descriptor = DescribeAttachment(session, AttachmentKey(key, attachment), snapshot.Mail, attachment);
                        if ("Outlook attachment: " + descriptor.Title == target) matches.Add(descriptor);
                    }
                }
                if (matches.Count != 1 || discovery.Truncated)
                    throw AttachmentError("Attachment target is missing, ambiguous or outside complete discovery; open its mail in an Inspector and rediscover.");
                return matches[0];
            });
        }

        private ResourceReadSelection CaptureAttachmentSource(ChatSession session, string key)
        {
            string mailKey; int index; string entryId;
            if (_payloads == null || !TryAttachmentKey(key, out mailKey, out index) || !TryOutlookMailKey(mailKey, out entryId))
                throw AttachmentError("Attachment source is unavailable.");
            var mail = CaptureOutlookMail(mailKey, false);
            var attachment = mail.Attachments.SingleOrDefault(item => item.Index == index);
            if (attachment == null || AttachmentKey(mailKey, attachment) != key) throw AttachmentError("Attachment changed after target resolution.");
            OutlookAttachmentContentSnapshot capture;
            try { capture = OutlookReader().CaptureAttachment(new OutlookAttachmentReadRequest {
                EntryId = entryId, BoundMailOnly = mailKey == "mail-bound", Expected = attachment }, CancellationToken.None); }
            catch (OutlookBackendException error) { throw new ResourceRequestException(error.Message, error.ErrorCode, error.Retryable); }
            AttachmentContent content;
            try { content = AttachmentStore.ReadContent(attachment.FileName, capture.Bytes); }
            catch (Exception error) when (error is InvalidOperationException || error is System.IO.IOException || error is ArgumentException)
            { throw new ResourceRequestException("Attachment content cannot be read: " + error.Message, "outlook_attachment_content_unavailable", false); }
            if (!string.IsNullOrEmpty(content.Warning) && content.Text != null)
                content.Text = "[PDF extraction warning: " + content.Warning + "]\n\n" + content.Text;
            if (content.Text != null && content.Text.Length > MaximumMaterializedCharacters)
                throw AttachmentError("The complete attachment text exceeds the capture limit.");
            var source = new OutlookAttachmentSource { Title = AttachmentTitle(mail.Mail, attachment), FileName = attachment.FileName,
                Kind = content.Kind, MimeType = content.ContentType, Warning = content.Warning, PageCount = content.PageCount, PageTextLengths = content.PageTextLengths,
                Original = PayloadRef.FromBlob(_payloads.StoreBytes(capture.Bytes, content.ContentType)),
                Text = content.Text == null ? null : PayloadRef.FromBlob(_payloads.StoreText(content.Text, "text/plain; charset=utf-8")) };
            var json = JsonConvert.SerializeObject(source);
            if (json.Length > 32000) throw AttachmentError("Attachment metadata exceeds the snapshot limit.");
            var hash = TextPatternEngine.Sha256(json);
            var descriptor = DescribeAttachment(session, key, mail.Mail, attachment);
            descriptor.MimeType = "application/json";
            return new ResourceReadSelection { Result = new ResourceReadResult { Resource = descriptor,
                Representation = OutlookAttachmentSourceView, Text = json, Complete = true, ContentSha256 = hash,
                ReturnedCharacters = json.Length, TotalCharacters = json.Length,
                CompleteViewPayload = PayloadRef.FromBlob(_payloads.StoreText(json, "application/json")),
                CompleteViewParts = new[] { source.Original, source.Text }.Where(item => item != null).ToArray() },
                ResourceRefs = new[] { descriptor.Reference } };
        }

        private static ResourceRequestException AttachmentError(string message)
        { return new ResourceRequestException(message, "outlook_attachment_target_unavailable", false); }
    }
}
