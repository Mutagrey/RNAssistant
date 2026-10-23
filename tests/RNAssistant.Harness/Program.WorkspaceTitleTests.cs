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
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Runtime;
using RNAssistant.Runtime.Contracts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void WorkspaceAcceptedCallHistory()
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
                    var requests = new string[3];
                    var server = Task.Run(async () =>
                    {
                        try
                        {
                            while (served < 3)
                            {
                                var context = await listener.GetContextAsync().ConfigureAwait(false);
                                var number = Interlocked.Increment(ref served);
                                using (var reader = new StreamReader(context.Request.InputStream))
                                    requests[number - 1] = await reader.ReadToEndAsync().ConfigureAwait(false);
                                var content = number == 1
                                    ? "{\"message\":\"Смотрю ресурсы.\",\"action\":\"tool\",\"tool_calls\":[{\"name\":\"common.resources_find\",\"arguments\":{}},{\"name\":\"common.resources_find\",\"arguments\":{\"query\":\"missing\"}}]}"
                                    : number == 2
                                        ? "{\"message\":\"Первый ответ.\",\"action\":\"done\",\"tool_calls\":[]}"
                                        : "{\"message\":\"История читается.\",\"action\":\"done\",\"tool_calls\":[]}";
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
                            SmartChatTitles = false };
                        string sessionId;
                        using (var service = new WorkspaceConversationService(paths))
                        {
                            sessionId = service.CreateSession(workspace, "История").Id;
                            var first = service.SubmitInputAsync(workspace, new WorkspaceSubmitInputRequest {
                                SessionId = sessionId, Message = "Посмотри ресурсы", Mode = ChatModes.Agent
                            }, settings, () => "test-key").GetAwaiter().GetResult();
                            AssertEqual(RunLifecycle.Completed, first.Summary.Lifecycle, "first workspace turn completes");
                            var calls = service.GetSession(workspace, sessionId).Messages.Where(message =>
                                message.Role == "assistant" && message.ToolCalls != null && message.ToolCalls.Count > 0).ToArray();
                            AssertEqual(2, calls.Length, "accepted read batch has two durable per-call records");
                            AssertTrue(calls.All(call => call.ToolCalls.Single().Id == call.ToolCallId &&
                                call.ToolCalls.Single().Name == call.ToolName), "each durable call keeps its runtime identity");
                            AssertTrue(calls.All(call => call.AcceptedCallOrigin != null &&
                                !string.IsNullOrWhiteSpace(call.AcceptedCallOrigin.ModelAttemptId)) &&
                                calls[0].AcceptedCallOrigin.ModelAttemptId == calls[1].AcceptedCallOrigin.ModelAttemptId &&
                                calls.Select(call => call.AcceptedCallOrigin.CallIndex).SequenceEqual(new[] { 0, 1 }),
                                "batch calls keep one exact model attempt and their original positions");
                            AssertEqual("Смотрю ресурсы.", PluginApplicationService.PresentWorkspaceMessage(calls[0]).Text,
                                "plugin chat presents the accepted narration after a tool call");
                        }
                        using (var reopened = new WorkspaceConversationService(paths))
                        {
                            var next = reopened.SubmitInputAsync(workspace, new WorkspaceSubmitInputRequest {
                                SessionId = sessionId, Message = "Продолжай", Mode = ChatModes.Agent
                            }, settings, () => "test-key").GetAwaiter().GetResult();
                            AssertEqual(RunLifecycle.Completed, next.Summary.Lifecycle, "next turn reads durable call history");
                            AssertEqual("История читается.", next.Summary.AssistantMessage, "next turn receives model answer");
                            var modelMessages = (JArray)JObject.Parse(requests[2])["messages"];
                            AssertTrue(modelMessages.Any(item => (string)item["role"] == "assistant" &&
                                (string)item["content"] == "Первый ответ."),
                                "next request contains accepted final narration without a v7 JSON envelope");
                            var legacy = reopened.GetSession(workspace, sessionId);
                            AssertTrue(legacy.Messages.Any(item => item.Role == "assistant" && item.ProtocolMessage &&
                                item.Content.Contains("\"message\":\"Первый ответ.\"")),
                                "model projection leaves the durable v7 response intact");
                            var oldCall = legacy.Messages.First(message => message.Role == "assistant" &&
                                message.ToolCalls != null && message.ToolCalls.Count > 0);
                            oldCall.ToolCallId = null; oldCall.ToolName = null; oldCall.AcceptedCallOrigin = null;
                            AssertEqual("Смотрю ресурсы.", PluginApplicationService.PresentWorkspaceMessage(oldCall).Text,
                                "old workspace records still display their accepted narration");
                            new ChatStore(paths).Commit(legacy,
                                new RNAssistant.Core.Persistence.ConversationCommit(new[] { oldCall }));
                        }
                        server.GetAwaiter().GetResult();
                        using (var legacyService = new WorkspaceConversationService(paths))
                        {
                            var legacy = legacyService.SubmitInputAsync(workspace, new WorkspaceSubmitInputRequest {
                                SessionId = sessionId, Message = "Ещё раз", Mode = ChatModes.Agent
                            }, settings, () => "test-key", cancellationToken: new CancellationTokenSource(3000).Token)
                                .GetAwaiter().GetResult();
                            AssertEqual(RunLifecycle.Failed, legacy.Summary.Lifecycle,
                                "incomplete old call history cannot silently enter model context");
                            AssertTrue(legacy.Summary.AssistantMessage.Contains("Создайте новый чат"),
                                "incompatible history gives an explicit recovery action");
                        }
                        var forged = new ChatStore(paths).Load(sessionId);
                        var forgedResult = forged.Messages.First(message => message.Role == "tool" &&
                            message.ToolName == "common.resources_find");
                        forgedResult.ToolName = "different.tool";
                        new ChatStore(paths).Commit(forged,
                            new RNAssistant.Core.Persistence.ConversationCommit(new[] { forgedResult }));
                        using (var invalidService = new WorkspaceConversationService(paths))
                        {
                            var rejected = false;
                            try
                            {
                                invalidService.SubmitInputAsync(workspace, new WorkspaceSubmitInputRequest {
                                    SessionId = sessionId, Message = "После подмены результата", Mode = ChatModes.Agent
                                }, settings, () => "test-key", cancellationToken: new CancellationTokenSource(3000).Token)
                                    .GetAwaiter().GetResult();
                            }
                            catch (InvalidOperationException error)
                            {
                                rejected = error.Message.Contains("Stored workspace tool result is invalid") &&
                                    error.Message.Contains("canonical tool name");
                            }
                            AssertTrue(rejected, "a result whose saved tool name differs from its wire is refused before replay");
                            AssertEqual(3, served, "corrupt result cannot cause another model request");
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
                                    : new JObject { ["message"] = "Подготовлю отчёт.", ["action"] = "done",
                                        ["tool_calls"] = new JArray() }.ToString(Newtonsoft.Json.Formatting.None);
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
