using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Outlook = Microsoft.Office.Interop.Outlook;
using RNAssistant.Core.Models;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Domains.Outlook;
using RNAssistant.Office.Tools;
using RNAssistant.OfficeHosts.Identity;

namespace RNAssistant.OfficeHosts
{
    public sealed class OutlookAdapter : IOfficeApplicationAdapter,
        IOfficeContextProvider, IOfficeBuiltInSkillProvider,
        IOfficeDocumentCatalog, IOfficeDocumentSessionProvider, IOfficeDispatcherProvider,
        IOutlookBackendProvider
    {
        private readonly OutlookDocumentSession _documentSession;
        private readonly OutlookInteropBackend _outlookBackend;

        public OutlookAdapter(
            Outlook.Application application,
            Outlook.MailItem targetMail,
            Outlook.MAPIFolder targetFolder,
            Outlook.Inspector targetInspector,
            Outlook.Explorer targetExplorer,
            IOfficeStaDispatcher dispatcher,
            Outlook.Store targetStore = null)
        {
            var bound = (object)targetMail ?? (object)targetFolder ?? targetStore;
            var runtimeDocumentId = DocumentIdentity.RuntimeKey(
                HostName, bound ?? throw new ArgumentNullException("target"));
            _documentSession = new OutlookDocumentSession(
                application, targetMail, targetFolder,
                targetInspector, targetExplorer,
                runtimeDocumentId, dispatcher, targetStore);
            _outlookBackend = new OutlookInteropBackend(_documentSession);
        }

        public string HostName { get { return "Outlook"; } }
        public IOfficeDocumentSession DocumentSession { get { return _documentSession; } }
        public IOfficeStaDispatcher StaDispatcher { get { return _documentSession.StaDispatcher; } }
        public IOutlookBackend OutlookBackend { get { return _outlookBackend; } }
        public string DocumentKey { get { return _documentSession.StableDocumentId; } }
        public string RuntimeDocumentKey { get { return _documentSession.RuntimeDocumentId; } }
        public string DocumentTitle { get { return _documentSession.Title; } }

        public IReadOnlyList<OpenOfficeDocumentDto> ListOpenDocuments()
        {
            var targets = OfficeTargetEnumerator.ListOpenTargets("Outlook",
                progId => _documentSession.Application);
            var result = new List<OpenOfficeDocumentDto>();
            foreach (var target in targets)
            {
                if (string.IsNullOrWhiteSpace(target.StoreId)) continue;
                result.Add(new OpenOfficeDocumentDto { Host = "Outlook",
                    DocumentKey = target.DocumentKey, Title = target.Name,
                    IsActive = string.Equals(target.DocumentKey, DocumentKey, StringComparison.Ordinal) });
            }
            return result;
        }

        public bool ActivateDocument(string documentKey)
        {
            return string.Equals(documentKey, DocumentKey, StringComparison.Ordinal);
        }

        public bool OpenDocument(string path) { return false; }

        public OfficeContext GetOfficeContext()
        {
            var hwnd = _documentSession.WindowHwnd;
            var context = new OfficeContext
            {
                Host = HostName,
                AppHwnd = new IntPtr(hwnd),
                ProcessId = NativeWindowInfo.GetProcessId(hwnd)
            };
            if (_documentSession.IsMailTarget)
            {
                var mail = RequireSelectedMail();
                context.DocumentTitle = SafeString(
                    delegate { return mail.Subject; });
                context.SelectionAddress = SafeString(
                    delegate { return mail.EntryID; });
                context.SelectionText = Trim(SafeString(
                    delegate { return mail.Body; }), 2000);
                context.DocumentPath = _documentSession.FolderPath;
                try
                {
                    var folder = mail.Parent as Outlook.MAPIFolder;
                    if (folder != null)
                        context.ContainerName = SafeString(
                            delegate { return folder.Name; });
                }
                catch { }
                return context;
            }
            context.DocumentTitle = _documentSession.Title;
            context.DocumentPath = _documentSession.FolderPath;
            context.ContainerName = context.DocumentTitle;
            return context;
        }

        public IEnumerable<SkillDefinition> GetBuiltInSkills()
        {
            return new[]
            {
                new SkillDefinition
                {
                    Id = "outlook.email_assistant",
                    Host = "Outlook",
                    Name = "Outlook email assistant",
                    Description = "Draft, summarize, and reply to Outlook mail.",
                    BodyMarkdown = "# Outlook Email Assistant\n\nUse this skill for reading, searching, collecting, drafting, replying to, or classifying Outlook mail. A multi-stage mailbox workflow normally also uses `common.task_tracking`. Exact loaded tool schemas remain authoritative for arguments.\n\n## Workflow\n\n- Identify whether the requested outcome is a summary/extraction, a search/collection, a draft/reply/forward, or a metadata update. Use `common.resources_find/read` for mail bodies and metadata: choose an exact returned Outlook mail target; `text` is the full body, `source` includes headers/body/attachment metadata, and `structure` excludes the body. Discovery stays within the bound Inspector or folder; duplicate semantic targets require disambiguation, never invent an EntryID. For folder collection, use `common.resources_find` with scope=document and choose the Outlook collection target. Read text first for collectionTruncated and totalFolderItems, then records/table at path=$.messages for bounded rows or bind the same target/path to HTML. The snapshot covers up to 500 newest folder items, with bodyPreview capped at 1000 characters and explicit bodyTruncated; do not infer full bodies or complete-folder totals from previews. Group by the month field in the consumer. Inspector bindings do not grant folder access. Use `outlook.search_mail` for bounded literal/regex field search; narrow the scope before drawing conclusions. For mailbox-wide work, call `outlook.index_archive` repeatedly until complete, then discover `Outlook archive page` targets with `common.resources_find` and read their text through `common.resources_read`. Read the Outlook archive digest target from the index result for compact conversation groups ranked by message count; this rank is activity, not importance. The digest samples first and last messages only, so inspect cited archive pages and rows before claiming decisions. Each archive page reports scan coverage and exact attachment targets for on-demand text or media reads; unread attachment contents remain unexamined. Cite page and row for findings, mark incomplete coverage, and do not treat zero lexical matches as proof of absence.\n- Use `outlook.create_draft` for `new`, `reply`, `replyAll`, or `forward`. It creates and displays a draft; it does not send mail. Match the requested tone and recipient context, keep replies concise unless asked otherwise, and preserve names, dates, links, attachments, and commitments.\n- Use `outlook.update_mail` only for its supported selected-mail metadata operations such as categories or read state. It does not rewrite or send a message. Never claim that mail was sent because no built-in send tool exists.\n\n## Definition of done\n\nFor read-only work, cite the returned mail evidence without inventing omitted body content. For mutations, finish only from verified draft or metadata read-back. If an effect is `unknown`, tell the user to inspect Outlook before retrying because repeating may duplicate a draft or change.",
                    Enabled = true,
                    BuiltIn = true
                }
            };
        }

        public string GetDocumentSnapshot(int maxChars)
        {
            var mail = _documentSession.SelectedMail();
            if (mail == null)
                return Trim(
                    "Current folder: " + _documentSession.FolderPath,
                    maxChars);
            return Trim(
                "Subject: " + SafeString(delegate { return mail.Subject; }) +
                "\nFrom: " + SafeString(delegate { return mail.SenderName; }) +
                "\nReceived: " + SafeString(
                    delegate { return mail.ReceivedTime.ToString(); }) +
                "\n\n" + SafeString(delegate { return mail.Body; }),
                maxChars);
        }

        public void PrepareForContextCapture()
        {
            try { _documentSession.Activate(); }
            catch { }
        }

        public ContextNote CaptureSelectionContext(string mode, int maxChars)
        {
            var mail = RequireSelectedMail();
            var referenceOnly = string.Equals(
                mode, "reference", StringComparison.OrdinalIgnoreCase);
            var entryId = SafeString(delegate { return mail.EntryID; });
            var subject = SafeString(delegate { return mail.Subject; });
            var reference = string.IsNullOrWhiteSpace(entryId)
                ? subject : entryId;
            var text = referenceOnly
                ? "Reference only. Use Outlook tools with this email if exact body content is needed."
                : Trim(
                    "Subject: " + subject +
                    "\nFrom: " + SafeString(delegate { return mail.SenderName; }) +
                    " <" + SafeString(
                        delegate { return mail.SenderEmailAddress; }) + ">" +
                    "\nReceived: " + SafeString(
                        delegate { return mail.ReceivedTime.ToString(); }) +
                    "\n\n" + SafeString(delegate { return mail.Body; }),
                    maxChars);
            return new ContextNote
            {
                Host = HostName,
                Kind = referenceOnly ? "mail-reference" : "mail",
                Title = "Outlook mail: " + subject,
                Reference = reference,
                Source = subject,
                Text = text,
                Preview = Trim(text, 360),
                DetailsJson = JsonConvert.SerializeObject(new
                {
                    subject,
                    sender = SafeString(delegate { return mail.SenderName; }),
                    senderEmail = SafeString(
                        delegate { return mail.SenderEmailAddress; }),
                    received = SafeString(
                        delegate { return mail.ReceivedTime.ToString("O"); }),
                    entryId,
                    mode = referenceOnly ? "reference" : "text"
                })
            };
        }

        private Outlook.MailItem RequireSelectedMail()
        {
            var mail = _documentSession.SelectedMail();
            if (mail == null)
                throw new InvalidOperationException(
                    "Select an email first in the bound Outlook window.");
            return mail;
        }

        private static string SafeString(Func<string> getter)
        {
            try { return getter() ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static string Trim(string text, int maxChars)
        {
            maxChars = Math.Max(0, maxChars);
            if (maxChars == 0) return string.Empty;
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
                return text ?? string.Empty;
            return text.Substring(0, maxChars) + "\n...[truncated]";
        }
    }
}
