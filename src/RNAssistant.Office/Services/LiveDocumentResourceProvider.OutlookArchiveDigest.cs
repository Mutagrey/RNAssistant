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
        internal const string OutlookArchiveDigestKind = "outlook-archive-digest";
        private const int ArchiveDigestGroupsPerPage = 50;
        private const int ArchiveDigestBodyPreview = 240;

        private sealed class ArchiveDigestRow
        {
            internal int Page;
            internal int Row;
            internal OutlookArchiveIndexedMail Mail;
        }

        private sealed class ArchiveDigestGroup
        {
            internal string Key;
            internal string ConversationId;
            internal bool Approximate;
            internal List<ArchiveDigestRow> Rows = new List<ArchiveDigestRow>();
        }

        private sealed class ArchiveDigestCache
        {
            internal string Generation;
            internal string Cursor;
            internal int PageCount;
            internal IReadOnlyList<ArchiveDigestGroup> Groups;
        }

        private readonly Dictionary<string, ArchiveDigestCache> _archiveDigest =
            new Dictionary<string, ArchiveDigestCache>(StringComparer.Ordinal);

        private static bool TryOutlookArchiveDigestKey(string key, out string id, out int page)
        {
            id = null; page = -1;
            if (key == null || !key.StartsWith("archive-digest-", StringComparison.Ordinal) ||
                key.Length < 81 || key[79] != '-') return false;
            id = key.Substring(15, 64);
            return id.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f') &&
                int.TryParse(key.Substring(80), NumberStyles.None, CultureInfo.InvariantCulture, out page) &&
                page >= 0 && key == "archive-digest-" + id + "-" + page.ToString(CultureInfo.InvariantCulture);
        }

        private IReadOnlyList<ArchiveDigestGroup> ArchiveDigestGroups(OutlookArchiveIndexManifest manifest)
        {
            ArchiveDigestCache cached;
            if (_archiveDigest.TryGetValue(manifest.Id, out cached) &&
                cached.Generation == manifest.Generation && cached.Cursor == manifest.Cursor &&
                cached.PageCount == manifest.Pages.Count) return cached.Groups;
            var groups = new Dictionary<string, ArchiveDigestGroup>(StringComparer.Ordinal);
            for (var page = 0; page < manifest.Pages.Count; page++)
            {
                var records = _archiveIndex.ReadPage(manifest, page);
                for (var row = 0; row < records.Count; row++)
                {
                    var mail = records[row];
                    if (mail.Duplicate) continue;
                    var conversationId = (mail.ConversationId ?? string.Empty).Trim();
                    var approximate = conversationId.Length == 0;
                    var subject = (mail.Subject ?? string.Empty).Trim().ToLowerInvariant();
                    var sender = (mail.Sender ?? string.Empty).Trim().ToLowerInvariant();
                    var identity = approximate ? "subject:" + subject + "|sender:" + sender :
                        "conversation:" + conversationId;
                    if (approximate && subject.Length == 0)
                        identity += "|store:" + mail.StoreId + "|entry:" + mail.EntryId;
                    ArchiveDigestGroup group;
                    if (!groups.TryGetValue(identity, out group))
                    {
                        group = new ArchiveDigestGroup { Key = TextPatternEngine.Sha256(identity),
                            ConversationId = conversationId, Approximate = approximate };
                        groups.Add(identity, group);
                    }
                    group.Rows.Add(new ArchiveDigestRow { Page = page, Row = row + 1, Mail = mail });
                }
            }
            var ordered = groups.Values.OrderByDescending(group => group.Rows.Count)
                .ThenByDescending(group => group.Rows.Max(row => row.Mail.ReceivedUtc))
                .ThenBy(group => group.Key, StringComparer.Ordinal).ToArray();
            _archiveDigest[manifest.Id] = new ArchiveDigestCache {
                Generation = manifest.Generation, Cursor = manifest.Cursor,
                PageCount = manifest.Pages.Count, Groups = ordered };
            return ordered;
        }

        private ResourceDescriptor DescribeOutlookArchiveDigest(ChatSession session, string id, int page)
        {
            var manifest = ArchiveManifest(id);
            var groups = ArchiveDigestGroups(manifest);
            if (page < 0 || page >= (groups.Count + ArchiveDigestGroupsPerPage - 1) /
                ArchiveDigestGroupsPerPage)
                throw new ResourceRequestException("Archive digest page is unavailable.",
                    "RESOURCE_TARGET_INVALID", false);
            var descriptor = new ResourceDescriptor {
                Reference = new ResourceRef(CreateUri(session, "archive-digest-" + id + "-" +
                    page.ToString(CultureInfo.InvariantCulture))),
                Provider = ProviderName, Kind = OutlookArchiveDigestKind,
                Title = ArchiveTitlePrefix(manifest) + " / page " +
                    (page + 1).ToString(CultureInfo.InvariantCulture),
                Mutable = true, MimeType = "application/json", Tracking = "externally-observed" };
            descriptor.Representations.AddRange(new[] { "metadata", "text" });
            descriptor.Metadata["coverage"] = "Activity ranking by distinct indexed mail count; grouped by ConversationID or approximate subject/sender. Samples are incomplete; inspect source pages before conclusions.";
            return descriptor;
        }

        internal ResourceDescriptor ResolveOutlookArchiveDigest(ChatSession session, string target)
        {
            if (_archiveIndex == null || !IsOutlookMailbox ||
                !target.StartsWith("Outlook archive digest: ", StringComparison.Ordinal))
                throw new ResourceRequestException("An exact archive digest target is required.",
                    "RESOURCE_TARGET_INVALID", false);
            return _scope.Read(session, () =>
            {
                foreach (var manifest in _archiveIndex.List(_adapter.DocumentKey))
                {
                    var prefix = "Outlook archive digest: " + ArchiveTitlePrefix(manifest) + " / page ";
                    if (!target.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    int page;
                    if (!int.TryParse(target.Substring(prefix.Length), NumberStyles.None,
                        CultureInfo.InvariantCulture, out page)) break;
                    var descriptor = DescribeOutlookArchiveDigest(session, manifest.Id, page - 1);
                    if ("Outlook archive digest: " + descriptor.Title == target) return descriptor;
                }
                throw new ResourceRequestException("Archive digest target is unavailable.",
                    "RESOURCE_TARGET_INVALID", false);
            });
        }

        private ResourceListPage ListOutlookArchiveDigests(ChatSession session, string cursor, int limit)
        {
            if (_archiveIndex == null || !IsOutlookMailbox)
                return new ResourceListPage { Items = new List<ResourceDescriptor>(), Total = 0 };
            var items = _archiveIndex.List(_adapter.DocumentKey)
                .SelectMany(manifest => Enumerable.Range(0,
                    (ArchiveDigestGroups(manifest).Count + ArchiveDigestGroupsPerPage - 1) /
                    ArchiveDigestGroupsPerPage)
                    .Select(page => DescribeOutlookArchiveDigest(session, manifest.Id, page))).ToList();
            var binding = ResourceReadCursor.ListBinding(ProviderName, OutlookArchiveDigestKind);
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

        private string ReadOutlookArchiveDigest(string id, int page)
        {
            var manifest = ArchiveManifest(id);
            var groups = ArchiveDigestGroups(manifest);
            if (page < 0 || page >= (groups.Count + ArchiveDigestGroupsPerPage - 1) /
                ArchiveDigestGroupsPerPage)
                throw new ResourceRequestException("Archive digest page is unavailable.",
                    "RESOURCE_TARGET_INVALID", false);
            var first = page * ArchiveDigestGroupsPerPage;
            var digestPageCount = (groups.Count + ArchiveDigestGroupsPerPage - 1) /
                ArchiveDigestGroupsPerPage;
            var json = new JObject {
                ["fromUtc"] = manifest.FromUtc, ["toUtcExclusive"] = manifest.ToUtc,
                ["scanComplete"] = manifest.Complete, ["readErrors"] = manifest.Errors,
                ["failedBodies"] = manifest.FailedBodies, ["attachmentContentsExamined"] = false,
                ["rankBasis"] = "Distinct indexed message count; not decision importance or severity",
                ["groupCount"] = groups.Count, ["groupOffset"] = first,
                ["digestPageCount"] = digestPageCount,
                ["nextPageTarget"] = page + 1 < digestPageCount
                    ? "Outlook archive digest: " + ArchiveTitlePrefix(manifest) + " / page " +
                        (page + 2).ToString(CultureInfo.InvariantCulture) : null,
                ["groups"] = new JArray(groups.Skip(first).Take(ArchiveDigestGroupsPerPage)
                    .Select((group, offset) => DigestGroupJson(group, first + offset + 1)))
            }.ToString(Formatting.None);
            if (json.Length > MaximumMaterializedCharacters)
                throw new ResourceRequestException("Archive digest exceeds the complete view limit.",
                    "RESOURCE_SNAPSHOT_TOO_LARGE", false);
            return json;
        }

        private JObject DigestGroupJson(ArchiveDigestGroup group, int rank)
        {
            var rows = group.Rows.OrderBy(row => row.Mail.ReceivedUtc).ThenBy(row => row.Page)
                .ThenBy(row => row.Row).ToArray();
            var samples = rows.Take(3).Concat(rows.Skip(Math.Max(3, rows.Length - 3)))
                .Distinct().ToArray();
            return new JObject {
                ["activityRank"] = rank, ["groupKey"] = group.Key,
                ["groupBasis"] = group.Approximate ? "subject+sender (approximate)" : "ConversationID",
                ["conversationId"] = group.ConversationId.Length == 0 ? null : group.ConversationId,
                ["subject"] = rows.Select(row => row.Mail.Subject).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                ["messageCount"] = rows.Length,
                ["firstUtc"] = rows[0].Mail.ReceivedUtc,
                ["lastUtc"] = rows[rows.Length - 1].Mail.ReceivedUtc,
                ["senders"] = new JArray(rows.Select(row => row.Mail.Sender).Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(10)),
                ["senderListTruncated"] = rows.Select(row => row.Mail.Sender).Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Skip(10).Any(),
                ["attachmentCount"] = rows.Sum(row => (long)row.Mail.AttachmentCount),
                ["sampleTruncated"] = samples.Length < rows.Length,
                ["sampleMessages"] = new JArray(samples.Select(DigestSampleJson))
            };
        }

        private JObject DigestSampleJson(ArchiveDigestRow row)
        {
            string preview = null;
            bool previewTruncated = false;
            if (row.Mail.Body != null && row.Mail.Body.ByteLength <= 50000)
            {
                var body = _archiveIndex.ReadBody(row.Mail);
                preview = body.Substring(0, Math.Min(ArchiveDigestBodyPreview, body.Length));
                previewTruncated = body.Length > preview.Length;
            }
            else if (row.Mail.Body != null) previewTruncated = true;
            return new JObject {
                ["page"] = row.Page + 1, ["row"] = row.Row,
                ["receivedUtc"] = row.Mail.ReceivedUtc,
                ["subject"] = row.Mail.Subject, ["sender"] = row.Mail.Sender,
                ["bodyPreview"] = preview, ["bodyPreviewTruncated"] = previewTruncated,
                ["bodyCaptured"] = row.Mail.Body != null
            };
        }
    }
}
