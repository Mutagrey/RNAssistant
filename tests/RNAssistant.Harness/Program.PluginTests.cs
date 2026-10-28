using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Runtime.Contracts;
using RNAssistant.Runtime;
using RNAssistant.Office;
using RNAssistant.Office.WebView;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void WithPluginPaths(Action<AppDataPaths> test)
        {
            WithTempPaths(paths => test(paths.Root.StartsWith("/var/",StringComparison.Ordinal)
                ? AppDataPaths.CreateForRoot("/private" + paths.Root) : paths));
        }
        private sealed class PluginPicker : IPluginProjectPicker
        {
            public string Root;
            public Task<PluginDataSelection> PickAsync(CancellationToken token){return Task.FromResult(new PluginDataSelection{RootPath=Root,Writable=true});}
        }
        private sealed class PluginExecutor : IPluginCommandExecutor
        {
            public Func<PluginInstance,CancellationToken,Task<string>> Execute;
            public Task<string> ExecuteAsync(PluginInstance instance,PluginCommandDefinition command,string input,CancellationToken token,Action<string> progress)
            {return Execute(instance,token);}
        }
        private sealed class PluginChatOfficeProbe : IWorkspaceOfficeCapabilities
        {
            public int Captures;
            public int Binds;
            public WorkspaceOfficeRunTarget CaptureCurrentTarget()
            {
                Interlocked.Increment(ref Captures);
                return new WorkspaceOfficeRunTarget { Host = "Excel", DocumentTitle = "Book.xlsx",
                    DocumentKey = "book", RuntimeDocumentKey = "runtime", DocumentAuthorityId = "authority" };
            }
            public WorkspaceOfficeCapabilitySet ForTarget(WorkspaceOfficeRunTarget target, ChatSession session,
                AppSettings settings, string runId)
            {
                Interlocked.Increment(ref Binds);
                return new WorkspaceOfficeCapabilitySet { Target = target, Available = false,
                    ModelProjection = new ModelResultProjection() };
            }
        }

        private static void PluginsInputReceiptBeforeModelResponse()
        {
            WithPluginPaths(paths =>
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
                using (var listener = new HttpListener())
                using (var entered = new ManualResetEventSlim())
                using (var release = new ManualResetEventSlim())
                using (var service = new PluginApplicationService(paths))
                {
                    listener.Prefixes.Add("http://127.0.0.1:" + port + "/"); listener.Start();
                    var server = Task.Run(async () =>
                    {
                        var context = await listener.GetContextAsync().ConfigureAwait(false);
                        using (var reader = new StreamReader(context.Request.InputStream))
                            await reader.ReadToEndAsync().ConfigureAwait(false);
                        entered.Set();
                        if (!release.Wait(10000)) throw new TimeoutException("Receipt test did not release the model.");
                        var bytes = Encoding.UTF8.GetBytes(new JObject { ["choices"] = new JArray(new JObject {
                            ["message"] = new JObject { ["content"] = ModelProtocolWire.WriteAcceptedDecision(
                                ConversationResponse.FinishAction, "No tools needed.", "Reply.", results: new ConversationResultClaim[0], outcome: ConversationOutcome.Completed) } })
                        }.ToString(Formatting.None));
                        context.Response.ContentLength64 = bytes.Length;
                        await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                        context.Response.Close();
                    });
                    new SettingsService(paths).Save(new AppSettings { BaseUrl = "http://127.0.0.1:" + port,
                        Model = "test-model", AgentResponseMode = "json_object", StreamResponses = false,
                        AutoCompressContext = false, SmartChatTitles = false });
                    var plugin = service.Create(new PluginCreateRequest { Name = "Input receipt",
                        RootPath = Path.Combine(paths.Root, "receipt-source"), Description = "Test" });
                    var chat = service.CreateProjectChat(new ProjectChatRequest { WorkspaceId = plugin.SourceWorkspaceId });
                    var request = new ProjectChatRequest { WorkspaceId = plugin.SourceWorkspaceId, SessionId = chat.SessionId,
                        Input = new WorkspaceSubmitInputRequest { SessionId = chat.SessionId, RequestId = "receipt-once",
                            Message = "Read this message", Mode = ChatModes.Agent } };
                    var wire = new System.Collections.Concurrent.ConcurrentQueue<string>();
                    using (var bridge = new AssistantWebBridge(new AssistantController(paths), wire.Enqueue) { Plugins = service })
                    {
                        var pending = bridge.HandleMessageAsync(JsonConvert.SerializeObject(new { contractVersion = 2,
                            id = "receipt-submit", type = "submitProjectChat", bridgeToken = BridgeToken(bridge), payload = request }));
                        JObject[] receipts;
                        JObject terminal;
                        try
                        {
                            AssertTrue(entered.Wait(5000), "model request starts");
                            AssertTrue(pending.Wait(5000), "submit returns while the model response is still held");
                            terminal = JObject.Parse(pending.GetAwaiter().GetResult());
                            AssertTrue((bool)terminal["ok"], "admission succeeds");
                            AssertEqual("running", (string)terminal["payload"]["state"]["Status"], "admission includes the first durable kernel checkpoint");
                            AssertTrue(!service.Conversations.RunsDrained.IsCompleted, "application owns the outstanding run");
                            PluginFailure(() => { using (PluginStore.AcquireRuntimeUse(paths, exclusive: true)) { } },
                                "plugins_busy");
                            receipts = wire.Select(JObject.Parse).Where(item => item["payload"]?["inputReceipt"] != null).ToArray();
                            AssertEqual(2, receipts.Length, "accepted and applied receipts cross the real bridge before the response");
                            AssertTrue(!(bool)receipts[0]["payload"]["inputReceipt"]["DeliveryRecorded"] &&
                                (bool)receipts[1]["payload"]["inputReceipt"]["DeliveryRecorded"], "admission and history delivery are distinct");
                            AssertEqual("receipt-submit", (string)receipts[1]["id"], "receipt keeps the transport request identity");
                            AssertEqual(chat.SessionId, (string)receipts[1]["payload"]["inputReceipt"]["SessionId"], "receipt keeps session identity");
                            var duplicate = WorkspaceBridgeCall(bridge, BridgeToken(bridge), "submitProjectChat", request);
                            AssertTrue((bool)duplicate["ok"] && JToken.DeepEquals(terminal["payload"]["inputReceipt"], duplicate["payload"]["inputReceipt"]),
                                "retry while running returns the same receipt without another model request");
                            bridge.Dispose();
                            using (var reopened = new AssistantWebBridge(new AssistantController(paths), ignored => { }) { Plugins = service })
                            {
                                var observed = WorkspaceBridgeCall(reopened, BridgeToken(reopened), "getProjectChat", new ProjectChatRequest {
                                    WorkspaceId = request.WorkspaceId, SessionId = request.SessionId, ReceiptRequestId = request.Input.RequestId });
                                AssertEqual("running", (string)observed["payload"]["state"]["Status"], "WebView recreation does not cancel the admitted run");
                                AssertTrue(JToken.DeepEquals(terminal["payload"]["inputReceipt"], observed["payload"]["inputReceipt"]),
                                    "recreated bridge reads the original receipt while the run holds the session lease");
                            }
                        }
                        finally { release.Set(); }
                        server.GetAwaiter().GetResult();
                        service.Conversations.RunsDrained.GetAwaiter().GetResult();
                        AssertTrue(JToken.DeepEquals(receipts[1]["payload"]["inputReceipt"], terminal["payload"]["inputReceipt"]),
                            "admission response repeats the exact delivered receipt");
                        AssertEqual("completed", service.ProjectChat(request).State.Status, "run completes after admission and bridge disposal");
                        AssertEqual(1, service.ProjectChat(request).Messages.Count(item => item.Role == "user"), "one persisted input");
                        var fixture = Environment.GetEnvironmentVariable("RNA_WORKSPACE_RECEIPT_FIXTURE");
                        if (!string.IsNullOrWhiteSpace(fixture))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(fixture)));
                            File.WriteAllText(fixture, new JObject { ["accepted"] = receipts[0], ["applied"] = receipts[1],
                                ["terminal"] = terminal,
                                ["completed"] = JObject.FromObject(service.ProjectChat(request)) }.ToString(Formatting.Indented));
                        }
                    }
                }
            });
        }

        private static void PluginsAdmittedRunLifetime()
        {
            foreach (var closeOwner in new[] { false, true })
            WithPluginPaths(paths =>
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
                using (var listener = new HttpListener())
                using (var entered = new ManualResetEventSlim())
                using (var release = new ManualResetEventSlim())
                using (var transport = new CancellationTokenSource())
                using (var service = new PluginApplicationService(paths))
                {
                    listener.Prefixes.Add("http://127.0.0.1:" + port + "/"); listener.Start();
                    var server = Task.Run(async () =>
                    {
                        var context = await listener.GetContextAsync().ConfigureAwait(false);
                        using (var reader = new StreamReader(context.Request.InputStream))
                            await reader.ReadToEndAsync().ConfigureAwait(false);
                        entered.Set();
                        if (!release.Wait(10000)) throw new TimeoutException("Lifetime test did not release the endpoint.");
                        context.Response.Close();
                    });
                    try
                    {
                        var workspaceId = service.CreateProjectWorkspace(new ProjectConnectionRequest {
                            RootPath = Path.Combine(paths.Root, "owned-run") }).SelectedWorkspaceId;
                        var chat = service.CreateProjectChat(new ProjectChatRequest { WorkspaceId = workspaceId });
                        var request = new ProjectChatRequest { WorkspaceId = workspaceId, SessionId = chat.SessionId,
                            Input = new WorkspaceSubmitInputRequest { SessionId = chat.SessionId, RequestId = "owned-once",
                                Message = "Keep running", Mode = ChatModes.Agent } };
                        var settings = new AppSettings { BaseUrl = "http://127.0.0.1:" + port, Model = "test-model",
                            StreamResponses = false, SmartChatTitles = false, AutoCompressContext = false };
                        var leaseDirectory = Path.Combine(paths.WorkspaceDirectory, "run-leases");
                        Directory.CreateDirectory(leaseDirectory);
                        var leasePath = Path.Combine(leaseDirectory, TextPatternEngine.Sha256(chat.SessionId) + ".lck");
                        var workspace = new WorkspaceStore(paths).OpenConnected(workspaceId);
                        using (var pendingTransport = new CancellationTokenSource())
                        using (new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                        {
                            var blocked = service.Conversations.SubmitInputAsync(workspace, request.Input,
                                settings, () => "", cancellationToken: pendingTransport.Token);
                            AssertTrue(!service.Conversations.RunsDrained.Wait(100), "CLI admission waits for the occupied lease");
                            pendingTransport.Cancel();
                            AssertTrue(service.Conversations.RunsDrained.Wait(3000), "cancel before admission stops lease waiting promptly");
                            var cancelledBeforeAdmission = false;
                            try { blocked.GetAwaiter().GetResult(); }
                            catch (OperationCanceledException) { cancelledBeforeAdmission = true; }
                            AssertTrue(cancelledBeforeAdmission && new ChatStore(paths).Load(chat.SessionId).Messages.Count == 0,
                                "cancel before admission records no input and starts no model");
                        }
                        var accepted = service.SubmitProjectChatAsync(request, settings, () => "", transport.Token).GetAwaiter().GetResult();
                        AssertTrue(accepted.InputReceipt.DeliveryRecorded && entered.Wait(5000), "admission precedes the held model response");
                        transport.Cancel();
                        var worker = service.Conversations.RunsDrained;
                        AssertTrue(!worker.Wait(200), "late transport cancellation does not stop an admitted run");
                        if (closeOwner)
                        {
                            AssertTrue(service.CloseAsync().Wait(5000), "owner shutdown cancels and drains the run");
                            AssertTrue(service.Drained.IsCompleted, "pane shutdown can await all application work");
                        }
                        else
                        {
                            var current = service.ProjectChat(request);
                            var cancelled = service.ControlProjectChatAsync(new ProjectChatRequest {
                                WorkspaceId = workspaceId, SessionId = chat.SessionId,
                                RunId = current.State.RunId, Cursor = current.State.Cursor }, true,
                                settings, () => "", CancellationToken.None).GetAwaiter().GetResult();
                            AssertEqual(WorkspaceCancellationStatus.Requested, cancelled.CancelStatus.Value, "explicit Stop reaches the exact run owner");
                            AssertTrue(worker.Wait(5000), "cancelled run drains");
                        }
                        var durable = new ChatStore(paths).Load(chat.SessionId);
                        AssertEqual(RunLifecycle.Cancelled, durable.LastRun.KernelState.Summary.Lifecycle, "kernel stores terminal cancellation");
                        AssertEqual(1, durable.Messages.Count(item => item.Role == "user"), "one accepted input survives cancellation");
                        using (PluginStore.AcquireRuntimeUse(paths, exclusive: true)) { }
                        var lease = Path.Combine(paths.WorkspaceDirectory, "run-leases", TextPatternEngine.Sha256(chat.SessionId) + ".lck");
                        using (new FileStream(lease, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                    }
                    finally { release.Set(); server.GetAwaiter().GetResult(); service.CloseAsync().GetAwaiter().GetResult(); }
                }
            });
        }

        private static void PluginsSourceChatExcludesOffice()
        {
            WithPluginPaths(paths =>
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                using (var listener = new HttpListener())
                {
                    listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                    listener.Start();
                    var requests = new string[2];
                    var server = Task.Run(async () =>
                    {
                        try
                        {
                            for (var index = 0; index < requests.Length; index++)
                            {
                                var context = await listener.GetContextAsync().ConfigureAwait(false);
                                using (var reader = new StreamReader(context.Request.InputStream))
                                    requests[index] = await reader.ReadToEndAsync().ConfigureAwait(false);
                                var response = new JObject { ["choices"] = new JArray(new JObject {
                                    ["message"] = new JObject { ["content"] =
                                        ModelProtocolWire.WriteAcceptedDecision(ConversationResponse.FinishAction,
                                            "The reply needs no tools.", "Готово.", results: new ConversationResultClaim[0], outcome: ConversationOutcome.Completed) } })
                                }.ToString(Formatting.None);
                                var bytes = Encoding.UTF8.GetBytes(response);
                                context.Response.ContentType = "application/json";
                                context.Response.ContentLength64 = bytes.Length;
                                await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                                context.Response.Close();
                            }
                        }
                        catch (HttpListenerException) { }
                    });
                    try
                    {
                        var office = new PluginChatOfficeProbe();
                        using (var service = new PluginApplicationService(paths, officeCapabilities: office))
                        {
                            var settings = new AppSettings { BaseUrl = "http://127.0.0.1:" + port,
                                Model = "test-model", StreamResponses = false, AutoCompressContext = false,
                                SmartChatTitles = false };
                            var plugin = service.Create(new PluginCreateRequest { Name = "Source scope",
                                RootPath = Path.Combine(paths.Root, "plugin-source"), Description = "Test" });
                            var initialSource = service.ChatState(plugin.PluginId);
                            AssertTrue(initialSource.SessionId == null && initialSource.Messages.Count == 0,
                                "opening plugin source does not create a chat");
                            var source = service.CreateProjectChat(new ProjectChatRequest {
                                WorkspaceId = plugin.SourceWorkspaceId });
                            var sourceProgress = new System.Collections.Generic.List<WorkspaceRunEvent>();
                            var sourceReply = service.SubmitProjectChatAsync(new ProjectChatRequest {
                                WorkspaceId = source.Project.WorkspaceId, SessionId = source.SessionId,
                                Input = new WorkspaceSubmitInputRequest { SessionId = source.SessionId,
                                    Message = "Edit the plugin", Mode = ChatModes.Agent }
                            }, settings, () => "test-key", CancellationToken.None, sourceProgress.Add).GetAwaiter().GetResult();
                            service.Conversations.RunsDrained.GetAwaiter().GetResult();
                            AssertTrue(sourceReply.State.Office == null && office.Captures == 0 && office.Binds == 0,
                                "plugin source run never captures or binds an Office document");
                            var sourcePreview = sourceProgress.Where(item => item.ModelProgress != null).ToArray();
                            AssertTrue(sourcePreview.Length > 0 && sourcePreview[0].ModelProgress.Sequence > 0 &&
                                sourcePreview.All(item => item.ModelProgress.RunId == sourceReply.State.RunId),
                                "plugin source forwards ordered, run-scoped model preview");

                            var projectRoot = Path.Combine(paths.Root, "ordinary-project");
                            Directory.CreateDirectory(projectRoot);
                            var workspaceId = service.ConnectProjectWorkspace(new ProjectConnectionRequest {
                                RootPath = projectRoot }).SelectedWorkspaceId;
                            var project = service.CreateProjectChat(new ProjectChatRequest { WorkspaceId = workspaceId });
                            new SettingsService(paths).Save(settings);
                            var progressMessages = new System.Collections.Generic.List<string>();
                            using (var bridge = new AssistantWebBridge(new AssistantController(paths), progressMessages.Add))
                            {
                                bridge.Plugins = service;
                                var bridgeRequest = WorkspaceBridgeRequest(BridgeToken(bridge), "submitProjectChat",
                                    new ProjectChatRequest { WorkspaceId = workspaceId, SessionId = project.SessionId,
                                        Input = new WorkspaceSubmitInputRequest { SessionId = project.SessionId,
                                            Message = "Inspect this project", Mode = ChatModes.Agent } });
                                var reply = JObject.Parse(bridge.HandleMessageAsync(bridgeRequest).GetAwaiter().GetResult());
                                AssertTrue((bool)reply["ok"], "project chat submits through the shared bridge");
                                service.Conversations.RunsDrained.GetAwaiter().GetResult();
                                var projectReply = service.ProjectChat(new ProjectChatRequest {
                                    WorkspaceId = workspaceId, SessionId = project.SessionId });
                                AssertTrue(projectReply.State.Office != null && office.Captures == 1 && office.Binds == 1,
                                    "ordinary project chat still captures its Office target");
                                var preview = progressMessages.Select(JObject.Parse)
                                    .Where(item => (string)item["payload"]?["modelProgress"]?["RunId"] == projectReply.State.RunId)
                                    .ToArray();
                                AssertTrue(preview.Length > 0 && preview.All(item =>
                                    (string)item["id"] == (string)JObject.Parse(bridgeRequest)["id"] &&
                                    (string)item["type"] == "progress" &&
                                    (string)item["payload"]?["workspaceId"] == workspaceId &&
                                    (string)item["payload"]?["sessionId"] == project.SessionId &&
                                    (long)item["payload"]?["modelProgress"]?["Sequence"] > sourcePreview.Last().ModelProgress.Sequence),
                                    "project bridge delivers ordered model preview for the exact workspace/session");
                            }
                        }
                        server.GetAwaiter().GetResult();
                        var sourcePrompt = string.Join("\n", ((JArray)JObject.Parse(requests[0])["messages"])
                            .Select(message => (string)message["content"]));
                        var projectPrompt = string.Join("\n", ((JArray)JObject.Parse(requests[1])["messages"])
                            .Select(message => (string)message["content"]));
                        AssertTrue(sourcePrompt.Contains("This is the source chat for an RNAssistant plugin") &&
                            !sourcePrompt.Contains("type=office") && !sourcePrompt.Contains("\"enum\":[\"office\"]"),
                            "plugin source model request names its scope and omits Office resource admission");
                        AssertTrue(projectPrompt.Contains("Office document for this run") &&
                            projectPrompt.Contains("\"enum\":[\"office\"]"),
                            "ordinary project model request retains Office resources");
                    }
                    finally { listener.Stop(); }
                }
            });
        }
        private static void WorkspaceVisualSelectionUsesSharedChat()
        {
            WithPluginPaths(paths =>
            {
                using (var service = new PluginApplicationService(paths))
                {
                    var plugin = PluginFixture(service, paths, false);
                    var sessionId = service.CreateProjectChat(new ProjectChatRequest {
                        WorkspaceId = plugin.SourceWorkspaceId }).SessionId;
                    var selection = new HtmlVisualSelection {
                        Tag = "button", StableUiId = "", Selector = "html > body > main:nth-of-type(1) > button:nth-of-type(1)",
                        Attributes = new System.Collections.Generic.Dictionary<string, string> { { "id", "run" } },
                        Ancestry = new System.Collections.Generic.List<HtmlVisualAncestor>(), TextExcerpt = "Проверить команду",
                        Rect = new HtmlVisualRect { X = 10, Y = 20, Width = 80, Height = 30 },
                        Viewport = new HtmlVisualViewport { Width = 800, Height = 600, Dpr = 1 }
                    };
                    var body = HtmlVisualSelectionPolicy.Capture(selection, "Test plugin", "Fix button", 2);
                    AssertEqual(Encoding.UTF8.GetString(body), HtmlVisualSelectionPolicy.ReadCapturedBody(body),
                        "shared Core policy admits the exact captured body");
                    var forged = JObject.Parse(Encoding.UTF8.GetString(body));
                    forged["extra"] = "forged";
                    RuntimeThrows<InvalidOperationException>(() => HtmlVisualSelectionPolicy.ReadCapturedBody(
                        Encoding.UTF8.GetBytes(forged.ToString(Formatting.None))));
                    forged.Remove("extra");
                    forged["version"] = "invalid";
                    RuntimeThrows<InvalidOperationException>(() => HtmlVisualSelectionPolicy.ReadCapturedBody(
                        Encoding.UTF8.GetBytes(forged.ToString(Formatting.None))));
                    var opened = service.Open(new PluginOpenRequest { PluginId = plugin.PluginId });
                    var selectionRequest = new PluginVisualSelectionRequest { InstanceId = opened.InstanceId,
                        WorkspaceId = plugin.SourceWorkspaceId, SessionId = sessionId, PreviewGeneration = 1,
                        Selection = selection, Comment = "Fix button", AnnotationNumber = 2 };
                    var draft = service.StageVisualSelection(selectionRequest);
                    AssertEqual(PluginStore.ReleaseRef(plugin.PluginId, plugin.ReleaseId).Uri,
                        new AttachmentStore(paths).LoadDrafts(new[] { draft.Id }, sessionId).Single().VisualSelectionSource.Uri,
                        "native staging pins the bound plugin release without exposing its URI to the browser");
                    var checkedCandidate = service.Check(plugin.PluginId);
                    var preview = service.Open(new PluginOpenRequest { PluginId = plugin.PluginId,
                        CandidateId = checkedCandidate.CandidateId });
                    selectionRequest.InstanceId = preview.InstanceId;
                    var candidateDraft = service.StageVisualSelection(selectionRequest);
                    AssertTrue(preview.ReleaseId != plugin.ReleaseId &&
                        new AttachmentStore(paths).LoadDrafts(new[] { candidateDraft.Id }, sessionId).Single()
                            .VisualSelectionSource.Uri == PluginStore.ReleaseRef(plugin.PluginId, preview.ReleaseId).Uri,
                        "checked candidate preview keeps its own retained release identity");
                    service.DiscardVisualSelection(new PluginVisualDraftRequest { WorkspaceId = plugin.SourceWorkspaceId,
                        SessionId = sessionId, Id = candidateDraft.Id });
                    service.CloseInstanceAsync(preview.InstanceId).GetAwaiter().GetResult();
                    PluginFailure(() => service.StageVisualSelection(selectionRequest), "plugin_selection_changed");
                    selectionRequest.InstanceId = opened.InstanceId;
                    PluginFailure(() => service.StageVisualSelection(new PluginVisualSelectionRequest {
                        InstanceId = "unknown", WorkspaceId = plugin.SourceWorkspaceId, SessionId = sessionId,
                        PreviewGeneration = 1, Selection = selection, AnnotationNumber = 3 }), "plugin_selection_changed");
                    var disposable = service.StageVisualSelection(selectionRequest);
                    AssertTrue(service.DiscardVisualSelection(new PluginVisualDraftRequest {
                        WorkspaceId = plugin.SourceWorkspaceId, SessionId = sessionId, Id = disposable.Id }).Discarded,
                        "discard acknowledges a staged draft in its chat");
                    RuntimeThrows<InvalidOperationException>(() => new AttachmentStore(paths).LoadDrafts(
                        new[] { disposable.Id }, sessionId));

                    var portProbe = new TcpListener(IPAddress.Loopback, 0);
                    portProbe.Start();
                    var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
                    portProbe.Stop();
                    using (var listener = new HttpListener())
                    {
                        listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                        listener.Start();
                        string modelRequest = null;
                        var server = Task.Run(async () =>
                        {
                            var context = await listener.GetContextAsync().ConfigureAwait(false);
                            using (var reader = new StreamReader(context.Request.InputStream))
                                modelRequest = await reader.ReadToEndAsync().ConfigureAwait(false);
                            var response = new JObject { ["choices"] = new JArray(new JObject {
                                ["message"] = new JObject { ["content"] =
                                    ModelProtocolWire.WriteAcceptedDecision(ConversationResponse.FinishAction,
                                        "The reply needs no tools.", "Готово.", results: new ConversationResultClaim[0], outcome: ConversationOutcome.Completed) } })
                            }.ToString(Formatting.None);
                            var bytes = Encoding.UTF8.GetBytes(response);
                            context.Response.ContentType = "application/json";
                            context.Response.ContentLength64 = bytes.Length;
                            await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                            context.Response.Close();
                        });
                        try
                        {
                            var settings = new AppSettings { BaseUrl = "http://127.0.0.1:" + port,
                                Model = "test-model", StreamResponses = false, AutoCompressContext = false,
                                SmartChatTitles = false };
                            service.SubmitProjectChatAsync(new ProjectChatRequest {
                                WorkspaceId = plugin.SourceWorkspaceId, SessionId = sessionId,
                                Input = new WorkspaceSubmitInputRequest { SessionId = sessionId, RequestId = "visual-once",
                                    Message = "Inspect this button", Mode = ChatModes.Agent,
                                    ResourceDraftIds = new System.Collections.Generic.List<string> { draft.Id } }
                            }, settings, () => "test-key", CancellationToken.None).GetAwaiter().GetResult();
                            server.GetAwaiter().GetResult();
                            service.Conversations.RunsDrained.GetAwaiter().GetResult();
                            service.SubmitProjectChatAsync(new ProjectChatRequest {
                                WorkspaceId = plugin.SourceWorkspaceId, SessionId = sessionId,
                                Input = new WorkspaceSubmitInputRequest { SessionId = sessionId, RequestId = "visual-once",
                                    Message = "Inspect this button", Mode = ChatModes.Agent,
                                    ResourceDraftIds = new System.Collections.Generic.List<string> { draft.Id } }
                            }, settings, () => "test-key", CancellationToken.None).GetAwaiter().GetResult();
                            var session = new ChatStore(paths).Load(sessionId);
                            var user = session.Messages.Single(item => item.Role == "user");
                            var historyView = service.ProjectChat(new ProjectChatRequest {
                                WorkspaceId = plugin.SourceWorkspaceId, SessionId = sessionId });
                            var visualView = historyView.Messages.Single(item => item.Role == "user").Attachments.Single();
                            AssertEqual("Fix button", visualView.VisualAnnotation.Comment,
                                "shared project chat projects the immutable sent annotation");
                            AssertEqual(plugin.ReleaseId, visualView.VisualSourceArtifactId,
                                "sent annotation remains pinned to the retained release");
                            var committed = user.Attachments.Single();
                            AssertTrue(modelRequest.Contains("Current user visual selections") &&
                                modelRequest.Contains("skills.workspace.plugin.authoring") &&
                                modelRequest.Contains("\\\"attachment\\\"") &&
                                !modelRequest.Contains("rnassistant.html-visual-selection") &&
                                !modelRequest.Contains(PluginStore.ReleaseRef(plugin.PluginId, plugin.ReleaseId).Uri) &&
                                committed.ContentSha256 != null && committed.DraftChatId == null,
                                "model receives a read target without implicit DOM body or runtime source URI");
                            AssertTrue(user.ResourceRefs.Any(item => item.Uri == PluginStore.ReleaseRef(plugin.PluginId, plugin.ReleaseId).Uri) &&
                                new AttachmentStore(paths).ReadBytes(committed).SequenceEqual(body),
                                "accepted message retains exact plugin source and selection body");
                            var provider = new WorkspaceChatAttachmentResourceProvider(paths, session);
                            var found = provider.Find(null);
                            AssertEqual(1, found.Items.Count, "accepted selection is discoverable");
                            var target = found.Items.Single().Target;
                            var ports = typeof(WorkspaceConversationService).GetNestedType("WorkspacePorts",
                                System.Reflection.BindingFlags.NonPublic);
                            var schema = JObject.Parse((string)ports.GetMethod("ResourceSchema",
                                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                                .Invoke(null, new object[] { false, true, false, 16000 }));
                            string schemaError;
                            AssertTrue(ToolSchemaSupport.ValidateArguments(new JObject {
                                ["type"] = "attachment", ["target"] = target }, schema, false, out schemaError),
                                "shared workspace read schema admits selection targets");
                            AssertTrue(!ToolSchemaSupport.ValidateArguments(new JObject {
                                ["type"] = "attachment", ["target"] = target, ["scope"] = "document"
                            }, schema, false, out schemaError), "schema rejects a foreign selector");
                            var findHandler = new WorkspaceResourceHandler(null, null, () => null,
                                new System.Collections.Generic.Dictionary<string, ResourceEvidence>(), true, null, 16000,
                                chatAttachments: provider);
                            var execution = new ToolRuntimeFixture().Context(toolId: "core.read");
                            var foundByTool = findHandler.ExecuteAsync(new ToolHandlerContext(execution,
                                new System.Collections.Generic.Dictionary<string, object> { ["type"] = "attachment" }),
                                CancellationToken.None).GetAwaiter().GetResult();
                            AssertTrue(foundByTool.Result.Status == ToolResultStatus.Ok &&
                                foundByTool.Result.DataJson.Contains(target),
                                "core.find discovers the accepted target");
                            AssertEqual(body.Length, provider.Read(target, 16000).Result.CompleteViewPayload.ByteLength,
                                "resource read owns the original body");
                            var handler = new WorkspaceResourceHandler(null, null, () => null,
                                new System.Collections.Generic.Dictionary<string, ResourceEvidence>(), false, null, 16000,
                                chatAttachments: provider);
                            var read = handler.ExecuteAsync(new ToolHandlerContext(execution,
                                new System.Collections.Generic.Dictionary<string, object> {
                                    ["type"] = "attachment", ["target"] = target }), CancellationToken.None)
                                .GetAwaiter().GetResult();
                            AssertEqual(ToolResultStatus.Ok, read.Result.Status,
                                "core.read returns the committed selection");
                            AssertTrue(read.Result.DataJson.Contains("rnassistant.html-visual-selection") &&
                                read.ResourceEvidence.Single().Immutable,
                                "model read carries exact immutable evidence");
                            var accepted = HtmlVisualSelectionPolicy.ReadAnnotation(new AttachmentStore(paths).ReadBytes(committed));
                            var sourceWorkspace = new WorkspaceStore(paths).Open(plugin.SourceRoot, false, true);
                            var files = new WorkspaceFileService(paths);
                            var source = files.ReadText(sourceWorkspace, "index.html");
                            var anchor = "<button id=\"run\">Проверить команду</button>";
                            var published = new PluginStore(paths).ReadRelease(
                                new PluginReleaseIdentity(plugin.PluginId, plugin.ReleaseId));
                            var retainedHtml = Encoding.UTF8.GetString(new PluginStore(paths).ReadFile(published, "index.html"));
                            AssertTrue(accepted.Selection.Attributes["id"] == "run" &&
                                retainedHtml == source.Text && source.Text.Contains(anchor),
                                "accepted DOM evidence is checked against one real source anchor and the retained release");
                            AssertEqual("patch_ambiguous", RuntimeThrows<WorkspaceFileException>(() =>
                                files.PatchExact(sourceWorkspace, "index.html", source.Reference,
                                    "id=\"", "id=\"other-")).Code,
                                "an ambiguous source anchor refuses the annotated edit");
                            AssertEqual(source.Reference.Revision, files.ReadText(sourceWorkspace, "index.html").Reference.Revision,
                                "ambiguous refusal leaves plugin source unchanged");
                            var changed = files.PatchExact(sourceWorkspace, "index.html", source.Reference, anchor,
                                "<button id=\"run\">Выполнить команду</button>");
                            AssertTrue(changed.Text.Contains("Выполнить команду") &&
                                files.ReadText(sourceWorkspace, "index.html").Text.Contains("Выполнить команду") &&
                                service.Check(plugin.PluginId).HasChanges,
                                "existing guarded patch verifies real plugin source and produces a checked candidate");
                            AssertEqual("target_conflict", RuntimeThrows<WorkspaceFileException>(() =>
                                files.PatchExact(sourceWorkspace, "index.html", source.Reference, anchor,
                                    "<button id=\"run\">Устаревшая правка</button>")).Code,
                                "a stale source read cannot overwrite the verified annotation edit");
                            var bounded = new WorkspaceResourceHandler(null, null, () => null,
                                new System.Collections.Generic.Dictionary<string, ResourceEvidence>(), false, null, 64,
                                chatAttachments: provider).ExecuteAsync(new ToolHandlerContext(execution,
                                new System.Collections.Generic.Dictionary<string, object> {
                                    ["type"] = "attachment", ["target"] = target }), CancellationToken.None)
                                .GetAwaiter().GetResult();
                            AssertTrue(!(bool)JObject.Parse(bounded.Result.DataJson)["complete"] &&
                                bounded.ResourceEvidence.Single().ReadMetadata.RequestedCoverage.Kind ==
                                    ResourceCoverageKinds.Whole,
                                "automatic excerpt records whole-read intent without claiming whole evidence");
                            var wrongSelector = handler.ExecuteAsync(new ToolHandlerContext(execution,
                                new System.Collections.Generic.Dictionary<string, object> {
                                    ["type"] = "attachment", ["target"] = target, ["scope"] = "document" }),
                                CancellationToken.None).GetAwaiter().GetResult();
                            AssertEqual(ToolResultStatus.Error, wrongSelector.Result.Status,
                                "foreign resource selectors fail closed");
                            var duplicate = JsonConvert.DeserializeObject<ChatAttachment>(JsonConvert.SerializeObject(committed));
                            duplicate.Id = Guid.NewGuid().ToString("N");
                            var second = new ChatMessage { Id = Guid.NewGuid().ToString("N"), Role = "user",
                                CreatedUtc = user.CreatedUtc, Attachments = new System.Collections.Generic.List<ChatAttachment> { duplicate } };
                            session.Messages.Add(second);
                            ChatMessageAttachmentService.Link(session, second);
                            var collision = new WorkspaceChatAttachmentResourceProvider(paths, session).Find(null);
                            AssertTrue(!collision.Complete && collision.Items.Count == 0,
                                "ambiguous semantic targets are withheld from discovery");
                        }
                        finally { listener.Stop(); }
                    }
                }
            });
        }

        private static PluginItemDto PluginFixture(PluginApplicationService service,AppDataPaths paths,bool data=true)
        {
            var item=service.Create(new PluginCreateRequest{Name="Test plugin",RootPath=Path.Combine(paths.Root,"source"),Description="Test"});
            if(data){var file=Path.Combine(item.SourceRoot,"plugin.json");var json=JObject.Parse(File.ReadAllText(file));json["capabilities"]=new JArray("project.read","project.write");File.WriteAllText(file,json.ToString());}
            var candidate=service.Check(item.PluginId);AssertTrue(candidate.HasChanges,"unpublished plugin has changes to apply");
            return service.Apply(new PluginApplyRequest{PluginId=item.PluginId,CandidateId=candidate.CandidateId});
        }
        private static PluginInstance PluginOpen(PluginApplicationService service,string id,string candidate=null)
        {var view=service.Open(new PluginOpenRequest{PluginId=id,CandidateId=candidate});return service.FindInstance(new Uri(view.EntryUrl).GetLeftPart(UriPartial.Authority));}
        private static Task<PluginFrameResponse> PluginRequest(PluginApplicationService service,PluginInstance instance,PluginMethod method,string path=null,string project=null,string handle=null)
        {return service.DispatchAsync(instance,new PluginFrameRequest{Version=1,RequestId=Guid.NewGuid().ToString("N"),Method=method,Path=path,ProjectId=project,Handle=handle},CancellationToken.None);}
        private static void PluginFailure(Action action,string code)
        {try{action();throw new Exception("Expected "+code);}catch(PluginException error){AssertEqual(code,error.Code,"plugin refusal");}}

        private static void PluginsProjectChatFileUpload()
        {
            WithPluginPaths(paths => {
                using (var service = new PluginApplicationService(paths))
                {
                    var root = Path.Combine(paths.Root, "upload-project"); Directory.CreateDirectory(root);
                    var workspaceId = service.ConnectProjectWorkspace(new ProjectConnectionRequest { RootPath = root }).SelectedWorkspaceId;
                    var sessionId = service.CreateProjectChat(new ProjectChatRequest { WorkspaceId = workspaceId }).SessionId;
                    var source = Encoding.UTF8.GetBytes("Отчёт проекта\nСтрока 2");
                    PluginFailure(() => service.BeginProjectChatFileUpload(new ProjectChatUploadOpenRequest {
                        WorkspaceId = workspaceId, SessionId = sessionId, FileName = "../escape.txt", ByteLength = 1 }),
                        "project_file_invalid");
                    PluginFailure(() => service.BeginProjectChatFileUpload(new ProjectChatUploadOpenRequest {
                        WorkspaceId = workspaceId, SessionId = "foreign", FileName = "notes.txt", ByteLength = 1 }),
                        "project_chat_missing");
                    var opened = service.BeginProjectChatFileUpload(new ProjectChatUploadOpenRequest {
                        WorkspaceId = workspaceId, SessionId = sessionId, FileName = "notes.txt", ByteLength = source.Length });
                    AssertTrue(opened.RelativePath.StartsWith("chat-files/", StringComparison.Ordinal), "upload target stays inside project chat-files");
                    PluginFailure(() => service.WriteProjectChatFileChunk(new ProjectChatUploadChunkRequest {
                        WorkspaceId = "other", SessionId = sessionId, LeaseId = opened.LeaseId, Offset = 0,
                        Base64 = Convert.ToBase64String(source) }), "project_file_lease_missing");
                    PluginFailure(() => service.WriteProjectChatFileChunk(new ProjectChatUploadChunkRequest {
                        WorkspaceId = workspaceId, SessionId = sessionId, LeaseId = opened.LeaseId, Offset = 1,
                        Base64 = Convert.ToBase64String(source) }), "project_file_offset_invalid");
                    var first = service.WriteProjectChatFileChunk(new ProjectChatUploadChunkRequest {
                        WorkspaceId = workspaceId, SessionId = sessionId, LeaseId = opened.LeaseId, Offset = 0,
                        Base64 = Convert.ToBase64String(source, 0, 4) });
                    AssertEqual(4L, first.NextOffset, "first chunk acknowledges its exact byte offset");
                    PluginFailure(() => service.CompleteProjectChatFileUpload(new ProjectChatUploadLeaseRequest {
                        WorkspaceId = workspaceId, SessionId = sessionId, LeaseId = opened.LeaseId }), "project_file_incomplete");
                    service.WriteProjectChatFileChunk(new ProjectChatUploadChunkRequest {
                        WorkspaceId = workspaceId, SessionId = sessionId, LeaseId = opened.LeaseId, Offset = 4,
                        Base64 = Convert.ToBase64String(source, 4, source.Length - 4) });
                    var result = service.CompleteProjectChatFileUpload(new ProjectChatUploadLeaseRequest {
                        WorkspaceId = workspaceId, SessionId = sessionId, LeaseId = opened.LeaseId });
                    AssertEqual(opened.RelativePath, result.RelativePath, "published path matches the announced target");
                    AssertTrue(result.ModelReadable, "UTF-8 file is readable by project model resources");
                    AssertTrue(source.SequenceEqual(File.ReadAllBytes(Path.Combine(root, result.RelativePath.Replace('/', Path.DirectorySeparatorChar)))),
                        "exact uploaded bytes are published in the selected project");
                    PluginFailure(() => service.CompleteProjectChatFileUpload(new ProjectChatUploadLeaseRequest {
                        WorkspaceId = workspaceId, SessionId = sessionId, LeaseId = opened.LeaseId }), "project_file_lease_missing");
                    var files = new WorkspaceFileService(paths).ListPage(new WorkspaceStore(paths).OpenConnected(workspaceId), "chat-files");
                    AssertTrue(files.Names.Contains(Path.GetFileName(result.RelativePath)), "uploaded file is discoverable in the project resource tree");
                    var binary = new byte[] { 0x00, 0xff, 0x31 };
                    var binaryLease = service.BeginProjectChatFileUpload(new ProjectChatUploadOpenRequest {
                        WorkspaceId = workspaceId, SessionId = sessionId, FileName = "sample.bin", ByteLength = binary.Length });
                    service.WriteProjectChatFileChunk(new ProjectChatUploadChunkRequest {
                        WorkspaceId = workspaceId, SessionId = sessionId, LeaseId = binaryLease.LeaseId,
                        Offset = 0, Base64 = Convert.ToBase64String(binary) });
                    var binaryResult = service.CompleteProjectChatFileUpload(new ProjectChatUploadLeaseRequest {
                        WorkspaceId = workspaceId, SessionId = sessionId, LeaseId = binaryLease.LeaseId });
                    AssertTrue(!binaryResult.ModelReadable &&
                        binary.SequenceEqual(File.ReadAllBytes(Path.Combine(root, binaryResult.RelativePath.Replace('/', Path.DirectorySeparatorChar)))),
                        "binary file is retained exactly without claiming model text access");
                    var largeText = Encoding.UTF8.GetBytes(new string('a', 127) + "😀" + new string('b', 1024 * 1024));
                    foreach (var validUtf8 in new[] { true, false })
                    {
                        if (!validUtf8) largeText[largeText.Length - 1] = 0xff;
                        var largeLease = service.BeginProjectChatFileUpload(new ProjectChatUploadOpenRequest {
                            WorkspaceId = workspaceId, SessionId = sessionId,
                            FileName = validUtf8 ? "large.txt" : "late-invalid.bin", ByteLength = largeText.Length });
                        for (var offset = 0; offset < largeText.Length; offset += 256 * 1024)
                            service.WriteProjectChatFileChunk(new ProjectChatUploadChunkRequest {
                                WorkspaceId = workspaceId, SessionId = sessionId, LeaseId = largeLease.LeaseId,
                                Offset = offset, Base64 = Convert.ToBase64String(largeText, offset,
                                    Math.Min(256 * 1024, largeText.Length - offset)) });
                        var largeResult = service.CompleteProjectChatFileUpload(new ProjectChatUploadLeaseRequest {
                            WorkspaceId = workspaceId, SessionId = sessionId, LeaseId = largeLease.LeaseId });
                        AssertEqual(validUtf8, largeResult.ModelReadable,
                            "streamed upload readability checks the whole UTF-8 source without a size or surrogate-boundary refusal");
                        AssertTrue(largeText.SequenceEqual(File.ReadAllBytes(Path.Combine(root,
                            largeResult.RelativePath.Replace('/', Path.DirectorySeparatorChar)))),
                            "large upload preserves the exact source bytes");
                    }
                    var cancelled = service.BeginProjectChatFileUpload(new ProjectChatUploadOpenRequest {
                        WorkspaceId = workspaceId, SessionId = sessionId, FileName = "cancel.txt", ByteLength = 3 });
                    service.CancelProjectChatFileUpload(new ProjectChatUploadLeaseRequest {
                        WorkspaceId = workspaceId, SessionId = sessionId, LeaseId = cancelled.LeaseId });
                    AssertTrue(!File.Exists(Path.Combine(root, cancelled.RelativePath.Replace('/', Path.DirectorySeparatorChar))),
                        "cancelled upload never publishes a project file");
                }
            });
        }

        private static void PluginsPublishedHistoryAndRestore()
        {
            WithPluginPaths(paths => {
                using (var service = new PluginApplicationService(paths))
                {
                    var item = PluginFixture(service, paths, false);
                    var firstRelease = item.ReleaseId;
                    var sourceFile = Path.Combine(item.SourceRoot, "app.js");
                    var original = File.ReadAllText(sourceFile);
                    File.WriteAllText(sourceFile, "revision 2");
                    var unpublished = service.Check(item.PluginId);
                    AssertEqual(1, service.ReleaseHistory(new PluginReleaseHistoryRequest { PluginId = item.PluginId }).Items.Count,
                        "checked candidate does not appear among published versions");
                    var preview = service.Open(new PluginOpenRequest { PluginId = item.PluginId, CandidateId = unpublished.CandidateId });
                    PluginFailure(() => service.Open(new PluginOpenRequest { PluginId = item.PluginId, ReleaseId = preview.ReleaseId }),
                        "plugin_release_missing");
                    service.CloseInstanceAsync(preview.InstanceId).GetAwaiter().GetResult();
                    item = service.Apply(new PluginApplyRequest { PluginId = item.PluginId, CandidateId = unpublished.CandidateId });
                    for (var revision = 3; revision <= 14; revision++)
                    {
                        File.WriteAllText(sourceFile, "revision " + revision);
                        var checkedPackage = service.Check(item.PluginId);
                        item = service.Apply(new PluginApplyRequest { PluginId = item.PluginId, CandidateId = checkedPackage.CandidateId });
                    }
                    var latestSource = File.ReadAllText(sourceFile);
                    var firstPage = service.ReleaseHistory(new PluginReleaseHistoryRequest { PluginId = item.PluginId });
                    AssertEqual(12, firstPage.Items.Count, "history reads one bounded page of metadata");
                    AssertTrue(!string.IsNullOrEmpty(firstPage.NextCursor), "older publications have a cursor");
                    var secondPage = service.ReleaseHistory(new PluginReleaseHistoryRequest {
                        PluginId = item.PluginId, Cursor = firstPage.NextCursor });
                    AssertEqual(2, secondPage.Items.Count, "history loads only requested older metadata");
                    AssertEqual(firstRelease, secondPage.Items.Last().ReleaseId, "oldest published version is available");
                    AssertTrue(secondPage.NextCursor == null, "final page has no cursor");
                    var historicalView = service.Open(new PluginOpenRequest { PluginId = item.PluginId, ReleaseId = firstRelease });
                    AssertTrue(historicalView.Preview, "historical version uses isolated preview authority");
                    var instance = service.FindInstance(new Uri(historicalView.EntryUrl).GetLeftPart(UriPartial.Authority));
                    AssertEqual(original, Encoding.UTF8.GetString(service.ReadPackageFile(instance, "app.js")),
                        "historical preview serves frozen package bytes");
                    service.CloseInstanceAsync(historicalView.InstanceId).GetAwaiter().GetResult();
                    PluginFailure(() => service.RestoreVersion(new PluginRestoreVersionRequest { PluginId = item.PluginId,
                        ReleaseId = firstRelease, ExpectedActiveReleaseId = "stale" }), "plugin_selection_changed");
                    var restored = service.RestoreVersion(new PluginRestoreVersionRequest { PluginId = item.PluginId,
                        ReleaseId = firstRelease, ExpectedActiveReleaseId = item.ReleaseId });
                    AssertEqual(firstRelease, restored.ReleaseId, "restore switches the active publication to the selected release");
                    AssertEqual(latestSource, File.ReadAllText(sourceFile), "restore leaves editable source files untouched");
                    PluginFailure(() => service.ReleaseHistory(new PluginReleaseHistoryRequest {
                        PluginId = item.PluginId, Cursor = firstPage.NextCursor }), "plugin_history_changed");
                    var restoredPage = service.ReleaseHistory(new PluginReleaseHistoryRequest { PluginId = item.PluginId });
                    AssertEqual(firstRelease, restoredPage.Items.First().ReleaseId, "restored version moves to the front");
                    AssertTrue(restoredPage.Items.First().Active, "menu marks the restored version active");
                    AssertTrue(service.Check(item.PluginId).HasChanges, "current source still differs from restored publication");
                }
            });
        }

        private static void PluginsPublishAndSeparateAuthority()
        {
            WithPluginPaths(paths=>{
                var wire = new ChatMessage { Role = "assistant", ProtocolMessage = true,
                    ResponseProtocolVersion = ConversationResponse.ProtocolVersion,
                    Content = ModelProtocolWire.WriteAcceptedCall("Read the source.", "Читаю исходник",
                        new ConversationToolCall { Name = "core.read", Arguments = new JObject { ["target"] = "README.md" } }),
                    ToolCallId = "call-1", ToolName = "core.read",
                    ToolResultProtocolVersion = ToolResultWire.CurrentVersion,
                    ToolResultRole = ToolResultRoles.User,
                    PromptTokens = 12345, CompletionTokens = 678, TotalTokens = 13023,
                    AcceptedCallOrigin = new AcceptedToolCallOrigin("step-1", "attempt-1", 0) };
                var presented = PluginApplicationService.PresentWorkspaceMessage(wire);
                AssertEqual("Читаю исходник", presented.Content, "source chat presents the model message, not protocol JSON");
                AssertEqual(wire.Id, presented.Id, "workspace uses the shared message identity");
                AssertTrue(presented.PromptTokens == 12345 && presented.CompletionTokens == 678 &&
                    presented.TotalTokens == 13023,
                    "project and plugin chats retain accepted provider usage in the shared feed");
                AssertTrue(!JsonConvert.SerializeObject(presented).Contains("README.md"),
                    "workspace browser view never exposes accepted call arguments");
                wire.ToolCallId = null;
                AssertTrue(PluginApplicationService.PresentWorkspaceMessage(wire).Content.Contains("неполная"),
                    "source chat does not present an unpaired model action as accepted history");
                var reflection = new ChatMessage { Role = "assistant", ProtocolMessage = true,
                    ResponseProtocolVersion = ConversationResponse.ProtocolVersion,
                    ResponseModelAttemptId = "attempt-reflection",
                    Content = ModelProtocolWire.WriteAcceptedDecision(ConversationResponse.ReflectionAction,
                        "Review source first.", "", decision: "Inspect the exact file before editing.") };
                var reflected = PluginApplicationService.PresentWorkspaceMessage(reflection);
                AssertEqual("", reflected.Content, "empty reflection has no user-facing narration");
                AssertEqual("Inspect the exact file before editing.", reflected.ReflectionDecision,
                    "workspace reflection retains its saved decision for a separate step");
                var finish = new ChatMessage { Role = "assistant", ProtocolMessage = true,
                    ResponseProtocolVersion = ConversationResponse.ProtocolVersion,
                    ResponseModelAttemptId = "attempt-finish",
                    Content = ModelProtocolWire.WriteAcceptedDecision(ConversationResponse.FinishAction,
                        "The chart is not present.", "The chart remains unfinished.", results:
                        new[] { new ConversationResultClaim("Chart absent", new[] { "call-1" },
                            ConversationAssessment.PartiallySatisfied) }, outcome: ConversationOutcome.Partial) };
                var finished = PluginApplicationService.PresentWorkspaceMessage(finish);
                AssertEqual("partially_satisfied", finished.Results.Single().Assessment,
                    "workspace chat retains incomplete model assessment");
                AssertEqual("call-1", finished.Results.Single().Evidence.Single(),
                    "workspace chat retains the exact terminal evidence id");
                var activity = PluginApplicationService.PresentWorkspaceMessage(new ChatMessage {
                    Role = "assistant", Content = "", Activity = new ChatActivity {
                        Kind = "tool", Title = "core.read", ToolCallId = "call-1",
                        RuntimeGuardJson = "private-guard", ConfirmationCatalogSha256 = "private-catalog"
                    }
                });
                AssertEqual("core.read", activity.Activity.Title,
                    "workspace shows the saved tool activity through the shared message view");
                AssertTrue(activity.Activity.RuntimeGuardJson == null && activity.Activity.ConfirmationCatalogSha256 == null,
                    "workspace browser activity omits runtime-only guard data");
                using(var service=new PluginApplicationService(paths))
                {
                    var item=PluginFixture(service,paths);var active=PluginOpen(service,item.PluginId);
                    PluginFailure(()=>paths.ClearRuntimeData(),"plugins_busy");
                    AssertEqual("puzzle",item.Icon,"published plugin exposes its manifest icon");
                    AssertTrue(active.Release.SourceWorkspaceId!=active.Release.ReleaseId,"source and publication identities differ");
                    AssertEqual("project_required",PluginRequest(service,active,PluginMethod.List).Result.ErrorCode,"manifest grants no data access");
                    var picker=new PluginPicker{Root=item.SourceRoot};service.ProjectPicker=picker;
                    AssertEqual("source_is_not_data",PluginRequest(service,active,PluginMethod.PickProject).Result.ErrorCode,"source cannot become data");
                    picker.Root=Path.Combine(paths.Root,"plugin-preview-data");
                    AssertEqual("preview_is_not_active",PluginRequest(service,active,PluginMethod.PickProject).Result.ErrorCode,"test project cannot become active data");
                    picker.Root=Path.Combine(paths.Root,"data");Directory.CreateDirectory(picker.Root);File.WriteAllText(Path.Combine(picker.Root,"input.txt"),"original");
                    var project=PluginRequest(service,active,PluginMethod.PickProject).Result.Result.Handle;
                    AssertEqual("project_changed",PluginRequest(service,active,PluginMethod.OpenFile,"input.txt","forged").Result.ErrorCode,"foreign project refused");
                    var read=PluginRequest(service,active,PluginMethod.OpenFile,"input.txt",project).Result.Result;
                    var candidate=service.Check(item.PluginId);AssertTrue(!candidate.HasChanges,"unchanged package does not offer Apply in the UI");
                    var preview=PluginOpen(service,item.PluginId,candidate.CandidateId);
                    var sourceStamp=service.SourceState(item.PluginId).Stamp;
                    AssertEqual("project_required",PluginRequest(service,preview,PluginMethod.List).Result.ErrorCode,"preview inherits no production grant");
                    var scratch=PluginRequest(service,preview,PluginMethod.PickProject).Result.Result.Handle;
                    AssertTrue(scratch!=project && preview.Data.DataWorkspaceId!=active.Data.DataWorkspaceId,"preview test project is independent");
                    File.WriteAllText(Path.Combine(preview.Data.Workspace.RootPath,"preview.txt"),"kept");
                    var refreshedPreview=PluginOpen(service,item.PluginId,candidate.CandidateId);
                    PluginRequest(service,refreshedPreview,PluginMethod.PickProject).GetAwaiter().GetResult();
                    AssertEqual(preview.Data.DataWorkspaceId,refreshedPreview.Data.DataWorkspaceId,"preview refresh keeps its separate test project");
                    AssertTrue(File.Exists(Path.Combine(refreshedPreview.Data.Workspace.RootPath,"preview.txt")),"preview refresh keeps test data");
                    AssertEqual("not_found",PluginRequest(service,preview,PluginMethod.OpenFile,"input.txt",scratch).Result.ErrorCode,"production bytes absent from preview");
                    var oldPreviewGrant=preview.Data;
                    PluginRequest(service,preview,PluginMethod.PickProject).GetAwaiter().GetResult();
                    AssertTrue(oldPreviewGrant.Lifetime.IsCancellationRequested,"reselecting preview data revokes its previous grant");
                    AssertEqual("original",Encoding.UTF8.GetString(active.ReadRangeAsync(read.Handle,0,8,CancellationToken.None).Result),"pinned bytes available");
                    var oldCode=service.ReadPackageFile(active,"app.js");File.WriteAllText(Path.Combine(item.SourceRoot,"app.js"),"changed");
                    AssertTrue(sourceStamp!=service.SourceState(item.PluginId).Stamp,"source token changes after a declared file edit");
                    candidate=service.Check(item.PluginId);AssertTrue(candidate.HasChanges,"changed source offers Apply in the UI");
                    service.Apply(new PluginApplyRequest{PluginId=item.PluginId,CandidateId=candidate.CandidateId});
                    AssertTrue(oldCode.SequenceEqual(service.ReadPackageFile(active,"app.js")),"active operations retain frozen bytes");
                    var fresh=PluginOpen(service,item.PluginId);AssertEqual("project_required",PluginRequest(service,fresh,PluginMethod.List).Result.ErrorCode,"new publication receives no old data grant");
                    File.WriteAllText(Path.Combine(item.SourceRoot,"plugin.json"),"{}");PluginFailure(()=>service.Check(item.PluginId),"plugin_manifest_invalid");
                    AssertEqual(fresh.Release.ReleaseId,service.List().Items.Single().ReleaseId,"failed validation preserves active release");
                    service.DisableAsync(item.PluginId).GetAwaiter().GetResult();
                    AssertTrue(active.Token.IsCancellationRequested && fresh.Token.IsCancellationRequested,"disable revokes instances");
                    AssertTrue(File.Exists(Path.Combine(picker.Root,"input.txt")),"disable preserves data");
                    paths.ClearRuntimeData();
                    new ResourceAuthorityStore(paths).ResetAfterRuntimeDataClear(); service.ResetAfterRuntimeDataClear();
                    AssertTrue(File.Exists(Path.Combine(picker.Root,"input.txt")) && File.Exists(Path.Combine(item.SourceRoot,"app.js")),"explicit runtime clear preserves source and data folders after plugin drain");
                    AssertEqual(0,service.List().Items.Count,"runtime clear drops only catalog state; source reconnection is explicit");
                }
            });
        }
        private static void PluginsSourceChatSessions()
        {
            WithPluginPaths(paths => {
                PluginItemDto item; string firstId, secondId;
                using (var service = new PluginApplicationService(paths))
                {
                    item = PluginFixture(service, paths, false);
                    var empty = service.ChatState(item.PluginId);
                    AssertTrue(empty.SessionId == null && empty.Messages.Count == 0 &&
                        service.ProjectChats().Projects.Single(value => value.WorkspaceId == item.SourceWorkspaceId).Chats.Count == 0,
                        "opening source development keeps an empty workspace empty");
                    var first = service.CreateProjectChat(new ProjectChatRequest { WorkspaceId = item.SourceWorkspaceId }); firstId = first.SessionId;
                    AssertEqual(item.SourceWorkspaceId, first.Project.WorkspaceId, "source chat uses the common workspace identity");
                    var workspace = new WorkspaceStore(paths).OpenConnected(item.SourceWorkspaceId);
                    var session = service.Conversations.GetSession(workspace, firstId);
                    var message = new ChatMessage { Role = "user", Content = "Первое обсуждение плагина" };
                    session.Messages.Add(message);
                    new ChatStore(paths).Commit(session, ConversationCommit.UserInput(message));
                    var second = service.CreateProjectChat(new ProjectChatRequest { WorkspaceId = item.SourceWorkspaceId, Title = "Второе обсуждение" });
                    secondId = second.SessionId;
                    AssertTrue(firstId != secondId && second.Messages.Count == 0, "new chat has a distinct identity and empty history");
                    var project = service.ProjectChats().Projects.Single(value => value.WorkspaceId == item.SourceWorkspaceId);
                    AssertEqual(item.PluginId, project.PluginId, "common tree links source chats back to their plugin");
                    AssertEqual(item.Name, project.Name, "common tree labels source history with the plugin name");
                    AssertEqual(2, project.Chats.Count, "both conversations appear in the main tree");
                    AssertEqual(message.Content, service.ProjectChat(new ProjectChatRequest { WorkspaceId = item.SourceWorkspaceId,
                        SessionId = firstId }).Messages.Single().Content, "creating another chat preserves the first history");
                    AssertEqual(message.Id, service.ProjectChatSessionForPresentation(item.SourceWorkspaceId, firstId)
                        .Messages.Single().Id, "tool presentation reads the exact source chat");
                    var other = service.CreateProjectWorkspace(new ProjectConnectionRequest { RootPath = Path.Combine(paths.Root, "other") }).SelectedWorkspaceId;
                    PluginFailure(() => service.ProjectChat(new ProjectChatRequest { WorkspaceId = other, SessionId = secondId }), "project_chat_missing");
                    PluginFailure(() => service.ProjectChatSessionForPresentation(other, firstId), "project_chat_missing");
                    PluginFailure(() => service.SubmitProjectChatAsync(new ProjectChatRequest { WorkspaceId = item.SourceWorkspaceId,
                        SessionId = firstId, Input = new RNAssistant.Runtime.Contracts.WorkspaceSubmitInputRequest { SessionId = secondId, Message = "wrong chat" } },
                        new AppSettings(), () => "", CancellationToken.None).GetAwaiter().GetResult(), "project_chat_invalid");
                }
                using (var reopened = new PluginApplicationService(paths))
                {
                    AssertEqual(2, reopened.ProjectChats().Projects.Single(value => value.WorkspaceId == item.SourceWorkspaceId).Chats.Count,
                        "source chat history survives application restart");
                    reopened.DeleteProjectChat(new ProjectChatRequest { WorkspaceId = item.SourceWorkspaceId, SessionId = secondId });
                    AssertEqual(firstId, reopened.ChatState(item.PluginId).SessionId, "reopening uses an existing chat after another is deleted");
                    PluginFailure(() => reopened.ProjectChat(new ProjectChatRequest { WorkspaceId = item.SourceWorkspaceId, SessionId = secondId }), "project_chat_missing");
                    reopened.DeleteProjectHistory(new ProjectChatRequest { WorkspaceId = item.SourceWorkspaceId });
                    var fresh = reopened.ChatState(item.PluginId);
                    AssertTrue(fresh.SessionId == null && fresh.Messages.Count == 0,
                        "reopening after history deletion does not create a replacement chat");
                    AssertEqual(0, reopened.ProjectChats().Projects.Single(value => value.WorkspaceId == item.SourceWorkspaceId).Chats.Count,
                        "source workspace returns to the tree without a new chat");
                    var afterDelete = reopened.CreateProjectChat(new ProjectChatRequest { WorkspaceId = item.SourceWorkspaceId });
                    AssertTrue(afterDelete.SessionId != firstId && afterDelete.Messages.Count == 0,
                        "explicit creation after history deletion starts a fresh session");
                }
            });
        }

        private static void PluginsProjectChatFork()
        {
            WithPluginPaths(paths => {
                using (var service = new PluginApplicationService(paths))
                {
                    var workspaceId = service.CreateProjectWorkspace(new ProjectConnectionRequest {
                        RootPath = Path.Combine(paths.Root, "fork-project") }).SelectedWorkspaceId;
                    var source = service.CreateProjectChat(new ProjectChatRequest { WorkspaceId = workspaceId });
                    var workspace = new WorkspaceStore(paths).OpenConnected(workspaceId);
                    var session = service.Conversations.GetSession(workspace, source.SessionId);
                    var first = new ChatMessage { Role = "user", Content = "Сохранить первый шаг" };
                    session.Messages.Add(first);
                    var attachmentStore = new AttachmentStore(paths);
                    first.Attachments.Add(attachmentStore.Import("note.txt", "text/plain",
                        System.Text.Encoding.UTF8.GetBytes("Точное вложение"), source.SessionId));
                    attachmentStore.CommitToCas(first);
                    ChatMessageAttachmentService.Link(session, first);
                    var store = new ChatStore(paths);
                    store.Commit(session, ConversationCommit.UserInput(first));
                    var second = new ChatMessage { Role = "user", Content = "Будущий шаг" };
                    session.Messages.Add(second);
                    store.Commit(session, ConversationCommit.UserInput(second));

                    var fork = service.ForkProjectChat(new ProjectChatRequest { WorkspaceId = workspaceId,
                        SessionId = source.SessionId, MessageId = first.Id });
                    AssertTrue(fork.SessionId != source.SessionId && fork.Messages.Count == 1,
                        "fork creates one new session with only the selected prefix");
                    AssertEqual(first.Id, fork.Messages[0].Id, "fork retains exact message identity");
                    var loaded = service.Conversations.GetSession(workspace, fork.SessionId);
                    AssertEqual(source.SessionId, loaded.ParentSessionId, "fork records source identity");
                    AssertEqual(first.Id, loaded.ForkedThroughMessageId, "fork records the selected boundary");
                    AssertEqual(1, loaded.Artifacts.Count, "fork retains only the selected attachment artifact");
                    AssertEqual("Точное вложение", System.Text.Encoding.UTF8.GetString(
                        attachmentStore.ReadBytes(loaded.Messages[0].Attachments[0])),
                        "forked attachment reads the same immutable CAS bytes");
                    AssertEqual(ChatResourceUri.CreateArtifactRevision(loaded, loaded.Artifacts[0]).Uri,
                        loaded.Messages[0].ResourceRefs[0].Uri, "fork rebinds the chat resource to its new scope");
                    AssertEqual(2, service.ProjectChat(new ProjectChatRequest { WorkspaceId = workspaceId,
                        SessionId = source.SessionId }).Messages.Count, "fork does not rewrite source history");
                    AssertTrue(service.ProjectChats().Projects.Single(item => item.WorkspaceId == workspaceId).Chats
                        .Any(item => item.Id == fork.SessionId), "fork appears in the common chat tree");
                    var deleted = service.DeleteProjectChatMessage(new ProjectChatRequest { WorkspaceId = workspaceId,
                        SessionId = fork.SessionId, MessageId = first.Id });
                    AssertEqual(0, deleted.Messages.Count, "deleting in the fork updates its event-backed history");
                    AssertEqual(0, service.Conversations.GetSession(workspace, fork.SessionId).Artifacts.Count,
                        "deleted attachment is no longer a fork artifact head");
                    AssertEqual(2, service.ProjectChat(new ProjectChatRequest { WorkspaceId = workspaceId,
                        SessionId = source.SessionId }).Messages.Count, "deleting in a fork does not touch the source");
                    PluginFailure(() => service.ForkProjectChat(new ProjectChatRequest { WorkspaceId = workspaceId,
                        SessionId = source.SessionId, MessageId = "missing" }), "project_chat_invalid");
                }
            });
        }

        private static void PluginsProjectChatEdit()
        {
            WithPluginPaths(paths => {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
                using (var listener = new HttpListener())
                {
                    listener.Prefixes.Add("http://127.0.0.1:" + port + "/"); listener.Start();
                    var server = Task.Run(async () => {
                        var context = await listener.GetContextAsync().ConfigureAwait(false);
                        var response = new JObject { ["choices"] = new JArray(new JObject {
                            ["message"] = new JObject { ["content"] =
                                ModelProtocolWire.WriteAcceptedDecision(ConversationResponse.FinishAction,
                                    "The edit is complete.", "Ответ на исправленный запрос.",
                                    results: new ConversationResultClaim[0], outcome: ConversationOutcome.Completed) } }) }.ToString(Formatting.None);
                        var bytes = Encoding.UTF8.GetBytes(response);
                        context.Response.ContentType = "application/json";
                        context.Response.ContentLength64 = bytes.Length;
                        await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                        context.Response.Close();
                    });
                    try
                    {
                        using (var service = new PluginApplicationService(paths))
                        {
                            var workspaceId = service.CreateProjectWorkspace(new ProjectConnectionRequest {
                                RootPath = Path.Combine(paths.Root, "edit-project") }).SelectedWorkspaceId;
                            var created = service.CreateProjectChat(new ProjectChatRequest { WorkspaceId = workspaceId });
                            var workspace = new WorkspaceStore(paths).OpenConnected(workspaceId);
                            var session = service.Conversations.GetSession(workspace, created.SessionId);
                            var first = new ChatMessage { Role = "user", Content = "Исходный запрос" };
                            var attachments = new AttachmentStore(paths);
                            first.Attachments.Add(attachments.Import("evidence.txt", "text/plain",
                                Encoding.UTF8.GetBytes("Сохранить файл"), created.SessionId));
                            attachments.CommitToCas(first);
                            session.Messages.Add(first); ChatMessageAttachmentService.Link(session, first);
                            var store = new ChatStore(paths); store.Commit(session, ConversationCommit.UserInput(first));
                            var later = new ChatMessage { Role = "user", Content = "Будущий запрос" };
                            session.Messages.Add(later); store.Commit(session, ConversationCommit.UserInput(later));
                            var revision = session.Revision;
                            var settings = new AppSettings { BaseUrl = "http://127.0.0.1:" + port,
                                Model = "test-model", StreamResponses = false, SmartChatTitles = false,
                                AutoCompressContext = false };
                            service.SubmitProjectChatAsync(new ProjectChatRequest { WorkspaceId = workspaceId,
                                SessionId = created.SessionId, Input = new WorkspaceSubmitInputRequest {
                                    SessionId = created.SessionId, RequestId = "edit-request", EditMessageId = first.Id,
                                    ExpectedRevision = revision, Message = "Исправленный запрос", Mode = ChatModes.Agent }
                            }, settings, () => "test-key", CancellationToken.None).GetAwaiter().GetResult();
                            service.Conversations.RunsDrained.GetAwaiter().GetResult();
                            var after = service.Conversations.GetSession(workspace, created.SessionId);
                            var users = after.Messages.Where(message => message.Role == "user" && !message.ProtocolMessage).ToArray();
                            AssertEqual(1, users.Length, "edit replaces the selected user message and future history");
                            AssertEqual("Исправленный запрос", users[0].Content, "edit sends replacement text");
                            AssertTrue(users[0].Id != first.Id && after.Messages.All(message => message.Id != later.Id),
                                "edit uses a new accepted identity and removes the old tail");
                            AssertEqual("Сохранить файл", Encoding.UTF8.GetString(
                                attachments.ReadBytes(users[0].Attachments[0])), "edit preserves immutable attachment bytes");
                            AssertEqual(users[0].Id, after.Artifacts.Single(item => item.Id ==
                                "attachment_" + users[0].Attachments[0].Id).SourceMessageId,
                                "edited attachment remains bound to the new message");
                        }
                        server.GetAwaiter().GetResult();
                    }
                    finally { listener.Stop(); }
                }
            });
        }

        private static void PluginsProjectChatHistoryPages()
        {
            WithPluginPaths(paths => {
                using (var service = new PluginApplicationService(paths))
                {
                    var workspaceId = service.CreateProjectWorkspace(new ProjectConnectionRequest {
                        RootPath = Path.Combine(paths.Root, "paged-project") }).SelectedWorkspaceId;
                    var created = service.CreateProjectChat(new ProjectChatRequest { WorkspaceId = workspaceId });
                    var workspace = new WorkspaceStore(paths).OpenConnected(workspaceId);
                    var session = service.Conversations.GetSession(workspace, created.SessionId);
                    var messages = Enumerable.Range(0, 170).Select(index => new ChatMessage {
                        Role = "user", Content = "Message " + index }).ToList();
                    messages.Insert(30, new ChatMessage { Role = "tool", ProtocolMessage = true,
                        Content = "private protocol body" });
                    session.Messages.AddRange(messages);
                    new ChatStore(paths).Commit(session, new ConversationCommit(messages));

                    var latest = service.ProjectChat(new ProjectChatRequest { WorkspaceId = workspaceId,
                        SessionId = created.SessionId });
                    AssertEqual(171, latest.MessageTotalCount, "workspace history counts durable message indexes");
                    AssertEqual(91, latest.MessageStartIndex, "workspace opens the shared 80-message window");
                    AssertEqual("Message 169", latest.Messages.Last().Content, "latest answer stays in the first page");
                    var middle = service.ProjectChatMessages(new ProjectChatHistoryRequest {
                        WorkspaceId = workspaceId, SessionId = created.SessionId,
                        BeforeIndex = latest.MessageStartIndex });
                    AssertEqual(11, middle.StartIndex, "previous page follows raw indexes");
                    AssertTrue(middle.Messages.Single(item => item.Role == "tool").ProtocolMessage &&
                        middle.Messages.Single(item => item.Role == "tool").Content == null,
                        "paged protocol body remains hidden without shifting indexes");
                    var oldest = service.ProjectChatMessages(new ProjectChatHistoryRequest {
                        WorkspaceId = workspaceId, SessionId = created.SessionId,
                        BeforeIndex = middle.StartIndex });
                    AssertEqual(0, oldest.StartIndex, "oldest page remains reachable");
                    AssertEqual("Message 0", oldest.Messages.First().Content, "oldest user message survives paging");
                    PluginFailure(() => service.ProjectChatMessages(new ProjectChatHistoryRequest {
                        WorkspaceId = workspaceId, SessionId = created.SessionId, BeforeIndex = 172 }), "project_chat_invalid");
                    var other = service.CreateProjectWorkspace(new ProjectConnectionRequest {
                        RootPath = Path.Combine(paths.Root, "foreign-project") }).SelectedWorkspaceId;
                    PluginFailure(() => service.ProjectChatMessages(new ProjectChatHistoryRequest {
                        WorkspaceId = other, SessionId = created.SessionId, BeforeIndex = 91 }), "project_chat_missing");
                }
            });
        }

        private static void PluginsProjectChatRecoversAbandonedRun()
        {
            foreach (var possibleEffect in new[] { false, true })
            WithPluginPaths(paths =>
            {
                using (var service = new PluginApplicationService(paths))
                using (var bridge = new AssistantWebBridge(new AssistantController(paths), ignored => { }) { Plugins = service })
                {
                    var root = Path.Combine(paths.Root, "recovery-project");
                    var workspaceId = service.CreateProjectWorkspace(new ProjectConnectionRequest { RootPath = root }).SelectedWorkspaceId;
                    var chat = service.CreateProjectChat(new ProjectChatRequest { WorkspaceId = workspaceId });
                    var request = new ProjectChatRequest { WorkspaceId = workspaceId, SessionId = chat.SessionId };
                    var workspace = new WorkspaceStore(paths).OpenConnected(workspaceId);
                    var session = service.Conversations.GetSession(workspace, chat.SessionId);
                    var runId = Guid.NewGuid().ToString("N");
                    var call = new ToolCall("unfinished-write", "files.replace_text", "{\"relativePath\":\"keep.txt\"}");
                    var policy = new ToolPolicySnapshot(call.Name, "fixture", true, true);
                    var user = new ChatMessage { Role = "user", RunId = runId, Content = "Change the file" };
                    session.Messages.Add(user);
                    session.LastRun = new ChatRunRecord { RunId = runId, TurnId = runId, Status = "running",
                        StartedUtc = DateTime.UtcNow, ResponseProtocolVersion = AgentResponseProtocol.CurrentVersion,
                        KernelState = new AgentRunState(new RunSummary(runId, runId, RunLifecycle.Running,
                            new ToolCounts(readOk: 1), 2, 1, "", null, null), new AgentRunLimits(10, 10),
                            possibleEffect ? new ToolExecutionContext(call, policy, runId, runId,
                                "unfinished-step", DateTime.UtcNow, true, 9) : null) };
                    var store = new ChatStore(paths);
                    store.Commit(session, ConversationCommit.UserInput(user));
                    File.WriteAllText(Path.Combine(root, "keep.txt"), "unchanged");
                    var revision = session.Revision;
                    var leaseDirectory = Path.Combine(paths.WorkspaceDirectory, "run-leases");
                    Directory.CreateDirectory(leaseDirectory);
                    var leasePath = Path.Combine(leaseDirectory, TextPatternEngine.Sha256(session.Id) + ".lck");
                    JObject owned;
                    using (new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    {
                        var timer = System.Diagnostics.Stopwatch.StartNew();
                        owned = WorkspaceBridgeCall(bridge, BridgeToken(bridge), "getProjectChat", request);
                        AssertTrue(timer.ElapsedMilliseconds < 3000, "chat read does not wait for the ten-second run lease retry");
                        AssertTrue((bool)owned["ok"], "an occupied lease does not fail chat loading");
                        var running = owned["payload"]["state"].ToObject<WorkspaceSessionSnapshot>();
                        AssertEqual("running", running.Status, "missing IPC never interrupts a leased run");
                        AssertTrue(running.ModelProgressUnavailable, "owner preview is intentionally unavailable");
                        AssertEqual(revision, running.Revision, "owned history is not changed");
                    }
                    AssertEqual("running", service.Conversations.ReadState(workspace, session.Id).Status,
                        "plain inspection stays read-only; recovery belongs to chat activation");
                    var envelope = WorkspaceBridgeCall(bridge, BridgeToken(bridge), "getProjectChat", request);
                    AssertTrue((bool)envelope["ok"], "recovery crosses the actual project-chat bridge");
                    var recovered = envelope["payload"].ToObject<ProjectChatStateDto>();
                    AssertEqual("interrupted", recovered.State.Status, "opening the chat recovers a run without an owner");
                    AssertTrue(!recovered.State.ModelProgressUnavailable, "stopped run no longer polls an absent owner");
                    AssertEqual(possibleEffect, recovered.State.InterruptedEffectPossible, "possible effect remains explicit");
                    var durable = service.Conversations.GetSession(workspace, session.Id);
                    AssertEqual(RunLifecycle.Failed, durable.LastRun.KernelState.Summary.Lifecycle, "kernel records interrupted outcome");
                    AssertEqual("runtime_interrupted", durable.LastRun.KernelState.Summary.Reason, "durable reason");
                    AssertEqual(possibleEffect ? 1 : 0, durable.LastRun.KernelState.Summary.ToolCounts.WriteUnknown, "unconfirmed write is unknown");
                    AssertEqual(1, durable.LastRun.KernelState.Summary.ToolCounts.ReadOk, "completed reads survive recovery");
                    AssertEqual("unchanged", File.ReadAllText(Path.Combine(root, "keep.txt")), "recovery never replays the write");
                    AssertEqual(user.Id, recovered.Messages.First().Id, "accepted user message is retained");
                    AssertEqual(recovered.State.Revision, service.ProjectChat(request).State.Revision, "repeated reads do not append another interruption");
                    var fork = service.ForkProjectChat(new ProjectChatRequest { WorkspaceId = workspaceId,
                        SessionId = session.Id, MessageId = user.Id });
                    AssertEqual(user.Id, fork.Messages.Single().Id, "recovered chat permits fork at the exact prefix");
                    if (!possibleEffect)
                    {
                        var fixture = Environment.GetEnvironmentVariable("RNA_WORKSPACE_RECOVERY_FIXTURE");
                        if (!string.IsNullOrWhiteSpace(fixture))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(fixture)));
                            File.WriteAllText(fixture, new JObject { ["owned"] = owned, ["recovered"] = envelope }.ToString(Formatting.Indented));
                        }
                    }
                    durable.LastRun.Status = "awaitingconfirmation";
                    durable.LastRun.KernelState = new AgentRunState(new RunSummary(runId, runId, RunLifecycle.AwaitingConfirmation,
                        new ToolCounts(), 1, 0, "Confirm the edit", null,
                        new PendingConfirmation("pending-recovery", call, policy, "pending-step", 1)), new AgentRunLimits(10, 10));
                    store.Commit(durable, new ConversationCommit());
                    var pending = service.ProjectChat(request);
                    AssertEqual("pending-recovery", pending.PendingId, "a pending approval keeps its explicit resolution route");
                    AssertEqual(durable.Revision, pending.State.Revision, "pending approval is not an abandoned run");
                }
            });
        }

        private static void PluginsProjectChatBusyLeaseIsTyped()
        {
            WithPluginPaths(paths =>
            {
                using (var service = new PluginApplicationService(paths))
                {
                    var item = PluginFixture(service, paths, false);
                    var chat = service.CreateProjectChat(new ProjectChatRequest { WorkspaceId = item.SourceWorkspaceId });
                    var leaseDirectory = Path.Combine(paths.WorkspaceDirectory, "run-leases");
                    Directory.CreateDirectory(leaseDirectory);
                    var leasePath = Path.Combine(leaseDirectory,
                        TextPatternEngine.Sha256(chat.SessionId) + ".lck");
                    using (new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    {
                        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                            PluginFailure(() => service.SubmitProjectChatAsync(new ProjectChatRequest {
                                WorkspaceId = item.SourceWorkspaceId, SessionId = chat.SessionId,
                                Input = new RNAssistant.Runtime.Contracts.WorkspaceSubmitInputRequest {
                                    SessionId = chat.SessionId, RequestId = Guid.NewGuid().ToString("N"),
                                    Message = "Check the project" } },
                                new AppSettings { BaseUrl = "http://localhost", Model = "test" },
                                () => string.Empty, deadline.Token).GetAwaiter().GetResult(),
                                "project_chat_busy");
                    }
                    var workspace = new WorkspaceStore(paths).OpenConnected(item.SourceWorkspaceId);
                    AssertEqual(0, service.Conversations.GetSession(workspace, chat.SessionId).Messages.Count,
                        "busy submission did not append the user input");
                    AssertTrue(service.Conversations.PrepareResume(workspace, chat.SessionId) != null,
                        "chat lease is usable after the other owner releases it");
                }
            });
        }

        private static void PluginsPanelVisibility()
        {
            WithPluginPaths(paths => {
                PluginItemDto item;
                PluginInstance open;
                string sourceChat;
                using (var service = new PluginApplicationService(paths))
                {
                    item = PluginFixture(service, paths, false);
                    open = PluginOpen(service, item.PluginId);
                    sourceChat = service.CreateProjectChat(new ProjectChatRequest { WorkspaceId = item.SourceWorkspaceId }).SessionId;
                    var sourceManifest = File.ReadAllText(Path.Combine(item.SourceRoot, "plugin.json"));
                    var icon = service.SaveIcon(new PluginIconSaveRequest { Name = "Outline", Svg =
                        "<svg viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\"><circle cx=\"12\" cy=\"12\" r=\"8\"/></svg>" });
                    var edited = service.UpdateAppearance(new PluginAppearanceRequest { PluginId = item.PluginId, Name = "Мой плагин", IconId = icon.Id, IconColor = "#E04A35" });
                    AssertEqual("Мой плагин", edited.Name, "appearance changes the panel name");
                    AssertEqual(icon.Svg, edited.IconSvg, "appearance copies the validated icon");
                    AssertEqual(item.ReleaseId, edited.ReleaseId, "appearance does not publish draft code");
                    AssertEqual(sourceManifest, File.ReadAllText(Path.Combine(item.SourceRoot, "plugin.json")), "appearance preserves source manifest");
                    AssertTrue(!open.Token.IsCancellationRequested, "appearance does not revoke running plugin instances");
                    service.DeleteIcon(new PluginIconDeleteRequest { Id = icon.Id });
                    AssertEqual(icon.Svg, service.List().Items.Single().IconSvg, "appearance owns its icon after library deletion");
                    PluginFailure(() => service.UpdateAppearance(new PluginAppearanceRequest { PluginId = item.PluginId, Name = " " }), "plugin_name_invalid");
                    service.RemoveAsync(item.PluginId).GetAwaiter().GetResult();
                    AssertTrue(open.Token.IsCancellationRequested, "removal revokes visible instance");
                    AssertEqual(0, service.List().Items.Count, "removed plugin disappears from panel");
                    AssertEqual(item.PluginId, service.ListRemoved().Items.Single().PluginId, "removed plugin remains selectable for restoration");
                    PluginFailure(() => service.Open(new PluginOpenRequest { PluginId = item.PluginId }), "plugin_missing");
                    AssertTrue(File.Exists(Path.Combine(item.SourceRoot, "plugin.json")), "removal keeps source");
                }
                using (var service = new PluginApplicationService(paths))
                {
                    AssertEqual(item.PluginId, service.ListRemoved().Items.Single().PluginId, "panel removal survives restart");
                    var restored = service.Restore(item.PluginId);
                    AssertEqual("Мой плагин", restored.Name, "edited name survives restart and restoration");
                    AssertTrue(restored.IconSvg.Contains("circle"), "edited icon survives restart and restoration");
                    AssertEqual("#e04a35", restored.IconColor, "chosen icon color survives restart and restoration");
                    AssertEqual(item.ReleaseId, restored.ReleaseId, "restoration keeps published version");
                    AssertEqual(true, restored.Enabled, "restoration keeps activation state");
                    AssertEqual(item.PluginId, service.List().Items.Single().PluginId, "restored plugin returns to panel");
                    AssertEqual(sourceChat, service.ChatState(item.PluginId).SessionId, "restoration keeps source chat");
                    var original = service.UpdateAppearance(new PluginAppearanceRequest { PluginId = item.PluginId, Name = restored.Name, IconColor = "original" });
                    AssertEqual("original", service.List().Items.Single().IconColor, "original palette mode persists");
                    var themed = service.UpdateAppearance(new PluginAppearanceRequest { PluginId = item.PluginId, Name = restored.Name });
                    AssertTrue(themed.IconColor == null && themed.IconSvg == original.IconSvg, "default theme color preserves the original artwork");
                    PluginOpen(service, item.PluginId);
                }
            });
        }
        private static void PluginsSvgIcon()
        {
            var rich = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""128"" height=""64"" viewBox=""-10,0,128,64""><title>Цветная иконка</title>" +
                @"<defs><linearGradient id=""paint""><stop stop-color=""#ef4433""/><stop offset=""1"" stop-color=""rgb(10, 90, 220)"" stop-opacity="".45""/></linearGradient>" +
                @"<clipPath id=""clip""><rect width=""128"" height=""64""/></clipPath></defs><style>.mark{fill:url(#paint);stroke:navy;stroke-width:5}</style>" +
                @"<g transform=""translate(4 2) scale(.9)"" clip-path=""url(#clip)""><path class=""mark"" d=""M1e1 2 L90 2 90 60Z""/>" +
                string.Concat(Enumerable.Range(0, 40).Select(index => "<circle cx=\"" + index + "\" cy=\"32\" r=\"1\" fill=\"orange\"/>")) + "</g></svg>";
            rich = rich.Replace("</svg>", new string(' ', 5000) + "</svg>");
            var normalized = PluginIconSvg.Normalize(PluginPackageFiles.Utf8.GetBytes(rich));
            AssertTrue(normalized.Contains("viewBox=\"-10 0 128 64\"") && normalized.Contains("width=\"24\"") && normalized.Contains("height=\"24\""),
                "a large non-square SVG is sized without rewriting its coordinate system");
            AssertTrue(normalized.Contains("translate(4 2) scale(.9)") && normalized.Contains("#ef4433") && normalized.Contains("rgb(10, 90, 220)") &&
                normalized.Contains("fill:url(#paint)") && normalized.Contains("stop-opacity=\".45\"") && normalized.Contains("M1e1"), "transforms, CSS, gradients, transparency and scientific path notation survive");
            AssertEqual(normalized, PluginIconSvg.Normalize(PluginPackageFiles.Utf8.GetBytes(normalized)), "normalization is stable across library/source reads");
            AssertTrue(normalized.Contains("width:24px!important;height:24px!important"), "root stylesheet dimensions cannot override icon sizing");
            AssertTrue(PluginIconSvg.Normalize(PluginPackageFiles.Utf8.GetBytes("<svg width=\"64px\" height=\"32px\"><rect width=\"64\" height=\"32\" fill=\"red\"/></svg>"))
                .Contains("viewBox=\"0 0 64 32\""), "dimensions supply a missing viewBox");
            foreach (var unsafeSvg in new[] { rich.Replace("fill:url(#paint)", "fill:url(https://invalid)"),
                rich.Replace("<style>", "<style>@import 'https://invalid';"), rich.Replace("<g transform", "<g onmouseover=\"alert(1)\" transform"),
                "<!DOCTYPE svg [<!ENTITY x SYSTEM 'file:///tmp/secret'>]><svg><text>&x;</text></svg>" })
                PluginFailure(() => PluginIconSvg.Normalize(PluginPackageFiles.Utf8.GetBytes(unsafeSvg)), "plugin_icon_invalid");
            WithPluginPaths(paths => {
                Directory.CreateDirectory(paths.PluginIconDirectory);
                PluginFailure(() => new PluginIconLibrary(paths).Save("Oversized",
                    PluginPackageFiles.Utf8.GetBytes("<svg><title>" + new string('a', PluginIconSvg.MaximumBytes - 40) + "</title></svg>")), "plugin_icon_invalid");
                AssertEqual(0, Directory.GetFiles(paths.PluginIconDirectory).Length, "normalization size overflow cannot leave an unreadable library record");
                var oldPaths = new[] { "M3 3h18v18H3Z" };
                var oldId = "icon-" + PluginPackageFiles.Hash(PluginPackageFiles.Utf8.GetBytes(string.Join("\n", oldPaths)));
                var file = Path.Combine(paths.PluginIconDirectory, oldId + ".json");
                var oldRecord = JsonConvert.SerializeObject(new { id = oldId, name = "Existing", paths = oldPaths });
                File.WriteAllText(file, oldRecord);
                var migrated = new PluginIconLibrary(paths).Get(oldId);
                AssertEqual(oldId, migrated.Id, "one-way migration preserves saved icon identity");
                AssertTrue(migrated.Svg.Contains("stroke=\"currentColor\"") && File.ReadAllText(file) == oldRecord,
                    "old outline files retain appearance without a write during reads");
                AssertEqual(migrated.Svg, new PluginIconLibrary(paths).Get(oldId).Svg, "migrated icon survives reopening");
            });
            WithPluginPaths(paths => {
                using (var service = new PluginApplicationService(paths))
                {
                    var standard = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"24\" height=\"24\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\"><g><rect x=\"3\" y=\"3\" width=\"18\" height=\"18\" rx=\"2\"/><circle cx=\"9\" cy=\"9\" r=\"2\"/><path d=\"M4 18l5-5 4 4 3-3 4 4\"/></g></svg>";
                    var settings = new AppSettings { MaxTokens = 1000 };
                    var calls = 0;
                    var result = service.DrawIconAsync(new PluginIconDrawRequest { Name = "Photo", Description = "Иконка фотографии", IconColor = "original" }, settings,
                        (configuration, messages, options, progress, token) => {
                            calls++; AssertEqual(8192, configuration.MaxTokens, "icon completion has room for provider reasoning and SVG");
                            AssertEqual(LlmResponseFormats.Text, options.ResponseFormat, "model draws ordinary SVG");
                            AssertTrue(!messages.Any(message => message.Content.Contains("Primary color: original")), "palette mode is not sent as a literal paint value");
                            return Task.FromResult(new LlmCompletionResult { Content = "```svg\n" + standard + "\n```" });
                        }, CancellationToken.None).GetAwaiter().GetResult();
                    AssertEqual(1, calls, "standard outline SVG is accepted on the first attempt");
                    AssertTrue(result.Svg.Contains("<rect") && result.Svg.Contains("<circle") && result.Svg.Contains("stroke-width=\"2\""), "original SVG geometry and paint are preserved");
                    AssertEqual(1000, settings.MaxTokens, "drawing leaves user settings unchanged");
                    foreach (var unsafeSvg in new[] { standard.Replace("<g>", "<g onload=\"x()\">"),
                        standard.Replace("<g>", "<g><script>alert(1)</script>"), standard.Replace("<g>", "<g style=\"fill:url(https://invalid)\">"),
                        standard.Replace("<g>", "<g><use href=\"https://invalid\"/>"), standard.Replace("<g>", "<g><foreignObject/></g><g>") })
                        PluginFailure(() => PluginPackageFiles.ReadIconSvg(PluginPackageFiles.Utf8.GetBytes(unsafeSvg)), "plugin_icon_invalid");
                    calls = 0;
                    service.DrawIconAsync(new PluginIconDrawRequest { Name = "Retry", Description = "Исправить рисунок" }, settings,
                        (configuration, messages, options, progress, token) => {
                            calls++;
                            if (calls == 2) AssertTrue(messages.Any(message => message.Role == "assistant" && message.Content.Contains("<svg>unfinished")),
                                "repair sees the rejected drawing, not just a generic error");
                            return Task.FromResult(new LlmCompletionResult { Content = calls == 1 ?
                                "<svg>unfinished" : standard });
                        }, CancellationToken.None).GetAwaiter().GetResult();
                    AssertEqual(2, calls, "one bounded repair attempt");
                    AssertEqual(1, service.ListIcons().Items.Count, "invalid drawing is never saved and identical repaired geometry is deduplicated");
                }
            });
            var authoring = RNAssistant.Core.Services.WorkspaceSkillProvider.GetSkills()
                .Single(skill => skill.Id == RNAssistant.Core.Services.WorkspaceSkillProvider.PluginId);
            AssertTrue(authoring.BodyMarkdown.Contains("icon.svg") && authoring.BodyMarkdown.Contains("plugins.check"),
                "source workspace guidance owns text-authored SVG and package checking");
            WithPluginPaths(paths=>{
                using(var service=new PluginApplicationService(paths))
                {
                    var defaultParent=Path.Combine(paths.Root,"plugin-workspaces");
                    AssertEqual(defaultParent,service.List().DefaultSourceParent,"UI learns the actual default source location");
                    var automatic=service.Create(new PluginCreateRequest { Name="Default source" });
                    AssertEqual(defaultParent,Path.GetDirectoryName(automatic.SourceRoot),"creation chooses the managed plugin folder");
                    AssertTrue(Directory.Exists(automatic.SourceRoot),"default source folder is created");
                    PluginFailure(()=>service.Create(new PluginCreateRequest { Name="Missing custom path",RootPath=" " }),"plugin_create_invalid");
                    PluginFailure(()=>service.Create(new PluginCreateRequest { Name="Relative custom path",RootPath="relative-plugin" }),"plugin_create_invalid");
                    var emptyRoot=Path.Combine(paths.Root,"prepared-empty");Directory.CreateDirectory(emptyRoot);
                    AssertEqual(emptyRoot,service.Create(new PluginCreateRequest { Name="Empty source",RootPath=emptyRoot }).SourceRoot,
                        "creation accepts a pre-created empty folder");
                    var occupied=Path.Combine(paths.Root,"occupied");Directory.CreateDirectory(occupied);
                    File.WriteAllText(Path.Combine(occupied,"keep.txt"),"user data");
                    PluginFailure(()=>service.Create(new PluginCreateRequest { Name="Occupied",RootPath=occupied }),"target_exists");
                    AssertEqual("user data",File.ReadAllText(Path.Combine(occupied,"keep.txt")),"occupied source is never overwritten");
                    var wrapped=PluginApplicationService.ExtractModelSvg("Here is your icon:\n```svg\n<svg viewBox=\"0 0 24 24\"><path d=\"M3 3h18v18H3z\"/></svg>\n```");
                    AssertTrue(PluginPackageFiles.ReadIconSvg(PluginPackageFiles.Utf8.GetBytes(wrapped)).Contains("<path"),
                        "a fenced model reply yields only the validated SVG document");
                    var item=service.Create(new PluginCreateRequest { Name="Sales", RootPath=Path.Combine(paths.Root,"svg-source"),
                        Description="Summarize sales", IconDescription="Three rising columns" });
                    AssertTrue(File.ReadAllText(Path.Combine(item.SourceRoot,"README.md")).Contains("Three rising columns"),
                        "the text brief stays in source guidance");
                    var manifestPath=Path.Combine(item.SourceRoot,"plugin.json");
                    var manifest=JObject.Parse(File.ReadAllText(manifestPath));
                    manifest["icon"]="icon.svg";((JArray)manifest["files"]).Add("icon.svg");
                    File.WriteAllText(manifestPath,manifest.ToString());
                    var svgPath=Path.Combine(item.SourceRoot,"icon.svg");
                    File.WriteAllText(svgPath,"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><path d=\"M4 18V6h3v12M10 18v-7h3v7M16 18V4h3v14\"/></svg>");
                    var candidate=service.Check(item.PluginId);
                    var published=service.Apply(new PluginApplyRequest { PluginId=item.PluginId, CandidateId=candidate.CandidateId });
                    AssertEqual("icon.svg",published.Icon,"SVG source becomes the published icon");
                    AssertTrue(published.IconSvg.Contains("<path"),"published icon exposes normalized static SVG");
                    var catalog=service.ListIcons().Items;
                    AssertEqual(1,catalog.Count,"checking a valid icon adds it to the saved library");
                    AssertEqual(published.IconSvg,catalog[0].Svg,"library retains the validated drawing");
                    var reused=service.Create(new PluginCreateRequest { Name="Reused", RootPath=Path.Combine(paths.Root,"reused-source"), IconId=catalog[0].Id, IconColor="#3F74B6" });
                    AssertEqual("icon.svg",reused.Icon,"saved icon is copied into new plugin source");
                    AssertEqual("#3f74b6",reused.IconColor,"creation persists the chosen color");
                    AssertEqual(catalog[0].Svg,reused.IconSvg,"new source owns its copy of the drawing");
                    PluginFailure(()=>service.DeleteIcon(new PluginIconDeleteRequest { Id="dataTemplates" }),"plugin_icon_protected");
                    service.DeleteIcon(new PluginIconDeleteRequest { Id=catalog[0].Id });
                    AssertEqual(0,service.ListIcons().Items.Count,"saved drawing is removable");
                    AssertEqual(catalog[0].Svg,service.List().Items.Single(value=>value.PluginId==reused.PluginId).IconSvg,
                        "deleting a saved drawing leaves plugin source intact");
                    File.WriteAllText(svgPath,"<svg viewBox=\"0 0 24 24\" onload=\"alert(1)\"><path d=\"M4 4L20 20\"/></svg>");
                    PluginFailure(()=>service.Check(item.PluginId),"plugin_icon_invalid");
                    AssertEqual(published.IconSvg,service.List().Items.Single(value=>value.PluginId==item.PluginId).IconSvg,
                        "invalid draft cannot replace the active icon");
                    PluginFailure(()=>service.SaveIcon(new PluginIconSaveRequest { Name="Unsafe", Svg="<svg viewBox=\"0 0 24 24\" onload=\"alert(1)\"><path d=\"M4 4L20 20\"/></svg>" }),"plugin_icon_invalid");
                    var manual=service.SaveIcon(new PluginIconSaveRequest { Name="Manual", Svg="<svg viewBox=\"0 0 24 24\"><path d=\"M3 3h18v18H3Z\"/></svg>" });
                    paths.ClearRuntimeData();
                    AssertEqual(manual.Id,new PluginIconLibrary(paths).List().Single().Id,
                        "saved icons survive explicit runtime-data clearing");
                }
            });
        }
        private static void PluginsCommandsPinAndCancel()
        {
            WithPluginPaths(paths=>PluginCommandsPinAndCancelAsync(paths).GetAwaiter().GetResult());
        }
        private static async Task PluginCommandsPinAndCancelAsync(AppDataPaths paths)
        {
            using(var service=new PluginApplicationService(paths))
            {
                var item=PluginFixture(service,paths,false);var context=PluginOpen(service,item.PluginId);
                var started=new TaskCompletionSource<PluginInstance>();var finish=new TaskCompletionSource<string>();
                service.Executor=new PluginExecutor{Execute=(operation,token)=>{started.TrySetResult(operation);token.Register(()=>finish.TrySetCanceled());return finish.Task;}};
                var invocation=service.InvokeAsync(context,"echo","{\"text\":\"a\"}",CancellationToken.None);var running=await started.Task;
                File.WriteAllText(Path.Combine(item.SourceRoot,"commands.js"),"new code");var candidate=service.Check(item.PluginId);
                service.Apply(new PluginApplyRequest{PluginId=item.PluginId,CandidateId=candidate.CandidateId});
                AssertEqual(context.Release.ReleaseId,running.Release.ReleaseId,"invocation pins admitted publication");
                await service.CloseInstanceAsync(context.Id);
                AssertTrue(!running.Token.IsCancellationRequested,"closing the UI leaves its command alive");
                finish.SetResult("{\"text\":\"old release completed\"}");AssertTrue((await invocation).Json.Contains("old release"),"old operation finishes across update");
                try{await service.InvokeAsync(context,"echo","{\"text\":\"a\"}",CancellationToken.None);throw new Exception("Expected revoked refusal");}catch(OperationCanceledException){}
                context=PluginOpen(service,item.PluginId);started=new TaskCompletionSource<PluginInstance>();finish=new TaskCompletionSource<string>();
                invocation=service.InvokeAsync(context,"echo","{\"text\":\"b\"}",CancellationToken.None);await started.Task;
                await service.DisableAsync(item.PluginId);try{await invocation;throw new Exception("Expected cancellation");}catch(OperationCanceledException){}
                var source=new WorkspaceStore(paths).Open(item.SourceRoot,false,true);
                var tools=service.Conversations.AvailableToolIds(source);
                AssertTrue(tools.Contains("plugins.check") && tools.Contains("plugins.apply"),"development agent shares publication services");
            }
        }
        private static void PluginsVendorResolutionAndAdmission()
        {
            WithPluginPaths(paths=>{
                var folder=Path.Combine(paths.Root,"vendor-input");Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder,"module.js"),"export default 1;");File.WriteAllText(Path.Combine(folder,"LICENSE"),"MIT");
                var manifest=new VendorManifestV1{FormatVersion=1,Id="test-module",Version="1.0.0",Description="Test module",Source="local",License="MIT",
                    Licenses=new[]{"LICENSE"},Files=new[]{new VendorFileDefinition{Path="module.js",Sha256=PluginPackageFiles.Hash(File.ReadAllBytes(Path.Combine(folder,"module.js")))},new VendorFileDefinition{Path="LICENSE",Sha256=PluginPackageFiles.Hash(File.ReadAllBytes(Path.Combine(folder,"LICENSE")))}},
                    EntryPoints=new[]{new VendorEntryPoint{Path="module.js",Kind=VendorEntryKind.Module}}};
                File.WriteAllText(Path.Combine(folder,"vendor.json"),JsonConvert.SerializeObject(manifest));
                var vendors=new VendorCatalog(paths,null);vendors.Import(folder);var resolved=vendors.Resolve(manifest.Id,manifest.Version);
                VendorAdmission.RequirePlugin(resolved.Manifest);VendorAdmission.RequireExport(resolved.Manifest);
                PluginFailure(()=>VendorAdmission.RequireHtml(resolved.Manifest),"html_vendor_denied");
                var installed=Path.Combine(paths.Root,"vendors",manifest.Id,manifest.Version);
                File.WriteAllText(Path.Combine(installed,"module.js"),"export default 2;");PluginFailure(()=>vendors.Resolve(manifest.Id,manifest.Version),"vendor_changed");
                manifest.Files[0].Sha256=PluginPackageFiles.Hash(File.ReadAllBytes(Path.Combine(installed,"module.js")));File.WriteAllText(Path.Combine(installed,"vendor.json"),JsonConvert.SerializeObject(manifest));
                PluginFailure(()=>vendors.Resolve(manifest.Id,manifest.Version),"vendor_version_changed");
                manifest.Licenses=new string[0];PluginFailure(()=>VendorAdmission.RequireExport(manifest),"vendor_invalid");
            });
        }
        private static void PluginsBinaryUnknownAndRecovery()
        {
            WithPluginPaths(paths=>{
                var workspace=new WorkspaceStore(paths).Open(Path.Combine(paths.Root,"data"));var files=new WorkspaceFileService(paths);
                var staged=Path.Combine(paths.Root,"staged");var bytes=new byte[2*1024*1024];bytes[0]=255;File.WriteAllBytes(staged,bytes);
                files.FaultPoint=point=>{if(point=="after-dispatch")throw new IOException("lost receipt");};
                try{files.CommitStagedFile(workspace,"out.bin",staged,null,bytes.Length);throw new Exception("Expected unknown");}catch(WorkspaceFileException e){AssertEqual("effect_unknown",e.Code,"uncertain write");}
                files=new WorkspaceFileService(paths);
                try{files.CommitStagedFile(workspace,"out.bin",staged,null,bytes.Length);throw new Exception("Expected unresolved");}catch(WorkspaceFileException e){AssertEqual("unresolved_previous_effect",e.Code,"no automatic retry");}
                var recovery=files.ReconcileUncertain(workspace,"out.bin");AssertEqual(WorkspaceRecoveryOutcome.UnknownAfterDispatch,recovery.Outcome,"binary inspection preserves uncertain causality");
                var snapshot=files.CaptureFile(workspace,"out.bin",bytes.Length);AssertEqual(PluginPackageFiles.Hash(bytes),snapshot.ContentSha256,"full binary read-back");
                files.FaultPoint=point=>{if(point=="after-dispatch")throw new IOException("lost directory receipt");};
                try{files.CreateDirectory(workspace,"results");throw new Exception("Expected directory unknown");}catch(WorkspaceFileException e){AssertEqual("effect_unknown",e.Code,"directory mutation is journaled");}
                files.FaultPoint=null;
                AssertEqual(WorkspaceRecoveryOutcome.UnknownAfterDispatch,files.ReconcileUncertain(workspace,"results").Outcome,"directory inspection does not turn unknown causality into success");
            });
        }
        private static void PluginsResultsAndPreparedAuthority()
        {
            WithPluginPaths(paths=>PluginResultsAndPreparedAuthorityAsync(paths).GetAwaiter().GetResult());
        }
        private static async Task PluginResultsAndPreparedAuthorityAsync(AppDataPaths paths)
        {
            using(var service=new PluginApplicationService(paths))
            {
                var item=PluginFixture(service,paths);var ui=PluginOpen(service,item.PluginId);var other=PluginOpen(service,item.PluginId);
                var picker=new PluginPicker{Root=Path.Combine(paths.Root,"data")};Directory.CreateDirectory(picker.Root);File.WriteAllText(Path.Combine(picker.Root,"in.txt"),"accepted");service.ProjectPicker=picker;
                var project=(await PluginRequest(service,ui,PluginMethod.PickProject)).Result.Handle;
                await PluginRequest(service,ui,PluginMethod.OpenFile,"in.txt",project);
                File.WriteAllText(Path.Combine(picker.Root,"in.txt"),"changed!");
                service.Executor=new PluginExecutor{Execute=async (operation,token)=>{
                    var source=(await PluginRequest(service,operation,PluginMethod.OpenFile,"in.txt",project)).Result;
                    AssertEqual("accepted",Encoding.UTF8.GetString(await operation.ReadRangeAsync(source.Handle,0,8,token)),"command inherits the accepted source snapshot");
                    AssertEqual("plugin_capability_denied",(await PluginRequest(service,operation,PluginMethod.BeginWrite,"illegal.txt",project)).ErrorCode,"read command cannot write through a writable grant");
                    var writer=(await PluginRequest(service,operation,PluginMethod.BeginResult,"in.txt.canonical.json")).Result;
                    await operation.WriteChunkAsync(writer.Handle,0,Encoding.UTF8.GetBytes("{}"),token);
                    var result=(await PluginRequest(service,operation,PluginMethod.CommitResult,handle:writer.Handle)).Result;
                    return "{\"text\":"+JsonConvert.SerializeObject(result.Name)+"}";
                }};
                await service.InvokeAsync(ui,"echo","{\"text\":\"run\"}",CancellationToken.None);
                var opened=(await PluginRequest(service,ui,PluginMethod.OpenResult,"in.txt.canonical.json")).Result;
                AssertEqual("{}",Encoding.UTF8.GetString(service.ReadResult(ui,opened.Handle,0,2)),"parent can read retained command result after worker closes");
                AssertEqual("plugin_result_denied",(await PluginRequest(service,other,PluginMethod.OpenResult,"in.txt.canonical.json")).ErrorCode,"result cannot cross UI owners");
                PluginFailure(()=>service.ReadResult(other,opened.Handle,0,2),"plugin_result_denied");
                await PluginRequest(service,ui,PluginMethod.DropResult,"in.txt.canonical.json");
                AssertEqual("plugin_result_denied",(await PluginRequest(service,ui,PluginMethod.OpenResult,"in.txt.canonical.json")).ErrorCode,"discard releases result authority");
                var workspace=new WorkspaceStore(paths).Open(item.SourceRoot,false,true);
                var registry=new ToolHandlerRegistry();
                foreach(var tool in service.CaptureTools(workspace))registry.Register(ToolPackSnapshot.Capture(new ToolDescriptor(tool.Entry.Id,tool.Entry.Description,tool.Entry.ArgumentSchemaJson),tool.Entry.Policy,tool.Entry.Binding),tool.Handler);
                var runtime=new ToolRuntime(registry,"agent",false,true,(context,preparation)=>"approval");
                var checkCall=new ToolCall("check","plugins.check","{}");
                var checkedResult=await runtime.ExecuteAsync(new ToolExecutionContext(checkCall,runtime.Describe(checkCall),"run","turn","check",DateTime.UtcNow,false,5),CancellationToken.None);
                AssertTrue(!checkedResult.Result.DataJson.Contains("candidateId") && !checkedResult.Result.DataJson.Contains("releaseId"),"model sees validation facts without authority handles");
                var call=new ToolCall("apply","plugins.apply","{}");var policy=runtime.Describe(call);
                var pending=await runtime.ExecuteAsync(new ToolExecutionContext(call,policy,"run","turn","apply",DateTime.UtcNow,false,5),CancellationToken.None);
                AssertEqual(ToolExecutionOutcome.AwaitingConfirmation,pending.Outcome,"publication requires confirmation");
                service.Check(item.PluginId);
                var refused=await runtime.ExecuteAsync(new ToolExecutionContext(call,policy,"run","turn","apply",DateTime.UtcNow,true,5,pending.PreparedStateJson),CancellationToken.None);
                AssertTrue(!refused.MayHaveDispatched && refused.Result.Status==RNAssistant.Core.Tools.Contracts.ToolResultStatus.Error,"rechecking invalidates the prepared candidate without publishing another");
                var before=service.CaptureCommandContext(item.PluginId,project,true);await service.CloseInstanceAsync(before.Id);
                picker.Root=Path.Combine(paths.Root,"other-data");Directory.CreateDirectory(picker.Root);await PluginRequest(service,ui,PluginMethod.PickProject);
                PluginFailure(()=>service.CaptureCommandContext(item.PluginId,project,true),"plugin_project_changed");
                AssertEqual("project_changed",(await PluginRequest(service,ui,PluginMethod.OpenFile,"in.txt",project)).ErrorCode,"stale data handles cannot follow selection");
            }
        }
        private static void PluginsDataTemplatesPackage()
        {
            WithPluginPaths(paths=>{
                var web=Path.Combine(FindHarnessRepositoryRoot(),"web");using(var service=new PluginApplicationService(paths,web))
                {
                    service.EnsureDataTemplates();var item=service.List().Items.Single();var instance=PluginOpen(service,item.PluginId);
                    AssertEqual("data-templates",item.PluginId,"ordinary shipped plugin");
                    AssertTrue(!instance.Release.Files.Any(file=>file.Path=="application.json"),"one manifest authority");
                    AssertEqual(5,instance.Manifest.Vendors.Length,"shared vendor resolution");
                    AssertTrue(service.Conversations.AvailableToolIds(new WorkspaceStore(paths).Open(item.SourceRoot)).Contains("plugins.data-templates.extract"),"chat command descriptor");
                    foreach(var vendor in service.Vendors.List())service.Vendors.Resolve(vendor.Id,vendor.Version);
                    var firstRoot=Path.Combine(paths.Root,"data-project-one");
                    var secondRoot=Path.Combine(paths.Root,"data-project-two");
                    Directory.CreateDirectory(firstRoot);Directory.CreateDirectory(secondRoot);
                    File.WriteAllText(Path.Combine(firstRoot,"source.txt"),"keep");
                    var picker=new PluginPicker{Root=firstRoot};service.ProjectPicker=picker;
                    PluginRequest(service,instance,PluginMethod.PickProject).GetAwaiter().GetResult();
                    var first=service.ConnectProjectWorkspace(new ProjectConnectionRequest{RootPath=firstRoot}).SelectedWorkspaceId;
                    var second=service.ConnectProjectWorkspace(new ProjectConnectionRequest{RootPath=secondRoot}).SelectedWorkspaceId;
                    AssertTrue(first!=second,"connected workspaces have separate identities");
                    var created=service.CreateProjectChat(new ProjectChatRequest{WorkspaceId=first,Title="Project discussion"});
                    AssertEqual(created.SessionId,service.ProjectChats().Projects.Single(p=>p.WorkspaceId==first).Chats.Single().Id,
                        "project chat appears under its workspace");
                    service.RenameProjectChat(new ProjectChatRequest{WorkspaceId=first,SessionId=created.SessionId,Title="Renamed discussion"});
                    AssertEqual("Renamed discussion",service.ProjectChat(new ProjectChatRequest{WorkspaceId=first,SessionId=created.SessionId}).State.Title,
                        "project chat title persists in workspace history");
                    PluginFailure(()=>service.ProjectChat(new ProjectChatRequest{WorkspaceId=second,SessionId=created.SessionId}),"project_chat_missing");
                    picker.Root=secondRoot;PluginRequest(service,instance,PluginMethod.PickProject).GetAwaiter().GetResult();
                    AssertEqual(0,service.ProjectChats().Projects.Single(p=>p.WorkspaceId==second).Chats.Count,
                        "another workspace has separate chats");
                    AssertEqual(created.SessionId,service.ProjectChats().Projects.Single(p=>p.WorkspaceId==first).Chats.Single().Id,
                        "project chat history survives plugin project reselection");
                    PluginFailure(()=>service.ControlProjectChatAsync(new ProjectChatRequest {WorkspaceId=first,
                        SessionId=created.SessionId,RunId="missing",Cursor="events1.missing"},true,
                        new AppSettings(),()=>"",CancellationToken.None).GetAwaiter().GetResult(),"project_chat_stale");
                    var forgedRoot=Path.Combine(paths.Root,"unrelated-folder");Directory.CreateDirectory(forgedRoot);
                    var associationPath=Path.Combine(paths.WorkspaceDirectory,first+".json");
                    var originalAssociation=File.ReadAllText(associationPath);
                    try
                    {
                        var forgedAssociation=JObject.Parse(originalAssociation);forgedAssociation["RootPath"]=forgedRoot;
                        File.WriteAllText(associationPath,forgedAssociation.ToString());
                        try { service.CreateProjectChat(new ProjectChatRequest{WorkspaceId=first});throw new Exception("Expected connection refusal"); }
                        catch(InvalidDataException) { }
                        AssertTrue(!Directory.Exists(Path.Combine(forgedRoot,".rnassistant")),
                            "changed connection cannot create a manifest in another folder");
                    }
                    finally { File.WriteAllText(associationPath,originalAssociation); }
                    service.DeleteProjectHistory(new ProjectChatRequest{WorkspaceId=first});
                    AssertTrue(!service.ProjectChats().Projects.Any(p=>p.WorkspaceId==first),
                        "project history deletion removes the workspace from the tree");
                    AssertTrue(File.Exists(Path.Combine(firstRoot,"source.txt")),"project history deletion preserves source files");
                    var missingRoot=Path.Combine(paths.Root,"missing-project");Directory.CreateDirectory(missingRoot);
                    var missing=service.ConnectProjectWorkspace(new ProjectConnectionRequest{RootPath=missingRoot}).SelectedWorkspaceId;
                    service.CreateProjectChat(new ProjectChatRequest{WorkspaceId=missing,Title="Offline history"});
                    var movedRoot=missingRoot+"-moved";Directory.Move(missingRoot,movedRoot);
                    AssertTrue(!service.ProjectChats().Projects.Single(p=>p.WorkspaceId==missing).Available,
                        "missing workspace remains visible for history management");
                    service.DeleteProjectHistory(new ProjectChatRequest{WorkspaceId=missing});
                    AssertTrue(!service.ProjectChats().Projects.Any(p=>p.WorkspaceId==missing) && Directory.Exists(movedRoot),
                        "unavailable workspace history can be removed without touching moved files");
                    AssertTrue(Directory.Exists(firstRoot)&&Directory.Exists(secondRoot),"project chat actions preserve data folders");
                    service.RemoveAsync(item.PluginId).GetAwaiter().GetResult();
                    AssertEqual(0,service.List().Items.Count,"shipped plugin can leave the rail");
                    AssertEqual(item.PluginId,service.ListRemoved().Items.Single().PluginId,"shipped plugin remains restorable");
                    AssertTrue(!service.ProjectChats().Projects.Any(p=>p.WorkspaceId==first),"hidden workspace stays out of tree after plugin removal");
                    AssertEqual(first,service.ConnectProjectWorkspace(new ProjectConnectionRequest{RootPath=firstRoot}).SelectedWorkspaceId,
                        "reconnecting a workspace restores its identity");
                    AssertEqual(0,service.ProjectChats().Projects.Single(p=>p.WorkspaceId==first).Chats.Count,
                        "reconnected workspace has empty history");
                    AssertTrue(!new WorkspaceStore(paths).OpenConnected(first).ReadOnly,"connected workspace chats can edit files");
                    var independent=service.CreateProjectChat(new ProjectChatRequest{WorkspaceId=first,Title="Independent chat"});
                    AssertEqual(independent.SessionId,service.ProjectChats().Projects.Single(p=>p.WorkspaceId==first).Chats.Single().Id,
                        "workspace chat remains usable after plugin removal");
                    service.DeleteProjectChat(new ProjectChatRequest{WorkspaceId=first,SessionId=independent.SessionId});
                    PluginFailure(()=>service.DeleteProjectChat(new ProjectChatRequest{WorkspaceId=first,SessionId=independent.SessionId}),
                        "project_chat_missing");
                    AssertEqual(item.ReleaseId,service.Restore(item.PluginId).ReleaseId,"restoring shipped plugin keeps publication");
                }
            });
        }
    }
}
