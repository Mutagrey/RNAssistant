using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.Outlook;

namespace RNAssistant.Harness
{
    internal sealed partial class FakeOfficeAdapter
    {
        private OutlookBackendException _nextOutlookCreateDraftFailure;
        internal const string OutlookReadMailOperation =
            "outlook.read_mail.direct";
        internal const string OutlookReadFolderOperation =
            "outlook.read_folder.direct";
        internal const string OutlookCreateDraftOperation =
            "outlook.create_draft.direct";
        internal const string OutlookUpdateMailOperation =
            "outlook.update_mail.direct";

        public bool OutlookSelectedUnread
        {
            get { return OutlookSelected().Unread; }
        }

        public string OutlookSelectedCategories
        {
            get { return OutlookSelected().Categories; }
        }

        public Func<OutlookMailDiscoverySnapshot, OutlookMailDiscoverySnapshot> OutlookDiscoveryTransform { get; set; }
        public bool OutlookIsMailTarget { get; set; }
        public bool OutlookExcludeSecondMail { get; set; }
        public OutlookMailDiscoverySnapshot DiscoverMail(int maxItems)
        {
            BeginOutlookBackendCall("outlook.discover.direct");
            var source = OutlookIsMailTarget ? new[] { OutlookSelected() } :
                _outlookMail.Where(mail => !OutlookExcludeSecondMail || mail.EntryId != "mail-2").ToArray();
            var snapshot = new OutlookMailDiscoverySnapshot { BoundMail = OutlookIsMailTarget, Truncated = source.Length > maxItems,
                Items = source.Take(maxItems).Select(mail => new OutlookMailSummarySnapshot { EntryId = mail.EntryId,
                    Subject = mail.Subject ?? string.Empty, Sender = mail.Sender ?? string.Empty, Received = mail.Received }).ToArray() };
            return OutlookDiscoveryTransform == null ? snapshot : OutlookDiscoveryTransform(snapshot);
        }

        public int OutlookBodyMaterializationCount { get; private set; }
        public int OutlookCollectionCaptureCount { get; private set; }
        public Func<OutlookFolderSnapshot, OutlookFolderSnapshot> OutlookFolderSnapshotTransform { get; set; }
        public Func<OutlookMailReadSnapshot, OutlookMailReadSnapshot> OutlookReadSnapshotTransform { get; set; }
        public string OutlookSelectedBody
        {
            get { return OutlookSelected().Body; }
            set { OutlookSelected().Body = value; }
        }

        public OutlookMailReadSnapshot ReadMail(OutlookReadMailRequest request)
        {
            BeginOutlookBackendCall(OutlookReadMailOperation);
            request = request ?? new OutlookReadMailRequest();
            var mail = string.IsNullOrWhiteSpace(request.EntryId)
                ? OutlookSelected()
                : _outlookMail.FirstOrDefault(item => string.Equals(
                    item.EntryId, request.EntryId, StringComparison.Ordinal));
            if ((request.BoundMailOnly && !OutlookIsMailTarget) ||
                (mail != null && ((OutlookIsMailTarget && mail != OutlookSelected()) ||
                    (OutlookExcludeSecondMail && mail.EntryId == "mail-2")))) mail = null;
            if (mail == null)
                throw new OutlookBackendException(
                    "Mail item not found: " + (request.EntryId ?? string.Empty),
                    "outlook_mail_not_found", true);
            var includeBody = request.Content != "attachments";
            if (includeBody)
            {
                OutlookBodyMaterializationCount++;
                if ((mail.Body ?? string.Empty).Length > request.MaxChars)
                    throw new OutlookBackendException("The complete mail body exceeds the capture limit.", "RESOURCE_SNAPSHOT_TOO_LARGE", false);
            }
            var attachments = request.Content != "message" && string.Equals(
                mail.EntryId, "mail-1", StringComparison.Ordinal)
                ? new[]
                {
                    new OutlookAttachmentSnapshot
                    {
                        Index = 1,
                        FileName = "renewal.pdf",
                        DisplayName = "renewal.pdf",
                        Size = 2048,
                        Type = "olByValue"
                    }
                }
                : new OutlookAttachmentSnapshot[0];
            var captured = OutlookSnapshot(mail, includeBody ? request.MaxChars : 0, 0);
            captured.Body = includeBody ? mail.Body ?? string.Empty : null;
            captured.SearchBody = null;
            if (!includeBody) captured.StateToken = null;
            var snapshot = new OutlookMailReadSnapshot
            {
                BodyCaptured = includeBody,
                Mail = captured,
                Attachments = attachments
            };
            return OutlookReadSnapshotTransform == null ? snapshot : OutlookReadSnapshotTransform(snapshot);
        }

