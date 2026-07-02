using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Outlook = Microsoft.Office.Interop.Outlook;
using RNAssistant.Core.Models;
using RNAssistant.Office;
using RNAssistant.Office.Tools;

namespace RNAssistant.OfficeHosts
{
    public sealed class OutlookAdapter : IOfficeApplicationAdapter, IOfficeContextProvider
    {
        private readonly Outlook.Application _application;
        private readonly OfficeTargetDescriptor _target;

        public OutlookAdapter(Outlook.Application application)
            : this(application, null)
        {
        }

        public OutlookAdapter(Outlook.Application application, OfficeTargetDescriptor target)
        {
            _application = application;
            _target = target;
        }

        public string HostName { get { return "Outlook"; } }

        public string DocumentKey
        {
            get
            {
                var mail = SelectedMail();
                if (mail != null && !string.IsNullOrWhiteSpace(mail.EntryID))
                {
                    return mail.EntryID;
                }

                var folder = CurrentFolder();
                return folder == null ? "Outlook" : folder.FolderPath;
            }
        }

        public string RuntimeDocumentKey
        {
            get { return DocumentKey; }
        }

        public string DocumentTitle
        {
            get
            {
                var mail = SelectedMail();
                if (mail != null)
                {
                    return mail.Subject;
                }

                var folder = CurrentFolder();
                return folder == null ? "Outlook" : folder.Name;
            }
        }

        public OfficeContext GetOfficeContext()
        {
            var context = new OfficeContext { Host = HostName };
            var hwnd = ActiveOutlookHwnd();
            context.AppHwnd = new IntPtr(hwnd);
            context.ProcessId = NativeWindowInfo.GetProcessId(hwnd);

            var mail = SelectedMail();
            if (mail != null)
            {
                context.DocumentTitle = SafeString(delegate { return mail.Subject; });
                context.SelectionAddress = SafeString(delegate { return mail.EntryID; });
                context.SelectionText = Trim(SafeString(delegate { return mail.Body; }), 2000);
                try
                {
                    var folder = mail.Parent as Outlook.MAPIFolder;
                    if (folder != null)
                    {
                        context.ContainerName = folder.Name;
                        context.DocumentPath = folder.FolderPath;
                    }
                }
                catch
                {
                }
                return context;
            }

            var currentFolder = CurrentFolder();
            if (currentFolder != null)
            {
                context.DocumentTitle = SafeString(delegate { return currentFolder.Name; });
                context.DocumentPath = SafeString(delegate { return currentFolder.FolderPath; });
                context.ContainerName = context.DocumentTitle;
            }
            else
            {
                context.DocumentTitle = "Outlook";
            }

            return context;
        }

        public IEnumerable<ToolDefinition> GetBuiltInTools()
        {
            return new[]
            {
                Skill("outlook.get_context", "Read-only: Return active mail or folder context.", "{}"),
                Skill("outlook.read_current_mail", "Read-only: Read selected or open mail.", "{\"maxChars\":12000}"),
                Skill("outlook.read_selection", "Read-only: Read selected email metadata and body.", "{\"maxChars\":12000}"),
                Skill("outlook.read_mail_by_entry_id", "Read-only: Read one mail item by EntryID.", "{\"entryId\":\"\",\"maxChars\":12000}"),
                Skill("outlook.search_mail", "Read-only: Search recent mail in the current folder by text.", "{\"query\":\"text\",\"maxItems\":100,\"maxBodyChars\":1000}"),
                Skill("outlook.list_attachments", "Read-only: List attachments for selected mail or EntryID.", "{\"entryId\":\"\"}"),
                Skill("outlook.create_mail_draft", "Mutates document: Create and display a new mail draft without sending it.", "{\"to\":\"person@example.com\",\"cc\":\"\",\"bcc\":\"\",\"subject\":\"Subject\",\"body\":\"Body\"}", true, true, 1),
                Skill("outlook.create_reply_draft", "Mutates document: Create and display a reply draft for selected mail.", "{\"body\":\"Reply body\"}", true, true, 1),
                Skill("outlook.create_reply_all_draft", "Mutates document: Create and display a reply-all draft for selected mail.", "{\"body\":\"Reply body\"}", true, true, 1),
                Skill("outlook.create_forward_draft", "Mutates document: Create and display a forward draft for selected mail.", "{\"to\":\"person@example.com\",\"body\":\"Forward body\"}", true, true, 1),
                Skill("outlook.set_categories", "Mutates document: Set categories on selected mail.", "{\"categories\":\"Category A, Category B\"}", true, false, 1),
                Skill("outlook.mark_as_read", "Mutates document: Mark selected mail as read.", "{}", true, false, 1),
                Skill("outlook.collect_folder_mail", "Read-only: Collect recent mail metadata from current folder for analysis.", "{\"maxItems\":100,\"maxBodyChars\":1000}"),
                Skill("outlook.collect_monthly_summary_data", "Read-only: Collect current folder mail grouped by month for archive summary.", "{\"maxItems\":500,\"maxBodyChars\":500}")
            };
        }

