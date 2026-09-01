using System;
using System.Collections.Generic;
using System.Text;
using Excel = Microsoft.Office.Interop.Excel;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.Office.Domains.Vba;
using RNAssistant.Office.Tools;
using RNAssistant.OfficeHosts.Identity;

namespace RNAssistant.OfficeHosts
{
    public sealed partial class ExcelAdapter : IOfficeApplicationAdapter, IOfficeContextProvider, IOfficeBuiltInSkillProvider, IOfficeDocumentCatalog, IOfficeDocumentSessionProvider, IOfficeDispatcherProvider, IExcelBackendProvider, IVbaHostBackendProvider
    {
        private const int MaxContextPreviewCells = 2000;

        private readonly Excel.Application _application;
        private readonly Excel.Workbook _targetWorkbook;
        private readonly ExcelDocumentSession _documentSession;
        private readonly ExcelInteropBackend _excelBackend;
        private readonly ExcelFindReplaceInteropBackend _excelFindReplaceBackend;
        private readonly ExcelSheetInteropBackend _excelSheetBackend;
        private readonly ExcelRangeMutationInteropBackend _excelRangeMutationBackend;
        private readonly ExcelTableInteropBackend _excelTableBackend;
        private readonly ExcelChartInteropBackend _excelChartBackend;
        private readonly VbaInteropBackend _vbaHostBackend;
        private readonly string _qualificationOwnerLabel;

        public ExcelAdapter(
            Excel.Application application,
            Excel.Workbook targetWorkbook,
            IOfficeStaDispatcher dispatcher,
            string qualificationOwnerLabel = null)
        {
            _application = application ?? throw new ArgumentNullException(nameof(application));
            _targetWorkbook = targetWorkbook ?? throw new ArgumentNullException(nameof(targetWorkbook));
            _qualificationOwnerLabel = string.IsNullOrWhiteSpace(qualificationOwnerLabel)
                ? "host-owner" : qualificationOwnerLabel;
            var runtimeDocumentId = DocumentIdentity.RuntimeKey(HostName, _targetWorkbook);
            _documentSession = new ExcelDocumentSession(
                _targetWorkbook,
                runtimeDocumentId,
                dispatcher);
            _excelBackend = new ExcelInteropBackend(_documentSession);
            _excelFindReplaceBackend = new ExcelFindReplaceInteropBackend(_documentSession);
            _excelSheetBackend = new ExcelSheetInteropBackend(_documentSession);
            _excelRangeMutationBackend =
                new ExcelRangeMutationInteropBackend(_documentSession);
            _excelTableBackend = new ExcelTableInteropBackend(_documentSession);
            _excelChartBackend = new ExcelChartInteropBackend(_documentSession);
            _vbaHostBackend = new VbaInteropBackend(
                _documentSession, _application);
        }

        public string HostName { get { return "Excel"; } }
        public IOfficeDocumentSession DocumentSession { get { return _documentSession; } }
        public IOfficeStaDispatcher StaDispatcher { get { return _documentSession.StaDispatcher; } }
        public IExcelReadBackend ExcelReadBackend { get { return _excelBackend; } }
        public IExcelWriteBackend ExcelWriteBackend { get { return _excelBackend; } }
        public IExcelFindReplaceBackend ExcelFindReplaceBackend
        {
            get { return _excelFindReplaceBackend; }
        }
        public IExcelSheetBackend ExcelSheetBackend { get { return _excelSheetBackend; } }
        public IExcelRangeMutationBackend ExcelRangeMutationBackend
        {
            get { return _excelRangeMutationBackend; }
        }
        public IExcelTableBackend ExcelTableBackend { get { return _excelTableBackend; } }
        public IExcelChartBackend ExcelChartBackend { get { return _excelChartBackend; } }
        public IVbaHostBackend VbaHostBackend { get { return _vbaHostBackend; } }

        public string DocumentKey { get { return _documentSession.StableDocumentId; } }
        public string RuntimeDocumentKey { get { return _documentSession.RuntimeDocumentId; } }
        public string DocumentTitle { get { return RequireWorkbook().Name; } }

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

            var workbook = RequireWorkbook();
            context.DocumentPath = PersistentPath(workbook);
            context.DocumentTitle = SafeString(delegate { return workbook.Name; });

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
            var result = new List<OpenOfficeDocumentDto>();
            foreach (Excel.Workbook workbook in _application.Workbooks)
            {
                result.Add(new OpenOfficeDocumentDto
                {
                    Host = HostName,
                    DocumentKey = KeyForWorkbook(workbook),
                    Title = SafeString(delegate { return workbook.Name; }),
                    Path = PersistentPath(workbook),
                    IsActive = SameWorkbook(_targetWorkbook, workbook)
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
            return ExcelDocumentSession.StableKey(
                workbook,
                workbook == null ? "Excel:Runtime:none" : DocumentIdentity.RuntimeKey(HostName, workbook));
        }

        private static string PersistentPath(Excel.Workbook workbook)
        {
            if (workbook == null || string.IsNullOrWhiteSpace(SafeString(delegate { return workbook.Path; })))
            {
                return string.Empty;
            }

            return SafeString(delegate { return workbook.FullName; });
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
            var workbook = RequireWorkbook();

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
                RequireWorkbook().Activate();
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

        private Excel.Workbook RequireWorkbook()
        {
            if (!_documentSession.IsAlive)
            {
                throw new InvalidOperationException("Target Excel workbook is not open.");
            }

            return _targetWorkbook;
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