        public OutlookFolderSnapshot ReadFolder(OutlookFolderReadRequest request)
        {
            BeginOutlookBackendCall(OutlookReadFolderOperation);
            request = request ?? new OutlookFolderReadRequest();
            if (OutlookIsMailTarget)
                throw new OutlookBackendException("The bound Inspector is not a folder.", "outlook_folder_target_missing", true);
            var collection = request.MaxSearchBodyChars == 0;
            if (collection) OutlookCollectionCaptureCount++;
            var all = _outlookMail.Where(mail => !OutlookExcludeSecondMail || mail.EntryId != "mail-2").ToArray();
            var source = all
                .OrderByDescending(item => item.Received)
                .Take(Math.Max(1, request.MaxItems))
                .Select(item => {
                    if (!collection) return OutlookSnapshot(item, request.MaxBodyChars, request.MaxSearchBodyChars);
                    var body = item.Body ?? string.Empty;
                    var length = Math.Min(body.Length, request.MaxBodyChars);
                    if (length > 0 && length < body.Length && char.IsHighSurrogate(body[length - 1])) length--;
                    return new OutlookMailSnapshot { EntryId = item.EntryId, Subject = item.Subject, Sender = item.Sender,
                        Received = item.Received, Body = body.Substring(0, length), BodyTruncated = length < body.Length };
                })
                .ToArray();
            var snapshot = new OutlookFolderSnapshot
            {
                FolderPath = "\\Mock Store\\Inbox",
                Messages = source,
                TotalItems = all.Length,
                Truncated = all.Length > source.Length
            };
            return OutlookFolderSnapshotTransform == null ? snapshot : OutlookFolderSnapshotTransform(snapshot);
        }

        public OutlookDraftBackendResult CreateDraft(
            OutlookCreateDraftRequest request, Action markDispatchPossible)
        {
            BeginOutlookBackendCall(OutlookCreateDraftOperation);
            if (_nextOutlookCreateDraftFailure != null)
            {
                var failure = _nextOutlookCreateDraftFailure;
                _nextOutlookCreateDraftFailure = null;
                throw failure;
            }
            request = request ?? new OutlookCreateDraftRequest();
            FakeOutlookMail target = null;
            if (!string.Equals(request.Kind, "new", StringComparison.Ordinal))
            {
                target = OutlookSelected();
                if (!string.Equals(
                    request.ExpectedTargetToken, OutlookToken(target),
                    StringComparison.Ordinal))
                    throw new OutlookBackendException(
                        "Selected mail changed before draft creation.",
                        "outlook_mail_target_changed", true);
            }
            markDispatchPossible();
            _outlookDraft = request.Body ?? string.Empty;
            var body = string.Equals(request.Kind, "new", StringComparison.Ordinal)
                ? _outlookDraft
                : _outlookDraft + "\n\nOriginal message";
            ThrowAfterOutlookMutation();
            return new OutlookDraftBackendResult
            {
                Verified = true,
                Changed = true,
                Displayed = true,
                Kind = request.Kind,
                TargetEntryId = target == null ? string.Empty : target.EntryId,
                DraftEntryId = string.Empty,
                To = request.To ?? string.Empty,
                Cc = request.Cc ?? string.Empty,
                Bcc = request.Bcc ?? string.Empty,
                Subject = request.Subject ?? string.Empty,
                Body = body,
                StateToken = TextPatternEngine.Sha256(
                    (request.Kind ?? string.Empty) + "\n" + body)
            };
        }

        public void QueueOutlookCreateDraftFailure(
            string message, string errorCode, bool retryable)
        {
            _nextOutlookCreateDraftFailure = new OutlookBackendException(
                message, errorCode, retryable);
        }

