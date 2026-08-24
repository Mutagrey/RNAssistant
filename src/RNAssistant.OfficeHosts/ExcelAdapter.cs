using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Excel = Microsoft.Office.Interop.Excel;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Tools;

namespace RNAssistant.OfficeHosts
{
    public sealed class ExcelAdapter : IOfficeApplicationAdapter, IOfficeContextProvider, IOfficeBuiltInSkillProvider, IOfficeDocumentCatalog
    {
        private const long MaxReadableCellCount = 100000;
        private const int MaxContextPreviewCells = 2000;

        private readonly Excel.Application _application;
        private readonly OfficeTargetDescriptor _target;
        private readonly Excel.Workbook _targetWorkbook;

        public ExcelAdapter(Excel.Application application)
            : this(application, (OfficeTargetDescriptor)null)
        {
        }

        public ExcelAdapter(Excel.Application application, OfficeTargetDescriptor target)
        {
            _application = application;
            _target = target;
        }

        public ExcelAdapter(Excel.Application application, Excel.Workbook targetWorkbook)
        {
            _application = application;
            _targetWorkbook = targetWorkbook;
        }

        public string HostName { get { return "Excel"; } }

        public string DocumentKey
        {
            get
            {
                var workbook = ActiveWorkbook();
                if (workbook == null)
                {
                    return "Excel:NoWorkbook";
                }

                return DocumentIdentity.ForOfficeDocument(
                    HostName,
                    workbook.Path,
                    RuntimeDocumentKey,
                    () => workbook.CustomDocumentProperties);
            }
        }

        public string RuntimeDocumentKey
        {
            get
            {
                var workbook = ActiveWorkbook();
                return workbook == null ? "Excel:NoWorkbook" : DocumentIdentity.RuntimeKey(HostName, workbook);
            }
        }

        public string DocumentTitle
        {
            get
            {
                var workbook = ActiveWorkbook();
                return workbook == null ? "No workbook" : workbook.Name;
            }
        }

        public OfficeContext GetOfficeContext()
        {
            var context = new OfficeContext { Host = HostName };
            try
            {
                var hwnd = NativeWindowInfo.ReadLongMemberPath(_application, "Hwnd");
                context.AppHwnd = new IntPtr(hwnd);
                context.ProcessId = NativeWindowInfo.GetProcessId(hwnd);
            }
            catch
            {
            }

            var workbook = ActiveWorkbook();
            if (workbook != null)
            {
                context.DocumentPath = SafeString(delegate { return workbook.FullName; });
                context.DocumentTitle = SafeString(delegate { return workbook.Name; });
            }

            try
            {
                var range = workbook == null ? null : ResolveSelectionRange(workbook);
                var sheet = range == null ? null : range.Worksheet as Excel.Worksheet;
                context.ContainerName = sheet == null ? null : sheet.Name;
                context.SelectionAddress = range == null ? null : range.Address[false, false];
                context.SelectionText = range == null ? null : Trim(BuildRangeText(range, 2000), 2000);
            }
            catch
            {
            }

            return context;
        }

