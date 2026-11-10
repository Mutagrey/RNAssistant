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
using RNAssistant.Application.Conversations;
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
        private static AgentConversationScope OverviewScope(ChatStore store, ConversationInboxService inbox,
            ChatSession source, AppDataPaths paths, Action loaded = null, Action started = null)
        {
            var leases = ConversationLeaseService.ForOffice(paths);
            return new AgentConversationScope { Key = AgentScopeKey(source),
                Contains = current => AgentScopeKey(current) == AgentScopeKey(source),
                Catalog = new ConversationCatalogService(store, new ChatBlobStore(paths)),
                ReadCatalog = () => store.ListHeaders(false).Where(header => header.Host == source.Host &&
                    header.DocumentKey == source.DocumentKey && header.WorkspaceId == source.WorkspaceId)
                    .Select(header => new AgentConversationEndpoint { Address = new AgentChatAddress { ChatId = header.Id,
                        Title = header.Title, Source = source.WorkspaceId == null ? "office" : "workspace", WorkspaceId = source.WorkspaceId },
                        Inbox = inbox, Reserve = () => leases.Reserve(header.Id),
                        Load = () => { loaded?.Invoke(); return store.Load(header.Host, header.DocumentKey, header.Id); },
                        Start = _ => started?.Invoke() }).ToArray() };
        }

        private static ToolHandlerContext OverviewContext(string id, bool workspace = false, string representation = "text")
        {
            var args = new Dictionary<string, object> { ["target"] = ConversationCatalogAccess.TargetPrefix + AgentChatAddress.IdFor(id),
                ["representation"] = representation };
            if (workspace) args["type"] = "chat";
            return new ToolHandlerContext(new ToolRuntimeFixture().Context(toolId: "core.read"), args);
        }

        private static void ChatOverviewBoundedStorage()
        {
            WithTempPaths(paths => {
                var store = new ChatStore(paths); var source = GroupChat(store, "Organizer"); var target = GroupChat(store, "Chess");
                var user = target.Messages[0];
                var checkpoint = new ContextCheckpoint { Claims = new List<StructuredContextClaim> {
                    new StructuredContextClaim { ClaimId = "topic", Kind = "constraint", Text = "Build a chess game.",
                        SourceRoles = new List<string> { "user" }, SourceMessageIds = new List<string> { user.Id } } } };
                var artifact = store.DocumentArtifacts.PublishContext(target, checkpoint, 0);
                checkpoint.SharedResource = ChatResourceUri.CreateArtifactRevision(target, artifact);
                checkpoint.SummaryMarkdown = "PRIVATE COMPACTION";
                target.ContextCheckpoints.Add(checkpoint); target.ActiveContextCheckpointId = checkpoint.Id;
                target.Messages.Add(new ChatMessage { Role = "system", Content = "PRIVATE SYSTEM" });
                target.Messages.Add(new ChatMessage { Role = "developer", Content = "PRIVATE DEVELOPER" });
                target.Messages.Add(new ChatMessage { Role = "user", ProtocolMessage = true, Content = "PRIVATE TOOL RESULT" });
                for (var i = 0; i < 80; i++) target.Messages.Add(new ChatMessage { Role = "user", Content = "Turn " + i + new string('x', 1000) });
                target.Messages.Add(new ChatMessage { Role = "user", Content = "OVERSIZED" + new string('x', 100000) });
                target.Messages.Add(new ChatMessage { Role = "assistant", ProtocolMessage = true,
                    ResponseProtocolVersion = ConversationResponse.ProtocolVersion, ResponseModelAttemptId = "attempt-final",
                    Content = ModelProtocolWire.Write(new ConversationResponse(ConversationResponse.FinishAction,
                        "PRIVATE REASON", "Chess board ready.", results: new ConversationResultClaim[0], outcome: ConversationOutcome.Completed)) });
                store.Save(target);
                var before = store.Load(target.Id).Revision;
                var overview = store.ReadChatOverview(source, target.Id);
                var builds = store.SqliteProjectionFullBuildCount;
                var again = store.ReadChatOverview(source, target.Id);
                AssertTrue(overview.Excerpts.Count == 6 && overview.SampledMessages <= 36 && overview.SkippedOversizedMessages == 1,
                    "overview bounds selected rows, large payloads and delivered excerpts");
                var json = JsonConvert.SerializeObject(overview);
                AssertContains(json, "Chess board ready.", "only visible v8 finish text is projected");
                AssertTrue(!json.Contains("PRIVATE") && !json.Contains("OVERSIZED") && !json.Contains(target.Id),
                    "no instructions, tool result, reasoning, local summary or internal identity leaks");
                AssertTrue(overview.SummaryClaims.Single() == "Build a chess game." && overview.SummaryStatus == "published_excerpt",
                    "only explicit shared publication is used");
                AssertEqual(json, JsonConvert.SerializeObject(again), "unchanged source produces stable projection");
                AssertEqual(builds, store.SqliteProjectionFullBuildCount, "warm read does not rebuild the whole history");
                AssertEqual(before, store.Load(target.Id).Revision, "overview does not change target events");
                var inbox = new ConversationInboxService(EventStore(store)); var agents = new AgentCommunicationService();
                agents.Register(OverviewScope(store, inbox, source, paths));
                var access = new ConversationCatalogAccess(agents, source, 8);
                var bounded = access.Read(OverviewContext(target.Id), 2400, CancellationToken.None);
                var boundedText = (string)JObject.Parse(bounded.Result.DataJson)["text"];
                AssertTrue(boundedText.Length <= 2400 - 1024 && (bool)JObject.Parse(boundedText)["budgetTruncated"],
                    "model budget truncates complete excerpt records and reports it");
                var selectors = OverviewContext(target.Id); selectors.Arguments["startChar"] = 0;
                AssertEqual("chat_overview_invalid", RuntimeThrows<ResourceRequestException>(() =>
                    access.Read(selectors, 10000, CancellationToken.None)).ErrorCode, "overview is not a backdoor to arbitrary history paging");
                var reopened = new ChatStore(paths).ReadChatOverview(source, target.Id);
                AssertEqual(json, JsonConvert.SerializeObject(reopened), "overview is derived after restart");
                var legacy = new ChatSession { Host = source.Host, DocumentKey = source.DocumentKey,
                    DocumentAuthorityId = source.DocumentAuthorityId, Title = "Legacy", ReferenceContractVersion = 2 };
                legacy.Messages.Add(new ChatMessage { Role = "user", Content = "OLD HISTORY" }); store.Save(legacy);
                var incompatible = store.ReadChatOverview(source, legacy.Id);
                AssertTrue(!incompatible.ProtocolCompatible && incompatible.Excerpts.Count == 0 && incompatible.SummaryClaims.Count == 0,
                    "unsupported stream exposes metadata without interpreting history");
            });
        }

        private static void ChatOverviewAddressedGuards()
        {
            WithTempPaths(paths => {
                var store = new ChatStore(paths); var a = GroupChat(store, "Organizer"); var b = GroupChat(store, "Chess");
                var foreign = GroupChat(store, "Private", "other-doc"); var loaded = 0; var started = 0;
                var inbox = new ConversationInboxService(EventStore(store)); var enabled = true;
                var agents = new AgentCommunicationService(() => enabled ? 8 : 0);
                agents.Register(OverviewScope(store, inbox, a, paths, () => loaded++, () => started++));
                var access = new ConversationCatalogAccess(agents, a, 8); var id = AgentChatAddress.IdFor(b.Id);
                AssertEqual(2, access.Find(null).Items.Count, "catalog is scoped and includes unopened chats");
                var dispatches = 0;
                AssertEqual("chat_rename_unobserved", RuntimeThrows<ResourceRequestException>(() => access.Rename(id, "Changed", () => dispatches++)).ErrorCode,
                    "rename requires explicit prior observation");
                var read = access.Read(OverviewContext(b.Id), 10000, CancellationToken.None);
                var evidence = read.ResourceEvidence.Single();
                AssertTrue(evidence.Immutable && evidence.Payload != null && evidence.Complete &&
                    !(bool)JObject.Parse(read.Result.DataJson)["sourceComplete"], "evidence proves only the immutable overview, not full history");
                AssertTrue(loaded == 0 && started == 0, "catalog and overview do not load an execution or start any agent");
                AssertEqual("chat_unavailable", RuntimeThrows<ResourceRequestException>(() => access.Read(OverviewContext(foreign.Id), 10000, CancellationToken.None)).ErrorCode,
                    "known address is not cross-document access");
                using (ConversationLeaseService.ForOffice(paths).Reserve(b.Id))
                    AssertEqual("chat_busy", RuntimeThrows<ResourceRequestException>(() => access.Rename(id, "Changed", () => dispatches++)).ErrorCode,
                        "live/external lease rejects a rename without waiting");
                b.Messages.Add(new ChatMessage { Role = "user", Content = "Concurrent input" }); store.Save(b);
                AssertEqual("chat_rename_stale", RuntimeThrows<ResourceRequestException>(() => access.Rename(id, "Changed", () => dispatches++)).ErrorCode,
                    "a new message invalidates the observed source revision");
                AssertEqual(0, dispatches, "all preflight refusals are no effect");
                access.Read(OverviewContext(b.Id, representation: "metadata"), 10000, CancellationToken.None);
                var changed = access.Rename(id, "Chess game", () => dispatches++);
                var saved = store.Load(b.Id);
                AssertTrue(changed.Changed && saved.TitleAssignedByAgent && saved.Title == "Chess game" && saved.Messages.Count == b.Messages.Count,
                    "only exact metadata changes; concurrent input is preserved");
                AssertEqual("Chess game", store.ListHeaders(false).Single(header => header.Id == b.Id).Title, "catalog sees committed metadata");
                AssertEqual(1, store.Load(a.Id).Messages.Count, "target history is not merged into source");
                access.Read(OverviewContext(b.Id), 10000, CancellationToken.None);
                var entry = ChatRenameToolHandler.Entry(); var registry = new ToolHandlerRegistry();
                registry.Register(ToolPackSnapshot.Capture(new ToolDescriptor(entry.Id, entry.Description, entry.ArgumentSchemaJson),
                    entry.Policy, entry.Binding), new ChatRenameToolHandler(a, access.Rename));
                var runtime = new ToolRuntime(registry, ChatModes.Agent, false, false);
                var call = new ToolCall("rename-fault", entry.Id, new JObject { ["title"] = "Committed before fault", ["agent_id"] = id }.ToString());
                var writes = 0;
                store.AppendFaultPoint = point => { if (point == "after-record") { writes++; throw new IOException("Injected append fault"); } };
                ToolExecutionRecord failed;
                try { failed = runtime.ExecuteAsync(new ToolExecutionContext(call, runtime.Describe(call), "run", "turn", "step",
                    DateTime.UtcNow, false, 5), CancellationToken.None).GetAwaiter().GetResult(); }
                finally { store.AppendFaultPoint = null; }
                AssertTrue(failed.Outcome == ToolExecutionOutcome.Unknown && failed.MayHaveDispatched && writes == 1,
                    "post-dispatch failure stays unknown, without automatic replay or a no-effect claim");
                AssertEqual("Committed before fault", store.Load(b.Id).Title, "unknown response does not roll back durable title metadata");
                enabled = false;
                RuntimeThrows<AgentCommunicationException>(() => access.Find(null));
                enabled = true;
                store.Delete(b.Host, b.DocumentKey, b.Id);
                AssertEqual("chat_unavailable", RuntimeThrows<ResourceRequestException>(() => access.Read(OverviewContext(b.Id), 10000, CancellationToken.None)).ErrorCode,
                    "deleted target is not recreated");
                AssertEqual(0, started, "organizing never wakes target agents");
            });
        }

        private static void ChatOverviewOfficeKernel()
        {
            WithTempExecutor((executor, adapter) => {
                var paths = FixturePaths.Value; var store = new ChatStore(paths);
                var source = AgentChat(store, "Organizer"); var target = AgentChat(store, "Other");
                executor.BindResourceAuthority(source); executor.BindResourceAuthority(target); store.Save(target);
                source.Mode = ChatModes.Plan; source.MutationPolicy = MutationPolicy.Deny; store.Save(source);
                var inbox = new ConversationInboxService(EventStore(store)); var agents = new AgentCommunicationService();
                agents.Register(OverviewScope(store, inbox, source, paths));
                executor.AgentCommunication = agents; executor.AgentInbox = inbox;
                var step = 0; var id = AgentChatAddress.IdFor(target.Id);
                var calls = new[] {
                    new ConversationToolCall { Name = "core.find", Arguments = new JObject { ["scope"] = "chats", ["query"] = id } },
                    new ConversationToolCall { Name = "core.read", Arguments = new JObject { ["target"] = ConversationCatalogAccess.TargetPrefix + id } },
                    new ConversationToolCall { Name = "chat.rename", Arguments = new JObject { ["title"] = "Chess research", ["agent_id"] = id } }
                };
                LlmCompletionDelegate complete = (config, messages, options, stream, token) => {
                    var index = step++;
                    AssertTrue(index <= calls.Length, "overview requires no repair calls");
                    if (index == 2) AssertContains(FlattenSimple(messages), "historyComplete", "bounded overview reaches the model context");
                    var response = index < calls.Length ? ModelProtocolWire.WriteAcceptedCall("Organize chats", null, calls[index]) :
                        ScriptedVerifiedFinish(source, "Renamed", "Готово.", "Renamed chat", "chat.rename");
                    return Task.FromResult(new LlmCompletionResult { Content = response });
                };
                var settings = new AppSettings { AutoCompressContext = false, SmartChatTitles = false, StreamResponses = false, AgentResponseMode = "json_object" };
                new ConversationRunService(adapter, executor, ConversationStore(store), EventStore(store), complete, null, inbox: inbox)
                    .ExecuteAsync(ChatModes.Plan, "Организуй чаты", source, NewContext(adapter), settings,
                        executor.GetControllerTools().ToList(), null, null, null, null, CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual("Chess research", store.Load(target.Id).Title, "native core.find/read and addressed rename share one run owner");
                AssertEqual(4, step, "three requested tools and finish use no background summary calls");
                var saved = store.Load(source.Id);
                var retained = saved.Messages.SelectMany(message => message.ResourceEvidence ?? new List<ResourceEvidence>())
                    .First(item => item.Resource.Uri.Contains("/overview/"));
                AssertTrue(retained.Immutable && retained.Payload != null, "exact overview evidence survives kernel persistence/reopen");
                CasService(paths, store, new VbaJournalStore(paths), () => StorageProtector.None).Collect();
                AssertTrue(new ChatBlobStore(paths).HasVerifiedReference(retained.Payload.ToBlobReference()),
                    "the accepted evidence retains its overview payload through CAS collection");
            });
        }

        private static void ChatOverviewWorkspaceKernel()
        {
            WithTempPaths(paths => {
                var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
                using (var listener = new HttpListener())
                using (var service = new WorkspaceConversationService(paths))
                {
                    var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "overview"));
                    var source = service.CreateSession(workspace, "Organizer"); var target = service.CreateSession(workspace, "Chess");
                    var store = new ChatStore(paths); var id = AgentChatAddress.IdFor(target.Id);
                    listener.Prefixes.Add("http://127.0.0.1:" + port + "/"); listener.Start();
                    var server = Task.Run(async () => {
                        var calls = new[] {
                            new ConversationToolCall { Name = "core.find", Arguments = new JObject { ["type"] = "chat", ["query"] = id } },
                            new ConversationToolCall { Name = "core.read", Arguments = new JObject { ["type"] = "chat", ["target"] = ConversationCatalogAccess.TargetPrefix + id } },
                            new ConversationToolCall { Name = "chat.rename", Arguments = new JObject { ["title"] = "Chess rules", ["agent_id"] = id } }
                        };
                        for (var step = 0; step <= calls.Length; step++)
                        {
                            var context = await listener.GetContextAsync();
                            var content = step < calls.Length ? ModelProtocolWire.WriteAcceptedCall("Organize chats", null, calls[step]) :
                                ScriptedVerifiedFinish(store.Load(source.Id), "Renamed", "Готово.", "Renamed chat", "chat.rename");
                            var response = new JObject { ["choices"] = new JArray(new JObject { ["message"] = new JObject { ["content"] = content } }) }.ToString();
                            var bytes = Encoding.UTF8.GetBytes(response); context.Response.ContentType = "application/json";
                            context.Response.ContentLength64 = bytes.Length; await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                            context.Response.Close();
                        }
                    });
                    try
                    {
                        var settings = new AppSettings { BaseUrl = "http://127.0.0.1:" + port, Model = "fixture",
                            StreamResponses = false, AutoCompressContext = false, SmartChatTitles = false };
                        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
                        {
                            var result = service.SubmitInputAsync(workspace, new WorkspaceSubmitInputRequest {
                                SessionId = source.Id, Message = "Организуй чаты", Mode = ChatModes.Plan
                            }, settings, () => "test-key", cancellationToken: timeout.Token).GetAwaiter().GetResult();
                            AssertEqual(RunLifecycle.Completed, result.Summary.Lifecycle, "workspace overview flow completes");
                        }
                        server.GetAwaiter().GetResult();
                        AssertEqual("Chess rules", service.ListSessions(workspace).Single(chat => chat.Id == target.Id).Title,
                            "workspace routes the same discovery/read/rename contract");
                        AssertTrue(store.Load(target.Id).LastRun == null, "reading and renaming never runs the target");
                    }
                    finally { listener.Stop(); }
                }
            });
        }
    }
}
