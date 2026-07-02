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
        private static void ValidatesToolSaveAndPreservesMetadata()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var store = new ToolStore(paths);
                var executor = new OfficeToolExecutor(
                    adapter,
                    new VbaBackupStore(paths),
                    new SkillStore(paths),
                    store);
                var invalid = new ToolDefinition
                {
                    Id = "excel.invalid",
                    Host = "Excel",
                    Executor = "pipeline",
                    PipelineJson = "{\"steps\":[]}",
                    Enabled = true
                };

                var invalidResult = executor.ValidateToolDefinition(invalid);
                AssertTrue(!invalidResult.Success, "invalid tool rejected");
                AssertContains(invalidResult.Message, "at least one step", "invalid tool error");

                var valid = new ToolDefinition
                {
                    Id = "excel.safe_report",
                    Host = "Excel",
                    Name = "Safe report",
                    Description = "Create report.",
                    ArgumentSchemaJson = "{\"sheet\":\"Report\"}",
                    Executor = "pipeline",
                    PipelineJson = "{\"steps\":[{\"toolId\":\"excel.add_sheet\",\"arguments\":{\"name\":\"{{args.sheet}}\"}}]}",
                    Enabled = true,
                    RequiresConfirmation = true,
                    MutatesDocument = true,
                    AgentCanRun = false,
                    RiskLevel = 2,
                    UseWhen = "Create a report.",
                    VerifyJson = "{\"toolId\":\"excel.list_sheets\"}",
                    CapabilityStatus = "available"
                };

                AssertTrue(executor.ValidateToolDefinition(valid).Success, "valid tool accepted");
                store.SaveOne(valid);
                var loaded = store.Load().First(t => string.Equals(t.Id, valid.Id, StringComparison.OrdinalIgnoreCase));
                AssertTrue(loaded.MutatesDocument, "mutation metadata preserved");
                AssertTrue(!loaded.AgentCanRun, "agent run metadata preserved");
                AssertEqual(2, loaded.RiskLevel, "risk metadata preserved");
                AssertEqual(valid.UseWhen, loaded.UseWhen, "useWhen preserved");
                AssertEqual(valid.VerifyJson, loaded.VerifyJson, "verify metadata preserved");
            });
        }

        private static void AgentValidatesAndCreatesCustomTool()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var definitionArgs = new object[]
                {
                    "id", "excel.agent_report",
                    "host", "Excel",
                    "name", "Agent report",
                    "description", "Create an agent report.",
                    "argumentSchemaJson", "{\"sheet\":\"Report\"}",
                    "executor", "pipeline",
                    "pipelineJson", "{\"steps\":[{\"toolId\":\"excel.add_sheet\",\"arguments\":{\"name\":\"{{args.sheet}}\"}}]}",
                    "enabled", true,
                    "requiresConfirmation", true,
                    "mutatesDocument", true,
                    "agentCanRun", false
                };
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    null,
                    AgentBlock(
                        Command("common.tools_validate", definitionArgs),
                        Command("common.tools_save", definitionArgs)),
                    FinalBlock("Tool created."));

                var result = service.ExecuteAsync(
                    "Создай пользовательский инструмент для отчета.",
                    NewSession(adapter),
                    NewContext(adapter),
                    new AppSettings { AutoConfirmToolActions = true, RequireVerificationForMutations = false },
                    new List<ToolDefinition>(executor.GetControllerTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Tool created.", result.AssistantText, "agent tool creation final");
                var read = new ToolCommand { ToolId = "common.tools_read" };
                read.Arguments["id"] = "excel.agent_report";
                var readResult = executor.Execute(read, new List<ToolDefinition>(executor.GetControllerTools()), new AppSettings(), false, true);
                AssertTrue(readResult.Success, "agent-created tool readable");
                AssertContains(readResult.DataJson, "excel.add_sheet", "agent-created pipeline preserved");
            });
        }

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

        private static void ExpandedBuiltInToolsAreVisible()
        {
            var excel = new List<ToolDefinition>(FakeOfficeAdapter.ForHost("Excel").GetBuiltInTools());
            AssertTrue(HasTool(excel, "excel.read_formula_range"), "excel formula reader visible");
            AssertTrue(HasTool(excel, "excel.find_cells"), "excel find cells visible");
            AssertTrue(HasTool(excel, "excel.add_table"), "excel add table visible");
            AssertTrue(!FindTool(excel, "excel.clear_range").AgentCanRun, "excel clear requires confirmation");

            var word = new List<ToolDefinition>(FakeOfficeAdapter.ForHost("Word").GetBuiltInTools());
            AssertTrue(HasTool(word, "word.find_text"), "word find text visible");
            AssertTrue(HasTool(word, "word.add_table"), "word add table visible");

            var powerpoint = new List<ToolDefinition>(FakeOfficeAdapter.ForHost("PowerPoint").GetBuiltInTools());
            AssertTrue(HasTool(powerpoint, "powerpoint.list_shapes"), "powerpoint list shapes visible");
            AssertTrue(HasTool(powerpoint, "powerpoint.set_speaker_notes"), "powerpoint notes writer visible");
            AssertTrue(!FindTool(powerpoint, "powerpoint.move_slide").AgentCanRun, "powerpoint move requires confirmation");

            var outlook = new List<ToolDefinition>(FakeOfficeAdapter.ForHost("Outlook").GetBuiltInTools());
            AssertTrue(HasTool(outlook, "outlook.search_mail"), "outlook search visible");
            AssertTrue(HasTool(outlook, "outlook.create_mail_draft"), "outlook draft visible");
            AssertTrue(!FindTool(outlook, "outlook.mark_as_read").AgentCanRun, "outlook mark read requires confirmation");
        }

        private static void PromptToolMetadataIsWeakModelFriendly()
        {
            var adapter = FakeOfficeAdapter.ForHost("Excel");
            WithTempExecutor(adapter, delegate(OfficeToolExecutor executor, FakeOfficeAdapter fake)
            {
                var tools = new List<ToolDefinition>(fake.GetBuiltInTools());
                tools.AddRange(executor.GetControllerTools());
                var prompt = FlattenMessages(BuildPlannerMessages(
                    new AppSettings(),
                    tools,
                    new SkillDefinition[0]));

                AssertContains(prompt, "mode: read", "prompt includes read mode");
                AssertContains(prompt, "mode: mutation", "prompt includes mutation mode");
                AssertContains(prompt, "confirmation: required", "prompt includes confirmation metadata");
                AssertTrue(prompt.IndexOf("\"optional\"", StringComparison.OrdinalIgnoreCase) < 0, "prompt has no literal optional args");
                AssertContains(prompt, "common.tools_validate", "prompt includes tool validation");
                AssertContains(prompt, "common.prompts_read_defaults", "prompt includes prompt defaults reader");
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
                new ToolStore(paths).SaveOne(CustomTool("Excel", "excel.new"));
                AssertTrue(File.Exists(Path.Combine(brokenDirectory, "tool.json")), "broken tool preserved during save");
            });
        }

        private static void ToolStorePreservesExtraFilesAndOtherTools()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new ToolStore(paths);
                var first = CustomTool("Excel", "excel.first");
                first.PipelineJson = "{\"steps\":[{\"toolId\":\"excel.list_sheets\"}]}";
                var second = CustomTool("Word", "word.second");
                second.PipelineJson = "{\"steps\":[{\"toolId\":\"word.read_document\"}]}";
                store.Save(new[] { first, second });

                var firstStored = FindTool(store.Load(), first.Id);
                var extraPath = Path.Combine(firstStored.StoragePath, "notes.txt");
                File.WriteAllText(extraPath, "keep");
                first.Name = "Updated";
                store.SaveOne(first);

                AssertTrue(File.Exists(extraPath), "tool extra file preserved");
                AssertTrue(HasTool(store.Load(), second.Id), "other tool preserved");
                AssertTrue(store.Delete(first.Id), "first tool deleted");
                AssertTrue(HasTool(store.Load(), second.Id), "other tool survives delete");
                AssertEqual(0, Directory.GetFiles(paths.ToolsDirectory, "*.tmp", SearchOption.AllDirectories).Length, "no tool temp files");
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

                var allowed = executor.Execute(command, tools, new AppSettings { AutoConfirmToolActions = false }, false, false);
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
