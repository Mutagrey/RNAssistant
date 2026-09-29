using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Services
{
    internal sealed partial class LiveDocumentResourceProvider
    {
        private static bool TryOutlookArchivePageKey(string key, out string id, out int page)
        {
            id = null; page = -1;
            if (key == null || !key.StartsWith("archive-", StringComparison.Ordinal) || key.Length < 74) return false;
            var separator = key.LastIndexOf('-');
            if (separator != 72 || !int.TryParse(key.Substring(separator + 1),
                NumberStyles.None, CultureInfo.InvariantCulture, out page) || page < 0) return false;
            id = key.Substring(8, 64);
            return id.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
        }

        private static bool TryOutlookArchiveMailKey(string key, out string id, out int page, out int row)
        {
            id = null; page = -1; row = -1;
            if (key == null || !key.StartsWith("archive-mail-", StringComparison.Ordinal) || key.Length < 81) return false;
            id = key.Substring(13, 64);
            if (!id.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f') || key[77] != '-') return false;
            var separator = key.IndexOf('-', 78);
            return separator > 78 && int.TryParse(key.Substring(78, separator - 78), NumberStyles.None,
                CultureInfo.InvariantCulture, out page) && page >= 0 &&
                int.TryParse(key.Substring(separator + 1), NumberStyles.None,
                    CultureInfo.InvariantCulture, out row) && row > 0;
        }

        private OutlookArchiveIndexManifest ArchiveManifest(string id)
        {
            if (_archiveIndex == null || !IsOutlook || !_adapter.DocumentKey.StartsWith("outlook-mailbox:", StringComparison.Ordinal))
                throw new ResourceRequestException("Outlook archive index is unavailable for this target.", "RESOURCE_PROVIDER_UNAVAILABLE", false);
            var manifest = _archiveIndex.List(_adapter.DocumentKey).SingleOrDefault(item => item.Id == id);
            if (manifest == null) throw new ResourceRequestException("Outlook archive period is unavailable.", "RESOURCE_TARGET_INVALID", false);
            return manifest;
        }

        private ResourceDescriptor DescribeOutlookArchivePage(ChatSession session, string id, int page)
        {
            var manifest = ArchiveManifest(id);
            if (page >= manifest.Pages.Count)
                throw new ResourceRequestException("Outlook archive page is unavailable.", "RESOURCE_TARGET_INVALID", false);
            var key = "archive-" + id + "-" + page.ToString(CultureInfo.InvariantCulture);
            var descriptor = new ResourceDescriptor { Reference = new ResourceRef(CreateUri(session, key)),
                Provider = ProviderName, Kind = OutlookArchivePageKind,
                Title = ArchiveTitlePrefix(manifest) +
                    " / page " + (page + 1).ToString(CultureInfo.InvariantCulture),
                Mutable = true, MimeType = "application/json", Tracking = "externally-observed" };
            descriptor.Representations.AddRange(new[] { "metadata", "text" });
            descriptor.Metadata["coverage"] = manifest.Complete && manifest.Errors == 0 && manifest.FailedBodies == 0
                ? "Indexed mail bodies complete; attachment contents unexamined."
                : "Archive scan incomplete or contains read errors; attachment contents unexamined.";
            descriptor.Metadata["source"] = "Outlook mailbox and attached PST archives";
            return descriptor;
        }

        private ResourceDescriptor DescribeOutlookArchiveMail(ChatSession session, string id, int page, int row)
        {
            var manifest = ArchiveManifest(id);
            var records = _archiveIndex.ReadPage(manifest, page);
            if (row < 1 || row > records.Count || records[row - 1].Body == null)
                throw new ResourceRequestException("Indexed mail body is unavailable.", "RESOURCE_TARGET_INVALID", false);
            var key = "archive-mail-" + id + "-" + page.ToString(CultureInfo.InvariantCulture) +
                "-" + row.ToString(CultureInfo.InvariantCulture);
            var descriptor = new ResourceDescriptor { Reference = new ResourceRef(CreateUri(session, key)),
                Provider = ProviderName, Kind = OutlookArchiveMailKind,
                Title = ArchiveTitlePrefix(manifest) +
                    " / page " + (page + 1).ToString(CultureInfo.InvariantCulture) +
                    " / row " + row.ToString(CultureInfo.InvariantCulture),
                Mutable = true, MimeType = "text/plain; charset=utf-8", Tracking = "externally-observed" };
            descriptor.Representations.AddRange(new[] { "metadata", "text" });
            descriptor.Metadata["subject"] = records[row - 1].Subject ?? string.Empty;
            descriptor.Metadata["receivedUtc"] = records[row - 1].ReceivedUtc.ToString("O", CultureInfo.InvariantCulture);
            return descriptor;
        }

        internal ResourceDescriptor ResolveOutlookArchiveMail(ChatSession session, string semanticTarget)
        {
            if (_archiveIndex == null || !IsOutlook || !semanticTarget.StartsWith("Outlook archive mail: ", StringComparison.Ordinal))
                throw new ResourceRequestException("An exact archive mail target is required.", "RESOURCE_TARGET_INVALID", false);
            var title = semanticTarget.Substring("Outlook archive mail: ".Length);
            return _scope.Read(session, () =>
            {
                foreach (var manifest in _archiveIndex.List(_adapter.DocumentKey))
                {
                    var prefix = ArchiveTitlePrefix(manifest) + " / page ";
                    if (!title.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var segments = title.Substring(prefix.Length).Split(new[] { " / row " }, StringSplitOptions.None);
                    int page, row;
                    if (segments.Length != 2 || !int.TryParse(segments[0], out page) || !int.TryParse(segments[1], out row)) break;
                    var descriptor = DescribeOutlookArchiveMail(session, manifest.Id, page - 1, row);
                    if (descriptor.Title == title) return descriptor;
                }
                throw new ResourceRequestException("Archive mail target is unavailable.", "RESOURCE_TARGET_INVALID", false);
            });
        }

        internal ResourceDescriptor ResolveOutlookArchivePage(ChatSession session, string semanticTarget)
        {
            if (_archiveIndex == null || !IsOutlook)
                throw new ResourceRequestException("Outlook archive index is unavailable.", "RESOURCE_PROVIDER_UNAVAILABLE", false);
            if (!semanticTarget.StartsWith("Outlook archive page: ", StringComparison.Ordinal))
                throw new ResourceRequestException("An exact archive page target is required.", "RESOURCE_TARGET_INVALID", false);
            var title = semanticTarget.Substring("Outlook archive page: ".Length);
            return _scope.Read(session, () =>
            {
                var found = _archiveIndex.List(_adapter.DocumentKey)
                    .SelectMany(manifest => Enumerable.Range(0, manifest.Pages.Count)
                        .Select(page => DescribeOutlookArchivePage(session, manifest.Id, page)))
                    .Where(item => string.Equals(item.Title, title, StringComparison.Ordinal)).Take(2).ToArray();
                if (found.Length != 1)
                    throw new ResourceRequestException("Archive page target is missing or ambiguous.", "RESOURCE_TARGET_INVALID", false);
                return found[0];
            });
        }

        private ResourceListPage ListOutlookArchivePages(ChatSession session, string cursor, int limit)
        {
            if (_archiveIndex == null || !_adapter.DocumentKey.StartsWith("outlook-mailbox:", StringComparison.Ordinal))
                return new ResourceListPage { Items = new List<ResourceDescriptor>(), Total = 0 };
            var items = _archiveIndex.List(_adapter.DocumentKey)
                .SelectMany(manifest => Enumerable.Range(0, manifest.Pages.Count)
                    .Select(page => DescribeOutlookArchivePage(session, manifest.Id, page))).ToList();
            var binding = ResourceReadCursor.ListBinding(ProviderName, OutlookArchivePageKind);
            var position = ResourceReadCursor.ParseRevisionBound(cursor, binding);
            var revision = ResourceReadCursor.CollectionRevision(items);
            ResourceReadCursor.ValidateContinuation(position, revision);
            ResourceReadCursor.ValidateCollectionOffset(position, items.Count);
            var selected = items.Skip(position.Offset).Take(limit).ToList();
            var next = position.Offset + selected.Count;
            return new ResourceListPage { Items = selected, Total = items.Count,
                Cursor = ResourceReadCursor.CreateRevisionBound(position.Offset, revision, binding),
                NextCursor = next < items.Count ? ResourceReadCursor.CreateRevisionBound(next, revision, binding) : null,
                Truncated = next < items.Count };
        }

        private string ReadOutlookArchivePage(string id, int page)
        {
            var manifest = ArchiveManifest(id);
            var records = _archiveIndex.ReadPage(manifest, page);
            var json = new JObject {
                ["fromUtc"] = manifest.FromUtc,
                ["toUtcExclusive"] = manifest.ToUtc,
                ["scanComplete"] = manifest.Complete,
                ["examinedItems"] = manifest.ExaminedItems,
                ["indexedMessages"] = manifest.IndexedMessages,
                ["uniqueMessages"] = manifest.UniqueMessages,
                ["capturedBodies"] = manifest.CapturedBodies,
                ["failedBodies"] = manifest.FailedBodies,
                ["readErrors"] = manifest.Errors,
                ["attachmentContentsExamined"] = false,
                ["messages"] = new JArray(records.Select((item, index) => new JObject {
                    ["row"] = index + 1, ["folder"] = item.FolderPath,
                    ["subject"] = item.Subject, ["sender"] = item.Sender,
                    ["receivedUtc"] = item.ReceivedUtc,
                    ["internetMessageId"] = item.InternetMessageId,
                    ["conversationId"] = item.ConversationId,
                    ["attachmentCount"] = item.AttachmentCount,
                    ["attachmentTargets"] = new JArray(Enumerable.Range(1, Math.Min(20,
                        Math.Max(0, item.AttachmentCount))).Select(attachment =>
                        ArchiveAttachmentTarget(manifest, page, index + 1, attachment))),
                    ["attachmentTargetsTruncated"] = item.AttachmentCount > 20,
                    ["duplicate"] = item.Duplicate,
                    ["body"] = item.Body != null && item.Body.ByteLength <= 700000
                        ? _archiveIndex.ReadBody(item) : null,
                    ["bodyCaptured"] = item.Body != null,
                    ["bodyTarget"] = item.Body != null && item.Body.ByteLength > 700000
                        ? "Outlook archive mail: " + ArchiveTitlePrefix(manifest) +
                            " / page " + (page + 1).ToString(CultureInfo.InvariantCulture) +
                            " / row " + (index + 1).ToString(CultureInfo.InvariantCulture) : null,
                    ["error"] = item.Error
                }))
            }.ToString(Formatting.None);
            if (json.Length > MaximumMaterializedCharacters)
                throw new ResourceRequestException("Archive page exceeds the complete view limit.", "RESOURCE_SNAPSHOT_TOO_LARGE", false);
            return json;
        }

        private string ReadOutlookArchiveMail(string id, int page, int row)
        {
            var manifest = ArchiveManifest(id);
            var records = _archiveIndex.ReadPage(manifest, page);
            if (row < 1 || row > records.Count || records[row - 1].Body == null)
                throw new ResourceRequestException("Indexed mail body is unavailable.", "RESOURCE_TARGET_INVALID", false);
            return _archiveIndex.ReadBody(records[row - 1]);
        }

        private static string ArchiveTitlePrefix(OutlookArchiveIndexManifest manifest)
        {
            return manifest.FromUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".." +
                manifest.ToUtc.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                (manifest.IncludePst ? " / mailbox+PST" : " / mailbox");
        }

        private ResourceSearchResult SearchOutlookArchivePages(ChatSession session, string query,
            int limit, int snippetChars)
        {
            var result = new ResourceSearchResult { Query = query };
            if (_archiveIndex == null) return result;
            foreach (var manifest in _archiveIndex.List(_adapter.DocumentKey))
            {
                if (!manifest.Complete || manifest.Errors > 0 || manifest.FailedBodies > 0)
                    result.ScanTruncated = true;
                for (var page = 0; page < manifest.Pages.Count; page++)
                {
                    string content;
                    try { content = ReadOutlookArchivePage(manifest.Id, page); }
                    catch (Exception error) when (error is System.IO.IOException || error is ResourceRequestException)
                    { result.UnavailableResources++; continue; }
                    result.ScannedCharacters = (int)Math.Min(int.MaxValue,
                        (long)result.ScannedCharacters + content.Length);
                    var offset = content.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                    if (offset >= 0)
                    {
                        if (result.Matches.Count >= limit) result.ScanTruncated = true;
                        else
                        {
                            var descriptor = DescribeOutlookArchivePage(session, manifest.Id, page);
                            var hash = TextPatternEngine.Sha256(content);
                            var start = Math.Max(0, offset - snippetChars / 3);
                            result.Scans.Add(new ResourceReadResult { Resource = descriptor,
                                Representation = ResourceRepresentations.Text, Text = content,
                                ContentSha256 = hash, TotalCharacters = content.Length,
                                ReturnedCharacters = content.Length, Complete = true });
                            result.Matches.Add(new ResourceSearchMatch {
                                Reference = new ResourceRef(descriptor.Reference.Uri, hash),
                                Kind = OutlookArchivePageKind, Title = descriptor.Title,
                                Representation = ResourceRepresentations.Text,
                                MatchOffset = offset, MatchLength = query.Length,
                                SnippetOffset = start,
                                Snippet = content.Substring(start, Math.Min(snippetChars, content.Length - start)) });
                        }
                    }
                    var records = _archiveIndex.ReadPage(manifest, page);
                    for (var row = 0; row < records.Count; row++)
                    {
                        if (records[row].Body == null || records[row].Body.ByteLength <= 700000) continue;
                        string body;
                        try { body = _archiveIndex.ReadBody(records[row]); }
                        catch (System.IO.IOException) { result.UnavailableResources++; result.ScanTruncated = true; continue; }
                        result.ScannedCharacters = (int)Math.Min(int.MaxValue,
                            (long)result.ScannedCharacters + body.Length);
                        var found = body.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                        if (found < 0) continue;
                        if (result.Matches.Count >= limit) { result.ScanTruncated = true; continue; }
                        var mailDescriptor = DescribeOutlookArchiveMail(session, manifest.Id, page, row + 1);
                        var bodyHash = TextPatternEngine.Sha256(body);
                        var bodyStart = Math.Max(0, found - snippetChars / 3);
                        result.Scans.Add(new ResourceReadResult { Resource = mailDescriptor,
                            Representation = ResourceRepresentations.Text, Text = body,
                            ContentSha256 = bodyHash, TotalCharacters = body.Length,
                            ReturnedCharacters = body.Length, Complete = true });
                        result.Matches.Add(new ResourceSearchMatch {
                            Reference = new ResourceRef(mailDescriptor.Reference.Uri, bodyHash),
                            Kind = OutlookArchiveMailKind, Title = mailDescriptor.Title,
                            Representation = ResourceRepresentations.Text,
                            MatchOffset = found, MatchLength = query.Length,
                            SnippetOffset = bodyStart,
                            Snippet = body.Substring(bodyStart, Math.Min(snippetChars, body.Length - bodyStart)) });
                    }
                }
            }
            return result;
        }
    }
}
