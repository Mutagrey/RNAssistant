using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using Word = Microsoft.Office.Interop.Word;
using RNAssistant.Core.Models;
using RNAssistant.Office;
using RNAssistant.Office.Tools;

namespace RNAssistant.OfficeHosts
{
    public sealed class WordAdapter : IOfficeApplicationAdapter, IOfficeContextProvider
    {
        private readonly Word.Application _application;
        private readonly OfficeTargetDescriptor _target;

        public WordAdapter(Word.Application application)
            : this(application, null)
        {
        }

        public WordAdapter(Word.Application application, OfficeTargetDescriptor target)
        {
            _application = application;
            _target = target;
        }

        public string HostName { get { return "Word"; } }

        public string DocumentKey
        {
            get
            {
                var doc = ActiveDocument();
                if (doc == null)
                {
                    return "Word:NoDocument";
                }

                return DocumentIdentity.ForOfficeDocument(
                    HostName,
                    doc.Path,
                    RuntimeDocumentKey,
                    () => doc.CustomDocumentProperties);
            }
        }

        public string RuntimeDocumentKey
        {
            get
            {
                var doc = ActiveDocument();
                return doc == null ? "Word:NoDocument" : "Word:Runtime:" + doc.GetHashCode().ToString("x");
            }
        }

        public string LegacyDocumentKey
        {
            get
            {
                var doc = ActiveDocument();
                if (doc == null)
                {
                    return "Word:NoDocument";
                }

                return string.IsNullOrWhiteSpace(doc.FullName) ? RuntimeDocumentKey : doc.FullName;
            }
        }

        public string DocumentTitle
        {
            get
            {
                var doc = ActiveDocument();
                return doc == null ? "No document" : doc.Name;
            }
        }

        public OfficeContext GetOfficeContext()
        {
            var context = new OfficeContext { Host = HostName };
            try
            {
                var hwnd = NativeWindowInfo.ReadLongMemberPath(_application, "ActiveWindow", "Hwnd");
                context.AppHwnd = new IntPtr(hwnd);
                context.ProcessId = NativeWindowInfo.GetProcessId(hwnd);
            }
            catch
            {
            }

            var doc = ActiveDocument();
            if (doc != null)
            {
                context.DocumentPath = SafeString(delegate { return doc.FullName; });
                context.DocumentTitle = SafeString(delegate { return doc.Name; });
            }

            try
            {
                var range = doc == null ? null : ResolveSelectionRange(doc);
                context.SelectionAddress = range == null ? null : range.Start + ":" + range.End;
                context.SelectionText = range == null ? null : Trim(range.Text, 2000);
            }
            catch
            {
            }

            return context;
        }

        public IEnumerable<ToolDefinition> GetBuiltInTools()
        {
            return new[]
            {
                Skill("word.get_context", "Return active Word document and selection context.", "{}"),
                Skill("word.get_selection_text", "Read current Word selection text.", "{}"),
                Skill("word.read_document", "Read current document text.", "{\"maxChars\":12000}"),
                Skill("word.read_selection", "Read current Word selection.", "{}"),
                Skill("word.insert_text", "Insert text at current cursor position.", "{\"text\":\"Text to insert\"}", true, true),
                Skill("word.replace_selection", "Replace selected text.", "{\"text\":\"Replacement text\"}", true, true),
                Skill("word.add_comment", "Add a comment to the current selection.", "{\"text\":\"Comment text\"}", true, true),
                Skill("word.vba_read_project", "Read VBA project modules and source code when Trust Access to VBA project is enabled.", "{\"maxChars\":30000}"),
                Skill("word.vba_read_module", "Read one VBA module by name.", "{\"moduleName\":\"Module1\",\"maxChars\":30000}"),
                Skill("word.vba_replace_module", "Replace a VBA module source code; RNAssistant stores rollback backups before replacement.", "{\"moduleName\":\"Module1\",\"code\":\"Sub Test()\\nEnd Sub\",\"createIfMissing\":true}", true, false),
                Skill("word.insert_vba_module", "Insert VBA module when Trust Access to VBA project is enabled; otherwise returns copyable code.", "{\"moduleName\":\"Module1\",\"code\":\"Sub Test()\\nEnd Sub\"}", true, false),
                Skill("word.run_macro", "Run a Word VBA macro by name.", "{\"macroName\":\"Module1.Test\"}", true, false)
            };
        }

        public string GetDocumentSnapshot(int maxChars)
        {
            var doc = ActiveDocument();
            if (doc == null)
            {
                return "No active Word document.";
            }

            return Trim(doc.Range().Text, maxChars);
        }

        public string GetVbaSnapshot(int maxChars)
        {
            var doc = ActiveDocument();
            if (doc == null)
            {
                return "No active Word document.";
            }

            return VbaProjectSupport.GetSnapshot(doc, doc.Name, maxChars);
        }

