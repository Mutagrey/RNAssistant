using System;
using System.Collections.Generic;
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
using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Runtime;
using RNAssistant.Runtime.Contracts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void WorkspaceDiscoveryAdmitsAndRestoresExactTools()
        {
            WithTempPaths(paths =>
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
                using (var listener = new HttpListener())
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
                {
                    listener.Prefixes.Add("http://127.0.0.1:" + port + "/"); listener.Start();
                    var requests = new List<JObject>();
                    string sessionId = null;
                    var server = Task.Run(async () =>
                    {
                        try
                        {
                            for (var index = 0; index < 5; index++)
                            {
                                var context = await listener.GetContextAsync().ConfigureAwait(false);
                                using (var reader = new StreamReader(context.Request.InputStream))
                                    requests.Add(JObject.Parse(await reader.ReadToEndAsync().ConfigureAwait(false)));
                                string content;
                                if (index == 0)
                                    content = new ConversationResponse(ConversationResponse.ExecuteAction,
                                        "Discover exact capabilities.", "Inspect the catalog.", calls: new[] {
                                            new ConversationToolCall { Name = CapabilityToolCatalog.SearchToolId,
                                                Arguments = new JObject { ["query"] = "fixture" } },
                                            new ConversationToolCall { Name = CapabilityToolCatalog.ReadToolId,
                                                Arguments = new JObject { ["id"] = "fixture.extra" } },
                                            new ConversationToolCall { Name = CapabilityToolCatalog.ReadToolId,
                                                Arguments = new JObject { ["id"] = "fixture.special" } }
                                        }).ToJson();
                                else if (index == 1)
                                    content = new ConversationResponse(ConversationResponse.ExecuteAction,
                                        "Use the admitted exact tool.", "Read the fixture.", calls: new[] {
                                            new ConversationToolCall { Name = "fixture.extra", Arguments = new JObject() }
                                        }).ToJson();
                                else if (index == 2)
                                    content = ScriptedVerifiedFinish(new ChatStore(paths).Load(sessionId),
                                        "Report the completed inspection.", "Inspected.", "The read completed.",
                                        CapabilityToolCatalog.SearchToolId, CapabilityToolCatalog.ReadToolId,
                                        CapabilityToolCatalog.ReadToolId, "fixture.extra");
                                else
                                    content = ModelProtocolWire.WriteAcceptedDecision(ConversationResponse.FinishAction,
                                        "Answer from accepted history.", "History inspected.", results: new ConversationResultClaim[0], outcome: ConversationOutcome.Completed);
                                var response = new JObject { ["choices"] = new JArray(new JObject {
                                    ["message"] = new JObject { ["content"] = "{\"response\":" + content + "}" } }) };
                                var bytes = Encoding.UTF8.GetBytes(response.ToString(Newtonsoft.Json.Formatting.None));
                                context.Response.ContentType = "application/json";
                                context.Response.ContentLength64 = bytes.Length;
                                await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                                context.Response.Close();
                            }
                        }
                        catch (HttpListenerException) { }
                        catch (ObjectDisposedException) { }
                    });
                    try
                    {
                        var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "discovery"));
                        var settings = new AppSettings { BaseUrl = "http://127.0.0.1:" + port, Model = "test-model",
                            StreamResponses = false, AutoCompressContext = false, SmartChatTitles = false,
                            AgentResponseMode = AgentResponseModes.JsonSchema, ContextWindowOverrideTokens = 32768 };
                        var profile = CallableToolProfile.Explicit("discovery-fixture", new[] {
                            CapabilityToolCatalog.SearchToolId, CapabilityToolCatalog.ReadToolId,
                            "common.resources_find", "common.resources_read", "common.skills_load" });
                        var binding = "fixture.v1"; var executions = 0;
                        Func<WorkspaceDescriptor, IReadOnlyList<WorkspaceToolContribution>> contributions = _ => new[] {
                            new WorkspaceToolContribution { Entry = PackageTool("fixture.extra", ToolExposure.Direct, binding),
                                Handler = new DiscoveryFixtureRead(() => executions++) },
                            new WorkspaceToolContribution { Entry = PackageTool("fixture.special", ToolExposure.Skill),
                                Handler = new DiscoveryFixtureRead(() => { throw new InvalidOperationException("Skill-only tool must stay hidden."); }) }
                        };
                        for (var turn = 0; turn < 3; turn++)
                        {
                            if (turn == 2) binding = "fixture.v2";
                            using (var service = new WorkspaceConversationService(paths, extraTools: contributions, toolProfile: profile))
                            {
                                if (turn == 0) sessionId = service.CreateSession(workspace, "Discovery").Id;
                                var result = service.SubmitInputAsync(workspace, new WorkspaceSubmitInputRequest {
                                    SessionId = sessionId, Message = "Inspect the fixture", Mode = ChatModes.Agent
                                }, settings, () => "test-key", cancellationToken: timeout.Token).GetAwaiter().GetResult();
                                AssertEqual(RunLifecycle.Completed, result.Summary.Lifecycle, "workspace discovery turn completes: " + turn);
                            }
                        }
                        server.GetAwaiter().GetResult();
                        AssertEqual(5, requests.Count, "bounded discovery, execution, restart and drift requests");
                        Func<int, string, bool> offered = (index, id) => ((JContainer)requests[index]["response_format"]["json_schema"]["schema"])
                            .DescendantsAndSelf().OfType<JValue>().Any(value => value.Type == JTokenType.String && (string)value == id);
                        AssertTrue(!offered(0, "fixture.extra"), "optional schema is absent before discovery");
                        AssertTrue(offered(1, "fixture.extra"), "successful exact read admits schema on the next request");
                        AssertTrue(requests[1]["messages"].ToString().Contains("schemaInRequest"), "shared projection removes duplicate admitted schema");
                        var readReceipts = requests[1]["messages"].Select(message => (string)message["content"])
                            .Where(content => content != null && content.StartsWith("TOOL_RESULT:\n", StringComparison.Ordinal))
                            .Select(content => JObject.Parse(content.Substring("TOOL_RESULT:\n".Length)))
                            .Where(result => (string)result["name"] == CapabilityToolCatalog.ReadToolId).ToArray();
                        AssertEqual(2, readReceipts.Length, "both schema reads have model receipts");
                        AssertTrue(readReceipts.All(result => result["data"]["loaded"] == null && result["data"]["revision"] == null),
                            "runtime pins and loaded flags never become model selection authority");
                        AssertTrue(offered(3, "fixture.extra"), "exact admission survives reopening and the next turn");
                        AssertTrue(!offered(4, "fixture.extra"), "binding drift invalidates admission before dispatch");
                        AssertTrue(requests[4]["messages"].ToString().Contains("TOOL_PACK_RESTORE_STATE"), "drift is explicit");
                        AssertTrue(Enumerable.Range(0, 5).All(index => !offered(index, "fixture.special")), "schema read cannot admit skill-only tools");
                        AssertEqual(1, executions, "restart and drift never replay effects");
                        var session = new ChatStore(paths).Load(sessionId);
                        AssertEqual(1, new ChatStore(paths).ReadEvents(session.Host, session.DocumentKey, session.Id)
                            .Count(item => item.Type == SessionEventTypes.ToolPackExtensionAccepted), "one canonical admission event");
                    }
                    finally { listener.Stop(); }
                }
            });
        }

        private sealed class DiscoveryFixtureRead : IReadOnlyToolHandler
        {
            private readonly Action _execute;
            internal DiscoveryFixtureRead(Action execute) { _execute = execute; }
            public Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested(); _execute();
                return Task.FromResult(new ToolHandlerResult(ToolResult.Ok("Read complete.", "{\"complete\":true}"), ToolEffectEvidence.None));
            }
        }
    }
}
