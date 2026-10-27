using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Application.Conversations;
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
using RNAssistant.Core.Persistence;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office;
using RNAssistant.Office.WebView;
using RNAssistant.Runtime;
using RNAssistant.Runtime.Contracts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void WorkspaceSteeringUsesRunOwner()
        {
            var fixtures = new JObject();
            foreach (var scenario in new[] { "model", "tool", "stop" }) WithPluginPaths(paths =>
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
                using (var listener = new HttpListener())
                using (var firstModel = new ManualResetEventSlim())
                using (var secondModel = new ManualResetEventSlim())
                using (var finish = new ManualResetEventSlim())
                using (var tool = new WorkspaceSteeringRead())
                using (var cancel = new CancellationTokenSource())
                using (var owner = new WorkspaceConversationService(paths, extraTools: _ => new[] {
                    new WorkspaceToolContribution { Entry = PackageTool("fixture.wait", ToolExposure.Direct), Handler = tool } }))
                using (var application = new PluginApplicationService(paths))
                using (var bridge = new AssistantWebBridge(new AssistantController(paths), ignored => { }) { Plugins = application })
                {
                    var settings = new AppSettings { BaseUrl = "http://127.0.0.1:" + port, Model = "test-model",
                        AgentResponseMode = "json_object", StreamResponses = false, SmartChatTitles = false,
                        AutoCompressContext = false, AutoConfirmToolActions = true };
                    new SettingsService(paths).Save(settings);
                    var workspaceId = application.CreateProjectWorkspace(new ProjectConnectionRequest {
                        RootPath = Path.Combine(paths.Root, "steer") }).SelectedWorkspaceId;
                    var workspace = new WorkspaceStore(paths).OpenConnected(workspaceId);
                    var chat = application.CreateProjectChat(new ProjectChatRequest { WorkspaceId = workspaceId });
                    JObject secondRequest = null;
                    var requests = 0;
                    listener.Prefixes.Add("http://127.0.0.1:" + port + "/"); listener.Start();
                    var server = Task.Run(async () =>
                    {
                        var first = await listener.GetContextAsync().ConfigureAwait(false);
                        using (var reader = new StreamReader(first.Request.InputStream)) await reader.ReadToEndAsync().ConfigureAwait(false);
                        Interlocked.Increment(ref requests); firstModel.Set();
                        if (scenario != "model") await SteeringReply(first, new ConversationResponse(ConversationResponse.ExecuteAction,
                            "Read before answering.", "Читаю.", calls: new[] { new ConversationToolCall {
                                Name = "fixture.wait", Arguments = new JObject() } }).ToJson()).ConfigureAwait(false);
                        var next = await listener.GetContextAsync().ConfigureAwait(false);
                        using (var reader = new StreamReader(next.Request.InputStream))
                            secondRequest = JObject.Parse(await reader.ReadToEndAsync().ConfigureAwait(false));
                        Interlocked.Increment(ref requests); secondModel.Set();
                        if (!finish.Wait(10000)) throw new TimeoutException("Steering test did not release response.");
                        if (scenario == "model")
                        {
                            // A late tool proposal from the cancelled request must never execute.
                            try { await SteeringReply(first, new ConversationResponse(ConversationResponse.ExecuteAction,
                                "Late response.", "Create.", calls: new[] { new ConversationToolCall { Name = "files.create",
                                    Arguments = new JObject { ["relativePath"] = "late.txt", ["text"] = "WRONG" } } }).ToJson()).ConfigureAwait(false); }
                            catch (Exception error) when (error is HttpListenerException || error is IOException || error is ObjectDisposedException) { }
                        }
                        var content = scenario == "tool" ? ScriptedVerifiedFinish(new ChatStore(paths).Load(chat.SessionId),
                            "Use the latest input.", "Уточнение принято.", "Read completed.", "fixture.wait") :
                            ModelProtocolWire.WriteAcceptedDecision(ConversationResponse.FinishAction, "Use the latest input.",
                                "Ответ по уточнению.", results: new ConversationResultClaim[0], outcome: ConversationOutcome.Completed);
                        await SteeringReply(next, content).ConfigureAwait(false);
                    });
                    Task<WorkspaceRunResult> run = null;
                    try
                    {
                        run = owner.SubmitInputAsync(workspace, new WorkspaceSubmitInputRequest { SessionId = chat.SessionId,
                            RequestId = "initial", Message = "Original task" }, settings, () => "", cancellationToken: cancel.Token);
                        AssertTrue(firstModel.Wait(5000), "initial model reached");
                        if (scenario != "model") AssertTrue(tool.Entered.Wait(5000), "tool executes before steer");
                        var waiting = application.ProjectChat(new ProjectChatRequest { WorkspaceId = workspaceId, SessionId = chat.SessionId });
                        AssertTrue(waiting.State.CanSteer, "another service discovers the input capability through the owner's pipe");
                        var request = new ProjectChatRequest { WorkspaceId = workspaceId, SessionId = chat.SessionId,
                            Input = new WorkspaceSubmitInputRequest { SessionId = chat.SessionId, RequestId = "steer-once",
                                Message = "STEER_UNIQUE_MESSAGE", SteerRunId = waiting.State.RunId } };
                        var accepted = WorkspaceBridgeCall(bridge, BridgeToken(bridge), "submitProjectChat", request);
                        AssertTrue((bool)accepted["ok"], "native bridge accepts steer: " + accepted);
                        var receipt = accepted["payload"]["inputReceipt"].ToObject<WorkspaceInputReceipt>();
                        AssertTrue(receipt.Steering && receipt.RunId == waiting.State.RunId, "receipt pins intake to the active run");
                        var duplicate = WorkspaceBridgeCall(bridge, BridgeToken(bridge), "submitProjectChat", request);
                        AssertTrue((bool)duplicate["ok"], "duplicate receipt is readable");
                        AssertEqual(receipt.InputId, (string)duplicate["payload"]["inputReceipt"]["InputId"], "retry has one durable identity");
                        request.Input.Message = "changed";
                        var conflict = WorkspaceBridgeCall(bridge, BridgeToken(bridge), "submitProjectChat", request);
                        AssertTrue(!(bool)conflict["ok"] && conflict.ToString().Contains("request_id_conflict"), "request reuse rejects changed text");
                        request.Input.Message = "STEER_UNIQUE_MESSAGE";
                        request.Input.SteerRunId = "other-run"; request.Input.RequestId = "foreign";
                        AssertTrue(!(bool)WorkspaceBridgeCall(bridge, BridgeToken(bridge), "submitProjectChat", request)["ok"], "another run cannot receive input");
                        request.Input.SteerRunId = waiting.State.RunId; request.Input.RequestId = "steer-once";
                        if (scenario != "model")
                        {
                            AssertTrue(!receipt.DeliveryRecorded && !tool.Token.IsCancellationRequested && requests == 1,
                                "intake returns before tool completion without cancelling it or starting another model");
                            var pending = application.ProjectChat(request);
                            AssertEqual("STEER_UNIQUE_MESSAGE", pending.State.PendingInputs.Single().Text, "durable pending input is visible");
                            AssertEqual(1, pending.Messages.Count(message => message.Role == "user"), "intake never edits the running message list");
                            if (scenario == "tool") { fixtures["waiting"] = JObject.FromObject(waiting); fixtures["accepted"] = accepted; }
                            if (scenario == "stop") cancel.Cancel();
                            tool.Release.Set();
                        }
                        if (scenario == "stop")
                        {
                            AssertTrue(run.Wait(5000) && owner.RunsDrained.Wait(5000), "stop drains the original owner");
                            AssertEqual(RunLifecycle.Cancelled, run.Result.Summary.Lifecycle, "explicit stop stays cancellation");
                            var stopped = application.ProjectChat(request);
                            AssertTrue(!stopped.State.CanSteer && stopped.State.PendingInputs.Count == 1, "unapplied input survives stop visibly");
                            fixtures["stopped"] = JObject.FromObject(stopped);
                            // A fresh application/run must not automatically replay an earlier exact-run steer.
                            using (var reopened = new WorkspaceConversationService(paths))
                            {
                                var next = reopened.SubmitInputAsync(workspace, new WorkspaceSubmitInputRequest {
                                    SessionId = chat.SessionId, RequestId = "new-task", Message = "NEW_TASK" }, settings, () => "");
                                AssertTrue(secondModel.Wait(5000), "explicit next turn starts"); finish.Set();
                                AssertTrue(next.Wait(5000) && reopened.RunsDrained.Wait(5000), "fresh run drains");
                                AssertEqual(0, owner.GetSession(workspace, chat.SessionId).Messages.Count(message => message.Id == receipt.InputId),
                                    "retained steer is not replayed into a different run");
                            }
                        }
                        else
                        {
                            AssertTrue(secondModel.Wait(5000), "same kernel requests after steering");
                            var actual = owner.GetSession(workspace, chat.SessionId);
                            AssertEqual(waiting.State.RunId, actual.LastRun.RunId, "steer does not create a new run");
                            AssertEqual(1, actual.Messages.Count(message => message.Id == receipt.InputId && message.Role == "user"), "one persisted user message");
                            var receiptAfter = application.ProjectChat(new ProjectChatRequest { WorkspaceId = workspaceId,
                                SessionId = chat.SessionId, ReceiptRequestId = "steer-once" }).InputReceipt;
                            AssertTrue(receiptAfter.DeliveryRecorded, "reconnect sees applied receipt without replay");
                            finish.Set(); AssertTrue(run.Wait(5000) && owner.RunsDrained.Wait(5000), "steered run completes");
                            AssertEqual(RunLifecycle.Completed, run.Result.Summary.Lifecycle, "kernel owns completed outcome");
                            if (scenario == "tool") fixtures["completed"] = JObject.FromObject(application.ProjectChat(request));
                        }
                        AssertTrue(server.Wait(5000), "scripted provider drains");
                        var contentText = secondRequest["messages"].ToString(Formatting.None);
                        AssertEqual(scenario == "stop" ? 0 : 1,
                            System.Text.RegularExpressions.Regex.Matches(contentText, "STEER_UNIQUE_MESSAGE").Count,
                            "actual model request includes consumed input exactly once, never an unapplied input");
                        AssertTrue(!File.Exists(Path.Combine(workspace.RootPath, "late.txt")), "late discarded model response has no file effect");
                        AssertEqual(2, requests, "only expected model requests");
                    }
                    finally
                    {
                        finish.Set(); tool.Release.Set(); cancel.Cancel(); listener.Stop();
                        try { owner.CloseAsync().Wait(5000); server.Wait(5000); } catch { }
                    }
                }
            });
            var fixture = Environment.GetEnvironmentVariable("RNA_WORKSPACE_STEER_FIXTURE");
            if (!string.IsNullOrWhiteSpace(fixture))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(fixture)));
                File.WriteAllText(fixture, fixtures.ToString(Formatting.Indented));
            }
        }

        private static async Task SteeringReply(HttpListenerContext context, string content)
        {
            var bytes = Encoding.UTF8.GetBytes(new JObject { ["choices"] = new JArray(new JObject {
                ["message"] = new JObject { ["content"] = content } }) }.ToString(Formatting.None));
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            context.Response.Close();
        }

        private static void WorkspaceSteerRecovery()
        {
            foreach (var persisted in new[] { false, true }) WithPluginPaths(paths =>
            {
                using (var application = new PluginApplicationService(paths))
                {
                    var workspaceId = application.CreateProjectWorkspace(new ProjectConnectionRequest {
                        RootPath = Path.Combine(paths.Root, "recovery") }).SelectedWorkspaceId;
                    var workspace = new WorkspaceStore(paths).OpenConnected(workspaceId);
                    var session = application.Conversations.CreateSession(workspace, "Recovery");
                    var store = new ChatStore(paths);
                    var events = new ChatEventStoreAdapter(store);
                    var journal = new ConversationInputJournal(events);
                    var input = new ConversationInput { Id = "steer-input", OperationId = "steer-operation",
                        Revision = 1, AcceptedRunId = "original-run", RequestFingerprint = "fixture",
                        Text = "Retained steer", Status = ConversationInputStatus.Pending, Delivery = InputDelivery.Steer,
                        CreatedUtc = DateTime.UtcNow, DocumentRuntimeKey = workspaceId };
                    journal.Append(session, new ConversationInputEvent { Input = input });
                    input.Revision++; input.Status = ConversationInputStatus.Delivering;
                    journal.Append(session, new ConversationInputEvent { Input = input });
                    if (persisted)
                    {
                        var message = new ChatMessage { Id = input.Id, RunId = input.AcceptedRunId, Role = "user", Content = input.Text };
                        session.Messages.Add(message); store.Commit(session, ConversationCommit.UserInput(message));
                    }
                    var recovered = application.Conversations.PrepareResume(workspace, session.Id);
                    var receipt = application.Conversations.ReadInputReceipt(recovered, input.OperationId);
                    AssertEqual(persisted, receipt.DeliveryRecorded, "recovery only acknowledges a persisted exact user message");
                    AssertTrue(receipt.Steering, "recovery retains intake kind");
                    AssertEqual(persisted ? 0 : 1, application.Conversations.ReadState(workspace, recovered).PendingInputs.Count,
                        "unapplied body remains visible after restart");
                    if (!persisted)
                    {
                        var inbox = new ConversationInboxService(events);
                        inbox.Resume(recovered);
                        AssertTrue(!inbox.TryStartWorker(recovered) && inbox.TakeNext(recovered) == null,
                            "exact-run input cannot become a queued new task");
                        recovered.LastRun = new ChatRunRecord { RunId = "another-run" };
                        inbox.Bind(recovered);
                        AssertTrue(!inbox.HasSteer(recovered) && inbox.TakeSteer(recovered).Count == 0,
                            "a later run cannot consume retained steering");
                    }
                }
            });
        }

        private sealed class WorkspaceSteeringRead : IReadOnlyToolHandler, IDisposable
        {
            internal readonly ManualResetEventSlim Entered = new ManualResetEventSlim();
            internal readonly ManualResetEventSlim Release = new ManualResetEventSlim();
            internal CancellationToken Token;
            public async Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken token)
            {
                Token = token; Entered.Set();
                await Task.Run(() => { if (!Release.Wait(10000)) throw new TimeoutException("Read not released."); }).ConfigureAwait(false);
                return new ToolHandlerResult(ToolResult.Ok("Read complete", "{}"), ToolEffectEvidence.None);
            }
            public void Dispose() { Entered.Dispose(); Release.Dispose(); }
        }
    }
}
