using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Storage;
using RNAssistant.Office;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Office.WebView;
using RNAssistant.Desktop;
using RNAssistant.OfficeHosts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private sealed class FakeOfficeAdapter : IOfficeApplicationAdapter
        {
            public readonly List<ToolCommand> Executed = new List<ToolCommand>();
            public string VbaModuleType = "StdModule";
            public readonly List<string> RanMacros = new List<string>();
            public bool FailUnknownSkills { get; set; }
            public string DocumentKeyValue { get; set; }
            public string RuntimeDocumentKeyValue { get; set; }

            private readonly string _hostName;
            private readonly string _documentTitle;
            private readonly string _documentSnapshot;
            private readonly List<ToolDefinition> _builtInTools;
            private readonly Dictionary<string, Queue<ToolResult>> _scriptedResults;
            private readonly Dictionary<string, FakeVbaModule> _vbaModules;

            public string VbaModuleCode
            {
                get { return GetVbaModuleCode("Module1"); }
                set { SetVbaModule("Module1", value, VbaModuleType); }
            }

            public FakeOfficeAdapter()
                : this("Excel", "Harness.xlsx", ExcelBuiltIns(), "Harness document")
            {
            }

            private FakeOfficeAdapter(string hostName, string documentTitle, IEnumerable<ToolDefinition> builtInSkills, string documentSnapshot)
            {
                _hostName = hostName;
                _documentTitle = documentTitle;
                _documentSnapshot = documentSnapshot;
                _builtInTools = new List<ToolDefinition>((builtInSkills ?? new ToolDefinition[0]).Select(CloneTool));
                _scriptedResults = new Dictionary<string, Queue<ToolResult>>(StringComparer.OrdinalIgnoreCase);
                _vbaModules = new Dictionary<string, FakeVbaModule>(StringComparer.OrdinalIgnoreCase);
                DocumentKeyValue = "doc";
                RuntimeDocumentKeyValue = "runtime-doc";
            }

            public static FakeOfficeAdapter ForHost(string host)
            {
                if (string.Equals(host, "Word", StringComparison.OrdinalIgnoreCase))
                {
                    return new FakeOfficeAdapter("Word", "Harness.docx", WordBuiltIns(), "Harness Word document");
                }

                if (string.Equals(host, "PowerPoint", StringComparison.OrdinalIgnoreCase))
                {
                    return new FakeOfficeAdapter("PowerPoint", "Harness.pptx", PowerPointBuiltIns(), "Harness slide deck");
                }

                if (string.Equals(host, "Outlook", StringComparison.OrdinalIgnoreCase))
                {
                    return new FakeOfficeAdapter("Outlook", "Selected mail", OutlookBuiltIns(), "Subject: Harness mail");
                }

                return new FakeOfficeAdapter("Excel", "Harness.xlsx", ExcelBuiltIns(), "Harness workbook");
            }

            public string HostName { get { return _hostName; } }
            public string DocumentKey { get { return DocumentKeyValue; } }
            public string LegacyDocumentKey { get { return "legacy-doc"; } }
            public string RuntimeDocumentKey { get { return RuntimeDocumentKeyValue; } }
            public string DocumentTitle { get { return _documentTitle; } }

            public string GetDocumentSnapshot(int maxChars)
            {
                return _documentSnapshot;
            }

            public string GetVbaSnapshot(int maxChars)
            {
                return string.Join("\n", _vbaModules.Values.Select(module => module.Name + " (" + module.Type + "): " + module.Code.Length + " chars").ToArray());
            }

            public void PrepareForContextCapture()
            {
            }

            public ContextNote CaptureSelectionContext(string mode, int maxChars)
            {
                return null;
            }

            public IEnumerable<ToolDefinition> GetBuiltInTools()
            {
                return _builtInTools.Select(CloneTool).ToArray();
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

            public string GetVbaModuleCode(string moduleName)
            {
                FakeVbaModule module;
                return _vbaModules.TryGetValue(string.IsNullOrWhiteSpace(moduleName) ? "Module1" : moduleName, out module)
                    ? module.Code
                    : string.Empty;
            }

            public ToolResult ExecuteTool(ToolCommand command)
            {
                Executed.Add(Clone(command));
                ToolResult scripted;
                if (TryDequeueResult(command.ToolId, out scripted))
                {
                    return scripted;
                }

                if ((command.ToolId ?? string.Empty).EndsWith(".vba_read_module", StringComparison.OrdinalIgnoreCase))
                {
                    var moduleName = Argument(command, "moduleName", "Module1");
                    FakeVbaModule module;
                    if (!_vbaModules.TryGetValue(moduleName, out module))
                    {
                        return ToolResult.Fail("VBA module not found: " + moduleName);
                    }

                    return ToolResult.Ok("read " + command.ToolId, JsonConvert.SerializeObject(new { name = module.Name, code = module.Code, type = module.Type }));
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
                    return ToolResult.Ok("ran " + command.ToolId);
                }

                if (FailUnknownSkills && !IsKnownTool(command.ToolId))
                {
                    return ToolResult.Fail("Unsupported " + HostName + " tool: " + command.ToolId);
                }

                return ToolResult.Ok("executed " + command.ToolId, JsonConvert.SerializeObject(new { host = HostName, toolId = command.ToolId }));
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
                    BuiltIn("Excel", "excel.workbook_summary", false, false, true),
                    BuiltIn("Excel", "excel.list_sheets", false, false, true),
                    BuiltIn("Excel", "excel.read_range", false, false, true),
                    BuiltIn("Excel", "excel.profile_range", false, false, true),
                    BuiltIn("Excel", "excel.list_charts", false, false, true),
                    BuiltIn("Excel", "excel.write_range", false, true, true),
                    BuiltIn("Excel", "excel.write_table", false, true, true),
                    BuiltIn("Excel", "excel.add_chart", false, true, true),
                    BuiltIn("Excel", "excel.autofit", false, true, true),
                    BuiltIn("Excel", "excel.add_sheet", false, true, true),
                    BuiltIn("Excel", "excel.vba_read_project", false, false, true),
                    BuiltIn("Excel", "excel.vba_read_module", false, false, true),
                    BuiltIn("Excel", "excel.vba_replace_module", false, true, false),
                    BuiltIn("Excel", "excel.insert_vba_module", false, true, false),
                    BuiltIn("Excel", "excel.run_macro", false, true, false)
                };
            }

            private static IEnumerable<ToolDefinition> WordBuiltIns()
            {
                return new[]
                {
                    BuiltIn("Word", "word.read_document", false, false, true),
                    BuiltIn("Word", "word.read_selection", false, false, true),
                    BuiltIn("Word", "word.insert_text", false, true, true),
                    BuiltIn("Word", "word.replace_selection", false, true, true),
                    BuiltIn("Word", "word.add_comment", false, true, true),
                    BuiltIn("Word", "word.vba_read_project", false, false, true),
                    BuiltIn("Word", "word.vba_read_module", false, false, true),
                    BuiltIn("Word", "word.vba_replace_module", false, true, false),
                    BuiltIn("Word", "word.insert_vba_module", false, true, false),
                    BuiltIn("Word", "word.run_macro", false, true, false)
                };
            }

            private static IEnumerable<ToolDefinition> PowerPointBuiltIns()
            {
                return new[]
                {
                    BuiltIn("PowerPoint", "powerpoint.read_slides", false, false, true),
                    BuiltIn("PowerPoint", "powerpoint.add_slide", false, true, true),
                    BuiltIn("PowerPoint", "powerpoint.replace_selection_text", false, true, true),
                    BuiltIn("PowerPoint", "powerpoint.vba_read_project", false, false, true),
                    BuiltIn("PowerPoint", "powerpoint.vba_read_module", false, false, true),
                    BuiltIn("PowerPoint", "powerpoint.vba_replace_module", false, true, false),
                    BuiltIn("PowerPoint", "powerpoint.insert_vba_module", false, true, false),
                    BuiltIn("PowerPoint", "powerpoint.run_macro", false, true, false)
                };
            }

            private static IEnumerable<ToolDefinition> OutlookBuiltIns()
            {
                return new[]
                {
                    BuiltIn("Outlook", "outlook.read_selection", false, false, true),
                    BuiltIn("Outlook", "outlook.draft_reply", false, true, true),
                    BuiltIn("Outlook", "outlook.collect_folder_mail", false, false, true),
                    BuiltIn("Outlook", "outlook.collect_monthly_summary_data", false, false, true)
                };
            }

            private static ToolDefinition BuiltIn(string host, string id, bool requiresConfirmation, bool mutatesDocument, bool agentCanRun)
            {
                return new ToolDefinition
                {
                    Id = id,
                    Host = host,
                    Name = id,
                    Enabled = true,
                    BuiltIn = true,
                    RequiresConfirmation = requiresConfirmation,
                    MutatesDocument = mutatesDocument,
                    AgentCanRun = agentCanRun
                };
            }

            private static ToolDefinition CloneTool(ToolDefinition skill)
            {
                return new ToolDefinition
                {
                    Id = skill.Id,
                    Host = skill.Host,
                    Name = skill.Name,
                    Description = skill.Description,
                    ArgumentSchemaJson = skill.ArgumentSchemaJson,
                    Executor = skill.Executor,
                    RequiresConfirmation = skill.RequiresConfirmation,
                    MutatesDocument = skill.MutatesDocument,
                    AgentCanRun = skill.AgentCanRun,
                    PipelineJson = skill.PipelineJson,
                    Code = skill.Code,
                    Readme = skill.Readme,
                    StoragePath = skill.StoragePath,
                    Enabled = skill.Enabled,
                    BuiltIn = skill.BuiltIn
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

            private sealed class FakeVbaModule
            {
                public string Name { get; set; }
                public string Code { get; set; }
                public string Type { get; set; }
            }
        }
    }
}
