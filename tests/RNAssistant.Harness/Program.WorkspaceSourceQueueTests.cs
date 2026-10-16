using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Storage;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.WebView;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void WorkspaceBrowseKeepsSelectionBoundaries()
        {
            WithTempPaths(paths =>
            {
                var files = new WorkspaceFileService(paths);
                using (var app = new WorkspaceApplicationService(paths, files))
                {
                    var state = CreateSourceQueueProject(app, paths);
                    var reports = Path.Combine(state.Workspace.RootPath, "reports");
                    Directory.CreateDirectory(Path.Combine(reports, "nested"));
                    File.WriteAllText(Path.Combine(reports, ".env-private"), "hidden");
                    for (var i = 0; i < 61; i++) File.WriteAllText(Path.Combine(reports, "source-" + i.ToString("D3") + ".xlsb"), "fixture");
                    files.FaultPoint = stage => { throw new InvalidOperationException("Metadata browsing must not capture files or publish authority."); };
                    var first = app.BrowseFiles(new BrowseWorkspaceFilesRequest { BindingId = state.Workspace.BindingId, Directory = "reports", Query = "source-" });
                    var next = app.BrowseFiles(new BrowseWorkspaceFilesRequest { BindingId = state.Workspace.BindingId, Directory = "reports", Query = "source-", Offset = 50 });
                    AssertEqual(50, first.Items.Count, "bounded directory page");
                    AssertEqual(11, next.Items.Count, "remaining directory page");
                    AssertEqual(61, first.Matched, "directory query matches before paging");
                    AssertEqual(61, first.Items.Concat(next.Items).Select(item => item.Path).Distinct().Count(), "stable pages do not drop or repeat names");
                    AssertEqual("reports/source-050.xlsb", next.Items[0].Path, "workspace-relative path, not a browser name");
                    AssertEqual(7L, next.Items[0].ByteLength.Value, "metadata-only length");
                    var folder = app.BrowseFiles(new BrowseWorkspaceFilesRequest { BindingId = state.Workspace.BindingId, Directory = "reports", Query = "nested" });
                    AssertTrue(folder.Items.Single().IsDirectory && folder.Items[0].ByteLength == null, "directories are navigable, not source files");
                    AssertEqual(0, app.BrowseFiles(new BrowseWorkspaceFilesRequest { BindingId = state.Workspace.BindingId, Directory = "reports", Query = ".env" }).Matched, "protected paths stay hidden");
                    try { app.BrowseFiles(new BrowseWorkspaceFilesRequest { BindingId = state.Workspace.BindingId }, new CancellationToken(true)); throw new Exception("Expected cancellation"); }
                    catch (OperationCanceledException) { }
                    using (var bridge = new AssistantWebBridge(new AssistantController(), null, app))
                    {
                        var response = WorkspaceBridgeCall(bridge, BridgeToken(bridge), "browseWorkspaceFiles", new BrowseWorkspaceFilesRequest {
                            BindingId = state.Workspace.BindingId, Directory = "reports", Query = "source-", Offset = 50 });
                        AssertTrue((bool)response["ok"], "typed directory bridge");
                        AssertEqual(11, response["payload"]["items"].Count(), "bridge metadata page");
                        app.OpenWorkspace(new OpenWorkspaceRequest { RootPath = state.Workspace.RootPath, ReadOnly = true });
                        ExpectSourceQueueError("workspace_binding_changed", () => app.BrowseFiles(new BrowseWorkspaceFilesRequest { BindingId = state.Workspace.BindingId }));
                    }
                }
            });
        }

        private static void WorkspaceSourceQueueSerializesAndPages()
        {
            WithTempPaths(paths =>
            {
                var files = new WorkspaceFileService(paths);
                using (var app = new WorkspaceApplicationService(paths, files))
                using (var entered = new ManualResetEventSlim())
                using (var release = new ManualResetEventSlim())
                {
                    var state = CreateSourceQueueProject(app, paths);
                    var root = state.Workspace.RootPath;
                    WriteSourceFixture(Path.Combine(root, "large.bin"), 2L * 1024 * 1024);
                    var names = new List<string> { "large.bin", "missing.csv" };
                    for (var i = 0; i < 60; i++)
                    { var name = "part-" + i.ToString("D2") + ".csv"; File.WriteAllText(Path.Combine(root, name), "x"); names.Add(name); }
                    var leases = 0;
                    files.FaultPoint = stage => {
                        if (stage != "before-file-lease" || Interlocked.Increment(ref leases) != 2) return;
                        AssertTrue(SynchronizationContext.Current == null, "source capture stays off UI context");
                        entered.Set(); AssertTrue(release.Wait(5000), "release first source");
                    };
                    var queue = app.PrepareSources(SourceQueueRequest(state, names));
                    names.Clear(); // Accepted descriptors must be copied.
                    try
                    {
                        AssertTrue(entered.Wait(5000), "first source starts");
                        queue = ReadSourcePage(app, queue.QueueId);
                        AssertEqual(1, queue.Running, "one heavy file");
                        AssertEqual(61, queue.Pending, "pending entries keep only descriptors");
                        var failures = app.ReadSourceQueue(new WorkspaceSourcePageRequest { QueueId = queue.QueueId, State = WorkspaceSourceState.Failed });
                        AssertEqual(0, failures.Items.Count, "filter excludes running row");
                        AssertEqual(queue.Items[0].Id, failures.CurrentItem.Id, "current progress is independent of page/filter");
                        AssertEqual(queue.QueueId, app.ReadState().SourceQueueId, "view can rediscover accepted queue");
                        ExpectSourceQueueError("source_queue_active", () => app.OpenWorkspace(new OpenWorkspaceRequest { RootPath = root }));
                        ExpectSourceQueueError("source_queue_active", app.Reset);
                        ExpectSourceQueueError("source_queue_active", () => app.PrepareSources(SourceQueueRequest(state, new List<string> { "part-00.csv" })));
                        AssertEqual(WorkspaceSourceQueueState.Pausing, SourceCommand(app, queue.QueueId, WorkspaceSourceCommand.Pause).State,
                            "pause lets the current file settle");
                        AssertTrue(new ChatRunRegistry(paths).HasExternalRuns(), "existing maintenance gate sees background storage work");
                    }
                    finally { release.Set(); }
                    queue = WaitSourceQueue(app, queue.QueueId, item => item.State == WorkspaceSourceQueueState.Paused && item.Running == 0);
                    AssertEqual(1, queue.Prepared, "pause preserves first completed source");
                    File.WriteAllText(Path.Combine(root, "large.bin"), "changed live source");
                    using (var retained = app.OpenPreparedSource(queue.QueueId, queue.Items[0].Id))
                    { AssertEqual(2L * 1024 * 1024, retained.Length, "runtime-only exact pin survives live replacement"); AssertEqual(0, retained.ReadByte(), "original source bytes"); }
                    var preparedId = queue.Items[0].Id;
                    ExpectSourceQueueError("source_queue_invalid", () => app.ControlSourceQueue(new WorkspaceSourceControlRequest {
                        QueueId = queue.QueueId, Command = WorkspaceSourceCommand.RetrySelected, ItemIds = new List<string> { preparedId } }));
                    SourceCommand(app, queue.QueueId, WorkspaceSourceCommand.Resume);
                    queue = WaitSourceQueue(app, queue.QueueId, item => item.State == WorkspaceSourceQueueState.Finished);
                    AssertEqual(61, queue.Prepared, "all valid sources prepared");
                    AssertEqual(1, queue.Failed, "missing file is a per-item failure");
                    AssertEqual("source_missing", queue.Items[1].ErrorCode, "typed source error");
                    AssertEqual(50, queue.Items.Count, "bounded first page");
                    AssertEqual(12, app.ReadSourceQueue(new WorkspaceSourcePageRequest { QueueId = queue.QueueId, Offset = 50 }).Items.Count,
                        "bounded remainder");
                    var filtered = app.ReadSourceQueue(new WorkspaceSourcePageRequest { QueueId = queue.QueueId,
                        Query = "part-", State = WorkspaceSourceState.Prepared, Sort = WorkspaceSourceSort.Path, Limit = 10 });
                    AssertEqual(60, filtered.Matched, "server-side source filter");
                    AssertEqual("part-00.csv", filtered.Items[0].Path, "stable path ordering");
                    var serialized = JsonConvert.SerializeObject(filtered);
                    AssertTrue(!serialized.Contains("rna://") && !serialized.Contains("Sha256") && !serialized.Contains("Payload"),
                        "presentation cannot manufacture exact source pins");
                    var reopened = app.OpenTabularProject(new WorkspaceProjectPathRequest { BindingId = state.Workspace.BindingId, ManifestPath = "project.json" });
                    AssertTrue(reopened.SourceQueueId == null, "new project selection discards only the old transient queue");
                    ExpectSourceQueueError("project_selection_changed", () => app.PrepareSources(SourceQueueRequest(state, new List<string> { "part-00.csv" })));
                }
            });
        }

        private static void WorkspaceSourceQueueCancelsAndRetries()
        {
            WithTempPaths(paths =>
            {
                using (var app = new WorkspaceApplicationService(paths))
                {
                    var state = CreateSourceQueueProject(app, paths);
                    File.WriteAllText(Path.Combine(state.Workspace.RootPath, "a.csv"), "aa");
                    File.WriteAllText(Path.Combine(state.Workspace.RootPath, "b.csv"), "bb");
                    WorkspaceSourceQueuePage queue;
                    using (RuntimeStorageActivity.AcquireSourceReader(paths, CancellationToken.None))
                    {
                        queue = app.PrepareSources(SourceQueueRequest(state, new List<string> { "a.csv", "b.csv" }));
                        queue = WaitSourceQueue(app, queue.QueueId, item => item.Items[0].Phase == WorkspaceSourcePhase.WaitingForReader);
                        SourceCommand(app, queue.QueueId, WorkspaceSourceCommand.Pause);
                        SourceCommand(app, queue.QueueId, WorkspaceSourceCommand.CancelCurrent);
                        queue = WaitSourceQueue(app, queue.QueueId, item => item.Cancelled == 1 && item.Running == 0);
                        AssertEqual(1, queue.Pending, "cancel current does not discard pending work");
                        var cancelledId = queue.Items[0].Id;
                        ExpectSourceQueueError("source_queue_invalid", () => app.ControlSourceQueue(new WorkspaceSourceControlRequest {
                            QueueId = queue.QueueId, Command = WorkspaceSourceCommand.RetrySelected,
                            ItemIds = new List<string> { cancelledId, "foreign" } }));
                        AssertEqual(1, ReadSourcePage(app, queue.QueueId).Cancelled, "invalid retry selection is atomic");
                        app.ControlSourceQueue(new WorkspaceSourceControlRequest {
                            QueueId = queue.QueueId, Command = WorkspaceSourceCommand.RetrySelected, ItemIds = new List<string> { cancelledId } });
                        AssertEqual(2, ReadSourcePage(app, queue.QueueId).Pending, "retry remains paused until explicit resume");
                    }
                    SourceCommand(app, queue.QueueId, WorkspaceSourceCommand.Resume);
                    queue = WaitSourceQueue(app, queue.QueueId, item => item.State == WorkspaceSourceQueueState.Finished);
                    AssertEqual(2, queue.Prepared, "explicit resume prepares both items");
                    var request = SourceQueueRequest(state, new List<string> { "a.csv", "b.csv" });
                    request.MaximumFileBytes = request.MaximumBatchBytes = 3;
                    queue = app.PrepareSources(request);
                    queue = WaitSourceQueue(app, queue.QueueId, item => item.State == WorkspaceSourceQueueState.Finished);
                    AssertEqual(1, queue.Prepared, "batch admission counts accepted source bytes");
                    AssertEqual(1, queue.Failed, "remaining budget cannot be silently exceeded");
                    AssertEqual(2L, queue.CapturedBytes, "exact volume counter");
                    ExpectSourceQueueError("source_queue_changed", () => ReadSourcePage(app, "foreign"));
                    using (RuntimeStorageActivity.AcquireSourceReader(paths, CancellationToken.None))
                    {
                        queue = app.PrepareSources(SourceQueueRequest(state, new List<string> { "a.csv", "b.csv" }));
                        WaitSourceQueue(app, queue.QueueId, item => item.Running == 1);
                        SourceCommand(app, queue.QueueId, WorkspaceSourceCommand.CancelAll);
                        queue = WaitSourceQueue(app, queue.QueueId, item => item.State == WorkspaceSourceQueueState.Finished);
                        AssertEqual(2, queue.Cancelled, "cancel waiting/current/pending without acquiring the reader");
                    }
                    AssertTrue(!new ChatRunRegistry(paths).HasExternalRuns(), "settled work releases maintenance activity");
                }
            });
        }

        private static void WorkspaceSourceQueueRejectsProjectDrift()
        {
            WithTempPaths(paths =>
            {
                var files = new WorkspaceFileService(paths);
                using (var app = new WorkspaceApplicationService(paths, files))
                using (var entered = new ManualResetEventSlim())
                using (var release = new ManualResetEventSlim())
                {
                    var state = CreateSourceQueueProject(app, paths);
                    var root = state.Workspace.RootPath;
                    File.WriteAllText(Path.Combine(root, "a.csv"), "a");
                    File.WriteAllText(Path.Combine(root, "b.csv"), "b");
                    var leases = 0;
                    files.FaultPoint = stage => {
                        if (stage != "before-file-lease" || Interlocked.Increment(ref leases) != 2) return;
                        entered.Set(); AssertTrue(release.Wait(5000), "release drift capture");
                    };
                    var queue = app.PrepareSources(SourceQueueRequest(state, new List<string> { "a.csv", "b.csv" }));
                    try
                    {
                        AssertTrue(entered.Wait(5000), "capture reached accepted project");
                        var manifest = Path.Combine(root, "project.json");
                        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("Queue project", "Externally edited"));
                    }
                    finally { release.Set(); }
                    queue = WaitSourceQueue(app, queue.QueueId, item => item.State == WorkspaceSourceQueueState.Paused && item.Running == 0);
                    AssertEqual("project_changed", queue.ErrorCode, "post-capture guard catches concurrent manifest change");
                    AssertEqual(0, queue.Prepared, "captured bytes alone cannot accept a changed project");
                    AssertEqual(1, queue.Pending, "project failure stops dispatch to later sources");
                    ExpectSourceQueueError("source_queue_reopen_project", () => SourceCommand(app, queue.QueueId, WorkspaceSourceCommand.Resume));
                    ExpectSourceQueueError("source_queue_invalid", () => app.OpenPreparedSource(queue.QueueId, queue.Items[0].Id));
                    SourceCommand(app, queue.QueueId, WorkspaceSourceCommand.CancelPending);
                    WaitSourceQueue(app, queue.QueueId, item => item.State == WorkspaceSourceQueueState.Finished);
                    var inspected = app.OpenTabularProject(new WorkspaceProjectPathRequest { BindingId = state.Workspace.BindingId, ManifestPath = "project.json" });
                    AssertEqual("Externally edited", inspected.Project.Name, "explicit reopen inspects changed project");
                    File.Delete(Path.Combine(root, "project.json"));
                    queue = app.PrepareSources(SourceQueueRequest(inspected, new List<string> { "a.csv", "b.csv" }));
                    queue = WaitSourceQueue(app, queue.QueueId, item => item.State == WorkspaceSourceQueueState.Paused && item.Running == 0);
                    AssertEqual("project_unavailable", queue.ErrorCode, "missing project is not a missing source");
                    AssertEqual(1, queue.Pending, "missing project stops later dispatch");
                    ExpectSourceQueueError("source_queue_reopen_project", () => SourceCommand(app, queue.QueueId, WorkspaceSourceCommand.Resume));
                    SourceCommand(app, queue.QueueId, WorkspaceSourceCommand.CancelAll);
                    WaitSourceQueue(app, queue.QueueId, item => item.State == WorkspaceSourceQueueState.Finished);
                }
            });
        }

        private static void WorkspaceSourceBridgeOwnsWorkerLifetime()
        {
            WithTempPaths(paths =>
            {
                var files = new WorkspaceFileService(paths);
                var app = new WorkspaceApplicationService(paths, files);
                var state = CreateSourceQueueProject(app, paths);
                File.WriteAllText(Path.Combine(state.Workspace.RootPath, "a.csv"), "a");
                using (var entered = new ManualResetEventSlim())
                using (var release = new ManualResetEventSlim())
                using (var bridge = new AssistantWebBridge(new AssistantController(), null, app))
                {
                    var leases = 0;
                    files.FaultPoint = stage => {
                        if (stage != "before-file-lease" || Interlocked.Increment(ref leases) != 2) return;
                        entered.Set(); AssertTrue(release.Wait(5000), "release closing worker");
                    };
                    var token = BridgeToken(bridge);
                    var response = WorkspaceBridgeCall(bridge, token, "prepareWorkspaceSources",
                        SourceQueueRequest(state, new List<string> { "a.csv" }));
                    AssertTrue((bool)response["ok"], "typed bridge admits queue immediately");
                    var queueId = (string)response["payload"]["queueId"];
                    try
                    {
                        AssertTrue(entered.Wait(5000), "bridge background capture starts");
                        var blocked = WorkspaceBridgeCall(bridge, token, "clearRuntimeData", null);
                        AssertEqual("source_queue_active", (string)blocked["errorCode"], "clear refuses before deleting source authority");
                        var switchingEntered = new TaskCompletionSource<bool>();
                        var switched = new TaskCompletionSource<OfficeHostChatResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                        bridge.OfficeHostChatRequested = host => { switchingEntered.SetResult(true); return switched.Task; };
                        var switching = bridge.HandleMessageAsync(WorkspaceBridgeRequest(token, "createOfficeHostChat", new OfficeHostChatPayload { Host = "Word" }));
                        AssertTrue(switchingEntered.Task.Wait(5000), "exclusive host switch starts");
                        bridge.RebindController(new AssistantController());
                        switched.SetResult(new OfficeHostChatResponse { Host = "Word", ChatId = "word-chat" });
                        AssertTrue((bool)Newtonsoft.Json.Linq.JObject.Parse(switching.GetAwaiter().GetResult())["ok"], "host switch completes");
                        var restored = WorkspaceBridgeCall(bridge, token, "getWorkspaceSourceQueue", new WorkspaceSourcePageRequest { QueueId = queueId });
                        AssertEqual(queueId, (string)restored["payload"]["queueId"], "controller replacement retains application queue");
                        AssertEqual(1, (int)restored["payload"]["running"], "navigation cannot cancel accepted preparation");
                        var untrusted = WorkspaceBridgeCall(bridge, "wrong-token", "controlWorkspaceSourceQueue",
                            new WorkspaceSourceControlRequest { QueueId = queueId, Command = WorkspaceSourceCommand.CancelAll });
                        AssertTrue(!(bool)untrusted["ok"], "queue controls keep the bridge trust gate");
                        bridge.Dispose();
                        AssertTrue(!bridge.RequestsDrained.IsCompleted, "closing UI cannot release resources before source worker drains");
                    }
                    finally { release.Set(); }
                    AssertTrue(bridge.RequestsDrained.Wait(5000), "closing cancels and drains background work");
                    AssertTrue(!new ChatRunRegistry(paths).HasExternalRuns(), "close releases storage activity");
                }
            });
        }

        private static WorkspaceStateDto CreateSourceQueueProject(WorkspaceApplicationService app, AppDataPaths paths)
        {
            var root = Path.Combine(paths.Root, "workspace");
            Directory.CreateDirectory(root);
            var state = app.OpenWorkspace(new OpenWorkspaceRequest { RootPath = root });
            return app.CreateTabularProject(new CreateTabularProjectRequest {
                BindingId = state.Workspace.BindingId, ManifestPath = "project.json", Name = "Queue project" });
        }
        private static PrepareWorkspaceSourcesRequest SourceQueueRequest(WorkspaceStateDto state, List<string> paths)
        { return new PrepareWorkspaceSourcesRequest { BindingId = state.Workspace.BindingId, ProjectRevision = state.Revision,
            Paths = paths, MaximumFileBytes = 3L * 1024 * 1024, MaximumBatchBytes = 4L * 1024 * 1024 }; }
        private static WorkspaceSourceQueuePage ReadSourcePage(WorkspaceApplicationService app, string id)
        { return app.ReadSourceQueue(new WorkspaceSourcePageRequest { QueueId = id }); }
        private static WorkspaceSourceQueuePage SourceCommand(WorkspaceApplicationService app, string id, WorkspaceSourceCommand command)
        { return app.ControlSourceQueue(new WorkspaceSourceControlRequest { QueueId = id, Command = command }); }
        private static WorkspaceSourceQueuePage WaitSourceQueue(WorkspaceApplicationService app, string id, Func<WorkspaceSourceQueuePage, bool> predicate)
        {
            WorkspaceSourceQueuePage page = null;
            AssertTrue(SpinWait.SpinUntil(() => { page = ReadSourcePage(app, id); return predicate(page); }, 5000),
                "source queue reaches expected state: " + (page == null ? "absent" : JsonConvert.SerializeObject(page)));
            return page;
        }
        private static void ExpectSourceQueueError(string code, Action action)
        {
            try { action(); }
            catch (WorkspaceApplicationException error) { AssertEqual(code, error.Code, "queue refusal"); return; }
            throw new InvalidOperationException("Expected queue refusal " + code);
        }
    }
}
