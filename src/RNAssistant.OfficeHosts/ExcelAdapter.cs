using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Excel = Microsoft.Office.Interop.Excel;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.Office.Tools;
using RNAssistant.OfficeHosts.Identity;
using RNAssistant.OfficeHosts.Vba;

namespace RNAssistant.OfficeHosts
{
    public sealed partial class ExcelAdapter : IOfficeApplicationAdapter, IOfficeContextProvider, IOfficeBuiltInSkillProvider, IOfficeDocumentCatalog, IOfficeDocumentSessionProvider, IOfficeDispatcherProvider, IExcelBackendProvider
    {
        private const int MaxContextPreviewCells = 2000;

        private readonly Excel.Application _application;
        private readonly Excel.Workbook _targetWorkbook;
        private readonly ExcelDocumentSession _documentSession;
        private readonly ExcelInteropBackend _excelBackend;
        private readonly ExcelFindReplaceInteropBackend _excelFindReplaceBackend;
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

        public IEnumerable<ToolDefinition> GetBuiltInTools()
        {
            return OfficeBuiltInToolCatalog.ForHost(HostName);
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

        public ToolResult ExecuteTool(ToolCommand command)
        {
            try
            {
                switch (command.ToolId)
                {
                    case "excel.create_chat_chart":
                        return CreateChatChart(command);
                    case "excel.add_table":
                        return AddTable(command);
                    case "excel.upsert_chart":
                        return UpsertChart(command);
                    case "excel.delete_chart":
                        return DeleteChart(command);
                    case "excel.format_range":
                        return FormatRange(command);
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
                    case "excel.vba_replace_module":
                        return ReplaceVbaModule(command);
                    case "excel.run_macro":
                        return RunMacro(command);
                    case "excel.vba_install_package_internal":
                        return VbaProjectSupport.InstallPackage(RequireWorkbook(), ToolArgumentReader.String(command.Arguments, "componentsJson", "[]"), ToolArgumentReader.String(command.Arguments, "marker", string.Empty));
                    case "excel.vba_remove_package_internal":
                        return VbaProjectSupport.RemovePackage(RequireWorkbook(), ToolArgumentReader.String(command.Arguments, "expectedComponentsJson", "{}"), ToolArgumentReader.String(command.Arguments, "expectedMarker", string.Empty));
                    case "excel.vba_create_module_internal":
                        return VbaProjectSupport.CreateModule(RequireWorkbook(), ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty), ToolArgumentReader.String(command.Arguments, "componentType", "StdModule"), ToolArgumentReader.String(command.Arguments, "code", string.Empty));
                    case "excel.vba_rename_module_internal":
                        return VbaProjectSupport.RenameModule(
                            RequireWorkbook(),
                            ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty),
                            ToolArgumentReader.String(command.Arguments, "newModuleName", string.Empty),
                            ToolArgumentReader.String(command.Arguments, "expectedCodeSha256", null),
                            ToolArgumentReader.String(command.Arguments, "expectedComponentType", null));
                    case "excel.vba_delete_module_internal":
                        return VbaProjectSupport.DeleteModule(
                            RequireWorkbook(),
                            ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty),
                            ToolArgumentReader.String(command.Arguments, "expectedCodeSha256", null));
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
            if (command.Arguments.ContainsKey("startLine") || command.Arguments.ContainsKey("lineCount"))
            {
                return VbaProjectSupport.ReadModuleLines(
                    workbook,
                    moduleName,
                    ToolArgumentReader.Int32(command.Arguments, "startLine", 1),
                    ToolArgumentReader.Int32(command.Arguments, "lineCount", 200));
            }
            var maxChars = ToolArgumentReader.Int32(command.Arguments, "maxChars", 30000);
            return VbaProjectSupport.ReadModule(workbook, moduleName, maxChars);
        }

        private ToolResult ReplaceVbaModule(ToolCommand command)
        {
            var workbook = RequireWorkbook();
            var moduleName = ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty);
            var code = ToolArgumentReader.String(command.Arguments, "code", string.Empty);
            var createIfMissing = ToolArgumentReader.Boolean(command.Arguments, "createIfMissing", true);
            return VbaProjectSupport.ReplaceModule(
                workbook,
                moduleName,
                code,
                createIfMissing,
                ToolArgumentReader.String(command.Arguments, "expectedCodeSha256", null));
        }

        private ToolResult RunMacro(ToolCommand command)
        {
            var macroName = ToolArgumentReader.String(command.Arguments, "macroName", string.Empty);
            if (string.IsNullOrWhiteSpace(macroName))
            {
                return ToolResult.Fail("No macroName provided.");
            }

            var argumentsJson = ToolArgumentReader.String(command.Arguments, "argumentsJson", "[]");
            var output = VbaProjectSupport.RunStringFunction(_application, macroName, argumentsJson);
            return ToolResult.Ok("Macro ran: " + macroName, JsonConvert.SerializeObject(new { output = output }));
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
