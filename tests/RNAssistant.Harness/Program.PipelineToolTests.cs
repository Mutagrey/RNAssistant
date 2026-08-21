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
        private static void PipelineDryRunResolvesPlaceholders()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = BuildPipelineTools(false);
                var command = new ToolCommand { ToolId = "excel.make_report" };
                command.Arguments["sheet"] = "Report";

                var result = executor.Execute(command, tools, new AppSettings(), true, false);

                AssertTrue(result.Success, "pipeline dry-run result");
                AssertContains(result.Message, "Dry run completed", "dry-run message");
                AssertContains(result.DataJson, "Report", "pipeline data");
                AssertEqual(0, adapter.Executed.Count, "adapter execution count");
            });
        }

        private static void PipelineExecutesFakeAdapterSteps()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = BuildPipelineTools(false);
                var command = new ToolCommand { ToolId = "excel.make_report" };
                command.Arguments["sheet"] = "Report";

                var result = executor.Execute(command, tools, new AppSettings { AutoConfirmToolActions = true }, false, false);

                AssertTrue(result.Success, "pipeline result");
                AssertEqual(2, adapter.Executed.Count, "adapter execution count");
                AssertEqual("excel.add_sheet", adapter.Executed[0].ToolId, "first tool");
                AssertEqual("Report", adapter.Executed[0].Arguments["name"], "first arg");
                AssertEqual("excel.write_table", adapter.Executed[1].ToolId, "second tool");
                AssertEqual("Report", adapter.Executed[1].Arguments["sheet"], "second arg");
            });
        }

        private static void PipelineResolvesStepOutputPlaceholders()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = BuildStepPlaceholderPipelineTools();
                var command = new ToolCommand { ToolId = "excel.chain_report" };

                var result = executor.Execute(command, tools, new AppSettings { AutoConfirmToolActions = true }, false, false);

                AssertTrue(result.Success, "pipeline result");
                AssertEqual(2, adapter.Executed.Count, "adapter execution count");
                AssertEqual("added sheet Report", adapter.Executed[1].Arguments["sourceMessage"], "step message placeholder");
                AssertEqual(true, adapter.Executed[1].Arguments["sourceSuccess"], "step success placeholder");

                var nested = new ToolDefinition
                {
                    Id = "excel.nested_placeholder",
                    Host = "Excel",
                    Name = "Nested placeholder",
                    Executor = "pipeline",
                    Enabled = true,
                    ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{\"header\":{\"type\":\"string\",\"description\":\"Header text.\"}},\"required\":[\"header\"],\"additionalProperties\":false}",
                    PipelineJson = "{\"steps\":[{\"id\":\"table\",\"toolId\":\"excel.write_table\",\"arguments\":{\"sheet\":\"Nested\",\"startAddress\":\"A1\",\"values\":[[\"{{args.header}}\"]]}}]}"
                };
                var nestedTools = adapter.GetBuiltInTools().Concat(new[] { nested }).ToList();
                var nestedResult = executor.Execute(Command(nested.Id, "header", "Revenue"), nestedTools, new AppSettings { AutoConfirmToolActions = true }, false, false);
                AssertTrue(nestedResult.Success, "nested pipeline placeholder result");
                AssertEqual("Revenue", adapter.CellValue("Nested", "A1"), "nested pipeline placeholder value");
            });
        }

        private static void PipelineStopsAfterFailedStep()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.QueueResult("excel.write_table", ToolResult.Fail("No table values provided."));
                var tools = BuildThreeStepPipelineTools();
                var command = new ToolCommand { ToolId = "excel.full_report" };
                command.Arguments["sheet"] = "Report";

                var result = executor.Execute(command, tools, new AppSettings { AutoConfirmToolActions = true }, false, false);

                AssertTrue(!result.Success, "pipeline should fail");
                AssertEqual("partial_failure", result.Status, "pipeline partial failure status");
                AssertEqual(false, result.Retryable, "pipeline partial failure retryable");
                AssertContains(result.Message, "Pipeline step failed: table", "failure message");
                AssertContains(result.DataJson, "\"id\":\"table\"", "failure data keeps failed step");
                AssertEqual(2, adapter.Executed.Count, "adapter execution count");
                AssertEqual("excel.add_sheet", adapter.Executed[0].ToolId, "first tool");
                AssertEqual("excel.write_table", adapter.Executed[1].ToolId, "failed tool");
            });
        }

        private static void PipelineRejectsMissingStepToolId()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = new List<ToolDefinition>
                {
                    new ToolDefinition
                    {
                        Id = "excel.bad_step",
                        Host = "Excel",
                        Name = "Bad step",
                        Executor = "pipeline",
                        Enabled = true,
                        PipelineJson = "{\"steps\":[{\"id\":\"prepare\",\"arguments\":{\"name\":\"Report\"}}]}"
                    }
                };

                var result = executor.Execute(new ToolCommand { ToolId = "excel.bad_step" }, tools, new AppSettings { AutoConfirmToolActions = true }, false, false);

                AssertTrue(!result.Success, "pipeline should fail");
                AssertContains(result.Message, "Pipeline step has no toolId", "missing tool id message");
                AssertEqual(0, adapter.Executed.Count, "adapter execution count");
            });
        }

        private static void PipelineRejectsDuplicateStepIds()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var pipeline = CustomTool("Excel", "excel.duplicate_steps");
                pipeline.PipelineJson = "{\"steps\":[" +
                    "{\"id\":\"read\",\"toolId\":\"excel.list_sheets\"}," +
                    "{\"id\":\"read\",\"toolId\":\"excel.list_sheets\"}]}";

                var validation = executor.ValidateToolDefinition(pipeline);
                var execution = executor.Execute(
                    new ToolCommand { ToolId = pipeline.Id },
                    new List<ToolDefinition> { pipeline },
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false);

                AssertTrue(!validation.Success, "duplicate ids rejected during validation");
                AssertTrue(!execution.Success, "duplicate ids rejected during execution");
                AssertContains(execution.Message, "step id must be unique", "duplicate id message");
                AssertEqual(0, adapter.Executed.Count, "duplicate pipeline adapter count");
            });
        }

        private static void CustomPipelineNeedsConfirmation()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = BuildPipelineTools(true);
                var command = new ToolCommand { ToolId = "excel.make_report" };
                command.Arguments["sheet"] = "Report";

                var result = executor.Execute(command, tools, new AppSettings { AutoConfirmToolActions = false }, false, false);

                AssertTrue(!result.Success, "pipeline should fail");
                AssertContains(result.Message, "requires confirmation", "confirmation message");
                AssertEqual(0, adapter.Executed.Count, "adapter execution count");
            });
        }

        private static void AgentModeGatesBuiltInMutation()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = BuildPipelineTools(false);
                var command = new ToolCommand { ToolId = "excel.make_report" };
                command.Arguments["sheet"] = "Report";

                var result = executor.Execute(command, tools, new AppSettings { AutoConfirmToolActions = false }, false, false);

                AssertTrue(!result.Success, "pipeline should fail");
                AssertContains(result.Message, "requires confirmation", "confirmation message");
                AssertEqual(0, adapter.Executed.Count, "adapter execution count");
            });
        }
    }
}