        public string GetDocumentSnapshot(int maxChars)
        {
            var mail = SelectedMail();
            if (mail == null)
            {
                var folder = CurrentFolder();
                return folder == null ? "No selected email." : "Current folder: " + folder.FolderPath;
            }

            return Trim("Subject: " + mail.Subject + "\nFrom: " + mail.SenderName + "\nReceived: " + mail.ReceivedTime + "\n\n" + mail.Body, maxChars);
        }

        public string GetVbaSnapshot(int maxChars)
        {
            return string.Empty;
        }

        public void PrepareForContextCapture()
        {
            try
            {
                var explorer = _application.ActiveExplorer();
                if (explorer != null)
                {
                    explorer.Activate();
                    return;
                }

                var inspector = _application.ActiveInspector();
                if (inspector != null)
                {
                    inspector.Activate();
                }
            }
            catch
            {
            }
        }

        public ContextNote CaptureSelectionContext(string mode, int maxChars)
        {
            var mail = RequireSelectedMail();
            var referenceOnly = string.Equals(mode, "reference", StringComparison.OrdinalIgnoreCase);
            var reference = string.IsNullOrWhiteSpace(mail.EntryID) ? mail.Subject : mail.EntryID;
            var text = referenceOnly
                ? "Reference only. Use Outlook tools with the selected email if exact body content is needed."
                : Trim("Subject: " + mail.Subject + "\nFrom: " + mail.SenderName + " <" + mail.SenderEmailAddress + ">\nReceived: " + mail.ReceivedTime + "\n\n" + mail.Body, maxChars);

            return new ContextNote
            {
                Host = HostName,
                Kind = referenceOnly ? "mail-reference" : "mail",
                Title = "Outlook mail: " + mail.Subject,
                Reference = reference,
                Source = mail.Subject,
                Text = text,
                Preview = Trim(text, 360),
                DetailsJson = JsonConvert.SerializeObject(new
                {
                    subject = mail.Subject,
                    sender = mail.SenderName,
                    senderEmail = mail.SenderEmailAddress,
                    received = mail.ReceivedTime,
                    entryId = mail.EntryID,
                    mode = referenceOnly ? "reference" : "text"
                })
            };
        }

        public ToolResult ExecuteTool(ToolCommand command)
        {
            try
            {
                switch (command.ToolId)
                {
                    case "outlook.get_context":
                        return ToolResult.Ok("Outlook context collected.", JsonConvert.SerializeObject(GetOfficeContext()));
                    case "outlook.read_current_mail":
                    case "outlook.read_selection":
                        return ReadSelection(command);
                    case "outlook.read_mail_by_entry_id":
                        return ReadMailByEntryId(command);
                    case "outlook.search_mail":
                        return SearchMail(command);
                    case "outlook.list_attachments":
                        return ListAttachments(command);
                    case "outlook.create_mail_draft":
                        return CreateMailDraft(command);
                    case "outlook.create_reply_draft":
                        return DraftReply(command);
                    case "outlook.create_reply_all_draft":
                        return DraftReplyAll(command);
                    case "outlook.create_forward_draft":
                        return DraftForward(command);
                    case "outlook.set_categories":
                        return SetCategories(command);
                    case "outlook.mark_as_read":
                        return MarkAsRead();
                    case "outlook.collect_folder_mail":
                        return CollectFolderMail(command, false);
                    case "outlook.collect_monthly_summary_data":
                        return CollectFolderMail(command, true);
                    default:
                        return ToolResult.Fail("Unsupported Outlook tool: " + command.ToolId);
                }
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ex.Message);
            }
        }

