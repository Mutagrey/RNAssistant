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
        internal const string OutlookSearchKind = "outlook-search-scope";
        internal const string OutlookArchivePageKind = "outlook-archive-page";
        internal const string OutlookArchiveMailKind = "outlook-archive-mail";
        private const string OutlookCollectionKey = "folder-collection";
        private const string OutlookCollectionLimitPrefix = "folder-collection-latest-";
        private const string OutlookCollectionTitle = "Recent mail in bound folder";
        private readonly IOutlookBackend _outlook;
        internal bool IsOutlook { get { return string.Equals(_adapter.HostName, "Outlook", StringComparison.OrdinalIgnoreCase); } }
        internal bool IsOutlookMailbox { get { return IsOutlook && (_adapter.DocumentKey ?? string.Empty).StartsWith("outlook-mailbox:", StringComparison.Ordinal); } }

        private OutlookService OutlookReader()
        {
            if (_outlook == null)
                throw new ResourceRequestException("The bound Outlook reader is unavailable.", "RESOURCE_PROVIDER_UNAVAILABLE", false);
            return new OutlookService(_outlook);
        }

        private static bool IsOutlookSearch(string key)
        {
            if (key == null || !key.StartsWith("search-latest-", StringComparison.Ordinal)) return false;
            var number = key.Substring(14);
            if (number.EndsWith("+body", StringComparison.Ordinal)) number = number.Substring(0, number.Length - 5);
            int count;
            return int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out count) && count > 0 &&
                count <= OutlookService.MaxItems && number == count.ToString(CultureInfo.InvariantCulture);
        }

        private static bool TryOutlookCollectionLimit(string key, out int maxItems)
        {
            maxItems = OutlookService.MaxItems;
            if (key == OutlookCollectionKey) return true;
            if (key == null || !key.StartsWith(OutlookCollectionLimitPrefix, StringComparison.Ordinal)) return false;
            var count = key.Substring(OutlookCollectionLimitPrefix.Length);
            return int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out maxItems) &&
                maxItems >= 1 && maxItems <= OutlookService.MaxItems &&
                count == maxItems.ToString(CultureInfo.InvariantCulture);
        }

        internal ResourceDescriptor ResolveOutlookCollection(ChatSession session, string target)
        {
            if (!IsOutlook || IsOutlookMailbox || _outlook == null)
                throw new ResourceRequestException("The bound Outlook folder is unavailable.", "RESOURCE_PROVIDER_UNAVAILABLE", false);
            const string prefix = "Outlook collection: ";
            if (target == null || !target.StartsWith(prefix, StringComparison.Ordinal))
                throw new ResourceRequestException("Use an exact Outlook collection target.", "RESOURCE_TARGET_INVALID", false);
            var title = target.Substring(prefix.Length);
            var key = title == OutlookCollectionTitle ? OutlookCollectionKey :
                title.StartsWith("latest:", StringComparison.Ordinal) ?
                    OutlookCollectionLimitPrefix + title.Substring(7) : null;
            int maxItems;
            if (!TryOutlookCollectionLimit(key, out maxItems))
                throw new ResourceRequestException("Use Outlook collection: latest:N with N from 1 to 500.", "RESOURCE_TARGET_INVALID", false);
            return _scope.Read(session, () => DescribeOutlookCollection(session, key));
        }

        internal ResourceDescriptor ResolveOutlookSearch(ChatSession session, string scope)
        {
            if (!IsOutlook || _outlook == null)
                throw new ResourceRequestException("The bound Outlook reader is unavailable.", "RESOURCE_PROVIDER_UNAVAILABLE", false);
            var key = "search-" + scope.Replace(':', '-');
            if (!IsOutlookSearch(key) || scope != key.Substring(7).Replace("latest-", "latest:"))
                throw new ResourceRequestException("Use latest:N or latest:N+body, with N from 1 to 500.", "RESOURCE_TARGET_INVALID", false);
            return _scope.Read(session, () => DescribeOutlookSearch(session, key));
        }

        private ResourceDescriptor DescribeOutlookSearch(ChatSession session, string key)
        {
            var descriptor = new ResourceDescriptor { Reference = new ResourceRef(CreateUri(session, key)), Provider = ProviderName,
                Kind = OutlookSearchKind, Title = key.Substring(7).Replace("latest-", "latest:"), Mutable = true,
                MimeType = "application/json", Tracking = "externally-observed" };
            descriptor.Representations.AddRange(new[] { "metadata", "text" });
            descriptor.Metadata["coverage"] = "Newest N folder items; +body captures at most 100000 characters per mail. Read text for truncation.";
            return descriptor;
        }

        private string ReadOutlookSearch(string key)
        {
            var includeBody = key.EndsWith("+body", StringComparison.Ordinal);
            var number = key.Substring(14);
            if (includeBody) number = number.Substring(0, number.Length - 5);
            OutlookSearchSnapshot snapshot;
            try { snapshot = OutlookReader().CaptureSearch(int.Parse(number, CultureInfo.InvariantCulture), includeBody, CancellationToken.None); }
            catch (OutlookBackendException error) { throw new ResourceRequestException(error.Message, error.ErrorCode, error.Retryable); }
            var json = new JObject { ["maximumItems"] = snapshot.MaximumItems, ["bodyCaptured"] = includeBody,
                ["maximumBodyCharacters"] = includeBody ? OutlookService.MaxSearchBodyChars : 0,
                ["folder"] = new JObject { ["totalItems"] = snapshot.Folder.TotalItems, ["truncated"] = snapshot.Folder.Truncated,
                    ["messages"] = new JArray(snapshot.Folder.Messages.Select(mail => new JObject {
                        ["subject"] = mail.Subject, ["sender"] = mail.Sender, ["senderEmail"] = mail.SenderEmail,
                        ["senderEmailTruncated"] = mail.SenderEmailTruncated,
                        ["to"] = mail.To, ["cc"] = mail.Cc, ["bcc"] = mail.Bcc,
                        ["recipientsTruncated"] = mail.RecipientsTruncated, ["received"] = mail.Received,
                        ["body"] = mail.Body, ["bodyTruncated"] = mail.BodyTruncated })) } }.ToString(Formatting.None);
            if (json.Length > MaximumMaterializedCharacters)
                throw new ResourceRequestException("Reduce maxItems or exclude body; changing the query alone does not reduce capture.",
                    "RESOURCE_SNAPSHOT_TOO_LARGE", false);
            return json;
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
            string archiveId; int archivePage;
            if (TryOutlookArchivePageKey(target, out archiveId, out archivePage))
                return ReadOutlookArchivePage(archiveId, archivePage);
            if (TryOutlookArchiveDigestKey(target, out archiveId, out archivePage))
                return ReadOutlookArchiveDigest(archiveId, archivePage);
            int archiveRow;
            if (TryOutlookArchiveMailKey(target, out archiveId, out archivePage, out archiveRow))
                return ReadOutlookArchiveMail(archiveId, archivePage, archiveRow);
            if (TryOutlookCollectionLimit(target, out var collectionLimit))
                return ReadOutlookCollection(collectionLimit);
            if (IsOutlookSearch(target)) return ReadOutlookSearch(target);
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
                    ["size"] = item.Size, ["type"] = item.Type,
                    ["target"] = "Outlook attachment: " + AttachmentTitle(mail, item) }))
            }.ToString(Formatting.None);
            if (content.Length > MaximumMaterializedCharacters)
                throw new ResourceRequestException("The complete mail view exceeds the capture limit.", "RESOURCE_SNAPSHOT_TOO_LARGE", false);
            return content;
        }

        private ResourceDescriptor DescribeOutlookCollection(ChatSession session, string key)
        {
            int maxItems;
            if (!TryOutlookCollectionLimit(key, out maxItems))
                throw new ArgumentException("Invalid Outlook collection key.", nameof(key));
            var descriptor = new ResourceDescriptor {
                Reference = new ResourceRef(CreateUri(session, key)), Provider = ProviderName,
                Kind = OutlookCollectionKind, Title = key == OutlookCollectionKey ?
                    OutlookCollectionTitle : "latest:" + maxItems.ToString(CultureInfo.InvariantCulture), Mutable = true,
                MimeType = "application/json", Tracking = "externally-observed" };
            descriptor.Representations.AddRange(new[] { "metadata", "text", "records", "table" });
            descriptor.Metadata["host"] = "Outlook";
            descriptor.Metadata["recordsPath"] = "$.messages";
            descriptor.Metadata["coverage"] = "Newest " + maxItems.ToString(CultureInfo.InvariantCulture) +
                " folder items; mail rows only. Read text for collection truncation and total folder count. " +
                "Use Outlook collection: latest:N (1-500) to bound capture before reading records.";
            descriptor.Metadata["bodyPreview"] = "At most 1000 characters; bodyTruncated is explicit. Read individual mail resources for complete bodies.";
            descriptor.Metadata["grouping"] = "month is yyyy-MM; grouping belongs to the consumer.";
            return descriptor;
        }

        private string ReadOutlookCollection(int maxItems)
        {
            OutlookFolderSnapshot snapshot;
            try { snapshot = OutlookReader().CaptureCollection(maxItems, CancellationToken.None); }
            catch (OutlookBackendException error)
            { throw new ResourceRequestException(error.Message, error.ErrorCode, error.Retryable); }
            var content = new JObject {
                ["totalFolderItems"] = snapshot.TotalItems,
                ["collectionTruncated"] = snapshot.Truncated,
                ["maximumFolderItems"] = maxItems,
                ["maximumBodyPreviewCharacters"] = OutlookService.CollectionPreviewCharacters,
                ["messages"] = new JArray(snapshot.Messages.Select(mail => new JObject {
                    ["subject"] = mail.Subject, ["sender"] = mail.Sender, ["received"] = mail.Received,
                    ["month"] = mail.Received.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                    ["bodyPreview"] = mail.Body, ["bodyTruncated"] = mail.BodyTruncated }))
            }.ToString(Formatting.None);
            if (content.Length > MaximumMaterializedCharacters)
                throw new ResourceRequestException("Outlook collection capture is too large. Read Outlook collection: latest:N with a smaller N; maxRows does not reduce capture.",
                    "RESOURCE_SNAPSHOT_TOO_LARGE", false);
            return content;
        }
    }
}
