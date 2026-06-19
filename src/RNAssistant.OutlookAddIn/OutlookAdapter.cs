using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Outlook = Microsoft.Office.Interop.Outlook;
using RNAssistant.Core.Models;
using RNAssistant.Office;
using RNAssistant.Office.Tools;

namespace RNAssistant.OutlookAddIn
{
    public sealed class OutlookAdapter : IOfficeApplicationAdapter
    {
        private readonly Outlook.Application _application;

        public OutlookAdapter(Outlook.Application application)
        {
            _application = application;
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

        public string LegacyDocumentKey
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

        public IEnumerable<ToolDefinition> GetBuiltInTools()
        {
            return new[]
            {
                Skill("outlook.read_selection", "Read selected email metadata and body.", "{\"maxChars\":12000}"),
                Skill("outlook.draft_reply", "Create and display a reply draft for selected email.", "{\"body\":\"Reply body\"}", true, true),
                Skill("outlook.collect_folder_mail", "Collect recent mail metadata from current folder for LLM analysis.", "{\"maxItems\":100,\"maxBodyChars\":1000}"),
                Skill("outlook.collect_monthly_summary_data", "Collect current folder mail grouped by month for archive summary.", "{\"maxItems\":500,\"maxBodyChars\":500}")
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
                    case "outlook.read_selection":
                        return ReadSelection(command);
                    case "outlook.draft_reply":
                        return DraftReply(command);
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
            return ToolResult.Ok("Selected email read.", JsonConvert.SerializeObject(new
            {
                subject = mail.Subject,
                sender = mail.SenderName,
                senderEmail = mail.SenderEmailAddress,
                received = mail.ReceivedTime,
                body = Trim(mail.Body, maxChars)
            }));
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

        private Outlook.MailItem SelectedMail()
        {
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
                throw new InvalidOperationException("Select an email first.");
            }
            return mail;
        }

        private Outlook.MAPIFolder CurrentFolder()
        {
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

        private static ToolDefinition Skill(string id, string description, string schema, bool mutatesDocument = false, bool agentCanRun = true)
        {
            return new ToolDefinition { Id = id, Host = "Outlook", Name = id, Description = description, ArgumentSchemaJson = schema, BuiltIn = true, Enabled = true, MutatesDocument = mutatesDocument, AgentCanRun = agentCanRun };
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
