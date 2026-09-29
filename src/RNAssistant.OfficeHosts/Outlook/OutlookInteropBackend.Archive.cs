using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using RNAssistant.Office.Domains.Outlook;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace RNAssistant.OfficeHosts
{
    internal sealed partial class OutlookInteropBackend
    {
        public OutlookArchiveAttachmentContent ReadArchiveAttachment(
            OutlookArchiveAttachmentRequest request, CancellationToken cancellationToken)
        {
            if (!_session.IsMailboxTarget || request == null || string.IsNullOrWhiteSpace(request.StoreId) ||
                string.IsNullOrWhiteSpace(request.EntryId) || request.Index < 1 ||
                request.Index > OutlookService.MaxAttachments || request.ExpectedCount < request.Index ||
                request.ExpectedModificationUtc.Kind != DateTimeKind.Utc ||
                request.ExpectedModificationUtc == DateTime.MinValue)
                throw new OutlookBackendException("An exact indexed attachment and timestamp are required.",
                    "outlook_archive_attachment_invalid", false);
            cancellationToken.ThrowIfCancellationRequested();
            var allowed = false;
            foreach (Outlook.Store store in _session.Application.Session.Stores)
            {
                if (!string.Equals(store.StoreID, request.StoreId, StringComparison.Ordinal)) continue;
                allowed = string.Equals(request.StoreId, _session.StoreId, StringComparison.Ordinal) ||
                    request.IncludePst && SafeArchiveString(() => store.FilePath)
                        .EndsWith(".pst", StringComparison.OrdinalIgnoreCase);
                break;
            }
            if (!allowed)
                throw new OutlookBackendException("The indexed Outlook store is detached or outside this archive.",
                    "outlook_archive_store_unavailable", false);
            Outlook.MailItem mail = null;
            try
            {
                var found = _session.Application.Session.GetItemFromID(request.EntryId, request.StoreId);
                mail = found as Outlook.MailItem;
                if (mail == null)
                {
                    if (found != null && Marshal.IsComObject(found)) Marshal.ReleaseComObject(found);
                    throw new OutlookBackendException("Indexed mail is unavailable.",
                        "outlook_archive_mail_unavailable", false);
                }
                var parent = mail.Parent as Outlook.MAPIFolder;
                try
                {
                    if (parent == null || !string.Equals(parent.StoreID, request.StoreId, StringComparison.Ordinal))
                        throw new OutlookBackendException("Indexed mail moved outside its source store.",
                            "outlook_archive_mail_changed", false);
                }
                finally { if (parent != null) Marshal.ReleaseComObject(parent); }
                cancellationToken.ThrowIfCancellationRequested();
                var ownedMail = mail;
                mail = null;
                var capture = CaptureAttachmentFromMail(ownedMail, null, request.Index,
                    request.ExpectedModificationUtc, request.ExpectedCount, true);
                return new OutlookArchiveAttachmentContent { Attachment = capture.Attachment, Bytes = capture.Bytes };
            }
            finally { if (mail != null) Marshal.ReleaseComObject(mail); }
        }

        private sealed class ArchiveFolder
        {
            internal Outlook.MAPIFolder Folder;
            internal string StoreId;
            internal string EntryId;
            internal string Path;
            internal int ItemCount;
        }

        public OutlookArchiveScanBatch ScanArchive(OutlookArchiveScanRequest request, CancellationToken cancellationToken)
        {
            if (!_session.IsMailboxTarget || request == null || request.FromUtc.Kind != DateTimeKind.Utc ||
                request.ToUtc.Kind != DateTimeKind.Utc || request.FromUtc >= request.ToUtc ||
                request.MaxMessages < 1 || request.MaxMessages > 25)
                throw new OutlookBackendException("A bound mailbox, UTC period and 1-25 messages are required.",
                    "outlook_archive_scope_invalid", false);
            var folders = new List<ArchiveFolder>();
            var errors = new List<string>();
            try
            {
                var stores = _session.Application.Session.Stores;
                foreach (Outlook.Store store in stores)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var storeId = store.StoreID;
                    var path = SafeArchiveString(() => store.FilePath);
                    if (!string.Equals(storeId, _session.StoreId, StringComparison.Ordinal) &&
                        !(request.IncludePst && path.EndsWith(".pst", StringComparison.OrdinalIgnoreCase)))
                        continue;
                    Outlook.MAPIFolder root = null;
                    try
                    {
                        root = store.GetRootFolder();
                        AddArchiveFolders(root, storeId, folders, errors, cancellationToken);
                        root = null; // ownership transferred to folders
                    }
                    catch (Exception error) when (!(error is OperationCanceledException))
                    {
                        errors.Add("store " + store.DisplayName + ": " + error.Message);
                        if (root != null && !folders.Any(info => ReferenceEquals(info.Folder, root)))
                            Marshal.ReleaseComObject(root);
                    }
                }
                folders.Sort((left, right) => string.Compare(left.StoreId + "|" + left.EntryId,
                    right.StoreId + "|" + right.EntryId, StringComparison.Ordinal));
                var signature = ArchiveSignature(folders);
                if (!string.IsNullOrEmpty(request.ExpectedSourceSignature) &&
                    !string.Equals(signature, request.ExpectedSourceSignature, StringComparison.Ordinal))
                    throw new OutlookBackendException("Outlook archive sources or folder item counts changed; start a new scan.",
                        "outlook_archive_source_changed", false);
                int folderIndex = 0, itemIndex = 1;
                if (!string.IsNullOrEmpty(request.Cursor))
                {
                    var pieces = request.Cursor.Split(':');
                    if (pieces.Length != 2 || !int.TryParse(pieces[0], NumberStyles.None, CultureInfo.InvariantCulture, out folderIndex) ||
                        !int.TryParse(pieces[1], NumberStyles.None, CultureInfo.InvariantCulture, out itemIndex) ||
                        folderIndex < 0 || folderIndex > folders.Count || itemIndex < 1)
                        throw new OutlookBackendException("Archive checkpoint is invalid.", "outlook_archive_cursor_invalid", false);
                }
                var result = new List<OutlookArchiveMail>();
                var examined = 0;
                for (; folderIndex < folders.Count; folderIndex++, itemIndex = 1)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var info = folders[folderIndex];
                    Outlook.Items items = null;
                    try
                    {
                        items = info.Folder.Items;
                        items.Sort("[ReceivedTime]", true);
                        for (; itemIndex <= info.ItemCount; itemIndex++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            object entry = null;
                            try
                            {
                                entry = items[itemIndex];
                                var mail = entry as Outlook.MailItem;
                                examined++;
                                if (mail != null)
                                {
                                    var received = mail.ReceivedTime;
                                    if (received.Year < 1900) received = mail.SentOn;
                                    if (received.Year >= 1900)
                                    {
                                        var utc = DateTime.SpecifyKind(received, DateTimeKind.Local).ToUniversalTime();
                                        if (utc >= request.FromUtc && utc < request.ToUtc)
                                            result.Add(CaptureArchiveMail(mail, info, utc));
                                    }
                                }
                            }
                            catch (Exception error) when (!(error is OperationCanceledException))
                            { errors.Add(info.Path + " item " + itemIndex + ": " + error.Message); }
                            finally { if (entry != null && Marshal.IsComObject(entry)) Marshal.ReleaseComObject(entry); }
                            if (result.Count >= request.MaxMessages || examined >= 2000)
                            {
                                var next = folderIndex.ToString(CultureInfo.InvariantCulture) + ":" +
                                    (itemIndex + 1).ToString(CultureInfo.InvariantCulture);
                                return new OutlookArchiveScanBatch { SourceSignature = signature,
                                    NextCursor = next, ExaminedItems = examined, Messages = result, Errors = errors,
                                    Complete = false };
                            }
                        }
                    }
                    catch (Exception error) when (!(error is OperationCanceledException))
                    { errors.Add(info.Path + ": " + error.Message); }
                    finally { if (items != null) Marshal.ReleaseComObject(items); }
                }
                return new OutlookArchiveScanBatch { SourceSignature = signature, Complete = true,
                    ExaminedItems = examined, Messages = result, Errors = errors };
            }
            finally
            {
                foreach (var info in folders)
                    if (info.Folder != null) Marshal.ReleaseComObject(info.Folder);
            }
        }

        private static void AddArchiveFolders(Outlook.MAPIFolder folder, string storeId,
            ICollection<ArchiveFolder> result, ICollection<string> errors, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var folderType = folder.PropertyAccessor.GetProperty(
                    "http://schemas.microsoft.com/mapi/proptag/0x36010003");
                if (Convert.ToInt32(folderType, CultureInfo.InvariantCulture) == 2)
                {
                    Marshal.ReleaseComObject(folder);
                    return;
                }
            }
            catch { } // The type property is optional; physical duplicates are marked during analysis.
            var info = new ArchiveFolder { Folder = folder, StoreId = storeId,
                EntryId = folder.EntryID, Path = folder.FolderPath };
            Outlook.Items ownItems = null;
            try { ownItems = folder.Items; info.ItemCount = ownItems.Count; }
            catch (Exception error) { errors.Add(info.Path + ": " + error.Message); }
            finally { if (ownItems != null) Marshal.ReleaseComObject(ownItems); }
            result.Add(info);
            Outlook.Folders children = null;
            try
            {
                children = folder.Folders;
                foreach (Outlook.MAPIFolder child in children)
                    AddArchiveFolders(child, storeId, result, errors, token);
            }
            catch (Exception error) when (!(error is OperationCanceledException))
            { errors.Add(info.Path + " subfolders: " + error.Message); }
            finally { if (children != null) Marshal.ReleaseComObject(children); }
        }

        private static OutlookArchiveMail CaptureArchiveMail(Outlook.MailItem mail, ArchiveFolder folder, DateTime utc)
        {
            var item = new OutlookArchiveMail { StoreId = folder.StoreId, FolderPath = folder.Path,
                ReceivedUtc = utc, EntryId = mail.EntryID, Subject = mail.Subject ?? string.Empty,
                Sender = mail.SenderName ?? string.Empty };
            try { item.InternetMessageId = SafeArchiveString(() => Convert.ToString(mail.PropertyAccessor.GetProperty(
                "http://schemas.microsoft.com/mapi/proptag/0x1035001F"), CultureInfo.InvariantCulture)); }
            catch { }
            try { item.ConversationId = mail.ConversationID; }
            catch { }
            try { item.AttachmentCount = mail.Attachments.Count; }
            catch (Exception error) { item.Error = "attachment metadata: " + error.Message; }
            try { item.LastModificationUtc = DateTime.SpecifyKind(
                mail.LastModificationTime, DateTimeKind.Local).ToUniversalTime(); }
            catch (Exception error) { item.Error = "modification timestamp: " + error.Message; }
            try
            {
                item.Body = mail.Body;
                if (item.Body != null && item.Body.Length > 1000000)
                { item.Body = null; item.Error = "body exceeds 1,000,000 characters"; }
            }
            catch (Exception error) { item.Error = "body: " + error.Message; }
            return item;
        }

        private static string ArchiveSignature(IEnumerable<ArchiveFolder> folders)
        {
            var text = string.Join("\n", folders.Select(item => item.StoreId + "|" + item.EntryId +
                "|" + item.ItemCount.ToString(CultureInfo.InvariantCulture)));
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        private static string SafeArchiveString(Func<string> getter)
        {
            try { return getter() ?? string.Empty; }
            catch { return string.Empty; }
        }
    }
}
