using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
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