        private ToolResult ReadSelection(ToolCommand command)
        {
            var mail = RequireSelectedMail();
            var maxChars = ToolArgumentReader.Int32(command.Arguments, "maxChars", 12000);
            return ToolResult.Ok("Selected email read.", JsonConvert.SerializeObject(MailPayload(mail, maxChars)));
        }

        private ToolResult ReadMailByEntryId(ToolCommand command)
        {
            var entryId = ToolArgumentReader.String(command.Arguments, "entryId", string.Empty);
            if (string.IsNullOrWhiteSpace(entryId))
            {
                return ToolResult.Fail("entryId is required.");
            }

            var mail = _application.Session.GetItemFromID(entryId, Type.Missing) as Outlook.MailItem;
            if (mail == null)
            {
                return ToolResult.Fail("Mail item not found: " + entryId);
            }

            var maxChars = ToolArgumentReader.Int32(command.Arguments, "maxChars", 12000);
            return ToolResult.Ok("Email read by EntryID.", JsonConvert.SerializeObject(MailPayload(mail, maxChars)));
        }

        private ToolResult SearchMail(ToolCommand command)
        {
            var query = ToolArgumentReader.String(command.Arguments, "query", string.Empty);
            if (string.IsNullOrWhiteSpace(query))
            {
                return ToolResult.Fail("query is required.");
            }

            var folder = CurrentFolder();
            if (folder == null)
            {
                return ToolResult.Fail("No current Outlook folder.");
            }

            var maxItems = Math.Max(1, Math.Min(500, ToolArgumentReader.Int32(command.Arguments, "maxItems", 100)));
            var maxBodyChars = ToolArgumentReader.Int32(command.Arguments, "maxBodyChars", 1000);
            var matches = new List<object>();
            var items = folder.Items;
            items.Sort("[ReceivedTime]", true);
            for (var i = 1; i <= items.Count && matches.Count < maxItems; i++)
            {
                var mail = items[i] as Outlook.MailItem;
                if (mail == null)
                {
                    continue;
                }

                var haystack = (mail.Subject ?? string.Empty) + "\n" +
                    (mail.SenderName ?? string.Empty) + "\n" +
                    (mail.SenderEmailAddress ?? string.Empty) + "\n" +
                    (mail.Body ?? string.Empty);
                if (haystack.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                matches.Add(MailPayload(mail, maxBodyChars));
            }

            return ToolResult.Ok("Mail search matches: " + matches.Count, JsonConvert.SerializeObject(new { folder = folder.FolderPath, messages = matches }));
        }

        private ToolResult ListAttachments(ToolCommand command)
        {
            var entryId = ToolArgumentReader.String(command.Arguments, "entryId", string.Empty);
            var mail = string.IsNullOrWhiteSpace(entryId)
                ? RequireSelectedMail()
                : _application.Session.GetItemFromID(entryId, Type.Missing) as Outlook.MailItem;
            if (mail == null)
            {
                return ToolResult.Fail("Mail item not found.");
            }

            var attachments = new List<object>();
            for (var i = 1; i <= mail.Attachments.Count; i++)
            {
                var attachment = mail.Attachments[i];
                attachments.Add(new
                {
                    index = i,
                    fileName = attachment.FileName,
                    displayName = attachment.DisplayName,
                    size = attachment.Size,
                    type = attachment.Type.ToString()
                });
            }

            return ToolResult.Ok("Attachments listed: " + attachments.Count, JsonConvert.SerializeObject(attachments));
        }

        private ToolResult CreateMailDraft(ToolCommand command)
        {
            var mail = _application.CreateItem(Outlook.OlItemType.olMailItem) as Outlook.MailItem;
            if (mail == null)
            {
                return ToolResult.Fail("Could not create mail draft.");
            }

            mail.To = ToolArgumentReader.String(command.Arguments, "to", string.Empty);
            mail.CC = ToolArgumentReader.String(command.Arguments, "cc", string.Empty);
            mail.BCC = ToolArgumentReader.String(command.Arguments, "bcc", string.Empty);
            mail.Subject = ToolArgumentReader.String(command.Arguments, "subject", string.Empty);
            mail.Body = ToolArgumentReader.String(command.Arguments, "body", string.Empty);
            mail.Display(false);
            return ToolResult.Ok("Mail draft displayed.");
        }

        private ToolResult DraftReply(ToolCommand command)
        {
            var mail = RequireSelectedMail();
            var body = ToolArgumentReader.String(command.Arguments, "body", string.Empty);
            var reply = mail.Reply() as Outlook.MailItem;
            if (reply == null)
            {
                return ToolResult.Fail("Could not create reply.");
            }

            reply.Body = body + "\n\n" + reply.Body;
            reply.Display(false);
            return ToolResult.Ok("Reply draft displayed.");
        }

        private ToolResult DraftReplyAll(ToolCommand command)
        {
            var mail = RequireSelectedMail();
            var body = ToolArgumentReader.String(command.Arguments, "body", string.Empty);
            var reply = mail.ReplyAll() as Outlook.MailItem;
            if (reply == null)
            {
                return ToolResult.Fail("Could not create reply-all draft.");
            }

            reply.Body = body + "\n\n" + reply.Body;
            reply.Display(false);
            return ToolResult.Ok("Reply-all draft displayed.");
        }

        private ToolResult DraftForward(ToolCommand command)
        {
            var mail = RequireSelectedMail();
            var body = ToolArgumentReader.String(command.Arguments, "body", string.Empty);
            var forward = mail.Forward() as Outlook.MailItem;
            if (forward == null)
            {
                return ToolResult.Fail("Could not create forward draft.");
            }

            forward.To = ToolArgumentReader.String(command.Arguments, "to", string.Empty);
            forward.Body = body + "\n\n" + forward.Body;
            forward.Display(false);
            return ToolResult.Ok("Forward draft displayed.");
        }

        private ToolResult SetCategories(ToolCommand command)
        {
            var mail = RequireSelectedMail();
            mail.Categories = ToolArgumentReader.String(command.Arguments, "categories", string.Empty);
            mail.Save();
            return ToolResult.Ok("Mail categories updated.");
        }

        private ToolResult MarkAsRead()
        {
            var mail = RequireSelectedMail();
            mail.UnRead = false;
            mail.Save();
            return ToolResult.Ok("Mail marked as read.");
        }

        private ToolResult CollectFolderMail(ToolCommand command, bool groupedByMonth)
        {
            var folder = CurrentFolder();
            if (folder == null)
            {
                return ToolResult.Fail("No current Outlook folder.");
            }

            var maxItems = ToolArgumentReader.Int32(command.Arguments, "maxItems", groupedByMonth ? 500 : 100);
            var maxBodyChars = ToolArgumentReader.Int32(command.Arguments, "maxBodyChars", groupedByMonth ? 500 : 1000);
            var rows = new List<object>();
            var monthly = new Dictionary<string, List<object>>();
            var items = folder.Items;
            items.Sort("[ReceivedTime]", true);

            var count = Math.Min(items.Count, Math.Max(1, maxItems));
            for (var i = 1; i <= count; i++)
            {
                var mail = items[i] as Outlook.MailItem;
                if (mail == null)
                {
                    continue;
                }

                var record = new
                {
                    subject = mail.Subject,
                    sender = mail.SenderName,
                    received = mail.ReceivedTime,
                    body = Trim(mail.Body, maxBodyChars)
                };

                if (groupedByMonth)
                {
                    var key = mail.ReceivedTime.ToString("yyyy-MM");
                    if (!monthly.ContainsKey(key))
                    {
                        monthly[key] = new List<object>();
                    }
                    monthly[key].Add(record);
                }
                else
                {
                    rows.Add(record);
                }
            }

            var data = groupedByMonth
                ? JsonConvert.SerializeObject(new { folder = folder.FolderPath, months = monthly })
                : JsonConvert.SerializeObject(new { folder = folder.FolderPath, messages = rows });
            return ToolResult.Ok("Mail data collected.", data);
        }

        private static object MailPayload(Outlook.MailItem mail, int maxBodyChars)
        {
            return new
            {
                entryId = mail.EntryID,
                subject = mail.Subject,
                sender = mail.SenderName,
                senderEmail = mail.SenderEmailAddress,
                received = mail.ReceivedTime,
                categories = mail.Categories,
                unread = mail.UnRead,
                body = Trim(mail.Body, maxBodyChars)
            };
        }

        private Outlook.MailItem SelectedMail()
        {
            if (HasTargetMail())
            {
                return TargetMail();
            }

            if (HasTargetFolder())
            {
                return null;
            }

            try
            {
                var inspector = _application.ActiveInspector();
                if (inspector != null)
                {
                    var currentItem = inspector.CurrentItem as Outlook.MailItem;
                    if (currentItem != null)
                    {
                        return currentItem;
                    }
                }
            }
            catch
            {
            }

            try
            {
                var explorer = _application.ActiveExplorer();
                if (explorer == null || explorer.Selection == null || explorer.Selection.Count == 0)
                {
                    return null;
                }

                return explorer.Selection[1] as Outlook.MailItem;
            }
            catch
            {
                return null;
            }
        }

        private Outlook.MailItem RequireSelectedMail()
        {
            var mail = SelectedMail();
            if (mail == null)
            {
                throw new InvalidOperationException(_target != null && !string.IsNullOrWhiteSpace(_target.EntryId)
                    ? "Target Outlook mail item is not available."
                    : "Select an email first.");
            }
            return mail;
        }

        private Outlook.MailItem TargetMail()
        {
            if (!HasTargetMail())
            {
                return null;
            }

            try
            {
                return _application.Session.GetItemFromID(_target.EntryId, Type.Missing) as Outlook.MailItem;
            }
            catch
            {
                return null;
            }
        }

        private Outlook.MAPIFolder CurrentFolder()
        {
            if (HasTargetFolder())
            {
                return TargetFolder();
            }

            if (HasTargetMail())
            {
                return null;
            }

            try
            {
                var explorer = _application.ActiveExplorer();
                return explorer == null ? null : explorer.CurrentFolder as Outlook.MAPIFolder;
            }
            catch
            {
                return null;
            }
        }

        private Outlook.MAPIFolder TargetFolder()
        {
            if (!HasTargetFolder())
            {
                return null;
            }

            try
            {
                foreach (Outlook.MAPIFolder root in _application.Session.Folders)
                {
                    var found = FindFolder(root, _target.FolderPath);
                    if (found != null)
                    {
                        return found;
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private bool HasTargetMail()
        {
            return _target != null && !string.IsNullOrWhiteSpace(_target.EntryId);
        }

        private bool HasTargetFolder()
        {
            return _target != null && !string.IsNullOrWhiteSpace(_target.FolderPath);
        }

        private static Outlook.MAPIFolder FindFolder(Outlook.MAPIFolder folder, string folderPath)
        {
            if (folder == null)
            {
                return null;
            }

            if (string.Equals(folder.FolderPath, folderPath, StringComparison.OrdinalIgnoreCase))
            {
                return folder;
            }

            foreach (Outlook.MAPIFolder child in folder.Folders)
            {
                var found = FindFolder(child, folderPath);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private long ActiveOutlookHwnd()
        {
            try
            {
                var inspector = _application.ActiveInspector();
                var hwnd = NativeWindowInfo.ReadLongMemberPath(inspector, "HWND");
                if (hwnd != 0)
                {
                    return hwnd;
                }
            }
            catch
            {
            }

            try
            {
                return NativeWindowInfo.ReadLongMemberPath(_application.ActiveExplorer(), "HWND");
            }
            catch
            {
                return 0;
            }
        }

        private delegate string StringGetter();

        private static string SafeString(StringGetter getter)
        {
            try { return getter(); }
            catch { return string.Empty; }
        }

        private static ToolDefinition Skill(string id, string description, string schema, bool mutatesDocument = false, bool agentCanRun = true, int riskLevel = 0)
        {
            return new ToolDefinition { Id = id, Host = "Outlook", Name = id, Description = description, ArgumentSchemaJson = schema, BuiltIn = true, Enabled = true, MutatesDocument = mutatesDocument, AgentCanRun = agentCanRun, RiskLevel = riskLevel };
        }

        private static string Trim(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            {
                return text;
            }
            return text.Substring(0, maxChars) + "\n...[truncated]";
        }
    }
}
