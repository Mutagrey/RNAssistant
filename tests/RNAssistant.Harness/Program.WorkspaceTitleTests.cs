using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RNAssistant.Application;
using RNAssistant.Core.Agent;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Runtime;
using RNAssistant.Runtime.Contracts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void WorkspaceAcceptedCallHistory(bool strict = false, bool rejectFinish = false, bool failedRead = false)
        {
            WithTempPaths(paths =>
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                using (var listener = new HttpListener())
                {
                    listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                    listener.Start();
                    var served = 0;
                    var expectedRequests = rejectFinish ? 4 : 3;
                    var requests = new string[expectedRequests];
                    string sessionId = null;
                    var server = Task.Run(async () =>
                    {
                        try
                        {
                            while (served < expectedRequests)
                            {
                                var context = await listener.GetContextAsync().ConfigureAwait(false);
                                var number = Interlocked.Increment(ref served);
                                using (var reader = new StreamReader(context.Request.InputStream))
                                    requests[number - 1] = await reader.ReadToEndAsync().ConfigureAwait(false);
                                var content = number == 1
                                    ? new ConversationResponse(ConversationResponse.ExecuteAction, "Inspect available resources.",
                                        "Смотрю ресурсы.", calls: new[] {
                                            new ConversationToolCall { Name = "core.find", Arguments = new JObject() },
                                            failedRead ? new ConversationToolCall { Name = "core.read", Arguments = new JObject { ["target"] = "missing.txt" } }
                                                : new ConversationToolCall { Name = "core.find", Arguments = new JObject { ["query"] = "missing" } }
                                        }).ToJson()
                                    : number < expectedRequests
                                        ? ScriptedVerifiedFinish(new ChatStore(paths).Load(sessionId),
                                            "Report the resource search.", "Первый ответ.", "Resources inspected.",
                                            failedRead ? new[] { "core.find" } : new[] { "core.find", "core.find" })
                                        : ModelProtocolWire.WriteAcceptedDecision(ConversationResponse.FinishAction,
                                            "Answer the follow-up from persisted history.", "История читается.",
                                            results: new ConversationResultClaim[0], outcome: ConversationOutcome.Completed);
                                if (rejectFinish && number == 2)
                                {
                                    var invalid = JObject.Parse(content);
                                    invalid["results"][0]["evidence"] = new JArray("r1");
                                    content = invalid.ToString(Newtonsoft.Json.Formatting.None);
                                }
                                if (strict)
                                {
                                    AssertEqual("json_schema", (string)JObject.Parse(requests[number - 1])["response_format"]?["type"],
                                        "workspace sends its prepared strict schema on every request");
                                    content = "{\"response\":" + content + "}";
                                }
                                var response = new JObject { ["choices"] = new JArray(new JObject {
                                    ["message"] = new JObject { ["content"] = content } }) }.ToString(Newtonsoft.Json.Formatting.None);
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
                        var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "history"));
                        var settings = new AppSettings { BaseUrl = "http://127.0.0.1:" + port,
                            Model = "test-model", StreamResponses = false, AutoCompressContext = false,
                            AgentResponseMode = strict ? AgentResponseModes.JsonSchema : AgentResponseModes.JsonObject,
                            SmartChatTitles = false };
                        using (var service = new WorkspaceConversationService(paths))
                        {
                            sessionId = service.CreateSession(workspace, "История").Id;
                            var first = service.SubmitInputAsync(workspace, new WorkspaceSubmitInputRequest {
                                SessionId = sessionId, Message = "Посмотри ресурсы", Mode = ChatModes.Agent
                            }, settings, () => "test-key").GetAwaiter().GetResult();
                            AssertEqual(RunLifecycle.Completed, first.Summary.Lifecycle, "first workspace turn completes: " + first.Summary.Reason + " " + first.Summary.AssistantMessage);
                            var runView = service.ReadState(workspace, sessionId).RunViewState;
                            AssertEqual(first.Summary.RunId, runView.RunId,
                                "workspace chat exposes the same typed run state as Office");
                            AssertEqual(RunViewLifecycles.Completed, runView.Lifecycle,
                                "workspace presentation uses kernel lifecycle");
                            var calls = service.GetSession(workspace, sessionId).Messages.Where(message =>
                                message.Role == "assistant" && message.AcceptedCallOrigin != null &&
                                (message.ToolName == "core.find" || failedRead && message.ToolName == "core.read")).ToArray();
                            if (rejectFinish)
                            {
                                AssertEqual(3, first.Summary.IterationsUsed, "rejected finish consumes a step of the same run");
                                AssertTrue(requests[2].Contains("unknown_evidence") && requests[2].Contains("Available terminal references"),
                                    "correction feedback supplies exact available references");
                            }
                            AssertEqual(2, calls.Length, "accepted read batch has two durable per-call records");
                            var activities = service.GetSession(workspace, sessionId).Messages.Where(message =>
                                message.RunId == first.Summary.RunId && message.Activity != null && calls.Any(call => call.ToolCallId == message.Activity.ToolCallId))
                                .Select(message => message.Activity).ToArray();
                            AssertEqual(2, activities.Length, "each accepted attempt retains its own activity");
                            AssertTrue(activities.Take(failedRead ? 1 : 2).All(activity => activity.Display?.Action == "Поиск" &&
                                activity.ResultSummary?.Kind == "resource-find" && activity.Status == "completed"),
                                "workspace uses the shared catalog and typed result summary");
                            AssertEqual(failedRead ? "missing.txt" : "missing", activities[1].Subtitle, "workspace keeps the second call's distinct target");
                            if (failedRead) AssertTrue(activities[1].Status == "failed" && activities[1].ResultSummary == null,
                                "a failed read cannot appear as completed or inherit a successful read summary");
                            AssertTrue(calls.Select(call => call.ExecutionReference.Alias).SequenceEqual(new[] { "E1", "E2" }),
                                "whole batch has distinct stable typed references");
                            var nextWire = JObject.Parse(requests[1]);
                            var nextContents = string.Join("\n", nextWire["messages"].Select(item => (string)item["content"]));
                            AssertTrue(calls.All(call => !nextContents.Contains(call.ToolCallId)),
                                "model message bodies expose aliases, never canonical GUIDs");
                            AssertTrue(nextContents.Contains("\"id\":\"E1\"") && nextContents.Contains("\"tool_call_id\":\"E1\""),
                                "model receives both accepted call and result with the same short reference");
                            AssertTrue(calls.All(call => !string.IsNullOrWhiteSpace(call.ToolCallId) &&
                                (string)JObject.Parse(call.Content)["tool_calls"]?[0]?["name"] == call.ToolName &&
                                JObject.Parse(call.Content)["tool_calls"]?.Count() == 1),
                                "each v8 per-call envelope keeps one tool while runtime owns its identity");
                            AssertTrue(calls.All(call => call.AcceptedCallOrigin != null &&
                                !string.IsNullOrWhiteSpace(call.AcceptedCallOrigin.ModelAttemptId)) &&
                                calls[0].AcceptedCallOrigin.ModelAttemptId == calls[1].AcceptedCallOrigin.ModelAttemptId &&
                                calls.Select(call => call.AcceptedCallOrigin.CallIndex).SequenceEqual(new[] { 0, 1 }),
                                "batch calls keep one exact model attempt and their original positions");
                            AssertEqual("Смотрю ресурсы.", PluginApplicationService.PresentWorkspaceMessage(calls[0]).Content,
                                "plugin chat presents the accepted narration after a tool call");
                            AssertEqual("step", PluginApplicationService.PresentWorkspaceMessage(calls[0]).Activity.Kind,
                                "workspace accepted narration joins the shared run feed");
                            var reflection = PluginApplicationService.PresentWorkspaceMessage(new ChatMessage
                            {
                                Role = "assistant", ProtocolMessage = true, RunId = first.Summary.RunId,
                                ResponseProtocolVersion = ConversationResponse.ProtocolVersion,
                                ResponseModelAttemptId = "attempt-reflection-test",
                                Content = ModelProtocolWire.WriteAcceptedDecision(ConversationResponse.ReflectionAction,
                                    "Inspect the evidence.", "Проверяю результат.", "Read the exact source.")
                            });
                            AssertEqual("step", reflection.Activity.Kind, "workspace reflection joins the shared run feed");
                            AssertEqual("Проверяю результат.", reflection.Activity.StepMessage,
                                "workspace reflection keeps the visible commentary");
                        }
                        string oldCallMessageId = null, oldCallId = null, oldToolName = null;
                        AcceptedToolCallOrigin oldOrigin = null;
                        using (var reopened = new WorkspaceConversationService(paths))
                        {
                            var next = reopened.SubmitInputAsync(workspace, new WorkspaceSubmitInputRequest {
                                SessionId = sessionId, Message = "Продолжай", Mode = ChatModes.Agent
                            }, settings, () => "test-key").GetAwaiter().GetResult();
                            AssertEqual(RunLifecycle.Completed, next.Summary.Lifecycle, "next turn reads durable call history");
                            AssertEqual("История читается.", next.Summary.AssistantMessage, "next turn receives model answer");
                            var modelMessages = (JArray)JObject.Parse(requests[expectedRequests - 1])["messages"];
                            AssertTrue(modelMessages.Any(item => (string)item["role"] == "assistant" &&
                                (string)item["content"] == "Первый ответ."),
                                "next request contains accepted final narration without a protocol envelope");
                            var corrupted = reopened.GetSession(workspace, sessionId);
                            AssertTrue(corrupted.Messages.Any(item => item.Role == "assistant" && item.ProtocolMessage &&
                                item.Content.Contains("\"message\":\"Первый ответ.\"")),
                                "model projection leaves the durable v8 response intact");
                            var oldCall = corrupted.Messages.First(message => message.Role == "assistant" &&
                                message.AcceptedCallOrigin != null && message.ToolName == "core.find");
                            oldCallMessageId = oldCall.Id;
                            oldCallId = oldCall.ToolCallId;
                            oldToolName = oldCall.ToolName;
                            oldOrigin = oldCall.AcceptedCallOrigin;
                            oldCall.ToolCallId = null; oldCall.ToolName = null; oldCall.AcceptedCallOrigin = null;
                            AssertContains(PluginApplicationService.PresentWorkspaceMessage(oldCall).Content, "неполная",
                                "a corrupted v8 call is not presented as accepted history");
                            new ChatStore(paths).Commit(corrupted,
                                new RNAssistant.Core.Persistence.ConversationCommit(new[] { oldCall }));
                        }
                        server.GetAwaiter().GetResult();
                        using (var corruptedService = new WorkspaceConversationService(paths))
                        {
                            var invalidRequest = new WorkspaceSubmitInputRequest {
                                SessionId = sessionId, RequestId = "invalid-v8-history-retry",
                                Message = "Ещё раз", Mode = ChatModes.Agent
                            };
                            InvalidOperationException rejected = null;
                            try
                            {
                                corruptedService.SubmitInputAsync(workspace, invalidRequest,
                                    settings, () => "test-key", cancellationToken: new CancellationTokenSource(3000).Token)
                                    .GetAwaiter().GetResult();
                            }
                            catch (InvalidOperationException error) { rejected = error; }
                            AssertTrue(rejected != null && rejected.Message.Contains("Stored workspace response is invalid"),
                                "incomplete accepted call history fails before another model request");
                            AssertTrue(corruptedService.FindAcceptedInput(workspace, invalidRequest) == null,
                                "rejected v8 history leaves no accepted input receipt");
                        }
                        var forged = new ChatStore(paths).Load(sessionId);
                        AssertTrue(!forged.Messages.Any(message => message.Role == "user" && message.Content == "Ещё раз"),
                            "invalid v8 response history does not accept another user turn");
                        var repairedCall = forged.Messages.Single(message => message.Id == oldCallMessageId);
                        repairedCall.ToolCallId = oldCallId;
                        repairedCall.ToolName = oldToolName;
                        repairedCall.AcceptedCallOrigin = oldOrigin;
                        new ChatStore(paths).Commit(forged,
                            new RNAssistant.Core.Persistence.ConversationCommit(new[] { repairedCall }));
                        forged = new ChatStore(paths).Load(sessionId);
                        var sibling = forged.Messages.Where(message => message.Role == "assistant" &&
                            message.AcceptedCallOrigin != null).Skip(1).First();
                        var siblingOrigin = sibling.AcceptedCallOrigin;
                        foreach (var invalidIndex in new[] { 0, siblingOrigin.CallIndex + 2 })
                        {
                            forged = new ChatStore(paths).Load(sessionId);
                            sibling = forged.Messages.Single(message => message.Id == sibling.Id);
                            sibling.AcceptedCallOrigin = new AcceptedToolCallOrigin(siblingOrigin.StepId,
                                siblingOrigin.ModelAttemptId, invalidIndex);
                            new ChatStore(paths).Commit(forged,
                                new RNAssistant.Core.Persistence.ConversationCommit(new[] { sibling }));
                            using (var batchService = new WorkspaceConversationService(paths))
                            {
                                var invalidBatch = new WorkspaceSubmitInputRequest {
                                    SessionId = sessionId, RequestId = "invalid-v8-batch-origin-" + invalidIndex,
                                    Message = "После подмены пакета", Mode = ChatModes.Agent
                                };
                                InvalidOperationException rejected = null;
                                try
                                {
                                    batchService.SubmitInputAsync(workspace, invalidBatch, settings,
                                        () => "test-key", cancellationToken: new CancellationTokenSource(3000).Token)
                                        .GetAwaiter().GetResult();
                                }
                                catch (InvalidOperationException error) { rejected = error; }
                                AssertTrue(rejected != null && rejected.Message.Contains("Stored workspace batch"),
                                    "calls from a broken v8 batch sequence cannot be rejoined");
                                AssertTrue(batchService.FindAcceptedInput(workspace, invalidBatch) == null,
                                    "bad v8 batch origin does not accept a new input");
                                AssertEqual(expectedRequests, served, "bad v8 batch origin cannot cause a model request");
                            }
                        }
                        forged = new ChatStore(paths).Load(sessionId);
                        sibling = forged.Messages.Single(message => message.Id == sibling.Id);
                        sibling.AcceptedCallOrigin = siblingOrigin;
                        new ChatStore(paths).Commit(forged,
                            new RNAssistant.Core.Persistence.ConversationCommit(new[] { sibling }));
                        forged = new ChatStore(paths).Load(sessionId);
                        var forgedResult = forged.Messages.First(message => message.Role == "tool" &&
                            message.ToolName == "core.find");
                        forgedResult.ToolName = "different.tool";
                        new ChatStore(paths).Commit(forged,
                            new RNAssistant.Core.Persistence.ConversationCommit(new[] { forgedResult }));
                        using (var invalidService = new WorkspaceConversationService(paths))
                        {
                            InvalidOperationException rejected = null;
                            try
                            {
                                invalidService.SubmitInputAsync(workspace, new WorkspaceSubmitInputRequest {
                                    SessionId = sessionId, Message = "После подмены результата", Mode = ChatModes.Agent
                                }, settings, () => "test-key", cancellationToken: new CancellationTokenSource(3000).Token)
                                    .GetAwaiter().GetResult();
                            }
                            catch (InvalidOperationException error) { rejected = error; }
                            AssertTrue(rejected != null && rejected.Message.Contains("Stored workspace tool result is invalid"),
                                "a result whose saved tool name differs from its wire is refused before replay");
                            AssertEqual(expectedRequests, served, "corrupt result cannot cause another model request");
                        }
                    }
                    finally { listener.Stop(); }
                }
            });
        }

        private static void WorkspaceChatAutoTitle()
        {
            WithTempPaths(paths =>
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                using (var listener = new HttpListener())
                using (var service = new WorkspaceConversationService(paths))
                {
                    listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                    listener.Start();
                    var served = 0;
                    var lateTitleRequested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var releaseLateTitle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var server = Task.Run(async () =>
                    {
                        try
                        {
                            while (served < 4)
                            {
                                var context = await listener.GetContextAsync().ConfigureAwait(false);
                                var requestNumber = Interlocked.Increment(ref served);
                                string request;
                                using (var reader = new StreamReader(context.Request.InputStream))
                                    request = await reader.ReadToEndAsync().ConfigureAwait(false);
                                if (requestNumber == 4)
                                {
                                    lateTitleRequested.SetResult(true);
                                    await releaseLateTitle.Task.ConfigureAwait(false);
                                }
                                var content = request.Contains("# Chat title")
                                    ? requestNumber == 4 ? "Навязанное название" : "Продажи за октябрь"
                                    : ModelProtocolWire.WriteAcceptedDecision(ConversationResponse.FinishAction,
                                        "A direct answer needs no tools.", "Подготовлю отчёт.",
                                        results: new ConversationResultClaim[0], outcome: ConversationOutcome.Completed);
                                var response = new JObject { ["choices"] = new JArray(new JObject {
                                    ["message"] = new JObject { ["content"] = content } }) }.ToString(Newtonsoft.Json.Formatting.None);
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
                        var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "titles"));
                        var session = service.CreateSession(workspace, "Новый чат");
                        var settings = new AppSettings { BaseUrl = "http://127.0.0.1:" + port,
                            Model = "test-model", StreamResponses = false, AutoCompressContext = false };
                        var run = service.SubmitInputAsync(workspace, new WorkspaceSubmitInputRequest {
                            SessionId = session.Id, Message = "Создай краткий отчёт о продажах", Mode = ChatModes.Agent
                        }, settings, () => "test-key").GetAwaiter().GetResult();
                        AssertEqual(RunLifecycle.Completed, run.Summary.Lifecycle, "workspace model run completes");
                        AssertEqual("Краткий отчёт о продажах", service.ReadState(workspace, session.Id).Title,
                            "first accepted message assigns a readable draft title");
                        AssertTrue(SpinWait.SpinUntil(() =>
                            service.ReadState(workspace, session.Id).Title == "Продажи за октябрь", 10000),
                            "smart title replaces only the auto title");
                        AssertTrue(SpinWait.SpinUntil(() =>
                            !service.ReadState(workspace, session.Id).TitleGenerationPending, 3000),
                            "title generation reaches a terminal state");
                        AssertEqual("Продажи за октябрь", service.ListSessions(workspace)[0].Title,
                            "project tree reads the generated title from durable history");
                        service.RenameSession(workspace, session.Id, "Мой заголовок");
                        AssertEqual("Мой заголовок", service.ReadState(workspace, session.Id).Title,
                            "manual title wins after automatic naming");
                        var second = service.CreateSession(workspace, "Новый чат");
                        service.SubmitInputAsync(workspace, new WorkspaceSubmitInputRequest {
                            SessionId = second.Id, Message = "Составь план анализа", Mode = ChatModes.Agent
                        }, settings, () => "test-key").GetAwaiter().GetResult();
                        AssertTrue(lateTitleRequested.Task.Wait(3000), "second title request starts after the run");
                        service.RenameSession(workspace, second.Id, "Ручное название");
                        releaseLateTitle.SetResult(true);
                        AssertTrue(SpinWait.SpinUntil(() =>
                            !service.ReadState(workspace, second.Id).TitleGenerationPending, 3000),
                            "late title request completes");
                        AssertEqual("Ручное название", service.ReadState(workspace, second.Id).Title,
                            "late model title does not overwrite a manual rename");
                        server.GetAwaiter().GetResult();
                    }
                    finally { releaseLateTitle.TrySetResult(true); listener.Stop(); }
                }
            });
        }
    }
}
