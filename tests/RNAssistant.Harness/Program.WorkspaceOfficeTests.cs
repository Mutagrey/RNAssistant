using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Office;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Runtime;
using RNAssistant.Runtime.Contracts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void WorkspaceOfficeTargetStaysPinned()
        {
            var directFind = JObject.Parse(ResourceFindToolHandler.Descriptor.ParametersJson);
            var boundFind = ResourceModelFindSchema.BoundOffice();
            AssertTrue(JToken.DeepEquals(directFind["properties"]["query"], boundFind["properties"]["query"]),
                "direct Office and workspace Office discovery share the query limit and description");
            AssertEqual("document", (string)boundFind["properties"]["scope"]["default"],
                "workspace Office discovery defaults to the pinned document");
            string schemaError;
            AssertTrue(ToolSchemaSupport.ValidateArguments(new JObject { ["type"] = "office", ["scope"] = "vba",
                ["query"] = new string('x', 500) }, boundFind, false, out schemaError),
                "workspace Office discovery accepts the shared maximum query in a bound scope: " + schemaError);
            AssertTrue(!ToolSchemaSupport.ValidateArguments(new JObject { ["type"] = "office",
                ["query"] = new string('x', 501) }, boundFind, false, out schemaError),
                "workspace Office discovery rejects queries longer than the direct Office limit");
            AssertTrue(!ToolSchemaSupport.ValidateArguments(new JObject { ["type"] = "office", ["scope"] = "all" },
                boundFind, false, out schemaError),
                "workspace Office discovery cannot escape the pinned document scopes");
            AssertTrue(ToolSchemaSupport.ValidateArguments(new JObject { ["scope"] = "all" }, directFind,
                false, out schemaError), "direct Office discovery keeps its broader scopes: " + schemaError);
            WithTempPaths(paths =>
            {
                var adapter = new FakeOfficeAdapter();
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths),
                    new SkillStore(paths), new ToolStore(paths), paths: paths);
                var office = new WorkspaceOfficeCapabilityService(adapter, executor, paths);
                {
                    var target = office.CaptureCurrentTarget();
                    AssertTrue(target != null && !string.IsNullOrWhiteSpace(target.DocumentAuthorityId),
                        "workspace run captures exact Office authority");
                    var session = new ChatSession { Id = "workspace-office-test", WorkspaceId = "ws-test",
                        Messages = new List<ChatMessage>() };
                    var initial = office.ForTarget(target, session, new AppSettings(), "run-one");
                    AssertTrue(initial.Available && initial.Tools.Any(item => item.Entry.Id == "excel.inspect") &&
                        initial.Tools.Any(item => item.Entry.Id == "excel.write_range") &&
                        initial.Tools.All(item => item.Entry.Id != "excel.create_chat_chart"),
                        "workspace run receives document tools without chat-owned chart artifacts");
                    AssertTrue(ReferenceEquals(ModelToolResultProjection.Instance, initial.ModelProjection),
                        "workspace Office capabilities carry the same model result projection as Office chat");
                    var registry = RegisterOfficeContributions(initial);
                    var runtime = new ToolRuntime(registry, "agent", true, true,
                        mutationObserver: initial.MutationObserver);
                    var liveWriteCall = new ToolCall("live-write", "excel.write_range",
                        "{\"kind\":\"value\",\"sheet\":\"Data\",\"address\":\"C1\",\"value\":\"42\"}");
                    var liveWrite = runtime.ExecuteAsync(new ToolExecutionContext(liveWriteCall, runtime.Describe(liveWriteCall),
                        "run-one", "run-one", "step", DateTime.UtcNow, false, 1), CancellationToken.None)
                        .GetAwaiter().GetResult();
                    AssertEqual(ToolExecutionOutcome.Ok, liveWrite.Outcome,
                        "workspace document mutation uses the Office handler and authority observer");
                    var liveCall = new ToolCall("live-resource", "common.resources_read",
                        "{\"target\":\"Excel range: Data!A1:B2\",\"representation\":\"text\"}");
                    var liveExecution = new ToolExecutionContext(liveCall,
                        new ToolPolicySnapshot(liveCall.Name, "test", ResourceReadToolHandler.Policy),
                        "run-one", "run-one", "step", DateTime.UtcNow, false, 1);
                    var live = initial.ResourceRead.ExecuteAsync(new ToolHandlerContext(liveExecution,
                        new Dictionary<string, object> { ["target"] = "Excel range: Data!A1:B2",
                            ["representation"] = "text" }), CancellationToken.None).GetAwaiter().GetResult();
                    AssertEqual(ToolResultStatus.Ok, live.Result.Status,
                        "workspace can read the pinned Office document");

                    adapter.DocumentKeyValue = "another-document";
                    adapter.RuntimeDocumentKeyValue = "another-runtime";
                    var changed = office.ForTarget(target, session, new AppSettings(), "run-one");
                    AssertTrue(!changed.Available, "switching documents does not retarget the run");
                    AssertTrue(initial.Tools.Select(item => item.Entry.Id).OrderBy(id => id).SequenceEqual(
                        changed.Tools.Select(item => item.Entry.Id).OrderBy(id => id)),
                        "document switch preserves the accepted Office catalog");
                    var inspect = changed.Tools.Single(item => item.Entry.Id == "excel.inspect");
                    var call = new ToolCall("pinned-read", inspect.Entry.Id, "{\"kind\":\"workbook\"}");
                    var execution = new ToolExecutionContext(call,
                        new ToolPolicySnapshot(call.Name, "test", inspect.Entry.Policy),
                        "run-one", "run-one", "step", DateTime.UtcNow, false, 1);
                    var read = inspect.Handler.ExecuteAsync(new ToolHandlerContext(execution,
                        new Dictionary<string, object> { ["kind"] = "workbook" }), CancellationToken.None)
                        .GetAwaiter().GetResult();
                    AssertEqual(ToolResultStatus.Error, read.Result.Status,
                        "Office read refuses the replacement document");
                    AssertTrue(read.Result.DataJson.Contains("active_document_changed"),
                        "Office read reports exact target mismatch");
                    var resourceCall = new ToolCall("pinned-resource", "common.resources_read",
                        "{\"target\":\"Excel range: Data!A1:B2\",\"representation\":\"text\"}");
                    var resourceExecution = new ToolExecutionContext(resourceCall,
                        new ToolPolicySnapshot(resourceCall.Name, "test", ResourceReadToolHandler.Policy),
                        "run-one", "run-one", "step", DateTime.UtcNow, false, 1);
                    var resource = changed.ResourceRead.ExecuteAsync(new ToolHandlerContext(resourceExecution,
                        new Dictionary<string, object> { ["target"] = "Excel range: Data!A1:B2",
                            ["representation"] = "text" }), CancellationToken.None).GetAwaiter().GetResult();
                    AssertEqual(ToolResultStatus.Error, resource.Result.Status,
                        "semantic Office read refuses the replacement document");

                    var foreignAdapter = FakeOfficeAdapter.ForHost("Word");
                    var foreignExecutor = new OfficeToolExecutor(foreignAdapter, new VbaJournalStore(paths),
                        new SkillStore(paths), new ToolStore(paths), paths: paths);
                    var foreign = new WorkspaceOfficeCapabilityService(foreignAdapter, foreignExecutor, paths);
                    {
                        var switched = foreign.ForTarget(target, session, new AppSettings(), "run-one");
                        AssertTrue(!switched.Available && switched.Tools.Any(item => item.Entry.Id == "excel.inspect"),
                            "host switch preserves the pending run's Office catalog");
                        RegisterOfficeContributions(switched);
                        var refused = switched.Tools.Single(item => item.Entry.Id == "excel.inspect").Handler
                            .ExecuteAsync(new ToolHandlerContext(execution,
                                new Dictionary<string, object> { ["kind"] = "workbook" }), CancellationToken.None)
                            .GetAwaiter().GetResult();
                        AssertEqual(ToolResultStatus.Error, refused.Result.Status,
                            "host switch refuses an Office read before dispatch");
                        var write = switched.Tools.Single(item => item.Entry.Id == "excel.write_range");
                        var writeCall = new ToolCall("pinned-write", write.Entry.Id, "{}");
                        var writeExecution = new ToolExecutionContext(writeCall,
                            new ToolPolicySnapshot(writeCall.Name, "test", write.Entry.Policy),
                            "run-one", "run-one", "step", DateTime.UtcNow, true, 1);
                        var rejected = false;
                        try { switched.MutationObserver.Prepare(writeExecution, new Dictionary<string, object>()); }
                        catch (ToolMutationPreparationException error)
                        { rejected = error.Code == "active_document_changed"; }
                        AssertTrue(rejected, "confirmed write cannot prepare against another host");
                    }
                }
            });
        }

        private sealed class WorkspaceOfficeProjectionProbe : IWorkspaceOfficeCapabilities
        {
            private readonly bool _omitProjection;
            public WorkspaceOfficeProjectionProbe(bool omitProjection = false)
            { _omitProjection = omitProjection; }

            public WorkspaceOfficeRunTarget CaptureCurrentTarget()
            {
                return new WorkspaceOfficeRunTarget { Host = "Word", DocumentKey = "document",
                    RuntimeDocumentKey = "runtime", DocumentAuthorityId = "authority", DocumentTitle = "Document" };
            }

            public WorkspaceOfficeCapabilitySet ForTarget(WorkspaceOfficeRunTarget target,
                ChatSession session, AppSettings settings, string runId)
            {
                return new WorkspaceOfficeCapabilitySet { Target = target, Available = true,
                    ModelProjection = _omitProjection ? null : ModelToolResultProjection.Instance,
                    Tools = new[] { new WorkspaceToolContribution {
                        Entry = OfficeToolCatalog.ForHost("Word").Single(item => item.Id == WordToolIds.ReplaceText),
                        Handler = new RuntimeTestHandler()
                    } } };
            }
        }

        private static void WorkspaceOfficeHistoryUsesHostProjection()
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
                                var reply = new JObject { ["choices"] = new JArray(new JObject {
                                    ["message"] = new JObject { ["content"] =
                                        ModelProtocolWire.WriteAcceptedDecision(ConversationResponse.FinishAction,
                                            "Answer from the retained Office result.", "Done.",
                                            results: new ConversationResultClaim[0]) } }) };
                                var bytes = Encoding.UTF8.GetBytes(reply.ToString(Formatting.None));
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
                        var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "office-projection"));
                        var settings = new AppSettings { BaseUrl = "http://127.0.0.1:" + port,
                            Model = "test-model", StreamResponses = false, AutoCompressContext = false,
                            SmartChatTitles = false, ToolResultRole = ToolResultRoles.User };
                        var office = new WorkspaceOfficeProjectionProbe();
                        string sessionId;
                        using (var service = new WorkspaceConversationService(paths, officeCapabilities: office))
                        {
                            var session = service.CreateSession(workspace, "Office result history");
                            sessionId = session.Id;
                            var original = new ChatMessage { Role = "user", Content = "Replace text", RunId = "prior-run" };
                            var call = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall {
                                Id = "prior-call", Name = WordToolIds.ReplaceText,
                                Arguments = new Dictionary<string, object> { ["find"] = "old", ["replace"] = "new" }
                            }, "Replacing.", "Apply the requested replacement.", null,
                                ToolResultRoles.User, FixtureCallOrigin("prior-step"));
                            call.RunId = "prior-run";
                            var result = AgentJsonProtocol.CreateToolResultMessage(new ToolInvocation {
                                ToolCallId = "prior-call", ToolId = WordToolIds.ReplaceText
                            }, ToolResult.Ok("Replaced.", "{\"replacements\":2,\"scopeSha256\":\"private-scope-hash\"}"), ToolResultRoles.User);
                            result.RunId = "prior-run";
                            AssertTrue(new ModelResultProjection().Project(result).Content.Contains("scopeSha256"),
                                "the generic workspace projection would expose the Office-only scope field");
                            session.Messages.AddRange(new[] { original, call, result });
                            new ChatStore(paths).Commit(session, new ConversationCommit(session.Messages));
                            var rejectedInput = new WorkspaceSubmitInputRequest { SessionId = sessionId,
                                RequestId = "missing-projection", Message = "Do not send", Mode = ChatModes.Agent };
                            using (var incomplete = new WorkspaceConversationService(paths,
                                officeCapabilities: new WorkspaceOfficeProjectionProbe(true)))
                            {
                                RuntimeThrows<InvalidOperationException>(() => incomplete.SubmitInputAsync(workspace,
                                    rejectedInput, settings, () => "test-key").GetAwaiter().GetResult());
                                AssertTrue(incomplete.FindAcceptedInput(workspace, rejectedInput) == null,
                                    "missing host projection refuses before input acceptance");
                            }
                            var first = service.SubmitInputAsync(workspace, new WorkspaceSubmitInputRequest {
                                SessionId = sessionId, Message = "What changed?", Mode = ChatModes.Agent
                            }, settings, () => "test-key").GetAwaiter().GetResult();
                            AssertEqual(RunLifecycle.Completed, first.Summary.Lifecycle, "workspace turn reads saved Office result");
                        }
                        using (var reopened = new WorkspaceConversationService(paths, officeCapabilities: office))
                            reopened.SubmitInputAsync(workspace, new WorkspaceSubmitInputRequest {
                                SessionId = sessionId, Message = "Continue", Mode = ChatModes.Agent
                            }, settings, () => "test-key").GetAwaiter().GetResult();
                        server.GetAwaiter().GetResult();
                        var delivered = requests.Select(request => string.Join("\n",
                            ((JArray)JObject.Parse(request)["messages"]).Select(message => (string)message["content"]))).ToArray();
                        AssertTrue(delivered.All(content => content.Contains("replacements")) &&
                            requests.All(request => !request.Contains("scopeSha256") && !request.Contains("private-scope-hash")),
                            "workspace model requests use the Office projection before and after restart");
                        AssertTrue(new ChatStore(paths).Load(sessionId).Messages.Any(message =>
                            message.ToolCallId == "prior-call" && message.Content.Contains("scopeSha256")),
                            "durable Office result keeps diagnostic fields");
                    }
                    finally { listener.Stop(); }
                }
            });
        }

        private static ToolHandlerRegistry RegisterOfficeContributions(RNAssistant.Runtime.Contracts.WorkspaceOfficeCapabilitySet office)
        {
            var registry = new ToolHandlerRegistry();
            foreach (var contribution in office.Tools)
                registry.Register(ToolPackSnapshot.Capture(new ToolDescriptor(contribution.Entry.Id,
                    contribution.Entry.Description, contribution.Entry.ArgumentSchemaJson),
                    contribution.Entry.Policy, contribution.Entry.Binding), contribution.Handler);
            return registry;
        }
    }
}
