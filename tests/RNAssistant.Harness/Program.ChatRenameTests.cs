using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RNAssistant.Runtime;
using RNAssistant.Runtime.Contracts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static async Task ChatRenameGuards()
        {
            var session = NewSession(FakeOfficeAdapter.ForHost("Excel"));
            session.Title = "Original";
            var entry = ChatRenameToolHandler.Entry();
            var registry = new ToolHandlerRegistry();
            registry.Register(ToolPackSnapshot.Capture(new ToolDescriptor(entry.Id, entry.Description,
                entry.ArgumentSchemaJson), entry.Policy, entry.Binding), new ChatRenameToolHandler(session));
            var policy = ConversationRunPolicy.For(ChatModes.Plan, MutationPolicy.Deny);
            var runtime = new ToolRuntime(registry, policy.Mode, false, false, runPolicy: policy);
            AssertTrue(!policy.Allows(entry.Id, entry.Policy, new ToolBinding("foreign.handler")),
                "a lookalike name cannot bypass mutation policy");
            foreach (var args in new[] { new JObject { ["title"] = "   " }, new JObject { ["title"] = "a\nb" },
                new JObject { ["title"] = new string('x', 201) },
                new JObject { ["title"] = "Foreign", ["chat_id"] = "another-chat" } })
            {
                var call = new ToolCall(Guid.NewGuid().ToString("N"), entry.Id, args.ToString());
                var result = await runtime.ExecuteAsync(new ToolExecutionContext(call, runtime.Describe(call),
                    "run", "turn", "step", DateTime.UtcNow, false, 5), CancellationToken.None);
                AssertEqual(ToolExecutionOutcome.Error, result.Outcome, "invalid title/address is refused");
                AssertTrue(!result.MayHaveDispatched && !session.TitleAssignedByAgent && session.Title == "Original",
                    "refusal leaves title and naming policy untouched");
            }
        }

        private static void ChatRenameOffice()
        {
            WithTempExecutor((executor, adapter) => {
                var store = new ChatStore(FixturePaths.Value);
                var other = AgentChat(store, "Other chat");
                foreach (var mode in new[] { ChatModes.Agent, ChatModes.Plan })
                {
                    var session = NewSession(adapter);
                    session.Title = "Новый чат"; session.Mode = mode; session.MutationPolicy = MutationPolicy.Deny;
                    foreach (var title in new[] { "Новый чат", "Анализ продаж" })
                    {
                        var attempt = 0;
                        LlmCompletionDelegate complete = (configuration, messages, options, stream, token) => {
                            AssertContains(FlattenSimple(messages), "chat.rename", "rename is directly exposed without skill loading");
                            var response = attempt++ == 0
                                ? ModelProtocolWire.WriteAcceptedCall("Name the current task", null,
                                    new ConversationToolCall { Name = "chat.rename", Arguments = new JObject { ["title"] = title } })
                                : ScriptedVerifiedFinish(session, "Title updated", "Готово.", "Chat renamed", "chat.rename");
                            return Task.FromResult(new LlmCompletionResult { Content = response });
                        };
                        var settings = new AppSettings { AutoConfirmToolActions = false, SmartChatTitles = false,
                            AutoCompressContext = false, StreamResponses = false, AgentResponseMode = "json_object" };
                        var service = new ConversationRunService(adapter, executor, ConversationStore(store), EventStore(store), complete, null);
                        var reply = service.ExecuteAsync(mode, "Обнови название", session, NewContext(adapter), settings,
                            executor.GetControllerTools().ToList(), null, null, null, null, CancellationToken.None).GetAwaiter().GetResult();
                        AssertTrue(!reply.WaitingForConfirmation && attempt == 2, "rename completes without approval in " + mode);
                        session = store.Load(session.Id);
                        AssertEqual(title, session.Title, "kernel commit persists the title");
                        AssertTrue(session.TitleAssignedByAgent && !ChatTitleBuilder.ShouldAssign(session) &&
                            !ChatTitleBuilder.CanReplaceAutoTitle(session, title), "late automatic title cannot replace even a matching placeholder");
                        ChatTitleBuilder.ApplyFallback(session, "Fallback request", "Fallback answer");
                        AssertEqual(title, session.Title, "disabled-smart-title fallback respects agent naming");
                        AssertEqual("Other chat", store.Load(other.Id).Title, "another chat is unaffected");
                    }
                }
            });
        }

        private static void ChatRenameWorkspace()
        {
            foreach (var pluginSource in new[] { false, true }) WithTempPaths(paths => {
                var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
                using (var listener = new HttpListener())
                using (var service = new WorkspaceConversationService(paths, isPluginSourceWorkspace: _ => pluginSource))
                {
                    var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "rename"));
                    var session = service.CreateSession(workspace, "Новый чат");
                    var store = new ChatStore(paths);
                    listener.Prefixes.Add("http://127.0.0.1:" + port + "/"); listener.Start();
                    var server = Task.Run(async () => {
                        for (var i = 0; i < 2; i++)
                        {
                            var context = await listener.GetContextAsync().ConfigureAwait(false);
                            string request;
                            using (var reader = new StreamReader(context.Request.InputStream))
                                request = await reader.ReadToEndAsync().ConfigureAwait(false);
                            AssertContains(request, "chat.rename", "workspace exposes rename in the initial request");
                            var content = i == 0
                                ? ModelProtocolWire.WriteAcceptedCall("Name the current task", null,
                                    new ConversationToolCall { Name = "chat.rename", Arguments = new JObject { ["title"] = "  План анализа  " } })
                                : ScriptedVerifiedFinish(store.Load(session.Id), "Title updated", "Готово.", "Chat renamed", "chat.rename");
                            var response = new JObject { ["choices"] = new JArray(new JObject {
                                ["message"] = new JObject { ["content"] = content } }) }.ToString();
                            var bytes = Encoding.UTF8.GetBytes(response);
                            context.Response.ContentType = "application/json"; context.Response.ContentLength64 = bytes.Length;
                            await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                            context.Response.Close();
                        }
                    });
                    try
                    {
                        var settings = new AppSettings { BaseUrl = "http://127.0.0.1:" + port, Model = "fixture",
                            StreamResponses = false, AutoCompressContext = false, AutoConfirmToolActions = false, SmartChatTitles = true };
                        var result = service.SubmitInputAsync(workspace, new WorkspaceSubmitInputRequest {
                            SessionId = session.Id, Message = "Составь план", Mode = ChatModes.Plan
                        }, settings, () => "test-key").GetAwaiter().GetResult();
                        AssertEqual(RunLifecycle.Completed, result.Summary.Lifecycle, "workspace/plugin rename run completes");
                        server.GetAwaiter().GetResult();
                        var state = service.ReadState(workspace, session.Id);
                        AssertEqual("План анализа", state.Title, "workspace projection receives the trimmed title");
                        AssertTrue(!state.TitleGenerationPending && store.Load(session.Id).TitleAssignedByAgent,
                            "smart title generation is suppressed after a durable agent rename");
                        AssertEqual(state.Title, service.ListSessions(workspace).Single().Title, "chat catalog follows the same commit");
                    }
                    finally { listener.Stop(); }
                }
            });
        }
    }
}
