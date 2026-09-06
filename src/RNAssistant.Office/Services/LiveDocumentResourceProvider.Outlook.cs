using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Domains.Outlook;

namespace RNAssistant.Office.Services
{
    internal sealed partial class LiveDocumentResourceProvider
    {
        internal const string OutlookMailKind = "outlook-mail";
        internal const string OutlookCollectionKind = "outlook-collection";
        private const string OutlookCollectionKey = "folder-collection";
        private readonly IOutlookBackend _outlook;
        internal bool IsOutlook { get { return string.Equals(_adapter.HostName, "Outlook", StringComparison.OrdinalIgnoreCase); } }

        private OutlookService OutlookReader()
        {
            if (_outlook == null)
                throw new ResourceRequestException("The bound Outlook reader is unavailable.", "RESOURCE_PROVIDER_UNAVAILABLE", false);
            return new OutlookService(_outlook);
        }

        private static string OutlookMailKey(string entryId)
        {
            return string.IsNullOrEmpty(entryId) ? "mail-bound" :
                "mail-" + Convert.ToBase64String(Encoding.UTF8.GetBytes(entryId)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static bool TryOutlookMailKey(string target, out string entryId)
        {
            entryId = null;
            if (target == "mail-bound") return true;
            if (target == null || !target.StartsWith("mail-", StringComparison.Ordinal) || target.Length > 22000) return false;
            try
            {
                var encoded = target.Substring(5).Replace('-', '+').Replace('_', '/');
                encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
                entryId = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(encoded));
                return entryId.Length > 0 && entryId.Length <= 4096 && OutlookMailKey(entryId) == target;
            }
            catch (ArgumentException) { return false; }
            catch (FormatException) { return false; }
        }

        private ResourceDescriptor DescribeOutlookMail(ChatSession session, string key, OutlookMailSummarySnapshot mail)
        {
            var descriptor = new ResourceDescriptor { Reference = new ResourceRef(CreateUri(session, key)),
                Provider = ProviderName, Kind = OutlookMailKind, Mutable = true, MimeType = "text/plain; charset=utf-8",
                Title = mail.Subject + " — " + mail.Sender + " — " + mail.Received.ToString("s", CultureInfo.InvariantCulture) };
            descriptor.Representations.AddRange(new[] { ResourceRepresentations.Metadata, ResourceRepresentations.Text,
                ResourceRepresentations.Source, ResourceRepresentations.Structure });
            descriptor.Metadata["host"] = "Outlook";
            descriptor.Metadata["live"] = "true";
            return descriptor;
        }

        private ResourceListPage ListOutlookMail(ChatSession session, string cursor, int limit)
        {
            try
            {
                var capture = OutlookReader().DiscoverMail(CancellationToken.None);
                var items = capture.Items.Select(mail => DescribeOutlookMail(session, OutlookMailKey(mail.EntryId), mail)).ToList();
                var binding = ResourceReadCursor.ListBinding(ProviderName, OutlookMailKind);
                var position = ResourceReadCursor.ParseRevisionBound(cursor, binding);
                var revision = ResourceReadCursor.CollectionRevision(items);
                ResourceReadCursor.ValidateContinuation(position, revision);
                ResourceReadCursor.ValidateCollectionOffset(position, items.Count);
                var selected = items.Skip(position.Offset).Take(limit).ToList();
                var next = position.Offset + selected.Count;
                return new ResourceListPage { Items = selected, Total = items.Count,
                    Cursor = ResourceReadCursor.CreateRevisionBound(position.Offset, revision, binding),
                    NextCursor = next < items.Count ? ResourceReadCursor.CreateRevisionBound(next, revision, binding) : null,
                    Truncated = capture.Truncated || next < items.Count };
            }
            catch (OutlookBackendException error)
            { throw new ResourceRequestException(error.Message, error.ErrorCode, error.Retryable); }
        }

        private OutlookMailReadSnapshot CaptureOutlookMail(string target, bool includeBody)
        {
            string entryId = null;
            if (target != "root" && target != "selection" && !TryOutlookMailKey(target, out entryId))
                throw new ResourceRequestException("Invalid Outlook resource target.", "RESOURCE_TARGET_INVALID", false);
            try
            {
                return OutlookReader().CaptureMail(new OutlookReadMailRequest { EntryId = entryId,
                    BoundMailOnly = target == "mail-bound", Content = includeBody ? "both" : "attachments",
                    MaxChars = OutlookService.MaxBodyChars }, CancellationToken.None);
            }
            catch (OutlookBackendException error)
            { throw new ResourceRequestException(error.Message, error.ErrorCode, error.Retryable); }
        }

        private string ReadOutlookSource(string target, string representation)
        {
            if (target == OutlookCollectionKey) return ReadOutlookCollection();
            var snapshot = CaptureOutlookMail(target, representation != ResourceRepresentations.Structure);
            var mail = snapshot.Mail;
            var content = representation == ResourceRepresentations.Text ? mail.Body : new JObject
            {
                ["message"] = new JObject { ["subject"] = mail.Subject, ["sender"] = mail.Sender,
                    ["senderEmail"] = mail.SenderEmail, ["to"] = mail.To, ["cc"] = mail.Cc, ["bcc"] = mail.Bcc,
                    ["received"] = mail.Received, ["categories"] = mail.Categories, ["unread"] = mail.Unread,
                    ["body"] = mail.Body },
                ["bodyCaptured"] = snapshot.BodyCaptured,
                ["attachments"] = new JArray(snapshot.Attachments.Select(item => new JObject {
                    ["index"] = item.Index, ["fileName"] = item.FileName, ["displayName"] = item.DisplayName,
                    ["size"] = item.Size, ["type"] = item.Type }))
            }.ToString(Formatting.None);
            if (content.Length > MaximumMaterializedCharacters)
                throw new ResourceRequestException("The complete mail view exceeds the capture limit.", "RESOURCE_SNAPSHOT_TOO_LARGE", false);
            return content;
        }

        private ResourceDescriptor DescribeOutlookCollection(ChatSession session)
        {
            var descriptor = new ResourceDescriptor {
                Reference = new ResourceRef(CreateUri(session, OutlookCollectionKey)), Provider = ProviderName,
                Kind = OutlookCollectionKind, Title = "Recent mail in bound folder", Mutable = true,
                MimeType = "application/json", Tracking = "externally-observed" };
            descriptor.Representations.AddRange(new[] { "metadata", "text", "records", "table" });
            descriptor.Metadata["host"] = "Outlook";
            descriptor.Metadata["recordsPath"] = "$.messages";
            descriptor.Metadata["coverage"] = "Newest 500 folder items; mail rows only. Read text for collection truncation and total folder count.";
            descriptor.Metadata["bodyPreview"] = "At most 1000 characters; bodyTruncated is explicit. Read individual mail resources for complete bodies.";
            descriptor.Metadata["grouping"] = "month is yyyy-MM; grouping belongs to the consumer.";
            return descriptor;
        }

        private string ReadOutlookCollection()
        {
            OutlookFolderSnapshot snapshot;
            try { snapshot = OutlookReader().CaptureCollection(CancellationToken.None); }
            catch (OutlookBackendException error)
            { throw new ResourceRequestException(error.Message, error.ErrorCode, error.Retryable); }
            var content = new JObject {
                ["totalFolderItems"] = snapshot.TotalItems,
                ["collectionTruncated"] = snapshot.Truncated,
                ["maximumFolderItems"] = OutlookService.MaxItems,
                ["maximumBodyPreviewCharacters"] = OutlookService.CollectionPreviewCharacters,
                ["messages"] = new JArray(snapshot.Messages.Select(mail => new JObject {
                    ["subject"] = mail.Subject, ["sender"] = mail.Sender, ["received"] = mail.Received,
                    ["month"] = mail.Received.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                    ["bodyPreview"] = mail.Body, ["bodyTruncated"] = mail.BodyTruncated }))
            }.ToString(Formatting.None);
            if (content.Length > MaximumMaterializedCharacters)
                throw new ResourceRequestException("The mail collection exceeds the serialized snapshot limit.", "RESOURCE_SNAPSHOT_TOO_LARGE", false);
            return content;
        }
    }
}
