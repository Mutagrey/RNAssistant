using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        private const string EmptyFormalToolSchema = "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}";
        private const string SheetFormalToolSchema = "{\"type\":\"object\",\"properties\":{\"sheet\":{\"type\":\"string\",\"description\":\"Worksheet name.\"}},\"required\":[\"sheet\"],\"additionalProperties\":false}";

        private static ToolDefinition CustomTool(string host, string id)
        {
            return new ToolDefinition
            {
                Id = id,
                Host = host,
                Name = id,
                Executor = "pipeline",
                ArgumentSchemaJson = EmptyFormalToolSchema,
                Enabled = true,
                BuiltIn = false,
                PipelineJson = "{\"steps\":[]}"
            };
        }

        private static bool HasTool(IEnumerable<ToolDefinition> tools, string id)
        {
            foreach (var tool in tools)
            {
                if (tool != null && string.Equals(tool.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasSkill(IEnumerable<SkillDefinition> skills, string id)
        {
            foreach (var skill in skills)
            {
                if (skill != null && string.Equals(skill.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static ToolDefinition FindTool(IEnumerable<ToolDefinition> tools, string id)
        {
            foreach (var tool in tools ?? new ToolDefinition[0])
            {
                if (tool != null && string.Equals(tool.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return tool;
                }
            }

            return null;
        }

        private static List<ToolDefinition> BuildPipelineTools(bool requiresConfirmation)
        {
            return new List<ToolDefinition>
            {
                new ToolDefinition
                {
                    Id = "excel.make_report",
                    Host = "Excel",
                    Name = "Make report",
                    Executor = "pipeline",
                    Enabled = true,
                    RequiresConfirmation = requiresConfirmation,
                    ArgumentSchemaJson = SheetFormalToolSchema,
                    PipelineJson = "{" +
                        "\"steps\":[" +
                        "{\"id\":\"sheet\",\"toolId\":\"excel.add_sheet\",\"arguments\":{\"name\":\"{{args.sheet}}\"}}," +
                        "{\"id\":\"table\",\"toolId\":\"excel.write_table\",\"arguments\":{\"sheet\":\"{{args.sheet}}\",\"startAddress\":\"A1\",\"values\":\"[[\\\"Month\\\",\\\"Sales\\\"]]\"}}" +
                        "]}"
                }
            };
        }

        private static List<ToolDefinition> BuildStepPlaceholderPipelineTools()
        {
            return new List<ToolDefinition>
            {
                new ToolDefinition
                {
                    Id = "excel.chain_report",
                    Host = "Excel",
                    Name = "Chain report",
                    Executor = "pipeline",
                    Enabled = true,
                    ArgumentSchemaJson = EmptyFormalToolSchema,
                    PipelineJson = "{" +
                        "\"steps\":[" +
                        "{\"id\":\"sheet\",\"toolId\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Report\"}}," +
                        "{\"id\":\"table\",\"toolId\":\"excel.write_table\",\"arguments\":{\"sheet\":\"Report\",\"startAddress\":\"A1\",\"values\":[[\"{{steps.sheet.message}}\",\"{{steps.sheet.success}}\"]]}}" +
                        "]}"
                }
            };
        }

        private static List<ToolDefinition> BuildThreeStepPipelineTools()
        {
            return new List<ToolDefinition>
            {
                new ToolDefinition
                {
                    Id = "excel.full_report",
                    Host = "Excel",
                    Name = "Full report",
                    Executor = "pipeline",
                    Enabled = true,
                    ArgumentSchemaJson = SheetFormalToolSchema,
                    PipelineJson = "{" +
                        "\"steps\":[" +
                        "{\"id\":\"sheet\",\"toolId\":\"excel.add_sheet\",\"arguments\":{\"name\":\"{{args.sheet}}\"}}," +
                        "{\"id\":\"table\",\"toolId\":\"excel.write_table\",\"arguments\":{\"sheet\":\"{{args.sheet}}\",\"startAddress\":\"A1\"}}," +
                        "{\"id\":\"chart\",\"toolId\":\"excel.upsert_chart\",\"arguments\":{\"sheet\":\"{{args.sheet}}\",\"sourceRange\":\"A1:B2\",\"chartType\":\"column\",\"title\":\"Report\"}}" +
                        "]}"
                }
            };
        }

        private static ToolCommand Command(string id, params object[] keyValues)
        {
            var command = new ToolCommand { ToolId = id };
            for (var i = 0; i + 1 < (keyValues == null ? 0 : keyValues.Length); i += 2)
            {
                command.Arguments[Convert.ToString(keyValues[i])] = keyValues[i + 1];
            }

            return command;
        }

        private static ChatSession NewSession(FakeOfficeAdapter adapter)
        {
            return new ChatSession
            {
                Host = adapter.HostName,
                DocumentKey = adapter.DocumentKey,
                DocumentTitle = adapter.DocumentTitle,
                Title = "New chat"
            };
        }

        private static DocumentContext NewContext(FakeOfficeAdapter adapter)
        {
            return new DocumentContext
            {
                Host = adapter.HostName,
                DocumentKey = adapter.DocumentKey,
                Title = adapter.DocumentTitle
            };
        }
    }
}
