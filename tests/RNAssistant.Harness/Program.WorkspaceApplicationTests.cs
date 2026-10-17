using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Storage;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.WebView;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void WorkspaceProjectSourcesSaveAndRecover()
        {
            WithTempPaths(paths =>
            {
                var files = new WorkspaceFileService(paths);
                using (var app = new WorkspaceApplicationService(paths, files))
                using (var bridge = new AssistantWebBridge(new AssistantController(), null, app))
                {
                    var initial = CreateSourceQueueProject(app, paths);
                    var request = new SaveTabularProjectSourcesRequest { BindingId = initial.Workspace.BindingId,
                        ManifestPath = initial.Project.ManifestPath, ProjectRevision = initial.Revision,
                        Paths = new[] { "missing.xlsb", "data.json" } };
                    var reply = WorkspaceBridgeCall(bridge, BridgeToken(bridge), "saveTabularProjectSources", request);
                    AssertTrue((bool)reply["ok"], "save is a typed bridge operation");
                    AssertEqual("missing.xlsb", (string)reply["payload"]["project"]["sources"][0]["path"], "verified source projection");
                    var saved = app.ReadState();
                    AssertTrue(saved.Revision > initial.Revision && saved.SourceQueueId == null, "save replaces the selected project without starting work");
                    ExpectSourceQueueError("project_selection_changed", () => app.SaveTabularProjectSources(request));
                    ExpectSourceQueueError("project_selection_changed", () => app.PrepareSources(SourceQueueRequest(initial, new System.Collections.Generic.List<string> { "missing.xlsb" })));
                    request.ProjectRevision = saved.Revision; request.ManifestPath = "other/project.json";
                    ExpectSourceQueueError("project_selection_changed", () => app.SaveTabularProjectSources(request));
                    AssertTrue(app.ReadState().Project != null, "foreign path cannot disturb the selected project");
                    request.ManifestPath = saved.Project.ManifestPath;
                    var writes = 0;
                    files.FaultPoint = stage => { if (stage == "after-dispatch") { writes++; throw new IOException("lost save result"); } };
                    request.Paths = new[] { "new.csv" };
                    reply = WorkspaceBridgeCall(bridge, BridgeToken(bridge), "saveTabularProjectSources", request);
                    AssertEqual("effect_unknown", (string)reply["errorCode"], "unverified write is not success");
                    AssertTrue(app.ReadState().Project == null, "unknown save revokes the previous project pin");
                    var openedPath = new WorkspaceProjectPathRequest { BindingId = saved.Workspace.BindingId, ManifestPath = saved.Project.ManifestPath };
                    var recovered = app.ReconcileFile(openedPath);
                    AssertTrue(recovered.State.Project == null, "inspection does not implicitly reopen");
                    var inspected = app.OpenTabularProject(openedPath);
                    AssertEqual("new.csv", inspected.Project.Sources[0].Path, "explicit open observes recovered composition");
                    AssertEqual(1, writes, "no automatic save replay");
                    using (var restarted = new WorkspaceApplicationService(paths))
                    {
                        var root = restarted.OpenWorkspace(new OpenWorkspaceRequest { RootPath = initial.Workspace.RootPath, ReadOnly = true });
                        var project = restarted.OpenTabularProject(new WorkspaceProjectPathRequest { BindingId = root.Workspace.BindingId, ManifestPath = "project.json" });
                        AssertEqual(inspected.Project.Sources[0].SourceId, project.Project.Sources[0].SourceId, "restart restores declarations with stable IDs");
                        AssertTrue(project.SourceQueueId == null, "restart never replays preparation");
                    }
                }
            });
        }

        private static void WorkspaceProjectDraftCommandsRoundTrip()
        {
            WithTempPaths(paths =>
            {
                var files = new WorkspaceFileService(paths);
                using (var app = new WorkspaceApplicationService(paths, files))
                using (var bridge = new AssistantWebBridge(new AssistantController(), null, app))
                {
                    var token = BridgeToken(bridge);
                    var project = CreateSourceQueueProject(app, paths);
                    var selected = new WorkspaceSelectedProjectRequest { BindingId = project.Workspace.BindingId,
                        ManifestPath = project.Project.ManifestPath, ProjectRevision = project.Revision };
                    var save = new SaveTabularProjectDraftRequest { BindingId = selected.BindingId,
                        ManifestPath = selected.ManifestPath, ProjectRevision = selected.ProjectRevision,
                        File = TabularProjectStore.NewDraftFileName("Mapping.json"), Input = "{invalid" };
                    var saved = WorkspaceBridgeCall(bridge, token, "saveTabularProjectDraft", save);
                    AssertTrue((bool)saved["ok"], "draft save has a typed bridge receipt");
                    var fileName = (string)saved["payload"]["draft"]["file"];
                    AssertEqual("{invalid", File.ReadAllText(Path.Combine(project.Workspace.RootPath, "drafts", fileName)),
                        "bridge save preserves raw invalid JSON");
                    var revision = (long)saved["payload"]["state"]["revision"];
                    AssertTrue(revision > project.Revision, "saved draft advances the selection guard");
                    AssertEqual("project_selection_changed", (string)WorkspaceBridgeCall(bridge, token,
                        "listTabularProjectDrafts", selected)["errorCode"], "stale list guard");
                    selected.ProjectRevision = revision;
                    save.ProjectRevision = revision;
                    var listed = WorkspaceBridgeCall(bridge, token, "listTabularProjectDrafts", selected);
                    AssertEqual(fileName, (string)listed["payload"]["items"][0]["file"], "metadata list through bridge");
                    var open = new OpenTabularProjectDraftRequest { BindingId = selected.BindingId,
                        ManifestPath = selected.ManifestPath, ProjectRevision = selected.ProjectRevision, File = fileName };
                    AssertEqual("{invalid", (string)WorkspaceBridgeCall(bridge, token,
                        "openTabularProjectDraft", open)["payload"]["input"], "raw draft read through bridge");
                    open.File = "../project.json";
                    AssertEqual("draft_invalid", (string)WorkspaceBridgeCall(bridge, token,
                        "openTabularProjectDraft", open)["errorCode"], "foreign file cannot be opened as a draft");

                    var dispatches = 0;
                    files.FaultPoint = stage => { if (stage == "after-dispatch") { dispatches++; throw new IOException("lost draft receipt"); } };
                    save.File = TabularProjectStore.NewDraftFileName("Unknown");
                    var unknown = WorkspaceBridgeCall(bridge, token, "saveTabularProjectDraft", save);
                    AssertEqual("effect_unknown", (string)unknown["errorCode"], "uncertain draft save is not success");
                    AssertTrue(((string)unknown["error"]).Contains("drafts/" + save.File),
                        "uncertain result identifies the exact recovery target");
                    AssertTrue(app.ReadState().Project == null, "uncertain save revokes the project selection");
                    AssertEqual(1, dispatches, "uncertain snapshot is not replayed");
                    var unknownFile = save.File;
                    AssertTrue(File.Exists(Path.Combine(project.Workspace.RootPath, "drafts", unknownFile)),
                        "unknown target is recoverable from the original request");
                    files.FaultPoint = null;
                    app.ReconcileFile(new WorkspaceProjectPathRequest { BindingId = selected.BindingId,
                        ManifestPath = "drafts/" + unknownFile });
                    var reopened = app.OpenTabularProject(new WorkspaceProjectPathRequest { BindingId = selected.BindingId,
                        ManifestPath = selected.ManifestPath });
                    AssertEqual(2, app.ListTabularProjectDrafts(new WorkspaceSelectedProjectRequest {
                        BindingId = selected.BindingId, ManifestPath = selected.ManifestPath,
                        ProjectRevision = reopened.Revision }).Items.Count, "explicit recovery reveals both snapshots");
                }
            });
        }

        private static void WorkspaceApplicationPinsSelection()
        {
            WithTempPaths(paths =>
            {
                var root = Path.Combine(paths.Root, "workspace");
                Directory.CreateDirectory(root);
                var app = new WorkspaceApplicationService(paths);
                AssertTrue(app.ReadState().Workspace == null, "no implicit root selection");
                var first = app.OpenWorkspace(new OpenWorkspaceRequest { RootPath = root });
                var created = app.CreateTabularProject(new CreateTabularProjectRequest {
                    BindingId = first.Workspace.BindingId, ManifestPath = "project.json", Name = "Bound project" });
                AssertEqual("Bound project", created.Project.Name, "typed application result after read-back");
                var protectedOpen = app.OpenWorkspace(new OpenWorkspaceRequest { RootPath = root, ReadOnly = true });
                AssertTrue(protectedOpen.Workspace.BindingId != first.Workspace.BindingId,
                    "same root with a new permission selection gets a new binding");
                try
                {
                    app.CreateTabularProject(new CreateTabularProjectRequest {
                        BindingId = first.Workspace.BindingId, ManifestPath = "stale/project.json", Name = "Stale" });
                    throw new Exception("Stale workspace binding was accepted.");
                }
                catch (WorkspaceApplicationException ex) { AssertEqual("workspace_binding_changed", ex.Code, "old permission cannot authorize dispatch"); }
                AssertTrue(!Directory.Exists(Path.Combine(root, "stale")), "stale UI causes no file effect");
                ExpectTabularFailure(() => app.CreateTabularProject(new CreateTabularProjectRequest {
                    BindingId = protectedOpen.Workspace.BindingId, ManifestPath = "denied/project.json", Name = "Denied" }), "read_only_mount");
                var opened = app.OpenTabularProject(new WorkspaceProjectPathRequest {
                    BindingId = protectedOpen.Workspace.BindingId, ManifestPath = "project.json" });
                AssertTrue(opened.Workspace.ReadOnly && opened.Project.Name == "Bound project", "read-only selection can open");
                var missing = false;
                try { app.OpenWorkspace(new OpenWorkspaceRequest { RootPath = Path.Combine(paths.Root, "absent") }); }
                catch (DirectoryNotFoundException) { missing = true; }
                AssertTrue(missing, "choosing a missing root does not create it");
                AssertEqual(opened.Workspace.BindingId, app.ReadState().Workspace.BindingId, "failed root switch preserves prior selection");
                File.WriteAllText(Path.Combine(root, "project.json"), "{}");
                ExpectTabularFailure(() => app.OpenTabularProject(new WorkspaceProjectPathRequest {
                    BindingId = opened.Workspace.BindingId, ManifestPath = "project.json" }), "project_invalid");
                AssertTrue(app.ReadState().Project == null, "failed open cannot leave an old success projection");
                app.Reset();
                AssertTrue(app.ReadState().Workspace == null, "runtime reset revokes the workspace binding");
            });
        }

        private static void WorkspaceBridgePreservesLifetimeAndWorkerBoundary()
        {
            WithTempPaths(paths =>
            {
                var root = Path.Combine(paths.Root, "workspace");
                Directory.CreateDirectory(root);
                var files = new WorkspaceFileService(paths);
                var previousContext = SynchronizationContext.Current;
                var callerContext = new SynchronizationContext();
                var checkedWorker = false;
                files.FaultPoint = stage => {
                    checkedWorker = true;
                    AssertTrue(!ReferenceEquals(callerContext, SynchronizationContext.Current), "file dispatch leaves UI callback");
                };
                try
                {
                    SynchronizationContext.SetSynchronizationContext(callerContext);
                    using (var bridge = new AssistantWebBridge(new AssistantController(), null, new WorkspaceApplicationService(paths, files)))
                    {
                        var token = BridgeToken(bridge);
                        var selected = WorkspaceBridgeCall(bridge, token, "openWorkspace", new OpenWorkspaceRequest { RootPath = root });
                        AssertTrue((bool)selected["ok"], "typed workspace open");
                        var binding = (string)selected["payload"]["workspace"]["bindingId"];
                        var created = WorkspaceBridgeCall(bridge, token, "createTabularProject", new CreateTabularProjectRequest {
                            BindingId = binding, ManifestPath = "project.json", Name = "Independent project" });
                        AssertTrue((bool)created["ok"] && checkedWorker, "manifest created on worker boundary");
                        var entered = new TaskCompletionSource<bool>();
                        var result = new TaskCompletionSource<OfficeHostChatResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                        bridge.OfficeHostChatRequested = host => { entered.SetResult(true); return result.Task; };
                        var switching = bridge.HandleMessageAsync(WorkspaceBridgeRequest(token, "createOfficeHostChat", new OfficeHostChatPayload { Host = "Word" }));
                        AssertTrue(entered.Task.Wait(5000), "host switch starts");
                        bridge.RebindController(new AssistantController());
                        result.SetResult(new OfficeHostChatResponse { Host = "Word", ChatId = "word-chat" });
                        AssertTrue((bool)JObject.Parse(switching.GetAwaiter().GetResult())["ok"], "Office controller switched");
                        var after = WorkspaceBridgeCall(bridge, token, "getWorkspaceState", null);
                        AssertEqual(binding, (string)after["payload"]["workspace"]["bindingId"], "workspace lifetime survives controller replacement");
                        AssertEqual("Independent project", (string)after["payload"]["project"]["name"], "project survives Office target change");
                        var untrusted = WorkspaceBridgeCall(bridge, "wrong-token", "openWorkspace", new OpenWorkspaceRequest { RootPath = root });
                        AssertTrue(!(bool)untrusted["ok"], "new operations keep the bridge token boundary");
                    }
                }
                finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
            });
        }

        private static void WorkspaceBridgeReportsUnknownWithoutReplay()
        {
            WithTempPaths(paths =>
            {
                var root = Path.Combine(paths.Root, "workspace");
                Directory.CreateDirectory(root);
                var files = new WorkspaceFileService(paths);
                var dispatches = 0;
                files.FaultPoint = stage => { if (stage == "after-dispatch") { dispatches++; throw new IOException("interrupted"); } };
                using (var bridge = new AssistantWebBridge(new AssistantController(), null, new WorkspaceApplicationService(paths, files)))
                {
                    var token = BridgeToken(bridge);
                    var opened = WorkspaceBridgeCall(bridge, token, "openWorkspace", new OpenWorkspaceRequest { RootPath = root });
                    var binding = (string)opened["payload"]["workspace"]["bindingId"];
                    var unknown = WorkspaceBridgeCall(bridge, token, "createTabularProject", new CreateTabularProjectRequest {
                        BindingId = binding, ManifestPath = "project.json", Name = "Interrupted" });
                    AssertEqual("effect_unknown", (string)unknown["errorCode"], "typed unknown crosses the bridge unchanged");
                    var request = new WorkspaceProjectPathRequest { BindingId = binding, ManifestPath = "project.json" };
                    var blocked = WorkspaceBridgeCall(bridge, token, "openTabularProject", request);
                    AssertEqual("unresolved_previous_effect", (string)blocked["errorCode"], "ordinary open cannot conceal uncertainty");
                    var recovered = WorkspaceBridgeCall(bridge, token, "reconcileWorkspaceFile", request);
                    AssertEqual("UnknownAfterDispatch", (string)recovered["payload"]["outcome"], "inspection preserves causality");
                    AssertTrue(recovered["payload"]["state"]["project"].Type == JTokenType.Null, "inspection does not claim project-open success");
                    var inspected = WorkspaceBridgeCall(bridge, token, "openTabularProject", request);
                    AssertEqual("Interrupted", (string)inspected["payload"]["project"]["name"], "explicit open reads the current file");
                    AssertEqual(1, dispatches, "no replay during inspection or open");
                }
            });
        }

        private static void WorkspaceBridgeSerializesRuntimeReset()
        {
            WithTempPaths(paths =>
            {
                var root = Path.Combine(paths.Root, "workspace");
                Directory.CreateDirectory(root);
                var files = new WorkspaceFileService(paths);
                using (var entered = new ManualResetEventSlim())
                using (var release = new ManualResetEventSlim())
                using (var bridge = new AssistantWebBridge(new AssistantController(), null, new WorkspaceApplicationService(paths, files)))
                {
                    files.FaultPoint = stage => {
                        if (stage != "after-dispatch") return;
                        entered.Set();
                        AssertTrue(release.Wait(5000), "release paused read-back");
                    };
                    var token = BridgeToken(bridge);
                    var selected = WorkspaceBridgeCall(bridge, token, "openWorkspace", new OpenWorkspaceRequest { RootPath = root });
                    var binding = (string)selected["payload"]["workspace"]["bindingId"];
                    var writing = bridge.HandleMessageAsync(WorkspaceBridgeRequest(token, "createTabularProject",
                        new CreateTabularProjectRequest { BindingId = binding, ManifestPath = "project.json", Name = "Before reset" }));
                    try
                    {
                        AssertTrue(entered.Wait(5000), "write reached dispatch");
                        var blocked = WorkspaceBridgeCall(bridge, token, "clearRuntimeData", null);
                        AssertEqual("bridge_switch_busy", (string)blocked["errorCode"], "runtime clearing cannot delete a live file journal");
                    }
                    finally { release.Set(); }
                    AssertTrue((bool)JObject.Parse(writing.GetAwaiter().GetResult())["ok"], "accepted write completes its read-back");
                    var cleared = WorkspaceBridgeCall(bridge, token, "clearRuntimeData", null);
                    AssertTrue((bool)cleared["ok"], "explicit reset is accepted after the file request drains");
                    var state = WorkspaceBridgeCall(bridge, token, "getWorkspaceState", null);
                    AssertTrue(state["payload"]["workspace"].Type == JTokenType.Null, "runtime reset revokes workspace selection");
                    var stale = WorkspaceBridgeCall(bridge, token, "openTabularProject",
                        new WorkspaceProjectPathRequest { BindingId = binding, ManifestPath = "project.json" });
                    AssertEqual("workspace_binding_changed", (string)stale["errorCode"], "previous binding cannot return after reset");
                }
            });
        }

        private static string WorkspaceBridgeRequest(string token, string type, object payload)
        {
            return Newtonsoft.Json.JsonConvert.SerializeObject(new BridgeRequest {
                ContractVersion = BridgeOperationCatalog.Version, Id = Guid.NewGuid().ToString("N"), Type = type,
                BridgeToken = token, Payload = payload == null ? null : JToken.FromObject(payload) });
        }

        private static JObject WorkspaceBridgeCall(AssistantWebBridge bridge, string token, string type, object payload)
        { return JObject.Parse(bridge.HandleMessageAsync(WorkspaceBridgeRequest(token, type, payload)).GetAwaiter().GetResult()); }
    }
}
