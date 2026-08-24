using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
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
                    "{\"toolId\":\"common.skills_upsert\",\"arguments\":{\"id\":\"common.saved\",\"description\":\"Saved test skill.\",\"bodyMarkdown\":\"test\"}}" +
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

        private static void CompactToolCatalogRejectsRemovedAliases()
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

            var removedByHost = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                { "Excel", new[] { "excel.list_sheets", "excel.write_table", "excel.add_chart" } },
                { "Word", new[] { "word.read_document", "word.insert_text", "word.apply_style" } },
                { "PowerPoint", new[] { "powerpoint.list_slides", "powerpoint.set_shape_text", "powerpoint.add_picture" } },
                { "Outlook", new[] { "outlook.read_current_mail", "outlook.create_mail_draft", "outlook.mark_as_read" } }
            };
            foreach (var pair in removedByHost)
            {
                WithTempExecutor(FakeOfficeAdapter.ForHost(pair.Key), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                    foreach (var id in pair.Value)
                    {
                        var result = executor.Execute(new ToolCommand { ToolId = id }, tools, new AppSettings(), false, false);
                        AssertEqual("unknown_tool", result.ErrorCode, id + " is removed");
                    }
                });
            }

            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                foreach (var id in new[] { "common.skills_list", "common.tools_create", "common.prompts_read_defaults", "common.html_workspace_upsert_file" })
                {
                    var result = executor.Execute(new ToolCommand { ToolId = id }, tools, new AppSettings(), false, false);
                    AssertEqual("unknown_tool", result.ErrorCode, id + " is removed");
                }

                var removedPipeline = CustomTool("Excel", "excel.removed_id_pipeline");
                removedPipeline.PipelineJson = "{\"steps\":[{\"toolId\":\"excel.list_shapes\",\"arguments\":{}}]}";
                var prepared = AgentRunService.PrepareToolsForRun(adapter.GetBuiltInTools().Concat(new[] { removedPipeline }));
                AssertTrue(prepared.All(item => item.Id != removedPipeline.Id), "pipeline with removed id stays invalid");
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

                var fileCommand = new ToolCommand { ToolId = "common.html_workspace_upsert" };
                fileCommand.Arguments["resourceType"] = "file";
                fileCommand.Arguments["name"] = "index.html";
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

                var dataCommand = new ToolCommand { ToolId = "common.html_workspace_upsert" };
                dataCommand.Arguments["resourceType"] = "data";
                dataCommand.Arguments["name"] = "rows";
                dataCommand.Arguments["content"] = "{\"items\":[1,2]}";
                var dataResult = executor.Execute(dataCommand, tools, new AppSettings(), false, false, session);
                AssertTrue(dataResult.Success, "html workspace data save succeeds");
                AssertEqual(1, session.HtmlWorkspace.DataSources.Count, "html data count");

                var readResult = executor.Execute(new ToolCommand
                {
                    ToolId = "common.html_workspace_read",
                    Arguments = { ["resourceType"] = "data", ["name"] = "rows" }
                }, tools, new AppSettings(), false, false, session);
                AssertTrue(readResult.Success, "html workspace read succeeds");
                AssertContains(readResult.DataJson, "rnassistant.htmlWorkspace", "workspace result type");
                AssertContains(readResult.DataJson, "items", "workspace data included");

                var incompleteRead = new ToolCommand { ToolId = "common.html_workspace_read" };
                incompleteRead.Arguments["name"] = "rows";
                var incompleteResult = executor.Execute(incompleteRead, tools, new AppSettings(), false, false, session);
                AssertTrue(!incompleteResult.Success, "HTML read rejects incomplete selector");
                AssertContains(incompleteResult.Message, "resourceType is required", "HTML selector diagnostic");

                var deleteScript = new ToolCommand { ToolId = "common.html_workspace_delete" };
                deleteScript.Arguments["resourceType"] = "file";
                deleteScript.Arguments["name"] = "app.js";
                var deleteScriptResult = executor.Execute(deleteScript, tools, new AppSettings(), false, false, session);
                AssertTrue(deleteScriptResult.Success, "html workspace file delete succeeds");
                AssertEqual(1, session.HtmlWorkspace.Files.Count, "html script deleted");

                var deleteData = new ToolCommand { ToolId = "common.html_workspace_delete" };
                deleteData.Arguments["resourceType"] = "data";
                deleteData.Arguments["name"] = "rows";
                var deleteDataResult = executor.Execute(deleteData, tools, new AppSettings(), false, false, session);
                AssertTrue(deleteDataResult.Success, "html workspace data delete succeeds");
                AssertEqual(0, session.HtmlWorkspace.DataSources.Count, "html data deleted");
                HtmlArtifactToolExecutor.RestoreSnapshot(session, session.HtmlWorkspace.History[0].Id);
                AssertEqual(1, session.HtmlWorkspace.DataSources.Count, "html data delete can be undone");

                var boundSession = new ChatSession
                {
                    Host = adapter.HostName,
                    DocumentKey = adapter.DocumentKey,
                    DocumentTitle = adapter.DocumentTitle,
                    Title = "Bound HTML data"
                };
                var bind = new ToolCommand { ToolId = HtmlArtifactToolExecutor.BindDataToolId };
                bind.Arguments["dataName"] = "sales";
                bind.Arguments["sourceTool"] = "excel.read_range";
                bind.Arguments["sourceArguments"] = new JObject
                {
                    ["sheet"] = "Data",
                    ["address"] = "A1:B4",
                    ["content"] = "values"
                };
                bind.Arguments["transform"] = "table";
                bind.Arguments["headers"] = "firstRow";
                bind.Arguments["refreshPolicy"] = "on_preview";
                var bindResult = executor.Execute(bind, tools, new AppSettings(), false, false, boundSession);
                AssertTrue(bindResult.Success, "html Office data binding succeeds");
                AssertEqual("excel.read_range", boundSession.HtmlWorkspace.DataSources[0].Binding.ToolId, "html binding source persisted");
                var table = JObject.Parse(boundSession.HtmlWorkspace.DataSources[0].Json);
                AssertEqual("rnassistant.table.v1", (string)table["schema"], "html table transform schema");
                AssertEqual(3, table["rowCount"].Value<int>(), "html table transform row count");
                AssertEqual("Jan", (string)table["rows"][0]["month"], "html table header becomes stable key");

                adapter.ExecuteTool(Command("excel.write_range", "kind", "value", "sheet", "Data", "address", "B2", "value", "999"));
                var historyBeforeRefresh = boundSession.HtmlWorkspace.History.Count;
                var refresh = new ToolCommand { ToolId = HtmlArtifactToolExecutor.RefreshDataToolId };
                refresh.Arguments["name"] = "sales";
                var refreshResult = executor.Execute(refresh, tools, new AppSettings(), false, false, boundSession);
                AssertTrue(refreshResult.Success, "bound HTML data refresh succeeds");
                AssertContains(boundSession.HtmlWorkspace.DataSources[0].Json, "999", "bound HTML data refreshes without model rewrite");
                AssertEqual(historyBeforeRefresh, boundSession.HtmlWorkspace.History.Count, "automatic data refresh does not spam undo history");

                var freeze = new ToolCommand { ToolId = HtmlArtifactToolExecutor.FreezeDataToolId };
                freeze.Arguments["name"] = "sales";
                var freezeResult = executor.Execute(freeze, tools, new AppSettings(), false, false, boundSession);
                AssertTrue(freezeResult.Success, "bound HTML data can be frozen");
                AssertTrue(boundSession.HtmlWorkspace.DataSources[0].Binding == null, "freeze keeps JSON and removes binding");

                var invalidData = new ToolCommand { ToolId = "common.html_workspace_upsert" };
                invalidData.Arguments["resourceType"] = "data";
                invalidData.Arguments["name"] = "bad";
                invalidData.Arguments["content"] = "{ bad";
                var invalidResult = executor.Execute(invalidData, tools, new AppSettings(), false, false, session);
                AssertTrue(!invalidResult.Success, "invalid html data fails");
                AssertContains(invalidResult.Message, "Invalid HTML workspace JSON", "invalid html data message");

                HtmlArtifactToolExecutor.UpsertFile(session, "styles.css", "css", "body{}", false);
                var cssActive = new ToolCommand { ToolId = "common.html_workspace_set_active" };
                cssActive.Arguments["name"] = "styles.css";
                var cssActiveResult = executor.Execute(cssActive, tools, new AppSettings(), false, false, session);
                AssertTrue(!cssActiveResult.Success, "non-html file cannot become active preview");

                var failedSession = new ChatSession { Title = "HTML failed mutation" };
                HtmlArtifactToolExecutor.UpsertFile(failedSession, "index.html", "html", "<h1>First</h1>", true);
                HtmlArtifactToolExecutor.UpsertFile(failedSession, "index.html", "html", "<h1>Second</h1>", true);
                HtmlArtifactToolExecutor.RestoreSnapshot(failedSession, failedSession.HtmlWorkspace.History[0].Id);
                var failedHistoryCount = failedSession.HtmlWorkspace.History.Count;
                var failedRedoCount = failedSession.HtmlWorkspace.RedoBranches.Count;
                var missingActive = new ToolCommand { ToolId = "common.html_workspace_set_active" };
                missingActive.Arguments["name"] = "missing.html";
                var missingResult = executor.Execute(missingActive, tools, new AppSettings(), false, false, failedSession);
                AssertTrue(!missingResult.Success, "missing active HTML file fails");
                AssertEqual(failedHistoryCount, failedSession.HtmlWorkspace.History.Count, "failed set-active preserves history");
                AssertEqual(failedRedoCount, failedSession.HtmlWorkspace.RedoBranches.Count, "failed set-active preserves redo branches");
                var missingDryRun = executor.Execute(missingActive, tools, new AppSettings(), true, false, failedSession);
                AssertTrue(!missingDryRun.Success, "set-active dry run validates file existence");

                var absolutePath = new ToolCommand { ToolId = "common.html_workspace_upsert" };
                absolutePath.Arguments["resourceType"] = "file";
                absolutePath.Arguments["name"] = "/index.html";
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
            AssertEqual(1, session.HtmlWorkspace.RedoBranches.Count, "html undo exposes one redo branch");

            HtmlArtifactToolExecutor.RedoSnapshot(session, session.HtmlWorkspace.RedoBranches[0].Id);
            AssertContains(session.HtmlWorkspace.Files[0].Content, "Second", "html redo restores undone file content");
            AssertEqual("index.html", session.HtmlWorkspace.ActiveFileId, "html redo keeps active file");
            AssertEqual(0, session.HtmlWorkspace.RedoBranches.Count, "html redo has no direct child after moving forward");
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
            HtmlArtifactToolExecutor.RedoSnapshot(largeSession, largeSession.HtmlWorkspace.RedoBranches[0].Id);
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
                command.Arguments["pipeline"] = JObject.Parse("{\"version\":1,\"steps\":[{\"toolId\":\"excel.inspect\",\"arguments\":{\"kind\":\"sheets\"}}]}");

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
