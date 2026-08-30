using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ManualReadOnlyRunSkipsChatLease()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                AssertTrue(!executor.RequiresSessionLeaseForManualRun("excel.read_range", tools),
                    "read-only Excel tool can run beside an active chat");
                AssertTrue(executor.RequiresSessionLeaseForManualRun("excel.write_range", tools),
                    "document mutation keeps the chat lease");

                AssertTrue(!executor.RequiresSessionLeaseForManualRun("common.resources_read", tools),
                    "resource reads can run beside an active chat");

                adapter.VbaModuleCode = "A";
                var agentSession = NewSession(adapter);
                AssertEqual("A", ReadVbaSource(executor, agentSession, "Module1").Text,
                    "agent observes the original VBA source through resources");
                adapter.VbaModuleCode = "B";
                var manualSnapshot = OfficeToolExecutor.CreateIsolatedManualSession(agentSession);
                AssertTrue(!string.Equals(agentSession.Id, manualSnapshot.Id, StringComparison.OrdinalIgnoreCase),
                    "manual read snapshot has an isolated observation identity");
                AssertEqual("B", ReadVbaSource(executor, manualSnapshot, "Module1").Text,
                    "manual library resource read succeeds on current VBA source");
                var staleAgent = executor.Execute(
                    Command("common.vba_apply_patch", "moduleName", "Module1", "patch", new JArray(new JObject
                    {
                        ["op"] = "replace",
                        ["find"] = "B",
                        ["text"] = "C"
                    })),
                    tools,
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false,
                    agentSession);
                AssertEqual("stale_vba_module", staleAgent.ErrorCode,
                    "manual library read does not silently refresh the running agent snapshot");
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
                var disabled = CustomTool("Excel", "excel.disabled_custom");
                disabled.Enabled = false;
                var tools = new List<ToolDefinition>(builtIns);
                tools.Add(disabled);

                var disabledResult = executor.Execute(
                    new ToolCommand { ToolId = "excel.disabled_custom" },
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
                { "Excel", 15 },
                { "Word", 9 },
                { "PowerPoint", 9 },
                { "Outlook", 5 }
            };
            foreach (var pair in expectedHostCounts)
            {
                var runnable = ConversationRunService.PrepareToolsForRun(FakeOfficeAdapter.ForHost(pair.Key).GetBuiltInTools());
                AssertEqual(pair.Value, runnable.Count, pair.Key + " compact runnable tool count");
            }

            var removedByHost = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                { "Excel", new[] { "excel.get_context", "excel.get_selection", "excel.list_sheets", "excel.write_table", "excel.add_chart" } },
                { "Word", new[] { "word.get_context", "word.read_document", "word.insert_text", "word.apply_style" } },
                { "PowerPoint", new[] { "powerpoint.get_context", "powerpoint.get_selection", "powerpoint.list_slides", "powerpoint.set_shape_text", "powerpoint.add_picture" } },
                { "Outlook", new[] { "outlook.get_context", "outlook.read_current_mail", "outlook.create_mail_draft", "outlook.mark_as_read" } }
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
                foreach (var id in new[]
                {
                    "common.skills_list",
                    "common.tools_create",
                    "common.prompts_read_defaults",
                    "common.html_workspace_upsert_file",
                    "common.plan_read",
                    "common.html_workspace_read",
                    "common.html_workspace_search"
                })
                {
                    var result = executor.Execute(new ToolCommand { ToolId = id }, tools, new AppSettings(), false, false);
                    AssertEqual("unknown_tool", result.ErrorCode, id + " is removed");
                }

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
                var dryResources = new ResourceGatewayService()
                    .List(drySession, ChatArtifactResourceProvider.ProviderName, ChatHtmlResourceCatalog.FileKind, null, 10);
                AssertEqual(0, dryResources.Items.Count, "empty html workspace has no file resources");
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

                var gateway = new ResourceGatewayService();
                var dataResource = gateway
                    .List(session, ChatArtifactResourceProvider.ProviderName, ChatHtmlResourceCatalog.DataKind, null, 10)
                    .Items.Single(item => item.Title == "rows");
                var readResult = ReadResource(gateway, session, dataResource.Reference.Uri, "text", null, 8000).Result;
                AssertContains(readResult.Text, "items", "html data reads through the resource gateway");

                var removedRead = executor.Execute(
                    new ToolCommand { ToolId = "common.html_workspace_read" },
                    tools,
                    new AppSettings(),
                    false,
                    false,
                    session);
                var removedSearch = executor.Execute(
                    new ToolCommand { ToolId = "common.html_workspace_search" },
                    tools,
                    new AppSettings(),
                    false,
                    false,
                    session);
                AssertEqual("unknown_tool", removedRead.ErrorCode, "removed HTML read id stays unknown");
                AssertEqual("unknown_tool", removedSearch.ErrorCode, "removed HTML search id stays unknown");

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
                var directRead = executor.Execute(Command("excel.read_range", "sheet", "Data", "address", "A1:B4", "content", "values"), tools, new AppSettings(), false, false, boundSession);
                AssertTrue(directRead.Success, "published excel.read_range contract executes directly");

                var invalidBind = new ToolCommand { ToolId = HtmlArtifactToolExecutor.BindDataToolId };
                invalidBind.Arguments["dataName"] = "invalid";
                invalidBind.Arguments["sourceTool"] = "excel.read_range";
                invalidBind.Arguments["sourceArguments"] = new JObject
                {
                    ["sheet"] = "Data",
                    ["address"] = "A1:B4",
                    ["content"] = "values",
                    ["kind"] = "range"
                };
                var invalidBindResult = executor.Execute(invalidBind, tools, new AppSettings(), false, false, boundSession);
                AssertTrue(!invalidBindResult.Success, "HTML bind rejects fields from another source schema before execution");
                AssertContains(invalidBindResult.Message, "unsupported property kind", "HTML bind reports the exact invalid nested field");

                var bindDefinition = executor.GetControllerTools().Single(item => item.Id == HtmlArtifactToolExecutor.BindDataToolId);
                var bindResponseSchema = JObject.Parse(ConversationResponseSchemaBuilder.Build(new[] { bindDefinition }));
                var bindVariants = bindResponseSchema.SelectToken("properties.tool_calls.items.anyOf[0].properties.arguments.anyOf") as JArray;
                var rangeVariant = bindVariants == null ? null : bindVariants.OfType<JObject>().FirstOrDefault(item =>
                    string.Equals((string)item.SelectToken("properties.sourceTool.enum[0]"), "excel.read_range", StringComparison.OrdinalIgnoreCase));
                AssertTrue(rangeVariant != null, "HTML bind strict schema has an excel.read_range branch");
                AssertTrue(rangeVariant.SelectToken("properties.sourceArguments.properties.kind") == null, "HTML bind range branch does not advertise inspect.kind");
                AssertTrue(rangeVariant.SelectToken("properties.sourceArguments.properties.address") != null, "HTML bind range branch exposes read_range.address");

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

                var gateEntries = 0;
                var gateExits = 0;
                var gatedHtml = new HtmlArtifactToolExecutor(
                    adapter,
                    adapter.GetBuiltInTools(),
                    ignoredSession =>
                    {
                        gateEntries += 1;
                        return new CallbackDisposable(() => gateExits += 1);
                    });
                var gatedSession = new ChatSession
                {
                    Host = adapter.HostName,
                    DocumentKey = adapter.DocumentKey,
                    DocumentTitle = adapter.DocumentTitle
                };
                var gatedBindResult = gatedHtml.ExecuteControllerTool(bind, gatedSession, false);
                AssertTrue(gatedBindResult.Success, "gated HTML binding source read succeeds");
                AssertEqual(1, gateEntries, "HTML binding enters the shared live-document read gate");
                AssertEqual(1, gateExits, "HTML binding releases the shared live-document read gate");

                var write = executor.Execute(Command("excel.write_range", "kind", "value", "sheet", "Data",
                    "address", "B2", "value", "999"), tools, new AppSettings(), false, false, boundSession);
                AssertTrue(write.Success, "HTML refresh fixture writes through the typed Excel owner");
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

        private static void HtmlWorkspaceSourceToolsAreBoundedAndAtomic()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = new ChatSession
                {
                    Host = adapter.HostName,
                    DocumentKey = adapter.DocumentKey,
                    DocumentTitle = adapter.DocumentTitle,
                    Title = "HTML source tools"
                };
                var tools = new List<ToolDefinition>(adapter.GetBuiltInTools());
                HtmlArtifactToolExecutor.UpsertFile(session, "index.html", "html", "one\ntwo\nthree\nfour", true);
                HtmlArtifactToolExecutor.UpsertFile(session, "app.js", "script", "alpha\nconst beta = 1;\nalpha beta;", false);

                var gateway = new ResourceGatewayService();
                var files = gateway.List(
                    session,
                    ChatArtifactResourceProvider.ProviderName,
                    ChatHtmlResourceCatalog.FileKind,
                    null,
                    10);
                var indexResource = files.Items.Single(item => item.Title == "index.html");
                var readResult = ReadResource(gateway, session, indexResource.Reference.Uri, "source", null, 128).Result;
                AssertEqual("one\ntwo\nthree\nfour", readResult.Text, "HTML source reads through canonical resource URI");
                AssertEqual(ResourceRepresentations.Source, readResult.Representation, "HTML file advertises source representation");

                var searchResult = gateway.Search(
                    session,
                    ChatArtifactResourceProvider.ProviderName,
                    "beta",
                    ChatHtmlResourceCatalog.FileKind,
                    1,
                    128);
                AssertEqual(1, searchResult.Matches.Count, "HTML source search is bounded");
                AssertEqual("app.js", searchResult.Matches[0].Title, "HTML search identifies the exact source resource");
                AssertContains(searchResult.Matches[0].Snippet, "beta", "HTML search returns a bounded snippet");

                var patch = new ToolCommand { ToolId = HtmlArtifactToolExecutor.ApplyPatchToolId };
                patch.Arguments["name"] = "app.js";
                patch.Arguments["patch"] = new JArray
                {
                    new JObject { ["op"] = "replace", ["find"] = "const beta = 1;", ["text"] = "const beta = 2;" },
                    new JObject { ["op"] = "insertAfter", ["find"] = "alpha beta;", ["text"] = "\nwindow.ready = true;" }
                };
                var artifactCount = session.Artifacts.Count;
                var patchResult = executor.Execute(patch, tools, new AppSettings(), false, false, session);
                AssertTrue(patchResult.Success, "HTML structured patch succeeds");
                AssertContains(session.HtmlWorkspace.Files.Single(item => item.Path == "app.js").Content, "beta = 2", "HTML patch replaces exact source");
                AssertContains(session.HtmlWorkspace.Files.Single(item => item.Path == "app.js").Content, "window.ready", "HTML patch inserts source");
                AssertEqual(artifactCount + 1, session.Artifacts.Count, "HTML patch creates one artifact revision");

                var failedPatch = new ToolCommand { ToolId = HtmlArtifactToolExecutor.ApplyPatchToolId };
                failedPatch.Arguments["name"] = "app.js";
                failedPatch.Arguments["patch"] = new JArray
                {
                    new JObject { ["op"] = "replace", ["find"] = "alpha", ["text"] = "omega" }
                };
                var beforeFailure = session.HtmlWorkspace.Files.Single(item => item.Path == "app.js").Content;
                artifactCount = session.Artifacts.Count;
                var failedResult = executor.Execute(failedPatch, tools, new AppSettings(), false, false, session);
                AssertTrue(!failedResult.Success, "ambiguous HTML patch fails");
                AssertEqual("text_patch_ambiguous", failedResult.ErrorCode, "ambiguous HTML patch has stable code");
                AssertEqual(beforeFailure, session.HtmlWorkspace.Files.Single(item => item.Path == "app.js").Content, "failed HTML patch is atomic");
                AssertEqual(artifactCount, session.Artifacts.Count, "failed HTML patch creates no revision");

                var strictCreate = new ToolCommand { ToolId = HtmlArtifactToolExecutor.UpsertToolId };
                strictCreate.Arguments["resourceType"] = "file";
                strictCreate.Arguments["name"] = "index.html";
                strictCreate.Arguments["content"] = "overwrite";
                strictCreate.Arguments["mode"] = "createOnly";
                var strictCreateResult = executor.Execute(strictCreate, tools, new AppSettings(), false, false, session);
                AssertTrue(!strictCreateResult.Success, "HTML createOnly rejects an existing file");
                AssertEqual("one\ntwo\nthree\nfour", session.HtmlWorkspace.Files.Single(item => item.Path == "index.html").Content, "strict upsert preserves existing content");

                var strictUpdate = new ToolCommand { ToolId = HtmlArtifactToolExecutor.UpsertToolId };
                strictUpdate.Arguments["resourceType"] = "file";
                strictUpdate.Arguments["name"] = "missing.css";
                strictUpdate.Arguments["content"] = "body{}";
                strictUpdate.Arguments["mode"] = "updateOnly";
                var strictUpdateResult = executor.Execute(strictUpdate, tools, new AppSettings(), false, false, session);
                AssertTrue(!strictUpdateResult.Success, "HTML updateOnly rejects a missing file");

                var inspectSession = new ChatSession
                {
                    Host = adapter.HostName,
                    DocumentKey = adapter.DocumentKey,
                    DocumentTitle = adapter.DocumentTitle,
                    Title = "HTML inspection"
                };
                HtmlArtifactToolExecutor.UpsertFile(inspectSession, "index.html", "html", "<main id=\"total\"></main><div id='total'></div><script src=\"local.js\"></script>", true);
                HtmlArtifactToolExecutor.UpsertFile(inspectSession, "styles.css", "css", "@import url('theme.css');", false);
                HtmlArtifactToolExecutor.UpsertFile(inspectSession, "app.js", "script", "import thing from 'pkg';\ndocument.getElementById('missing');\nRNAssistantData.missingData;", false);
                var inspect = new ToolCommand { ToolId = HtmlArtifactToolExecutor.InspectWorkspaceToolId };
                var inspectResult = executor.Execute(inspect, tools, new AppSettings(), false, false, inspectSession);
                AssertTrue(inspectResult.Success, "HTML static inspection succeeds even when findings exist");
                var inspection = JObject.Parse(inspectResult.DataJson);
                AssertTrue(!(bool)inspection["runtimeExecuted"], "HTML inspection identifies static-only scope");
                AssertTrue(!(bool)inspection["passed"], "HTML inspection fails preflight when errors exist");
                AssertTrue((int)inspection["errorCount"] >= 4, "HTML inspection reports assembly and CSP errors");
                AssertTrue((int)inspection["warningCount"] >= 2, "HTML inspection reports likely missing references");
                AssertTrue(inspection["issues"].Any(item => (string)item["code"] == "html.duplicate_id"), "HTML inspection finds duplicate ids");
                AssertTrue(inspection["issues"].Any(item => (string)item["code"] == "script.module_syntax_unsupported"), "HTML inspection finds module syntax");
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

            var gateway = new ResourceGatewayService();
            var activeArtifact = transportSession.Artifacts.Single(item => item.Id == transportSession.ActiveHtmlArtifactId);
            var manifestResult = ReadResource(
                gateway,
                transportSession,
                ArtifactUri(transportSession, activeArtifact),
                ResourceRepresentations.Structure,
                null,
                8000).Result;
            AssertContains(manifestResult.Text, "resources", "html structure contains the resource manifest");
            AssertContains(manifestResult.Text, "index.html", "html structure identifies current members");
            AssertTrue(manifestResult.Text.IndexOf("CURRENT_THIRD", StringComparison.Ordinal) < 0, "html structure omits file bodies");

            var fileResource = gateway.List(
                transportSession,
                ChatArtifactResourceProvider.ProviderName,
                ChatHtmlResourceCatalog.FileKind,
                null,
                10).Items.Single(item => item.Title == "index.html");
            var sourceResult = ReadResource(
                gateway,
                transportSession,
                fileResource.Reference.Uri,
                ResourceRepresentations.Source,
                null,
                8000).Result;
            AssertContains(sourceResult.Text, "CURRENT_THIRD", "resource read includes current file content");
            AssertTrue(sourceResult.Text.IndexOf("CURRENT_FIRST", StringComparison.Ordinal) < 0, "resource read omits old snapshot bodies");
            AssertTrue(sourceResult.Text.IndexOf("HISTORY_SECOND", StringComparison.Ordinal) < 0, "resource read omits latest history body");
        }

        private static void ToolValidateChecksPayloadWithoutSaving()
        {
            WithTempPaths(paths =>
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var store = new ToolStore(paths);
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths), store);
                var tool = CustomTool("Excel", "excel.validated");
                var command = Command("common.tools_validate", "id", tool.Id, "host", tool.Host,
                    "description", tool.Description, "executor", "vba", "components", ToolComponentsPayload(tool));
                var result = executor.Execute(command, adapter.GetBuiltInTools().ToList(), new AppSettings(), false, false);
                AssertTrue(result.Success, "VBA tool validates: " + result.Message);
                AssertTrue(!HasTool(store.Load(), tool.Id), "validation does not save");
                command.Arguments["parameterDefinitions"] = new JArray();
                var compact = executor.Execute(command, adapter.GetBuiltInTools().ToList(), new AppSettings(), false, false);
                AssertTrue(compact.Success, "compact parameters remain supported");
                AssertContains(compact.DataJson, "\"parameters\":{", "native parameter schema returned");
                command.Arguments["parameters"] = JObject.Parse(EmptyFormalToolSchema);
                var ambiguous = executor.Execute(command, adapter.GetBuiltInTools().ToList(), new AppSettings(), false, false);
                AssertEqual("tool_parameters_ambiguous", ambiguous.ErrorCode, "ambiguous parameter input rejected");
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

    }
}
