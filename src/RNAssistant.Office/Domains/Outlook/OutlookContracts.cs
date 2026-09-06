using System;
using System.Collections.Generic;

namespace RNAssistant.Office.Domains.Outlook
{
    public sealed class OutlookReadMailRequest
    {
        public string EntryId { get; set; }
        public string Content { get; set; }
        public int MaxChars { get; set; }
    }

    public sealed class OutlookSearchMailRequest
    {
        public string Query { get; set; }
        public string Mode { get; set; }
        public bool MatchCase { get; set; }
        public bool WholeWord { get; set; }
        public string Fields { get; set; }
        public int MaxItems { get; set; }
        public int MaxResults { get; set; }
        public int MaxBodyChars { get; set; }
        public int ContextChars { get; set; }
    }

    public sealed class OutlookCreateDraftRequest
    {
        public string Kind { get; set; }
        public string To { get; set; }
        public string Cc { get; set; }
        public string Bcc { get; set; }
        public string Subject { get; set; }
        public string Body { get; set; }
        public string ExpectedTargetToken { get; set; }
    }

    public sealed class OutlookUpdateMailRequest
    {
        public string Kind { get; set; }
        public bool HasCategories { get; set; }
        public string Categories { get; set; }
        public string ExpectedTargetToken { get; set; }
    }

    public sealed class OutlookCollectMailRequest
    {
        public string GroupBy { get; set; }
        public int MaxItems { get; set; }
        public int MaxBodyChars { get; set; }
    }

    public sealed class OutlookFolderReadRequest
    {
        public int MaxItems { get; set; }
        public int MaxBodyChars { get; set; }
        public int MaxSearchBodyChars { get; set; }
    }

    public sealed class OutlookAttachmentSnapshot
    {
        public int Index { get; set; }
        public string FileName { get; set; }
        public string DisplayName { get; set; }
        public long Size { get; set; }
        public string Type { get; set; }
    }

    public sealed class OutlookMailSnapshot
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
        public string SearchBody { get; set; }
        public string StateToken { get; set; }
    }

    public sealed class OutlookMailReadSnapshot
    {
        public bool BodyCaptured { get; set; }
        public OutlookMailSnapshot Mail { get; set; }
        public IReadOnlyList<OutlookAttachmentSnapshot> Attachments { get; set; }
    }

    public sealed class OutlookFolderSnapshot
    {
        public string FolderPath { get; set; }
        public IReadOnlyList<OutlookMailSnapshot> Messages { get; set; }
        public int TotalItems { get; set; }
        public bool Truncated { get; set; }
    }

    public sealed class OutlookDraftBackendResult
    {
        public bool Verified { get; set; }
        public bool Changed { get; set; }
        public bool Displayed { get; set; }
        public string Kind { get; set; }
        public string TargetEntryId { get; set; }
        public string DraftEntryId { get; set; }
        public string To { get; set; }
        public string Cc { get; set; }
        public string Bcc { get; set; }
        public string Subject { get; set; }
        public string Body { get; set; }
        public string StateToken { get; set; }
    }

    public sealed class OutlookUpdateBackendResult
    {
        public bool Verified { get; set; }
        public bool Changed { get; set; }
        public OutlookMailSnapshot Before { get; set; }
        public OutlookMailSnapshot After { get; set; }
    }

    public interface IOutlookBackend
    {
        OutlookMailReadSnapshot ReadMail(OutlookReadMailRequest request);
        OutlookFolderSnapshot ReadFolder(OutlookFolderReadRequest request);
        OutlookDraftBackendResult CreateDraft(
            OutlookCreateDraftRequest request, Action markDispatchPossible);
        OutlookUpdateBackendResult UpdateMail(
            OutlookUpdateMailRequest request, Action markDispatchPossible);
    }

    public sealed class OutlookBackendException : InvalidOperationException
    {
        public string ErrorCode { get; private set; }
        public bool Retryable { get; private set; }
        public string DetailsJson { get; private set; }

        public OutlookBackendException(
            string message, string errorCode, bool retryable,
            string detailsJson = null)
            : base(message)
        {
            ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                ? "outlook_backend_failed" : errorCode;
            Retryable = retryable;
            DetailsJson = detailsJson;
        }
    }

    public enum OutlookOutcomeStatus { Ok, Error, Unknown }
    public enum OutlookEffect
    {
        None,
        VerifiedNoChange,
        VerifiedChange,
        Unknown
    }

    public sealed class OutlookOutcome
    {
        public OutlookOutcomeStatus Status { get; private set; }
        public OutlookEffect Effect { get; private set; }
        public string Message { get; private set; }
        public string DataJson { get; private set; }
        public string ErrorCode { get; private set; }
        public bool Retryable { get; private set; }

        public static OutlookOutcome Ok(
            string message, string dataJson, OutlookEffect effect)
        {
            return new OutlookOutcome
            {
                Status = OutlookOutcomeStatus.Ok,
                Effect = effect,
                Message = message ?? string.Empty,
                DataJson = dataJson
            };
        }

        public static OutlookOutcome Error(
            string message, string dataJson, string errorCode, bool retryable)
        {
            return new OutlookOutcome
            {
                Status = OutlookOutcomeStatus.Error,
                Effect = OutlookEffect.None,
                Message = message ?? string.Empty,
                DataJson = dataJson,
                ErrorCode = errorCode,
                Retryable = retryable
            };
        }

        public static OutlookOutcome Unknown(
            string message, string dataJson, string errorCode)
        {
            return new OutlookOutcome
            {
                Status = OutlookOutcomeStatus.Unknown,
                Effect = OutlookEffect.Unknown,
                Message = message ?? string.Empty,
                DataJson = dataJson,
                ErrorCode = errorCode,
                Retryable = false
            };
        }
    }
}
