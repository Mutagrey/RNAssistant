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
                    ArgumentSchemaJson = EmptyFormalToolSchema,
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
                    ArgumentSchemaJson = SheetFormalToolSchema,
                    Executor = "pipeline",
                    PipelineJson = "{\"steps\":[{\"toolId\":\"excel.add_sheet\",\"arguments\":{\"name\":\"{{args.sheet}}\"}}]}",
                    Enabled = true,
                    RequiresConfirmation = true,
                    MutatesDocument = true,
                    AgentCanRun = false,
                    RiskLevel = 2,
                    UseWhen = "Create a report.",
                    CapabilityStatus = "available"
                };

                AssertTrue(executor.ValidateToolDefinition(valid).Success, "valid tool accepted");
                var invalidHost = valid.Clone();
                invalidHost.Host = "UnknownOffice";
                AssertEqual("invalid_tool_host", executor.ValidateToolDefinition(invalidHost).ErrorCode, "unknown tool host rejected");
                var oversizedCatalogEntry = valid.Clone();
                oversizedCatalogEntry.AgentCanRun = true;
                oversizedCatalogEntry.Description = new string('x', 7000);
                AssertTrue(executor.ValidateToolDefinition(oversizedCatalogEntry).Success,
                    "storage validation does not duplicate runtime prompt budgeting");
                AssertTrue(AgentRunService.PrepareToolsForRun(
                        adapter.GetBuiltInTools().Concat(new[] { oversizedCatalogEntry }))
                    .Any(tool => string.Equals(tool.Id, oversizedCatalogEntry.Id, StringComparison.OrdinalIgnoreCase)),
                    "valid catalog entry remains runnable and the complete prompt budget decides whether the request fits");
                store.SaveOne(valid);
                var loaded = store.Load().First(t => string.Equals(t.Id, valid.Id, StringComparison.OrdinalIgnoreCase));
                AssertTrue(loaded.MutatesDocument, "mutation metadata preserved");
                AssertTrue(!loaded.AgentCanRun, "agent run metadata preserved");
                AssertEqual(2, loaded.RiskLevel, "risk metadata preserved");
                AssertEqual(valid.UseWhen, loaded.UseWhen, "useWhen preserved");
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

        private static void BuiltInToolIdsCannotBeShadowed()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var shadow = CustomTool("Excel", "excel.add_sheet");
                shadow.PipelineJson = "{\"steps\":[{\"toolId\":\"excel.list_sheets\"}]}";
                var store = new ToolStore(paths);
                store.SaveOne(shadow);
                var executor = new OfficeToolExecutor(adapter, new VbaBackupStore(paths), new SkillStore(paths), store);

                var catalogTool = FindTool(new ToolCatalogService(adapter, executor, store).GetVisibleTools(), shadow.Id);
                AssertTrue(catalogTool != null && catalogTool.BuiltIn, "catalog keeps built-in definition");
                var command = new ToolCommand { ToolId = shadow.Id };
                command.Arguments["name"] = "Protected";
                var result = executor.Execute(command, new[] { shadow }, new AppSettings { AutoConfirmToolActions = true }, false, false);

                AssertTrue(result.Success, "built-in executes despite custom collision");
                AssertTrue(adapter.HasSheet("Protected"), "built-in add sheet was executed");
                AssertEqual(1, adapter.Executed.Count(item => string.Equals(item.ToolId, "excel.add_sheet", StringComparison.OrdinalIgnoreCase)), "built-in add sheet executed once");
                AssertEqual(0, adapter.Executed.Count(item => string.Equals(item.ToolId, "excel.list_sheets", StringComparison.OrdinalIgnoreCase)), "shadow pipeline was not executed");

                var save = new ToolCommand { ToolId = "common.tools_create" };
                save.Arguments["id"] = shadow.Id;
                save.Arguments["host"] = "Excel";
                save.Arguments["description"] = "Invalid shadow.";
                save.Arguments["executor"] = "pipeline";
                save.Arguments["parameters"] = JObject.Parse(EmptyFormalToolSchema);
                save.Arguments["pipeline"] = JObject.Parse(shadow.PipelineJson);
                var saveResult = executor.Execute(save, adapter.GetBuiltInTools().ToList(), new AppSettings { AutoConfirmToolActions = true }, false, false);
                AssertTrue(!saveResult.Success, "controller rejects reserved id");
                AssertEqual("reserved_tool_id", saveResult.ErrorCode, "reserved id error code");
            });
        }

        private static void RefreshedCustomToolGetsEffectiveSafety()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = adapter.GetBuiltInTools().ToList();
                var pipeline = CustomTool("Excel", "excel.dynamic_mutation");
                pipeline.AgentCanRun = false;
                pipeline.MutatesDocument = false;
                pipeline.RiskLevel = 0;
                pipeline.PipelineJson = "{\"steps\":[{\"toolId\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Dynamic\"}}]}";

                tools.Add(pipeline);
                var profile = ToolSafetyPolicy.Resolve(pipeline, tools);
                AssertTrue(profile.MutatesDocument, "nested mutation propagated");
                AssertTrue(!profile.AgentCanRun, "nested mutation agent safety propagated");
                AssertTrue(profile.RiskLevel > 0, "nested mutation risk propagated");
                AssertTrue(!pipeline.MutatesDocument, "source tool remains unchanged");
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
                var prompt = FlattenMessages(new AgentPromptComposer().BuildMessages(
                    "Test request",
                    fake,
                    tools,
                    new SkillDefinition[0],
                    new DocumentContext(),
                    new AppSettings(),
                    NewSession(fake),
                    null));

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

                var dotted = CustomTool("Excel", "excel.collision");
                var underscored = CustomTool("Excel", "excel_collision");
                store.SaveOne(dotted);
                store.SaveOne(underscored);
                var collisionTools = store.Load();
                AssertTrue(HasTool(collisionTools, dotted.Id), "dotted tool id survives storage collision");
                AssertTrue(HasTool(collisionTools, underscored.Id), "underscored tool id survives storage collision");
                AssertTrue(!string.Equals(FindTool(collisionTools, dotted.Id).StoragePath, FindTool(collisionTools, underscored.Id).StoragePath, StringComparison.OrdinalIgnoreCase), "colliding safe ids use distinct directories");
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
        private static void AgentToolCrudPreservesOmittedFields()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var command = new ToolCommand { ToolId = "common.tools_create" };
                command.Arguments["id"] = "excel.generated_report";
                command.Arguments["host"] = "Excel";
                command.Arguments["name"] = "Generated report";
                command.Arguments["description"] = "Create a generated report sheet.";
                command.Arguments["parameters"] = JObject.Parse(SheetFormalToolSchema);
                command.Arguments["executor"] = "pipeline";
                command.Arguments["pipeline"] = JObject.Parse("{\"version\":1,\"steps\":[{\"id\":\"sheet\",\"toolId\":\"excel.add_sheet\",\"arguments\":{\"name\":\"{{args.sheet}}\"}}]}");
                command.Arguments["enabled"] = true;
                command.Arguments["requiresConfirmation"] = true;
                command.Arguments["mutatesDocument"] = true;
                command.Arguments["agentCanRun"] = false;
                command.Arguments["riskLevel"] = 2;

                var blocked = executor.Execute(command, new List<ToolDefinition>(adapter.GetBuiltInTools()), new AppSettings { AutoConfirmToolActions = false }, false, false);
                AssertTrue(!blocked.Success, "tool create should require confirmation");
                AssertContains(blocked.Status, "waiting_confirmation", "blocked status");

                var saved = executor.Execute(command, new List<ToolDefinition>(adapter.GetBuiltInTools()), new AppSettings { AutoConfirmToolActions = true }, false, false);
                AssertTrue(saved.Success, "tool create should succeed");
                AssertContains(saved.Message, "created", "create message");

                var update = new ToolCommand { ToolId = "common.tools_update" };
                update.Arguments["id"] = "excel.generated_report";
                update.Arguments["name"] = "Updated report";
                var updated = executor.Execute(update, new List<ToolDefinition>(adapter.GetBuiltInTools()), new AppSettings { AutoConfirmToolActions = true }, false, false);
                AssertTrue(updated.Success, "partial tool update should succeed");

                var read = executor.Execute(new ToolCommand { ToolId = "common.tools_read", Arguments = { ["id"] = "excel.generated_report" } }, new List<ToolDefinition>(adapter.GetBuiltInTools()), new AppSettings(), false, false);
                AssertTrue(read.Success, "tool read should succeed");
                AssertContains(read.DataJson, "excel.add_sheet", "saved pipeline");
                AssertContains(read.DataJson, "\"parameters\":{", "schema returned as native object");
                AssertContains(read.DataJson, "Updated report", "updated field returned");
                AssertContains(read.DataJson, "Create a generated report sheet", "omitted description preserved");
            });
        }

        private static void AgentSkillCrudPreservesOmittedFields()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var create = Command(
                    "common.skills_create",
                    "id", "excel.review_style",
                    "host", "Excel",
                    "name", "Review style",
                    "description", "Review workbook style consistently.",
                    "version", "1.0.0",
                    "bodyMarkdown", "# Review style\n\nPreserve workbook conventions.",
                    "enabled", true);
                var created = executor.Execute(create, tools, new AppSettings { AutoConfirmToolActions = true }, false, false);
                AssertTrue(created.Success, "skill create succeeds");

                var update = Command("common.skills_update", "id", "excel.review_style", "description", "Review workbook formatting consistently.");
                var updated = executor.Execute(update, tools, new AppSettings { AutoConfirmToolActions = true }, false, false);
                AssertTrue(updated.Success, "partial skill update succeeds");

                var read = executor.Execute(Command("common.skills_read", "id", "excel.review_style"), tools, new AppSettings(), false, true);
                AssertTrue(read.Success, "skill read succeeds");
                AssertContains(read.DataJson, "Review workbook formatting consistently", "skill description updated");
                AssertContains(read.DataJson, "Preserve workbook conventions", "omitted skill body preserved");
                AssertContains(read.DataJson, "\"version\":\"1.0.0\"", "omitted version preserved");

                var deleted = executor.Execute(Command("common.skills_delete", "id", "excel.review_style"), tools, new AppSettings { AutoConfirmToolActions = true }, false, false);
                AssertTrue(deleted.Success, "skill delete succeeds");
            });
        }

        private static void SkillIdsDoNotCollideAndDisabledReadsFail()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var store = new SkillStore(paths);
                store.Save(new[]
                {
                    new SkillDefinition
                    {
                        Id = "common.a.b",
                        Host = "Common",
                        Name = "Dot id",
                        Description = "Enabled skill.",
                        BodyMarkdown = "DOT_SKILL",
                        Enabled = true
                    },
                    new SkillDefinition
                    {
                        Id = "common.a_b",
                        Host = "Common",
                        Name = "Underscore id",
                        Description = "Disabled skill.",
                        BodyMarkdown = "DISABLED_SKILL",
                        Enabled = false
                    }
                });
                var loaded = store.Load();
                AssertEqual(2, loaded.Count, "similar skill ids are stored separately");
                AssertTrue(!string.Equals(loaded[0].StoragePath, loaded[1].StoragePath, StringComparison.OrdinalIgnoreCase),
                    "skill directories do not collide");
                var invalidDirectory = Path.Combine(paths.SkillsDirectory, "common", "invalid_external");
                Directory.CreateDirectory(invalidDirectory);
                File.WriteAllText(Path.Combine(invalidDirectory, "SKILL.md"),
                    "---\nid: common.invalid\nhost: Common\nname: Invalid\ndescription: " +
                    new string('x', 4001) + "\n---\nBody");
                AssertEqual(2, store.Load().Count, "invalid external skill metadata is skipped");
                var whitespaceIdRejected = false;
                try
                {
                    store.SaveOne(new SkillDefinition
                    {
                        Id = " common.bad ",
                        Host = "Common",
                        Name = "Bad id",
                        Description = "Invalid id.",
                        BodyMarkdown = "INVALID",
                        Enabled = true
                    });
                }
                catch (ArgumentException)
                {
                    whitespaceIdRejected = true;
                }
                AssertTrue(whitespaceIdRejected, "skill ids with surrounding whitespace are rejected");

                var executor = new OfficeToolExecutor(adapter, new VbaBackupStore(paths), store, new ToolStore(paths));
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var enabledRead = executor.Execute(
                    Command("common.skills_read", "id", "common.a.b"), tools, new AppSettings(), false, false,
                    new ChatSession(), 40, loaded, CancellationToken.None);
                AssertTrue(enabledRead.Success, "enabled runtime skill can be read");

                var disabledRead = executor.Execute(
                    Command("common.skills_read", "id", "common.a_b"), tools, new AppSettings(), false, false,
                    new ChatSession(), 40, loaded, CancellationToken.None);
                AssertTrue(!disabledRead.Success, "disabled runtime skill cannot be read by agent");
                AssertTrue(disabledRead.DataJson == null || disabledRead.DataJson.IndexOf("DISABLED_SKILL", StringComparison.Ordinal) < 0,
                    "disabled skill body is not exposed");
                var confirmedRuntimeRead = executor.Execute(
                    Command("common.skills_read", "id", "common.a_b"), tools, new AppSettings(), false, true,
                    new ChatSession(), 40, loaded.Where(item => item.Enabled).ToList(), CancellationToken.None);
                AssertTrue(!confirmedRuntimeRead.Success, "confirmation bypass does not broaden the runtime skill catalog");
            });
        }
    }
}
