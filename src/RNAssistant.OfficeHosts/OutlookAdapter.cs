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
        IOfficeDocumentSessionProvider, IOfficeDispatcherProvider,
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
            IOfficeStaDispatcher dispatcher)
        {
            var bound = (object)targetMail ?? targetFolder;
            var runtimeDocumentId = DocumentIdentity.RuntimeKey(
                HostName, bound ?? throw new ArgumentNullException("target"));
            _documentSession = new OutlookDocumentSession(
                application, targetMail, targetFolder,
                targetInspector, targetExplorer,
                runtimeDocumentId, dispatcher);
            _outlookBackend = new OutlookInteropBackend(_documentSession);
        }

        public string HostName { get { return "Outlook"; } }
        public IOfficeDocumentSession DocumentSession { get { return _documentSession; } }
        public IOfficeStaDispatcher StaDispatcher { get { return _documentSession.StaDispatcher; } }
        public IOutlookBackend OutlookBackend { get { return _outlookBackend; } }
        public string DocumentKey { get { return _documentSession.StableDocumentId; } }
        public string RuntimeDocumentKey { get { return _documentSession.RuntimeDocumentId; } }
        public string DocumentTitle { get { return _documentSession.Title; } }

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
                    BodyMarkdown = "# Outlook Email Assistant\n\nUse this skill for email tasks.\n\n- Identify whether the user wants a draft, reply, summary, or extraction.\n- Match the requested tone and recipient context.\n- Keep replies concise unless asked otherwise.\n- Do not send mail unless the user explicitly requests sending and a tool supports it.\n- Preserve important dates, names, and commitments.",
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
