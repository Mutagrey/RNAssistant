using System;
using System.Collections.Generic;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;
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

        private static void PipelineRejectsCycles()
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
                AssertContains(result.Message, "Pipeline cycle detected", "cycle message");
                AssertEqual(0, adapter.Executed.Count, "recursive pipeline adapter count");
            });
        }

        private static void PipelineResolvesNestedConfirmationBeforeExecution()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var pipeline = CustomTool("Excel", "excel.read_then_save_skill");
                pipeline.PipelineJson = "{\"steps\":[" +
                    "{\"toolId\":\"excel.read_range\",\"arguments\":{\"address\":\"A1\"}}," +
                    "{\"toolId\":\"common.skills_save\",\"arguments\":{\"id\":\"common.saved\",\"bodyMarkdown\":\"test\"}}" +
                    "]}";

                var result = executor.Execute(
                    new ToolCommand { ToolId = pipeline.Id },
                    new List<ToolDefinition> { pipeline },
                    new AppSettings { AutoConfirmToolActions = false },
                    false,
                    false);

                AssertEqual("waiting_confirmation", result.Status, "nested confirmation status");
                AssertEqual(0, adapter.Executed.Count, "pipeline does not partially execute");
            });
        }

        private static void PipelineEffectiveSafetyPropagatesNestedRisk()
        {
            var write = new ToolDefinition
            {
                Id = "excel.write_range",
                Host = "Excel",
                BuiltIn = true,
                Enabled = true,
                MutatesDocument = true,
                RiskLevel = 3
            };
            var pipeline = CustomTool("Excel", "excel.hidden_mutation");
            pipeline.MutatesDocument = false;
            pipeline.RiskLevel = 0;
            pipeline.PipelineJson = "{\"steps\":[{\"toolId\":\"excel.write_range\",\"arguments\":{}}]}";

            var profile = ToolSafetyPolicy.Resolve(pipeline, new[] { pipeline, write });

            AssertTrue(profile.Valid, "nested safety profile");
            AssertTrue(profile.MutatesDocument, "nested mutation propagated");
            AssertEqual(3, profile.RiskLevel, "nested risk propagated");
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

        private static void RemovedToolIdsAreUnknown()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var command = new ToolCommand { ToolId = "common.render_html" };
                var result = executor.Execute(
                    command,
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    new AppSettings(),
                    false,
                    false);
                AssertTrue(!result.Success, "removed html artifact fails");
                AssertContains(result.Message, "Unknown tool id", "removed html artifact diagnostic");
            });

            WithTempExecutor(FakeOfficeAdapter.ForHost("Outlook"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var result = executor.Execute(
                    new ToolCommand { ToolId = "outlook.draft_reply" },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    new AppSettings(),
                    false,
                    false);
                AssertTrue(!result.Success, "removed Outlook alias fails");
                AssertContains(result.Message, "Unknown tool id", "removed Outlook alias diagnostic");
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

                var drySession = new ChatSession();
                drySession.HtmlWorkspace.Files = null;
                drySession.HtmlWorkspace.DataSources = null;
                drySession.HtmlWorkspace.UpdatedUtc = default(DateTime);
                var dryRead = executor.Execute(new ToolCommand { ToolId = "common.html_workspace_read" }, tools, new AppSettings(), true, false, drySession);
                AssertTrue(dryRead.Success, "html workspace dry read succeeds");
                AssertTrue(drySession.HtmlWorkspace.Files == null, "html dry run does not normalize files in place");
                AssertTrue(drySession.HtmlWorkspace.DataSources == null, "html dry run does not normalize data in place");
                AssertEqual(default(DateTime), drySession.HtmlWorkspace.UpdatedUtc, "html dry run keeps timestamp");

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

                var deleteScript = new ToolCommand { ToolId = "common.html_workspace_delete_file" };
                deleteScript.Arguments["path"] = "app.js";
                var deleteScriptResult = executor.Execute(deleteScript, tools, new AppSettings(), false, false, session);
                AssertTrue(deleteScriptResult.Success, "html workspace file delete succeeds");
                AssertEqual(1, session.HtmlWorkspace.Files.Count, "html script deleted");

                var deleteData = new ToolCommand { ToolId = "common.html_workspace_delete_data" };
                deleteData.Arguments["name"] = "rows";
                var deleteDataResult = executor.Execute(deleteData, tools, new AppSettings(), false, false, session);
                AssertTrue(deleteDataResult.Success, "html workspace data delete succeeds");
                AssertEqual(0, session.HtmlWorkspace.DataSources.Count, "html data deleted");
                HtmlArtifactToolExecutor.RestoreSnapshot(session, session.HtmlWorkspace.History[0].Id);
                AssertEqual(1, session.HtmlWorkspace.DataSources.Count, "html data delete can be undone");

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

                store.ClearMessages(session.Host, session.DocumentKey, session.Id);
                var loaded = store.Load(session.Host, session.DocumentKey, session.Id);
                AssertEqual(0, loaded.Messages.Count, "messages cleared");
                AssertEqual(1, loaded.HtmlWorkspace.Files.Count, "html workspace preserved");
                AssertEqual("index.html", loaded.HtmlWorkspace.ActiveFileId, "active html preserved");

                AssertTrue(store.Delete(session.Host, session.DocumentKey, session.Id), "chat deleted");
                AssertTrue(store.Load(session.Host, session.DocumentKey, session.Id) == null, "deleted chat not loaded");
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
                    AgentBlock(Command("common.html_workspace_upsert_data", "name", "sales", "json", "{\"rows\":[{\"month\":\"Jan\",\"sales\":120}]}")),
                    AgentBlock(Command("common.html_workspace_upsert_file", "path", "app.js", "kind", "script", "content", "window.rows=window.RNAssistantData.sales.rows;", "setActive", false)),
                    AgentBlock(Command("common.html_workspace_upsert_file", "path", "index.html", "kind", "html", "content", "<!doctype html><html><head><script>window.rows=window.RNAssistantData.sales.rows;</script></head><body><h1>Sales</h1></body></html>", "setActive", true)),
                    "Готово.");
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "Сделай HTML страницу отчета по продажам.",
                    session,
                    NewContext(adapter),
                    new AppSettings(),
                    new List<ToolDefinition>(executor.GetControllerTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Готово.", result.AssistantText, "html agent final answer");
                AssertEqual(2, session.HtmlWorkspace.Files.Count, "agent html file count");
                AssertEqual(1, session.HtmlWorkspace.DataSources.Count, "agent html data count");
                AssertEqual("index.html", session.HtmlWorkspace.ActiveFileId, "agent html active file");
                AssertContains(session.HtmlWorkspace.Files[0].Content, "RNAssistantData.sales.rows", "agent html uses data");
                AssertContains(FlattenMessages(calls[0]), "common.html_workspace_upsert_file", "prompt exposes html file tool");
                AssertContains(FlattenMessages(calls[0]), "\"enum\":[\"html\",\"css\",\"script\"]", "prompt exposes script file kind");
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
                    new AppSettings(),
                    new List<ToolDefinition>(executor.GetControllerTools()),
                    null).GetAwaiter().GetResult();

                var prompt = FlattenMessages(calls[0]);
                AssertContains(prompt, "HTML MODE IS ENABLED", "html mode prompt marker");
                AssertContains(prompt, "common.html_workspace_upsert_file", "html mode exposes workspace file tool");
                AssertContains(prompt, "common.html_workspace_upsert_data", "html mode exposes data tool");
                AssertTrue(prompt.IndexOf("common.render_html", StringComparison.OrdinalIgnoreCase) < 0, "html mode prompt omits removed inline render tool");
            });
        }

        private static void ChatHtmlWorkspaceKeepsGenericFollowUpRoute()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    calls,
                    AgentBlock(Command("common.html_workspace_read")),
                    AgentBlock(Command(
                        "common.html_workspace_upsert_file",
                        "path", "app.js",
                        "kind", "script",
                        "content", "window.chartReady=true;",
                        "setActive", false)),
                    "Готово.");
                var session = NewSession(adapter);
                HtmlArtifactToolExecutor.UpsertFile(session, "index.html", "html", "<main>Chart</main>", true);
                var officeRoute = new OfficeIntentRouter().Route(
                    "Добавь новый лист Excel.",
                    new OfficeSnapshot { Host = "Excel" },
                    session);
                AssertTrue(officeRoute.TaskType != "html", "explicit Office target does not inherit html route");

                var result = service.ExecuteAsync(
                    "Никаких внешних зависимостей. Сделай, чтобы локально работало.",
                    session,
                    NewContext(adapter),
                    new AppSettings
                    {
                        MaxAgentToolsPerRequest = 8,
                        RequireVerificationForMutations = false
                    },
                    new List<ToolDefinition>(executor.GetControllerTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Готово.", result.AssistantText, "html follow-up final answer");
                AssertContains(FlattenMessages(calls[0]), "taskType: html", "existing workspace keeps html route");
                AssertContains(FlattenMessages(calls[0]), "requiresInspection: true", "existing workspace requires inspection");
                AssertContains(FlattenMessages(calls[0]), "common.html_workspace_upsert_file", "html file tool retained");
                AssertEqual(3, calls.Count, "html follow-up reads before mutation");
                AssertTrue(session.HtmlWorkspace.Files.Exists(file => file.Path == "app.js"), "html follow-up updates workspace");
            });
        }

        private static void ChatLargeMalformedHtmlPlannerResponseIsRebuilt()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var longContent = new string('x', 2500);
                var malformed =
                    "{\"kind\":\"tool_plan\",\"intent\":\"mutate\",\"message\":null,\"steps\":[{" +
                    "\"toolId\":\"common.html_workspace_upsert_file\",\"arguments\":{\"path\":\"index.html\",\"kind\":\"html\",\"content\":\"" +
                    longContent +
                    "\"}],\"expectedOutcome\":\"Ready\"}";
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    calls,
                    RawResponse(malformed),
                    AgentBlock(Command(
                        "common.html_workspace_upsert_file",
                        "path", "index.html",
                        "kind", "html",
                        "content", "<main>Local chart</main>",
                        "setActive", true)),
                    "Готово.");
                var session = NewSession(adapter);
                session.HtmlModeEnabled = true;

                var result = service.ExecuteAsync(
                    "Сделай локальный HTML-график.",
                    session,
                    NewContext(adapter),
                    new AppSettings { RequireVerificationForMutations = false, FallbackToJsonObject = false },
                    new List<ToolDefinition>(executor.GetControllerTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Готово.", result.AssistantText, "malformed html response repaired");
                AssertEqual(3, calls.Count, "repair and final call count");
                AssertTrue(
                    FlattenMessages(calls[1]).IndexOf(new string('x', 64), StringComparison.Ordinal) < 0,
                    "large malformed body omitted from repair prompt");
                AssertContains(FlattenMessages(calls[1]), "previous response was not a valid AgentDecision v1 decision", "repair requests a valid decision");
            });
        }

        private static void ChatHtmlDeleteRequiresReadBeforeMutation()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    calls,
                    AgentBlock(Command("common.html_workspace_delete_file", "path", "app.js")),
                    AgentBlock(Command("common.html_workspace_read")),
                    AgentBlock(Command("common.html_workspace_delete_file", "path", "app.js")),
                    "Готово.");
                var session = NewSession(adapter);
                HtmlArtifactToolExecutor.UpsertFile(session, "index.html", "html", "<main>Chart</main>", true);
                HtmlArtifactToolExecutor.UpsertFile(session, "app.js", "script", "window.ready=true;", false);

                var result = service.ExecuteAsync(
                    "Удалить app.js из HTML workspace.",
                    session,
                    NewContext(adapter),
                    new AppSettings { RequireVerificationForMutations = false },
                    new List<ToolDefinition>(executor.GetControllerTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Готово.", result.AssistantText, "html delete final answer");
                AssertEqual(4, calls.Count, "html delete retries after required read");
                AssertContains(FlattenMessages(calls[1]), "Target must be inspected before mutation", "delete before read is rejected");
                AssertTrue(!session.HtmlWorkspace.Files.Exists(file => file.Path == "app.js"), "agent deletes html file after read");
                AssertTrue(session.HtmlWorkspace.History.Count > 0, "agent delete remains undoable");
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
                command.Arguments["repairDecisionPrompt"] = "CUSTOM REPAIR";
                command.Arguments["chatSystemPrompt"] = "CUSTOM CHAT";
                command.Arguments["systemPromptRole"] = "developer";

                var blocked = executor.Execute(
                    command,
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    new AppSettings { AutoConfirmToolActions = false },
                    false,
                    false);
                AssertContains(blocked.Status, "waiting_confirmation", "prompt save waits confirmation");

                var runtimeSettings = new AppSettings { AutoConfirmToolActions = true };
                var saved = executor.Execute(
                    command,
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    runtimeSettings,
                    false,
                    false);
                AssertTrue(saved.Success, "prompt save succeeds");

                AssertEqual("CUSTOM REPAIR", settingsStore.AgentPrompts.RepairDecisionPrompt, "repair prompt saved");
                AssertEqual("CUSTOM CHAT", settingsStore.ChatSystemPrompt, "chat prompt saved");
                AssertEqual("developer", settingsStore.SystemPromptRole, "developer prompt role saved");
                AssertEqual("CUSTOM REPAIR", runtimeSettings.AgentPrompts.RepairDecisionPrompt, "runtime repair prompt updated");
                AssertEqual("developer", runtimeSettings.SystemPromptRole, "runtime developer prompt role updated");

                var read = executor.Execute(
                    new ToolCommand { ToolId = "common.prompts_read" },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    new AppSettings(),
                    false,
                    false);
                AssertTrue(read.Success, "prompt read succeeds");
                AssertContains(read.DataJson, "CUSTOM REPAIR", "prompt read data");
            });
        }

        private static void PromptToolReadsDefaults()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var settingsStore = new AppSettings();
                settingsStore.AgentPrompts.RepairDecisionPrompt = "CUSTOM REPAIR";
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
                AssertContains(read.DataJson, "CUSTOM REPAIR", "current prompt returned");
                AssertContains(read.DataJson, "local Office assistant and action agent", "default main prompt returned");
                AssertContains(read.DataJson, "in Chat mode", "default chat prompt returned");
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
                command.Arguments["argumentSchemaJson"] = EmptyFormalToolSchema;
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
                    new AppSettings { AutoConfirmToolActions = true, RequireVerificationForMutations = false, FallbackToJsonObject = false },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Done.", result.AssistantText, "assistant text");
                AssertEqual(3, calls.Count, "llm call count");
                AssertContains(FlattenMessages(calls[1]), "Validation error: unknown_tool", "semantic repair prompt");
                AssertContains(FlattenMessages(calls[1]), "excel.add_sheet", "retry prompt contains exact tool id");
                AssertContains(FlattenMessages(calls[2]), "excel.add_sheet succeeded", "successful retry is present as an observation");
                AssertTrue(FlattenMessages(calls[2]).IndexOf("unknown_tool", StringComparison.OrdinalIgnoreCase) < 0, "successful retry does not keep stale validation error");
                AssertEqual(1, adapter.Executed.Count, "adapter execution count");
                AssertEqual("excel.add_sheet", adapter.Executed[0].ToolId, "retry tool");
                var resultJson = Newtonsoft.Json.JsonConvert.SerializeObject(result.ToolResults);
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

        private static List<ChatMessage> BuildPlannerMessages(
            AppSettings settings,
            IEnumerable<ToolDefinition> tools,
            IEnumerable<SkillDefinition> skills)
        {
            return new PlannerPromptComposer().BuildMessages(
                "Test request",
                new OfficeSnapshot { Host = "Excel" },
                new RoutedTask
                {
                    App = "Excel",
                    Mode = "mutate",
                    TaskType = "content",
                    Phase = AgentPhases.Mutation,
                    RiskAllowed = 2,
                    RequiresTool = true
                },
                new ToolCatalogSlice { Tools = new List<ToolDefinition>(tools ?? new ToolDefinition[0]) },
                new AgentObservation[0],
                new DocumentContext(),
                skills ?? new SkillDefinition[0],
                settings ?? new AppSettings());
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
