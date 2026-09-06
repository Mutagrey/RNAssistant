using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.Office.Domains.PowerPoint;
using RNAssistant.Office.Domains.Outlook;
using RNAssistant.Office.Domains.Word;
using RNAssistant.Office.Domains.Vba;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal sealed partial class FakeOfficeAdapter : IOfficeApplicationAdapter,
        IOfficeContextProvider, IOfficeBuiltInSkillProvider,
        IOfficeDocumentCatalog, IOfficeDocumentSessionProvider,
        IOfficeDispatcherProvider, IExcelBackendProvider, IExcelReadBackend,
        IExcelWriteBackend, IExcelFindReplaceBackend, IExcelSheetBackend,
        IExcelRangeMutationBackend, IExcelTableBackend, IExcelChartBackend,
        IWordBackendProvider, IWordBackend, IPowerPointBackendProvider,
        IPowerPointBackend, IOutlookBackendProvider, IOutlookBackend,
        IVbaHostBackendProvider, IVbaHostBackend
    {
        internal const string ExcelInspectOperation = "inspect";
        internal const string ExcelRangeReadOperation = "range.read";
        internal const string ExcelWriteReadOperation = "write.read";
        internal const string ExcelWriteApplyOperation = "write.apply";
        internal const string ExcelFindScopeReadOperation = "find_replace.read";
        internal const string ExcelReplaceApplyOperation = "find_replace.apply";
        internal const string ExcelSheetReadOperation = "sheet.read";
        internal const string ExcelSheetAddOperation = "sheet.add";
        internal const string ExcelSheetRenameOperation = "sheet.rename";
        internal const string ExcelRangeMutationReadOperation = "range_mutation.read";
        internal const string ExcelRangeMutationApplyOperation = "range_mutation.apply";
        internal const string ExcelTableReadOperation = "table.read";
        internal const string ExcelTableAddOperation = "table.add";
        internal const string ExcelChartSourceReadOperation = "chart.read_source";
        internal const string ExcelChartReadOperation = "chart.read";
        internal const string ExcelChartApplyOperation = "chart.apply";

        public readonly List<string> ExcelBackendCalls = new List<string>();
        public readonly List<string> WordBackendCalls = new List<string>();
        public readonly List<string> PowerPointBackendCalls = new List<string>();
        public readonly List<string> OutlookBackendCalls = new List<string>();
        public int TotalBackendCallCount
        {
            get
            {
                return ExcelBackendCalls.Count + WordBackendCalls.Count +
                    PowerPointBackendCalls.Count + OutlookBackendCalls.Count +
                    VbaBackendCalls.Count;
            }
        }

        public void ClearBackendCalls()
        {
            ExcelBackendCalls.Clear();
            WordBackendCalls.Clear();
            PowerPointBackendCalls.Clear();
            OutlookBackendCalls.Clear();
            VbaBackendCalls.Clear();
        }
        public readonly List<ToolInvocation> ExcelSheetRequests = new List<ToolInvocation>();
        public readonly List<ToolInvocation> ExcelRangeMutationRequests =
            new List<ToolInvocation>();
        public readonly List<ToolInvocation> ExcelTableRequests =
            new List<ToolInvocation>();
        public readonly List<ToolInvocation> ExcelChartRequests =
            new List<ToolInvocation>();
        public string VbaModuleType = "StdModule";
        public readonly List<string> RanMacros = new List<string>();
        public Action<string> BeforeExcelBackendCall { get; set; }
        public string ThrowOnExcelBackendOperation { get; set; }
        public Func<string, string> VbaWriteTransform { get; set; }
        public bool ExcelWriteThrowAfterMutation { get; set; }
        public bool ExcelReplaceThrowAfterMutation { get; set; }
        public bool ExcelSheetThrowAfterMutation { get; set; }
        public bool ExcelRangeMutationThrowAfterMutation { get; set; }
        public bool ExcelTableThrowAfterMutation { get; set; }
        public bool ExcelChartThrowAfterMutation { get; set; }
        public bool WordThrowAfterMutation { get; set; }
        public bool PowerPointThrowAfterMutation { get; set; }
        public bool OutlookThrowAfterMutation { get; set; }
        public Func<ExcelSheetCollectionSnapshot, ExcelSheetCollectionSnapshot>
            ExcelSheetReadTransform { get; set; }
        public Func<ExcelRangeMutationSnapshot, ExcelRangeMutationSnapshot>
            ExcelRangeMutationReadTransform { get; set; }
        public Func<ExcelTableCollectionSnapshot, ExcelTableCollectionSnapshot>
            ExcelTableReadTransform { get; set; }
        public Func<ExcelChartCollectionSnapshot, ExcelChartCollectionSnapshot>
            ExcelChartReadTransform { get; set; }
        public int VbaReportedLineCountOffset { get; set; }
        public string DocumentKeyValue { get; set; }
        public string RuntimeDocumentKeyValue { get; set; }
        public string DocumentPathValue { get; set; }
        private ExcelInspectSnapshot _nextExcelInspectSnapshot;
        public List<ExcelNameSnapshot> ExcelNamesForTest { get; } = new List<ExcelNameSnapshot>();
        private ExcelWriteBackendException _nextExcelWriteApplyFailure;
        private ExcelSheetBackendException _nextExcelSheetApplyFailure;
        private ExcelRangeMutationBackendException _nextExcelRangeMutationApplyFailure;
        private ExcelTableBackendException _nextExcelTableApplyFailure;
        private ExcelChartBackendException _nextExcelChartApplyFailure;

        private readonly string _hostName;
        private string _documentTitle;
        private readonly string _documentSnapshot;
        private readonly Dictionary<string, FakeVbaModule> _vbaModules;
        private readonly Dictionary<string, FakeSheet> _sheets;
        private readonly List<string> _excelSheetOrder;
        private readonly Dictionary<string, string> _excelRangeFormats;
        private readonly Dictionary<string, string> _excelRangeFilters;
        private readonly Dictionary<string, string> _excelRangeAutoFits;
        private string _activeExcelSheetName;
        private readonly List<FakeSlide> _slides;
        private readonly List<FakeOutlookMail> _outlookMail;
        private int _nextPowerPointSlideId = 1;
        private int _nextPowerPointShapeId = 1;
        private readonly List<string> _wordComments;
        private readonly List<WordTableSnapshot> _wordTables =
            new List<WordTableSnapshot>();
        private string _wordFormatToken = string.Empty;
        private string _wordText;
        private string _outlookSelection;
        private string _outlookDraft;
        private readonly FakeDocumentSession _documentSession;
        private readonly FakeStaDispatcher _dispatcher;

        public string VbaModuleCode
        {
            get { return GetVbaModuleCode("Module1"); }
            set { SetVbaModule("Module1", value, VbaModuleType); }
        }

        public FakeOfficeAdapter()
            : this("Excel", "Harness.xlsx", "Harness document")
        {
        }

        private FakeOfficeAdapter(string hostName, string documentTitle,
            string documentSnapshot)
        {
            _hostName = hostName;
            _documentTitle = documentTitle;
            _documentSnapshot = documentSnapshot;
            _vbaModules = new Dictionary<string, FakeVbaModule>(StringComparer.OrdinalIgnoreCase);
            _sheets = new Dictionary<string, FakeSheet>(StringComparer.OrdinalIgnoreCase);
            _excelSheetOrder = new List<string>();
            _excelRangeFormats = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            _excelRangeFilters = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            _excelRangeAutoFits = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            _slides = new List<FakeSlide>();
            _outlookMail = new List<FakeOutlookMail>();
            _wordComments = new List<string>();
            _wordText = documentSnapshot ?? string.Empty;
            _outlookSelection = documentSnapshot ?? string.Empty;
            _outlookDraft = string.Empty;
            DocumentKeyValue = "doc";
            RuntimeDocumentKeyValue = "runtime-doc";
            DocumentPathValue = "C:\\Demo\\MockWorkbook.xlsx";
            _dispatcher = new FakeStaDispatcher();
            _documentSession = new FakeDocumentSession(this, _dispatcher);
            SeedDemoState();
        }

        public static FakeOfficeAdapter ForHost(string host)
        {
            if (string.Equals(host, "Word", StringComparison.OrdinalIgnoreCase))
            {
                return new FakeOfficeAdapter("Word", "MockDocument.docx",
                    "Quarterly revenue grew 18%. Main risks: churn and delayed enterprise renewals.");
            }

            if (string.Equals(host, "PowerPoint", StringComparison.OrdinalIgnoreCase))
            {
                return new FakeOfficeAdapter("PowerPoint", "MockDeck.pptx",
                    "Mock slide deck");
            }

            if (string.Equals(host, "Outlook", StringComparison.OrdinalIgnoreCase))
            {
                return new FakeOfficeAdapter("Outlook", "Selected mail",
                    "Subject: Renewal follow-up\nCustomer asks for a concise answer about next steps.");
            }

            return new FakeOfficeAdapter("Excel", "MockWorkbook.xlsx",
                "Mock workbook");
        }

        public string HostName { get { return _hostName; } }
        public string DocumentKey { get { return DocumentKeyValue; } }
        public string RuntimeDocumentKey { get { return RuntimeDocumentKeyValue; } }
        public string DocumentTitle { get { return _documentTitle; } }
        public IOfficeDocumentSession DocumentSession
        {
            get { return _documentSession; }
        }
        public IOfficeStaDispatcher StaDispatcher
        {
            get { return _dispatcher; }
        }
        public string WordText { get { return _wordText; } }
        public string OutlookDraft { get { return _outlookDraft; } }
        public int WordCommentCount { get { return _wordComments.Count; } }
        public int SlideCount { get { return _slides.Count; } }
        public int DocumentSnapshotReadCount { get; private set; }
        public int ExcelReadMaterializationCount { get; private set; }
        public IExcelReadBackend ExcelReadBackend
        {
            get { return string.Equals(_hostName, "Excel", StringComparison.OrdinalIgnoreCase) ? this : null; }
        }
        public IExcelWriteBackend ExcelWriteBackend
        {
            get { return string.Equals(_hostName, "Excel", StringComparison.OrdinalIgnoreCase) ? this : null; }
        }
        public IExcelFindReplaceBackend ExcelFindReplaceBackend
        {
            get { return string.Equals(_hostName, "Excel", StringComparison.OrdinalIgnoreCase) ? this : null; }
        }
        public IExcelSheetBackend ExcelSheetBackend
        {
            get { return string.Equals(_hostName, "Excel", StringComparison.OrdinalIgnoreCase) ? this : null; }
        }
        public IExcelRangeMutationBackend ExcelRangeMutationBackend
        {
            get { return string.Equals(_hostName, "Excel", StringComparison.OrdinalIgnoreCase) ? this : null; }
        }
        public IExcelTableBackend ExcelTableBackend
        {
            get { return string.Equals(_hostName, "Excel", StringComparison.OrdinalIgnoreCase) ? this : null; }
        }
        public IExcelChartBackend ExcelChartBackend
        {
            get { return string.Equals(_hostName, "Excel", StringComparison.OrdinalIgnoreCase) ? this : null; }
        }
        public IWordBackend WordBackend
        {
            get
            {
                return string.Equals(
                    _hostName, "Word", StringComparison.OrdinalIgnoreCase)
                    ? this : null;
            }
        }
        public IPowerPointBackend PowerPointBackend
        {
            get
            {
                return string.Equals(
                    _hostName, "PowerPoint", StringComparison.OrdinalIgnoreCase)
                    ? this : null;
            }
        }
        public IOutlookBackend OutlookBackend
        {
            get
            {
                return string.Equals(
                    _hostName, "Outlook", StringComparison.OrdinalIgnoreCase)
                    ? this : null;
            }
        }
        public IVbaHostBackend VbaHostBackend
        {
            get
            {
                return string.Equals(
                    _hostName, "Excel", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        _hostName, "Word", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        _hostName, "PowerPoint", StringComparison.OrdinalIgnoreCase)
                    ? this : null;
            }
        }

        public OfficeContext GetOfficeContext()
        {
            return new OfficeContext
            {
                Host = _hostName,
                DocumentPath = DocumentPathValue,
                DocumentTitle = _documentTitle,
                ContainerName = string.Equals(_hostName, "Excel", StringComparison.OrdinalIgnoreCase) ? "Data" : string.Empty,
                SelectionAddress = string.Equals(_hostName, "Excel", StringComparison.OrdinalIgnoreCase) ? "A1:B4" : "selection",
                SelectionText = SelectionText()
            };
        }

        public IReadOnlyList<OpenOfficeDocumentDto> ListOpenDocuments()
        {
            if (!string.Equals(_hostName, "Excel", StringComparison.OrdinalIgnoreCase))
            {
                return new[]
                {
                    new OpenOfficeDocumentDto { Host = _hostName, DocumentKey = DocumentKey, Title = DocumentTitle, IsActive = true }
                };
            }

            return new[]
            {
                new OpenOfficeDocumentDto { Host = "Excel", DocumentKey = "doc", Title = "MockWorkbook.xlsx", Path = "C:\\Demo\\MockWorkbook.xlsx", IsActive = DocumentKeyValue == "doc" },
                new OpenOfficeDocumentDto { Host = "Excel", DocumentKey = "forecast-doc", Title = "Forecast.xlsx", Path = "C:\\Demo\\Forecast.xlsx", IsActive = DocumentKeyValue == "forecast-doc" }
            };
        }

        public bool ActivateDocument(string documentKey)
        {
            var document = ListOpenDocuments().FirstOrDefault(item =>
                string.Equals(item.DocumentKey, documentKey, StringComparison.OrdinalIgnoreCase));
            if (document == null)
            {
                return false;
            }
            DocumentKeyValue = document.DocumentKey;
            RuntimeDocumentKeyValue = "runtime-" + document.DocumentKey;
            DocumentPathValue = document.Path;
            _documentTitle = document.Title;
            return true;
        }

        public bool OpenDocument(string path)
        {
            return false;
        }

        public string GetDocumentSnapshot(int maxChars)
        {
            DocumentSnapshotReadCount += 1;
            var snapshot = string.Empty;
            if (string.Equals(_hostName, "Excel", StringComparison.OrdinalIgnoreCase))
            {
                snapshot = BuildWorkbookSummary();
            }
            else if (string.Equals(_hostName, "Word", StringComparison.OrdinalIgnoreCase))
            {
                snapshot = _wordText;
            }
            else if (string.Equals(_hostName, "PowerPoint", StringComparison.OrdinalIgnoreCase))
            {
                snapshot = JsonConvert.SerializeObject(_slides.Select(s => new { title = s.Title, body = s.Body }).ToArray());
            }
            else
            {
                snapshot = _outlookSelection;
            }

            return Trim(snapshot, maxChars);
        }

        public void PrepareForContextCapture()
        {
        }

        public ContextNote CaptureSelectionContext(string mode, int maxChars)
        {
            return new ContextNote
            {
                Host = _hostName,
                Kind = string.IsNullOrWhiteSpace(mode) ? "selection" : mode,
                Title = "Mock selection",
                Reference = string.Equals(_hostName, "Excel", StringComparison.OrdinalIgnoreCase) ? "Data!A1:B4" : "selection",
                Source = _documentTitle,
                Text = Trim(SelectionText(), maxChars),
                Preview = Trim(SelectionText(), 240),
                DetailsJson = JsonConvert.SerializeObject(new { mock = true, host = _hostName })
            };
        }

        public IEnumerable<SkillDefinition> GetBuiltInSkills()
        {
            if (string.Equals(_hostName, "Word", StringComparison.OrdinalIgnoreCase))
            {
                return new[] { BuiltInSkill("word.document_editing", "Word", "Word document editing", "Rewrite, insert, format, and review Word document content.") };
            }
            if (string.Equals(_hostName, "PowerPoint", StringComparison.OrdinalIgnoreCase))
            {
                return new[] { BuiltInSkill("powerpoint.deck_building", "PowerPoint", "PowerPoint deck building", "Create and improve slide structure, content, and speaker notes.") };
            }
            if (string.Equals(_hostName, "Outlook", StringComparison.OrdinalIgnoreCase))
            {
                return new[] { BuiltInSkill("outlook.email_assistant", "Outlook", "Outlook email assistant", "Draft, summarize, and reply to Outlook mail.") };
            }
            return new[] { BuiltInSkill("excel.analysis_reporting", "Excel", "Excel analysis reporting", "Analyze ranges, create summaries, tables, and charts in Excel.") };
        }

        public void QueueExcelInspectSnapshot(ExcelInspectSnapshot snapshot)
        {
            _nextExcelInspectSnapshot = snapshot;
        }

        public void QueueExcelWriteApplyFailure(string message, string errorCode, bool retryable)
        {
            _nextExcelWriteApplyFailure = new ExcelWriteBackendException(message, errorCode, retryable);
        }

        public void QueueExcelSheetApplyFailure(
            string message, string errorCode, bool retryable)
        {
            _nextExcelSheetApplyFailure =
                new ExcelSheetBackendException(message, errorCode, retryable);
        }

        public void QueueExcelRangeMutationApplyFailure(
            string message, string errorCode, bool retryable)
        {
            _nextExcelRangeMutationApplyFailure =
                new ExcelRangeMutationBackendException(
                    message, errorCode, retryable);
        }

        public void QueueExcelTableApplyFailure(
            string message, string errorCode, bool retryable)
        {
            _nextExcelTableApplyFailure =
                new ExcelTableBackendException(message, errorCode, retryable);
        }

        public void QueueExcelChartApplyFailure(
            string message, string errorCode, bool retryable)
        {
            _nextExcelChartApplyFailure =
                new ExcelChartBackendException(message, errorCode, retryable);
        }

        private void BeginExcelBackendCall(string operation)
        {
            ExcelBackendCalls.Add(operation);
            var before = BeforeExcelBackendCall;
            if (before != null) before(operation);
            if (!string.IsNullOrWhiteSpace(ThrowOnExcelBackendOperation) &&
                string.Equals(ThrowOnExcelBackendOperation, operation, StringComparison.Ordinal))
            {
                ThrowOnExcelBackendOperation = null;
                throw new InvalidOperationException("scripted Excel backend failure");
            }
        }

        public void SetVbaModule(string moduleName, string code, string type)
        {
            var name = string.IsNullOrWhiteSpace(moduleName) ? "Module1" : moduleName;
            _vbaModules[name] = new FakeVbaModule
            {
                Name = name,
                Code = code ?? string.Empty,
                Type = string.IsNullOrWhiteSpace(type) ? "StdModule" : type
            };
        }

        public void SetDocumentTitle(string title)
        {
            _documentTitle = title ?? string.Empty;
        }

        public string GetVbaModuleCode(string moduleName)
        {
            FakeVbaModule module;
            return _vbaModules.TryGetValue(string.IsNullOrWhiteSpace(moduleName) ? "Module1" : moduleName, out module)
                ? module.Code
                : string.Empty;
        }

        public bool HasSheet(string sheetName)
        {
            return _sheets.ContainsKey(string.IsNullOrWhiteSpace(sheetName) ? "Sheet1" : sheetName);
        }

        public string CellValue(string sheetName, string address)
        {
            FakeSheet sheet;
            if (!_sheets.TryGetValue(string.IsNullOrWhiteSpace(sheetName) ? "Sheet1" : sheetName, out sheet))
            {
                return string.Empty;
            }

            var cell = ParseAddress(address);
            object value;
            return sheet.Cells.TryGetValue(CellKey(cell.Row, cell.Column), out value)
                ? Convert.ToString(value) : string.Empty;
        }

        public int ChartCount(string sheetName)
        {
            FakeSheet sheet;
            return _sheets.TryGetValue(string.IsNullOrWhiteSpace(sheetName) ? "Sheet1" : sheetName, out sheet)
                ? sheet.Charts.Count
                : 0;
        }

        public void SeedExcelSheets(int count)
        {
            for (var index = 1; index <= Math.Max(0, count); index++)
                EnsureSheet("Bound " + index);
        }

        private void SeedDemoState()
        {
            SetVbaModule("Module1", "Sub DemoMacro()\n    MsgBox \"RNAssistant mock demo\"\nEnd Sub", VbaModuleType);
            if (string.Equals(_hostName, "Excel", StringComparison.OrdinalIgnoreCase))
            {
                WriteMatrix("Data", "A1", new List<List<string>>
                {
                    new List<string> { "Month", "Sales" },
                    new List<string> { "Jan", "120" },
                    new List<string> { "Feb", "150" },
                    new List<string> { "Mar", "180" }
                });
            }
            else if (string.Equals(_hostName, "Outlook", StringComparison.OrdinalIgnoreCase))
            {
                _outlookMail.Add(new FakeOutlookMail
                {
                    EntryId = "mail-1",
                    Subject = "Renewal follow-up",
                    Sender = "Customer",
                    SenderEmail = "customer@example.com",
                    To = "owner@example.com",
                    Received = new DateTime(2026, 8, 31, 9, 0, 0, DateTimeKind.Utc),
                    Categories = string.Empty,
                    Unread = true,
                    Body = "Customer asks for a concise answer about next steps."
                });
                _outlookMail.Add(new FakeOutlookMail
                {
                    EntryId = "mail-2",
                    Subject = "Quarterly plan",
                    Sender = "Manager",
                    SenderEmail = "manager@example.com",
                    To = "owner@example.com",
                    Received = new DateTime(2026, 7, 15, 10, 0, 0, DateTimeKind.Utc),
                    Categories = "Planning",
                    Unread = false,
                    Body = "Please review the quarterly plan."
                });
            }
            else if (string.Equals(_hostName, "PowerPoint", StringComparison.OrdinalIgnoreCase))
            {
                _slides.Add(new FakeSlide
                {
                    Id = _nextPowerPointSlideId++,
                    Title = "Q2 Results",
                    Body = "Revenue grew 18%; retention needs focus.",
                    Shapes = new List<FakePowerPointShape>()
                });
            }
        }

        public ExcelRangeSnapshot ReadRange(ExcelRangeReadRequest request)
        {
            BeginExcelBackendCall(ExcelRangeReadOperation);
            if (request == null) throw new ArgumentNullException(nameof(request));
            var sheetName = string.IsNullOrWhiteSpace(request.Sheet) ? "Sheet1" : request.Sheet;
            var content = (request.Content ?? "values").ToLowerInvariant();
            var range = request.Address ?? string.Empty;
            if (string.IsNullOrWhiteSpace(range)) range = content == "profile" ? "A1:B4" : "A1";
            var maxCells = request.MaxCells;
            if (maxCells < 1 || maxCells > ExcelReadService.MaxReadCells)
                throw new ExcelReadBackendException("invalid range bound", "excel_range_bound_invalid", false);
            var bounds = ParseRange(range);
            var rows = bounds.End.Row - bounds.Start.Row + 1;
            var columns = bounds.End.Column - bounds.Start.Column + 1;
            var cellCount = rows <= 0 || columns <= 0 ? 0 : (long)rows * columns;
            if (cellCount > maxCells)
            {
                throw new ExcelReadBackendException("range is too large", "excel_range_too_large", true,
                    JsonConvert.SerializeObject(new
                    {
                        address = range, cellCount = cellCount, maxCells = maxCells
                    }));
            }
            ExcelReadMaterializationCount++;
            var matrix = ReadRange(sheetName, range)
                .Select(row => row.Cast<object>().ToList()).ToList();
            var snapshot = new ExcelRangeSnapshot
            {
                Sheet = sheetName,
                Address = range,
                Rows = rows,
                Columns = columns,
                CellCount = cellCount
            };
            if (content == "values" || content == "profile") snapshot.Values = matrix;
            if (content == "formulas" || content == "profile") snapshot.Formulas = matrix
                .Select(row => row.ToList()).ToList();
            return snapshot;
        }

        public ExcelInspectSnapshot Inspect(ExcelInspectRequest request)
        {
            BeginExcelBackendCall(ExcelInspectOperation);
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (_nextExcelInspectSnapshot != null)
            {
                var scripted = _nextExcelInspectSnapshot;
                _nextExcelInspectSnapshot = null;
                return scripted;
            }
            var kind = (request.Kind ?? "workbook").ToLowerInvariant();
            var maxItems = request.MaxItems;
            if (maxItems < 1 || maxItems > ExcelReadService.MaxInspectItems)
                throw new ExcelReadBackendException("invalid inspection bound", "excel_inspect_bound_invalid", false);
            var sheetFilter = request.Sheet ?? string.Empty;
            var sheets = _sheets.Values.Where(sheet => string.IsNullOrWhiteSpace(sheetFilter) ||
                string.Equals(sheet.Name, sheetFilter, StringComparison.OrdinalIgnoreCase)).ToList();
            var snapshot = new ExcelInspectSnapshot { Kind = kind };
            if (kind == "workbook" || kind == "sheets")
            {
                var items = sheets.Take(maxItems).Select(sheet => new ExcelSheetSnapshot
                {
                    Name = sheet.Name,
                    UsedRange = kind == "workbook" ? "A1:B4" : null
                }).ToList();
                snapshot.Sheets = kind == "sheets" ? items : null;
                snapshot.Workbook = kind == "workbook" ? new ExcelWorkbookSnapshot
                {
                    Name = DocumentTitle, FullName = DocumentPathValue, Sheets = items
                } : null;
                snapshot.ReturnedCount = items.Count;
                snapshot.Truncated = sheets.Count > items.Count;
            }
            else if (kind == "charts")
            {
                var chartName = request.ChartName ?? string.Empty;
                var items = sheets.SelectMany(sheet => sheet.Charts.Select(chart => FakeChartSnapshot(sheet, chart)))
                    .Take(maxItems + 1).ToList();
                if (!string.IsNullOrWhiteSpace(chartName))
                {
                    snapshot.Chart = items.Take(maxItems).FirstOrDefault(chart =>
                        string.Equals(chart.Name, chartName, StringComparison.OrdinalIgnoreCase));
                    if (snapshot.Chart == null)
                        throw new ExcelReadBackendException(
                            "Chart not found: " + chartName, "excel_chart_not_found", false);
                    snapshot.ReturnedCount = 1;
                }
                else
                {
                    snapshot.Charts = items.Take(maxItems).ToList();
                    snapshot.ReturnedCount = snapshot.Charts.Count;
                    snapshot.Truncated = items.Count > maxItems;
                }
            }
            else if (kind == "tables")
            {
                var items = sheets.SelectMany(sheet => sheet.Tables.Select(table => new ExcelTableSnapshot
                {
                    Sheet = sheet.Name, Name = table.Name,
                    DisplayName = table.DisplayName, Range = table.Range,
                    Rows = table.Rows, Columns = table.Columns
                })).Take(maxItems + 1).ToList();
                snapshot.Tables = items.Take(maxItems).ToList();
                snapshot.ReturnedCount = snapshot.Tables.Count;
                snapshot.Truncated = items.Count > maxItems;
            }
            else if (kind == "names")
            {
                snapshot.Names = ExcelNamesForTest.Take(maxItems).Select(name => new ExcelNameSnapshot {
                    Name = name.Name, RefersTo = name.RefersTo, TargetKind = name.TargetKind,
                    Sheet = name.Sheet, Address = name.Address
                }).ToList();
                snapshot.ReturnedCount = snapshot.Names.Count;
                snapshot.Truncated = ExcelNamesForTest.Count > maxItems;
            }
            else if (kind == "shapes")
            {
                snapshot.Shapes = new List<ExcelShapeSnapshot>();
            }
            else throw new ExcelReadBackendException(
                "invalid inspect kind", "excel_inspect_kind_invalid", false);
            return snapshot;
        }

        private static ExcelChartSnapshot FakeChartSnapshot(FakeSheet sheet, FakeChart chart)
        {
            return new ExcelChartSnapshot
            {
                Sheet = sheet.Name, Name = chart.Name, Title = chart.Title,
                ChartType = chart.ChartType, Series = new List<ExcelChartSeriesSnapshot>(),
                SeriesTruncated = false
            };
        }

        private string SelectionText()
        {
            if (string.Equals(_hostName, "Excel", StringComparison.OrdinalIgnoreCase))
            {
                return "Data!A1:B4\nMonth,Sales\nJan,120\nFeb,150\nMar,180";
            }

            if (string.Equals(_hostName, "PowerPoint", StringComparison.OrdinalIgnoreCase))
            {
                return _slides.Count == 0 ? _documentSnapshot : _slides[_slides.Count - 1].Title + "\n" + _slides[_slides.Count - 1].Body;
            }

            if (string.Equals(_hostName, "Outlook", StringComparison.OrdinalIgnoreCase))
            {
                return _outlookSelection;
            }

            return _wordText;
        }

        private FakeSheet EnsureSheet(string name)
        {
            var sheetName = string.IsNullOrWhiteSpace(name) ? "Sheet1" : name;
            FakeSheet sheet;
            if (!_sheets.TryGetValue(sheetName, out sheet))
            {
                sheet = new FakeSheet { Name = sheetName };
                _sheets[sheetName] = sheet;
                _excelSheetOrder.Add(sheetName);
                if (string.IsNullOrWhiteSpace(_activeExcelSheetName))
                    _activeExcelSheetName = sheetName;
            }

            return sheet;
        }

        private void WriteMatrix(string sheetName, string startAddress, List<List<string>> values)
        {
            var sheet = EnsureSheet(sheetName);
            var start = ParseAddress(startAddress);
            for (var r = 0; r < values.Count; r++)
            {
                for (var c = 0; c < values[r].Count; c++)
                {
                    sheet.Cells[CellKey(start.Row + r, start.Column + c)] = values[r][c] ?? string.Empty;
                    sheet.FormulaCells.Remove(CellKey(start.Row + r, start.Column + c));
                }
            }
        }

        private void ClearRange(string sheetName, string range)
        {
            var sheet = EnsureSheet(sheetName);
            var bounds = ParseRange(range);
            for (var row = bounds.Start.Row; row <= bounds.End.Row; row++)
            {
                for (var column = bounds.Start.Column; column <= bounds.End.Column; column++)
                {
                    var key = CellKey(row, column);
                    sheet.Cells.Remove(key);
                    sheet.FormulaCells.Remove(key);
                }
            }
        }

        private List<List<string>> ReadRange(string sheetName, string range)
        {
            var sheet = EnsureSheet(sheetName);
            var bounds = ParseRange(range);
            var values = new List<List<string>>();
            for (var row = bounds.Start.Row; row <= bounds.End.Row; row++)
            {
                var line = new List<string>();
                for (var column = bounds.Start.Column; column <= bounds.End.Column; column++)
                {
                    object value;
                    line.Add(sheet.Cells.TryGetValue(CellKey(row, column), out value)
                        ? Convert.ToString(value) : string.Empty);
                }

                values.Add(line);
            }

            return values;
        }

        private string BuildWorkbookSummary()
        {
            return JsonConvert.SerializeObject(_sheets.Values.Select(s => new
            {
                name = s.Name,
                cellCount = s.Cells.Count,
                tableCount = s.Tables.Count,
                chartCount = s.Charts.Count,
                tables = s.Tables.Select(table => table.Name).ToArray(),
                charts = s.Charts.Select(c => new { name = c.Name, title = c.Title, sourceRange = c.SourceRange, chartType = c.ChartType }).ToArray()
            }).ToArray());
        }

        private bool TryFindChart(string sheetName, string chartName, out FakeSheet resolvedSheet, out FakeChart resolvedChart)
        {
            foreach (var pair in _sheets)
            {
                if (!string.IsNullOrWhiteSpace(sheetName) &&
                    !string.Equals(pair.Key, sheetName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var chart = pair.Value.Charts.FirstOrDefault(item =>
                    string.Equals(item.Name, chartName, StringComparison.OrdinalIgnoreCase));
                if (chart != null)
                {
                    resolvedSheet = pair.Value;
                    resolvedChart = chart;
                    return true;
                }
            }

            resolvedSheet = null;
            resolvedChart = null;
            return false;
        }

        private FakeSlide LastOrNewSlide()
        {
            if (_slides.Count == 0)
            {
                _slides.Add(new FakeSlide
                {
                    Id = _nextPowerPointSlideId++,
                    Shapes = new List<FakePowerPointShape>()
                });
            }

            return _slides[_slides.Count - 1];
        }

        private static FakeRange ParseRange(string value)
        {
            var range = string.IsNullOrWhiteSpace(value) ? "A1:A1" : value;
            var bang = range.IndexOf('!');
            if (bang >= 0)
            {
                range = range.Substring(bang + 1);
            }

            var parts = range.Split(':');
            var start = ParseAddress(parts.Length > 0 ? parts[0] : "A1");
            var end = ParseAddress(parts.Length > 1 ? parts[1] : parts[0]);
            return new FakeRange { Start = start, End = end };
        }

        private static FakeCellAddress ParseAddress(string value)
        {
            var address = string.IsNullOrWhiteSpace(value) ? "A1" : value.Trim();
            var letters = new string(address.TakeWhile(char.IsLetter).ToArray());
            var digits = new string(address.SkipWhile(char.IsLetter).TakeWhile(char.IsDigit).ToArray());
            int row;
            if (!int.TryParse(digits, out row) || row <= 0)
            {
                row = 1;
            }

            return new FakeCellAddress { Row = row, Column = ColumnNumber(letters) };
        }

        private static int ColumnNumber(string letters)
        {
            var result = 0;
            foreach (var ch in (letters ?? "A").ToUpperInvariant())
            {
                if (ch < 'A' || ch > 'Z')
                {
                    continue;
                }

                result = result * 26 + (ch - 'A' + 1);
            }

            return result <= 0 ? 1 : result;
        }

        private static string CellKey(int row, int column)
        {
            return row + ":" + column;
        }

        private static SkillDefinition BuiltInSkill(string id, string host, string name, string description)
        {
            return new SkillDefinition
            {
                Id = id,
                Host = host,
                Name = name,
                Description = description,
                BodyMarkdown = "# " + name,
                Enabled = true,
                BuiltIn = true
            };
        }

        private sealed class FakeStaDispatcher : IOfficeStaDispatcher
        {
            private readonly ThreadLocal<int> _invokeDepth =
                new ThreadLocal<int>(() => 0);

            public bool CheckAccess { get { return _invokeDepth.Value > 0; } }

            public T Invoke<T>(Func<T> action)
            {
                _invokeDepth.Value += 1;
                try
                {
                    return action();
                }
                finally
                {
                    _invokeDepth.Value -= 1;
                }
            }
        }

        private sealed class FakeDocumentSession : IOfficeDocumentSession
        {
            private readonly FakeOfficeAdapter _owner;

            internal FakeDocumentSession(FakeOfficeAdapter owner,
                IOfficeStaDispatcher dispatcher)
            {
                _owner = owner;
                StaDispatcher = dispatcher;
                MutationGate = new object();
            }

            public string Host { get { return _owner.HostName; } }
            public string StableDocumentId
            {
                get { return _owner.DocumentKey; }
            }
            public string RuntimeDocumentId
            {
                get { return _owner.RuntimeDocumentKey; }
            }
            public object BoundDocumentObject { get { return _owner; } }
            public bool IsAlive
            {
                get
                {
                    return !string.IsNullOrWhiteSpace(
                        _owner.RuntimeDocumentKey);
                }
            }
            public IOfficeStaDispatcher StaDispatcher { get; private set; }
            public object MutationGate { get; private set; }
        }

        private static string Trim(string value, int maxChars)
        {
            value = value ?? string.Empty;
            return maxChars > 0 && value.Length > maxChars ? value.Substring(0, maxChars) : value;
        }

        private static int LineCount(string value)
        {
            return VbaTextCanonicalizer.LiveCodeLineCount(value);
        }

        private sealed class FakeVbaModule
        {
            public string Name { get; set; }
            public string Code { get; set; }
            public string Type { get; set; }
        }

        private sealed class FakeSheet
        {
            public string Name { get; set; }
            public Dictionary<string, object> Cells { get; private set; }
            public HashSet<string> FormulaCells { get; private set; }
            public List<FakeChart> Charts { get; private set; }
            public List<FakeTable> Tables { get; private set; }

            public FakeSheet()
            {
                Cells = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                FormulaCells = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Charts = new List<FakeChart>();
                Tables = new List<FakeTable>();
            }
        }

        private sealed class FakeTable
        {
            public string Name { get; set; }
            public string DisplayName { get; set; }
            public string Range { get; set; }
            public int Rows { get; set; }
            public int Columns { get; set; }
            public bool HasHeaders { get; set; }
            public string Style { get; set; }
        }

        private sealed class FakeChart
        {
            public string Name { get; set; }
            public string SourceRange { get; set; }
            public string ChartType { get; set; }
            public bool HasTitle { get; set; }
            public string Title { get; set; }
            public string CategoryLabelsRange { get; set; }
            public bool HasXAxisTitle { get; set; }
            public string XAxisTitle { get; set; }
            public bool HasYAxisTitle { get; set; }
            public string YAxisTitle { get; set; }
            public double Left { get; set; }
            public double Top { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
            public int SeriesCount { get; set; }
        }

        private sealed class FakeSlide
        {
            public int Id { get; set; }
            public string Title { get; set; }
            public string Body { get; set; }
            public string Notes { get; set; }
            public List<FakePowerPointShape> Shapes { get; set; }
        }

        private sealed class FakePowerPointShape
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string Kind { get; set; }
            public string Text { get; set; }
            public int Left { get; set; }
            public int Top { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public int Rows { get; set; }
            public int Columns { get; set; }
            public IReadOnlyList<IReadOnlyList<object>> Values { get; set; }
        }

        private sealed class FakeCellAddress
        {
            public int Row { get; set; }
            public int Column { get; set; }
        }

        private sealed class FakeRange
        {
            public FakeCellAddress Start { get; set; }
            public FakeCellAddress End { get; set; }
        }
    }
}
