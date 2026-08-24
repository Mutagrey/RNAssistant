using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Word = Microsoft.Office.Interop.Word;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
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
                return KeyForDocument(ActiveDocument());
            }
        }

        public string RuntimeDocumentKey
        {
            get
            {
                var doc = ActiveDocument();
                return doc == null ? "Word:NoDocument" : DocumentIdentity.RuntimeKey(HostName, doc);
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
                context.DocumentPath = PersistentPath(doc);
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
                result.Add(new OpenOfficeDocumentDto
                {
                    Host = HostName,
                    DocumentKey = KeyForDocument(document),
                    Title = SafeString(delegate { return document.Name; }),
                    Path = PersistentPath(document),
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

            var runtimeKey = DocumentIdentity.RuntimeKey(HostName, document);
            return DocumentIdentity.ForOfficeDocument(
                HostName,
                PersistentPath(document),
                runtimeKey,
                () => document.CustomDocumentProperties);
        }

        private static string PersistentPath(Word.Document document)
        {
            if (document == null || string.IsNullOrWhiteSpace(SafeString(delegate { return document.Path; })))
            {
                return string.Empty;
            }

            return SafeString(delegate { return document.FullName; });
        }

        public IEnumerable<ToolDefinition> GetBuiltInTools()
        {
            return new[]
            {
                Tool("word.get_context", "Read-only: Return active document and selection context.", "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}"),
                Tool("word.read_text", "Read-only: Read document, selection, or character-range text with one source selector.", "{\"type\":\"object\",\"properties\":{\"source\":{\"type\":\"string\",\"enum\":[\"document\",\"selection\",\"range\"],\"description\":\"Text source.\",\"default\":\"document\"},\"start\":{\"type\":\"integer\",\"description\":\"Zero-based inclusive start for range source.\",\"default\":0},\"end\":{\"type\":\"integer\",\"description\":\"Zero-based exclusive end for range source.\"},\"maxChars\":{\"type\":\"integer\",\"description\":\"Maximum number of text characters returned.\",\"default\":12000}},\"required\":[],\"additionalProperties\":false}", canSourceHtmlData: true),
                Tool("word.find_text", "Read-only: Find literal or regex text across Word stories and return stable coordinates/hash.", "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"description\":\"Non-empty literal or regular-expression search query.\",\"minLength\":1},\"scope\":{\"type\":\"string\",\"description\":\"Search or operation scope supported by the tool.\",\"default\":\"main\",\"enum\":[\"main\",\"selection\",\"all\"]},\"mode\":{\"type\":\"string\",\"description\":\"Text matching mode: literal or regex.\",\"default\":\"literal\",\"enum\":[\"literal\",\"regex\"]},\"matchCase\":{\"type\":\"boolean\",\"description\":\"Whether matching is case-sensitive.\",\"default\":false},\"wholeWord\":{\"type\":\"boolean\",\"description\":\"Whether only whole-word matches are accepted.\",\"default\":false},\"maxResults\":{\"type\":\"integer\",\"description\":\"Maximum number of matches returned.\",\"default\":50},\"contextChars\":{\"type\":\"integer\",\"description\":\"Maximum context characters returned around each match.\",\"default\":80}},\"required\":[\"query\"],\"additionalProperties\":false}"),
                Tool("word.inspect", "Read-only: Inspect headings, tables, comments, or document statistics with one selector.", "{\"type\":\"object\",\"properties\":{\"kind\":{\"type\":\"string\",\"enum\":[\"headings\",\"tables\",\"comments\",\"stats\"],\"description\":\"Document structure to return.\"},\"maxResults\":{\"type\":\"integer\",\"description\":\"Maximum heading count.\",\"default\":100},\"maxTables\":{\"type\":\"integer\",\"description\":\"Maximum table count.\",\"default\":20},\"maxRows\":{\"type\":\"integer\",\"description\":\"Maximum rows returned per table.\",\"default\":50}},\"required\":[\"kind\"],\"additionalProperties\":false}", canSourceHtmlData: true),
                Tool("word.write_text", "Mutates document: Insert text, insert a paragraph, or replace the current selection.", "{\"type\":\"object\",\"properties\":{\"mode\":{\"type\":\"string\",\"enum\":[\"insert\",\"paragraph\",\"replaceSelection\"],\"description\":\"Text write operation.\"},\"text\":{\"type\":\"string\",\"description\":\"Complete text to insert or assign.\"},\"location\":{\"type\":\"string\",\"description\":\"For paragraph mode: selection, start, or end.\",\"default\":\"selection\",\"enum\":[\"selection\",\"start\",\"end\"]}},\"required\":[\"mode\",\"text\"],\"additionalProperties\":false}", true, true, 2),
                Tool("word.replace_text", "Mutates document: Replace bounded literal or regex text in the current scope. A separate search is optional; runtime reads the scope immediately before mutation and verifies the result.", "{\"type\":\"object\",\"properties\":{\"find\":{\"type\":\"string\",\"description\":\"Literal or regular-expression text to find.\",\"minLength\":1},\"replace\":{\"type\":\"string\",\"description\":\"Replacement text; regex capture groups are allowed only in regex mode.\"},\"scope\":{\"type\":\"string\",\"description\":\"Search or operation scope supported by the tool.\",\"default\":\"main\",\"enum\":[\"main\",\"selection\",\"all\"]},\"mode\":{\"type\":\"string\",\"description\":\"Text matching mode: literal or regex.\",\"default\":\"literal\",\"enum\":[\"literal\",\"regex\"]},\"replaceAll\":{\"type\":\"boolean\",\"description\":\"Whether all matches in scope may be replaced.\",\"default\":true},\"matchCase\":{\"type\":\"boolean\",\"description\":\"Whether matching is case-sensitive.\",\"default\":false},\"wholeWord\":{\"type\":\"boolean\",\"description\":\"Whether only whole-word matches are accepted.\",\"default\":false},\"maxReplacements\":{\"type\":\"integer\",\"description\":\"Safety limit for replacements.\",\"default\":500}},\"required\":[\"find\"],\"additionalProperties\":false}", true, true, 2, true),
                Tool("word.format_text", "Mutates document: Apply a named style or basic font formatting with one explicit kind selector.", "{\"type\":\"object\",\"properties\":{\"kind\":{\"type\":\"string\",\"enum\":[\"style\",\"font\"],\"description\":\"Formatting operation.\"},\"style\":{\"type\":\"string\",\"description\":\"Named Word style when kind is style.\"},\"target\":{\"type\":\"string\",\"description\":\"Style target; font formatting always targets the selection.\",\"default\":\"selection\",\"enum\":[\"selection\",\"document\"]},\"bold\":{\"type\":\"boolean\",\"description\":\"Whether bold formatting is enabled for kind=font.\"},\"italic\":{\"type\":\"boolean\",\"description\":\"Whether italic formatting is enabled for kind=font.\"},\"underline\":{\"type\":\"boolean\",\"description\":\"Whether underline formatting is enabled for kind=font.\"},\"fontSize\":{\"type\":\"integer\",\"description\":\"Font size in points for kind=font.\",\"minimum\":1},\"fontName\":{\"type\":\"string\",\"description\":\"Installed font family name for kind=font.\"}},\"required\":[\"kind\"],\"additionalProperties\":false}", true, true, 1),
                Tool("word.add_table", "Mutates document: Insert a table at selection, start, or end. Runtime infers dimensions from values when rows/columns are omitted.", "{\"type\":\"object\",\"properties\":{\"rows\":{\"type\":\"integer\",\"description\":\"Optional table row count; omit to infer it from values or use 2 for an empty table.\",\"minimum\":1},\"columns\":{\"type\":\"integer\",\"description\":\"Optional table column count; omit to infer it from values or use 2 for an empty table.\",\"minimum\":1},\"values\":{\"type\":\"array\",\"items\":{\"type\":\"array\",\"items\":{\"type\":[\"string\",\"number\",\"boolean\",\"null\"]}},\"description\":\"Optional two-dimensional row array; dimensions are inferred when omitted.\"},\"location\":{\"type\":\"string\",\"description\":\"Insertion target supported by the tool.\",\"default\":\"selection\",\"enum\":[\"selection\",\"start\",\"end\"]}},\"required\":[],\"additionalProperties\":false}", true, true, 2),
                Tool("word.insert_page_break", "Mutates document: Insert a page break at the current cursor position.", "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}", true, true, 1),
                Tool("word.add_comment", "Mutates document: Add a comment to the current selection.", "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\",\"description\":\"Complete text to insert, replace, or assign.\"}},\"required\":[\"text\"],\"additionalProperties\":false}", true, true, 1)
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
                    case "word.read_text":
                        return ReadText(command);
                    case "word.find_text":
                        return FindText(command);
                    case "word.inspect":
                        return InspectDocument(command);
                    case "word.write_text":
                        return WriteText(command);
                    case "word.replace_text":
                        return ReplaceText(command);
                    case "word.format_text":
                        return FormatText(command);
                    case "word.add_table":
                        return AddTable(command);
                    case "word.insert_page_break":
                        return InsertPageBreak();
                    case "word.add_comment":
                        var doc = RequireDocument();
                        doc.Comments.Add(ResolveSelectionRange(doc), ToolArgumentReader.String(command.Arguments, "text", string.Empty));
                        return ToolResult.Ok("Comment added.");
                    case "word.vba_list_project_components_internal":
                        return ListVbaProjectComponents();
                    case "word.vba_read_module":
                        return ReadVbaModule(command);
                    case "word.vba_replace_module":
                        return ReplaceVbaModule(command);
                    case "word.run_macro":
                        return RunMacro(command);
                    case "word.vba_install_package_internal":
                        return VbaProjectSupport.InstallPackage(RequireDocument(), ToolArgumentReader.String(command.Arguments, "componentsJson", "[]"), ToolArgumentReader.String(command.Arguments, "marker", string.Empty));
                    case "word.vba_remove_package_internal":
                        return VbaProjectSupport.RemovePackage(RequireDocument(), ToolArgumentReader.String(command.Arguments, "expectedComponentsJson", "{}"), ToolArgumentReader.String(command.Arguments, "expectedMarker", string.Empty));
                    case "word.vba_create_module_internal":
                        return VbaProjectSupport.CreateModule(RequireDocument(), ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty), ToolArgumentReader.String(command.Arguments, "componentType", "StdModule"), ToolArgumentReader.String(command.Arguments, "code", string.Empty));
                    case "word.vba_delete_module_internal":
                        return VbaProjectSupport.DeleteModule(RequireDocument(), ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty));
                    default:
                        return ToolResult.Fail("Unsupported Word tool: " + command.ToolId);
                }
            }
            catch (Exception ex)
            {
                var isVba = (command == null ? string.Empty : command.ToolId ?? string.Empty)
                    .IndexOf(".vba_", StringComparison.OrdinalIgnoreCase) >= 0;
                return ToolResult.Fail(ex.Message, null, isVba ? "vba_access_error" : "office_tool_error", !isVba);
            }
        }

        private ToolResult ReadDocument(ToolCommand command)
        {
            var maxChars = ToolArgumentReader.Int32(command.Arguments, "maxChars", 12000);
            var doc = RequireDocument();
            return ToolResult.Ok("Document read.", JsonConvert.SerializeObject(new { text = Trim(doc.Range().Text, maxChars) }));
        }

        private ToolResult ReadText(ToolCommand command)
        {
            var source = ToolArgumentReader.String(command.Arguments, "source", "document");
            if (string.Equals(source, "selection", StringComparison.OrdinalIgnoreCase))
            {
                var maxChars = ToolArgumentReader.Int32(command.Arguments, "maxChars", 12000);
                return ToolResult.Ok("Selection read.", JsonConvert.SerializeObject(new { text = Trim(SelectionText(), maxChars) }));
            }
            if (string.Equals(source, "range", StringComparison.OrdinalIgnoreCase)) return ReadRange(command);
            return string.Equals(source, "document", StringComparison.OrdinalIgnoreCase)
                ? ReadDocument(command)
                : ToolResult.Fail("source must be document, selection, or range.");
        }

        private ToolResult InspectDocument(ToolCommand command)
        {
            var kind = ToolArgumentReader.String(command.Arguments, "kind", string.Empty);
            if (string.Equals(kind, "headings", StringComparison.OrdinalIgnoreCase)) return ReadHeadings(command);
            if (string.Equals(kind, "tables", StringComparison.OrdinalIgnoreCase)) return ReadTables(command);
            if (string.Equals(kind, "comments", StringComparison.OrdinalIgnoreCase)) return ListComments();
            if (string.Equals(kind, "stats", StringComparison.OrdinalIgnoreCase)) return DocumentStats();
            return ToolResult.Fail("kind must be headings, tables, comments, or stats.");
        }

        private ToolResult WriteText(ToolCommand command)
        {
            var mode = ToolArgumentReader.String(command.Arguments, "mode", string.Empty);
            if (string.Equals(mode, "insert", StringComparison.OrdinalIgnoreCase))
            {
                InsertText(ToolArgumentReader.String(command.Arguments, "text", string.Empty));
                return ToolResult.Ok("Text inserted.");
            }
            if (string.Equals(mode, "paragraph", StringComparison.OrdinalIgnoreCase)) return InsertParagraph(command);
            if (string.Equals(mode, "replaceSelection", StringComparison.OrdinalIgnoreCase))
            {
                ResolveSelectionRange(RequireDocument()).Text = ToolArgumentReader.String(command.Arguments, "text", string.Empty);
                return ToolResult.Ok("Selection replaced.");
            }
            return ToolResult.Fail("mode must be insert, paragraph, or replaceSelection.");
        }

        private ToolResult ReadRange(ToolCommand command)
        {
            var doc = RequireDocument();
            var start = Math.Max(0, Math.Min(doc.Content.End, ToolArgumentReader.Int32(command.Arguments, "start", 0)));
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

            var scope = ToolArgumentReader.String(command.Arguments, "scope", "main");
            var maxResults = Math.Max(1, Math.Min(500, ToolArgumentReader.Int32(command.Arguments, "maxResults", 50)));
            var contextChars = Math.Max(0, Math.Min(1000, ToolArgumentReader.Int32(command.Arguments, "contextChars", 80)));
            var matches = new List<object>();
            var hash = new System.Text.StringBuilder();
            var total = 0;
            try
            {
                foreach (var story in SearchRanges(scope))
                {
                    var text = story.Range.Text ?? string.Empty;
                    hash.Append(story.Kind).Append('\n').Append(story.Range.Start).Append(':').Append(story.Range.End).Append('\n').Append(text).Append('\n');
                    var found = TextPatternEngine.Find(text, query, PatternOptions(command), Math.Max(1, maxResults - matches.Count), contextChars);
                    total += found.MatchCount;
                    foreach (var match in found.Matches)
                    {
                        if (matches.Count >= maxResults) break;
                        matches.Add(new { story = story.Kind, start = story.Range.Start + match.Index, end = story.Range.Start + match.Index + match.Length, preview = match.Preview });
                    }
                }
                var scopeHash = TextPatternEngine.Sha256(hash.ToString());
                return ToolResult.Ok("Text matches found: " + total, JsonConvert.SerializeObject(new { query = query, scope = scope, matchCount = total, returnedCount = matches.Count, truncated = total > matches.Count, scopeSha256 = scopeHash, contentSha256 = scopeHash, matches = matches }));
            }
            catch (TextPatternException ex)
            {
                return ToolResult.Fail(ex.Message, null, ex.ErrorCode, false);
            }
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
            var scope = ToolArgumentReader.String(command.Arguments, "scope", "main");
            var replaceAll = ToolArgumentReader.Boolean(command.Arguments, "replaceAll", true);
            var maxReplacements = Math.Max(1, Math.Min(500, ToolArgumentReader.Int32(command.Arguments, "maxReplacements", 500)));
            var ranges = new List<WordSearchRange>(SearchRanges(scope));
            var plans = new List<WordReplacementPlan>();
            var options = PatternOptions(command);
            var replacementPlanned = false;
            try
            {
                foreach (var story in ranges)
                {
                    var text = story.Range.Text ?? string.Empty;
                    var found = TextPatternEngine.Find(text, find, options, 1, 0);
                    if (found.MatchCount > 0 && (replaceAll || !replacementPlanned))
                    {
                        var edits = TextPatternEngine.PlanReplacements(text, find, replace, options, replaceAll, maxReplacements);
                        if (edits.Count > 0)
                        {
                            plans.Add(new WordReplacementPlan { Story = story, Edits = edits });
                            replacementPlanned = true;
                        }
                    }
                }
                var replacements = 0;
                foreach (var plan in plans) replacements += plan.Edits.Count;
                if (replacements > maxReplacements) return ToolResult.Fail("Replacement count exceeds maxReplacements=" + maxReplacements + ".", null, "replacement_limit_exceeded", false);
                for (var p = plans.Count - 1; p >= 0; p--)
                {
                    var plan = plans[p];
                    for (var e = plan.Edits.Count - 1; e >= 0; e--)
                    {
                        var edit = plan.Edits[e];
                        var target = plan.Story.Range.Duplicate;
                        target.SetRange(plan.Story.Range.Start + edit.Index, plan.Story.Range.Start + edit.Index + edit.Length);
                        target.Text = edit.Text;
                    }
                }
                var verify = new ToolCommand { ToolId = "word.find_text" };
                verify.Arguments["query"] = find; verify.Arguments["scope"] = scope;
                verify.Arguments["mode"] = ToolArgumentReader.String(command.Arguments, "mode", "literal");
                verify.Arguments["matchCase"] = ToolArgumentReader.Boolean(command.Arguments, "matchCase", false);
                verify.Arguments["wholeWord"] = ToolArgumentReader.Boolean(command.Arguments, "wholeWord", false);
                verify.Arguments["maxResults"] = 500; verify.Arguments["contextChars"] = 80;
                var post = FindText(verify);
                if (!post.Success) return post;
                var postHash = Convert.ToString(JObject.Parse(post.DataJson ?? "{}")["scopeSha256"]);
                return ToolResult.Ok("Word replacements completed: " + replacements + ".", JsonConvert.SerializeObject(new { replacements = replacements, scopeSha256 = postHash }));
            }
            catch (TextPatternException ex)
            {
                return ToolResult.Fail(ex.Message, null, ex.ErrorCode, false);
            }
        }

        private TextPatternOptions PatternOptions(ToolCommand command)
        {
            return new TextPatternOptions { Mode = ToolArgumentReader.String(command.Arguments, "mode", "literal"), MatchCase = ToolArgumentReader.Boolean(command.Arguments, "matchCase", false), WholeWord = ToolArgumentReader.Boolean(command.Arguments, "wholeWord", false) };
        }

        private IEnumerable<WordSearchRange> SearchRanges(string scope)
        {
            var doc = RequireDocument();
            if (string.Equals(scope, "selection", StringComparison.OrdinalIgnoreCase))
            {
                yield return new WordSearchRange { Kind = "selection", Range = ResolveSelectionRange(doc).Duplicate };
                yield break;
            }
            if (!string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase))
            {
                yield return new WordSearchRange { Kind = "main", Range = doc.Content.Duplicate };
                yield break;
            }
            foreach (Word.WdStoryType type in Enum.GetValues(typeof(Word.WdStoryType)))
            {
                Word.Range range;
                try { range = doc.StoryRanges[type]; }
                catch { continue; }
                while (range != null)
                {
                    yield return new WordSearchRange { Kind = type.ToString(), Range = range.Duplicate };
                    try { range = range.NextStoryRange; }
                    catch { range = null; }
                }
            }
        }

        private sealed class WordSearchRange { public string Kind { get; set; } public Word.Range Range { get; set; } }
        private sealed class WordReplacementPlan { public WordSearchRange Story { get; set; } public List<TextPatternReplacement> Edits { get; set; } }

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

        private ToolResult FormatText(ToolCommand command)
        {
            var kind = ToolArgumentReader.String(command.Arguments, "kind", string.Empty);
            if (string.Equals(kind, "style", StringComparison.OrdinalIgnoreCase)) return ApplyStyle(command);
            if (string.Equals(kind, "font", StringComparison.OrdinalIgnoreCase)) return FormatSelection(command);
            return ToolResult.Fail("kind must be style or font.");
        }

        private ToolResult FormatSelection(ToolCommand command)
        {
            if (!command.Arguments.ContainsKey("bold") &&
                !command.Arguments.ContainsKey("italic") &&
                !command.Arguments.ContainsKey("underline") &&
                !command.Arguments.ContainsKey("fontSize") &&
                !command.Arguments.ContainsKey("fontName"))
            {
                return ToolResult.Fail("kind=font requires at least one font formatting field.");
            }
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
            ResolvedTableArguments tableArguments;
            string argumentError;
            if (!TableArgumentResolver.TryResolve(command, 2, 2, out tableArguments, out argumentError))
            {
                return ToolResult.Fail(argumentError);
            }
            var rows = tableArguments.Rows;
            var columns = tableArguments.Columns;
            var location = ToolArgumentReader.String(command.Arguments, "location", "selection");
            var doc = RequireDocument();
            var range = ResolveInsertionRange(doc, location);
            var table = doc.Tables.Add(range, rows, columns);
            var values = tableArguments.Values;
            if (values != null)
            {
                for (var r = 1; r <= rows && r <= values.Count; r++)
                {
                    var row = (JArray)values[r - 1];
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

        private ToolResult ListVbaProjectComponents()
        {
            var doc = RequireDocument();
            return VbaProjectSupport.ListProjectComponents(doc, doc.Name);
        }

        private ToolResult ReadVbaModule(ToolCommand command)
        {
            if (command.Arguments.ContainsKey("startLine") || command.Arguments.ContainsKey("lineCount"))
            {
                return VbaProjectSupport.ReadModuleLines(
                    RequireDocument(),
                    ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty),
                    ToolArgumentReader.Int32(command.Arguments, "startLine", 1),
                    ToolArgumentReader.Int32(command.Arguments, "lineCount", 200));
            }
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

        private ToolResult RunMacro(ToolCommand command)
        {
            var macroName = ToolArgumentReader.String(command.Arguments, "macroName", string.Empty);
            if (string.IsNullOrWhiteSpace(macroName))
            {
                return ToolResult.Fail("No macroName provided.");
            }

            var output = VbaProjectSupport.RunStringFunction(_application, macroName, ToolArgumentReader.String(command.Arguments, "argumentsJson", "[]"));
            return ToolResult.Ok("Macro ran: " + macroName, JsonConvert.SerializeObject(new { output = output }));
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
                return string.Equals(
                    DocumentIdentity.RuntimeKey("Word", range.Document),
                    DocumentIdentity.RuntimeKey("Word", doc),
                    StringComparison.OrdinalIgnoreCase);
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

        private static ToolDefinition Tool(string id, string description, string schema, bool mutatesDocument = false, bool agentCanRun = true, int riskLevel = 0, bool requiresConfirmation = false, bool canSourceHtmlData = false)
        {
            return new ToolDefinition { Id = id, Host = "Word", Name = id, Description = description, ArgumentSchemaJson = schema, BuiltIn = true, Enabled = true, MutatesDocument = mutatesDocument, AgentCanRun = agentCanRun, RiskLevel = riskLevel, RequiresConfirmation = requiresConfirmation, CanSourceHtmlData = canSourceHtmlData };
        }

        private static string Trim(string text, int maxChars)
        {
            maxChars = Math.Max(0, maxChars);
            if (maxChars == 0) return string.Empty;
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            {
                return text;
            }
            return text.Substring(0, maxChars) + "\n...[truncated]";
        }
    }
}
