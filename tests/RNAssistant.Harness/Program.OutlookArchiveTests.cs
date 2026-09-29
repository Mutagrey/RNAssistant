using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Domains.Outlook;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void OutlookArchiveIndexResumesExactPages()
        {
            WithTempPaths(paths =>
            {
                var index = new OutlookArchiveIndexService(paths, new ChatBlobStore(paths));
                var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var to = from.AddMonths(6);
                const string mailbox = "outlook-mailbox:test";
                var manifest = index.Open(mailbox, from, to, true);
                index.Append(manifest, new OutlookArchiveScanBatch {
                    SourceSignature = "sources-a", NextCursor = "0:2", ExaminedItems = 1,
                    Messages = new[] { new OutlookArchiveMail { StoreId = "store-a", EntryId = "mail-1",
                    FolderPath = "\\Inbox", Subject = "Pump failure", Sender = "Ops",
                        ReceivedUtc = from.AddDays(1), Body = "Pump failed; repair approved.", AttachmentCount = 1,
                        InternetMessageId = "<pump@example.test>" } },
                    Errors = new string[0]
                });
                var resumed = new OutlookArchiveIndexService(paths, new ChatBlobStore(paths))
                    .Open(mailbox, from, to, true);
                AssertEqual("0:2", resumed.Cursor, "scan cursor survives restart");
                AssertEqual(1, resumed.Pages.Count, "exact page stored");
                var page = index.ReadPage(resumed, 0);
                AssertEqual("Pump failed; repair approved.", index.ReadBody(page[0]), "full body survives restart");
                AssertEqual(1, resumed.AttachmentCandidates, "unexamined attachment count retained");
                var mismatch = false;
                try { index.Append(resumed, new OutlookArchiveScanBatch { SourceSignature = "changed",
                    NextCursor = "0:3", Messages = new OutlookArchiveMail[0], Errors = new string[0] }); }
                catch (InvalidOperationException) { mismatch = true; }
                AssertTrue(mismatch, "changed source set cannot continue the checkpoint");
                var stale = index.Open(mailbox, from, to, true);
                index.Append(resumed, new OutlookArchiveScanBatch { SourceSignature = "sources-a",
                    Complete = true, ExaminedItems = 2, Messages = new[] { new OutlookArchiveMail {
                        StoreId = "pst-a", EntryId = "copy-1", FolderPath = "\\Archive",
                        Subject = "Pump failure", Sender = "Ops", ReceivedUtc = from.AddDays(1),
                        Body = "Pump failed; repair approved.", InternetMessageId = "<pump@example.test>" } },
                    Errors = new[] { "Unreadable folder" } });
                var complete = index.Open(mailbox, from, to, true);
                AssertTrue(complete.Complete, "terminal checkpoint saved");
                AssertEqual(1, complete.Errors, "coverage error retained");
                AssertEqual(2, complete.IndexedMessages, "copies remain available as sources");
                AssertEqual(1, complete.UniqueMessages, "matching message id and body are deduplicated");
                var raced = false;
                try { index.Append(stale, new OutlookArchiveScanBatch { SourceSignature = "sources-a",
                    NextCursor = "0:4", Messages = new OutlookArchiveMail[0], Errors = new string[0] }); }
                catch (InvalidOperationException) { raced = true; }
                AssertTrue(raced, "stale writer cannot replace a newer checkpoint");
                var maintenance = CasService(paths, new ChatStore(paths), new VbaJournalStore(paths),
                    () => StorageProtector.None);
                AssertTrue(maintenance.Audit().CanGarbageCollect, "archive pointer can be scanned for CAS reachability");
                AssertTrue(maintenance.Collect().Completed, "CAS collection completes with archive index");
                AssertEqual("Pump failed; repair approved.", index.ReadBody(index.ReadPage(complete, 0)[0]),
                    "archive body survives CAS collection");
                AssertTrue(!index.Open(mailbox, from, to, true, true).Complete,
                    "explicit refresh starts a new scan");
            });
        }

        private static void OutlookArchivePagesSearchAndRead()
        {
            WithTempPaths(paths =>
            {
                var adapter = FakeOfficeAdapter.ForHost("Outlook");
                adapter.DocumentKeyValue = "outlook-mailbox:test";
                var session = NewSession(adapter);
                session.DocumentAuthorityId = RNAssistant.Core.Models.DocumentAuthorityId.Create().Id;
                var blobs = new ChatBlobStore(paths);
                var index = new OutlookArchiveIndexService(paths, blobs);
                var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var manifest = index.Open(adapter.DocumentKey, from, from.AddMonths(1), true);
                index.Append(manifest, new OutlookArchiveScanBatch { SourceSignature = "sources-a",
                    Complete = true, ExaminedItems = 2,
                    Messages = new[] { new OutlookArchiveMail { StoreId = "store-a", EntryId = "mail-1",
                        FolderPath = "\\Inbox", Subject = "Pump failure", Sender = "Ops",
                        ReceivedUtc = from.AddDays(1), Body = "Repair was approved on Monday." } },
                    Errors = new string[0] });
                var provider = new LiveDocumentResourceProvider(adapter, blobs, index);
                var listed = provider.List(session, LiveDocumentResourceProvider.OutlookArchivePageKind, null, 20);
                AssertEqual(1, listed.Items.Count, "archive page discoverable");
                var read = provider.Read(session, new RNAssistant.Core.Models.ResourceReadRequest {
                    Reference = listed.Items[0].Reference, Representation = "text" });
                AssertContains(read.Result.Text, "Repair was approved", "full indexed body in resource");
                AssertContains(read.Result.Text, "attachmentContentsExamined", "coverage in resource");
                var search = provider.Search(session, "approved", LiveDocumentResourceProvider.OutlookArchivePageKind, 10, 200);
                AssertEqual(1, search.Matches.Count, "indexed body searched");
                AssertEqual(1, search.Scans.Count, "exact source accompanies match");
                var longManifest = index.Open(adapter.DocumentKey, from.AddMonths(1), from.AddMonths(2), true);
                index.Append(longManifest, new OutlookArchiveScanBatch { SourceSignature = "sources-a",
                    Complete = true, ExaminedItems = 1,
                    Messages = new[] { new OutlookArchiveMail { StoreId = "store-a", EntryId = "mail-2",
                        FolderPath = "\\Sent", Subject = "Large report", Sender = "Ops",
                        ReceivedUtc = from.AddMonths(1).AddDays(1),
                        Body = new string('x', 710000) + " oil decision needle" } }, Errors = new string[0] });
                var longPage = provider.List(session, LiveDocumentResourceProvider.OutlookArchivePageKind, null, 20)
                    .Items.Single(item => item.Title.Contains("2026-02-01"));
                var longText = provider.Read(session, new RNAssistant.Core.Models.ResourceReadRequest {
                    Reference = longPage.Reference, Representation = "text" }).Result.Text;
                AssertContains(longText, "bodyTarget", "large body uses a separate exact target");
                var longTarget = "Outlook archive mail: 2026-02-01..2026-02-28 / mailbox+PST / page 1 / row 1";
                var longDescriptor = provider.ResolveOutlookArchiveMail(session, longTarget);
                string bodyCursor = null;
                string bodyTail = null;
                do
                {
                    var chunk = provider.Read(session, new RNAssistant.Core.Models.ResourceReadRequest {
                        Reference = longDescriptor.Reference, Representation = "text",
                        Cursor = bodyCursor, MaxChars = RNAssistant.Core.Models.ResourceReadRequest.MaximumCharacters }).Result;
                    bodyTail = chunk.Text;
                    bodyCursor = chunk.NextCursor;
                } while (bodyCursor != null);
                AssertContains(bodyTail, "oil decision needle", "complete large body read through continuation");
                var largeSearch = provider.Search(session, "oil decision needle",
                    LiveDocumentResourceProvider.OutlookArchivePageKind, 10, 200);
                AssertTrue(largeSearch.Matches.Any(item => item.Kind == LiveDocumentResourceProvider.OutlookArchiveMailKind),
                    "large body remains searchable");
            });
        }
    }
}
