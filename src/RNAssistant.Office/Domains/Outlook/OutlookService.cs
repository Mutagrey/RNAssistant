using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Domains.Outlook
{
    public sealed class OutlookService
    {
        public const int MaxItems = 500;
        public const int MaxAttachments = 1000;
        public const int MaxBodyChars = 1000000;
        public const int MaxSearchBodyChars = 100000;

        private readonly IOutlookBackend _backend;

        public OutlookService(IOutlookBackend backend)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        public OutlookMailReadSnapshot CaptureMail(
            OutlookReadMailRequest request, CancellationToken cancellationToken)
        {
            if (request == null || request.MaxChars < 1 || request.MaxChars > MaxBodyChars ||
                (request.Content != "message" && request.Content != "attachments" && request.Content != "both"))
                throw new OutlookBackendException("Invalid exact mail capture request.", "invalid_arguments", false);
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = _backend.ReadMail(request);
            cancellationToken.ThrowIfCancellationRequested();
            var includeBody = request.Content != "attachments";
            if (snapshot == null || snapshot.Mail == null || snapshot.Attachments == null ||
                snapshot.BodyCaptured != includeBody ||
                (includeBody && (snapshot.Mail.Body == null || snapshot.Mail.Body.Length > request.MaxChars ||
                    string.IsNullOrEmpty(snapshot.Mail.StateToken))) ||
                (!includeBody && (snapshot.Mail.Body != null || snapshot.Mail.StateToken != null)) ||
                (!string.IsNullOrEmpty(request.EntryId) && request.EntryId != snapshot.Mail.EntryId))
                throw new OutlookBackendException("Outlook backend returned an incomplete or mismatched capture.", "outlook_mail_snapshot_invalid", false);
            if (snapshot.Attachments.Count > MaxAttachments)
                throw new OutlookBackendException("Outlook attachment collection exceeds the safety limit.", "outlook_attachment_limit_exceeded", false);
            for (var index = 0; index < snapshot.Attachments.Count; index++)
            {
                var attachment = snapshot.Attachments[index];
                if (attachment == null || attachment.Index != index + 1 || attachment.Size < 0 ||
                    attachment.FileName == null || attachment.DisplayName == null || attachment.Type == null)
                    throw new OutlookBackendException("Outlook attachment metadata is incomplete.", "outlook_attachment_snapshot_invalid", false);
            }
            return snapshot;
        }

        public OutlookMailDiscoverySnapshot DiscoverMail(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = _backend.DiscoverMail(MaxItems);
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot == null || snapshot.Items == null || snapshot.Items.Count > MaxItems ||
                (snapshot.BoundMail && (snapshot.Items.Count != 1 || snapshot.Truncated)))
                throw new OutlookBackendException("Invalid bound mail discovery.", "outlook_discovery_invalid", false);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mail in snapshot.Items)
                if (mail == null || mail.EntryId == null || mail.EntryId.Length > 4096 ||
                    (!snapshot.BoundMail && string.IsNullOrWhiteSpace(mail.EntryId)) ||
                    mail.Subject == null || mail.Sender == null || !ids.Add(mail.EntryId))
                    throw new OutlookBackendException("Incomplete or duplicate mail identity.", "outlook_discovery_invalid", false);
            return snapshot;
        }

        public OutlookOutcome SearchMail(
            OutlookSearchMailRequest request,
            CancellationToken cancellationToken)
        {
            request = request ?? new OutlookSearchMailRequest();
            if (string.IsNullOrWhiteSpace(request.Query))
                return Failure("query is required.", "invalid_arguments", false);
            request.MaxItems = Math.Max(1, Math.Min(MaxItems, request.MaxItems));
            request.MaxResults = Math.Max(1, Math.Min(500, request.MaxResults));
            request.MaxBodyChars = Math.Max(
                0, Math.Min(MaxBodyChars, request.MaxBodyChars));
            request.ContextChars = Math.Max(
                0, Math.Min(1000, request.ContextChars));
            HashSet<string> fields;
            var fieldsError = Fields(request.Fields, out fields);
            if (fieldsError != null) return fieldsError;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var folder = _backend.ReadFolder(new OutlookFolderReadRequest
                {
                    MaxItems = request.MaxItems,
                    MaxBodyChars = request.MaxBodyChars,
                    MaxSearchBodyChars = MaxSearchBodyChars
                });
                if (folder == null || folder.Messages == null)
                    return Failure(
                        "Outlook folder backend returned no snapshot.",
                        "outlook_folder_snapshot_missing", true);
                var options = new TextPatternOptions
                {
                    Mode = request.Mode,
                    MatchCase = request.MatchCase,
                    WholeWord = request.WholeWord
                };
                var matches = new JArray();
                var total = 0;
                foreach (var mail in folder.Messages)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var field in SearchFields(mail))
                    {
                        if (!fields.Contains(field.Key)) continue;
                        var found = TextPatternEngine.Find(
                            field.Value, request.Query, options,
                            Math.Max(1, request.MaxResults - matches.Count),
                            request.ContextChars);
                        total += found.MatchCount;
                        foreach (var match in found.Matches)
                        {
                            if (matches.Count >= request.MaxResults) break;
                            matches.Add(new JObject
                            {
                                ["entryId"] = mail.EntryId ?? string.Empty,
                                ["subject"] = mail.Subject ?? string.Empty,
                                ["received"] = new JValue(mail.Received),
                                ["field"] = field.Key,
                                ["start"] = match.Index,
                                ["end"] = match.Index + match.Length,
                                ["preview"] = match.Preview ?? string.Empty,
                                ["body"] = mail.Body ?? string.Empty
                            });
                        }
                    }
                }
                return OutlookOutcome.Ok(
                    "Mail search matches: " + total,
                    new JObject
                    {
                        ["folder"] = folder.FolderPath ?? string.Empty,
                        ["matchCount"] = total,
                        ["returnedCount"] = matches.Count,
                        ["truncated"] = total > matches.Count || folder.Truncated,
                        ["matches"] = matches
                    }.ToString(Formatting.None),
                    OutlookEffect.None);
            }
            catch (OperationCanceledException) { throw; }
            catch (TextPatternException ex)
            {
                return Failure(ex.Message, ex.ErrorCode, false);
            }
            catch (OutlookBackendException ex)
            {
                return Failure(
                    ex.Message, ex.ErrorCode, ex.Retryable, ex.DetailsJson);
            }
            catch (Exception ex)
            {
                return Failure(
                    "Outlook mail search failed: " + ex.Message,
                    "outlook_search_failed", true);
            }
        }

        public OutlookOutcome CollectMail(
            OutlookCollectMailRequest request,
            CancellationToken cancellationToken)
        {
            request = request ?? new OutlookCollectMailRequest();
            request.GroupBy = Normalize(request.GroupBy, "none");
            if (request.GroupBy != "none" && request.GroupBy != "month")
                return Failure(
                    "groupBy must be none or month.",
                    "invalid_arguments", false);
            request.MaxItems = Math.Max(1, Math.Min(MaxItems, request.MaxItems));
            request.MaxBodyChars = Math.Max(
                0, Math.Min(MaxBodyChars, request.MaxBodyChars));
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var folder = _backend.ReadFolder(new OutlookFolderReadRequest
                {
                    MaxItems = request.MaxItems,
                    MaxBodyChars = request.MaxBodyChars,
                    MaxSearchBodyChars = 0
                });
                if (folder == null || folder.Messages == null)
                    return Failure(
                        "Outlook folder backend returned no snapshot.",
                        "outlook_folder_snapshot_missing", true);
                JToken data;
                if (request.GroupBy == "month")
                {
                    var months = new JObject();
                    foreach (var group in folder.Messages.GroupBy(
                        mail => mail.Received.ToString("yyyy-MM")))
                        months[group.Key] = new JArray(
                            group.Select(CollectJson).ToArray());
                    data = new JObject
                    {
                        ["folder"] = folder.FolderPath ?? string.Empty,
                        ["months"] = months
                    };
                }
                else
                {
                    data = new JObject
                    {
                        ["folder"] = folder.FolderPath ?? string.Empty,
                        ["messages"] = new JArray(
                            folder.Messages.Select(CollectJson).ToArray())
                    };
                }
                return OutlookOutcome.Ok(
                    "Mail data collected.", data.ToString(Formatting.None),
                    OutlookEffect.None);
            }
            catch (OperationCanceledException) { throw; }
            catch (OutlookBackendException ex)
            {
                return Failure(
                    ex.Message, ex.ErrorCode, ex.Retryable, ex.DetailsJson);
            }
            catch (Exception ex)
            {
                return Failure(
                    "Outlook mail collection failed: " + ex.Message,
                    "outlook_collect_failed", true);
            }
        }

        public OutlookOutcome CreateDraft(
            OutlookCreateDraftRequest request,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            request = request ?? new OutlookCreateDraftRequest();
            request.Kind = DraftKind(request.Kind);
            if (request.Kind == null)
                return Failure(
                    "kind must be new, reply, replyAll, or forward.",
                    "invalid_arguments", false);
            request.To = request.To ?? string.Empty;
            request.Cc = request.Cc ?? string.Empty;
            request.Bcc = request.Bcc ?? string.Empty;
            request.Subject = request.Subject ?? string.Empty;
            request.Body = request.Body ?? string.Empty;
            var sizeError = DraftSize(request);
            if (sizeError != null) return sizeError;
            var dispatched = false;
            Action mark = delegate
            {
                if (dispatched) return;
                dispatched = true;
                markDispatchPossible();
            };
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (request.Kind != "new")
                {
                    var target = CaptureMail(new OutlookReadMailRequest
                    {
                        Content = "message",
                        MaxChars = MaxBodyChars
                    }, cancellationToken);
                    request.ExpectedTargetToken = target.Mail.StateToken;
                }
                var result = _backend.CreateDraft(request, mark);
                if (!dispatched)
                    return OutlookOutcome.Unknown(
                        "Outlook draft backend returned without a dispatch boundary.",
                        DraftData(result),
                        "outlook_draft_dispatch_boundary_missing");
                cancellationToken.ThrowIfCancellationRequested();
                if (result == null || !result.Verified || !result.Changed ||
                    !result.Displayed || !DraftVerified(request, result))
                    return OutlookOutcome.Unknown(
                        "Outlook draft may have been created, but exact read-back diverged.",
                        DraftData(result),
                        "outlook_draft_verification_failed");
                return OutlookOutcome.Ok(
                    DraftMessage(request.Kind), DraftData(result),
                    OutlookEffect.VerifiedChange);
            }
            catch (OperationCanceledException)
            {
                if (!dispatched) throw;
                return OutlookOutcome.Unknown(
                    "Cancellation was observed after the Outlook draft dispatch boundary; inspect open drafts before retrying.",
                    null, "outlook_effect_unknown");
            }
            catch (OutlookBackendException ex)
            {
                return dispatched
                    ? OutlookOutcome.Unknown(
                        "Outlook draft final state is unknown. " + ex.Message,
                        ex.DetailsJson, "outlook_effect_unknown")
                    : Failure(
                        ex.Message, ex.ErrorCode, ex.Retryable, ex.DetailsJson);
            }
            catch (Exception ex)
            {
                return dispatched
                    ? OutlookOutcome.Unknown(
                        "Outlook draft final state is unknown. " + ex.Message,
                        null, "outlook_effect_unknown")
                    : Failure(
                        "Outlook draft failed before dispatch: " + ex.Message,
                        "outlook_draft_failed", true);
            }
        }

        public OutlookOutcome UpdateMail(
            OutlookUpdateMailRequest request,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            request = request ?? new OutlookUpdateMailRequest();
            request.Kind = Normalize(request.Kind, string.Empty);
            request.Categories = request.Categories ?? string.Empty;
            if (request.Kind != "categories" && request.Kind != "markread")
                return Failure(
                    "kind must be categories or markRead.",
                    "invalid_arguments", false);
            if (request.Kind == "categories" && !request.HasCategories)
                return Failure(
                    "categories is required for kind=categories.",
                    "invalid_arguments", false);
            if (request.Categories.Length > 65536)
                return Failure(
                    "categories exceeds the 65536-character safety limit.",
                    "outlook_categories_too_large", false);
            var dispatched = false;
            Action mark = delegate
            {
                if (dispatched) return;
                dispatched = true;
                markDispatchPossible();
            };
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = CaptureMail(new OutlookReadMailRequest
                {
                    Content = "message",
                    MaxChars = MaxBodyChars
                }, cancellationToken);
                var before = target.Mail;
                if ((request.Kind == "categories" && string.Equals(
                        before.Categories ?? string.Empty, request.Categories,
                        StringComparison.Ordinal)) ||
                    (request.Kind == "markread" && !before.Unread))
                    return OutlookOutcome.Ok(
                        request.Kind == "categories"
                            ? "Mail categories are unchanged."
                            : "Mail is already marked as read.",
                        UpdateData(before), OutlookEffect.VerifiedNoChange);
                request.ExpectedTargetToken = before.StateToken;
                var result = _backend.UpdateMail(request, mark);
                if (!dispatched)
                    return OutlookOutcome.Unknown(
                        "Outlook update backend returned without a dispatch boundary.",
                        result == null ? null : UpdateData(result.After),
                        "outlook_update_dispatch_boundary_missing");
                cancellationToken.ThrowIfCancellationRequested();
                if (!UpdateVerified(request, before, result))
                    return OutlookOutcome.Unknown(
                        "Outlook mail may have changed, but exact read-back diverged.",
                        result == null ? null : UpdateData(result.After),
                        "outlook_update_verification_failed");
                return OutlookOutcome.Ok(
                    request.Kind == "categories"
                        ? "Mail categories updated."
                        : "Mail marked as read.",
                    UpdateData(result.After), OutlookEffect.VerifiedChange);
            }
            catch (OperationCanceledException)
            {
                if (!dispatched) throw;
                return OutlookOutcome.Unknown(
                    "Cancellation was observed after the Outlook update dispatch boundary; inspect the mail before retrying.",
                    null, "outlook_effect_unknown");
            }
            catch (OutlookBackendException ex)
            {
                return dispatched
                    ? OutlookOutcome.Unknown(
                        "Outlook update final state is unknown. " + ex.Message,
                        ex.DetailsJson, "outlook_effect_unknown")
                    : Failure(
                        ex.Message, ex.ErrorCode, ex.Retryable, ex.DetailsJson);
            }
            catch (Exception ex)
            {
                return dispatched
                    ? OutlookOutcome.Unknown(
                        "Outlook update final state is unknown. " + ex.Message,
                        null, "outlook_effect_unknown")
                    : Failure(
                        "Outlook update failed before dispatch: " + ex.Message,
                        "outlook_update_failed", true);
            }
        }

        private static bool UpdateVerified(
            OutlookUpdateMailRequest request,
            OutlookMailSnapshot before,
            OutlookUpdateBackendResult result)
        {
            if (result == null || !result.Verified || !result.Changed ||
                result.After == null) return false;
            if (!string.Equals(
                Identity(before), Identity(result.After),
                StringComparison.Ordinal)) return false;
            return request.Kind == "categories"
                ? string.Equals(
                    result.After.Categories ?? string.Empty,
                    request.Categories, StringComparison.Ordinal)
                : !result.After.Unread;
        }

        private static bool DraftVerified(
            OutlookCreateDraftRequest request,
            OutlookDraftBackendResult result)
        {
            if (!string.Equals(request.Kind, result.Kind,
                StringComparison.Ordinal)) return false;
            if (!StartsWith(result.Body, request.Body)) return false;
            if (request.Kind == "new")
                return string.Equals(result.To ?? string.Empty,
                        request.To, StringComparison.Ordinal) &&
                    string.Equals(result.Cc ?? string.Empty,
                        request.Cc, StringComparison.Ordinal) &&
                    string.Equals(result.Bcc ?? string.Empty,
                        request.Bcc, StringComparison.Ordinal) &&
                    string.Equals(result.Subject ?? string.Empty,
                        request.Subject, StringComparison.Ordinal) &&
                    string.Equals(result.Body ?? string.Empty,
                        request.Body, StringComparison.Ordinal);
            if (request.Kind == "forward")
                return string.Equals(result.To ?? string.Empty,
                    request.To, StringComparison.Ordinal);
            return !string.IsNullOrWhiteSpace(result.TargetEntryId) ||
                !string.IsNullOrWhiteSpace(result.StateToken);
        }

        private static OutlookOutcome DraftSize(OutlookCreateDraftRequest request)
        {
            if (request.Body.Length > MaxBodyChars)
                return Failure(
                    "body exceeds the 1000000-character safety limit.",
                    "outlook_body_too_large", false);
            if (request.To.Length > 65536 || request.Cc.Length > 65536 ||
                request.Bcc.Length > 65536 || request.Subject.Length > 65536)
                return Failure(
                    "A draft header exceeds the 65536-character safety limit.",
                    "outlook_header_too_large", false);
            return null;
        }

        private static OutlookOutcome Fields(
            string text, out HashSet<string> fields)
        {
            fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in (text ?? "subject,sender,body").Split(','))
            {
                var field = (raw ?? string.Empty).Trim();
                if (field.Length > 0) fields.Add(field);
            }
            if (fields.Count == 0)
                return Failure(
                    "fields must contain at least one mail field.",
                    "invalid_arguments", false);
            foreach (var field in fields)
                if (!string.Equals(field, "subject", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(field, "sender", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(field, "recipients", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(field, "body", StringComparison.OrdinalIgnoreCase))
                    return Failure(
                        "Unsupported Outlook search field: " + field + ".",
                        "invalid_arguments", false);
            return null;
        }

        private static IEnumerable<KeyValuePair<string, string>> SearchFields(
            OutlookMailSnapshot mail)
        {
            yield return Pair("subject", mail.Subject);
            yield return Pair("sender",
                (mail.Sender ?? string.Empty) + " <" +
                (mail.SenderEmail ?? string.Empty) + ">");
            yield return Pair("recipients",
                "To: " + (mail.To ?? string.Empty) + "; CC: " +
                (mail.Cc ?? string.Empty) + "; BCC: " +
                (mail.Bcc ?? string.Empty));
            yield return Pair("body", mail.SearchBody ?? mail.Body);
        }

        private static KeyValuePair<string, string> Pair(
            string key, string value)
        {
            return new KeyValuePair<string, string>(key, value ?? string.Empty);
        }

        private static JObject CollectJson(OutlookMailSnapshot mail)
        {
            return new JObject
            {
                ["subject"] = mail.Subject ?? string.Empty,
                ["sender"] = mail.Sender ?? string.Empty,
                ["received"] = new JValue(mail.Received),
                ["body"] = mail.Body ?? string.Empty
            };
        }

        private static string DraftData(OutlookDraftBackendResult result)
        {
            if (result == null) return null;
            return new JObject
            {
                ["kind"] = result.Kind ?? string.Empty,
                ["targetEntryId"] = result.TargetEntryId ?? string.Empty,
                ["draftEntryId"] = result.DraftEntryId ?? string.Empty,
                ["subject"] = result.Subject ?? string.Empty,
                ["displayed"] = result.Displayed
            }.ToString(Formatting.None);
        }

        private static string UpdateData(OutlookMailSnapshot mail)
        {
            if (mail == null) return null;
            return new JObject
            {
                ["entryId"] = mail.EntryId ?? string.Empty,
                ["categories"] = mail.Categories ?? string.Empty,
                ["unread"] = mail.Unread,
                ["stateSha256"] = mail.StateToken ?? string.Empty
            }.ToString(Formatting.None);
        }

        private static string Identity(OutlookMailSnapshot mail)
        {
            return mail == null ? string.Empty :
                (!string.IsNullOrWhiteSpace(mail.EntryId)
                    ? mail.EntryId : mail.StateToken ?? string.Empty);
        }

        private static bool StartsWith(string value, string prefix)
        {
            return (value ?? string.Empty).StartsWith(
                prefix ?? string.Empty, StringComparison.Ordinal);
        }

        private static string DraftKind(string value)
        {
            var normalized = Normalize(value, string.Empty);
            if (normalized == "new" || normalized == "reply" ||
                normalized == "forward") return normalized;
            return normalized == "replyall" ? "replyAll" : null;
        }

        private static string DraftMessage(string kind)
        {
            if (kind == "reply") return "Reply draft displayed.";
            if (kind == "replyAll") return "Reply-all draft displayed.";
            if (kind == "forward") return "Forward draft displayed.";
            return "Mail draft displayed.";
        }

        private static string Normalize(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value)
                ? fallback
                : value.Trim().ToLowerInvariant();
        }

        private static OutlookOutcome Failure(
            string message, string code, bool retryable,
            string detailsJson = null)
        {
            var data = detailsJson;
            if (string.IsNullOrWhiteSpace(data))
                data = new JObject
                {
                    ["code"] = code,
                    ["retryable"] = retryable
                }.ToString(Formatting.None);
            return OutlookOutcome.Error(
                message, data, code, retryable);
        }
    }
}
