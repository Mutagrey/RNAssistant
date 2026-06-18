using System;
using System.Collections.Generic;
using System.Text;
using Excel = Microsoft.Office.Interop.Excel;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office;
using RNAssistant.Office.Skills;

namespace RNAssistant.ExcelAddIn
{
    public sealed class ExcelAdapter : IOfficeApplicationAdapter
    {
        private readonly Excel.Application _application;

        public ExcelAdapter(Excel.Application application)
        {
            _application = application;
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

        public IEnumerable<SkillDefinition> GetBuiltInSkills()
        {
            return new[]
            {
                Skill("excel.workbook_summary", "Return workbook metadata, sheets and used ranges.", "{}"),
                Skill("excel.list_sheets", "List workbook sheet names.", "{}"),
                Skill("excel.read_range", "Read a worksheet range.", "{\"sheet\":\"optional\",\"address\":\"A1:D20\"}"),
                Skill("excel.write_range", "Write a scalar value to a worksheet range.", "{\"sheet\":\"optional\",\"address\":\"A1\",\"value\":\"text\"}"),
                Skill("excel.write_table", "Write a 2D JSON array to a worksheet starting at a cell.", "{\"sheet\":\"optional\",\"startAddress\":\"A1\",\"values\":[[\"Header\", \"Value\"],[\"A\", 1]]}"),
                Skill("excel.add_chart", "Create a chart from a worksheet source range.", "{\"sheet\":\"optional\",\"sourceRange\":\"A1:B6\",\"chartType\":\"line|column|bar|pie\",\"title\":\"Chart title\",\"left\":300,\"top\":20,\"width\":480,\"height\":300}"),
                Skill("excel.add_sheet", "Add a new worksheet.", "{\"name\":\"Sheet name\"}"),
                Skill("excel.vba_read_project", "Read VBA project modules and source code when Trust Access to VBA project is enabled.", "{\"maxChars\":30000}"),
                Skill("excel.vba_read_module", "Read one VBA module by name.", "{\"moduleName\":\"Module1\",\"maxChars\":30000}"),
                Skill("excel.vba_replace_module", "Replace a VBA module source code; RNAssistant stores rollback backups before replacement.", "{\"moduleName\":\"Module1\",\"code\":\"Sub Test()\\nEnd Sub\",\"createIfMissing\":true}"),
                Skill("excel.insert_vba_module", "Insert VBA module when Trust Access to VBA project is enabled; otherwise returns copyable code.", "{\"moduleName\":\"Module1\",\"code\":\"Sub Test()\\nEnd Sub\"}"),
                Skill("excel.run_macro", "Run an Excel VBA macro by name.", "{\"macroName\":\"Module1.Test\"}")
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
            var range = _application.Selection as Excel.Range;
            if (range == null)
            {
                range = _application.ActiveCell;
            }

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

        public SkillResult ExecuteSkill(SkillCommand command)
        {
            try
            {
                switch (command.SkillId)
                {
                    case "excel.workbook_summary":
                        return WorkbookSummary();
                    case "excel.list_sheets":
                        return ListSheets();
                    case "excel.read_range":
                        return ReadRange(command);
                    case "excel.write_range":
                        return WriteRange(command);
                    case "excel.write_table":
                        return WriteTable(command);
                    case "excel.add_chart":
                        return AddChart(command);
                    case "excel.add_sheet":
                        return AddSheet(command);
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
                        return SkillResult.Fail("Unsupported Excel skill: " + command.SkillId);
                }
            }
            catch (Exception ex)
            {
                return SkillResult.Fail(ex.Message);
            }
        }

        private SkillResult WorkbookSummary()
        {
            var workbook = RequireWorkbook();
            var sheets = new List<object>();
            foreach (Excel.Worksheet sheet in workbook.Worksheets)
            {
                sheets.Add(new { name = sheet.Name, usedRange = sheet.UsedRange.Address[false, false] });
            }

            return SkillResult.Ok("Workbook summary collected.", JsonConvert.SerializeObject(new
            {
                name = workbook.Name,
                fullName = workbook.FullName,
                sheets = sheets
            }));
        }

        private SkillResult ListSheets()
        {
            var workbook = RequireWorkbook();
            var names = new List<string>();
            foreach (Excel.Worksheet sheet in workbook.Worksheets)
            {
                names.Add(sheet.Name);
            }

            return SkillResult.Ok("Sheets listed.", JsonConvert.SerializeObject(names));
        }

        private SkillResult ReadRange(SkillCommand command)
        {
            var sheet = ResolveSheet(SkillArgumentReader.String(command.Arguments, "sheet", null));
            var address = SkillArgumentReader.String(command.Arguments, "address", "A1");
            var range = sheet.Range[address];
            var rows = RangeToRows(range);
            return SkillResult.Ok("Range read: " + sheet.Name + "!" + address, JsonConvert.SerializeObject(rows));
        }

        private SkillResult WriteRange(SkillCommand command)
        {
            var sheet = ResolveSheet(SkillArgumentReader.String(command.Arguments, "sheet", null));
            var address = SkillArgumentReader.String(command.Arguments, "address", "A1");
            var value = SkillArgumentReader.String(command.Arguments, "value", string.Empty);
            sheet.Range[address].Value2 = value;
            return SkillResult.Ok("Wrote value to " + sheet.Name + "!" + address);
        }

        private SkillResult WriteTable(SkillCommand command)
        {
            var sheet = ResolveSheet(SkillArgumentReader.String(command.Arguments, "sheet", null));
            var startAddress = SkillArgumentReader.String(command.Arguments, "startAddress", "A1");
            var valuesJson = SkillArgumentReader.String(command.Arguments, "values", "[]");
            var values = JArray.Parse(valuesJson);
            if (values.Count == 0)
            {
                return SkillResult.Fail("No table values provided.");
            }

            var rows = values.Count;
            var columns = 0;
            foreach (var rowToken in values)
            {
                var row = rowToken as JArray;
                if (row == null)
                {
                    return SkillResult.Fail("Table values must be a 2D JSON array.");
                }
                columns = Math.Max(columns, row.Count);
            }
            if (columns == 0)
            {
                return SkillResult.Fail("No table columns provided.");
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
            return SkillResult.Ok("Wrote table to " + sheet.Name + "!" + target.Address[false, false], JsonConvert.SerializeObject(new { sheet = sheet.Name, range = target.Address[false, false], rows = rows, columns = columns }));
        }

        private SkillResult AddChart(SkillCommand command)
        {
            var sheet = ResolveSheet(SkillArgumentReader.String(command.Arguments, "sheet", null));
            var sourceRange = SkillArgumentReader.String(command.Arguments, "sourceRange", "A1:B6");
            var title = SkillArgumentReader.String(command.Arguments, "title", "Chart");
            var chartType = SkillArgumentReader.String(command.Arguments, "chartType", "line");
            var left = SkillArgumentReader.Int32(command.Arguments, "left", 300);
            var top = SkillArgumentReader.Int32(command.Arguments, "top", 20);
            var width = SkillArgumentReader.Int32(command.Arguments, "width", 480);
            var height = SkillArgumentReader.Int32(command.Arguments, "height", 300);

            var source = sheet.Range[sourceRange];
            var chartObjects = (Excel.ChartObjects)sheet.ChartObjects(Type.Missing);
            var chartObject = chartObjects.Add(left, top, width, height);
            var chart = chartObject.Chart;
            chart.SetSourceData(source);
            chart.ChartType = ResolveChartType(chartType);
            chart.HasTitle = true;
            chart.ChartTitle.Text = title;
            return SkillResult.Ok("Chart added: " + title, JsonConvert.SerializeObject(new { sheet = sheet.Name, sourceRange = sourceRange, chartType = chartType, title = title }));
        }

        private SkillResult AddSheet(SkillCommand command)
        {
            var workbook = RequireWorkbook();
            var name = SkillArgumentReader.String(command.Arguments, "name", "AI Sheet");
            var sheet = (Excel.Worksheet)workbook.Worksheets.Add();
            sheet.Name = name;
            return SkillResult.Ok("Added sheet: " + name);
        }

        private SkillResult ReadVbaProject(SkillCommand command)
        {
            var workbook = RequireWorkbook();
            var maxChars = SkillArgumentReader.Int32(command.Arguments, "maxChars", 30000);
            return VbaProjectSupport.ReadProject(workbook, workbook.Name, maxChars);
        }

        private SkillResult ReadVbaModule(SkillCommand command)
        {
            var workbook = RequireWorkbook();
            var moduleName = SkillArgumentReader.String(command.Arguments, "moduleName", string.Empty);
            var maxChars = SkillArgumentReader.Int32(command.Arguments, "maxChars", 30000);
            return VbaProjectSupport.ReadModule(workbook, moduleName, maxChars);
        }

        private SkillResult ReplaceVbaModule(SkillCommand command)
        {
            var workbook = RequireWorkbook();
            var moduleName = SkillArgumentReader.String(command.Arguments, "moduleName", string.Empty);
            var code = SkillArgumentReader.String(command.Arguments, "code", string.Empty);
            var createIfMissing = SkillArgumentReader.Boolean(command.Arguments, "createIfMissing", true);
            return VbaProjectSupport.ReplaceModule(workbook, moduleName, code, createIfMissing);
        }

        private SkillResult InsertVbaModule(SkillCommand command)
        {
            var workbook = RequireWorkbook();
            var moduleName = SkillArgumentReader.String(command.Arguments, "moduleName", "RNAssistantModule");
            var code = SkillArgumentReader.String(command.Arguments, "code", string.Empty);
            if (string.IsNullOrWhiteSpace(code))
            {
                return SkillResult.Fail("No VBA code provided.");
            }

            try
            {
                return VbaProjectSupport.InsertModule(workbook, moduleName, code);
            }
            catch (Exception ex)
            {
                return SkillResult.Ok("VBA insert was blocked. Enable 'Trust access to the VBA project object model' or copy the code manually. " + ex.Message, JsonConvert.SerializeObject(new { moduleName = moduleName, code = code }));
            }
        }

        private SkillResult RunMacro(SkillCommand command)
        {
            var macroName = SkillArgumentReader.String(command.Arguments, "macroName", string.Empty);
            if (string.IsNullOrWhiteSpace(macroName))
            {
                return SkillResult.Fail("No macroName provided.");
            }

            _application.Run(macroName);
            return SkillResult.Ok("Macro ran: " + macroName);
        }

        private Excel.Workbook ActiveWorkbook()
        {
            try { return _application.ActiveWorkbook; }
            catch { return null; }
        }

        private Excel.Workbook RequireWorkbook()
        {
            var workbook = ActiveWorkbook();
            if (workbook == null)
            {
                throw new InvalidOperationException("No active workbook.");
            }

            return workbook;
        }

        private Excel.Worksheet ResolveSheet(string name)
        {
            var workbook = RequireWorkbook();
            if (string.IsNullOrWhiteSpace(name))
            {
                return (Excel.Worksheet)_application.ActiveSheet;
            }

            return (Excel.Worksheet)workbook.Worksheets[name];
        }

        private static SkillDefinition Skill(string id, string description, string schema)
        {
            return new SkillDefinition { Id = id, Host = "Excel", Name = id, Description = description, ArgumentSchemaJson = schema, BuiltIn = true, Enabled = true };
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