        public void PrepareForContextCapture()
        {
            try
            {
                var doc = ActiveDocument();
                if (doc != null)
                {
                    doc.Activate();
                    return;
                }

                _application.Activate();
            }
            catch
            {
            }
        }

        public ContextNote CaptureSelectionContext(string mode, int maxChars)
        {
            var doc = RequireDocument();
            var range = ResolveSelectionRange(doc);
            var referenceOnly = string.Equals(mode, "reference", StringComparison.OrdinalIgnoreCase);
            var reference = doc.Name + " chars " + range.Start + "-" + range.End;
            var selectedText = Trim(range.Text, maxChars);
            if (string.IsNullOrWhiteSpace(selectedText) && !referenceOnly)
            {
                throw new InvalidOperationException("Select Word text first.");
            }

            var text = referenceOnly
                ? "Reference only. Use Word tools with the current selection/document if exact text is needed."
                : selectedText;

            return new ContextNote
            {
                Host = HostName,
                Kind = referenceOnly ? "text-reference" : "text-selection",
                Title = "Word selection",
                Reference = reference,
                Source = reference,
                Text = text,
                Preview = Trim(text, 360),
                DetailsJson = JsonConvert.SerializeObject(new
                {
                    document = doc.Name,
                    start = range.Start,
                    end = range.End,
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
                    case "word.get_context":
                        return ToolResult.Ok("Word context collected.", JsonConvert.SerializeObject(GetOfficeContext()));
                    case "word.read_document":
                        return ReadDocument(command);
                    case "word.get_selection_text":
                    case "word.read_selection":
                        return ToolResult.Ok("Selection read.", JsonConvert.SerializeObject(new { text = SelectionText() }));
                    case "word.insert_text":
                        InsertText(ToolArgumentReader.String(command.Arguments, "text", string.Empty));
                        return ToolResult.Ok("Text inserted.");
                    case "word.replace_selection":
                        ResolveSelectionRange(RequireDocument()).Text = ToolArgumentReader.String(command.Arguments, "text", string.Empty);
                        return ToolResult.Ok("Selection replaced.");
                    case "word.add_comment":
                        var doc = RequireDocument();
                        doc.Comments.Add(ResolveSelectionRange(doc), ToolArgumentReader.String(command.Arguments, "text", string.Empty));
                        return ToolResult.Ok("Comment added.");
                    case "word.vba_read_project":
                        return ReadVbaProject(command);
                    case "word.vba_read_module":
                        return ReadVbaModule(command);
                    case "word.vba_replace_module":
                        return ReplaceVbaModule(command);
                    case "word.insert_vba_module":
                        return InsertVbaModule(command);
                    case "word.run_macro":
                        return RunMacro(command);
                    default:
                        return ToolResult.Fail("Unsupported Word tool: " + command.ToolId);
                }
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ex.Message);
            }
        }

        private ToolResult ReadDocument(ToolCommand command)
        {
            var maxChars = ToolArgumentReader.Int32(command.Arguments, "maxChars", 12000);
            var doc = RequireDocument();
            return ToolResult.Ok("Document read.", JsonConvert.SerializeObject(new { text = Trim(doc.Range().Text, maxChars) }));
        }

        private ToolResult ReadVbaProject(ToolCommand command)
        {
            var doc = RequireDocument();
            return VbaProjectSupport.ReadProject(doc, doc.Name, ToolArgumentReader.Int32(command.Arguments, "maxChars", 30000));
        }

        private ToolResult ReadVbaModule(ToolCommand command)
        {
            return VbaProjectSupport.ReadModule(
                RequireDocument(),
                ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty),
                ToolArgumentReader.Int32(command.Arguments, "maxChars", 30000));
        }

