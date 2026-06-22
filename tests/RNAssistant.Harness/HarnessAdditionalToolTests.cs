using System.Collections.Generic;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void PipelineRejectsInvalidDefinitions()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var invalid = CustomTool("Excel", "excel.bad_pipeline");
                invalid.PipelineJson = "{ bad json";
                var invalidResult = executor.Execute(
                    new ToolCommand { ToolId = "excel.bad_pipeline" },
                    new List<ToolDefinition> { invalid },
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false);

                AssertTrue(!invalidResult.Success, "invalid pipeline should fail");
                AssertContains(invalidResult.Message, "Invalid pipeline JSON", "invalid pipeline message");
                AssertEqual(0, adapter.Executed.Count, "invalid pipeline adapter count");

                var empty = CustomTool("Excel", "excel.empty_pipeline");
                empty.PipelineJson = "{\"steps\":[]}";
                var emptyResult = executor.Execute(
                    new ToolCommand { ToolId = "excel.empty_pipeline" },
                    new List<ToolDefinition> { empty },
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false);

                AssertTrue(!emptyResult.Success, "empty pipeline should fail");
                AssertContains(emptyResult.Message, "Pipeline has no steps", "empty pipeline message");
                AssertEqual(0, adapter.Executed.Count, "empty pipeline adapter count");
            });
        }

        private static void PipelineEnforcesNestingLimit()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var recursive = CustomTool("Excel", "excel.recursive_pipeline");
                recursive.PipelineJson = "{\"steps\":[{\"id\":\"self\",\"toolId\":\"excel.recursive_pipeline\"}]}";

                var result = executor.Execute(
                    new ToolCommand { ToolId = "excel.recursive_pipeline" },
                    new List<ToolDefinition> { recursive },
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false);

                AssertTrue(!result.Success, "recursive pipeline should fail");
                AssertContains(result.Message, "Pipeline nesting limit exceeded", "nesting limit message");
                AssertEqual(0, adapter.Executed.Count, "recursive pipeline adapter count");
            });
        }

        private static void UnknownAndDisabledToolsFail()
        {
            var adapter = FakeOfficeAdapter.ForHost("Excel");
            WithTempExecutor(adapter, delegate(OfficeToolExecutor executor, FakeOfficeAdapter fake)
            {
                var builtIns = new List<ToolDefinition>(fake.GetBuiltInTools());
                var unknown = executor.Execute(
                    new ToolCommand { ToolId = "create_worksheet" },
                    builtIns,
                    new AppSettings(),
                    false,
                    false);

                AssertTrue(!unknown.Success, "unknown tool should fail");
                AssertContains(unknown.Message, "Unknown tool id", "unknown tool message");
                AssertContains(unknown.Message, "excel.add_sheet", "unknown tool suggestion");
                AssertContains(unknown.DataJson, "availableToolIds", "unknown tool diagnostics");
                AssertEqual(0, fake.Executed.Count, "unknown adapter count");

                fake.Executed.Clear();
                var disabled = CustomTool("Excel", "excel.disabled_pipeline");
                disabled.Enabled = false;
                disabled.PipelineJson = "{\"steps\":[{\"toolId\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Disabled\"}}]}";
                var tools = new List<ToolDefinition>(builtIns);
                tools.Add(disabled);

                var disabledResult = executor.Execute(
                    new ToolCommand { ToolId = "excel.disabled_pipeline" },
                    tools,
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false);

                AssertTrue(!disabledResult.Success, "disabled tool should fail");
                AssertContains(disabledResult.Message, "Tool is disabled", "disabled tool message");
                AssertEqual(0, fake.Executed.Count, "disabled adapter count");
            });
        }

        private static void HtmlArtifactToolRequiresSetting()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var command = new ToolCommand { ToolId = "common.render_html" };
                command.Arguments["title"] = "Demo";
                command.Arguments["html"] = "<div><script>window.demo=1</script>Demo</div>";
                command.Arguments["height"] = 240;

                var blocked = executor.Execute(
                    command,
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    new AppSettings { AllowUnsafeHtmlArtifacts = false },
                    false,
                    false);
                AssertTrue(!blocked.Success, "html artifact disabled");
                AssertContains(blocked.Message, "disabled", "disabled message");

                var allowed = executor.Execute(
                    command,
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    new AppSettings { AllowUnsafeHtmlArtifacts = true },
                    false,
                    false);
                AssertTrue(allowed.Success, "html artifact enabled");
                AssertContains(allowed.DataJson, "rnassistant.html", "html artifact type");
                AssertContains(allowed.DataJson, "<script>", "raw script preserved");
            });
        }

        private static void PromptToolSavesAgentPromptTemplates()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var settingsStore = new AppSettings();
                var executor = new OfficeToolExecutor(
                    adapter,
                    new VbaBackupStore(paths),
                    new SkillStore(paths),
                    new ToolStore(paths),
                    () => settingsStore,
                    value => settingsStore = value);
                var command = new ToolCommand { ToolId = "common.prompts_save" };
                command.Arguments["toolRoutingPrompt"] = "CUSTOM ROUTING";
                command.Arguments["retryFailedToolPrompt"] = "Retry {{toolId}} with {{availableToolIds}}";

                var blocked = executor.Execute(
                    command,
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    new AppSettings { AutoConfirmToolActions = false },
                    false,
                    false);
                AssertContains(blocked.Status, "waiting_confirmation", "prompt save waits confirmation");

                var saved = executor.Execute(
                    command,
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false);
                AssertTrue(saved.Success, "prompt save succeeds");

                AssertEqual("CUSTOM ROUTING", settingsStore.AgentPrompts.ToolRoutingPrompt, "routing prompt saved");
                AssertContains(settingsStore.AgentPrompts.RetryFailedToolPrompt, "{{toolId}}", "retry prompt placeholder saved");

                var read = executor.Execute(
                    new ToolCommand { ToolId = "common.prompts_read" },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    new AppSettings(),
                    false,
                    false);
                AssertTrue(read.Success, "prompt read succeeds");
                AssertContains(read.DataJson, "CUSTOM ROUTING", "prompt read data");
            });
        }

        private static void PromptToolReadsDefaults()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var settingsStore = new AppSettings();
                settingsStore.AgentPrompts.ToolRoutingPrompt = "CUSTOM ROUTING";
                var executor = new OfficeToolExecutor(
                    adapter,
                    new VbaBackupStore(paths),
                    new SkillStore(paths),
                    new ToolStore(paths),
                    () => settingsStore,
                    value => settingsStore = value);

                var read = executor.Execute(
                    new ToolCommand { ToolId = "common.prompts_read_defaults" },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    new AppSettings(),
                    false,
                    false);

                AssertTrue(read.Success, "prompt defaults read succeeds");
                AssertContains(read.DataJson, "CUSTOM ROUTING", "current prompt returned");
                AssertContains(read.DataJson, "built-in tools cannot solve", "default prompt returned");
            });
        }

        private static void ToolValidateChecksPayloadWithoutSaving()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var toolStore = new ToolStore(paths);
                var executor = new OfficeToolExecutor(adapter, new VbaBackupStore(paths), new SkillStore(paths), toolStore);
                var command = new ToolCommand { ToolId = "common.tools_validate" };
                command.Arguments["id"] = "excel.validated";
                command.Arguments["host"] = "Excel";
                command.Arguments["name"] = "Validated";
                command.Arguments["description"] = "Validated pipeline.";
                command.Arguments["argumentSchemaJson"] = "{}";
                command.Arguments["executor"] = "pipeline";
                command.Arguments["pipelineJson"] = "{\"steps\":[{\"toolId\":\"excel.list_sheets\",\"arguments\":{}}]}";

                var result = executor.Execute(command, new List<ToolDefinition>(adapter.GetBuiltInTools()), new AppSettings(), false, false);

                AssertTrue(result.Success, "tool validate succeeds");
                AssertContains(result.Message, "valid", "tool validate message");
                AssertTrue(!HasTool(toolStore.Load(), "excel.validated"), "tool validate does not save");
            });
        }

        private static void ChatUnknownToolRetriesExactAvailableId()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    calls,
                    AgentBlock(Command("create_worksheet", "name", "Report")),
                    AgentBlock(Command("excel.add_sheet", "name", "Report")),
                    "Done.");
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "Create a new worksheet named Report.",
                    session,
                    NewContext(adapter),
                    new AppSettings { ContextCharLimit = 8000 },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Done.", result.AssistantText, "assistant text");
                AssertEqual(3, calls.Count, "llm call count");
                AssertContains(FlattenMessages(calls[1]), "Use only these exact available tool ids", "retry available ids prompt");
                AssertContains(FlattenMessages(calls[1]), "excel.add_sheet", "retry prompt contains exact tool id");
                AssertEqual(1, adapter.Executed.Count, "adapter execution count");
                AssertEqual("excel.add_sheet", adapter.Executed[0].ToolId, "retry tool");
                var resultJson = Newtonsoft.Json.JsonConvert.SerializeObject(result.ToolResults);
                AssertContains(resultJson, "create_worksheet", "unknown tool logged");
                AssertContains(resultJson, "Unknown tool id", "unknown failure logged");
            });
        }

        private static string FlattenMessages(IEnumerable<ChatMessage> messages)
        {
            var values = new List<string>();
            foreach (var message in messages ?? new ChatMessage[0])
            {
                if (message != null)
                {
                    values.Add(message.Content ?? string.Empty);
                }
            }

            return string.Join("\n", values.ToArray());
        }

        private static void ConfirmationMatrixCoversDryAndManualRuns()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = BuildPipelineTools(true);
                var command = new ToolCommand { ToolId = "excel.make_report" };
                command.Arguments["sheet"] = "Report";

                var blocked = executor.Execute(
                    command,
                    tools,
                    new AppSettings { AutoConfirmToolActions = false },
                    false,
                    false);
                AssertTrue(!blocked.Success, "normal custom run should require confirmation");
                AssertContains(blocked.Message, "requires confirmation", "blocked custom message");
                AssertEqual(0, adapter.Executed.Count, "blocked adapter count");

                var dryRun = executor.Execute(
                    command,
                    tools,
                    new AppSettings { AutoConfirmToolActions = false },
                    true,
                    false);
                AssertTrue(dryRun.Success, "dry-run custom tool should be allowed");
                AssertContains(dryRun.Message, "Dry run completed", "dry-run message");
                AssertEqual(0, adapter.Executed.Count, "dry-run adapter count");

                var manualRun = executor.Execute(
                    command,
                    tools,
                    new AppSettings { AutoConfirmToolActions = false },
                    false,
                    true);
                AssertTrue(manualRun.Success, "manual custom tool should be allowed");
                AssertEqual(2, adapter.Executed.Count, "manual adapter count");
                AssertEqual("excel.add_sheet", adapter.Executed[0].ToolId, "manual first tool");
                AssertEqual("excel.write_table", adapter.Executed[1].ToolId, "manual second tool");
            });
        }
    }
}
