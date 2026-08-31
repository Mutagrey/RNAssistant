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

        public OutlookMailReadSnapshot ReadMail(OutlookReadMailRequest request)
        {
            request = request ?? new OutlookReadMailRequest();
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
                var count = mail.Attachments == null ? 0 : mail.Attachments.Count;
                if (count > OutlookService.MaxAttachments)
                    throw new OutlookBackendException(
                        "Outlook attachment collection exceeds the safety limit.",
                        "outlook_attachment_limit_exceeded", false);
                for (var index = 1; index <= count; index++)
                {
                    var attachment = mail.Attachments[index];
                    attachments.Add(new OutlookAttachmentSnapshot
                    {
                        Index = index,
                        FileName = SafeString(delegate { return attachment.FileName; }),
                        DisplayName = SafeString(delegate { return attachment.DisplayName; }),
                        Size = SafeInt(delegate { return attachment.Size; }),
                        Type = SafeString(delegate { return attachment.Type.ToString(); })
                    });
                }
            }
            return new OutlookMailReadSnapshot
            {
                Mail = Snapshot(mail, request.MaxChars, 0),
                Attachments = attachments
            };
        }

        public OutlookFolderSnapshot ReadFolder(OutlookFolderReadRequest request)
        {
            request = request ?? new OutlookFolderReadRequest();
            var folder = _session.Folder;
            var items = folder.Items;
            items.Sort("[ReceivedTime]", true);
            var total = items.Count;
            var limit = Math.Min(total, Math.Max(1, request.MaxItems));
            var messages = new List<OutlookMailSnapshot>();
            for (var index = 1; index <= limit; index++)
            {
                var mail = items[index] as Outlook.MailItem;
                if (mail == null) continue;
                messages.Add(Snapshot(
                    mail, request.MaxBodyChars,
                    request.MaxSearchBodyChars));
            }
            return new OutlookFolderSnapshot
            {
                FolderPath = SafeString(delegate { return folder.FolderPath; }),
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

        private static int SafeInt(Func<int> getter)
        {
            try { return getter(); }
            catch { return 0; }
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