        public IReadOnlyList<OpenOfficeDocumentDto> ListOpenDocuments()
        {
            Excel.Workbook active;
            try { active = _application.ActiveWorkbook; }
            catch { active = null; }
            var result = new List<OpenOfficeDocumentDto>();
            foreach (Excel.Workbook workbook in _application.Workbooks)
            {
                result.Add(new OpenOfficeDocumentDto
                {
                    Host = HostName,
                    DocumentKey = KeyForWorkbook(workbook),
                    Title = SafeString(delegate { return workbook.Name; }),
                    Path = SafeString(delegate { return workbook.FullName; }),
                    IsActive = active != null && SameWorkbook(active, workbook)
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

            foreach (Excel.Workbook workbook in _application.Workbooks)
            {
                if (!string.Equals(KeyForWorkbook(workbook), documentKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                workbook.Activate();
                if (workbook.Windows != null && workbook.Windows.Count > 0)
                {
                    workbook.Windows[1].Activate();
                }
                NativeWindowInfo.BringToForeground(NativeWindowInfo.ReadLongMemberPath(_application, "Hwnd"));
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
                var workbook = _application.Workbooks.Open(path);
                if (workbook == null)
                {
                    return false;
                }
                workbook.Activate();
                if (workbook.Windows != null && workbook.Windows.Count > 0)
                {
                    workbook.Windows[1].Activate();
                }
                NativeWindowInfo.BringToForeground(NativeWindowInfo.ReadLongMemberPath(_application, "Hwnd"));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private string KeyForWorkbook(Excel.Workbook workbook)
        {
            if (workbook == null)
            {
                return "Excel:NoWorkbook";
            }
            var runtimeKey = DocumentIdentity.RuntimeKey(HostName, workbook);
            return DocumentIdentity.ForOfficeDocument(
                HostName,
                SafeString(delegate { return workbook.Path; }),
                runtimeKey,
                () => workbook.CustomDocumentProperties);
        }

        public IEnumerable<ToolDefinition> GetBuiltInTools()
        {
            return new[]
            {
                Tool("excel.get_context", "Read-only: Return active workbook, sheet, and selection context.", "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}"),
                Tool("excel.get_selection", "Read-only: Read the current or launcher-captured selection values. Rejects selections larger than 100000 cells.", "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}", canSourceHtmlData: true),
                Tool("excel.inspect", "Read-only: Inspect workbook metadata, sheets, charts, tables, defined names, or shapes. For charts, chartName returns one detailed chart; omit it to list.", "{\"type\":\"object\",\"properties\":{\"kind\":{\"type\":\"string\",\"enum\":[\"workbook\",\"sheets\",\"charts\",\"tables\",\"names\",\"shapes\"],\"description\":\"Workbook information to return.\"},\"sheet\":{\"type\":\"string\",\"description\":\"Optional worksheet filter for charts, tables, or shapes.\"},\"chartName\":{\"type\":\"string\",\"description\":\"Optional exact chart name when kind is charts; omit to list chart summaries.\"}},\"required\":[\"kind\"],\"additionalProperties\":false}", canSourceHtmlData: true),
                Tool("excel.read_range", "Read-only: Read worksheet values/formulas, or profile a range/selection. Hard limit: 100000 cells; split larger ranges.", "{\"type\":\"object\",\"properties\":{\"sheet\":{\"type\":\"string\",\"description\":\"Worksheet name; omit only when the active sheet is intended.\"},\"address\":{\"type\":\"string\",\"description\":\"Optional A1 range; values/formulas default to A1, while profile uses the selection or used range when omitted.\"},\"content\":{\"type\":\"string\",\"enum\":[\"values\",\"formulas\",\"profile\"],\"description\":\"Range representation to return.\",\"default\":\"values\"}},\"required\":[],\"additionalProperties\":false}", canSourceHtmlData: true),
                Tool("excel.find_cells", "Read-only: Find literal or regex matches in cell values or formulas and return stable scope coordinates/hash.", "{\"type\":\"object\",\"properties\":{\"sheet\":{\"type\":\"string\",\"description\":\"Worksheet filter; provide it for sheet or range scope.\"},\"address\":{\"type\":\"string\",\"description\":\"A1 range; required when scope is range.\"},\"scope\":{\"type\":\"string\",\"description\":\"Search or operation scope supported by the tool.\",\"enum\":[\"workbook\",\"sheet\",\"range\",\"selection\"]},\"query\":{\"type\":\"string\",\"description\":\"Non-empty literal or regular-expression search query.\",\"minLength\":1},\"mode\":{\"type\":\"string\",\"description\":\"Text matching mode: literal or regex.\",\"default\":\"literal\",\"enum\":[\"literal\",\"regex\"]},\"matchCase\":{\"type\":\"boolean\",\"description\":\"Whether matching is case-sensitive.\",\"default\":false},\"wholeWord\":{\"type\":\"boolean\",\"description\":\"Whether only whole-word matches are accepted.\",\"default\":false},\"lookIn\":{\"type\":\"string\",\"description\":\"Cell content to inspect: values or formulas.\",\"default\":\"values\",\"enum\":[\"values\",\"formulas\",\"both\"]},\"maxResults\":{\"type\":\"integer\",\"description\":\"Maximum number of matches returned.\",\"default\":50},\"contextChars\":{\"type\":\"integer\",\"description\":\"Maximum context characters returned around each match.\",\"default\":80}},\"required\":[\"query\"],\"additionalProperties\":false}"),
                Tool("excel.create_chat_chart", "Read-only: Create an interactive chart artifact in chat from a selection or range.", "{\"type\":\"object\",\"properties\":{\"sheet\":{\"type\":\"string\",\"description\":\"Worksheet containing address; omit when charting the current selection.\"},\"address\":{\"type\":\"string\",\"description\":\"A1 range to chart; omit to use the current selection.\"},\"chartType\":{\"type\":\"string\",\"description\":\"Chart type supported by the current host; use auto when available.\",\"default\":\"auto\"},\"title\":{\"type\":\"string\",\"description\":\"Human-readable title.\",\"default\":\"Excel chart\"}},\"required\":[],\"additionalProperties\":false}"),
                Tool("excel.replace_cells", "Mutates document: Replace bounded literal or regex matches in the current scope. A separate search is optional; runtime reads the scope immediately before mutation and verifies the result.", "{\"type\":\"object\",\"properties\":{\"sheet\":{\"type\":\"string\",\"description\":\"Worksheet filter; provide it for sheet or range scope.\"},\"address\":{\"type\":\"string\",\"description\":\"A1 range; required when scope is range.\"},\"scope\":{\"type\":\"string\",\"description\":\"Search or operation scope supported by the tool.\",\"default\":\"range\",\"enum\":[\"workbook\",\"sheet\",\"range\",\"selection\"]},\"find\":{\"type\":\"string\",\"description\":\"Literal or regular-expression text to find.\",\"minLength\":1},\"replace\":{\"type\":\"string\",\"description\":\"Replacement text; regex capture groups are allowed only in regex mode.\"},\"mode\":{\"type\":\"string\",\"description\":\"Text matching mode: literal or regex.\",\"default\":\"literal\",\"enum\":[\"literal\",\"regex\"]},\"matchCase\":{\"type\":\"boolean\",\"description\":\"Whether matching is case-sensitive.\",\"default\":false},\"wholeWord\":{\"type\":\"boolean\",\"description\":\"Whether only whole-word matches are accepted.\",\"default\":false},\"lookIn\":{\"type\":\"string\",\"description\":\"Cell content to inspect: values or formulas.\",\"default\":\"values\",\"enum\":[\"values\",\"formulas\"]},\"replaceAll\":{\"type\":\"boolean\",\"description\":\"Whether all matches in scope may be replaced.\",\"default\":true},\"maxReplacements\":{\"type\":\"integer\",\"description\":\"Safety limit for replacements.\",\"default\":500}},\"required\":[\"find\"],\"additionalProperties\":false}", true, true, 2, true),
                Tool("excel.write_range", "Mutates document: Write one scalar value, one formula, or a 2D table to a worksheet range.", "{\"type\":\"object\",\"properties\":{\"kind\":{\"type\":\"string\",\"enum\":[\"value\",\"formula\",\"table\"],\"description\":\"Write mode.\"},\"sheet\":{\"type\":\"string\",\"description\":\"Worksheet name; omit only when the active sheet is intended.\"},\"address\":{\"type\":\"string\",\"description\":\"Target A1 range or top-left cell.\",\"default\":\"A1\"},\"value\":{\"type\":[\"string\",\"number\",\"boolean\",\"null\"],\"description\":\"Scalar value when kind is value.\"},\"formula\":{\"type\":\"string\",\"description\":\"Excel formula including the leading equals sign when kind is formula.\"},\"values\":{\"type\":\"array\",\"items\":{\"type\":\"array\",\"items\":{\"type\":[\"string\",\"number\",\"boolean\",\"null\"]}},\"description\":\"Two-dimensional row array when kind is table.\"}},\"required\":[\"kind\"],\"additionalProperties\":false}", true, true, 2),
                Tool("excel.add_table", "Mutates document: Convert a source range into an Excel table.", "{\"type\":\"object\",\"properties\":{\"sheet\":{\"type\":\"string\",\"description\":\"Worksheet name; omit only when the active sheet is intended.\"},\"sourceRange\":{\"type\":\"string\",\"description\":\"A1 range containing the source data.\",\"default\":\"A1:B2\"},\"name\":{\"type\":\"string\",\"description\":\"Human-readable name or exact saved item name, as required by the tool.\"},\"hasHeaders\":{\"type\":\"boolean\",\"description\":\"Whether the first row contains headers.\",\"default\":true},\"style\":{\"type\":\"string\",\"description\":\"Built-in style name supported by the host.\"}},\"required\":[],\"additionalProperties\":false}", true, true, 2),
                Tool("excel.upsert_chart", "Mutates document: Update a named existing chart or create it when missing. Omitted fields are preserved on update; creation uses runtime defaults. Use strict mode only when existence matters.", "{\"type\":\"object\",\"properties\":{\"mode\":{\"type\":\"string\",\"enum\":[\"upsert\",\"createOnly\",\"updateOnly\"],\"description\":\"Existence policy; upsert normally avoids a separate chart lookup.\",\"default\":\"upsert\"},\"sheet\":{\"type\":\"string\",\"description\":\"Worksheet name; omit only when the active sheet is intended for creation or when searching all sheets by chartName.\"},\"chartName\":{\"type\":\"string\",\"description\":\"Exact chart name. Omit to create a chart with Excel's generated name.\"},\"sourceRange\":{\"type\":\"string\",\"description\":\"A1 source range; creation defaults to A1:B6.\"},\"chartType\":{\"type\":\"string\",\"description\":\"Chart type; creation defaults to line.\"},\"title\":{\"type\":\"string\",\"description\":\"Chart title; creation defaults to Chart. Empty text removes an existing title.\"},\"categoryLabelsRange\":{\"type\":\"string\",\"description\":\"A1 range used for chart category labels.\"},\"xAxisTitle\":{\"type\":\"string\",\"description\":\"Horizontal axis title.\"},\"yAxisTitle\":{\"type\":\"string\",\"description\":\"Vertical axis title.\"},\"left\":{\"type\":\"integer\",\"description\":\"Horizontal position in points; creation defaults to 300.\"},\"top\":{\"type\":\"integer\",\"description\":\"Vertical position in points; creation defaults to 20.\"},\"width\":{\"type\":\"integer\",\"description\":\"Width in points; creation defaults to 480.\"},\"height\":{\"type\":\"integer\",\"description\":\"Height in points; creation defaults to 300.\"}},\"required\":[],\"additionalProperties\":false}", true, true, 2),
                Tool("excel.delete_chart", "Mutates document: Delete one existing worksheet chart by name.", "{\"type\":\"object\",\"properties\":{\"sheet\":{\"type\":\"string\",\"description\":\"Optional worksheet filter; omit to find the named chart across the workbook.\"},\"chartName\":{\"type\":\"string\",\"description\":\"Exact worksheet chart object name.\"}},\"required\":[\"chartName\"],\"additionalProperties\":false}", true, true, 3, true),
                Tool("excel.format_range", "Mutates document: Apply number/font/fill/alignment formatting and optionally autofit rows or columns in one call.", "{\"type\":\"object\",\"properties\":{\"sheet\":{\"type\":\"string\",\"description\":\"Worksheet name; omit only when the active sheet is intended.\"},\"address\":{\"type\":\"string\",\"description\":\"Optional A1 range. Formatting defaults to A1; an autofit-only call defaults to the used range.\"},\"numberFormat\":{\"type\":\"string\",\"description\":\"Excel number-format code.\"},\"bold\":{\"type\":\"boolean\",\"description\":\"Whether bold formatting is enabled.\"},\"italic\":{\"type\":\"boolean\",\"description\":\"Whether italic formatting is enabled.\"},\"fillColor\":{\"type\":\"string\",\"description\":\"Fill color as a hex value such as #FFF2CC.\"},\"fontColor\":{\"type\":\"string\",\"description\":\"Font color as a hex value such as #1F1F1F.\"},\"horizontalAlignment\":{\"type\":\"string\",\"description\":\"Horizontal alignment: left, center, right, or general.\",\"enum\":[\"left\",\"center\",\"right\",\"general\"]},\"autoFit\":{\"type\":\"string\",\"description\":\"Optional autofit operation.\",\"enum\":[\"columns\",\"rows\",\"both\"]}},\"required\":[],\"additionalProperties\":false}", true, true, 1),
                Tool("excel.add_sheet", "Mutates document: Add a new worksheet.", "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"Human-readable name or exact saved item name, as required by the tool.\",\"default\":\"AI Sheet\"}},\"required\":[],\"additionalProperties\":false}", true, true, 1),
                Tool("excel.rename_sheet", "Mutates document: Rename a worksheet.", "{\"type\":\"object\",\"properties\":{\"sheet\":{\"type\":\"string\",\"description\":\"Worksheet name; omit only when the active sheet is intended.\"},\"newName\":{\"type\":\"string\",\"description\":\"New exact name.\"}},\"required\":[\"newName\"],\"additionalProperties\":false}", true, true, 2, true),
                Tool("excel.clear_range", "Mutates document: Clear cell values, formats, or both in a range.", "{\"type\":\"object\",\"properties\":{\"sheet\":{\"type\":\"string\",\"description\":\"Worksheet name; omit only when the active sheet is intended.\"},\"address\":{\"type\":\"string\",\"description\":\"A1-style range address.\"},\"clearWhat\":{\"type\":\"string\",\"description\":\"Content to clear: values, formats, or all.\",\"default\":\"values\",\"enum\":[\"values\",\"formats\",\"all\"]}},\"required\":[\"address\"],\"additionalProperties\":false}", true, true, 3, true),
                Tool("excel.sort_range", "Mutates document: Sort rows in a range by one key column.", "{\"type\":\"object\",\"properties\":{\"sheet\":{\"type\":\"string\",\"description\":\"Worksheet name; omit only when the active sheet is intended.\"},\"address\":{\"type\":\"string\",\"description\":\"A1-style range address.\"},\"keyColumn\":{\"type\":\"integer\",\"description\":\"One-based sort-key column index within the range.\",\"default\":1},\"descending\":{\"type\":\"boolean\",\"description\":\"Whether to sort in descending order.\",\"default\":false},\"hasHeaders\":{\"type\":\"boolean\",\"description\":\"Whether the first row contains headers.\",\"default\":true}},\"required\":[\"address\"],\"additionalProperties\":false}", true, true, 2, true),
                Tool("excel.filter_range", "Mutates document: Apply AutoFilter criteria to a range.", "{\"type\":\"object\",\"properties\":{\"sheet\":{\"type\":\"string\",\"description\":\"Worksheet name; omit only when the active sheet is intended.\"},\"address\":{\"type\":\"string\",\"description\":\"A1-style range address.\"},\"field\":{\"type\":\"integer\",\"description\":\"One-based column index within the filter range.\",\"default\":1},\"criteria\":{\"type\":\"string\",\"description\":\"AutoFilter criterion understood by Excel.\"}},\"required\":[\"address\"],\"additionalProperties\":false}", true, true, 2, true),
                Tool("excel.vba_read_module", "Internal VBA backend read; use common.vba_read_module.", "{\"type\":\"object\",\"properties\":{\"moduleName\":{\"type\":\"string\",\"description\":\"Exact VBA component name.\"},\"maxChars\":{\"type\":\"integer\",\"description\":\"Maximum number of text characters returned.\",\"default\":30000,\"minimum\":1,\"maximum\":1000000}},\"required\":[\"moduleName\"],\"additionalProperties\":false}", false, false),
                Tool("excel.vba_read_lines", "Internal VBA backend range read; the public facade is common.vba_read_module.", "{\"type\":\"object\",\"properties\":{\"moduleName\":{\"type\":\"string\",\"description\":\"Exact VBA component name.\"},\"startLine\":{\"type\":\"integer\",\"description\":\"One-based first line.\",\"default\":1,\"minimum\":1},\"lineCount\":{\"type\":\"integer\",\"description\":\"Maximum consecutive lines returned.\",\"default\":200,\"minimum\":1,\"maximum\":500}},\"required\":[\"moduleName\"],\"additionalProperties\":false}", false, false),
                Tool("excel.vba_replace_module", "Mutates document: Replace a VBA module source code and create a rollback backup.", "{\"type\":\"object\",\"properties\":{\"moduleName\":{\"type\":\"string\",\"description\":\"Exact VBA component name.\"},\"code\":{\"type\":\"string\",\"description\":\"Complete VBA source code.\"},\"createIfMissing\":{\"type\":\"boolean\",\"description\":\"Whether a missing VBA standard module may be created.\",\"default\":true}},\"required\":[\"moduleName\",\"code\"],\"additionalProperties\":false}", true, false, 3),
                Tool("excel.insert_vba_module", "Mutates document: Insert a VBA module or return copyable code if trust access is blocked.", "{\"type\":\"object\",\"properties\":{\"moduleName\":{\"type\":\"string\",\"description\":\"Exact VBA component name.\",\"default\":\"RNAssistantModule\"},\"code\":{\"type\":\"string\",\"description\":\"Complete VBA source code.\"}},\"required\":[\"code\"],\"additionalProperties\":false}", true, false, 3),
                Tool("excel.run_macro", "Mutates document: Run an Excel VBA macro by name.", "{\"type\":\"object\",\"properties\":{\"macroName\":{\"type\":\"string\",\"description\":\"Exact public VBA macro name.\"}},\"required\":[\"macroName\"],\"additionalProperties\":false}", true, false, 3)
            };
        }

        public IEnumerable<SkillDefinition> GetBuiltInSkills()
        {
            return new[]
            {
                new SkillDefinition
                {
                    Id = "excel.analysis_reporting",
                    Host = "Excel",
                    Name = "Excel analysis reporting",
                    Description = "Analyze ranges, create summaries, tables, and charts in Excel.",
                    BodyMarkdown = "# Excel Analysis Reporting\n\nUse this skill for Excel reporting tasks.\n\n- For a new report sheet, execute the direct sequence: `excel.add_sheet`, `excel.write_range` with kind=table, optional `excel.format_range` with autoFit, then `excel.upsert_chart`.\n- Do not inspect a brand-new sheet unless existing workbook content is required. Use `excel.inspect` with kind=sheets only when a naming collision must be checked.\n- Inspect sheets/ranges before modifying unknown existing content.\n- Write tables with stable headers and predictable start addresses.\n- Prefer chart source ranges that include headers.\n- Keep generated sheets named clearly and avoid overwriting existing sheets unless asked.\n- If the exact required tool is present in RUNTIME_CONTEXT.tools, execute it; do not report that the capability is missing.",
                    Enabled = true,
                    BuiltIn = true
                }
            };
        }

        public string GetDocumentSnapshot(int maxChars)
        {
            var workbook = ActiveWorkbook();
            if (workbook == null)
            {
                return "No active workbook.";
            }

            var builder = new StringBuilder();
            builder.AppendLine("Workbook: " + workbook.Name);
            foreach (Excel.Worksheet sheet in workbook.Worksheets)
            {
                builder.AppendLine("Sheet: " + sheet.Name);
                var used = sheet.UsedRange;
                builder.AppendLine("UsedRange: " + used.Address[false, false]);
                AppendRangeValues(builder, used, maxChars);
                if (builder.Length >= maxChars)
                {
                    break;
                }
            }

            return Trim(builder.ToString(), maxChars);
        }

        public void PrepareForContextCapture()
        {
            try
            {
                var workbook = ActiveWorkbook();
                if (workbook != null)
                {
                    workbook.Activate();
                    return;
                }

                if (_application.ActiveWindow != null)
                {
                    _application.ActiveWindow.Activate();
                }
            }
            catch
            {
            }
        }

        public ContextNote CaptureSelectionContext(string mode, int maxChars)
        {
            var workbook = RequireWorkbook();
            var range = ResolveSelectionRange(workbook);
            if (range == null)
            {
                throw new InvalidOperationException("Select an Excel range first.");
            }

            var sheet = range.Worksheet as Excel.Worksheet;
            var address = range.Address[false, false];
            var reference = (sheet == null ? string.Empty : sheet.Name + "!") + address;
            var referenceOnly = string.Equals(mode, "reference", StringComparison.OrdinalIgnoreCase);
            var text = referenceOnly
                ? "Reference only. Use Excel tools with this sheet and address if exact cell values are needed."
                : BuildRangeText(range, maxChars);

            return new ContextNote
            {
                Host = HostName,
                Kind = referenceOnly ? "range-reference" : "range",
                Title = "Excel " + reference,
                Reference = reference,
                Source = workbook.Name + " / " + reference,
                Text = text,
                Preview = Trim(text, 360),
                DetailsJson = JsonConvert.SerializeObject(new
                {
                    workbook = workbook.Name,
                    sheet = sheet == null ? string.Empty : sheet.Name,
                    address = address,
                    rows = range.Rows.Count,
                    columns = range.Columns.Count,
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
                    case "excel.get_context":
                        return GetContextTool();
                    case "excel.get_selection":
                        return GetSelectionTool();
                    case "excel.inspect":
                        return InspectWorkbook(command);
                    case "excel.workbook_summary":
                        return WorkbookSummary();
                    case "excel.list_sheets":
                        return ListSheets();
                    case "excel.read_range":
                        return ReadRangeByContent(command);
                    case "excel.read_formula_range":
                        return ReadFormulaRange(command);
                    case "excel.profile_range":
                        return ProfileRange(command);
                    case "excel.find_cells":
                        return FindCells(command);
                    case "excel.replace_cells":
                        return ReplaceCells(command);
                    case "excel.create_chat_chart":
                        return CreateChatChart(command);
                    case "excel.list_charts":
                        return ListCharts(command);
                    case "excel.get_chart":
                        return GetChart(command);
                    case "excel.list_tables":
                        return ListTables(command);
                    case "excel.list_names":
                        return ListNames();
                    case "excel.list_shapes":
                        return ListShapes(command);
                    case "excel.write_range":
                        return WriteRangeByKind(command);
                    case "excel.write_table":
                        return WriteTable(command);
                    case "excel.set_formula":
                        return SetFormula(command);
                    case "excel.add_table":
                        return AddTable(command);
                    case "excel.upsert_chart":
                        return UpsertChart(command);
                    case "excel.add_chart":
                        return AddChart(command);
                    case "excel.update_chart":
                        return UpdateChart(command);
                    case "excel.delete_chart":
                        return DeleteChart(command);
                    case "excel.format_range":
                        return FormatRange(command);
                    case "excel.autofit":
                        return Autofit(command);
                    case "excel.add_sheet":
                        return AddSheet(command);
                    case "excel.rename_sheet":
                        return RenameSheet(command);
                    case "excel.clear_range":
                        return ClearRange(command);
                    case "excel.sort_range":
                        return SortRange(command);
                    case "excel.filter_range":
                        return FilterRange(command);
                    case "excel.vba_list_project_components_internal":
                        return ListVbaProjectComponents();
                    case "excel.vba_read_module":
                        return ReadVbaModule(command);
                    case "excel.vba_read_lines":
                        return ReadVbaLines(command);
                    case "excel.vba_replace_module":
                        return ReplaceVbaModule(command);
                    case "excel.insert_vba_module":
                        return InsertVbaModule(command);
                    case "excel.run_macro":
                        return RunMacro(command);
                    case "excel.vba_install_package_internal":
                        return VbaProjectSupport.InstallPackage(RequireWorkbook(), ToolArgumentReader.String(command.Arguments, "componentsJson", "[]"), ToolArgumentReader.String(command.Arguments, "marker", string.Empty));
                    case "excel.vba_remove_package_internal":
                        return VbaProjectSupport.RemovePackage(RequireWorkbook(), ToolArgumentReader.String(command.Arguments, "expectedComponentsJson", "{}"), ToolArgumentReader.String(command.Arguments, "expectedMarker", string.Empty));
                    case "excel.vba_create_module_internal":
                        return VbaProjectSupport.CreateModule(RequireWorkbook(), ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty), ToolArgumentReader.String(command.Arguments, "componentType", "StdModule"), ToolArgumentReader.String(command.Arguments, "code", string.Empty));
                    case "excel.vba_delete_module_internal":
                        return VbaProjectSupport.DeleteModule(RequireWorkbook(), ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty));
                    default:
                        return ToolResult.Fail("Unsupported Excel tool: " + command.ToolId);
                }
            }
            catch (Exception ex)
            {
                var isVba = (command == null ? string.Empty : command.ToolId ?? string.Empty)
                    .IndexOf(".vba_", StringComparison.OrdinalIgnoreCase) >= 0;
                return ToolResult.Fail(ex.Message, null, isVba ? "vba_access_error" : "office_tool_error", !isVba);
            }
        }

        private ToolResult GetContextTool()
        {
            return ToolResult.Ok("Excel context collected.", JsonConvert.SerializeObject(GetOfficeContext()));
        }

        private ToolResult GetSelectionTool()
        {
            var workbook = RequireWorkbook();
            var range = ResolveSelectionRange(workbook);
            if (range == null)
            {
                return ToolResult.Fail("Select an Excel range first.");
            }

            var sizeError = ValidateReadableRange(range, "Selection");
            if (sizeError != null) return sizeError;

            var sheet = range.Worksheet as Excel.Worksheet;
            return ToolResult.Ok("Selection read.", JsonConvert.SerializeObject(new
            {
                workbook = workbook.Name,
                sheet = sheet == null ? string.Empty : sheet.Name,
                address = range.Address[false, false],
                values = RangeToRows(range)
            }));
        }

        private ToolResult WorkbookSummary()
        {
            var workbook = RequireWorkbook();
            var sheets = new List<object>();
            foreach (Excel.Worksheet sheet in workbook.Worksheets)
            {
                sheets.Add(new { name = sheet.Name, usedRange = sheet.UsedRange.Address[false, false] });
            }

            return ToolResult.Ok("Workbook summary collected.", JsonConvert.SerializeObject(new
            {
                name = workbook.Name,
                fullName = workbook.FullName,
                sheets = sheets
            }));
        }

        private ToolResult ListSheets()
        {
            var workbook = RequireWorkbook();
            var names = new List<string>();
            foreach (Excel.Worksheet sheet in workbook.Worksheets)
            {
                names.Add(sheet.Name);
            }

            return ToolResult.Ok("Sheets listed.", JsonConvert.SerializeObject(names));
        }

        private ToolResult InspectWorkbook(ToolCommand command)
        {
            var kind = ToolArgumentReader.String(command.Arguments, "kind", string.Empty).Trim().ToLowerInvariant();
            switch (kind)
            {
                case "workbook": return WorkbookSummary();
                case "sheets": return ListSheets();
                case "charts": return string.IsNullOrWhiteSpace(ToolArgumentReader.String(command.Arguments, "chartName", string.Empty))
                    ? ListCharts(command)
                    : GetChart(command);
                case "tables": return ListTables(command);
                case "names": return ListNames();
                case "shapes": return ListShapes(command);
                default: return ToolResult.Fail("kind must be workbook, sheets, charts, tables, names, or shapes.");
            }
        }

        private ToolResult ReadRangeByContent(ToolCommand command)
        {
            var content = ToolArgumentReader.String(command.Arguments, "content", "values");
            if (string.Equals(content, "formulas", StringComparison.OrdinalIgnoreCase)) return ReadFormulaRange(command);
            if (string.Equals(content, "profile", StringComparison.OrdinalIgnoreCase)) return ProfileRange(command);
            return string.Equals(content, "values", StringComparison.OrdinalIgnoreCase)
                ? ReadRange(command)
                : ToolResult.Fail("content must be values, formulas, or profile.");
        }

        private ToolResult ReadRange(ToolCommand command)
        {
            var sheet = ResolveSheet(ToolArgumentReader.String(command.Arguments, "sheet", null));
            var address = ToolArgumentReader.String(command.Arguments, "address", "A1");
            var range = sheet.Range[address];
            var sizeError = ValidateReadableRange(range, "Range");
            if (sizeError != null) return sizeError;
            var rows = RangeToRows(range);
            return ToolResult.Ok("Range read: " + sheet.Name + "!" + address, JsonConvert.SerializeObject(rows));
        }

        private ToolResult ReadFormulaRange(ToolCommand command)
        {
            var sheet = ResolveSheet(ToolArgumentReader.String(command.Arguments, "sheet", null));
            var address = ToolArgumentReader.String(command.Arguments, "address", "A1");
            var range = sheet.Range[address];
            var sizeError = ValidateReadableRange(range, "Formula range");
            if (sizeError != null) return sizeError;
            var rows = RangeToFormulaRows(range);
            return ToolResult.Ok("Formula range read: " + sheet.Name + "!" + address, JsonConvert.SerializeObject(rows));
        }

        private ToolResult ProfileRange(ToolCommand command)
        {
            var sheetName = ToolArgumentReader.String(command.Arguments, "sheet", null);
            var address = ToolArgumentReader.String(command.Arguments, "address", string.Empty);
            var sheet = ResolveSheet(sheetName);
            var range = string.IsNullOrWhiteSpace(address)
                ? ResolveSelectionRange(RequireWorkbook()) ?? sheet.UsedRange
                : sheet.Range[address];
            var sizeError = ValidateReadableRange(range, "Profile range");
            if (sizeError != null) return sizeError;
            var rows = RangeToRows(range);
            var formulaRows = RangeToFormulaRows(range);
            var rowCount = rows.Count;
            var columnCount = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
            var blankCells = 0;
            var formulaCells = 0;
            var numericColumns = new List<object>();
            for (var c = 0; c < columnCount; c++)
            {
                var numeric = 0;
                var nonBlank = 0;
                for (var r = 0; r < rowCount; r++)
                {
                    var value = c < rows[r].Count ? rows[r][c] : null;
                    if (IsBlank(value))
                    {
                        blankCells += 1;
                        continue;
                    }

                    nonBlank += 1;
                    if (IsNumeric(value))
                    {
                        numeric += 1;
                    }
                }

                if (nonBlank > 0 && numeric == nonBlank)
                {
                    numericColumns.Add(new
                    {
                        index = c + 1,
                        header = HeaderAt(rows, c),
                        nonBlank = nonBlank
                    });
                }
            }

            for (var r = 0; r < formulaRows.Count; r++)
            {
                for (var c = 0; c < formulaRows[r].Count; c++)
                {
                    var formula = Convert.ToString(formulaRows[r][c]);
                    if (!string.IsNullOrWhiteSpace(formula) && formula.StartsWith("=", StringComparison.Ordinal))
                    {
                        formulaCells += 1;
                    }
                }
            }

            return ToolResult.Ok("Range profiled: " + sheet.Name + "!" + range.Address[false, false], JsonConvert.SerializeObject(new
            {
                sheet = sheet.Name,
                address = range.Address[false, false],
                rows = rowCount,
                columns = columnCount,
                blankCells = blankCells,
                formulaCells = formulaCells,
                headers = rows.Count == 0 ? new string[0] : rows[0].Select(v => Convert.ToString(v)).ToArray(),
                numericColumns = numericColumns,
                sample = rows.Take(10).ToArray()
            }));
        }

        private ToolResult FindCells(ToolCommand command)
        {
            var sheetFilter = ToolArgumentReader.String(command.Arguments, "sheet", string.Empty);
            var address = ToolArgumentReader.String(command.Arguments, "address", string.Empty);
            var scope = ToolArgumentReader.String(command.Arguments, "scope", string.IsNullOrWhiteSpace(sheetFilter) ? "workbook" : "sheet");
            var query = ToolArgumentReader.String(command.Arguments, "query", string.Empty);
            var lookIn = ToolArgumentReader.String(command.Arguments, "lookIn", "values");
            var maxResults = Math.Max(1, Math.Min(500, ToolArgumentReader.Int32(command.Arguments, "maxResults", 50)));
            var contextChars = Math.Max(0, Math.Min(1000, ToolArgumentReader.Int32(command.Arguments, "contextChars", 80)));
            if (string.IsNullOrWhiteSpace(query))
            {
                return ToolResult.Fail("query is required.");
            }

            var options = PatternOptions(command);
            var matches = new List<object>();
            var hashBuilder = new StringBuilder();
            var total = 0;
            try
            {
                foreach (var item in SearchRanges(scope, sheetFilter, address))
                {
                    foreach (Excel.Range cell in item.Range.Cells)
                    {
                        var value = Convert.ToString(cell.Value2) ?? string.Empty;
                        var formula = Convert.ToString(cell.Formula) ?? string.Empty;
                        var hasFormula = Convert.ToBoolean(cell.HasFormula);
                        hashBuilder.Append(item.Sheet.Name).Append('!').Append(cell.Address[false, false]).Append('\n').Append(value).Append('\n').Append(formula).Append('\n');
                        var fields = new List<ExcelSearchField>();
                        if (!hasFormula && !string.Equals(lookIn, "formulas", StringComparison.OrdinalIgnoreCase))
                        {
                            fields.Add(new ExcelSearchField { Name = "value", Text = value });
                        }
                        if (hasFormula && !string.Equals(lookIn, "values", StringComparison.OrdinalIgnoreCase))
                        {
                            if (string.Equals(lookIn, "both", StringComparison.OrdinalIgnoreCase))
                            {
                                fields.Add(new ExcelSearchField { Name = "value", Text = value });
                            }
                            fields.Add(new ExcelSearchField { Name = "formula", Text = formula });
                        }
                        foreach (var field in fields)
                        {
                            var found = TextPatternEngine.Find(field.Text, query, options, Math.Max(1, maxResults - matches.Count), contextChars);
                            total += found.MatchCount;
                            foreach (var match in found.Matches)
                            {
                                if (matches.Count >= maxResults) break;
                                matches.Add(new { sheet = item.Sheet.Name, address = cell.Address[false, false], field = field.Name, start = match.Index, end = match.Index + match.Length, value = value, formula = formula, preview = match.Preview });
                            }
                        }
                    }
                }
                var scopeHash = TextPatternEngine.Sha256(hashBuilder.ToString());
                return ToolResult.Ok("Cells found: " + total, JsonConvert.SerializeObject(new { query = query, mode = options.Mode, scope = scope, matchCount = total, returnedCount = matches.Count, truncated = total > matches.Count, scopeSha256 = scopeHash, contentSha256 = scopeHash, matches = matches }));
            }
            catch (TextPatternException ex)
            {
                return ToolResult.Fail(ex.Message, null, ex.ErrorCode, false);
            }
        }

        private ToolResult ReplaceCells(ToolCommand command)
        {
            var scope = ToolArgumentReader.String(command.Arguments, "scope", "range");
            var sheet = ToolArgumentReader.String(command.Arguments, "sheet", string.Empty);
            var address = ToolArgumentReader.String(command.Arguments, "address", string.Empty);
            var find = ToolArgumentReader.String(command.Arguments, "find", string.Empty);
            var replacement = ToolArgumentReader.String(command.Arguments, "replace", string.Empty);
            var lookIn = ToolArgumentReader.String(command.Arguments, "lookIn", "values");
            if (string.Equals(lookIn, "both", StringComparison.OrdinalIgnoreCase)) return ToolResult.Fail("replace_cells lookIn must be values or formulas.", null, "invalid_arguments", false);
            var replaceAll = ToolArgumentReader.Boolean(command.Arguments, "replaceAll", true);
            var maxReplacements = Math.Max(1, Math.Min(10000, ToolArgumentReader.Int32(command.Arguments, "maxReplacements", 500)));

            var options = PatternOptions(command);
            var targets = new List<ExcelCellReplacement>();
            var replacementPlanned = false;
            try
            {
                foreach (var item in SearchRanges(scope, sheet, address))
                {
                    foreach (Excel.Range cell in item.Range.Cells)
                    {
                        var value = Convert.ToString(cell.Value2) ?? string.Empty;
                        var formula = Convert.ToString(cell.Formula) ?? string.Empty;
                        var hasFormula = Convert.ToBoolean(cell.HasFormula);
                        if (string.Equals(lookIn, "values", StringComparison.OrdinalIgnoreCase) && hasFormula) continue;
                        if (string.Equals(lookIn, "formulas", StringComparison.OrdinalIgnoreCase) && !hasFormula) continue;
                        var current = string.Equals(lookIn, "formulas", StringComparison.OrdinalIgnoreCase) ? formula : value;
                        var found = TextPatternEngine.Find(current, find, options, 1, 0);
                        if (found.MatchCount > 0 && (replaceAll || !replacementPlanned))
                        {
                            var replaced = TextPatternEngine.Replace(current, find, replacement, options, replaceAll, maxReplacements);
                            if (replaced.MatchCount > 0)
                            {
                                targets.Add(new ExcelCellReplacement { Cell = cell, Formula = string.Equals(lookIn, "formulas", StringComparison.OrdinalIgnoreCase), Text = replaced.Text, Count = replaced.MatchCount });
                                replacementPlanned = true;
                            }
                        }
                    }
                }
                var total = targets.Sum(target => target.Count);
                if (total > maxReplacements) return ToolResult.Fail("Replacement count exceeds maxReplacements=" + maxReplacements + ".", null, "replacement_limit_exceeded", false);
                foreach (var target in targets)
                {
                    if (target.Formula) target.Cell.Formula = target.Text;
                    else target.Cell.Value2 = target.Text;
                }

                var verifyCommand = new ToolCommand { ToolId = "excel.find_cells" };
                verifyCommand.Arguments["query"] = find;
                foreach (var name in new[] { "sheet", "address", "scope", "mode", "matchCase", "wholeWord", "lookIn" })
                    if (command.Arguments.ContainsKey(name)) verifyCommand.Arguments[name] = command.Arguments[name];
                verifyCommand.Arguments["maxResults"] = 500;
                verifyCommand.Arguments["contextChars"] = 80;
                var post = FindCells(verifyCommand);
                if (!post.Success) return post;
                var postJson = JObject.Parse(post.DataJson ?? "{}");
                var postHash = (string)postJson["scopeSha256"];
                return ToolResult.Ok("Excel replacements completed: " + total + ".", JsonConvert.SerializeObject(new { replacements = total, scopeSha256 = postHash }));
            }
            catch (TextPatternException ex)
            {
                return ToolResult.Fail(ex.Message, null, ex.ErrorCode, false);
            }
        }

        private TextPatternOptions PatternOptions(ToolCommand command)
        {
            return new TextPatternOptions
            {
                Mode = ToolArgumentReader.String(command.Arguments, "mode", "literal"),
                MatchCase = ToolArgumentReader.Boolean(command.Arguments, "matchCase", false),
                WholeWord = ToolArgumentReader.Boolean(command.Arguments, "wholeWord", false)
            };
        }

        private IEnumerable<ExcelSearchRange> SearchRanges(string scope, string sheetName, string address)
        {
            var workbook = RequireWorkbook();
            if (string.Equals(scope, "selection", StringComparison.OrdinalIgnoreCase))
            {
                var selected = ResolveSelectionRange(workbook);
                if (selected == null) throw new InvalidOperationException("No Excel range is selected.");
                yield return new ExcelSearchRange { Sheet = (Excel.Worksheet)selected.Worksheet, Range = selected };
                yield break;
            }
            if (string.Equals(scope, "range", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(address)) throw new InvalidOperationException("address is required for range scope.");
                var rangeSheet = ResolveSheet(sheetName);
                yield return new ExcelSearchRange { Sheet = rangeSheet, Range = rangeSheet.Range[address] };
                yield break;
            }
            if (string.Equals(scope, "sheet", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(sheetName))
            {
                var selectedSheet = ResolveSheet(sheetName);
                yield return new ExcelSearchRange { Sheet = selectedSheet, Range = selectedSheet.UsedRange };
                yield break;
            }
            foreach (Excel.Worksheet candidate in workbook.Worksheets)
            {
                yield return new ExcelSearchRange { Sheet = candidate, Range = candidate.UsedRange };
            }
        }

        private sealed class ExcelSearchRange { public Excel.Worksheet Sheet { get; set; } public Excel.Range Range { get; set; } }
        private sealed class ExcelSearchField { public string Name { get; set; } public string Text { get; set; } }
        private sealed class ExcelCellReplacement { public Excel.Range Cell { get; set; } public bool Formula { get; set; } public string Text { get; set; } public int Count { get; set; } }

        private ToolResult ListCharts(ToolCommand command)
        {
            var workbook = RequireWorkbook();
            var sheetFilter = ToolArgumentReader.String(command.Arguments, "sheet", string.Empty);
            var charts = new List<object>();
            foreach (Excel.Worksheet sheet in workbook.Worksheets)
            {
                if (!string.IsNullOrWhiteSpace(sheetFilter) &&
                    !string.Equals(sheet.Name, sheetFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var chartObjects = (Excel.ChartObjects)sheet.ChartObjects(Type.Missing);
                var chartCount = chartObjects.Count;
                for (var i = 1; i <= chartCount; i++)
                {
                    var chartObject = (Excel.ChartObject)chartObjects.Item(i);
                    charts.Add(ChartDetails(sheet, chartObject));
                }
            }

            return ToolResult.Ok("Charts listed: " + charts.Count, JsonConvert.SerializeObject(charts));
        }

        private ToolResult GetChart(ToolCommand command)
        {
            Excel.Worksheet sheet;
            var chartObject = ResolveChartObject(
                ToolArgumentReader.String(command.Arguments, "sheet", string.Empty),
                ToolArgumentReader.String(command.Arguments, "chartName", string.Empty),
                out sheet);
            return ToolResult.Ok("Chart read: " + chartObject.Name, JsonConvert.SerializeObject(ChartDetails(sheet, chartObject)));
        }

        private ToolResult ListTables(ToolCommand command)
        {
            var workbook = RequireWorkbook();
            var sheetFilter = ToolArgumentReader.String(command.Arguments, "sheet", string.Empty);
            var tables = new List<object>();
            foreach (Excel.Worksheet sheet in workbook.Worksheets)
            {
                if (!string.IsNullOrWhiteSpace(sheetFilter) &&
                    !string.Equals(sheet.Name, sheetFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (Excel.ListObject table in sheet.ListObjects)
                {
                    tables.Add(new
                    {
                        sheet = sheet.Name,
                        name = table.Name,
                        displayName = table.DisplayName,
                        range = table.Range == null ? string.Empty : table.Range.Address[false, false],
                        rows = table.ListRows.Count,
                        columns = table.ListColumns.Count
                    });
                }
            }

            return ToolResult.Ok("Tables listed: " + tables.Count, JsonConvert.SerializeObject(tables));
        }

        private ToolResult ListNames()
        {
            var workbook = RequireWorkbook();
            var names = new List<object>();
            foreach (Excel.Name name in workbook.Names)
            {
                names.Add(new
                {
                    name = name.Name,
                    refersTo = name.RefersTo,
                    value = SafeString(delegate { return Convert.ToString(name.RefersToRange == null ? string.Empty : name.RefersToRange.Value2); })
                });
            }

            return ToolResult.Ok("Defined names listed: " + names.Count, JsonConvert.SerializeObject(names));
        }

        private ToolResult ListShapes(ToolCommand command)
        {
            var workbook = RequireWorkbook();
            var sheetFilter = ToolArgumentReader.String(command.Arguments, "sheet", string.Empty);
            var shapes = new List<object>();
            foreach (Excel.Worksheet sheet in workbook.Worksheets)
            {
                if (!string.IsNullOrWhiteSpace(sheetFilter) &&
                    !string.Equals(sheet.Name, sheetFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (Excel.Shape shape in sheet.Shapes)
                {
                    shapes.Add(new
                    {
                        sheet = sheet.Name,
                        name = shape.Name,
                        type = shape.Type.ToString(),
                        left = shape.Left,
                        top = shape.Top,
                        width = shape.Width,
                        height = shape.Height,
                        alternativeText = SafeString(delegate { return shape.AlternativeText; })
                    });
                }
            }

            return ToolResult.Ok("Shapes listed: " + shapes.Count, JsonConvert.SerializeObject(shapes));
        }

        private ToolResult CreateChatChart(ToolCommand command)
        {
            var workbook = RequireWorkbook();
            var sheetName = ToolArgumentReader.String(command.Arguments, "sheet", null);
            var address = ToolArgumentReader.String(command.Arguments, "address", string.Empty);
            Excel.Worksheet sheet = null;
            var range = ResolveSelectionRange(workbook);
            if (!string.IsNullOrWhiteSpace(address))
            {
                sheet = ResolveSheet(sheetName);
                range = sheet.Range[address];
            }
            if (range == null)
            {
                return ToolResult.Fail("Select an Excel range first or provide sheet/address.");
            }

            sheet = range.Worksheet as Excel.Worksheet;
            var rowCount = Convert.ToInt32(range.Rows.Count);
            var columnCount = Convert.ToInt32(range.Columns.Count);
            var cellCount = rowCount * columnCount;
            if (cellCount > 10000)
            {
                return ToolResult.Fail("Selected range is too large for a chat chart: " + cellCount + " cells. Limit is 10000 cells.");
            }

            var rows = RangeToRows(range);
            var artifact = new ChartArtifactBuilder().Build(
                rows.Select(r => (IList<object>)r).ToList(),
                new ChartArtifactSource
                {
                    Host = "Excel",
                    Workbook = workbook.Name,
                    Sheet = sheet == null ? string.Empty : sheet.Name,
                    Address = range.Address[false, false],
                    SourceMode = string.IsNullOrWhiteSpace(address) ? "selection" : "range"
                },
                ToolArgumentReader.String(command.Arguments, "title", "Excel chart"),
                ToolArgumentReader.String(command.Arguments, "chartType", "auto"));

            return ToolResult.Ok(
                "Chat chart artifact created: " + artifact.Title,
                JsonConvert.SerializeObject(artifact));
        }

        private ToolResult WriteRange(ToolCommand command)
        {
            var sheet = ResolveSheet(ToolArgumentReader.String(command.Arguments, "sheet", null));
            var address = ToolArgumentReader.String(command.Arguments, "address", "A1");
            object value;
            if (command.Arguments == null || !command.Arguments.TryGetValue("value", out value))
            {
                return ToolResult.Fail("value is required when kind is value.");
            }
            sheet.Range[address].Value2 = value;
            return ToolResult.Ok("Wrote value to " + sheet.Name + "!" + address);
        }

        private ToolResult WriteRangeByKind(ToolCommand command)
        {
            var kind = ToolArgumentReader.String(command.Arguments, "kind", "value").Trim().ToLowerInvariant();
            if (kind == "formula") return SetFormula(command);
            if (kind == "table")
            {
                if (!command.Arguments.ContainsKey("values")) return ToolResult.Fail("values is required when kind is table.");
                if (!command.Arguments.ContainsKey("startAddress"))
                {
                    command.Arguments["startAddress"] = ToolArgumentReader.String(command.Arguments, "address", "A1");
                }
                return WriteTable(command);
            }
            return kind == "value"
                ? WriteRange(command)
                : ToolResult.Fail("kind must be value, formula, or table.");
        }

        private ToolResult WriteTable(ToolCommand command)
        {
            var sheet = ResolveSheet(ToolArgumentReader.String(command.Arguments, "sheet", null));
            var startAddress = ToolArgumentReader.String(command.Arguments, "startAddress", "A1");
            var valuesJson = ToolArgumentReader.String(command.Arguments, "values", "[]");
            var values = JArray.Parse(valuesJson);
            if (values.Count == 0)
            {
                return ToolResult.Fail("No table values provided.");
            }

            var rows = values.Count;
            var columns = 0;
            foreach (var rowToken in values)
            {
                var row = rowToken as JArray;
                if (row == null)
                {
                    return ToolResult.Fail("Table values must be a 2D JSON array.");
                }
                columns = Math.Max(columns, row.Count);
            }
            if (columns == 0)
            {
                return ToolResult.Fail("No table columns provided.");
            }

            var data = new object[rows, columns];
            for (var r = 0; r < rows; r++)
            {
                var row = (JArray)values[r];
                for (var c = 0; c < columns; c++)
                {
                    data[r, c] = c < row.Count ? ToCellValue(row[c]) : null;
                }
            }

            var start = sheet.Range[startAddress];
            var target = start.Resize[rows, columns];
            target.Value2 = data;
            return ToolResult.Ok("Wrote table to " + sheet.Name + "!" + target.Address[false, false], JsonConvert.SerializeObject(new { sheet = sheet.Name, range = target.Address[false, false], rows = rows, columns = columns }));
        }

        private ToolResult SetFormula(ToolCommand command)
        {
            var sheet = ResolveSheet(ToolArgumentReader.String(command.Arguments, "sheet", null));
            var address = ToolArgumentReader.String(command.Arguments, "address", "A1");
            var formula = ToolArgumentReader.String(command.Arguments, "formula", string.Empty);
            if (string.IsNullOrWhiteSpace(formula))
            {
                return ToolResult.Fail("formula is required.");
            }

            sheet.Range[address].Formula = formula;
            return ToolResult.Ok("Formula set in " + sheet.Name + "!" + address);
        }

        private ToolResult AddTable(ToolCommand command)
        {
            var sheet = ResolveSheet(ToolArgumentReader.String(command.Arguments, "sheet", null));
            var sourceRange = ToolArgumentReader.String(command.Arguments, "sourceRange", "A1:B2");
            var name = ToolArgumentReader.String(command.Arguments, "name", string.Empty);
            var hasHeaders = ToolArgumentReader.Boolean(command.Arguments, "hasHeaders", true);
            var style = ToolArgumentReader.String(command.Arguments, "style", string.Empty);
            var range = sheet.Range[sourceRange];
            var table = sheet.ListObjects.Add(
                Excel.XlListObjectSourceType.xlSrcRange,
                range,
                Type.Missing,
                hasHeaders ? Excel.XlYesNoGuess.xlYes : Excel.XlYesNoGuess.xlNo,
                Type.Missing);
            if (!string.IsNullOrWhiteSpace(name))
            {
                table.Name = name;
            }
            if (!string.IsNullOrWhiteSpace(style))
            {
                table.TableStyle = style;
            }

            return ToolResult.Ok("Table added: " + table.Name, JsonConvert.SerializeObject(new { sheet = sheet.Name, name = table.Name, range = table.Range.Address[false, false] }));
        }

        private ToolResult UpsertChart(ToolCommand command)
        {
            var mode = ToolArgumentReader.String(command.Arguments, "mode", "upsert");
            var chartName = ToolArgumentReader.String(command.Arguments, "chartName", string.Empty);
            Excel.Worksheet existingSheet;
            var existing = FindChartObject(
                ToolArgumentReader.String(command.Arguments, "sheet", string.Empty),
                chartName,
                out existingSheet);
            if (existing != null)
            {
                if (string.Equals(mode, "createOnly", StringComparison.OrdinalIgnoreCase))
                {
                    return ToolResult.Fail("Chart already exists: " + chartName + ". Use mode=upsert or updateOnly.", null, "chart_already_exists", false);
                }
                return UpdateChart(command);
            }
            if (string.Equals(mode, "updateOnly", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Fail(string.IsNullOrWhiteSpace(chartName)
                    ? "chartName is required for mode=updateOnly."
                    : "Chart not found: " + chartName + ". Use mode=upsert or createOnly.", null, "chart_not_found", false);
            }
            return AddChart(command);
        }

        private ToolResult AddChart(ToolCommand command)
        {
            var sheet = ResolveSheet(ToolArgumentReader.String(command.Arguments, "sheet", null));
            var sourceRange = ToolArgumentReader.String(command.Arguments, "sourceRange", "A1:B6");
            var title = ToolArgumentReader.String(command.Arguments, "title", "Chart");
            var chartType = ToolArgumentReader.String(command.Arguments, "chartType", "line");
            var left = ToolArgumentReader.Int32(command.Arguments, "left", 300);
            var top = ToolArgumentReader.Int32(command.Arguments, "top", 20);
            var width = ToolArgumentReader.Int32(command.Arguments, "width", 480);
            var height = ToolArgumentReader.Int32(command.Arguments, "height", 300);

            var source = sheet.Range[sourceRange];
            var chartObjects = (Excel.ChartObjects)sheet.ChartObjects(Type.Missing);
            var chartObject = chartObjects.Add(left, top, width, height);
            var chart = chartObject.Chart;
            chart.SetSourceData(source);
            chart.ChartType = ResolveChartType(chartType);
            chart.HasTitle = true;
            chart.ChartTitle.Caption = title;
            var chartName = ToolArgumentReader.String(command.Arguments, "chartName", string.Empty);
            if (!string.IsNullOrWhiteSpace(chartName))
            {
                chartObject.Name = chartName;
            }
            ApplyChartLabels(command, sheet, chart);
            return ToolResult.Ok("Chart added: " + chartObject.Name, JsonConvert.SerializeObject(ChartDetails(sheet, chartObject)));
        }

        private ToolResult UpdateChart(ToolCommand command)
        {
            Excel.Worksheet sheet;
            var chartObject = ResolveChartObject(
                ToolArgumentReader.String(command.Arguments, "sheet", string.Empty),
                ToolArgumentReader.String(command.Arguments, "chartName", string.Empty),
                out sheet);
            var chart = chartObject.Chart;

            if (command.Arguments.ContainsKey("sourceRange"))
            {
                var sourceRange = ToolArgumentReader.String(command.Arguments, "sourceRange", string.Empty);
                if (!string.IsNullOrWhiteSpace(sourceRange))
                {
                    chart.SetSourceData(sheet.Range[sourceRange]);
                }
            }
            if (command.Arguments.ContainsKey("chartType"))
            {
                var chartType = ToolArgumentReader.String(command.Arguments, "chartType", string.Empty);
                if (!string.IsNullOrWhiteSpace(chartType))
                {
                    chart.ChartType = ResolveChartType(chartType);
                }
            }
            if (command.Arguments.ContainsKey("title"))
            {
                var title = ToolArgumentReader.String(command.Arguments, "title", string.Empty);
                var hasTitle = !string.IsNullOrWhiteSpace(title);
                chart.HasTitle = hasTitle;
                if (hasTitle)
                {
                    chart.ChartTitle.Caption = title;
                }
            }
            if (command.Arguments.ContainsKey("left"))
            {
                chartObject.Left = ToolArgumentReader.Int32(command.Arguments, "left", Convert.ToInt32(chartObject.Left));
            }
            if (command.Arguments.ContainsKey("top"))
            {
                chartObject.Top = ToolArgumentReader.Int32(command.Arguments, "top", Convert.ToInt32(chartObject.Top));
            }
            if (command.Arguments.ContainsKey("width"))
            {
                chartObject.Width = Math.Max(40, ToolArgumentReader.Int32(command.Arguments, "width", Convert.ToInt32(chartObject.Width)));
            }
            if (command.Arguments.ContainsKey("height"))
            {
                chartObject.Height = Math.Max(40, ToolArgumentReader.Int32(command.Arguments, "height", Convert.ToInt32(chartObject.Height)));
            }

            ApplyChartLabels(command, sheet, chart);
            return ToolResult.Ok("Chart updated: " + chartObject.Name, JsonConvert.SerializeObject(ChartDetails(sheet, chartObject)));
        }

        private ToolResult DeleteChart(ToolCommand command)
        {
            Excel.Worksheet sheet;
            var chartObject = ResolveChartObject(
                ToolArgumentReader.String(command.Arguments, "sheet", string.Empty),
                ToolArgumentReader.String(command.Arguments, "chartName", string.Empty),
                out sheet);
            var chartName = chartObject.Name;
            chartObject.Delete();
            return ToolResult.Ok("Chart deleted: " + chartName, JsonConvert.SerializeObject(new { sheet = sheet.Name, chartName = chartName }));
        }

        private ToolResult FormatRange(ToolCommand command)
        {
            var address = ToolArgumentReader.String(command.Arguments, "address", string.Empty);
            var autoFit = ToolArgumentReader.String(command.Arguments, "autoFit", string.Empty);
            var hasFormatting = command.Arguments.ContainsKey("numberFormat") ||
                command.Arguments.ContainsKey("bold") ||
                command.Arguments.ContainsKey("italic") ||
                command.Arguments.ContainsKey("fillColor") ||
                command.Arguments.ContainsKey("fontColor") ||
                command.Arguments.ContainsKey("horizontalAlignment");
            if (!hasFormatting && string.IsNullOrWhiteSpace(autoFit))
            {
                return ToolResult.Fail("Provide at least one formatting field or autoFit operation.");
            }
            var sheet = ResolveSheet(ToolArgumentReader.String(command.Arguments, "sheet", null));
            var range = string.IsNullOrWhiteSpace(address)
                ? (!hasFormatting && !string.IsNullOrWhiteSpace(autoFit) ? sheet.UsedRange : sheet.Range["A1"])
                : sheet.Range[address];
            var numberFormat = ToolArgumentReader.String(command.Arguments, "numberFormat", string.Empty);
            if (!string.IsNullOrWhiteSpace(numberFormat))
            {
                range.NumberFormat = numberFormat;
            }
            if (command.Arguments.ContainsKey("bold"))
            {
                range.Font.Bold = ToolArgumentReader.Boolean(command.Arguments, "bold", false);
            }
            if (command.Arguments.ContainsKey("italic"))
            {
                range.Font.Italic = ToolArgumentReader.Boolean(command.Arguments, "italic", false);
            }

            var fillColor = ToolArgumentReader.String(command.Arguments, "fillColor", string.Empty);
            if (!string.IsNullOrWhiteSpace(fillColor))
            {
                range.Interior.Color = OleColor(fillColor);
            }
            var fontColor = ToolArgumentReader.String(command.Arguments, "fontColor", string.Empty);
            if (!string.IsNullOrWhiteSpace(fontColor))
            {
                range.Font.Color = OleColor(fontColor);
            }

            var horizontal = ToolArgumentReader.String(command.Arguments, "horizontalAlignment", string.Empty);
            if (!string.IsNullOrWhiteSpace(horizontal))
            {
                range.HorizontalAlignment = ResolveHorizontalAlignment(horizontal);
            }

            if (string.Equals(autoFit, "columns", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(autoFit, "both", StringComparison.OrdinalIgnoreCase))
            {
                range.Columns.AutoFit();
            }
            if (string.Equals(autoFit, "rows", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(autoFit, "both", StringComparison.OrdinalIgnoreCase))
            {
                range.Rows.AutoFit();
            }

            return ToolResult.Ok("Range formatted: " + sheet.Name + "!" + range.Address[false, false]);
        }

        private ToolResult Autofit(ToolCommand command)
        {
            var sheet = ResolveSheet(ToolArgumentReader.String(command.Arguments, "sheet", null));
            var address = ToolArgumentReader.String(command.Arguments, "address", string.Empty);
            var range = string.IsNullOrWhiteSpace(address) ? sheet.UsedRange : sheet.Range[address];
            range.Columns.AutoFit();
            range.Rows.AutoFit();
            return ToolResult.Ok("Autofit applied to " + sheet.Name + "!" + range.Address[false, false], JsonConvert.SerializeObject(new { sheet = sheet.Name, range = range.Address[false, false] }));
        }

        private ToolResult AddSheet(ToolCommand command)
        {
            var workbook = RequireWorkbook();
            var name = ToolArgumentReader.String(command.Arguments, "name", "AI Sheet");
            ValidateWorksheetName(workbook, name, null);
            Excel.Worksheet sheet = null;
            try
            {
                sheet = (Excel.Worksheet)workbook.Worksheets.Add();
                sheet.Name = name;
            }
            catch
            {
                if (sheet != null)
                {
                    var displayAlerts = _application.DisplayAlerts;
                    try
                    {
                        _application.DisplayAlerts = false;
                        sheet.Delete();
                    }
                    catch
                    {
                    }
                    finally
                    {
                        _application.DisplayAlerts = displayAlerts;
                    }
                }
                throw;
            }
            return ToolResult.Ok("Added sheet: " + name);
        }

        private ToolResult RenameSheet(ToolCommand command)
        {
            var sheet = ResolveSheet(ToolArgumentReader.String(command.Arguments, "sheet", null));
            var newName = ToolArgumentReader.String(command.Arguments, "newName", string.Empty);
            if (string.IsNullOrWhiteSpace(newName))
            {
                return ToolResult.Fail("newName is required.");
            }

            var oldName = sheet.Name;
            ValidateWorksheetName(RequireWorkbook(), newName, oldName);
            sheet.Name = newName;
            return ToolResult.Ok("Renamed sheet " + oldName + " to " + newName);
        }

        private ToolResult ClearRange(ToolCommand command)
        {
            var sheet = ResolveSheet(ToolArgumentReader.String(command.Arguments, "sheet", null));
            var address = ToolArgumentReader.String(command.Arguments, "address", string.Empty);
            if (string.IsNullOrWhiteSpace(address))
            {
                return ToolResult.Fail("address is required.");
            }

            var clearWhat = ToolArgumentReader.String(command.Arguments, "clearWhat", "values");
            var range = sheet.Range[address];
            if (string.Equals(clearWhat, "formats", StringComparison.OrdinalIgnoreCase))
            {
                range.ClearFormats();
            }
            else if (string.Equals(clearWhat, "all", StringComparison.OrdinalIgnoreCase))
            {
                range.Clear();
            }
            else
            {
                range.ClearContents();
            }

            return ToolResult.Ok("Range cleared: " + sheet.Name + "!" + address + " (" + clearWhat + ")");
        }

        private ToolResult SortRange(ToolCommand command)
        {
            var sheet = ResolveSheet(ToolArgumentReader.String(command.Arguments, "sheet", null));
            var address = ToolArgumentReader.String(command.Arguments, "address", string.Empty);
            if (string.IsNullOrWhiteSpace(address))
            {
                return ToolResult.Fail("address is required.");
            }

            var range = sheet.Range[address];
            var keyColumn = Math.Max(1, ToolArgumentReader.Int32(command.Arguments, "keyColumn", 1));
            var columnCount = Convert.ToInt32(range.Columns.Count);
            if (keyColumn > columnCount)
            {
                return ToolResult.Fail("keyColumn is outside the sort range.");
            }

            var descending = ToolArgumentReader.Boolean(command.Arguments, "descending", false);
            var hasHeaders = ToolArgumentReader.Boolean(command.Arguments, "hasHeaders", true);
            var key = range.Columns[keyColumn] as Excel.Range;
            if (key == null)
            {
                return ToolResult.Fail("Sort key column could not be resolved.");
            }

            range.Sort(
                Key1: key,
                Order1: descending ? Excel.XlSortOrder.xlDescending : Excel.XlSortOrder.xlAscending,
                Header: hasHeaders ? Excel.XlYesNoGuess.xlYes : Excel.XlYesNoGuess.xlNo,
                Orientation: Excel.XlSortOrientation.xlSortColumns);
            return ToolResult.Ok("Range sorted: " + sheet.Name + "!" + address);
        }

        private ToolResult FilterRange(ToolCommand command)
        {
            var sheet = ResolveSheet(ToolArgumentReader.String(command.Arguments, "sheet", null));
            var address = ToolArgumentReader.String(command.Arguments, "address", string.Empty);
            if (string.IsNullOrWhiteSpace(address))
            {
                return ToolResult.Fail("address is required.");
            }

            var field = Math.Max(1, ToolArgumentReader.Int32(command.Arguments, "field", 1));
            var criteria = ToolArgumentReader.String(command.Arguments, "criteria", string.Empty);
            var range = sheet.Range[address];
            var columnCount = Convert.ToInt32(range.Columns.Count);
            if (field > columnCount)
            {
                return ToolResult.Fail("field is outside the filter range.");
            }

            range.AutoFilter(field, string.IsNullOrWhiteSpace(criteria) ? Type.Missing : criteria, Excel.XlAutoFilterOperator.xlAnd, Type.Missing, true);
            return ToolResult.Ok("Range filtered: " + sheet.Name + "!" + address);
        }

        private ToolResult ListVbaProjectComponents()
        {
            var workbook = RequireWorkbook();
            return VbaProjectSupport.ListProjectComponents(workbook, workbook.Name);
        }

        private ToolResult ReadVbaModule(ToolCommand command)
        {
            var workbook = RequireWorkbook();
            var moduleName = ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty);
            var maxChars = ToolArgumentReader.Int32(command.Arguments, "maxChars", 30000);
            return VbaProjectSupport.ReadModule(workbook, moduleName, maxChars);
        }

        private ToolResult ReadVbaLines(ToolCommand command)
        {
            return VbaProjectSupport.ReadModuleLines(
                RequireWorkbook(),
                ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty),
                ToolArgumentReader.Int32(command.Arguments, "startLine", 1),
                ToolArgumentReader.Int32(command.Arguments, "lineCount", 200));
        }

        private ToolResult ReplaceVbaModule(ToolCommand command)
        {
            var workbook = RequireWorkbook();
            var moduleName = ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty);
            var code = ToolArgumentReader.String(command.Arguments, "code", string.Empty);
            var createIfMissing = ToolArgumentReader.Boolean(command.Arguments, "createIfMissing", true);
            return VbaProjectSupport.ReplaceModule(workbook, moduleName, code, createIfMissing);
        }

        private ToolResult InsertVbaModule(ToolCommand command)
        {
            var workbook = RequireWorkbook();
            var moduleName = ToolArgumentReader.String(command.Arguments, "moduleName", "RNAssistantModule");
            var code = ToolArgumentReader.String(command.Arguments, "code", string.Empty);
            if (string.IsNullOrWhiteSpace(code))
            {
                return ToolResult.Fail("No VBA code provided.");
            }

            try
            {
                return VbaProjectSupport.InsertModule(workbook, moduleName, code);
            }
            catch (Exception ex)
            {
                return ToolResult.Fail("VBA insert was blocked. Enable 'Trust access to the VBA project object model' or copy the code manually. " + ex.Message, JsonConvert.SerializeObject(new { moduleName = moduleName, code = code }), "vba_access_error", false);
            }
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

        private Excel.Workbook ActiveWorkbook()
        {
            if (HasTargetDocument())
            {
                return TargetWorkbook();
            }

            try { return _application.ActiveWorkbook; }
            catch { return null; }
        }

        private Excel.Workbook TargetWorkbook()
        {
            if (_targetWorkbook != null)
            {
                try
                {
                    var ignored = _targetWorkbook.Name;
                    return _targetWorkbook;
                }
                catch
                {
                    return null;
                }
            }

            if (!HasTargetDocument())
            {
                return null;
            }

            foreach (Excel.Workbook workbook in _application.Workbooks)
            {
                if (MatchesWorkbook(workbook))
                {
                    return workbook;
                }
            }

            return null;
        }

        private bool HasTargetDocument()
        {
            return _targetWorkbook != null || _target != null && _target.HasDocumentIdentity;
        }

        private bool MatchesWorkbook(Excel.Workbook workbook)
        {
            if (workbook == null)
            {
                return false;
            }

            var fullName = SafeString(delegate { return workbook.FullName; });
            if (!string.IsNullOrWhiteSpace(_target.FullName) && SamePath(fullName, _target.FullName))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(_target.Path) && SamePath(fullName, _target.Path))
            {
                return true;
            }

            var name = SafeString(delegate { return workbook.Name; });
            return string.IsNullOrWhiteSpace(_target.FullName)
                && string.IsNullOrWhiteSpace(_target.Path)
                && !string.IsNullOrWhiteSpace(_target.Name)
                && string.Equals(name, _target.Name, StringComparison.OrdinalIgnoreCase);
        }

        private Excel.Workbook RequireWorkbook()
        {
            var workbook = ActiveWorkbook();
            if (workbook == null)
            {
                throw new InvalidOperationException(!HasTargetDocument()
                    ? "No active workbook."
                    : "Target Excel workbook is not open.");
            }

            return workbook;
        }

        private Excel.Worksheet ResolveSheet(string name)
        {
            var workbook = RequireWorkbook();
            if (string.IsNullOrWhiteSpace(name))
            {
                try
                {
                    var activeSheet = _application.ActiveSheet as Excel.Worksheet;
                    if (activeSheet != null && SameWorkbook(activeSheet.Parent as Excel.Workbook, workbook))
                    {
                        return activeSheet;
                    }
                }
                catch
                {
                }

                try
                {
                    var workbookActiveSheet = workbook.ActiveSheet as Excel.Worksheet;
                    if (workbookActiveSheet != null)
                    {
                        return workbookActiveSheet;
                    }
                }
                catch
                {
                }

                var firstSheet = FirstWorksheet(workbook);
                if (firstSheet != null)
                {
                    return firstSheet;
                }

                throw new InvalidOperationException("Workbook has no worksheets.");
            }

            var sheet = FindWorksheet(workbook, name);
            if (sheet == null)
            {
                throw new InvalidOperationException("Worksheet not found: " + name);
            }

            return sheet;
        }

        private Excel.Range ResolveSelectionRange(Excel.Workbook workbook)
        {
            try
            {
                var range = _application.Selection as Excel.Range;
                if (RangeBelongsToWorkbook(range, workbook))
                {
                    return range;
                }
            }
            catch
            {
            }

            var targetRange = ResolveTargetSelectionRange(workbook);
            if (targetRange != null)
            {
                return targetRange;
            }

            try
            {
                var activeCell = _application.ActiveCell as Excel.Range;
                return RangeBelongsToWorkbook(activeCell, workbook) ? activeCell : null;
            }
            catch
            {
                return null;
            }
        }

        private Excel.Range ResolveTargetSelectionRange(Excel.Workbook workbook)
        {
            if (_target == null || string.IsNullOrWhiteSpace(_target.Selection))
            {
                return null;
            }

            var reference = _target.Selection.Trim();
            var separator = reference.LastIndexOf('!');
            if (separator <= 0 || separator >= reference.Length - 1)
            {
                return null;
            }

            var sheetName = reference.Substring(0, separator).Trim('\'');
            var address = reference.Substring(separator + 1);
            try
            {
                var sheet = FindWorksheet(workbook, sheetName);
                return sheet == null ? null : sheet.Range[address];
            }
            catch
            {
                return null;
            }
        }

        private static Excel.Worksheet FirstWorksheet(Excel.Workbook workbook)
        {
            if (workbook == null)
            {
                return null;
            }

            try
            {
                foreach (Excel.Worksheet sheet in workbook.Worksheets)
                {
                    return sheet;
                }
            }
            catch
            {
            }

            return null;
        }

        private static Excel.Worksheet FindWorksheet(Excel.Workbook workbook, string name)
        {
            if (workbook == null || string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            try
            {
                foreach (Excel.Worksheet sheet in workbook.Worksheets)
                {
                    if (string.Equals(SafeString(delegate { return sheet.Name; }), name, StringComparison.OrdinalIgnoreCase))
                    {
                        return sheet;
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static void ValidateWorksheetName(Excel.Workbook workbook, string name, string currentName)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 31 || name.IndexOfAny(new[] { ':', '\\', '/', '?', '*', '[', ']' }) >= 0 ||
                name[0] == '\'' || name[name.Length - 1] == '\'')
            {
                throw new InvalidOperationException("Invalid Excel worksheet name: " + (name ?? string.Empty));
            }

            var existing = FindWorksheet(workbook, name);
            if (existing != null && !string.Equals(name, currentName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Worksheet already exists: " + name);
            }
        }

        private static bool RangeBelongsToWorkbook(Excel.Range range, Excel.Workbook workbook)
        {
            if (range == null || workbook == null)
            {
                return false;
            }

            try
            {
                var sheet = range.Worksheet as Excel.Worksheet;
                return sheet != null && SameWorkbook(sheet.Parent as Excel.Workbook, workbook);
            }
            catch
            {
                return false;
            }
        }

        private static bool SameWorkbook(Excel.Workbook left, Excel.Workbook right)
        {
            if (left == null || right == null)
            {
                return false;
            }

            return string.Equals(
                DocumentIdentity.RuntimeKey("Excel", left),
                DocumentIdentity.RuntimeKey("Excel", right),
                StringComparison.OrdinalIgnoreCase);
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
            return new ToolDefinition { Id = id, Host = "Excel", Name = id, Description = description, ArgumentSchemaJson = schema, BuiltIn = true, Enabled = true, MutatesDocument = mutatesDocument, AgentCanRun = agentCanRun, RiskLevel = riskLevel, RequiresConfirmation = requiresConfirmation, CanSourceHtmlData = canSourceHtmlData };
        }

        private static List<List<object>> RangeToRows(Excel.Range range)
        {
            var rows = new List<List<object>>();
            object value = range.Value2;
            var array = value as object[,];
            if (array == null)
            {
                rows.Add(new List<object> { value });
                return rows;
            }

            for (var r = array.GetLowerBound(0); r <= array.GetUpperBound(0); r++)
            {
                var row = new List<object>();
                for (var c = array.GetLowerBound(1); c <= array.GetUpperBound(1); c++)
                {
                    row.Add(array[r, c]);
                }
                rows.Add(row);
            }
            return rows;
        }

        private static List<List<object>> RangeToFormulaRows(Excel.Range range)
        {
            var rows = new List<List<object>>();
            object value = range.Formula;
            var array = value as object[,];
            if (array == null)
            {
                rows.Add(new List<object> { value });
                return rows;
            }

            for (var r = array.GetLowerBound(0); r <= array.GetUpperBound(0); r++)
            {
                var row = new List<object>();
                for (var c = array.GetLowerBound(1); c <= array.GetUpperBound(1); c++)
                {
                    row.Add(array[r, c]);
                }
                rows.Add(row);
            }
            return rows;
        }

        private static ToolResult ValidateReadableRange(Excel.Range range, string operation)
        {
            var cellCount = RangeCellCount(range);
            if (cellCount <= MaxReadableCellCount) return null;
            var address = range == null ? string.Empty : range.Address[false, false];
            return ToolResult.Fail(
                (operation ?? "Excel read") + " is too large: " + cellCount +
                " cells. Limit is " + MaxReadableCellCount + "; split the request into smaller ranges.",
                JsonConvert.SerializeObject(new
                {
                    address = address,
                    cellCount = cellCount,
                    maxCells = MaxReadableCellCount
                }),
                "excel_range_too_large",
                true);
        }

        private static long RangeCellCount(Excel.Range range)
        {
            if (range == null) return 0;
            long total = 0;
            foreach (Excel.Range area in range.Areas)
            {
                var count = (long)Convert.ToInt32(area.Rows.Count) * Convert.ToInt32(area.Columns.Count);
                if (long.MaxValue - total < count) return long.MaxValue;
                total += count;
            }
            return total;
        }

        private static Excel.Range ContextPreviewRange(Excel.Range range, int maxCells)
        {
            if (range == null || RangeCellCount(range) <= maxCells) return range;
            var totalRows = Math.Max(1, Convert.ToInt32(range.Rows.Count));
            var totalColumns = Math.Max(1, Convert.ToInt32(range.Columns.Count));
            var columns = Math.Min(totalColumns, Math.Max(1, maxCells));
            var rows = Math.Min(totalRows, Math.Max(1, maxCells / columns));
            var start = range.Cells[1, 1] as Excel.Range;
            return start == null ? range : start.Resize[rows, columns];
        }

        private static string HeaderAt(IReadOnlyList<List<object>> rows, int columnIndex)
        {
            return rows != null && rows.Count > 0 && columnIndex >= 0 && columnIndex < rows[0].Count
                ? Convert.ToString(rows[0][columnIndex])
                : string.Empty;
        }

        private static bool IsBlank(object value)
        {
            return value == null || string.IsNullOrWhiteSpace(Convert.ToString(value));
        }

        private static bool IsNumeric(object value)
        {
            if (value == null)
            {
                return false;
            }

            return value is byte || value is short || value is int || value is long ||
                value is float || value is double || value is decimal;
        }

        private Excel.ChartObject ResolveChartObject(string sheetName, string chartName, out Excel.Worksheet resolvedSheet)
        {
            if (string.IsNullOrWhiteSpace(chartName))
            {
                throw new InvalidOperationException("chartName is required.");
            }

            var chart = FindChartObject(sheetName, chartName, out resolvedSheet);
            if (chart != null) return chart;
            throw new InvalidOperationException("Chart not found: " + chartName);
        }

        private Excel.ChartObject FindChartObject(string sheetName, string chartName, out Excel.Worksheet resolvedSheet)
        {
            resolvedSheet = null;
            if (string.IsNullOrWhiteSpace(chartName)) return null;

            var workbook = RequireWorkbook();
            foreach (Excel.Worksheet sheet in workbook.Worksheets)
            {
                if (!string.IsNullOrWhiteSpace(sheetName) &&
                    !string.Equals(sheet.Name, sheetName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var chartObjects = (Excel.ChartObjects)sheet.ChartObjects(Type.Missing);
                var chartCount = chartObjects.Count;
                for (var i = 1; i <= chartCount; i++)
                {
                    var chartObject = (Excel.ChartObject)chartObjects.Item(i);
                    if (string.Equals(chartObject.Name, chartName, StringComparison.OrdinalIgnoreCase))
                    {
                        resolvedSheet = sheet;
                        return chartObject;
                    }
                }
            }

            return null;
        }

        private static object ChartDetails(Excel.Worksheet sheet, Excel.ChartObject chartObject)
        {
            var chart = chartObject.Chart;
            return new
            {
                sheet = sheet == null ? string.Empty : sheet.Name,
                name = chartObject.Name,
                title = ChartTitle(chart),
                chartType = chart.ChartType.ToString(),
                xAxisTitle = AxisTitle(chart, Excel.XlAxisType.xlCategory),
                yAxisTitle = AxisTitle(chart, Excel.XlAxisType.xlValue),
                series = ChartSeries(chart),
                left = chartObject.Left,
                top = chartObject.Top,
                width = chartObject.Width,
                height = chartObject.Height
            };
        }

        private static List<object> ChartSeries(Excel.Chart chart)
        {
            var result = new List<object>();
            try
            {
                var collection = (Excel.SeriesCollection)chart.SeriesCollection(Type.Missing);
                var seriesCount = collection.Count;
                for (var i = 1; i <= seriesCount; i++)
                {
                    try
                    {
                        var series = (Excel.Series)collection.Item(i);
                        result.Add(new { name = Convert.ToString(series.Name), formula = Convert.ToString(series.Formula) });
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
            return result;
        }

        private static string AxisTitle(Excel.Chart chart, Excel.XlAxisType axisType)
        {
            var axis = PrimaryAxis(chart, axisType);
            if (axis == null)
            {
                return string.Empty;
            }

            try
            {
                if (!axis.HasTitle)
                {
                    return string.Empty;
                }

                var title = axis.AxisTitle;
                return title == null ? string.Empty : Convert.ToString(title.Caption);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static Excel.Axis PrimaryAxis(Excel.Chart chart, Excel.XlAxisType axisType)
        {
            if (chart == null)
            {
                return null;
            }

            try
            {
                // Avoid asking Excel for an Axis COM proxy when this chart type has no such axis.
                if (!Convert.ToBoolean(chart.HasAxis[axisType, Excel.XlAxisGroup.xlPrimary]))
                {
                    return null;
                }

                return chart.Axes(axisType, Excel.XlAxisGroup.xlPrimary) as Excel.Axis;
            }
            catch
            {
                return null;
            }
        }

        private static void ApplyChartLabels(ToolCommand command, Excel.Worksheet sheet, Excel.Chart chart)
        {
            if (command.Arguments.ContainsKey("categoryLabelsRange"))
            {
                var labelsRange = ToolArgumentReader.String(command.Arguments, "categoryLabelsRange", string.Empty);
                if (!string.IsNullOrWhiteSpace(labelsRange))
                {
                    var labels = sheet.Range[labelsRange];
                    var collection = (Excel.SeriesCollection)chart.SeriesCollection(Type.Missing);
                    var seriesCount = collection.Count;
                    for (var i = 1; i <= seriesCount; i++)
                    {
                        ((Excel.Series)collection.Item(i)).XValues = labels;
                    }
                }
            }

            ApplyAxisTitle(command, chart, "xAxisTitle", Excel.XlAxisType.xlCategory);
            ApplyAxisTitle(command, chart, "yAxisTitle", Excel.XlAxisType.xlValue);
        }

        private static void ApplyAxisTitle(ToolCommand command, Excel.Chart chart, string argumentName, Excel.XlAxisType axisType)
        {
            if (!command.Arguments.ContainsKey(argumentName))
            {
                return;
            }

            var title = ToolArgumentReader.String(command.Arguments, argumentName, string.Empty);
            var axis = PrimaryAxis(chart, axisType);
            if (axis == null)
            {
                if (string.IsNullOrWhiteSpace(title))
                {
                    return;
                }
                throw new InvalidOperationException("Chart does not support the requested axis title: " + argumentName);
            }

            var hasTitle = !string.IsNullOrWhiteSpace(title);
            axis.HasTitle = hasTitle;
            if (hasTitle)
            {
                var axisTitle = axis.AxisTitle;
                if (axisTitle == null)
                {
                    throw new InvalidOperationException("Excel did not create the requested axis title: " + argumentName);
                }
                axisTitle.Caption = title;
            }
        }

        private static string ChartTitle(Excel.Chart chart)
        {
            try
            {
                if (chart == null || !chart.HasTitle)
                {
                    return string.Empty;
                }

                var title = chart.ChartTitle;
                return title == null ? string.Empty : Convert.ToString(title.Caption);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static object ToCellValue(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            {
                return token.Value<double>();
            }
            if (token.Type == JTokenType.Boolean)
            {
                return token.Value<bool>();
            }
            return token.Type == JTokenType.String ? token.Value<string>() : token.ToString(Formatting.None);
        }

        private static Excel.XlChartType ResolveChartType(string value)
        {
            switch ((value ?? string.Empty).ToLowerInvariant())
            {
                case "column":
                case "col":
                    return Excel.XlChartType.xlColumnClustered;
                case "bar":
                    return Excel.XlChartType.xlBarClustered;
                case "pie":
                    return Excel.XlChartType.xlPie;
                case "line":
                default:
                    return Excel.XlChartType.xlLineMarkers;
            }
        }

        private static Excel.XlHAlign ResolveHorizontalAlignment(string value)
        {
            switch ((value ?? string.Empty).ToLowerInvariant())
            {
                case "left":
                    return Excel.XlHAlign.xlHAlignLeft;
                case "right":
                    return Excel.XlHAlign.xlHAlignRight;
                case "center":
                case "centre":
                    return Excel.XlHAlign.xlHAlignCenter;
                default:
                    return Excel.XlHAlign.xlHAlignGeneral;
            }
        }

        private static int OleColor(string value)
        {
            var text = (value ?? string.Empty).Trim().TrimStart('#');
            int rgb;
            if (text.Length != 6 || !int.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out rgb))
            {
                return 0;
            }

            var r = (rgb >> 16) & 0xFF;
            var g = (rgb >> 8) & 0xFF;
            var b = rgb & 0xFF;
            return r + (g << 8) + (b << 16);
        }

        private static void AppendRangeValues(StringBuilder builder, Excel.Range range, int maxChars)
        {
            var previewCells = Math.Max(1, Math.Min(MaxContextPreviewCells, Math.Max(1, maxChars / 2)));
            var preview = ContextPreviewRange(range, previewCells);
            foreach (var row in RangeToRows(preview))
            {
                builder.AppendLine(string.Join("\t", row));
                if (builder.Length >= maxChars)
                {
                    return;
                }
            }
        }

        private static string BuildRangeText(Excel.Range range, int maxChars)
        {
            var builder = new StringBuilder();
            AppendRangeValues(builder, range, maxChars);
            return Trim(builder.ToString(), maxChars);
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
