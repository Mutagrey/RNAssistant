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
        private static void ToolCatalogMergesVisibleTools()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                var toolStore = new ToolStore(paths);
                toolStore.Save(new[]
                {
                    CustomTool("Common", "common.inspect"),
                    CustomTool("Excel", "excel.custom"),
                    CustomTool("Word", "word.hidden")
                });
                var executor = new OfficeToolExecutor(adapter, new VbaBackupStore(paths), new SkillStore(paths));
                var catalog = new ToolCatalogService(adapter, executor, toolStore).GetVisibleTools();

                AssertTrue(HasTool(catalog, "excel.add_sheet"), "built-in tool visible");
                AssertTrue(HasTool(catalog, "excel.vba_apply_patch"), "controller VBA tool visible");
                AssertTrue(HasTool(catalog, "common.inspect"), "common custom tool visible");
                AssertTrue(HasTool(catalog, "excel.custom"), "host custom tool visible");
                AssertTrue(!HasTool(catalog, "word.hidden"), "other host custom tool hidden");
            });
        }

        private static void ToolStoreSavesAndUpdatesCustomTools()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new ToolStore(paths);
                var initial = CustomTool("Excel", "excel.custom_report");
                initial.Name = "Initial report";
                initial.PipelineJson = "{\"steps\":[{\"toolId\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Report\"}}]}";
                initial.Readme = "Creates a report sheet.";
                var otherHost = CustomTool("Word", "word.review");
                store.Save(new[] { initial, otherHost });

                var loadedInitial = FindTool(store.Load(), "excel.custom_report");
                AssertTrue(loadedInitial != null, "initial custom tool loaded");
                AssertEqual("Initial report", loadedInitial.Name, "initial name");
                AssertContains(loadedInitial.PipelineJson, "excel.add_sheet", "initial pipeline");
                AssertContains(loadedInitial.Readme, "report sheet", "initial readme");
                AssertTrue(!string.IsNullOrWhiteSpace(loadedInitial.StoragePath), "storage path set");

                var edited = CustomTool("Excel", "excel.custom_report");
                edited.Name = "Updated report";
                edited.RequiresConfirmation = true;
                edited.MutatesDocument = true;
                edited.PipelineJson = "{\"steps\":[{\"toolId\":\"excel.write_table\",\"arguments\":{\"sheet\":\"Report\",\"startAddress\":\"A1\",\"values\":\"[[\\\"A\\\"]]\"}}]}";
                store.Save(new[] { edited }, "Excel");

                var loaded = store.Load();
                var updated = FindTool(loaded, "excel.custom_report");
                AssertTrue(updated != null, "updated custom tool loaded");
                AssertEqual("Updated report", updated.Name, "updated name");
                AssertTrue(updated.RequiresConfirmation, "updated confirmation flag");
                AssertContains(updated.PipelineJson, "excel.write_table", "updated pipeline");
                AssertTrue(HasTool(loaded, "word.review"), "other host preserved");
            });
        }

        private static void ToolStoreSkipsBrokenCustomToolFiles()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var validDirectory = Path.Combine(paths.ToolsDirectory, "excel", "valid");
                var brokenDirectory = Path.Combine(paths.ToolsDirectory, "excel", "broken");
                Directory.CreateDirectory(validDirectory);
                Directory.CreateDirectory(brokenDirectory);
                File.WriteAllText(Path.Combine(validDirectory, "tool.json"), JsonConvert.SerializeObject(CustomTool("Excel", "excel.valid")));
                File.WriteAllText(Path.Combine(validDirectory, "pipeline.json"), "{\"steps\":[]}");
                File.WriteAllText(Path.Combine(brokenDirectory, "tool.json"), "{ broken");

                var loaded = new ToolStore(paths).Load();

                AssertEqual(1, loaded.Count, "loaded tool count");
                AssertEqual("excel.valid", loaded[0].Id, "loaded tool id");
                AssertContains(loaded[0].PipelineJson, "steps", "sidecar loaded");
            });
        }

        private static void ToolSafetyMetadataGatesMutations()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = new List<ToolDefinition>(adapter.GetBuiltInTools());
                tools.Add(new ToolDefinition
                {
                    Id = "excel.metadata_mutation",
                    Host = "Excel",
                    Name = "metadata mutation",
                    BuiltIn = true,
                    Enabled = true,
                    MutatesDocument = true,
                    AgentCanRun = true
                });
                var command = new ToolCommand { ToolId = "excel.metadata_mutation" };

                var blocked = executor.Execute(command, tools, new AppSettings { AgentModeEnabled = false, AutoConfirmToolActions = false }, false, false);
                AssertTrue(!blocked.Success, "metadata mutation blocked");
                AssertContains(blocked.Message, "requires confirmation", "metadata block message");
                AssertEqual(0, adapter.Executed.Count, "blocked adapter execution count");

                var allowed = executor.Execute(command, tools, new AppSettings { AgentModeEnabled = true, AutoConfirmToolActions = false }, false, false);
                AssertTrue(allowed.Success, "metadata mutation allowed in agent mode");
                AssertEqual(1, adapter.Executed.Count, "allowed adapter execution count");
            });
        }
        private static void AgentCanSaveCustomToolsWithConfirmation()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var command = new ToolCommand { ToolId = "common.tools_save" };
                command.Arguments["id"] = "excel.generated_report";
                command.Arguments["host"] = "Excel";
                command.Arguments["name"] = "Generated report";
                command.Arguments["description"] = "Create a generated report sheet.";
                command.Arguments["argumentSchemaJson"] = "{\"sheet\":\"Report\"}";
                command.Arguments["executor"] = "pipeline";
                command.Arguments["pipelineJson"] = "{\"steps\":[{\"id\":\"sheet\",\"toolId\":\"excel.add_sheet\",\"arguments\":{\"name\":\"{{args.sheet}}\"}}]}";
                command.Arguments["enabled"] = "true";
                command.Arguments["requiresConfirmation"] = "true";
                command.Arguments["mutatesDocument"] = "true";
                command.Arguments["agentCanRun"] = "false";

                var blocked = executor.Execute(command, new List<ToolDefinition>(adapter.GetBuiltInTools()), new AppSettings { AutoConfirmToolActions = false }, false, false);
                AssertTrue(!blocked.Success, "tool save should require confirmation");
                AssertContains(blocked.Status, "waiting_confirmation", "blocked status");

                var saved = executor.Execute(command, new List<ToolDefinition>(adapter.GetBuiltInTools()), new AppSettings { AutoConfirmToolActions = true }, false, false);
                AssertTrue(saved.Success, "tool save should succeed");
                AssertContains(saved.Message, "Custom tool saved", "save message");

                var read = executor.Execute(new ToolCommand { ToolId = "common.tools_read", Arguments = { ["id"] = "excel.generated_report" } }, new List<ToolDefinition>(adapter.GetBuiltInTools()), new AppSettings(), false, false);
                AssertTrue(read.Success, "tool read should succeed");
                AssertContains(read.DataJson, "excel.add_sheet", "saved pipeline");
            });
        }
    }
}
