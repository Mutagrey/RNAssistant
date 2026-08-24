using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Outlook = Microsoft.Office.Interop.Outlook;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office;
using RNAssistant.Office.Tools;

namespace RNAssistant.OfficeHosts
{
    public sealed class OutlookAdapter : IOfficeApplicationAdapter, IOfficeContextProvider, IOfficeBuiltInSkillProvider
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
                Tool("outlook.get_context", "Read-only: Return active mail or folder context.", "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}"),
                Tool("outlook.read_mail", "Read-only: Read message data, attachments, or both for selected/open mail or one exact EntryID.", "{\"type\":\"object\",\"properties\":{\"entryId\":{\"type\":\"string\",\"description\":\"Optional exact Outlook EntryID; omit to use selected or open mail.\"},\"content\":{\"type\":\"string\",\"enum\":[\"message\",\"attachments\",\"both\"],\"description\":\"Mail content to return.\",\"default\":\"message\"},\"maxChars\":{\"type\":\"integer\",\"description\":\"Maximum number of body characters returned.\",\"default\":12000}},\"required\":[],\"additionalProperties\":false}", canSourceHtmlData: true),
                Tool("outlook.search_mail", "Read-only: Search recent mail fields with literal or regex matching and return field coordinates.", "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"description\":\"Non-empty literal or regular-expression search query.\",\"minLength\":1},\"mode\":{\"type\":\"string\",\"description\":\"Text matching mode: literal or regex.\",\"default\":\"literal\",\"enum\":[\"literal\",\"regex\"]},\"matchCase\":{\"type\":\"boolean\",\"description\":\"Whether matching is case-sensitive.\",\"default\":false},\"wholeWord\":{\"type\":\"boolean\",\"description\":\"Whether only whole-word matches are accepted.\",\"default\":false},\"fields\":{\"type\":\"string\",\"description\":\"Comma-separated mail fields to search: subject, sender, recipients, body.\",\"default\":\"subject,sender,body\"},\"maxItems\":{\"type\":\"integer\",\"description\":\"Maximum number of source items inspected.\",\"default\":100},\"maxResults\":{\"type\":\"integer\",\"description\":\"Maximum number of matches returned.\",\"default\":50},\"maxBodyChars\":{\"type\":\"integer\",\"description\":\"Maximum number of body characters returned per item.\",\"default\":1000},\"contextChars\":{\"type\":\"integer\",\"description\":\"Maximum context characters returned around each match.\",\"default\":80}},\"required\":[\"query\"],\"additionalProperties\":false}"),
                Tool("outlook.create_draft", "Mutates document: Create and display a new, reply, reply-all, or forward draft without sending it.", "{\"type\":\"object\",\"properties\":{\"kind\":{\"type\":\"string\",\"enum\":[\"new\",\"reply\",\"replyAll\",\"forward\"],\"description\":\"Draft operation.\"},\"to\":{\"type\":\"string\",\"description\":\"Semicolon-separated primary recipients for new/forward drafts.\"},\"cc\":{\"type\":\"string\",\"description\":\"Semicolon-separated CC recipients for a new draft.\"},\"bcc\":{\"type\":\"string\",\"description\":\"Semicolon-separated BCC recipients for a new draft.\"},\"subject\":{\"type\":\"string\",\"description\":\"Subject for a new draft.\"},\"body\":{\"type\":\"string\",\"description\":\"Body text for the draft.\"}},\"required\":[\"kind\"],\"additionalProperties\":false}", true, true, 1),
                Tool("outlook.update_mail", "Mutates document: Set categories or mark the selected mail as read with one explicit operation.", "{\"type\":\"object\",\"properties\":{\"kind\":{\"type\":\"string\",\"enum\":[\"categories\",\"markRead\"],\"description\":\"Mail update operation.\"},\"categories\":{\"type\":\"string\",\"description\":\"Comma-separated categories for kind=categories; empty text clears them.\"}},\"required\":[\"kind\"],\"additionalProperties\":false}", true, true, 1),
                Tool("outlook.collect_mail", "Read-only: Collect recent mail metadata from the current folder, optionally grouped by month.", "{\"type\":\"object\",\"properties\":{\"groupBy\":{\"type\":\"string\",\"enum\":[\"none\",\"month\"],\"description\":\"Optional aggregation applied by runtime.\",\"default\":\"none\"},\"maxItems\":{\"type\":\"integer\",\"description\":\"Maximum number of source items inspected.\",\"default\":100},\"maxBodyChars\":{\"type\":\"integer\",\"description\":\"Maximum number of body characters returned per item.\",\"default\":1000}},\"required\":[],\"additionalProperties\":false}", canSourceHtmlData: true)
            };
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
            var mail = SelectedMail();
            if (mail == null)
            {
                var folder = CurrentFolder();
                return folder == null ? "No selected email." : "Current folder: " + folder.FolderPath;
            }

            return Trim("Subject: " + mail.Subject + "\nFrom: " + mail.SenderName + "\nReceived: " + mail.ReceivedTime + "\n\n" + mail.Body, maxChars);
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
                    case "outlook.read_mail":
                        return ReadMail(command);
                    case "outlook.search_mail":
                        return SearchMail(command);
                    case "outlook.create_draft":
                        return CreateDraft(command);
                    case "outlook.update_mail":
                        return UpdateMail(command);
                    case "outlook.collect_mail":
                        return CollectFolderMail(command,
                            string.Equals(ToolArgumentReader.String(command.Arguments, "groupBy", "none"), "month", StringComparison.OrdinalIgnoreCase));
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

        private ToolResult ReadMail(ToolCommand command)
        {
            var content = ToolArgumentReader.String(command.Arguments, "content", "message");
            if (string.Equals(content, "attachments", StringComparison.OrdinalIgnoreCase)) return ListAttachments(command);
            var message = string.IsNullOrWhiteSpace(ToolArgumentReader.String(command.Arguments, "entryId", string.Empty))
                ? ReadSelection(command)
                : ReadMailByEntryId(command);
            if (!message.Success || string.Equals(content, "message", StringComparison.OrdinalIgnoreCase)) return message;
            if (!string.Equals(content, "both", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Fail("content must be message, attachments, or both.");
            }
            var attachments = ListAttachments(command);
            if (!attachments.Success) return attachments;
            return ToolResult.Ok("Email and attachments read.", new JObject
            {
                ["message"] = JToken.Parse(message.DataJson ?? "{}"),
                ["attachments"] = JToken.Parse(attachments.DataJson ?? "[]")
            }.ToString(Formatting.None));
        }

        private ToolResult CreateDraft(ToolCommand command)
        {
            var kind = ToolArgumentReader.String(command.Arguments, "kind", string.Empty);
            if (string.Equals(kind, "new", StringComparison.OrdinalIgnoreCase)) return CreateMailDraft(command);
            if (string.Equals(kind, "reply", StringComparison.OrdinalIgnoreCase)) return DraftReply(command);
            if (string.Equals(kind, "replyAll", StringComparison.OrdinalIgnoreCase)) return DraftReplyAll(command);
            if (string.Equals(kind, "forward", StringComparison.OrdinalIgnoreCase)) return DraftForward(command);
            return ToolResult.Fail("kind must be new, reply, replyAll, or forward.");
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
            var maxResults = Math.Max(1, Math.Min(500, ToolArgumentReader.Int32(command.Arguments, "maxResults", 50)));
            var maxBodyChars = ToolArgumentReader.Int32(command.Arguments, "maxBodyChars", 1000);
            var contextChars = Math.Max(0, Math.Min(1000, ToolArgumentReader.Int32(command.Arguments, "contextChars", 80)));
            var requestedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var requested in (ToolArgumentReader.String(command.Arguments, "fields", "subject,sender,body") ?? string.Empty).Split(','))
            {
                var field = (requested ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(field)) requestedFields.Add(field);
            }
            if (requestedFields.Count == 0)
            {
                return ToolResult.Fail("fields must contain at least one mail field.", null, "invalid_arguments", false);
            }
            foreach (var field in requestedFields)
            {
                if (!string.Equals(field, "subject", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(field, "sender", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(field, "recipients", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(field, "body", StringComparison.OrdinalIgnoreCase))
                {
                    return ToolResult.Fail("Unsupported Outlook search field: " + field + ".", null, "invalid_arguments", false);
                }
            }
            var options = new TextPatternOptions { Mode = ToolArgumentReader.String(command.Arguments, "mode", "literal"), MatchCase = ToolArgumentReader.Boolean(command.Arguments, "matchCase", false), WholeWord = ToolArgumentReader.Boolean(command.Arguments, "wholeWord", false) };
            var matches = new List<object>();
            var total = 0;
            var items = folder.Items;
            items.Sort("[ReceivedTime]", true);
            try
            {
                for (var i = 1; i <= items.Count && i <= maxItems; i++)
                {
                    var mail = items[i] as Outlook.MailItem;
                    if (mail == null) continue;
                    var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "subject", mail.Subject ?? string.Empty },
                        { "sender", (mail.SenderName ?? string.Empty) + " <" + (mail.SenderEmailAddress ?? string.Empty) + ">" },
                        { "recipients", "To: " + (mail.To ?? string.Empty) + "; CC: " + (mail.CC ?? string.Empty) + "; BCC: " + (mail.BCC ?? string.Empty) },
                        { "body", mail.Body ?? string.Empty }
                    };
                    foreach (var field in fields)
                    {
                        if (!requestedFields.Contains(field.Key)) continue;
                        var found = TextPatternEngine.Find(field.Value, query, options, Math.Max(1, maxResults - matches.Count), contextChars);
                        total += found.MatchCount;
                        foreach (var match in found.Matches)
                        {
                            if (matches.Count >= maxResults) break;
                            matches.Add(new { entryId = mail.EntryID, subject = mail.Subject, received = mail.ReceivedTime, field = field.Key, start = match.Index, end = match.Index + match.Length, preview = match.Preview, body = Trim(mail.Body, maxBodyChars) });
                        }
                    }
                }
                return ToolResult.Ok("Mail search matches: " + total, JsonConvert.SerializeObject(new { folder = folder.FolderPath, matchCount = total, returnedCount = matches.Count, truncated = total > matches.Count, matches = matches }));
            }
            catch (TextPatternException ex) { return ToolResult.Fail(ex.Message, null, ex.ErrorCode, false); }
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

        private ToolResult UpdateMail(ToolCommand command)
        {
            var kind = ToolArgumentReader.String(command.Arguments, "kind", string.Empty);
            if (string.Equals(kind, "categories", StringComparison.OrdinalIgnoreCase))
            {
                if (!command.Arguments.ContainsKey("categories")) return ToolResult.Fail("categories is required for kind=categories.");
                return SetCategories(command);
            }
            if (string.Equals(kind, "markRead", StringComparison.OrdinalIgnoreCase)) return MarkAsRead();
            return ToolResult.Fail("kind must be categories or markRead.");
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

        private static ToolDefinition Tool(string id, string description, string schema, bool mutatesDocument = false, bool agentCanRun = true, int riskLevel = 0, bool canSourceHtmlData = false)
        {
            return new ToolDefinition { Id = id, Host = "Outlook", Name = id, Description = description, ArgumentSchemaJson = schema, BuiltIn = true, Enabled = true, MutatesDocument = mutatesDocument, AgentCanRun = agentCanRun, RiskLevel = riskLevel, CanSourceHtmlData = canSourceHtmlData };
        }

        private static string Trim(string text, int maxChars)
        {
            maxChars = Math.Max(0, maxChars);
            if (maxChars == 0)
            {
                return string.Empty;
            }
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            {
                return text;
            }
            return text.Substring(0, maxChars) + "\n...[truncated]";
        }
    }
}
