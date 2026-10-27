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
using RNAssistant.Core.Agent;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office;
using RNAssistant.Office.WebView;
using RNAssistant.Runtime.Contracts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void PluginsApprovalRunLifetime()
        {
            foreach (var ending in new[] { "complete", "stop", "close", "deny", "terminal" })
            WithPluginPaths(paths =>
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
                using (var listener = new HttpListener())
                using (var entered = new ManualResetEventSlim())
                using (var release = new ManualResetEventSlim())
                using (var transport = new CancellationTokenSource())
                using (var service = new PluginApplicationService(paths))
                using (var bridge = new AssistantWebBridge(new AssistantController(paths), ignored => { }) { Plugins = service })
                {
                    var settings = new AppSettings { BaseUrl = "http://127.0.0.1:" + port, Model = "test-model",
                        AgentResponseMode = "json_object", StreamResponses = false, SmartChatTitles = false,
                        AutoCompressContext = false, AutoConfirmToolActions = false };
                    new SettingsService(paths).Save(settings);
                    var root = Path.Combine(paths.Root, "approval-project");
                    var workspaceId = service.CreateProjectWorkspace(new ProjectConnectionRequest { RootPath = root }).SelectedWorkspaceId;
                    var chat = service.CreateProjectChat(new ProjectChatRequest { WorkspaceId = workspaceId });
                    var workspace = new WorkspaceStore(paths).OpenConnected(workspaceId);
                    var requests = 0;
                    listener.Prefixes.Add("http://127.0.0.1:" + port + "/"); listener.Start();
                    var server = Task.Run(async () =>
                    {
                        for (var index = 0; index < (ending == "deny" ? 1 : 2); index++)
                        {
                            var context = await listener.GetContextAsync().ConfigureAwait(false);
                            using (var reader = new StreamReader(context.Request.InputStream))
                                await reader.ReadToEndAsync().ConfigureAwait(false);
                            Interlocked.Increment(ref requests);
                            if (index == 1)
                            {
                                entered.Set();
                                if (!release.Wait(15000)) throw new TimeoutException("Approval test did not release the model.");
                                if (ending != "complete") { context.Response.Close(); continue; }
                            }
                            var content = index == 0
                                ? new ConversationResponse(ConversationResponse.ExecuteAction, "Create the requested file.",
                                    "Создаю файл.", calls: new[] { new ConversationToolCall { Name = "files.create",
                                        Arguments = new JObject { ["relativePath"] = "once.txt", ["text"] = "ONCE" } } }).ToJson()
                                : ScriptedVerifiedFinish(new ChatStore(paths).Load(chat.SessionId),
                                    "Report the verified write.", "Файл создан.", "File created.", "files.create");
                            var bytes = Encoding.UTF8.GetBytes(new JObject { ["choices"] = new JArray(new JObject {
                                ["message"] = new JObject { ["content"] = content } }) }.ToString(Formatting.None));
                            context.Response.ContentLength64 = bytes.Length;
                            await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                            context.Response.Close();
                        }
                    });
                    try
                    {
                        var initial = service.Conversations.SubmitInputAsync(workspace, new WorkspaceSubmitInputRequest {
                            SessionId = chat.SessionId, RequestId = "approval-once", Message = "Create once.txt", Mode = ChatModes.Agent },
                            settings, () => "").GetAwaiter().GetResult();
                        service.Conversations.RunsDrained.GetAwaiter().GetResult();
                        AssertEqual(RunLifecycle.AwaitingConfirmation, initial.Summary.Lifecycle, "real write pauses for approval");
                        AssertTrue(!File.Exists(Path.Combine(root, "once.txt")), "pending call has no file effect");
                        var request = new ProjectChatRequest { WorkspaceId = workspaceId, SessionId = chat.SessionId,
                            PendingId = initial.Summary.PendingConfirmation.PendingId, Approve = ending != "deny" };
                        var waiting = service.ProjectChat(request);
                        var leasePath = Path.Combine(paths.WorkspaceDirectory, "run-leases", TextPatternEngine.Sha256(chat.SessionId) + ".lck");
                        using (new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                        {
                            var timer = System.Diagnostics.Stopwatch.StartNew();
                            PluginFailure(() => service.ControlProjectChatAsync(request, false, settings, () => "", CancellationToken.None)
                                .GetAwaiter().GetResult(), "project_chat_busy");
                            AssertTrue(timer.ElapsedMilliseconds < 3000, "approval does not wait behind another session writer");
                        }
                        using (var cancelled = new CancellationTokenSource())
                        {
                            cancelled.Cancel();
                            var cancelledBeforeAdmission = false;
                            try { service.ControlProjectChatAsync(request, false, settings, () => "", cancelled.Token).GetAwaiter().GetResult(); }
                            catch (OperationCanceledException) { cancelledBeforeAdmission = true; }
                            AssertTrue(cancelledBeforeAdmission, "transport cancellation before approval is respected");
                        }
                        service.Conversations.RunsDrained.GetAwaiter().GetResult();
                        AssertEqual(waiting.State.Cursor, service.ProjectChat(request).State.Cursor,
                            "busy/cancelled requests do not consume the approval");

                        JObject admitted = null;
                        Task terminal = null;
                        if (ending == "complete")
                        {
                            var reply = bridge.HandleMessageAsync(WorkspaceBridgeRequest(BridgeToken(bridge), "resolveProjectChat", request));
                            AssertTrue(entered.Wait(5000), "approved write reaches the held model request");
                            AssertTrue(reply.Wait(3000), "approval bridge reply completes before the model response");
                            admitted = JObject.Parse(reply.GetAwaiter().GetResult());
                            AssertTrue((bool)admitted["ok"], "approval admitted through actual bridge");
                            AssertEqual("running", (string)admitted["payload"]["state"]["Status"], "admission returns a persisted running checkpoint");
                            AssertTrue(admitted["payload"]["pendingId"].Type == JTokenType.Null, "consumed confirmation is no longer offered");
                            var duplicate = WorkspaceBridgeCall(bridge, BridgeToken(bridge), "resolveProjectChat", request);
                            AssertTrue(!(bool)duplicate["ok"], "duplicate approval while running is refused");
                            bridge.Dispose();
                            using (var reopened = new AssistantWebBridge(new AssistantController(paths), ignored => { }) { Plugins = service })
                                AssertEqual("running", (string)WorkspaceBridgeCall(reopened, BridgeToken(reopened), "getProjectChat", request)
                                    ["payload"]["state"]["Status"], "bridge recreation leaves the owned continuation running");
                        }
                        else if (ending == "terminal")
                        {
                            terminal = service.Conversations.ResolvePendingAsync(workspace, chat.SessionId, request.PendingId,
                                true, settings, () => "", cancellationToken: transport.Token);
                            AssertTrue(entered.Wait(5000) && !terminal.IsCompleted, "terminal API still awaits the same tracked worker");
                        }
                        else
                        {
                            service.ControlProjectChatAsync(request, false, ending == "deny" ? null : settings,
                                () => "", transport.Token, progress: value => { throw new InvalidOperationException("Observer closed"); })
                                .GetAwaiter().GetResult();
                        }
                        if (ending != "deny")
                        {
                            AssertTrue(entered.Wait(5000), "continuation is waiting on the model");
                            AssertEqual("ONCE", File.ReadAllText(Path.Combine(root, "once.txt")), "approved write has real read-back evidence");
                            var worker = service.Conversations.RunsDrained;
                            PluginFailure(() => { using (PluginStore.AcquireRuntimeUse(paths, exclusive: true)) { } }, "plugins_busy");
                            transport.Cancel();
                            if (ending == "terminal")
                            {
                                AssertTrue(terminal.Wait(5000) && worker.Wait(5000), "terminal caller cancellation stops and drains execution");
                            }
                            else
                            {
                                AssertTrue(!worker.Wait(100), "late bridge cancellation cannot stop accepted continuation");
                                if (ending == "close") AssertTrue(service.CloseAsync().Wait(5000), "application close cancels and drains approval worker");
                                else if (ending == "stop")
                                {
                                    var current = service.ProjectChat(request).State;
                                    var stop = service.ControlProjectChatAsync(new ProjectChatRequest { WorkspaceId = workspaceId,
                                        SessionId = chat.SessionId, RunId = current.RunId, Cursor = current.Cursor }, true,
                                        settings, () => "", CancellationToken.None).GetAwaiter().GetResult();
                                    AssertEqual(WorkspaceCancellationStatus.Requested, stop.CancelStatus.Value, "exact Stop reaches the approval worker");
                                    AssertTrue(worker.Wait(5000), "stopped continuation drains");
                                }
                            }
                        }
                        release.Set();
                        AssertTrue(server.Wait(5000) && service.Conversations.RunsDrained.Wait(5000), "all owned work drains");
                        var durable = new ChatStore(paths).Load(chat.SessionId);
                        AssertEqual(ending == "complete" ? RunLifecycle.Completed : RunLifecycle.Cancelled,
                            durable.LastRun.KernelState.Summary.Lifecycle, "kernel records the actual terminal lifecycle: " + ending);
                        AssertEqual(ending == "deny" ? "confirmation_denied" : ending == "complete" ? "model_finish" : "cancelled",
                            durable.LastRun.KernelState.Summary.Reason, "terminal reason: " + ending);
                        AssertEqual(ending == "deny" ? 0 : 1, durable.LastRun.KernelState.Summary.ToolCounts.WriteOk, "one verified write, no replay");
                        AssertEqual(ending == "deny" ? 1 : 2, requests, "denial does not call the model; approval calls it once");
                        if (ending == "deny") AssertTrue(!File.Exists(Path.Combine(root, "once.txt")), "denial never writes");
                        AssertEqual(1, durable.Messages.Count(item => item.ToolName == "files.create" && item.ExecutionProgress != null),
                            "one terminal result for the accepted call");
                        using (PluginStore.AcquireRuntimeUse(paths, exclusive: true)) { }
                        using (new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                        if (ending == "complete")
                        {
                            using (var reopened = new PluginApplicationService(paths))
                            {
                                var revision = durable.Revision;
                                var refused = false;
                                try { reopened.ControlProjectChatAsync(request, false, settings, () => "", CancellationToken.None).GetAwaiter().GetResult(); }
                                catch (InvalidOperationException) { refused = true; }
                                reopened.Conversations.RunsDrained.GetAwaiter().GetResult();
                                AssertTrue(refused && new ChatStore(paths).Load(chat.SessionId).Revision == revision,
                                    "retry after restart cannot consume an old confirmation or replay its effect");
                            }
                            var fixture = Environment.GetEnvironmentVariable("RNA_WORKSPACE_APPROVAL_FIXTURE");
                            if (!string.IsNullOrWhiteSpace(fixture))
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(fixture)));
                                File.WriteAllText(fixture, new JObject { ["waiting"] = JObject.FromObject(waiting),
                                    ["admitted"] = admitted, ["completed"] = JObject.FromObject(service.ProjectChat(request)) }
                                    .ToString(Formatting.Indented));
                            }
                        }
                    }
                    finally { release.Set(); listener.Stop(); service.CloseAsync().GetAwaiter().GetResult(); }
                }
            });
        }
    }
}
