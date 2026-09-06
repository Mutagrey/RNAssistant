using System;
using System.Collections.Generic;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.Outlook;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace RNAssistant.OfficeHosts
{
    internal sealed class OutlookInteropBackend : IOutlookBackend
    {
        private readonly OutlookDocumentSession _session;

        internal OutlookInteropBackend(OutlookDocumentSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        public OutlookMailDiscoverySnapshot DiscoverMail(int maxItems)
        {
            if (maxItems < 1 || maxItems > OutlookService.MaxItems)
                throw new OutlookBackendException("Invalid discovery bound.", "invalid_arguments", false);
            var result = new List<OutlookMailSummarySnapshot>();
            if (_session.IsMailTarget)
            {
                result.Add(MailSummary(_session.SelectedMail()));
                return new OutlookMailDiscoverySnapshot { BoundMail = true, Items = result };
            }
            var items = _session.Folder.Items;
            items.Sort("[ReceivedTime]", true);
            var total = items.Count;
            for (var index = 1; index <= Math.Min(total, maxItems); index++)
            {
                var mail = items[index] as Outlook.MailItem;
                if (mail != null) result.Add(MailSummary(mail));
            }
            return new OutlookMailDiscoverySnapshot { Items = result, Truncated = total > maxItems };
        }

        private static OutlookMailSummarySnapshot MailSummary(Outlook.MailItem mail)
        {
            if (mail == null) throw new OutlookBackendException("Bound mail is unavailable.", "outlook_mail_not_found", false);
            return new OutlookMailSummarySnapshot { EntryId = mail.EntryID ?? string.Empty,
                Subject = mail.Subject ?? string.Empty, Sender = mail.SenderName ?? string.Empty, Received = mail.ReceivedTime };
        }

        public OutlookMailReadSnapshot ReadMail(OutlookReadMailRequest request)
        {
            request = request ?? new OutlookReadMailRequest();
            if (request.BoundMailOnly && !_session.IsMailTarget)
                throw new OutlookBackendException("This source requires a bound mail Inspector.", "outlook_mail_target_mismatch", false);
            var mail = _session.ResolveMail(request.EntryId);
            if (mail == null)
                throw new OutlookBackendException(
                    string.IsNullOrWhiteSpace(request.EntryId)
                        ? "Select an email first in the bound Outlook window."
                        : "Mail item not found: " + request.EntryId,
                    "outlook_mail_not_found", true);
            var attachments = new List<OutlookAttachmentSnapshot>();
            if (string.Equals(request.Content, "attachments", StringComparison.Ordinal) ||
                string.Equals(request.Content, "both", StringComparison.Ordinal))
            {
                var collection = mail.Attachments;
                if (collection == null)
                    throw new OutlookBackendException("Outlook attachment metadata is unavailable.", "outlook_attachment_snapshot_invalid", false);
                var count = collection.Count;
                if (count > OutlookService.MaxAttachments)
                    throw new OutlookBackendException(
                        "Outlook attachment collection exceeds the safety limit.",
                        "outlook_attachment_limit_exceeded", false);
                for (var index = 1; index <= count; index++)
                {
                    var attachment = collection[index];
                    attachments.Add(new OutlookAttachmentSnapshot
                    {
                        Index = index,
                        FileName = attachment.FileName ?? string.Empty,
                        DisplayName = attachment.DisplayName ?? string.Empty,
                        Size = attachment.Size,
                        Type = attachment.Type.ToString()
                    });
                }
            }
            return new OutlookMailReadSnapshot
            {
                BodyCaptured = request.Content != "attachments",
                Mail = CaptureSnapshot(mail, request.MaxChars, request.Content != "attachments"),
                Attachments = attachments
            };
        }

        public OutlookFolderSnapshot ReadFolder(OutlookFolderReadRequest request)
        {
            request = request ?? new OutlookFolderReadRequest();
            var collection = request.MaxSearchBodyChars == 0;
            if (collection && (request.MaxItems < 1 || request.MaxItems > OutlookService.MaxItems ||
                request.MaxBodyChars < 0 || request.MaxBodyChars > OutlookService.CollectionPreviewCharacters))
                throw new OutlookBackendException("Invalid collection bounds.", "invalid_arguments", false);
            var folder = _session.Folder;
            var items = folder.Items;
            items.Sort("[ReceivedTime]", true);
            var total = items.Count;
            var limit = Math.Min(total, Math.Max(1, request.MaxItems));
            var messages = new List<OutlookMailSnapshot>();
            long characters = 0;
            for (var index = 1; index <= limit; index++)
            {
                var mail = items[index] as Outlook.MailItem;
                if (mail == null) continue;
                var captured = collection ? CollectionSnapshot(mail, request.MaxBodyChars) :
                    Snapshot(mail, request.MaxBodyChars, request.MaxSearchBodyChars);
                if (collection)
                {
                    characters += captured.Subject.Length + captured.Sender.Length + captured.Body.Length;
                    if (characters > OutlookService.CollectionMaximumCharacters)
                        throw new OutlookBackendException("The mail collection exceeds the snapshot budget.", "RESOURCE_SNAPSHOT_TOO_LARGE", false);
                }
                messages.Add(captured);
            }
            return new OutlookFolderSnapshot
            {
                FolderPath = collection ? null : SafeString(delegate { return folder.FolderPath; }),
                Messages = messages,
                TotalItems = total,
                Truncated = total > limit
            };
        }

        public OutlookDraftBackendResult CreateDraft(
            OutlookCreateDraftRequest request, Action markDispatchPossible)
        {
            if (request == null)
                throw new OutlookBackendException(
                    "Outlook draft request is missing.",
                    "outlook_draft_request_missing", false);
            Outlook.MailItem target = null;
            if (!string.Equals(request.Kind, "new", StringComparison.Ordinal))
            {
                target = _session.SelectedMail();
                if (target == null)
                    throw new OutlookBackendException(
                        "Select an email first in the bound Outlook window.",
                        "outlook_mail_target_missing", true);
                if (!string.Equals(
                    request.ExpectedTargetToken, StateToken(target),
                    StringComparison.Ordinal))
                    throw new OutlookBackendException(
                        "The selected Outlook mail changed before draft creation.",
                        "outlook_mail_target_changed", true);
            }

            markDispatchPossible();
            Outlook.MailItem draft;
            if (string.Equals(request.Kind, "new", StringComparison.Ordinal))
                draft = _session.Application.CreateItem(
                    Outlook.OlItemType.olMailItem) as Outlook.MailItem;
            else if (string.Equals(request.Kind, "reply", StringComparison.Ordinal))
                draft = target.Reply() as Outlook.MailItem;
            else if (string.Equals(request.Kind, "replyAll", StringComparison.Ordinal))
                draft = target.ReplyAll() as Outlook.MailItem;
            else draft = target.Forward() as Outlook.MailItem;
            if (draft == null)
                throw new OutlookBackendException(
                    "Outlook did not create a draft item.",
                    "outlook_draft_creation_failed", false);

            if (string.Equals(request.Kind, "new", StringComparison.Ordinal))
            {
                draft.To = request.To ?? string.Empty;
                draft.CC = request.Cc ?? string.Empty;
                draft.BCC = request.Bcc ?? string.Empty;
                draft.Subject = request.Subject ?? string.Empty;
                draft.Body = request.Body ?? string.Empty;
            }
            else
            {
                if (string.Equals(request.Kind, "forward", StringComparison.Ordinal))
                    draft.To = request.To ?? string.Empty;
                draft.Body = (request.Body ?? string.Empty) + "\n\n" +
                    (draft.Body ?? string.Empty);
            }
            draft.Display(false);
            var displayed = false;
            try
            {
                var inspector = draft.GetInspector;
                displayed = inspector != null &&
                    NativeWindowInfo.ReadLongMemberPath(inspector, "HWND") != 0;
            }
            catch { }
            return new OutlookDraftBackendResult
            {
                Verified = displayed,
                Changed = true,
                Displayed = displayed,
                Kind = request.Kind,
                TargetEntryId = target == null
                    ? string.Empty : SafeString(delegate { return target.EntryID; }),
                DraftEntryId = SafeString(delegate { return draft.EntryID; }),
                To = SafeString(delegate { return draft.To; }),
                Cc = SafeString(delegate { return draft.CC; }),
                Bcc = SafeString(delegate { return draft.BCC; }),
                Subject = SafeString(delegate { return draft.Subject; }),
                Body = SafeString(delegate { return draft.Body; }),
                StateToken = StateToken(draft)
            };
        }

        public OutlookUpdateBackendResult UpdateMail(
            OutlookUpdateMailRequest request, Action markDispatchPossible)
        {
            if (request == null)
                throw new OutlookBackendException(
                    "Outlook update request is missing.",
                    "outlook_update_request_missing", false);
            var mail = _session.SelectedMail();
            if (mail == null)
                throw new OutlookBackendException(
                    "Select an email first in the bound Outlook window.",
                    "outlook_mail_target_missing", true);
            var before = Snapshot(mail, 1, 0);
            if (!string.Equals(
                request.ExpectedTargetToken, before.StateToken,
                StringComparison.Ordinal))
                throw new OutlookBackendException(
                    "The selected Outlook mail changed before update.",
                    "outlook_mail_target_changed", true);
            var changed = string.Equals(
                request.Kind, "categories", StringComparison.Ordinal)
                ? !string.Equals(
                    before.Categories ?? string.Empty,
                    request.Categories ?? string.Empty,
                    StringComparison.Ordinal)
                : before.Unread;
            if (!changed)
                return new OutlookUpdateBackendResult
                {
                    Verified = true,
                    Changed = false,
                    Before = before,
                    After = Snapshot(mail, 1, 0)
                };
            markDispatchPossible();
            if (string.Equals(request.Kind, "categories", StringComparison.Ordinal))
                mail.Categories = request.Categories ?? string.Empty;
            else mail.UnRead = false;
            mail.Save();
            var after = Snapshot(mail, 1, 0);
            var verified = string.Equals(
                    OutlookDocumentSession.MailIdentity(mail),
                    string.IsNullOrWhiteSpace(after.EntryId)
                        ? OutlookDocumentSession.MailIdentity(mail)
                        : after.EntryId,
                    StringComparison.Ordinal) &&
                (string.Equals(request.Kind, "categories", StringComparison.Ordinal)
                    ? string.Equals(
                        after.Categories ?? string.Empty,
                        request.Categories ?? string.Empty,
                        StringComparison.Ordinal)
                    : !after.Unread);
            return new OutlookUpdateBackendResult
            {
                Verified = verified,
                Changed = true,
                Before = before,
                After = after
            };
        }

        private static OutlookMailSnapshot CaptureSnapshot(Outlook.MailItem mail, int maxChars, bool includeBody)
        {
            // OOM exposes Body as one string. Reject an oversized body after this property
            // read; do not claim a pre-materialization bound or substitute a preview.
            var body = includeBody ? mail.Body ?? string.Empty : null;
            if (body != null && body.Length > maxChars)
                throw new OutlookBackendException("The complete mail body exceeds the capture limit.", "RESOURCE_SNAPSHOT_TOO_LARGE", false);
            var snapshot = new OutlookMailSnapshot
            {
                EntryId = mail.EntryID ?? string.Empty,
                Subject = mail.Subject ?? string.Empty,
                Sender = mail.SenderName ?? string.Empty,
                SenderEmail = mail.SenderEmailAddress ?? string.Empty,
                To = mail.To ?? string.Empty,
                Cc = mail.CC ?? string.Empty,
                Bcc = mail.BCC ?? string.Empty,
                Received = mail.ReceivedTime,
                Categories = mail.Categories ?? string.Empty,
                Unread = mail.UnRead,
                Body = body,
                SearchBody = null
            };
            if (includeBody)
                snapshot.StateToken = TextPatternEngine.Sha256(string.Join("\n", new[]
                {
                    string.IsNullOrEmpty(snapshot.EntryId) ? OutlookDocumentSession.MailIdentity(mail) : snapshot.EntryId,
                    snapshot.Categories, snapshot.Unread ? "1" : "0", snapshot.Subject, body
                }));
            return snapshot;
        }

        private static OutlookMailSnapshot CollectionSnapshot(Outlook.MailItem mail, int maxBodyChars)
        {
            // OOM materializes Body before slicing; no state-token/second body read.
            var subject = mail.Subject ?? string.Empty;
            var sender = mail.SenderName ?? string.Empty;
            if (subject.Length > 4096 || sender.Length > 4096)
                throw new OutlookBackendException("Mail collection headers exceed the bound.", "RESOURCE_SNAPSHOT_TOO_LARGE", false);
            var body = mail.Body ?? string.Empty;
            var length = Math.Min(body.Length, maxBodyChars);
            if (length > 0 && length < body.Length && char.IsHighSurrogate(body[length - 1])) length--;
            return new OutlookMailSnapshot { EntryId = mail.EntryID, Subject = subject, Sender = sender,
                Received = mail.ReceivedTime, Body = body.Substring(0, length), BodyTruncated = length < body.Length };
        }

        private static OutlookMailSnapshot Snapshot(
            Outlook.MailItem mail, int maxBodyChars, int maxSearchBodyChars)
        {
            if (mail == null) return null;
            var body = SafeString(delegate { return mail.Body; });
            return new OutlookMailSnapshot
            {
                EntryId = SafeString(delegate { return mail.EntryID; }),
                Subject = SafeString(delegate { return mail.Subject; }),
                Sender = SafeString(delegate { return mail.SenderName; }),
                SenderEmail = SafeString(
                    delegate { return mail.SenderEmailAddress; }),
                To = SafeString(delegate { return mail.To; }),
                Cc = SafeString(delegate { return mail.CC; }),
                Bcc = SafeString(delegate { return mail.BCC; }),
                Received = SafeDate(delegate { return mail.ReceivedTime; }),
                Categories = SafeString(delegate { return mail.Categories; }),
                Unread = SafeBool(delegate { return mail.UnRead; }),
                Body = Trim(body, maxBodyChars),
                SearchBody = Trim(body, maxSearchBodyChars),
                StateToken = StateToken(mail)
            };
        }

        private static string StateToken(Outlook.MailItem mail)
        {
            return TextPatternEngine.Sha256(string.Join("\n", new[]
            {
                OutlookDocumentSession.MailIdentity(mail),
                SafeString(delegate { return mail.Categories; }),
                SafeBool(delegate { return mail.UnRead; }) ? "1" : "0",
                SafeString(delegate { return mail.Subject; }),
                SafeString(delegate { return mail.Body; })
            }));
        }

        private static string Trim(string value, int maxChars)
        {
            value = value ?? string.Empty;
            if (maxChars <= 0) return string.Empty;
            return value.Length <= maxChars
                ? value : value.Substring(0, maxChars) + "\n...[truncated]";
        }

        private static string SafeString(Func<string> getter)
        {
            try { return getter() ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static bool SafeBool(Func<bool> getter)
        {
            try { return getter(); }
            catch { return false; }
        }

        private static DateTime SafeDate(Func<DateTime> getter)
        {
            try { return getter(); }
            catch { return DateTime.MinValue; }
        }
    }
}
