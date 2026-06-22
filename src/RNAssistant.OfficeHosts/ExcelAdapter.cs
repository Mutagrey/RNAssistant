using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Excel = Microsoft.Office.Interop.Excel;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Office;
using RNAssistant.Office.Tools;

namespace RNAssistant.OfficeHosts
{
    public sealed class ExcelAdapter : IOfficeApplicationAdapter, IOfficeContextProvider
    {
        private readonly Excel.Application _application;
        private readonly OfficeTargetDescriptor _target;

        public ExcelAdapter(Excel.Application application)
            : this(application, null)
        {
        }

        public ExcelAdapter(Excel.Application application, OfficeTargetDescriptor target)
        {
            _application = application;
            _target = target;
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
                return workbook == null ? "Excel:NoWorkbook" : "Excel:Runtime:" + workbook.GetHashCode().ToString("x");
            }
        }

        public string LegacyDocumentKey
        {
            get
            {
                var workbook = ActiveWorkbook();
                if (workbook == null)
                {
                    return "Excel:NoWorkbook";
                }

                return string.IsNullOrWhiteSpace(workbook.FullName) ? RuntimeDocumentKey : workbook.FullName;
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

        public IEnumerable<ToolDefinition> GetBuiltInTools()
        {
            return new[]
            {
                Skill("excel.get_context", "Read-only: Return active workbook, sheet, and selection context.", "{}"),
                Skill("excel.get_selection", "Read-only: Read the current or launcher-captured selection values.", "{}"),
                Skill("excel.workbook_summary", "Read-only: Return workbook metadata, sheets, and used ranges.", "{}"),
                Skill("excel.list_sheets", "Read-only: List workbook sheet names.", "{}"),
                Skill("excel.read_range", "Read-only: Read worksheet values from an A1 range.", "{\"sheet\":\"\",\"address\":\"A1:D20\"}"),
                Skill("excel.read_formula_range", "Read-only: Read formulas from an A1 range.", "{\"sheet\":\"\",\"address\":\"A1:D20\"}"),
                Skill("excel.profile_range", "Read-only: Profile a range or selection for dimensions, blanks, formulas, headers, and numeric columns.", "{\"sheet\":\"\",\"address\":\"\"}"),
                Skill("excel.find_cells", "Read-only: Find cells whose value or formula contains query text.", "{\"sheet\":\"\",\"query\":\"text\",\"lookIn\":\"values\",\"maxResults\":50}"),
                Skill("excel.create_chat_chart", "Read-only: Create an interactive chart artifact in chat from a selection or range.", "{\"sheet\":\"\",\"address\":\"\",\"chartType\":\"auto\",\"title\":\"Chart title\"}"),
                Skill("excel.list_charts", "Read-only: List chart objects in the workbook or one sheet.", "{\"sheet\":\"\"}"),
                Skill("excel.list_tables", "Read-only: List Excel tables in the workbook or one sheet.", "{\"sheet\":\"\"}"),
                Skill("excel.list_names", "Read-only: List workbook defined names.", "{}"),
                Skill("excel.list_shapes", "Read-only: List shapes in the workbook or one sheet.", "{\"sheet\":\"\"}"),
                Skill("excel.write_range", "Mutates document: Write one scalar value to a worksheet range.", "{\"sheet\":\"\",\"address\":\"A1\",\"value\":\"text\"}", true, true),
                Skill("excel.write_table", "Mutates document: Write a 2D JSON array to a worksheet starting at a cell.", "{\"sheet\":\"\",\"startAddress\":\"A1\",\"values\":[[\"Header\",\"Value\"],[\"A\",1]]}", true, true),
                Skill("excel.set_formula", "Mutates document: Write one formula to a worksheet range.", "{\"sheet\":\"\",\"address\":\"B2\",\"formula\":\"=SUM(A1:A10)\"}", true, true),
                Skill("excel.add_table", "Mutates document: Convert a source range into an Excel table.", "{\"sheet\":\"\",\"sourceRange\":\"A1:B6\",\"name\":\"Table1\",\"hasHeaders\":true,\"style\":\"TableStyleMedium2\"}", true, true),
                Skill("excel.add_chart", "Mutates document: Create a chart from a worksheet source range.", "{\"sheet\":\"\",\"sourceRange\":\"A1:B6\",\"chartType\":\"line\",\"title\":\"Chart title\",\"left\":300,\"top\":20,\"width\":480,\"height\":300}", true, true),
                Skill("excel.format_range", "Mutates document: Apply basic number, font, fill, and alignment formatting to a range.", "{\"sheet\":\"\",\"address\":\"A1:D20\",\"numberFormat\":\"\",\"bold\":true,\"italic\":false,\"fillColor\":\"#FFFF00\",\"fontColor\":\"#000000\",\"horizontalAlignment\":\"center\"}", true, true),
                Skill("excel.autofit", "Mutates document: Autofit rows and columns for a range or used range.", "{\"sheet\":\"\",\"address\":\"\"}", true, true),
                Skill("excel.add_sheet", "Mutates document: Add a new worksheet.", "{\"name\":\"Sheet name\"}", true, true),
                Skill("excel.rename_sheet", "Mutates document: Rename a worksheet.", "{\"sheet\":\"Old name\",\"newName\":\"New name\"}", true, false),
                Skill("excel.clear_range", "Mutates document: Clear cell values, formats, or both in a range.", "{\"sheet\":\"\",\"address\":\"A1:D20\",\"clearWhat\":\"values\"}", true, false),
                Skill("excel.sort_range", "Mutates document: Sort rows in a range by one key column.", "{\"sheet\":\"\",\"address\":\"A1:D20\",\"keyColumn\":1,\"descending\":false,\"hasHeaders\":true}", true, false),
                Skill("excel.filter_range", "Mutates document: Apply AutoFilter criteria to a range.", "{\"sheet\":\"\",\"address\":\"A1:D20\",\"field\":1,\"criteria\":\"North\"}", true, false),
                Skill("excel.vba_read_project", "Read-only: Read VBA project modules and source code when Trust Access to VBA project is enabled.", "{\"maxChars\":30000}"),
                Skill("excel.vba_read_module", "Read-only: Read one VBA module by name.", "{\"moduleName\":\"Module1\",\"maxChars\":30000}"),
                Skill("excel.vba_replace_module", "Mutates document: Replace a VBA module source code and create a rollback backup.", "{\"moduleName\":\"Module1\",\"code\":\"Sub Test()\\nEnd Sub\",\"createIfMissing\":true}", true, false),
                Skill("excel.insert_vba_module", "Mutates document: Insert a VBA module or return copyable code if trust access is blocked.", "{\"moduleName\":\"Module1\",\"code\":\"Sub Test()\\nEnd Sub\"}", true, false),
                Skill("excel.run_macro", "Mutates document: Run an Excel VBA macro by name.", "{\"macroName\":\"Module1.Test\"}", true, false)
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

        public string GetVbaSnapshot(int maxChars)
        {
            var workbook = ActiveWorkbook();
            if (workbook == null)
            {
                return "No active workbook.";
            }

            return VbaProjectSupport.GetSnapshot(workbook, workbook.Name, maxChars);
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
                    case "excel.workbook_summary":
                        return WorkbookSummary();
                    case "excel.list_sheets":
                        return ListSheets();
                    case "excel.read_range":
                        return ReadRange(command);
                    case "excel.read_formula_range":
                        return ReadFormulaRange(command);
                    case "excel.profile_range":
                        return ProfileRange(command);
                    case "excel.find_cells":
                        return FindCells(command);
                    case "excel.create_chat_chart":
                        return CreateChatChart(command);
                    case "excel.list_charts":
                        return ListCharts(command);
                    case "excel.list_tables":
                        return ListTables(command);
                    case "excel.list_names":
                        return ListNames();
                    case "excel.list_shapes":
                        return ListShapes(command);
                    case "excel.write_range":
                        return WriteRange(command);
                    case "excel.write_table":
                        return WriteTable(command);
                    case "excel.set_formula":
                        return SetFormula(command);
                    case "excel.add_table":
                        return AddTable(command);
                    case "excel.add_chart":
                        return AddChart(command);
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
                    case "excel.vba_read_project":
                        return ReadVbaProject(command);
                    case "excel.vba_read_module":
                        return ReadVbaModule(command);
                    case "excel.vba_replace_module":
                        return ReplaceVbaModule(command);
                    case "excel.insert_vba_module":
                        return InsertVbaModule(command);
                    case "excel.run_macro":
                        return RunMacro(command);
                    default:
                        return ToolResult.Fail("Unsupported Excel tool: " + command.ToolId);
                }
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ex.Message);
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

        private ToolResult ReadRange(ToolCommand command)
        {
            var sheet = ResolveSheet(ToolArgumentReader.String(command.Arguments, "sheet", null));
            var address = ToolArgumentReader.String(command.Arguments, "address", "A1");
            var range = sheet.Range[address];
            var rows = RangeToRows(range);
            return ToolResult.Ok("Range read: " + sheet.Name + "!" + address, JsonConvert.SerializeObject(rows));
        }

        private ToolResult ReadFormulaRange(ToolCommand command)
        {
            var sheet = ResolveSheet(ToolArgumentReader.String(command.Arguments, "sheet", null));
            var address = ToolArgumentReader.String(command.Arguments, "address", "A1");
            var range = sheet.Range[address];
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
            var query = ToolArgumentReader.String(command.Arguments, "query", string.Empty);
            var lookIn = ToolArgumentReader.String(command.Arguments, "lookIn", "values");
            var maxResults = Math.Max(1, Math.Min(500, ToolArgumentReader.Int32(command.Arguments, "maxResults", 50)));
            if (string.IsNullOrWhiteSpace(query))
            {
                return ToolResult.Fail("query is required.");
            }

            var workbook = RequireWorkbook();
            var matches = new List<object>();
            foreach (Excel.Worksheet sheet in workbook.Worksheets)
            {
                if (!string.IsNullOrWhiteSpace(sheetFilter) &&
                    !string.Equals(sheet.Name, sheetFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (Excel.Range cell in sheet.UsedRange.Cells)
                {
                    var value = string.Equals(lookIn, "formulas", StringComparison.OrdinalIgnoreCase)
                        ? Convert.ToString(cell.Formula)
                        : Convert.ToString(cell.Value2);
                    if (value != null && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        matches.Add(new
                        {
                            sheet = sheet.Name,
                            address = cell.Address[false, false],
                            value = Convert.ToString(cell.Value2),
                            formula = Convert.ToString(cell.Formula)
                        });
                        if (matches.Count >= maxResults)
                        {
                            return ToolResult.Ok("Cells found: " + matches.Count, JsonConvert.SerializeObject(matches));
                        }
                    }
                }
            }

            return ToolResult.Ok("Cells found: " + matches.Count, JsonConvert.SerializeObject(matches));
        }

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
                for (var i = 1; i <= chartObjects.Count; i++)
                {
                    var chartObject = (Excel.ChartObject)chartObjects.Item(i);
                    var chart = chartObject.Chart;
                    charts.Add(new
                    {
                        sheet = sheet.Name,
                        name = chartObject.Name,
                        title = ChartTitle(chart),
                        chartType = chart.ChartType.ToString(),
                        left = chartObject.Left,
                        top = chartObject.Top,
                        width = chartObject.Width,
                        height = chartObject.Height
                    });
                }
            }

            return ToolResult.Ok("Charts listed: " + charts.Count, JsonConvert.SerializeObject(charts));
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
            var value = ToolArgumentReader.String(command.Arguments, "value", string.Empty);
            sheet.Range[address].Value2 = value;
            return ToolResult.Ok("Wrote value to " + sheet.Name + "!" + address);
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
            chart.ChartTitle.Text = title;
            return ToolResult.Ok("Chart added: " + title, JsonConvert.SerializeObject(new { sheet = sheet.Name, sourceRange = sourceRange, chartType = chartType, title = title }));
        }

        private ToolResult FormatRange(ToolCommand command)
        {
            var sheet = ResolveSheet(ToolArgumentReader.String(command.Arguments, "sheet", null));
            var address = ToolArgumentReader.String(command.Arguments, "address", "A1");
            var range = sheet.Range[address];
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

            return ToolResult.Ok("Range formatted: " + sheet.Name + "!" + address);
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
            var sheet = (Excel.Worksheet)workbook.Worksheets.Add();
            sheet.Name = name;
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

        private ToolResult ReadVbaProject(ToolCommand command)
        {
            var workbook = RequireWorkbook();
            var maxChars = ToolArgumentReader.Int32(command.Arguments, "maxChars", 30000);
            return VbaProjectSupport.ReadProject(workbook, workbook.Name, maxChars);
        }

        private ToolResult ReadVbaModule(ToolCommand command)
        {
            var workbook = RequireWorkbook();
            var moduleName = ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty);
            var maxChars = ToolArgumentReader.Int32(command.Arguments, "maxChars", 30000);
            return VbaProjectSupport.ReadModule(workbook, moduleName, maxChars);
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

            _application.Run(macroName);
            return ToolResult.Ok("Macro ran: " + macroName);
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
            return _target != null && _target.HasDocumentIdentity;
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
                throw new InvalidOperationException(_target == null || !_target.HasDocumentIdentity
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

                workbook.Activate();
                var activatedSheet = _application.ActiveSheet as Excel.Worksheet;
                if (activatedSheet != null && SameWorkbook(activatedSheet.Parent as Excel.Workbook, workbook))
                {
                    return activatedSheet;
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

            return SamePath(SafeString(delegate { return left.FullName; }), SafeString(delegate { return right.FullName; }))
                || string.Equals(SafeString(delegate { return left.Name; }), SafeString(delegate { return right.Name; }), StringComparison.OrdinalIgnoreCase);
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
            return new ToolDefinition { Id = id, Host = "Excel", Name = id, Description = description, ArgumentSchemaJson = schema, BuiltIn = true, Enabled = true, MutatesDocument = mutatesDocument, AgentCanRun = agentCanRun };
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

        private static string ChartTitle(Excel.Chart chart)
        {
            try
            {
                return chart != null && chart.HasTitle ? chart.ChartTitle.Text : string.Empty;
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
            foreach (var row in RangeToRows(range))
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
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            {
                return text;
            }

            return text.Substring(0, maxChars) + "\n...[truncated]";
        }
    }
}
