using System;
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

        private static void HtmlWorkspaceToolsUpdateChatSession()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = new ChatSession
                {
                    Host = adapter.HostName,
                    DocumentKey = adapter.DocumentKey,
                    DocumentTitle = adapter.DocumentTitle,
                    Title = "HTML"
                };
                var tools = new List<ToolDefinition>(adapter.GetBuiltInTools());

                var fileCommand = new ToolCommand { ToolId = "common.html_workspace_upsert_file" };
                fileCommand.Arguments["path"] = "index.html";
                fileCommand.Arguments["kind"] = "html";
                fileCommand.Arguments["content"] = "<h1>Report</h1>";
                fileCommand.Arguments["setActive"] = true;

                var fileResult = executor.Execute(fileCommand, tools, new AppSettings(), false, false, session);
                AssertTrue(fileResult.Success, "html workspace file save succeeds");
                AssertEqual(1, session.HtmlWorkspace.Files.Count, "html file count");
                AssertEqual("index.html", session.HtmlWorkspace.ActiveFileId, "active html file");

                var scriptCommand = new ToolCommand { ToolId = "common.html_workspace_upsert_file" };
                scriptCommand.Arguments["path"] = "app.js";
                scriptCommand.Arguments["kind"] = "js";
                scriptCommand.Arguments["content"] = "window.reportReady = true;";
                scriptCommand.Arguments["setActive"] = false;
                var scriptResult = executor.Execute(scriptCommand, tools, new AppSettings(), false, false, session);
                AssertTrue(scriptResult.Success, "html workspace script save succeeds");
                AssertEqual(2, session.HtmlWorkspace.Files.Count, "html file and script count");
                AssertEqual("script", session.HtmlWorkspace.Files[1].Kind, "script kind normalized");

                var dataCommand = new ToolCommand { ToolId = "common.html_workspace_upsert_data" };
                dataCommand.Arguments["name"] = "rows";
                dataCommand.Arguments["json"] = "{\"items\":[1,2]}";
                var dataResult = executor.Execute(dataCommand, tools, new AppSettings(), false, false, session);
                AssertTrue(dataResult.Success, "html workspace data save succeeds");
                AssertEqual(1, session.HtmlWorkspace.DataSources.Count, "html data count");

                var readResult = executor.Execute(new ToolCommand { ToolId = "common.html_workspace_read" }, tools, new AppSettings(), false, false, session);
                AssertTrue(readResult.Success, "html workspace read succeeds");
                AssertContains(readResult.DataJson, "rnassistant.htmlWorkspace", "workspace result type");
                AssertContains(readResult.DataJson, "items", "workspace data included");

                var invalidData = new ToolCommand { ToolId = "common.html_workspace_upsert_data" };
                invalidData.Arguments["name"] = "bad";
                invalidData.Arguments["json"] = "{ bad";
                var invalidResult = executor.Execute(invalidData, tools, new AppSettings(), false, false, session);
                AssertTrue(!invalidResult.Success, "invalid html data fails");
                AssertContains(invalidResult.Message, "Invalid HTML workspace JSON", "invalid html data message");
            });
        }

        private static void HtmlWorkspacePersistsWithChatSession()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new ChatStore(paths);
                var session = store.Create("Excel", "book", "Book.xlsx", "HTML chat");
                HtmlArtifactToolExecutor.UpsertFile(session, "index.html", "html", "<h1>Saved</h1>", true);
                session.Messages.Add(new ChatMessage { Role = "user", Content = "hello" });
                store.Save(session);

                store.ClearMessages(session.Host, session.DocumentKey, session.SessionId);
                var loaded = store.Load(session.Host, session.DocumentKey, session.SessionId);
                AssertEqual(0, loaded.Messages.Count, "messages cleared");
                AssertEqual(1, loaded.HtmlWorkspace.Files.Count, "html workspace preserved");
                AssertEqual("index.html", loaded.HtmlWorkspace.ActiveFileId, "active html preserved");

                AssertTrue(store.Delete(session.Host, session.DocumentKey, session.SessionId), "chat deleted");
                AssertTrue(store.Load(session.Host, session.DocumentKey, session.SessionId) == null, "deleted chat not loaded");
            });
        }

        private static void HtmlWorkspaceUndoRestoresPreviousVersion()
        {
            var session = new ChatSession { Title = "HTML undo" };
            HtmlArtifactToolExecutor.UpsertDataSource(session, "sales", "{\"rows\":[1]}");
            HtmlArtifactToolExecutor.UpsertFile(session, "index.html", "html", "<h1>First</h1>", true);
            HtmlArtifactToolExecutor.UpsertFile(session, "index.html", "html", "<h1>Second</h1>", true);

            AssertTrue(session.HtmlWorkspace.History.Count > 0, "html history created");
            var historyCount = session.HtmlWorkspace.History.Count;
            HtmlArtifactToolExecutor.RestoreSnapshot(session, session.HtmlWorkspace.History[0].Id);
            AssertContains(session.HtmlWorkspace.Files[0].Content, "First", "html undo restores previous file content");
            AssertEqual("index.html", session.HtmlWorkspace.ActiveFileId, "html undo keeps active file");
            AssertEqual(1, session.HtmlWorkspace.DataSources.Count, "html undo keeps data");
            AssertEqual(historyCount - 1, session.HtmlWorkspace.History.Count, "html undo consumes restored version");
            AssertEqual(1, session.HtmlWorkspace.RedoHistory.Count, "html undo creates redo version");

            HtmlArtifactToolExecutor.RedoSnapshot(session, session.HtmlWorkspace.RedoHistory[0].Id);
            AssertContains(session.HtmlWorkspace.Files[0].Content, "Second", "html redo restores undone file content");
            AssertEqual("index.html", session.HtmlWorkspace.ActiveFileId, "html redo keeps active file");
            AssertEqual(0, session.HtmlWorkspace.RedoHistory.Count, "html redo consumes redo version");
            AssertEqual(historyCount, session.HtmlWorkspace.History.Count, "html redo restores undo history");
        }

        private static void ChatAgentCreatesHtmlWorkspace()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    calls,
                    AgentBlock(
                        Command("common.html_workspace_upsert_data", "name", "sales", "json", "{\"rows\":[{\"month\":\"Jan\",\"sales\":120}]}"),
                        Command("common.html_workspace_upsert_file", "path", "app.js", "kind", "script", "content", "window.rows=window.RNAssistantData.sales.rows;", "setActive", false),
                        Command("common.html_workspace_upsert_file", "path", "index.html", "kind", "html", "content", "<!doctype html><html><head><script>window.rows=window.RNAssistantData.sales.rows;</script></head><body><h1>Sales</h1></body></html>", "setActive", true)),
                    "Готово.");
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "Сделай HTML страницу отчета по продажам.",
                    session,
                    NewContext(adapter),
                    new AppSettings { ContextCharLimit = 8000 },
                    new List<ToolDefinition>(executor.GetControllerTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Готово.", result.AssistantText, "html agent final answer");
                AssertEqual(2, session.HtmlWorkspace.Files.Count, "agent html file count");
                AssertEqual(1, session.HtmlWorkspace.DataSources.Count, "agent html data count");
                AssertEqual("index.html", session.HtmlWorkspace.ActiveFileId, "agent html active file");
                AssertContains(session.HtmlWorkspace.Files[0].Content, "RNAssistantData.sales.rows", "agent html uses data");
                AssertContains(FlattenMessages(calls[0]), "common.html_workspace_upsert_file", "prompt exposes html file tool");
                AssertContains(FlattenMessages(calls[0]), "html|css|script", "prompt exposes script file kind");
                AssertContains(FlattenMessages(calls[0]), "window.RNAssistantData", "prompt explains data injection");
            });
        }

        private static void ChatHtmlModeForcesWorkspacePrompt()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var service = ChatServiceWithResponses(adapter, executor, calls, "Готово.");
                var session = NewSession(adapter);
                session.HtmlModeEnabled = true;

                service.ExecuteAsync(
                    "Сделай отчет по продажам.",
                    session,
                    NewContext(adapter),
                    new AppSettings { ContextCharLimit = 8000 },
                    new List<ToolDefinition>(executor.GetControllerTools()),
                    null).GetAwaiter().GetResult();

                var prompt = FlattenMessages(calls[0]);
                AssertContains(prompt, "HTML MODE IS ENABLED", "html mode prompt marker");
                AssertContains(prompt, "common.html_workspace_upsert_file", "html mode exposes workspace file tool");
                AssertContains(prompt, "common.html_workspace_upsert_data", "html mode exposes data tool");
                AssertTrue(prompt.IndexOf("common.render_html", StringComparison.OrdinalIgnoreCase) < 0, "html mode prompt omits legacy inline render tool");
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
                    new AppSettings { ContextCharLimit = 8000, AutoConfirmToolActions = true, RequireVerificationForMutations = false },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Done.", result.AssistantText, "assistant text");
                AssertEqual(3, calls.Count, "llm call count");
                AssertContains(FlattenMessages(calls[1]), "Tool is not available in the current route/phase", "validation observation prompt");
                AssertContains(FlattenMessages(calls[1]), "excel.add_sheet", "retry prompt contains exact tool id");
                AssertContains(FlattenMessages(calls[2]), "Local normalized observations are available", "successful retry clears stale failure directive");
                AssertTrue(FlattenMessages(calls[2]).IndexOf("failed or was rejected", StringComparison.OrdinalIgnoreCase) < 0, "successful retry does not keep failure directive");
                AssertEqual(1, adapter.Executed.Count, "adapter execution count");
                AssertEqual("excel.add_sheet", adapter.Executed[0].ToolId, "retry tool");
                var resultJson = Newtonsoft.Json.JsonConvert.SerializeObject(result.ToolResults);
                AssertContains(resultJson, "create_worksheet", "validation transcript keeps unknown tool");
                AssertContains(resultJson, "excel.add_sheet", "retry success logged");
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
