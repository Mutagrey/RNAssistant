using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
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
                var staleAgent = executor.ExecuteManual(
                    Command("common.vba_apply_patch", "moduleName", "Module1", "patch", new JArray(new JObject
                    {
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
                var builtIns = new List<ToolCatalogEntry>(OfficeToolCatalog.ForHost(fake.HostName));
                var unknown = executor.ExecuteManual(
                    new ToolInvocation { ToolId = "create_worksheet" },
                    builtIns,
                    new AppSettings(),
                    false,
                    false);

                AssertTrue(!unknown.Success, "unknown tool should fail");
                AssertContains(unknown.Message, "Unknown tool id", "unknown tool message");
                AssertContains(unknown.Message, "excel.add_sheet", "unknown tool suggestion");
                AssertContains(unknown.DataJson, "availableToolIds", "unknown tool diagnostics");
                AssertEqual(0, fake.TotalBackendCallCount, "unknown adapter count");

                fake.ClearBackendCalls();
                var disabled = CustomTool("Excel", "excel.disabled_custom");
                disabled.Enabled = false;
                var tools = new List<ToolCatalogEntry>(builtIns);
                tools.Add(disabled);

                var disabledResult = executor.ExecuteManual(
                    new ToolInvocation { ToolId = "excel.disabled_custom" },
                    tools,
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false);

                AssertTrue(!disabledResult.Success, "disabled tool should fail");
                AssertContains(disabledResult.Message, "Tool is disabled", "disabled tool message");
                AssertEqual(0, fake.TotalBackendCallCount, "disabled adapter count");
            });
        }

        private static void RemovedToolIdsAreUnknown()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var command = new ToolInvocation { ToolId = "common.render_html" };
                var result = executor.ExecuteManual(
                    command,
                    new List<ToolCatalogEntry>(OfficeToolCatalog.ForHost(adapter.HostName)),
                    new AppSettings(),
                    false,
                    false);
                AssertTrue(!result.Success, "removed html artifact fails");
                AssertContains(result.Message, "Unknown tool id", "removed html artifact diagnostic");
            });

            WithTempExecutor(FakeOfficeAdapter.ForHost("Outlook"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var result = executor.ExecuteManual(
                    new ToolInvocation { ToolId = "outlook.draft_reply" },
                    new List<ToolCatalogEntry>(OfficeToolCatalog.ForHost(adapter.HostName)),
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
                var runnable = ConversationRunService.PrepareToolsForRun(OfficeToolCatalog.ForHost(pair.Key));
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
                    var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                    foreach (var id in pair.Value)
                    {
                        var result = executor.ExecuteManual(new ToolInvocation { ToolId = id }, tools, new AppSettings(), false, false);
                        AssertEqual("unknown_tool", result.ErrorCode, id + " is removed");
                    }
                });
            }

            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                foreach (var id in new[]
                {
                    "common.skills_list",
                    "common.tools_create",
                    "common.prompts_read_defaults",
                    "common.html_workspace_upsert_file",
                    "common.html_workspace_upsert",
                    "common.html_workspace_inspect",
                    "common.html_workspace_set_active",
                    "common.plan_read",
                    "common.html_workspace_read",
                    "common.html_workspace_search"
                })
                {
                    var result = executor.ExecuteManual(new ToolInvocation { ToolId = id }, tools, new AppSettings(), false, false);
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
                var tools = new List<ToolCatalogEntry>(OfficeToolCatalog.ForHost(adapter.HostName));

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

                var fileCommand = new ToolInvocation { ToolId = HtmlWorkspaceToolCatalog.WriteFileToolId };
                fileCommand.Arguments["path"] = "index.html";
                fileCommand.Arguments["content"] = "<h1>Report</h1>";

                var fileResult = executor.ExecuteManual(fileCommand, tools, new AppSettings(), false, false, session);
                AssertTrue(fileResult.Success, "html workspace file save succeeds");
                AssertEqual(1, session.HtmlWorkspace.Files.Count, "html file count");
                AssertEqual("index.html", session.HtmlWorkspace.ActiveFileId, "active html file");

                var scriptCommand = new ToolInvocation { ToolId = HtmlWorkspaceToolCatalog.WriteFileToolId };
                scriptCommand.Arguments["path"] = "app.js";
                scriptCommand.Arguments["content"] = "window.reportReady = true;";
                var scriptResult = executor.ExecuteManual(scriptCommand, tools, new AppSettings(), false, false, session);
                AssertTrue(scriptResult.Success, "html workspace script save succeeds");
                AssertEqual(2, session.HtmlWorkspace.Files.Count, "html file and script count");
                AssertEqual("script", session.HtmlWorkspace.Files[1].Kind, "script kind normalized");

                var dataCommand = new ToolInvocation { ToolId = HtmlWorkspaceToolCatalog.WriteDataToolId };
                dataCommand.Arguments["name"] = "rows";
                dataCommand.Arguments["json"] = "{\"items\":[1,2]}";
                var dataResult = executor.ExecuteManual(dataCommand, tools, new AppSettings(), false, false, session);
                AssertTrue(dataResult.Success, "html workspace data save succeeds");
                AssertEqual(1, session.HtmlWorkspace.DataSources.Count, "html data count");

                var gateway = new ResourceGatewayService();
                var dataResource = gateway
                    .List(session, ChatArtifactResourceProvider.ProviderName, ChatHtmlResourceCatalog.DataKind, null, 10)
                    .Items.Single(item => item.Title == "rows");
                var readResult = ReadResource(gateway, session, dataResource.Reference.Uri, "text", null, 8000).Result;
                AssertContains(readResult.Text, "Resource", "HTML data member exposes canonical binding metadata");
                var boundRead = executor.ResourceGateway.Read(session, new ResourceReadRequest {
                    Reference = session.HtmlWorkspace.DataSources.Single().Binding.Resource, Representation = "text", MaxChars = 128 });
                AssertContains(boundRead.Result.Text, "items", "bound payload is read through the same gateway");

                var removedRead = executor.ExecuteManual(
                    new ToolInvocation { ToolId = "common.html_workspace_read" },
                    tools,
                    new AppSettings(),
                    false,
                    false,
                    session);
                var removedSearch = executor.ExecuteManual(
                    new ToolInvocation { ToolId = "common.html_workspace_search" },
                    tools,
                    new AppSettings(),
                    false,
                    false,
                    session);
                AssertEqual("unknown_tool", removedRead.ErrorCode, "removed HTML read id stays unknown");
                AssertEqual("unknown_tool", removedSearch.ErrorCode, "removed HTML search id stays unknown");

                var deleteScript = new ToolInvocation { ToolId = "common.html_workspace_delete" };
                deleteScript.Arguments["target"] = "app.js";
                var deleteScriptResult = executor.ExecuteManual(deleteScript, tools, new AppSettings(), false, false, session);
                AssertTrue(deleteScriptResult.Success, "html workspace file delete succeeds");
                AssertEqual(1, session.HtmlWorkspace.Files.Count, "html script deleted");

                var deleteData = new ToolInvocation { ToolId = "common.html_workspace_delete" };
                deleteData.Arguments["target"] = "rows";
                var deleteDataResult = executor.ExecuteManual(deleteData, tools, new AppSettings(), false, false, session);
                AssertTrue(deleteDataResult.Success, "html workspace data delete succeeds");
                AssertEqual(0, session.HtmlWorkspace.DataSources.Count, "html data deleted");
                HtmlWorkspaceToolService.RestoreSnapshot(session, session.HtmlWorkspace.History[0].Id);
                AssertEqual(1, session.HtmlWorkspace.DataSources.Count, "html data delete can be undone");

                var boundSession = NewSession(adapter);
                var sourceArtifact = new ChatArtifact { Kind = ChatArtifactKinds.File, Title = "sales.json",
                    MimeType = "application/json", InlineText = "[{\"month\":\"Jan\",\"sales\":120}]" };
                boundSession.Artifacts.Add(sourceArtifact);
                var target = executor.ResourceGateway.Find(boundSession, "sales.json", "conversation").Items.Single().Target;
                var definition = executor.GetControllerTools().Single(item => item.Id == HtmlWorkspaceToolCatalog.BindDataToolId);
                var schema = JObject.Parse(definition.ArgumentSchemaJson);
                AssertEqual("name,target", string.Join(",", ((JArray)schema["required"]).Values<string>()), "binding takes a semantic target");
                var invalidBind = Command(HtmlWorkspaceToolCatalog.BindDataToolId, "name", "bad", "sourceTool", "excel.read_range");
                AssertTrue(!executor.ExecuteManual(invalidBind, tools, new AppSettings(), false, false, boundSession).Success,
                    "retired nested source execution arguments are rejected");
                var bind = Command(HtmlWorkspaceToolCatalog.BindDataToolId, "name", "sales", "target", target, "policy", "head");
                var bound = executor.ExecuteManual(bind, tools, new AppSettings(), false, false, boundSession);
                AssertTrue(bound.Success, "resource binding succeeds: " + bound.Message);
                var binding = boundSession.HtmlWorkspace.DataSources.Single().Binding;
                AssertEqual("head", binding.Policy, "explicit head policy persists");
                AssertTrue(binding.Resource != null && !binding.Resource.IsExact, "head binding contains identity only");
                var beforeRefresh = boundSession.ActiveHtmlArtifactId;
                var refresh = executor.ExecuteManual(Command(HtmlWorkspaceToolCatalog.RefreshDataToolId, "name", "sales"),
                    tools, new AppSettings(), false, false, boundSession);
                AssertTrue(refresh.Success, "gateway refresh succeeds");
                AssertEqual(beforeRefresh, boundSession.ActiveHtmlArtifactId, "source resolution never rewrites workspace history");
                var freeze = executor.ExecuteManual(Command(HtmlWorkspaceToolCatalog.FreezeDataToolId, "name", "sales"),
                    tools, new AppSettings(), false, false, boundSession);
                AssertTrue(freeze.Success, "binding freeze succeeds: " + freeze.Message);
                binding = boundSession.HtmlWorkspace.DataSources.Single().Binding;
                AssertTrue(binding.Policy == "exact" && binding.Resource.IsExact, "freeze pins a canonical resource; it never removes provenance");

                var invalidData = new ToolInvocation { ToolId = HtmlWorkspaceToolCatalog.WriteDataToolId };
                invalidData.Arguments["name"] = "bad";
                invalidData.Arguments["json"] = "{ bad";
                var invalidResult = executor.ExecuteManual(invalidData, tools, new AppSettings(), false, false, session);
                AssertTrue(!invalidResult.Success, "invalid html data fails");
                AssertContains(invalidResult.Message, "Invalid HTML workspace JSON", "invalid html data message");

                HtmlWorkspaceToolService.UpsertFile(session, "styles.css", "css", "body{}", false);
                AssertTrue(!executor.GetControllerTools().Any(item =>
                        item.Id == "common.html_workspace_set_active"),
                    "active preview selection is not model-facing");
                var cssSelectionRejected = false;
                try
                {
                    HtmlWorkspaceToolService.SetActiveFile(
                        session, "styles.css");
                }
                catch (InvalidOperationException)
                {
                    cssSelectionRejected = true;
                }
                AssertTrue(cssSelectionRejected,
                    "internal UI selection still rejects non-HTML files");

                var failedSession = new ChatSession { Title = "HTML failed mutation" };
                HtmlWorkspaceToolService.UpsertFile(failedSession, "index.html", "html", "<h1>First</h1>", true);
                HtmlWorkspaceToolService.UpsertFile(failedSession, "index.html", "html", "<h1>Second</h1>", true);
                HtmlWorkspaceToolService.RestoreSnapshot(failedSession, failedSession.HtmlWorkspace.History[0].Id);
                var failedHistoryCount = failedSession.HtmlWorkspace.History.Count;
                var failedRedoCount = failedSession.HtmlWorkspace.RedoBranches.Count;
                var missingSelectionRejected = false;
                try
                {
                    HtmlWorkspaceToolService.SetActiveFile(
                        failedSession, "missing.html");
                }
                catch (InvalidOperationException)
                {
                    missingSelectionRejected = true;
                }
                AssertTrue(missingSelectionRejected,
                    "missing internal active HTML file fails");
                AssertEqual(failedHistoryCount, failedSession.HtmlWorkspace.History.Count, "failed set-active preserves history");
                AssertEqual(failedRedoCount, failedSession.HtmlWorkspace.RedoBranches.Count, "failed set-active preserves redo branches");

                var absolutePath = new ToolInvocation { ToolId = HtmlWorkspaceToolCatalog.WriteFileToolId };
                absolutePath.Arguments["path"] = "/index.html";
                absolutePath.Arguments["content"] = "<h1>Absolute</h1>";
                var absoluteResult = executor.ExecuteManual(absolutePath, tools, new AppSettings(), true, false, failedSession);
                AssertTrue(absoluteResult.Success,
                    "native HTML dry run accepts schema-valid arguments without domain dispatch");
                AssertTrue(!failedSession.HtmlWorkspace.Files.Any(item =>
                        string.Equals(item.Path, "/index.html", StringComparison.Ordinal)),
                    "native HTML dry run does not create an absolute-path file");
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
                var tools = new List<ToolCatalogEntry>(OfficeToolCatalog.ForHost(adapter.HostName));
                HtmlWorkspaceToolService.UpsertFile(session, "index.html", "html", "one\ntwo\nthree\nfour", true);
                HtmlWorkspaceToolService.UpsertFile(session, "app.js", "script", "alpha\nconst beta = 1;\nalpha beta;", false);

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

                var patch = new ToolInvocation { ToolId = HtmlWorkspaceToolCatalog.ApplyPatchToolId };
                patch.Arguments["path"] = "app.js";
                patch.Arguments["patch"] = new JArray
                {
                    new JObject { ["op"] = "replace", ["find"] = "const beta = 1;", ["text"] = "const beta = 2;" },
                    new JObject { ["op"] = "insertAfter", ["find"] = "alpha beta;", ["text"] = "\nwindow.ready = true;" }
                };
                var artifactCount = session.Artifacts.Count;
                var patchResult = executor.ExecuteManual(patch, tools, new AppSettings(), false, false, session);
                AssertTrue(patchResult.Success, "HTML structured patch succeeds");
                AssertContains(session.HtmlWorkspace.Files.Single(item => item.Path == "app.js").Content, "beta = 2", "HTML patch replaces exact source");
                AssertContains(session.HtmlWorkspace.Files.Single(item => item.Path == "app.js").Content, "window.ready", "HTML patch inserts source");
                AssertEqual(artifactCount + 1, session.Artifacts.Count, "HTML patch creates one artifact revision");

                var failedPatch = new ToolInvocation { ToolId = HtmlWorkspaceToolCatalog.ApplyPatchToolId };
                failedPatch.Arguments["path"] = "app.js";
                failedPatch.Arguments["patch"] = new JArray
                {
                    new JObject { ["op"] = "replace", ["find"] = "alpha", ["text"] = "omega" }
                };
                var beforeFailure = session.HtmlWorkspace.Files.Single(item => item.Path == "app.js").Content;
                artifactCount = session.Artifacts.Count;
                var failedResult = executor.ExecuteManual(failedPatch, tools, new AppSettings(), false, false, session);
                AssertTrue(!failedResult.Success, "ambiguous HTML patch fails");
                AssertEqual("text_patch_ambiguous", failedResult.ErrorCode, "ambiguous HTML patch has stable code");
                AssertEqual(beforeFailure, session.HtmlWorkspace.Files.Single(item => item.Path == "app.js").Content, "failed HTML patch is atomic");
                AssertEqual(artifactCount, session.Artifacts.Count, "failed HTML patch creates no revision");

                var retiredRegexPatch = new ToolInvocation
                {
                    ToolId = HtmlWorkspaceToolCatalog.ApplyPatchToolId
                };
                retiredRegexPatch.Arguments["path"] = "app.js";
                retiredRegexPatch.Arguments["patch"] = new JArray
                {
                    new JObject
                    {
                        ["op"] = "regexReplace",
                        ["pattern"] = "beta",
                        ["text"] = "gamma"
                    }
                };
                var retiredRegexResult = executor.ExecuteManual(
                    retiredRegexPatch, tools, new AppSettings(), false,
                    false, session);
                AssertTrue(!retiredRegexResult.Success,
                    "HTML patch rejects retired regex operations");
                AssertEqual(beforeFailure,
                    session.HtmlWorkspace.Files.Single(item =>
                        item.Path == "app.js").Content,
                    "rejected regex patch preserves source");

                var strictCreate = new ToolInvocation { ToolId = HtmlWorkspaceToolCatalog.WriteFileToolId };
                strictCreate.Arguments["path"] = "index.html";
                strictCreate.Arguments["content"] = "overwrite";
                strictCreate.Arguments["mode"] = "createOnly";
                var strictCreateResult = executor.ExecuteManual(strictCreate, tools, new AppSettings(), false, false, session);
                AssertTrue(!strictCreateResult.Success,
                    "HTML file write rejects retired existence-mode plumbing");
                AssertEqual("one\ntwo\nthree\nfour", session.HtmlWorkspace.Files.Single(item => item.Path == "index.html").Content,
                    "rejected legacy write preserves existing content");

                var inspectSession = new ChatSession
                {
                    Host = adapter.HostName,
                    DocumentKey = adapter.DocumentKey,
                    DocumentTitle = adapter.DocumentTitle,
                    Title = "HTML inspection"
                };
                HtmlWorkspaceToolService.UpsertFile(inspectSession, "index.html", "html", "<main id=\"total\"></main><div id='total'></div><script src=\"echarts.min.js\"></script>", true);
                HtmlWorkspaceToolService.UpsertFile(inspectSession, "styles.css", "css", "@import url('theme.css');", false);
                HtmlWorkspaceToolService.UpsertFile(inspectSession, "app.js", "script", "import thing from 'pkg';\ndocument.getElementById('missing');\nRNAssistantData.missingData;", false);
                HtmlWorkspaceToolService.UpsertDataSource(inspectSession, "sales", "{\"rows\":[1]}");
                var inspectResult = HtmlWorkspaceToolService.InspectForPreview(
                    inspectSession, CancellationToken.None);
                AssertEqual(HtmlWorkspaceOutcomeStatus.Ok, inspectResult.Status,
                    "internal HTML static inspection succeeds even when findings exist");
                var inspection = JObject.Parse(inspectResult.DataJson);
                AssertTrue(!(bool)inspection["runtimeExecuted"], "HTML inspection identifies static-only scope");
                AssertTrue(!(bool)inspection["passed"], "HTML inspection fails preflight when errors exist");
                AssertTrue((int)inspection["errorCount"] >= 5, "HTML inspection reports assembly, CSP and missing-data errors");
                AssertTrue((int)inspection["warningCount"] >= 1, "HTML inspection reports likely missing DOM references");
                AssertTrue(inspection["issues"].Any(item => (string)item["code"] == "html.duplicate_id"), "HTML inspection finds duplicate ids");
                AssertTrue(inspection["issues"].Any(item => (string)item["code"] == "script.module_syntax_unsupported"), "HTML inspection finds module syntax");
                AssertTrue(inspection["issues"].Any(item =>
                        (string)item["code"] == "script.data_source_missing" &&
                        (string)item["severity"] == "error" &&
                        ((string)item["message"] ?? string.Empty).IndexOf("sales", StringComparison.OrdinalIgnoreCase) >= 0),
                    "HTML inspection fails loudly when code uses a missing data-source name");
                AssertTrue(inspection["issues"].Any(item =>
                        (string)item["code"] == "html.script_src_unsupported" &&
                        ((string)item["message"] ?? string.Empty).IndexOf("bundled ECharts automatically", StringComparison.OrdinalIgnoreCase) >= 0),
                    "HTML inspection explains the bundled ECharts dependency");
            });
        }

        private static void HtmlWorkspacePersistsWithChatSession()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new ChatStore(paths);
                var session = store.Create("Excel", "book", "Book.xlsx", "HTML chat");
                HtmlWorkspaceToolService.UpsertFile(session, "index.html", "html", "<h1>Saved</h1>", true);
                HtmlWorkspaceToolService.UpsertFile(session, "index.html", "html", "<h1>Saved again</h1>", true);
                session.Messages.Add(new ChatMessage { Role = "user", Content = "hello" });
                store.Save(session);

                store.ClearMessages(session.Host, session.DocumentKey, session.Id);
                var loaded = store.Load(session.Host, session.DocumentKey, session.Id);
                AssertEqual(0, loaded.Messages.Count, "messages cleared");
                AssertEqual(1, loaded.HtmlWorkspace.Files.Count, "html workspace preserved");
                AssertEqual("index.html", loaded.HtmlWorkspace.ActiveFileId, "active html preserved");
                AssertEqual(1, loaded.HtmlWorkspace.History.Count, "html history preserved");
                HtmlWorkspaceToolService.RestoreSnapshot(loaded, loaded.HtmlWorkspace.History[0].Id);
                AssertEqual("<h1>Saved</h1>", loaded.HtmlWorkspace.Files[0].Content, "persisted html history supports undo");

                AssertTrue(store.Delete(session.Host, session.DocumentKey, session.Id), "chat deleted");
                AssertTrue(store.Load(session.Host, session.DocumentKey, session.Id) == null, "deleted chat not loaded");
            });
        }

        private static void HtmlWorkspaceUndoRestoresPreviousVersion()
        {
            var session = new ChatSession { Title = "HTML undo" };
            HtmlWorkspaceToolService.UpsertDataSource(session, "sales", "{\"rows\":[1]}");
            HtmlWorkspaceToolService.UpsertFile(session, "index.html", "html", "<h1>First</h1>", true);
            HtmlWorkspaceToolService.UpsertFile(session, "index.html", "html", "<h1>Second</h1>", true);

            AssertTrue(session.HtmlWorkspace.History.Count > 0, "html history created");
            var historyCount = session.HtmlWorkspace.History.Count;
            HtmlWorkspaceToolService.RestoreSnapshot(session, session.HtmlWorkspace.History[0].Id);
            AssertContains(session.HtmlWorkspace.Files[0].Content, "First", "html undo restores previous file content");
            AssertEqual("index.html", session.HtmlWorkspace.ActiveFileId, "html undo keeps active file");
            AssertEqual(1, session.HtmlWorkspace.DataSources.Count, "html undo keeps data");
            AssertEqual(historyCount - 1, session.HtmlWorkspace.History.Count, "html undo consumes restored version");
            AssertEqual(1, session.HtmlWorkspace.RedoBranches.Count, "html undo exposes one redo branch");

            HtmlWorkspaceToolService.RedoSnapshot(session, session.HtmlWorkspace.RedoBranches[0].Id);
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
                HtmlWorkspaceToolService.UpsertFile(
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

            HtmlWorkspaceToolService.RestoreSnapshot(largeSession, largeSession.HtmlWorkspace.History[0].Id);
            AssertEqual('k', largeSession.HtmlWorkspace.Files[0].Content[0], "bounded history still supports undo");
            HtmlWorkspaceToolService.RedoSnapshot(largeSession, largeSession.HtmlWorkspace.RedoBranches[0].Id);
            AssertEqual('l', largeSession.HtmlWorkspace.Files[0].Content[0], "bounded history still supports redo");

            var transportSession = new ChatSession { Title = "HTML compact transport" };
            HtmlWorkspaceToolService.UpsertFile(transportSession, "index.html", "html", "CURRENT_FIRST", true);
            HtmlWorkspaceToolService.UpsertFile(transportSession, "index.html", "html", "HISTORY_SECOND", true);
            HtmlWorkspaceToolService.UpsertFile(transportSession, "index.html", "html", "CURRENT_THIRD", true);

            var bridge = HtmlWorkspaceEditorResourceService.Metadata(transportSession);
            var bridgeJson = JsonConvert.SerializeObject(bridge);
            AssertTrue(bridge.Files.Single().Source.IsExact, "bridge workspace identifies the exact current source");
            AssertTrue(bridgeJson.IndexOf("CURRENT_THIRD", StringComparison.Ordinal) < 0, "current source is pulled separately, never echoed through bridge state");
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
                var result = executor.ValidateToolDefinition(tool);
                AssertTrue(result.Success, "VBA tool validates: " + result.Message);
                AssertTrue(!HasTool(store.Load(), tool.Id), "validation does not save");

                var unsafeIdentity = CustomToolWithParameter(
                    "excel.customer_lookup", "customerId",
                    "Customer identifier.");
                var rejected = executor.ValidateToolDefinition(unsafeIdentity);
                AssertEqual("tool_parameter_rationale_required",
                    rejected.ErrorCode,
                    "plumbing-shaped custom argument requires rationale");
                AssertEqual("tool_parameter_rationale_required",
                    executor.ValidateToolDefinition(CustomToolWithParameter(
                        "excel.uri_lookup", "uri", "Resource URI."))
                        .ErrorCode,
                    "runtime URI-shaped custom argument requires rationale");
                AssertTrue(executor.ValidateToolDefinition(
                        CustomToolWithParameter("excel.valid_state", "valid",
                            "Whether the current domain state is valid."))
                        .Success,
                    "ordinary names ending in lowercase id are not false positives");
                var reviewedIdentity = CustomToolWithParameter(
                    "excel.customer_lookup", "customerId",
                    "Domain identity rationale: customer record explicitly selected by the user.");
                AssertTrue(executor.ValidateToolDefinition(reviewedIdentity).Success,
                    "explicit domain identity rationale is accepted");
                store.Save(new[] { unsafeIdentity });
                AssertTrue(!HasTool(store.Load(), unsafeIdentity.Id),
                    "unreviewed installed package is not callable");
                store.Save(new[] { reviewedIdentity });
                AssertTrue(HasTool(store.Load(), reviewedIdentity.Id),
                    "reviewed installed package remains callable");
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
