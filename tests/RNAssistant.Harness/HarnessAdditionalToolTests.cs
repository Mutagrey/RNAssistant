using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;
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
                var pipeline = CustomTool("Excel", "excel.read_then_create_skill");
                pipeline.PipelineJson = "{\"steps\":[" +
                    "{\"toolId\":\"excel.read_range\",\"arguments\":{\"address\":\"A1\"}}," +
                    "{\"toolId\":\"common.skills_create\",\"arguments\":{\"id\":\"common.saved\",\"description\":\"Saved test skill.\",\"bodyMarkdown\":\"test\"}}" +
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
            AssertTrue(profile.RequiresConfirmation, "implicit custom mutation confirmation propagated");
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

        private static void CompactToolCatalogKeepsLegacyAliasesRunnable()
        {
            var expectedHostCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "Excel", 17 },
                { "Word", 10 },
                { "PowerPoint", 11 },
                { "Outlook", 6 }
            };
            foreach (var pair in expectedHostCounts)
            {
                var runnable = AgentRunService.PrepareToolsForRun(FakeOfficeAdapter.ForHost(pair.Key).GetBuiltInTools());
                AssertEqual(pair.Value, runnable.Count, pair.Key + " compact runnable tool count");
            }

            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var listCharts = executor.Execute(Command("excel.list_charts", "sheet", "Data"), tools, new AppSettings(), false, false);
                AssertTrue(listCharts.Success, "legacy Excel list alias executes");
                AssertEqual("excel.inspect", adapter.Executed.Last().ToolId, "Excel list alias canonical id");
                AssertEqual("charts", adapter.Executed.Last().Arguments["kind"], "Excel list alias selector");

                var formulas = executor.Execute(Command("excel.read_formula_range", "sheet", "Data", "address", "A1:B2"), tools, new AppSettings(), false, false);
                AssertTrue(formulas.Success, "legacy Excel formula alias executes");
                AssertEqual("formulas", adapter.Executed.Last().Arguments["content"], "Excel formula selector");

                AssertTrue(executor.Execute(Command("excel.profile_range", "sheet", "Data", "address", "A1:B2"), tools, new AppSettings(), false, false).Success,
                    "legacy Excel profile alias executes");
                AssertEqual("profile", adapter.Executed.Last().Arguments["content"], "Excel profile selector");

                var table = executor.Execute(Command("excel.write_table", "sheet", "Report", "startAddress", "B2", "values", new JArray(new JArray("A"))), tools, new AppSettings { AutoConfirmToolActions = true }, false, false);
                AssertTrue(table.Success, "legacy Excel table alias executes");
                AssertEqual("excel.write_range", adapter.Executed.Last().ToolId, "Excel table alias canonical id");
                AssertEqual("table", adapter.Executed.Last().Arguments["kind"], "Excel table selector");
                AssertEqual("B2", adapter.Executed.Last().Arguments["address"], "Excel table address normalized");

                var nullableWrite = Command("excel.write_range", "sheet", "Report", "values", new JArray(new JArray("B")));
                nullableWrite.Arguments["kind"] = null;
                nullableWrite.Arguments["formula"] = null;
                AssertTrue(executor.Execute(nullableWrite, tools, new AppSettings(), false, false).Success,
                    "Excel write inference ignores strict-schema nulls");
                AssertEqual("table", adapter.Executed.Last().Arguments["kind"], "Excel write kind inferred from non-null payload");

                AssertTrue(executor.Execute(Command("excel.add_chart", "sheet", "Data", "chartName", "Legacy Chart"), tools, new AppSettings(), false, false).Success,
                    "legacy Excel chart create alias executes");
                AssertEqual("excel.upsert_chart", adapter.Executed.Last().ToolId, "Excel chart canonical id");
                AssertEqual("createOnly", adapter.Executed.Last().Arguments["mode"], "Excel chart create policy");

                AssertTrue(executor.Execute(Command("excel.autofit", "sheet", "Data"), tools, new AppSettings(), false, false).Success,
                    "legacy Excel autofit alias executes");
                AssertEqual("excel.format_range", adapter.Executed.Last().ToolId, "Excel format canonical id");
                AssertEqual("both", adapter.Executed.Last().Arguments["autoFit"], "Excel autofit selector");

                AssertTrue(executor.Execute(new ToolCommand { ToolId = "common.skills_list" }, tools, new AppSettings(), false, false).Success,
                    "legacy skills list alias executes");
                AssertTrue(executor.Execute(new ToolCommand { ToolId = "common.tools_list" }, tools, new AppSettings(), false, false).Success,
                    "legacy tools list alias executes");
                var prompts = executor.Execute(new ToolCommand { ToolId = "common.prompts_read_defaults" }, tools, new AppSettings(), false, false);
                AssertTrue(prompts.Success && prompts.DataJson.IndexOf("\"defaults\"", StringComparison.Ordinal) >= 0,
                    "legacy prompt defaults alias executes");

                var legacyPipeline = CustomTool("Excel", "excel.legacy_catalog_pipeline");
                legacyPipeline.PipelineJson = "{\"steps\":[{\"id\":\"objects\",\"toolId\":\"excel.list_shapes\",\"arguments\":{}}]}";
                var prepared = AgentRunService.PrepareToolsForRun(adapter.GetBuiltInTools().Concat(new[] { legacyPipeline }));
                var preparedPipeline = prepared.Single(item => item.Id == legacyPipeline.Id);
                AssertContains(preparedPipeline.PipelineJson, "excel.inspect", "legacy pipeline id canonicalized before safety");
                AssertContains(preparedPipeline.PipelineJson, "\"kind\":\"shapes\"", "legacy pipeline selector injected");

                var controllerRunnable = AgentRunService.PrepareToolsForRun(executor.GetControllerTools());
                AssertEqual(24, controllerRunnable.Count, "compact common runnable tool count");
                AssertTrue(controllerRunnable.All(item => item.Id != "common.skills_list" &&
                    item.Id != "common.skills_create" &&
                    item.Id != "common.skills_update" &&
                    item.Id != "common.tools_list" &&
                    item.Id != "common.tools_create" &&
                    item.Id != "common.tools_update" &&
                    item.Id != "common.prompts_read_defaults" &&
                    item.Id != "common.html_workspace_upsert_file" &&
                    item.Id != "common.vba_list_modules"), "legacy common ids stay out of model catalog");
                AssertTrue(controllerRunnable.Any(item => item.Id == "common.skills_upsert") &&
                    controllerRunnable.Any(item => item.Id == "common.tools_upsert"), "common authoring upserts stay visible");
            });

            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = adapter.GetBuiltInTools().ToList();
                AssertTrue(executor.Execute(new ToolCommand { ToolId = "word.read_selection" }, tools, new AppSettings(), false, false).Success,
                    "legacy Word read alias executes");
                AssertEqual("selection", adapter.Executed.Last().Arguments["source"], "Word read selector");
                AssertTrue(executor.Execute(Command("word.insert_paragraph", "text", "Legacy"), tools, new AppSettings { AutoConfirmToolActions = true }, false, false).Success,
                    "legacy Word write alias executes");
                AssertEqual("paragraph", adapter.Executed.Last().Arguments["mode"], "Word write selector");
                AssertTrue(executor.Execute(Command("word.apply_style", "style", "Heading 1"), tools, new AppSettings(), false, false).Success,
                    "legacy Word style alias executes");
                AssertEqual("word.format_text", adapter.Executed.Last().ToolId, "Word format canonical id");
                AssertEqual("style", adapter.Executed.Last().Arguments["kind"], "Word format selector");
            });

            WithTempExecutor(FakeOfficeAdapter.ForHost("PowerPoint"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = adapter.GetBuiltInTools().ToList();
                AssertTrue(executor.Execute(Command("powerpoint.list_shapes", "slideIndex", 1), tools, new AppSettings(), false, false).Success,
                    "legacy PowerPoint list alias executes");
                AssertEqual("shapes", adapter.Executed.Last().Arguments["kind"], "PowerPoint list selector");
                AssertTrue(executor.Execute(new ToolCommand { ToolId = "powerpoint.list_shapes" }, tools, new AppSettings(), false, false).Success,
                    "legacy PowerPoint list default executes");
                AssertEqual(1L, Convert.ToInt64(adapter.Executed.Last().Arguments["slideIndex"]), "legacy PowerPoint slide default preserved");
                AssertTrue(executor.Execute(Command("powerpoint.read_speaker_notes", "slideIndex", 0), tools, new AppSettings(), false, false).Success,
                    "legacy PowerPoint notes alias executes");
                AssertEqual("powerpoint.read_slides", adapter.Executed.Last().ToolId, "PowerPoint notes canonical id");
                AssertEqual("notes", adapter.Executed.Last().Arguments["content"], "PowerPoint notes selector");
                AssertTrue(!adapter.Executed.Last().Arguments.ContainsKey("slideIndex"), "legacy all-notes sentinel removed");
                AssertTrue(executor.Execute(Command("powerpoint.replace_selection_text", "text", "Legacy"), tools, new AppSettings { AutoConfirmToolActions = true }, false, false).Success,
                    "legacy PowerPoint selection alias executes");
                AssertEqual("powerpoint.set_text", adapter.Executed.Last().ToolId, "PowerPoint write alias canonical id");
                AssertEqual("shape", adapter.Executed.Last().Arguments["target"], "PowerPoint text target selector");
                AssertTrue(executor.Execute(Command("powerpoint.add_picture", "path", "image.png"), tools, new AppSettings(), false, false).Success,
                    "legacy PowerPoint object alias executes");
                AssertEqual("powerpoint.add_object", adapter.Executed.Last().ToolId, "PowerPoint object canonical id");
                AssertEqual("picture", adapter.Executed.Last().Arguments["kind"], "PowerPoint object selector");
                AssertEqual(1L, Convert.ToInt64(adapter.Executed.Last().Arguments["slideIndex"]), "legacy PowerPoint object slide default preserved");
                AssertTrue(executor.Execute(Command("powerpoint.add_text_box", "text", "Legacy", "fontSize", 0), tools, new AppSettings(), false, false).Success,
                    "legacy PowerPoint zero font default remains compatible");
                AssertTrue(!adapter.Executed.Last().Arguments.ContainsKey("fontSize"), "legacy zero font default is omitted for the compact schema");
                var inferredObject = Command("powerpoint.add_object", "text", "Inferred");
                inferredObject.Arguments["kind"] = null;
                inferredObject.Arguments["path"] = null;
                AssertTrue(executor.Execute(inferredObject, tools, new AppSettings(), false, false).Success,
                    "PowerPoint object inference ignores strict-schema nulls");
                AssertEqual("textBox", adapter.Executed.Last().Arguments["kind"], "PowerPoint object inferred from text");
            });

            WithTempExecutor(FakeOfficeAdapter.ForHost("Outlook"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = adapter.GetBuiltInTools().ToList();
                AssertTrue(executor.Execute(new ToolCommand { ToolId = "outlook.read_current_mail" }, tools, new AppSettings(), false, false).Success,
                    "legacy Outlook read alias executes");
                AssertTrue(executor.Execute(Command("outlook.create_reply_all_draft", "body", "Legacy"), tools, new AppSettings { AutoConfirmToolActions = true }, false, false).Success,
                    "legacy Outlook draft alias executes");
                AssertEqual("replyAll", adapter.Executed.Last().Arguments["kind"], "Outlook draft selector");
                AssertTrue(executor.Execute(new ToolCommand { ToolId = "outlook.list_attachments" }, tools, new AppSettings(), false, false).Success,
                    "legacy Outlook attachments alias executes");
                AssertEqual("attachments", adapter.Executed.Last().Arguments["content"], "Outlook read selector");
                AssertTrue(executor.Execute(new ToolCommand { ToolId = "outlook.mark_as_read" }, tools, new AppSettings(), false, false).Success,
                    "legacy Outlook update alias executes");
                AssertEqual("outlook.update_mail", adapter.Executed.Last().ToolId, "Outlook update canonical id");
                AssertEqual("markRead", adapter.Executed.Last().Arguments["kind"], "Outlook update selector");
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

                var scriptCommand = new ToolCommand { ToolId = "common.html_workspace_upsert" };
                scriptCommand.Arguments["resourceType"] = "file";
                scriptCommand.Arguments["name"] = "app.js";
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

                var readResult = executor.Execute(new ToolCommand
                {
                    ToolId = "common.html_workspace_read",
                    Arguments = { ["dataName"] = "rows" }
                }, tools, new AppSettings(), false, false, session);
                AssertTrue(readResult.Success, "html workspace read succeeds");
                AssertContains(readResult.DataJson, "rnassistant.htmlWorkspace", "workspace result type");
                AssertContains(readResult.DataJson, "items", "workspace data included");

                var ambiguousRead = new ToolCommand { ToolId = "common.html_workspace_read" };
                ambiguousRead.Arguments["path"] = "index.html";
                ambiguousRead.Arguments["dataName"] = "rows";
                var ambiguousResult = executor.Execute(ambiguousRead, tools, new AppSettings(), false, false, session);
                AssertTrue(!ambiguousResult.Success, "legacy HTML read rejects conflicting selectors");
                AssertContains(ambiguousResult.Message, "either path or dataName", "legacy HTML selector diagnostic");

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

                HtmlArtifactToolExecutor.UpsertFile(session, "styles.css", "css", "body{}", false);
                var cssActive = new ToolCommand { ToolId = "common.html_workspace_set_active" };
                cssActive.Arguments["path"] = "styles.css";
                var cssActiveResult = executor.Execute(cssActive, tools, new AppSettings(), false, false, session);
                AssertTrue(!cssActiveResult.Success, "non-html file cannot become active preview");

                var failedSession = new ChatSession { Title = "HTML failed mutation" };
                HtmlArtifactToolExecutor.UpsertFile(failedSession, "index.html", "html", "<h1>First</h1>", true);
                HtmlArtifactToolExecutor.UpsertFile(failedSession, "index.html", "html", "<h1>Second</h1>", true);
                HtmlArtifactToolExecutor.RestoreSnapshot(failedSession, failedSession.HtmlWorkspace.History[0].Id);
                var failedHistoryCount = failedSession.HtmlWorkspace.History.Count;
                var failedRedoCount = failedSession.HtmlWorkspace.RedoHistory.Count;
                var missingActive = new ToolCommand { ToolId = "common.html_workspace_set_active" };
                missingActive.Arguments["path"] = "missing.html";
                var missingResult = executor.Execute(missingActive, tools, new AppSettings(), false, false, failedSession);
                AssertTrue(!missingResult.Success, "missing active HTML file fails");
                AssertEqual(failedHistoryCount, failedSession.HtmlWorkspace.History.Count, "failed set-active preserves history");
                AssertEqual(failedRedoCount, failedSession.HtmlWorkspace.RedoHistory.Count, "failed set-active preserves redo");
                var missingDryRun = executor.Execute(missingActive, tools, new AppSettings(), true, false, failedSession);
                AssertTrue(!missingDryRun.Success, "set-active dry run validates file existence");

                var absolutePath = new ToolCommand { ToolId = "common.html_workspace_upsert_file" };
                absolutePath.Arguments["path"] = "/index.html";
                absolutePath.Arguments["content"] = "<h1>Absolute</h1>";
                var absoluteResult = executor.Execute(absolutePath, tools, new AppSettings(), true, false, failedSession);
                AssertTrue(!absoluteResult.Success, "absolute workspace path rejected");
            });
        }

        private static void HtmlWorkspacePersistsWithChatSession()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new ChatStore(paths);
                var session = store.Create("Excel", "book", "Book.xlsx", "HTML chat");
                HtmlArtifactToolExecutor.UpsertFile(session, "index.html", "html", "<h1>Saved</h1>", true);
                HtmlArtifactToolExecutor.UpsertFile(session, "index.html", "html", "<h1>Saved again</h1>", true);
                session.Messages.Add(new ChatMessage { Role = "user", Content = "hello" });
                store.Save(session);

                store.ClearMessages(session.Host, session.DocumentKey, session.Id);
                var loaded = store.Load(session.Host, session.DocumentKey, session.Id);
                AssertEqual(0, loaded.Messages.Count, "messages cleared");
                AssertEqual(1, loaded.HtmlWorkspace.Files.Count, "html workspace preserved");
                AssertEqual("index.html", loaded.HtmlWorkspace.ActiveFileId, "active html preserved");
                AssertEqual(1, loaded.HtmlWorkspace.History.Count, "html history preserved");
                HtmlArtifactToolExecutor.RestoreSnapshot(loaded, loaded.HtmlWorkspace.History[0].Id);
                AssertEqual("<h1>Saved</h1>", loaded.HtmlWorkspace.Files[0].Content, "persisted html history supports undo");

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

        private static void HtmlWorkspaceHistoryIsBoundedAndTransportIsCompact()
        {
            var oversizedOnly = HtmlWorkspaceHistoryPolicy.Trim(new[]
            {
                new HtmlWorkspaceSnapshot
                {
                    Files = new List<HtmlWorkspaceFile>
                    {
                        new HtmlWorkspaceFile { Id = "large", Path = "large.html", Content = new string('x', (int)HtmlWorkspaceHistoryPolicy.MaxContentCharacters + 1) }
                    }
                }
            });
            AssertEqual(0, oversizedOnly.Count, "single oversized history snapshot is skipped");

            var largeSession = new ChatSession { Title = "HTML bounded history" };
            for (var revision = 0; revision < 12; revision++)
            {
                HtmlArtifactToolExecutor.UpsertFile(
                    largeSession,
                    "index.html",
                    "html",
                    new string((char)('a' + revision), 280000),
                    true);
            }

            var storedCharacters = largeSession.HtmlWorkspace.History
                .Sum(snapshot => HtmlWorkspaceHistoryPolicy.EstimateContentCharacters(snapshot));
            AssertTrue(largeSession.HtmlWorkspace.History.Count < HtmlWorkspaceHistoryPolicy.MaxItems, "large html history is pruned before item limit");
            AssertTrue(storedCharacters <= HtmlWorkspaceHistoryPolicy.MaxContentCharacters, "large html history stays within character budget");
            AssertEqual('k', largeSession.HtmlWorkspace.History[0].Files[0].Content[0], "latest undo snapshot is retained");

            HtmlArtifactToolExecutor.RestoreSnapshot(largeSession, largeSession.HtmlWorkspace.History[0].Id);
            AssertEqual('k', largeSession.HtmlWorkspace.Files[0].Content[0], "bounded history still supports undo");
            HtmlArtifactToolExecutor.RedoSnapshot(largeSession, largeSession.HtmlWorkspace.RedoHistory[0].Id);
            AssertEqual('l', largeSession.HtmlWorkspace.Files[0].Content[0], "bounded history still supports redo");

            var transportSession = new ChatSession { Title = "HTML compact transport" };
            HtmlArtifactToolExecutor.UpsertFile(transportSession, "index.html", "html", "CURRENT_FIRST", true);
            HtmlArtifactToolExecutor.UpsertFile(transportSession, "index.html", "html", "HISTORY_SECOND", true);
            HtmlArtifactToolExecutor.UpsertFile(transportSession, "index.html", "html", "CURRENT_THIRD", true);

            var bridgeJson = JsonConvert.SerializeObject(HtmlWorkspaceDto.From(transportSession.HtmlWorkspace));
            AssertContains(bridgeJson, "CURRENT_THIRD", "bridge workspace includes current file content");
            AssertTrue(bridgeJson.IndexOf("CURRENT_FIRST", StringComparison.Ordinal) < 0, "bridge workspace omits old snapshot bodies");
            AssertTrue(bridgeJson.IndexOf("HISTORY_SECOND", StringComparison.Ordinal) < 0, "bridge workspace history contains metadata only");

            var manifestResult = new HtmlArtifactToolExecutor().ExecuteControllerTool(
                new ToolCommand { ToolId = HtmlArtifactToolExecutor.ReadWorkspaceToolId },
                transportSession,
                false);
            AssertTrue(manifestResult.Success, "html workspace manifest read succeeds");
            AssertContains(manifestResult.DataJson, "contentCharacters", "html workspace manifest contains sizes");
            AssertTrue(manifestResult.DataJson.IndexOf("CURRENT_THIRD", StringComparison.Ordinal) < 0, "html workspace manifest omits file bodies");

            var toolResult = new HtmlArtifactToolExecutor().ExecuteControllerTool(
                new ToolCommand
                {
                    ToolId = HtmlArtifactToolExecutor.ReadWorkspaceToolId,
                    Arguments = { ["resourceType"] = "file", ["name"] = "index.html" }
                },
                transportSession,
                false);
            AssertTrue(toolResult.Success, "html workspace compact read succeeds");
            AssertContains(toolResult.DataJson, "CURRENT_THIRD", "tool workspace includes current file content");
            AssertTrue(toolResult.DataJson.IndexOf("CURRENT_FIRST", StringComparison.Ordinal) < 0, "tool workspace omits old snapshot bodies");
            AssertTrue(toolResult.DataJson.IndexOf("HISTORY_SECOND", StringComparison.Ordinal) < 0, "tool workspace omits latest history body");
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
                command.Arguments["parameters"] = JObject.Parse(EmptyFormalToolSchema);
                command.Arguments["executor"] = "pipeline";
                command.Arguments["pipeline"] = JObject.Parse("{\"version\":1,\"steps\":[{\"toolId\":\"excel.list_sheets\",\"arguments\":{}}]}");

                var result = executor.Execute(command, new List<ToolDefinition>(adapter.GetBuiltInTools()), new AppSettings(), false, false);

                AssertTrue(result.Success, "tool validate succeeds");
                AssertContains(result.Message, "valid", "tool validate message");
                AssertTrue(!HasTool(toolStore.Load(), "excel.validated"), "tool validate does not save");
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
                AssertEqual("excel.write_range", adapter.Executed[1].ToolId, "manual second tool");
            });
        }
    }
}
