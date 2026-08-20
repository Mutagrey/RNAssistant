using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Harness
{
    internal sealed class FakeOfficeAdapter : IOfficeApplicationAdapter, IOfficeContextProvider, IOfficeBuiltInSkillProvider, IOfficeDocumentCatalog
    {
        public readonly List<ToolCommand> Executed = new List<ToolCommand>();
        public string VbaModuleType = "StdModule";
        public readonly List<string> RanMacros = new List<string>();
        public bool FailUnknownSkills { get; set; }
        public string ThrowOnToolId { get; set; }
        public string DocumentKeyValue { get; set; }
        public string RuntimeDocumentKeyValue { get; set; }

        private readonly string _hostName;
        private string _documentTitle;
        private readonly string _documentSnapshot;
        private readonly List<ToolDefinition> _builtInTools;
        private readonly Dictionary<string, Queue<ToolResult>> _scriptedResults;
        private readonly Dictionary<string, FakeVbaModule> _vbaModules;
        private readonly Dictionary<string, FakeSheet> _sheets;
        private readonly List<FakeSlide> _slides;
        private readonly List<string> _wordComments;
        private string _wordText;
        private string _outlookSelection;
        private string _outlookDraft;

        public string VbaModuleCode
        {
            get { return GetVbaModuleCode("Module1"); }
            set { SetVbaModule("Module1", value, VbaModuleType); }
        }

        public FakeOfficeAdapter()
            : this("Excel", "Harness.xlsx", ExcelBuiltIns(), "Harness document")
        {
        }

        private FakeOfficeAdapter(string hostName, string documentTitle, IEnumerable<ToolDefinition> builtInTools, string documentSnapshot)
        {
            _hostName = hostName;
            _documentTitle = documentTitle;
            _documentSnapshot = documentSnapshot;
            _builtInTools = new List<ToolDefinition>((builtInTools ?? new ToolDefinition[0]).Select(CloneTool));
            _scriptedResults = new Dictionary<string, Queue<ToolResult>>(StringComparer.OrdinalIgnoreCase);
            _vbaModules = new Dictionary<string, FakeVbaModule>(StringComparer.OrdinalIgnoreCase);
            _sheets = new Dictionary<string, FakeSheet>(StringComparer.OrdinalIgnoreCase);
            _slides = new List<FakeSlide>();
            _wordComments = new List<string>();
            _wordText = documentSnapshot ?? string.Empty;
            _outlookSelection = documentSnapshot ?? string.Empty;
            _outlookDraft = string.Empty;
            DocumentKeyValue = "doc";
            RuntimeDocumentKeyValue = "runtime-doc";
            SeedDemoState();
        }

        public static FakeOfficeAdapter ForHost(string host)
        {
            if (string.Equals(host, "Word", StringComparison.OrdinalIgnoreCase))
            {
                return new FakeOfficeAdapter("Word", "MockDocument.docx", WordBuiltIns(), "Quarterly revenue grew 18%. Main risks: churn and delayed enterprise renewals.");
            }

            if (string.Equals(host, "PowerPoint", StringComparison.OrdinalIgnoreCase))
            {
                return new FakeOfficeAdapter("PowerPoint", "MockDeck.pptx", PowerPointBuiltIns(), "Mock slide deck");
            }

            if (string.Equals(host, "Outlook", StringComparison.OrdinalIgnoreCase))
            {
                return new FakeOfficeAdapter("Outlook", "Selected mail", OutlookBuiltIns(), "Subject: Renewal follow-up\nCustomer asks for a concise answer about next steps.");
            }

            return new FakeOfficeAdapter("Excel", "MockWorkbook.xlsx", ExcelBuiltIns(), "Mock workbook");
        }

        public string HostName { get { return _hostName; } }
        public string DocumentKey { get { return DocumentKeyValue; } }
        public string RuntimeDocumentKey { get { return RuntimeDocumentKeyValue; } }
        public string DocumentTitle { get { return _documentTitle; } }
        public string WordText { get { return _wordText; } }
        public string OutlookDraft { get { return _outlookDraft; } }
        public int WordCommentCount { get { return _wordComments.Count; } }
        public int SlideCount { get { return _slides.Count; } }
        public int DocumentSnapshotReadCount { get; private set; }

        public OfficeContext GetOfficeContext()
        {
            return new OfficeContext
            {
                Host = _hostName,
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

        public IEnumerable<ToolDefinition> GetBuiltInTools()
        {
            return _builtInTools.Select(CloneTool).ToArray();
        }

        public IEnumerable<SkillDefinition> GetBuiltInSkills()
        {
            if (string.Equals(_hostName, "Word", StringComparison.OrdinalIgnoreCase))
            {
                return new[] { BuiltInSkill("word.document_editing", "Word", "Word document editing", "Rewrite, insert, format, and review Word document content.", new[] { "word", "editing", "review", "formatting" }) };
            }
            if (string.Equals(_hostName, "PowerPoint", StringComparison.OrdinalIgnoreCase))
            {
                return new[] { BuiltInSkill("powerpoint.deck_building", "PowerPoint", "PowerPoint deck building", "Create and improve slide structure, content, and speaker notes.", new[] { "powerpoint", "slides", "deck", "notes" }) };
            }
            if (string.Equals(_hostName, "Outlook", StringComparison.OrdinalIgnoreCase))
            {
                return new[] { BuiltInSkill("outlook.email_assistant", "Outlook", "Outlook email assistant", "Draft, summarize, and reply to Outlook mail.", new[] { "outlook", "email", "draft", "reply" }) };
            }
            return new[] { BuiltInSkill("excel.analysis_reporting", "Excel", "Excel analysis reporting", "Analyze ranges, create summaries, tables, and charts in Excel.", new[] { "excel", "analysis", "reporting", "charts" }) };
        }

        public void QueueResult(string toolId, ToolResult result)
        {
            Queue<ToolResult> queue;
            if (!_scriptedResults.TryGetValue(toolId, out queue))
            {
                queue = new Queue<ToolResult>();
                _scriptedResults[toolId] = queue;
            }

            queue.Enqueue(result);
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
            string value;
            return sheet.Cells.TryGetValue(CellKey(cell.Row, cell.Column), out value) ? value : string.Empty;
        }

        public int ChartCount(string sheetName)
        {
            FakeSheet sheet;
            return _sheets.TryGetValue(string.IsNullOrWhiteSpace(sheetName) ? "Sheet1" : sheetName, out sheet)
                ? sheet.Charts.Count
                : 0;
        }

        public ToolResult ExecuteTool(ToolCommand command)
        {
            Executed.Add(Clone(command));
            if (!string.IsNullOrWhiteSpace(ThrowOnToolId) &&
                string.Equals(ThrowOnToolId, command == null ? null : command.ToolId, StringComparison.OrdinalIgnoreCase))
            {
                ThrowOnToolId = null;
                throw new InvalidOperationException("scripted adapter failure");
            }
            ToolResult scripted;
            if (TryDequeueResult(command.ToolId, out scripted))
            {
                return scripted;
            }

            var fakeResult = ExecuteStatefulTool(command);
            if (fakeResult != null)
            {
                return fakeResult;
            }

            if ((command.ToolId ?? string.Empty).EndsWith(".vba_list_project_components_internal", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Ok("read " + command.ToolId, JsonConvert.SerializeObject(new
                {
                    title = DocumentTitle,
                    modules = _vbaModules.Values.Select(module => new { name = module.Name, type = module.Type, lineCount = LineCount(module.Code) }).ToArray()
                }));
            }

            if ((command.ToolId ?? string.Empty).EndsWith(".vba_read_module", StringComparison.OrdinalIgnoreCase))
            {
                var moduleName = Argument(command, "moduleName", "Module1");
                FakeVbaModule module;
                if (!_vbaModules.TryGetValue(moduleName, out module))
                {
                    return ToolResult.Fail("VBA module not found: " + moduleName, null, "vba_module_not_found", true);
                }

                return ToolResult.Ok("read " + command.ToolId, JsonConvert.SerializeObject(new
                {
                    name = module.Name,
                    code = module.Code,
                    type = module.Type,
                    lineCount = LineCount(module.Code),
                    codeSha256 = VbaToolManifestParser.CodeSha256(module.Code)
                }));
            }

            if ((command.ToolId ?? string.Empty).EndsWith(".vba_replace_module", StringComparison.OrdinalIgnoreCase))
            {
                SetVbaModule(Argument(command, "moduleName", "Module1"), Argument(command, "code", string.Empty), VbaModuleType);
                return ToolResult.Ok("replaced " + command.ToolId);
            }

            if ((command.ToolId ?? string.Empty).EndsWith(".insert_vba_module", StringComparison.OrdinalIgnoreCase))
            {
                SetVbaModule(Argument(command, "moduleName", "Module1"), Argument(command, "code", string.Empty), VbaModuleType);
                return ToolResult.Ok("inserted " + command.ToolId);
            }

            if ((command.ToolId ?? string.Empty).EndsWith(".run_macro", StringComparison.OrdinalIgnoreCase))
            {
                RanMacros.Add(Argument(command, "macroName", string.Empty));
                return ToolResult.Ok("ran " + command.ToolId, JsonConvert.SerializeObject(new { output = "fake-vba-result" }));
            }

            if ((command.ToolId ?? string.Empty).EndsWith(".vba_install_package_internal", StringComparison.OrdinalIgnoreCase))
            {
                var marker = Argument(command, "marker", string.Empty);
                foreach (var component in JArray.Parse(Argument(command, "componentsJson", "[]")).OfType<JObject>())
                {
                    SetVbaModule((string)component["name"], "' " + marker + "\n" + ((string)component["code"] ?? string.Empty), (string)component["type"] ?? "StdModule");
                }
                return ToolResult.Ok("fake VBA package installed");
            }

            if ((command.ToolId ?? string.Empty).EndsWith(".vba_remove_package_internal", StringComparison.OrdinalIgnoreCase))
            {
                var expected = JObject.Parse(Argument(command, "expectedComponentsJson", "{}"));
                var expectedMarker = Argument(command, "expectedMarker", string.Empty);
                foreach (var property in expected.Properties())
                {
                    FakeVbaModule module;
                    if (_vbaModules.TryGetValue(property.Name, out module) && module.Code.IndexOf(expectedMarker, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        return ToolResult.Fail("not owned", null, "vba_component_not_owned", false);
                    }
                    if (_vbaModules.TryGetValue(property.Name, out module) && !string.Equals(VbaToolManifestParser.CodeSha256(module.Code), (string)property.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        return ToolResult.Fail("modified", null, "vba_component_modified", false);
                    }
                }
                foreach (var property in expected.Properties()) _vbaModules.Remove(property.Name);
                return ToolResult.Ok("fake VBA package removed");
            }

            if ((command.ToolId ?? string.Empty).EndsWith(".vba_create_module_internal", StringComparison.OrdinalIgnoreCase))
            {
                var name = Argument(command, "moduleName", "Module1");
                if (_vbaModules.ContainsKey(name)) return ToolResult.Fail("VBA module already exists: " + name, null, "vba_module_exists", false);
                SetVbaModule(name, Argument(command, "code", string.Empty), Argument(command, "componentType", "StdModule"));
                return ToolResult.Ok("fake VBA module created");
            }

            if ((command.ToolId ?? string.Empty).EndsWith(".vba_delete_module_internal", StringComparison.OrdinalIgnoreCase))
            {
                _vbaModules.Remove(Argument(command, "moduleName", "Module1"));
                return ToolResult.Ok("fake VBA module deleted");
            }

            if (FailUnknownSkills && !IsKnownTool(command.ToolId))
            {
                return ToolResult.Fail("Unsupported " + HostName + " tool: " + command.ToolId);
            }

            return ToolResult.Ok("executed " + command.ToolId, JsonConvert.SerializeObject(new { host = HostName, toolId = command.ToolId }));
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
            else if (string.Equals(_hostName, "PowerPoint", StringComparison.OrdinalIgnoreCase))
            {
                _slides.Add(new FakeSlide { Title = "Q2 Results", Body = "Revenue grew 18%; retention needs focus." });
            }
        }

        private ToolResult ExecuteStatefulTool(ToolCommand command)
        {
            var toolId = command == null ? string.Empty : command.ToolId ?? string.Empty;
            if (toolId.StartsWith("excel.", StringComparison.OrdinalIgnoreCase))
            {
                return ExecuteExcelTool(command);
            }

            if (toolId.StartsWith("word.", StringComparison.OrdinalIgnoreCase))
            {
                return ExecuteWordTool(command);
            }

            if (toolId.StartsWith("powerpoint.", StringComparison.OrdinalIgnoreCase))
            {
                return ExecutePowerPointTool(command);
            }

            if (toolId.StartsWith("outlook.", StringComparison.OrdinalIgnoreCase))
            {
                return ExecuteOutlookTool(command);
            }

            return null;
        }

        private ToolResult ExecuteExcelTool(ToolCommand command)
        {
            if (string.Equals(command.ToolId, "excel.get_context", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Ok("context", JsonConvert.SerializeObject(GetOfficeContext()));
            }

            if (string.Equals(command.ToolId, "excel.get_selection", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Ok("selection", JsonConvert.SerializeObject(new { sheet = "Data", address = "A1:B4", values = ReadRange("Data", "A1:B4") }));
            }

            if (string.Equals(command.ToolId, "excel.add_sheet", StringComparison.OrdinalIgnoreCase))
            {
                var name = Argument(command, "name", "Sheet" + (_sheets.Count + 1));
                EnsureSheet(name);
                return ToolResult.Ok("added sheet " + name, JsonConvert.SerializeObject(new { sheet = name }));
            }

            if (string.Equals(command.ToolId, "excel.write_table", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command.ToolId, "excel.write_range", StringComparison.OrdinalIgnoreCase))
            {
                var sheetName = Argument(command, "sheet", "Sheet1");
                var startAddress = Argument(command, "startAddress", Argument(command, "address", "A1"));
                var values = ReadMatrix(command.Arguments.ContainsKey("values") ? command.Arguments["values"] : null);
                WriteMatrix(sheetName, startAddress, values);
                return ToolResult.Ok("wrote " + values.Count + " row(s) to " + sheetName, JsonConvert.SerializeObject(new { sheet = sheetName, startAddress = startAddress, values = values }));
            }

            if (string.Equals(command.ToolId, "excel.read_range", StringComparison.OrdinalIgnoreCase))
            {
                var sheetName = Argument(command, "sheet", "Sheet1");
                var range = Argument(command, "range", Argument(command, "address", "A1:B10"));
                var values = ReadRange(sheetName, range);
                return ToolResult.Ok("read range " + sheetName + "!" + range, JsonConvert.SerializeObject(new { sheet = sheetName, range = range, values = values }));
            }

            if (string.Equals(command.ToolId, "excel.read_formula_range", StringComparison.OrdinalIgnoreCase))
            {
                var sheetName = Argument(command, "sheet", "Sheet1");
                var range = Argument(command, "address", "A1:B10");
                return ToolResult.Ok("read formulas " + sheetName + "!" + range, JsonConvert.SerializeObject(new { sheet = sheetName, range = range, formulas = ReadRange(sheetName, range) }));
            }

            if (string.Equals(command.ToolId, "excel.find_cells", StringComparison.OrdinalIgnoreCase))
            {
                var query = Argument(command, "query", string.Empty);
                var matches = new List<object>();
                foreach (var sheet in _sheets)
                {
                    foreach (var cell in sheet.Value.Cells)
                    {
                        if (cell.Value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            matches.Add(new { sheet = sheet.Key, address = cell.Key, value = cell.Value });
                        }
                    }
                }
                return ToolResult.Ok("found " + matches.Count + " cell(s)", JsonConvert.SerializeObject(matches));
            }

            if (string.Equals(command.ToolId, "excel.add_chart", StringComparison.OrdinalIgnoreCase))
            {
                var sheetName = Argument(command, "sheet", "Sheet1");
                var sheet = EnsureSheet(sheetName);
                var chart = new FakeChart
                {
                    Name = Argument(command, "chartName", "Chart " + (sheet.Charts.Count + 1)),
                    SourceRange = Argument(command, "sourceRange", string.Empty),
                    ChartType = Argument(command, "chartType", string.Empty),
                    Title = Argument(command, "title", string.Empty)
                };
                sheet.Charts.Add(chart);
                return ToolResult.Ok("added chart " + chart.Title, JsonConvert.SerializeObject(chart));
            }

            if (string.Equals(command.ToolId, "excel.list_charts", StringComparison.OrdinalIgnoreCase))
            {
                var sheetFilter = Argument(command, "sheet", string.Empty);
                var charts = _sheets
                    .Where(pair => string.IsNullOrWhiteSpace(sheetFilter) || string.Equals(pair.Key, sheetFilter, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(pair => pair.Value.Charts.Select(c => new { sheet = pair.Key, name = c.Name, title = c.Title, sourceRange = c.SourceRange, chartType = c.ChartType }))
                    .ToArray();
                return ToolResult.Ok("listed " + charts.Length + " chart(s)", JsonConvert.SerializeObject(charts));
            }

            if (string.Equals(command.ToolId, "excel.get_chart", StringComparison.OrdinalIgnoreCase))
            {
                FakeSheet chartSheet;
                FakeChart chart;
                if (!TryFindChart(Argument(command, "sheet", string.Empty), Argument(command, "chartName", string.Empty), out chartSheet, out chart))
                {
                    return ToolResult.Fail("Chart not found: " + Argument(command, "chartName", string.Empty));
                }
                return ToolResult.Ok("read chart " + chart.Name, JsonConvert.SerializeObject(new { sheet = chartSheet.Name, name = chart.Name, title = chart.Title, sourceRange = chart.SourceRange, chartType = chart.ChartType }));
            }

            if (string.Equals(command.ToolId, "excel.update_chart", StringComparison.OrdinalIgnoreCase))
            {
                FakeSheet chartSheet;
                FakeChart chart;
                if (!TryFindChart(Argument(command, "sheet", string.Empty), Argument(command, "chartName", string.Empty), out chartSheet, out chart))
                {
                    return ToolResult.Fail("Chart not found: " + Argument(command, "chartName", string.Empty));
                }
                if (command.Arguments.ContainsKey("sourceRange"))
                {
                    chart.SourceRange = Argument(command, "sourceRange", chart.SourceRange);
                }
                if (command.Arguments.ContainsKey("chartType"))
                {
                    chart.ChartType = Argument(command, "chartType", chart.ChartType);
                }
                if (command.Arguments.ContainsKey("title"))
                {
                    chart.Title = Argument(command, "title", chart.Title);
                }
                return ToolResult.Ok("updated chart " + chart.Name, JsonConvert.SerializeObject(new { sheet = chartSheet.Name, name = chart.Name, title = chart.Title, sourceRange = chart.SourceRange, chartType = chart.ChartType }));
            }

            if (string.Equals(command.ToolId, "excel.delete_chart", StringComparison.OrdinalIgnoreCase))
            {
                FakeSheet chartSheet;
                FakeChart chart;
                if (!TryFindChart(Argument(command, "sheet", string.Empty), Argument(command, "chartName", string.Empty), out chartSheet, out chart))
                {
                    return ToolResult.Fail("Chart not found: " + Argument(command, "chartName", string.Empty));
                }
                chartSheet.Charts.Remove(chart);
                return ToolResult.Ok("deleted chart " + chart.Name);
            }

            if (string.Equals(command.ToolId, "excel.add_table", StringComparison.OrdinalIgnoreCase))
            {
                var sheetName = Argument(command, "sheet", "Sheet1");
                var sheet = EnsureSheet(sheetName);
                var name = Argument(command, "name", "Table" + (sheet.Tables.Count + 1));
                sheet.Tables.Add(name);
                return ToolResult.Ok("added table " + name, JsonConvert.SerializeObject(new { sheet = sheetName, name = name, range = Argument(command, "sourceRange", "A1:B2") }));
            }

            if (string.Equals(command.ToolId, "excel.list_tables", StringComparison.OrdinalIgnoreCase))
            {
                var tables = _sheets.SelectMany(pair => pair.Value.Tables.Select(t => new { sheet = pair.Key, name = t })).ToArray();
                return ToolResult.Ok("listed " + tables.Length + " table(s)", JsonConvert.SerializeObject(tables));
            }

            if (string.Equals(command.ToolId, "excel.list_names", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command.ToolId, "excel.list_shapes", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command.ToolId, "excel.create_chat_chart", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Ok("listed " + command.ToolId, "[]");
            }

            if (string.Equals(command.ToolId, "excel.list_sheets", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Ok("listed " + _sheets.Count + " sheet(s)", JsonConvert.SerializeObject(_sheets.Keys.ToArray()));
            }

            if (string.Equals(command.ToolId, "excel.workbook_summary", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command.ToolId, "excel.profile_range", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Ok("workbook summary", BuildWorkbookSummary());
            }

            if (string.Equals(command.ToolId, "excel.set_formula", StringComparison.OrdinalIgnoreCase))
            {
                var sheetName = Argument(command, "sheet", "Sheet1");
                var address = Argument(command, "address", "A1");
                WriteMatrix(sheetName, address, new List<List<string>> { new List<string> { Argument(command, "formula", string.Empty) } });
                return ToolResult.Ok("set formula " + sheetName + "!" + address);
            }

            if (string.Equals(command.ToolId, "excel.rename_sheet", StringComparison.OrdinalIgnoreCase))
            {
                var oldName = Argument(command, "sheet", "Sheet1");
                var newName = Argument(command, "newName", string.Empty);
                if (string.IsNullOrWhiteSpace(newName))
                {
                    return ToolResult.Fail("newName is required.");
                }
                var sheet = EnsureSheet(oldName);
                _sheets.Remove(oldName);
                sheet.Name = newName;
                _sheets[newName] = sheet;
                return ToolResult.Ok("renamed sheet " + oldName + " to " + newName);
            }

            if (string.Equals(command.ToolId, "excel.clear_range", StringComparison.OrdinalIgnoreCase))
            {
                ClearRange(Argument(command, "sheet", "Sheet1"), Argument(command, "address", "A1:A1"));
                return ToolResult.Ok("cleared range");
            }

            if (string.Equals(command.ToolId, "excel.sort_range", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command.ToolId, "excel.filter_range", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command.ToolId, "excel.format_range", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Ok("applied " + command.ToolId);
            }

            if (string.Equals(command.ToolId, "excel.autofit", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Ok("autofit " + Argument(command, "sheet", "Sheet1"));
            }

            return null;
        }

        private ToolResult ExecuteWordTool(ToolCommand command)
        {
            if (string.Equals(command.ToolId, "word.read_document", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command.ToolId, "word.read_selection", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command.ToolId, "word.get_selection_text", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command.ToolId, "word.read_range", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Ok("read Word text", JsonConvert.SerializeObject(new { text = _wordText, comments = _wordComments.ToArray() }));
            }

            if (string.Equals(command.ToolId, "word.find_text", StringComparison.OrdinalIgnoreCase))
            {
                var query = Argument(command, "query", string.Empty);
                var index = _wordText.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                return ToolResult.Ok("found Word text", JsonConvert.SerializeObject(index < 0 ? new object[0] : new[] { new { start = index, end = index + query.Length } }));
            }

            if (string.Equals(command.ToolId, "word.read_headings", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command.ToolId, "word.read_tables", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command.ToolId, "word.list_comments", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command.ToolId, "word.document_stats", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Ok("read Word metadata", JsonConvert.SerializeObject(new { text = _wordText, comments = _wordComments.ToArray() }));
            }

            if (string.Equals(command.ToolId, "word.insert_text", StringComparison.OrdinalIgnoreCase))
            {
                var text = Argument(command, "text", string.Empty);
                _wordText += text;
                return ToolResult.Ok("inserted Word text", JsonConvert.SerializeObject(new { text = _wordText }));
            }

            if (string.Equals(command.ToolId, "word.insert_paragraph", StringComparison.OrdinalIgnoreCase))
            {
                _wordText += Environment.NewLine + Argument(command, "text", string.Empty);
                return ToolResult.Ok("inserted Word paragraph", JsonConvert.SerializeObject(new { text = _wordText }));
            }

            if (string.Equals(command.ToolId, "word.replace_selection", StringComparison.OrdinalIgnoreCase))
            {
                _wordText = Argument(command, "text", string.Empty);
                return ToolResult.Ok("replaced Word selection", JsonConvert.SerializeObject(new { text = _wordText }));
            }

            if (string.Equals(command.ToolId, "word.replace_text", StringComparison.OrdinalIgnoreCase))
            {
                _wordText = _wordText.Replace(Argument(command, "find", string.Empty), Argument(command, "replace", string.Empty));
                return ToolResult.Ok("replaced Word text", JsonConvert.SerializeObject(new { text = _wordText }));
            }

            if (string.Equals(command.ToolId, "word.apply_style", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command.ToolId, "word.format_selection", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command.ToolId, "word.add_table", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command.ToolId, "word.insert_page_break", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Ok("applied " + command.ToolId);
            }

            if (string.Equals(command.ToolId, "word.add_comment", StringComparison.OrdinalIgnoreCase))
            {
                var text = Argument(command, "text", string.Empty);
                _wordComments.Add(text);
                return ToolResult.Ok("added Word comment", JsonConvert.SerializeObject(new { comments = _wordComments.ToArray() }));
            }

            return null;
        }

        private ToolResult ExecutePowerPointTool(ToolCommand command)
        {
            if (string.Equals(command.ToolId, "powerpoint.get_selection", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command.ToolId, "powerpoint.read_slides", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command.ToolId, "powerpoint.read_slide", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command.ToolId, "powerpoint.list_slides", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command.ToolId, "powerpoint.list_shapes", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command.ToolId, "powerpoint.read_speaker_notes", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Ok("read slides", JsonConvert.SerializeObject(_slides.Select(s => new { title = s.Title, body = s.Body, notes = s.Notes }).ToArray()));
            }

            if (string.Equals(command.ToolId, "powerpoint.add_slide", StringComparison.OrdinalIgnoreCase))
            {
                var slide = new FakeSlide
                {
                    Title = Argument(command, "title", string.Empty),
                    Body = Argument(command, "body", string.Empty)
                };
                _slides.Add(slide);
                return ToolResult.Ok("added slide " + slide.Title, JsonConvert.SerializeObject(slide));
            }

            if (string.Equals(command.ToolId, "powerpoint.replace_selection_text", StringComparison.OrdinalIgnoreCase))
            {
                if (_slides.Count == 0)
                {
                    _slides.Add(new FakeSlide());
                }

                _slides[_slides.Count - 1].Body = Argument(command, "text", string.Empty);
                return ToolResult.Ok("replaced slide selection", JsonConvert.SerializeObject(_slides[_slides.Count - 1]));
            }

            if (string.Equals(command.ToolId, "powerpoint.set_speaker_notes", StringComparison.OrdinalIgnoreCase))
            {
                var slide = LastOrNewSlide();
                slide.Notes = Argument(command, "notes", string.Empty);
                return ToolResult.Ok("set notes", JsonConvert.SerializeObject(slide));
            }

            if (string.Equals(command.ToolId, "powerpoint.add_text_box", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command.ToolId, "powerpoint.set_shape_text", StringComparison.OrdinalIgnoreCase))
            {
                var slide = LastOrNewSlide();
                slide.Body = Argument(command, "text", slide.Body ?? string.Empty);
                return ToolResult.Ok("set slide shape text", JsonConvert.SerializeObject(slide));
            }

            if (string.Equals(command.ToolId, "powerpoint.add_picture", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command.ToolId, "powerpoint.add_table", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Ok("added slide object", JsonConvert.SerializeObject(new { slideCount = _slides.Count }));
            }

            if (string.Equals(command.ToolId, "powerpoint.duplicate_slide", StringComparison.OrdinalIgnoreCase))
            {
                var slide = LastOrNewSlide();
                _slides.Add(new FakeSlide { Title = slide.Title, Body = slide.Body, Notes = slide.Notes });
                return ToolResult.Ok("duplicated slide", JsonConvert.SerializeObject(new { slideCount = _slides.Count }));
            }

            if (string.Equals(command.ToolId, "powerpoint.move_slide", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Ok("moved slide");
            }

            return null;
        }

        private ToolResult ExecuteOutlookTool(ToolCommand command)
        {
            if (string.Equals(command.ToolId, "outlook.read_selection", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command.ToolId, "outlook.read_current_mail", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command.ToolId, "outlook.read_mail_by_entry_id", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Ok("read selected mail", JsonConvert.SerializeObject(new { text = _outlookSelection }));
            }

            if (string.Equals(command.ToolId, "outlook.search_mail", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command.ToolId, "outlook.list_attachments", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Ok("read Outlook metadata", JsonConvert.SerializeObject(new { selection = _outlookSelection }));
            }

            if (string.Equals(command.ToolId, "outlook.create_reply_draft", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command.ToolId, "outlook.create_reply_all_draft", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command.ToolId, "outlook.create_forward_draft", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command.ToolId, "outlook.create_mail_draft", StringComparison.OrdinalIgnoreCase))
            {
                _outlookDraft = Argument(command, "body", string.Empty);
                return ToolResult.Ok("drafted reply", JsonConvert.SerializeObject(new { body = _outlookDraft }));
            }

            if (string.Equals(command.ToolId, "outlook.set_categories", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command.ToolId, "outlook.mark_as_read", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Ok("updated Outlook mail");
            }

            if (string.Equals(command.ToolId, "outlook.collect_folder_mail", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command.ToolId, "outlook.collect_monthly_summary_data", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Ok("collected Outlook data", JsonConvert.SerializeObject(new { selection = _outlookSelection }));
            }

            return null;
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
                    sheet.Cells.Remove(CellKey(row, column));
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
                    string value;
                    line.Add(sheet.Cells.TryGetValue(CellKey(row, column), out value) ? value : string.Empty);
                }

                values.Add(line);
            }

            return values;
        }

        private static List<List<string>> ReadMatrix(object raw)
        {
            var values = new List<List<string>>();
            if (raw == null)
            {
                return values;
            }

            JToken token;
            if (raw is JToken)
            {
                token = (JToken)raw;
            }
            else
            {
                var text = Convert.ToString(raw);
                token = string.IsNullOrWhiteSpace(text) ? new JArray() : JToken.Parse(text);
            }

            var rows = token as JArray;
            if (rows == null)
            {
                values.Add(new List<string> { Convert.ToString(token) });
                return values;
            }

            foreach (var rowToken in rows)
            {
                var rowArray = rowToken as JArray;
                if (rowArray == null)
                {
                    values.Add(new List<string> { Convert.ToString(rowToken) });
                    continue;
                }

                values.Add(rowArray.Select(cell => Convert.ToString(cell)).ToList());
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
                tables = s.Tables.ToArray(),
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
                _slides.Add(new FakeSlide());
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

        private static string Argument(ToolCommand command, string name, string fallback)
        {
            object value;
            return command != null && command.Arguments != null && command.Arguments.TryGetValue(name, out value) && value != null
                ? Convert.ToString(value)
                : fallback;
        }

        private bool TryDequeueResult(string toolId, out ToolResult result)
        {
            result = null;
            Queue<ToolResult> queue;
            if (!_scriptedResults.TryGetValue(toolId ?? string.Empty, out queue) || queue.Count == 0)
            {
                return false;
            }

            result = queue.Dequeue();
            return true;
        }

        private bool IsKnownTool(string toolId)
        {
            return _builtInTools.Any(tool => string.Equals(tool.Id, toolId, StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<ToolDefinition> ExcelBuiltIns()
        {
            return new[]
            {
                BuiltIn("Excel", "excel.get_context", false, false, true),
                BuiltIn("Excel", "excel.get_selection", false, false, true),
                BuiltIn("Excel", "excel.workbook_summary", false, false, true),
                BuiltIn("Excel", "excel.list_sheets", false, false, true),
                BuiltIn("Excel", "excel.read_range", false, false, true),
                BuiltIn("Excel", "excel.read_formula_range", false, false, true),
                BuiltIn("Excel", "excel.profile_range", false, false, true),
                BuiltIn("Excel", "excel.find_cells", false, false, true),
                BuiltIn("Excel", "excel.create_chat_chart", false, false, true),
                BuiltIn("Excel", "excel.list_charts", false, false, true),
                BuiltIn("Excel", "excel.get_chart", false, false, true),
                BuiltIn("Excel", "excel.list_tables", false, false, true),
                BuiltIn("Excel", "excel.list_names", false, false, true),
                BuiltIn("Excel", "excel.list_shapes", false, false, true),
                BuiltIn("Excel", "excel.write_range", false, true, true),
                BuiltIn("Excel", "excel.write_table", false, true, true),
                BuiltIn("Excel", "excel.set_formula", false, true, true),
                BuiltIn("Excel", "excel.add_table", false, true, true),
                BuiltIn("Excel", "excel.add_chart", false, true, true),
                BuiltIn("Excel", "excel.update_chart", false, true, true),
                BuiltIn("Excel", "excel.delete_chart", false, true, false, 3),
                BuiltIn("Excel", "excel.format_range", false, true, true, 1),
                BuiltIn("Excel", "excel.autofit", false, true, true, 1),
                BuiltIn("Excel", "excel.add_sheet", false, true, true, 1),
                BuiltIn("Excel", "excel.rename_sheet", false, true, false),
                BuiltIn("Excel", "excel.clear_range", false, true, false, 3),
                BuiltIn("Excel", "excel.sort_range", false, true, false),
                BuiltIn("Excel", "excel.filter_range", false, true, false),
                BuiltIn("Excel", "excel.vba_read_module", false, false, true),
                BuiltIn("Excel", "excel.vba_replace_module", false, true, false, 3),
                BuiltIn("Excel", "excel.insert_vba_module", false, true, false, 3),
                BuiltIn("Excel", "excel.run_macro", false, true, false, 3)
            };
        }

        private static IEnumerable<ToolDefinition> WordBuiltIns()
        {
            return new[]
            {
                BuiltIn("Word", "word.get_context", false, false, true),
                BuiltIn("Word", "word.get_selection_text", false, false, true),
                BuiltIn("Word", "word.read_document", false, false, true),
                BuiltIn("Word", "word.read_selection", false, false, true),
                BuiltIn("Word", "word.read_range", false, false, true),
                BuiltIn("Word", "word.find_text", false, false, true),
                BuiltIn("Word", "word.read_headings", false, false, true),
                BuiltIn("Word", "word.read_tables", false, false, true),
                BuiltIn("Word", "word.list_comments", false, false, true),
                BuiltIn("Word", "word.document_stats", false, false, true),
                BuiltIn("Word", "word.insert_text", false, true, true),
                BuiltIn("Word", "word.insert_paragraph", false, true, true),
                BuiltIn("Word", "word.replace_selection", false, true, true),
                BuiltIn("Word", "word.replace_text", false, true, true),
                BuiltIn("Word", "word.apply_style", false, true, true, 1),
                BuiltIn("Word", "word.format_selection", false, true, true, 1),
                BuiltIn("Word", "word.add_table", false, true, true),
                BuiltIn("Word", "word.insert_page_break", false, true, true, 1),
                BuiltIn("Word", "word.add_comment", false, true, true, 1),
                BuiltIn("Word", "word.vba_read_module", false, false, true),
                BuiltIn("Word", "word.vba_replace_module", false, true, false, 3),
                BuiltIn("Word", "word.insert_vba_module", false, true, false, 3),
                BuiltIn("Word", "word.run_macro", false, true, false, 3)
            };
        }

        private static IEnumerable<ToolDefinition> PowerPointBuiltIns()
        {
            return new[]
            {
                BuiltIn("PowerPoint", "powerpoint.get_context", false, false, true),
                BuiltIn("PowerPoint", "powerpoint.get_selection", false, false, true),
                BuiltIn("PowerPoint", "powerpoint.read_slides", false, false, true),
                BuiltIn("PowerPoint", "powerpoint.read_slide", false, false, true),
                BuiltIn("PowerPoint", "powerpoint.list_slides", false, false, true),
                BuiltIn("PowerPoint", "powerpoint.list_shapes", false, false, true),
                BuiltIn("PowerPoint", "powerpoint.read_speaker_notes", false, false, true),
                BuiltIn("PowerPoint", "powerpoint.add_slide", false, true, true, 1),
                BuiltIn("PowerPoint", "powerpoint.replace_selection_text", false, true, true),
                BuiltIn("PowerPoint", "powerpoint.set_speaker_notes", false, true, true, 1),
                BuiltIn("PowerPoint", "powerpoint.add_text_box", false, true, true, 1),
                BuiltIn("PowerPoint", "powerpoint.set_shape_text", false, true, true),
                BuiltIn("PowerPoint", "powerpoint.add_picture", false, true, true, 1),
                BuiltIn("PowerPoint", "powerpoint.add_table", false, true, true, 1),
                BuiltIn("PowerPoint", "powerpoint.duplicate_slide", false, true, true, 1),
                BuiltIn("PowerPoint", "powerpoint.move_slide", false, true, false),
                BuiltIn("PowerPoint", "powerpoint.vba_read_module", false, false, true),
                BuiltIn("PowerPoint", "powerpoint.vba_replace_module", false, true, false, 3),
                BuiltIn("PowerPoint", "powerpoint.insert_vba_module", false, true, false, 3),
                BuiltIn("PowerPoint", "powerpoint.run_macro", false, true, false, 3)
            };
        }

        private static IEnumerable<ToolDefinition> OutlookBuiltIns()
        {
            return new[]
            {
                BuiltIn("Outlook", "outlook.get_context", false, false, true),
                BuiltIn("Outlook", "outlook.read_current_mail", false, false, true),
                BuiltIn("Outlook", "outlook.read_selection", false, false, true),
                BuiltIn("Outlook", "outlook.read_mail_by_entry_id", false, false, true),
                BuiltIn("Outlook", "outlook.search_mail", false, false, true),
                BuiltIn("Outlook", "outlook.list_attachments", false, false, true),
                BuiltIn("Outlook", "outlook.create_mail_draft", false, true, true, 1),
                BuiltIn("Outlook", "outlook.create_reply_draft", false, true, true, 1),
                BuiltIn("Outlook", "outlook.create_reply_all_draft", false, true, true, 1),
                BuiltIn("Outlook", "outlook.create_forward_draft", false, true, true, 1),
                BuiltIn("Outlook", "outlook.set_categories", false, true, false, 1),
                BuiltIn("Outlook", "outlook.mark_as_read", false, true, false, 1),
                BuiltIn("Outlook", "outlook.collect_folder_mail", false, false, true),
                BuiltIn("Outlook", "outlook.collect_monthly_summary_data", false, false, true)
            };
        }

        private static ToolDefinition BuiltIn(string host, string id, bool requiresConfirmation, bool mutatesDocument, bool agentCanRun, int riskLevel = 0)
        {
            return new ToolDefinition
            {
                Id = id,
                Host = host,
                Name = id,
                Description = (mutatesDocument ? "Mutates document: " : "Read-only: ") + id,
                ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":true}",
                Enabled = true,
                BuiltIn = true,
                RequiresConfirmation = requiresConfirmation,
                MutatesDocument = mutatesDocument,
                AgentCanRun = agentCanRun,
                RiskLevel = mutatesDocument && riskLevel <= 0 ? 2 : riskLevel
            };
        }

        private static SkillDefinition BuiltInSkill(string id, string host, string name, string description, string[] tags)
        {
            return new SkillDefinition
            {
                Id = id,
                Host = host,
                Name = name,
                Description = description,
                Tags = new List<string>(tags ?? new string[0]),
                BodyMarkdown = "# " + name,
                Enabled = true,
                BuiltIn = true
            };
        }

        private static ToolDefinition CloneTool(ToolDefinition tool)
        {
            return new ToolDefinition
            {
                Id = tool.Id,
                Host = tool.Host,
                Name = tool.Name,
                Description = tool.Description,
                ArgumentSchemaJson = tool.ArgumentSchemaJson,
                Executor = tool.Executor,
                RequiresConfirmation = tool.RequiresConfirmation,
                MutatesDocument = tool.MutatesDocument,
                MutatesLocalState = tool.MutatesLocalState,
                AgentCanRun = tool.AgentCanRun,
                PipelineJson = tool.PipelineJson,
                Code = tool.Code,
                Readme = tool.Readme,
                StoragePath = tool.StoragePath,
                Enabled = tool.Enabled,
                BuiltIn = tool.BuiltIn,
                RiskLevel = tool.RiskLevel,
                UseWhen = tool.UseWhen,
                DoNotUseWhen = tool.DoNotUseWhen,
                ExamplesJson = tool.ExamplesJson,
                VerifyJson = tool.VerifyJson,
                CapabilityStatus = tool.CapabilityStatus,
                Limitations = tool.Limitations
            };
        }

        private static ToolCommand Clone(ToolCommand command)
        {
            var clone = new ToolCommand { ToolId = command.ToolId, Description = command.Description };
            foreach (var pair in command.Arguments)
            {
                clone.Arguments[pair.Key] = pair.Value;
            }
            return clone;
        }

        private static string Trim(string value, int maxChars)
        {
            value = value ?? string.Empty;
            return maxChars > 0 && value.Length > maxChars ? value.Substring(0, maxChars) : value;
        }

        private static int LineCount(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0;
            return value.Replace("\r\n", "\n").Split('\n').Length;
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
            public Dictionary<string, string> Cells { get; private set; }
            public List<FakeChart> Charts { get; private set; }
            public List<string> Tables { get; private set; }

            public FakeSheet()
            {
                Cells = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Charts = new List<FakeChart>();
                Tables = new List<string>();
            }
        }

        private sealed class FakeChart
        {
            public string Name { get; set; }
            public string SourceRange { get; set; }
            public string ChartType { get; set; }
            public string Title { get; set; }
        }

        private sealed class FakeSlide
        {
            public string Title { get; set; }
            public string Body { get; set; }
            public string Notes { get; set; }
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