        public OutlookUpdateBackendResult UpdateMail(
            OutlookUpdateMailRequest request, Action markDispatchPossible)
        {
            BeginOutlookBackendCall(OutlookUpdateMailOperation);
            request = request ?? new OutlookUpdateMailRequest();
            var mail = OutlookSelected();
            var before = OutlookSnapshot(mail, 1, 0);
            if (!string.Equals(
                request.ExpectedTargetToken, before.StateToken,
                StringComparison.Ordinal))
                throw new OutlookBackendException(
                    "Selected mail changed before update.",
                    "outlook_mail_target_changed", true);
            var desiredChange = string.Equals(
                request.Kind, "categories", StringComparison.Ordinal)
                ? !string.Equals(
                    mail.Categories ?? string.Empty,
                    request.Categories ?? string.Empty,
                    StringComparison.Ordinal)
                : mail.Unread;
            if (!desiredChange)
                return new OutlookUpdateBackendResult
                {
                    Verified = true,
                    Changed = false,
                    Before = before,
                    After = OutlookSnapshot(mail, 1, 0)
                };
            markDispatchPossible();
            if (string.Equals(request.Kind, "categories", StringComparison.Ordinal))
                mail.Categories = request.Categories ?? string.Empty;
            else mail.Unread = false;
            ThrowAfterOutlookMutation();
            return new OutlookUpdateBackendResult
            {
                Verified = true,
                Changed = true,
                Before = before,
                After = OutlookSnapshot(mail, 1, 0)
            };
        }

        private void BeginOutlookBackendCall(string operation)
        {
            OutlookBackendCalls.Add(operation);
        }

        private void ThrowAfterOutlookMutation()
        {
            if (!OutlookThrowAfterMutation) return;
            OutlookThrowAfterMutation = false;
            throw new OutlookBackendException(
                "scripted Outlook failure after mutation",
                "outlook_scripted_post_dispatch", false);
        }

        private FakeOutlookMail OutlookSelected()
        {
            if (_outlookMail.Count == 0)
                throw new OutlookBackendException(
                    "Select an email first.",
                    "outlook_mail_target_missing", true);
            return _outlookMail[0];
        }

        private static OutlookMailSnapshot OutlookSnapshot(
            FakeOutlookMail mail, int maxBodyChars, int maxSearchBodyChars)
        {
            return new OutlookMailSnapshot
            {
                EntryId = mail.EntryId,
                Subject = mail.Subject,
                Sender = mail.Sender,
                SenderEmail = mail.SenderEmail,
                To = mail.To,
                Cc = mail.Cc,
                Bcc = mail.Bcc,
                Received = mail.Received,
                Categories = mail.Categories,
                Unread = mail.Unread,
                Body = OutlookTrim(mail.Body, Math.Max(0, maxBodyChars)),
                SearchBody = OutlookTrim(
                    mail.Body, Math.Max(0, maxSearchBodyChars)),
                StateToken = OutlookToken(mail)
            };
        }

        private static string OutlookToken(FakeOutlookMail mail)
        {
            return TextPatternEngine.Sha256(string.Join("\n", new[]
            {
                mail.EntryId ?? string.Empty,
                mail.Categories ?? string.Empty,
                mail.Unread ? "1" : "0",
                mail.Subject ?? string.Empty,
                mail.Body ?? string.Empty
            }));
        }

        private static string OutlookTrim(string value, int maxChars)
        {
            value = value ?? string.Empty;
            if (maxChars <= 0) return string.Empty;
            return value.Length <= maxChars
                ? value : value.Substring(0, maxChars) + "\n...[truncated]";
        }

        private sealed class FakeOutlookMail
        {
            public string EntryId { get; set; }
            public string Subject { get; set; }
            public string Sender { get; set; }
            public string SenderEmail { get; set; }
            public string To { get; set; }
            public string Cc { get; set; }
            public string Bcc { get; set; }
            public DateTime Received { get; set; }
            public string Categories { get; set; }
            public bool Unread { get; set; }
            public string Body { get; set; }
        }
    }
}