        private ToolResult ReplaceVbaModule(ToolCommand command)
        {
            return VbaProjectSupport.ReplaceModule(
                RequireDocument(),
                ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty),
                ToolArgumentReader.String(command.Arguments, "code", string.Empty),
                ToolArgumentReader.Boolean(command.Arguments, "createIfMissing", true));
        }

        private ToolResult InsertVbaModule(ToolCommand command)
        {
            var moduleName = ToolArgumentReader.String(command.Arguments, "moduleName", "RNAssistantModule");
            var code = ToolArgumentReader.String(command.Arguments, "code", string.Empty);
            try
            {
                return VbaProjectSupport.InsertModule(RequireDocument(), moduleName, code);
            }
            catch (Exception ex)
            {
                return ToolResult.Ok("VBA insert was blocked. Enable 'Trust access to the VBA project object model' or copy the code manually. " + ex.Message, JsonConvert.SerializeObject(new { moduleName = moduleName, code = code }));
            }
        }

        private ToolResult RunMacro(ToolCommand command)
        {
            var macroName = ToolArgumentReader.String(command.Arguments, "macroName", string.Empty);
            if (string.IsNullOrWhiteSpace(macroName))
            {
                return ToolResult.Fail("No macroName provided.");
            }

            _application.GetType().InvokeMember(
                "Run",
                BindingFlags.InvokeMethod,
                null,
                _application,
                new object[] { macroName });
            return ToolResult.Ok("Macro ran: " + macroName);
        }

        private string SelectionText()
        {
            try
            {
                return ResolveSelectionRange(RequireDocument()).Text;
            }
            catch
            {
                return string.Empty;
            }
        }

        private Word.Document ActiveDocument()
        {
            if (HasTargetDocument())
            {
                return TargetDocument();
            }

            try { return _application.ActiveDocument; }
            catch { return null; }
        }

        private Word.Document TargetDocument()
        {
            if (!HasTargetDocument())
            {
                return null;
            }

            foreach (Word.Document document in _application.Documents)
            {
                if (MatchesDocument(document))
                {
                    return document;
                }
            }

            return null;
        }

        private bool HasTargetDocument()
        {
            return _target != null && _target.HasDocumentIdentity;
        }

        private bool MatchesDocument(Word.Document document)
        {
            if (document == null)
            {
                return false;
            }

            var fullName = SafeString(delegate { return document.FullName; });
            if (!string.IsNullOrWhiteSpace(_target.FullName) && SamePath(fullName, _target.FullName))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(_target.Path) && SamePath(fullName, _target.Path))
            {
                return true;
            }

            var name = SafeString(delegate { return document.Name; });
            return string.IsNullOrWhiteSpace(_target.FullName)
                && string.IsNullOrWhiteSpace(_target.Path)
                && !string.IsNullOrWhiteSpace(_target.Name)
                && string.Equals(name, _target.Name, StringComparison.OrdinalIgnoreCase);
        }

        private Word.Document RequireDocument()
        {
            var doc = ActiveDocument();
            if (doc == null)
            {
                throw new InvalidOperationException(_target == null || !_target.HasDocumentIdentity
                    ? "No active Word document."
                    : "Target Word document is not open.");
            }
            return doc;
        }

        private void InsertText(string text)
        {
            var doc = RequireDocument();
            var range = ResolveSelectionRange(doc);
            if (IsLiveSelectionRange(range, doc))
            {
                doc.Activate();
                _application.Selection.TypeText(text);
                return;
            }

            range.Text = text;
        }

        private Word.Range ResolveSelectionRange(Word.Document doc)
        {
            try
            {
                if (_application.Selection != null && _application.Selection.Range != null)
                {
                    var range = _application.Selection.Range;
                    if (RangeBelongsToDocument(range, doc))
                    {
                        return range;
                    }
                }
            }
            catch
            {
            }

            var targetRange = ResolveTargetSelectionRange(doc);
            if (targetRange != null)
            {
                return targetRange;
            }

            throw new InvalidOperationException("Select Word text first.");
        }

        private Word.Range ResolveTargetSelectionRange(Word.Document doc)
        {
            if (_target == null || string.IsNullOrWhiteSpace(_target.Selection))
            {
                return null;
            }

            var parts = _target.Selection.Split(':');
            if (parts.Length != 2)
            {
                return null;
            }

            int start;
            int end;
            if (!int.TryParse(parts[0], out start) || !int.TryParse(parts[1], out end))
            {
                return null;
            }

            try
            {
                return doc.Range(start, end);
            }
            catch
            {
                return null;
            }
        }

        private bool IsLiveSelectionRange(Word.Range range, Word.Document doc)
        {
            try
            {
                return range != null
                    && _application.Selection != null
                    && _application.Selection.Range != null
                    && _application.Selection.Range.Start == range.Start
                    && _application.Selection.Range.End == range.End
                    && RangeBelongsToDocument(range, doc);
            }
            catch
            {
                return false;
            }
        }

        private static bool RangeBelongsToDocument(Word.Range range, Word.Document doc)
        {
            if (range == null || doc == null)
            {
                return false;
            }

            try
            {
                return SamePath(SafeString(delegate { return range.Document.FullName; }), SafeString(delegate { return doc.FullName; }))
                    || string.Equals(SafeString(delegate { return range.Document.Name; }), SafeString(delegate { return doc.Name; }), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private delegate string StringGetter();

        private static string SafeString(StringGetter getter)
        {
            try { return getter(); }
            catch { return string.Empty; }
        }

        private static bool SamePath(string left, string right)
        {
            return !string.IsNullOrWhiteSpace(left)
                && !string.IsNullOrWhiteSpace(right)
                && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static ToolDefinition Skill(string id, string description, string schema, bool mutatesDocument = false, bool agentCanRun = true)
        {
            return new ToolDefinition { Id = id, Host = "Word", Name = id, Description = description, ArgumentSchemaJson = schema, BuiltIn = true, Enabled = true, MutatesDocument = mutatesDocument, AgentCanRun = agentCanRun };
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
