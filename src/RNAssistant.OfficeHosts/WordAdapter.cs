using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Word = Microsoft.Office.Interop.Word;
using RNAssistant.Core.Models;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Tools;

namespace RNAssistant.OfficeHosts
{
    public sealed class WordAdapter : IOfficeApplicationAdapter, IOfficeContextProvider, IOfficeBuiltInSkillProvider, IOfficeDocumentCatalog
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

        public IReadOnlyList<OpenOfficeDocumentDto> ListOpenDocuments()
        {
            Word.Document active;
            try { active = _application.ActiveDocument; }
            catch { active = null; }
            var result = new List<OpenOfficeDocumentDto>();
            foreach (Word.Document document in _application.Documents)
            {
                var path = SafeString(delegate { return document.Path; });
                result.Add(new OpenOfficeDocumentDto
                {
                    Host = HostName,
                    DocumentKey = KeyForDocument(document),
                    Title = SafeString(delegate { return document.Name; }),
                    Path = string.IsNullOrWhiteSpace(path) ? string.Empty : SafeString(delegate { return document.FullName; }),
                    IsActive = active != null && string.Equals(KeyForDocument(active), KeyForDocument(document), StringComparison.OrdinalIgnoreCase)
                });
            }
            return result;
        }

        public bool ActivateDocument(string documentKey)
        {
            if (string.IsNullOrWhiteSpace(documentKey))
            {
                return false;
            }

            foreach (Word.Document document in _application.Documents)
            {
                if (!string.Equals(KeyForDocument(document), documentKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                document.Activate();
                NativeWindowInfo.BringToForeground(NativeWindowInfo.ReadLongMemberPath(_application, "ActiveWindow", "Hwnd"));
                return true;
            }
            return false;
        }

        public bool OpenDocument(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                var document = _application.Documents.Open(path);
                if (document == null)
                {
                    return false;
                }
                document.Activate();
                NativeWindowInfo.BringToForeground(NativeWindowInfo.ReadLongMemberPath(_application, "ActiveWindow", "Hwnd"));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private string KeyForDocument(Word.Document document)
        {
            if (document == null)
            {
                return "Word:NoDocument";
            }

            var runtimeKey = "Word:Runtime:" + document.GetHashCode().ToString("x");
            return DocumentIdentity.ForOfficeDocument(
                HostName,
                SafeString(delegate { return document.Path; }),
                runtimeKey,
                () => document.CustomDocumentProperties);
        }

        public IEnumerable<ToolDefinition> GetBuiltInTools()
        {
            return new[]
            {
                Skill("word.get_context", "Read-only: Return active document and selection context.", "{}"),
                Skill("word.get_selection_text", "Read-only: Read current selection text.", "{}"),
                Skill("word.read_document", "Read-only: Read current document text.", "{\"maxChars\":12000}"),
                Skill("word.read_selection", "Read-only: Read current selection text.", "{}"),
                Skill("word.read_range", "Read-only: Read document text by character range.", "{\"start\":0,\"end\":1000,\"maxChars\":12000}"),
                Skill("word.find_text", "Read-only: Find text occurrences in the document.", "{\"query\":\"text\",\"matchCase\":false,\"maxResults\":50}"),
                Skill("word.read_headings", "Read-only: List paragraphs that use heading styles.", "{\"maxResults\":100}"),
                Skill("word.read_tables", "Read-only: Read text from document tables.", "{\"maxTables\":20,\"maxRows\":50}"),
                Skill("word.list_comments", "Read-only: List document comments.", "{}"),
                Skill("word.document_stats", "Read-only: Return basic document counts.", "{}"),
                Skill("word.insert_text", "Mutates document: Insert text at the current cursor position.", "{\"text\":\"Text to insert\"}", true, true, 2),
                Skill("word.insert_paragraph", "Mutates document: Insert a paragraph at selection, start, or end.", "{\"text\":\"Paragraph text\",\"location\":\"selection\"}", true, true, 2),
                Skill("word.replace_selection", "Mutates document: Replace selected text.", "{\"text\":\"Replacement text\"}", true, true, 2),
                Skill("word.replace_text", "Mutates document: Replace document text using Word find/replace.", "{\"find\":\"old text\",\"replace\":\"new text\",\"replaceAll\":true,\"matchCase\":false}", true, true, 2),
                Skill("word.apply_style", "Mutates document: Apply a named Word style to selection or document.", "{\"style\":\"Heading 1\",\"target\":\"selection\"}", true, true, 1),
                Skill("word.format_selection", "Mutates document: Apply basic font formatting to the current selection.", "{\"bold\":true,\"italic\":false,\"underline\":false,\"fontSize\":12,\"fontName\":\"\"}", true, true, 1),
                Skill("word.add_table", "Mutates document: Insert a table at selection, start, or end.", "{\"rows\":2,\"columns\":2,\"values\":[[\"Header\",\"Value\"],[\"A\",\"1\"]],\"location\":\"selection\"}", true, true, 2),
                Skill("word.insert_page_break", "Mutates document: Insert a page break at the current cursor position.", "{}", true, true, 1),
                Skill("word.add_comment", "Mutates document: Add a comment to the current selection.", "{\"text\":\"Comment text\"}", true, true, 1),
                Skill("word.vba_read_project", "Read-only: Read VBA project modules and source code when Trust Access to VBA project is enabled.", "{\"maxChars\":30000}"),
                Skill("word.vba_read_module", "Read-only: Read one VBA module by name.", "{\"moduleName\":\"Module1\",\"maxChars\":30000}"),
                Skill("word.vba_replace_module", "Mutates document: Replace a VBA module source code and create a rollback backup.", "{\"moduleName\":\"Module1\",\"code\":\"Sub Test()\\nEnd Sub\",\"createIfMissing\":true}", true, false, 3),
                Skill("word.insert_vba_module", "Mutates document: Insert a VBA module or return copyable code if trust access is blocked.", "{\"moduleName\":\"Module1\",\"code\":\"Sub Test()\\nEnd Sub\"}", true, false, 3),
                Skill("word.run_macro", "Mutates document: Run a Word VBA macro by name.", "{\"macroName\":\"Module1.Test\"}", true, false, 3)
            };
        }

        public IEnumerable<SkillDefinition> GetBuiltInSkills()
        {
            return new[]
            {
                new SkillDefinition
                {
                    Id = "word.document_editing",
                    Host = "Word",
                    Name = "Word document editing",
                    Description = "Rewrite, insert, format, and review Word document content.",
                    Tags = new List<string> { "word", "editing", "review", "formatting" },
                    BodyMarkdown = "# Word Document Editing\n\nUse this skill for Word drafting and editing tasks.\n\n- Read selection or document context before targeted edits.\n- Preserve user tone unless the user asks to change it.\n- Use insert/replace tools for document mutations.\n- Keep formatting changes explicit.\n- For review tasks, separate findings from suggested edits.",
                    Enabled = true,
                    BuiltIn = true
                }
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
                    case "word.read_range":
                        return ReadRange(command);
                    case "word.find_text":
                        return FindText(command);
                    case "word.read_headings":
                        return ReadHeadings(command);
                    case "word.read_tables":
                        return ReadTables(command);
                    case "word.list_comments":
                        return ListComments();
                    case "word.document_stats":
                        return DocumentStats();
                    case "word.get_selection_text":
                    case "word.read_selection":
                        return ToolResult.Ok("Selection read.", JsonConvert.SerializeObject(new { text = SelectionText() }));
                    case "word.insert_text":
                        InsertText(ToolArgumentReader.String(command.Arguments, "text", string.Empty));
                        return ToolResult.Ok("Text inserted.");
                    case "word.insert_paragraph":
                        return InsertParagraph(command);
                    case "word.replace_selection":
                        ResolveSelectionRange(RequireDocument()).Text = ToolArgumentReader.String(command.Arguments, "text", string.Empty);
                        return ToolResult.Ok("Selection replaced.");
                    case "word.replace_text":
                        return ReplaceText(command);
                    case "word.apply_style":
                        return ApplyStyle(command);
                    case "word.format_selection":
                        return FormatSelection(command);
                    case "word.add_table":
                        return AddTable(command);
                    case "word.insert_page_break":
                        return InsertPageBreak();
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

        private ToolResult ReadRange(ToolCommand command)
        {
            var doc = RequireDocument();
            var start = Math.Max(0, ToolArgumentReader.Int32(command.Arguments, "start", 0));
            var end = ToolArgumentReader.Int32(command.Arguments, "end", Math.Min(doc.Content.End, start + 12000));
            var maxChars = ToolArgumentReader.Int32(command.Arguments, "maxChars", 12000);
            end = Math.Max(start, Math.Min(doc.Content.End, end));
            return ToolResult.Ok("Range read.", JsonConvert.SerializeObject(new
            {
                start = start,
                end = end,
                text = Trim(doc.Range(start, end).Text, maxChars)
            }));
        }

        private ToolResult FindText(ToolCommand command)
        {
            var query = ToolArgumentReader.String(command.Arguments, "query", string.Empty);
            if (string.IsNullOrWhiteSpace(query))
            {
                return ToolResult.Fail("query is required.");
            }

            var matchCase = ToolArgumentReader.Boolean(command.Arguments, "matchCase", false);
            var maxResults = Math.Max(1, Math.Min(500, ToolArgumentReader.Int32(command.Arguments, "maxResults", 50)));
            var docText = RequireDocument().Range().Text ?? string.Empty;
            var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var matches = new List<object>();
            var index = 0;
            while (matches.Count < maxResults && index < docText.Length)
            {
                index = docText.IndexOf(query, index, comparison);
                if (index < 0)
                {
                    break;
                }

                matches.Add(new
                {
                    start = index,
                    end = index + query.Length,
                    preview = PreviewAround(docText, index, query.Length)
                });
                index += Math.Max(1, query.Length);
            }

            return ToolResult.Ok("Text matches found: " + matches.Count, JsonConvert.SerializeObject(matches));
        }

        private ToolResult ReadHeadings(ToolCommand command)
        {
            var doc = RequireDocument();
            var maxResults = Math.Max(1, Math.Min(500, ToolArgumentReader.Int32(command.Arguments, "maxResults", 100)));
            var headings = new List<object>();
            foreach (Word.Paragraph paragraph in doc.Paragraphs)
            {
                var style = MemberText(paragraph.Range, "Style");
                if (style.IndexOf("Heading", StringComparison.OrdinalIgnoreCase) < 0 &&
                    style.IndexOf("Заголовок", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                headings.Add(new
                {
                    style = style,
                    start = paragraph.Range.Start,
                    end = paragraph.Range.End,
                    text = Trim(paragraph.Range.Text, 500)
                });
                if (headings.Count >= maxResults)
                {
                    break;
                }
            }

            return ToolResult.Ok("Headings read: " + headings.Count, JsonConvert.SerializeObject(headings));
        }

        private ToolResult ReadTables(ToolCommand command)
        {
            var doc = RequireDocument();
            var maxTables = Math.Max(1, Math.Min(50, ToolArgumentReader.Int32(command.Arguments, "maxTables", 20)));
            var maxRows = Math.Max(1, Math.Min(500, ToolArgumentReader.Int32(command.Arguments, "maxRows", 50)));
            var tables = new List<object>();
            for (var i = 1; i <= doc.Tables.Count && tables.Count < maxTables; i++)
            {
                var table = doc.Tables[i];
                var rows = new List<List<string>>();
                var rowLimit = Math.Min(table.Rows.Count, maxRows);
                for (var r = 1; r <= rowLimit; r++)
                {
                    var row = new List<string>();
                    for (var c = 1; c <= table.Columns.Count; c++)
                    {
                        row.Add(CleanCellText(table.Cell(r, c).Range.Text));
                    }
                    rows.Add(row);
                }

                tables.Add(new
                {
                    index = i,
                    rows = table.Rows.Count,
                    columns = table.Columns.Count,
                    values = rows
                });
            }

            return ToolResult.Ok("Tables read: " + tables.Count, JsonConvert.SerializeObject(tables));
        }

        private ToolResult ListComments()
        {
            var doc = RequireDocument();
            var comments = new List<object>();
            for (var i = 1; i <= doc.Comments.Count; i++)
            {
                var comment = doc.Comments[i];
                comments.Add(new
                {
                    index = i,
                    author = SafeString(delegate { return comment.Author; }),
                    text = Trim(comment.Range.Text, 1000),
                    scope = Trim(comment.Scope.Text, 500)
                });
            }

            return ToolResult.Ok("Comments listed: " + comments.Count, JsonConvert.SerializeObject(comments));
        }

        private ToolResult DocumentStats()
        {
            var doc = RequireDocument();
            return ToolResult.Ok("Document stats collected.", JsonConvert.SerializeObject(new
            {
                characters = doc.Characters.Count,
                words = doc.Words.Count,
                paragraphs = doc.Paragraphs.Count,
                tables = doc.Tables.Count,
                comments = doc.Comments.Count
            }));
        }

        private ToolResult InsertParagraph(ToolCommand command)
        {
            var text = ToolArgumentReader.String(command.Arguments, "text", string.Empty);
            var location = ToolArgumentReader.String(command.Arguments, "location", "selection");
            var range = ResolveInsertionRange(RequireDocument(), location);
            range.InsertAfter(text + Environment.NewLine);
            return ToolResult.Ok("Paragraph inserted.");
        }

        private ToolResult ReplaceText(ToolCommand command)
        {
            var find = ToolArgumentReader.String(command.Arguments, "find", string.Empty);
            if (string.IsNullOrWhiteSpace(find))
            {
                return ToolResult.Fail("find is required.");
            }

            var replace = ToolArgumentReader.String(command.Arguments, "replace", string.Empty);
            var replaceAll = ToolArgumentReader.Boolean(command.Arguments, "replaceAll", true);
            var matchCase = ToolArgumentReader.Boolean(command.Arguments, "matchCase", false);
            var doc = RequireDocument();
            var finder = doc.Content.Find;
            finder.ClearFormatting();
            finder.Replacement.ClearFormatting();
            finder.Text = find;
            finder.Replacement.Text = replace;
            finder.MatchCase = matchCase;
            finder.Execute(
                FindText: find,
                MatchCase: matchCase,
                Wrap: Word.WdFindWrap.wdFindContinue,
                ReplaceWith: replace,
                Replace: replaceAll ? Word.WdReplace.wdReplaceAll : Word.WdReplace.wdReplaceOne);
            return ToolResult.Ok("Text replaced.");
        }

        private ToolResult ApplyStyle(ToolCommand command)
        {
            var style = ToolArgumentReader.String(command.Arguments, "style", string.Empty);
            if (string.IsNullOrWhiteSpace(style))
            {
                return ToolResult.Fail("style is required.");
            }

            var target = ToolArgumentReader.String(command.Arguments, "target", "selection");
            var doc = RequireDocument();
            var range = string.Equals(target, "document", StringComparison.OrdinalIgnoreCase)
                ? doc.Range()
                : ResolveSelectionRange(doc);
            range.GetType().InvokeMember("Style", BindingFlags.SetProperty, null, range, new object[] { style });
            return ToolResult.Ok("Style applied: " + style);
        }

        private ToolResult FormatSelection(ToolCommand command)
        {
            var range = ResolveSelectionRange(RequireDocument());
            if (command.Arguments.ContainsKey("bold"))
            {
                range.Font.Bold = ToolArgumentReader.Boolean(command.Arguments, "bold", false) ? 1 : 0;
            }
            if (command.Arguments.ContainsKey("italic"))
            {
                range.Font.Italic = ToolArgumentReader.Boolean(command.Arguments, "italic", false) ? 1 : 0;
            }
            if (command.Arguments.ContainsKey("underline"))
            {
                range.Font.Underline = ToolArgumentReader.Boolean(command.Arguments, "underline", false)
                    ? Word.WdUnderline.wdUnderlineSingle
                    : Word.WdUnderline.wdUnderlineNone;
            }
            var fontSize = ToolArgumentReader.Int32(command.Arguments, "fontSize", 0);
            if (fontSize > 0)
            {
                range.Font.Size = fontSize;
            }
            var fontName = ToolArgumentReader.String(command.Arguments, "fontName", string.Empty);
            if (!string.IsNullOrWhiteSpace(fontName))
            {
                range.Font.Name = fontName;
            }

            return ToolResult.Ok("Selection formatted.");
        }

        private ToolResult AddTable(ToolCommand command)
        {
            var rows = Math.Max(1, ToolArgumentReader.Int32(command.Arguments, "rows", 2));
            var columns = Math.Max(1, ToolArgumentReader.Int32(command.Arguments, "columns", 2));
            var location = ToolArgumentReader.String(command.Arguments, "location", "selection");
            var doc = RequireDocument();
            var range = ResolveInsertionRange(doc, location);
            var table = doc.Tables.Add(range, rows, columns);
            var valuesJson = ToolArgumentReader.String(command.Arguments, "values", string.Empty);
            if (!string.IsNullOrWhiteSpace(valuesJson))
            {
                var values = JArray.Parse(valuesJson);
                for (var r = 1; r <= rows && r <= values.Count; r++)
                {
                    var row = values[r - 1] as JArray;
                    if (row == null)
                    {
                        continue;
                    }
                    for (var c = 1; c <= columns && c <= row.Count; c++)
                    {
                        table.Cell(r, c).Range.Text = Convert.ToString(row[c - 1]);
                    }
                }
            }

            return ToolResult.Ok("Table inserted.", JsonConvert.SerializeObject(new { rows = rows, columns = columns }));
        }

        private ToolResult InsertPageBreak()
        {
            _application.Selection.InsertBreak(Word.WdBreakType.wdPageBreak);
            return ToolResult.Ok("Page break inserted.");
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
                return ToolResult.Fail("VBA insert was blocked. Enable 'Trust access to the VBA project object model' or copy the code manually. " + ex.Message, JsonConvert.SerializeObject(new { moduleName = moduleName, code = code }));
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

        private Word.Range ResolveInsertionRange(Word.Document doc, string location)
        {
            if (string.Equals(location, "start", StringComparison.OrdinalIgnoreCase))
            {
                return doc.Range(0, 0);
            }

            if (string.Equals(location, "end", StringComparison.OrdinalIgnoreCase))
            {
                var end = Math.Max(0, doc.Content.End - 1);
                return doc.Range(end, end);
            }

            try
            {
                return ResolveSelectionRange(doc);
            }
            catch
            {
                var end = Math.Max(0, doc.Content.End - 1);
                return doc.Range(end, end);
            }
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

        private static string MemberText(object instance, string memberName)
        {
            if (instance == null || string.IsNullOrWhiteSpace(memberName))
            {
                return string.Empty;
            }

            try
            {
                var value = instance.GetType().InvokeMember(memberName, BindingFlags.GetProperty, null, instance, null);
                return Convert.ToString(value);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string CleanCellText(string value)
        {
            return (value ?? string.Empty).Replace("\r\a", string.Empty).Replace("\a", string.Empty).Trim();
        }

        private static string PreviewAround(string text, int index, int length)
        {
            text = text ?? string.Empty;
            var start = Math.Max(0, index - 60);
            var end = Math.Min(text.Length, index + length + 60);
            return text.Substring(start, end - start).Replace("\r", " ").Replace("\n", " ");
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

        private static ToolDefinition Skill(string id, string description, string schema, bool mutatesDocument = false, bool agentCanRun = true, int riskLevel = 0)
        {
            return new ToolDefinition { Id = id, Host = "Word", Name = id, Description = description, ArgumentSchemaJson = schema, BuiltIn = true, Enabled = true, MutatesDocument = mutatesDocument, AgentCanRun = agentCanRun, RiskLevel = riskLevel };
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
