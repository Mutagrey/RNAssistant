using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Application.Contracts;
using RNAssistant.Application.Conversations;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Runtime;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static ConversationGroupService NativeGroups(AppDataPaths paths, ConversationJournal journal,
            ChatSession source, ResourceJournal resources)
        {
            return new ConversationGroupService(journal, DocumentArtifactStore.PlanScope(source), resources, resources,
                resources, new ChatBlobStore(paths), (session, context) => journal.MutationOrigin(session, context));
        }

        private static void JournalGroupGuards()
        {
            foreach (var office in new[] { true, false }) WithJournalInput(office, (paths, journal, source, scope) => {
                IConversationStore store = journal;
                var resources = new ResourceJournal(paths); var groups = NativeGroups(paths, journal, source, resources);
                var member = store.CreateTransient(source.Host, source.DocumentKey, source.DocumentTitle, "Member");
                store.Commit(member, new ConversationCommit());
                var sourceHead = source.StorageHeadHash; var memberHead = member.StorageHeadHash;
                var create = new ChatGroupChange { Action = ChatGroupAction.Create, Name = "Team" };
                var group = groups.Change(source, create, "create-team").Group;
                var catalog = groups.Catalog(store.ListHeaders(false)).Single();
                AssertTrue(catalog.Error == null && catalog.Groups.Single().Id == group.Id,
                    "native catalog uses a scope address, not a generated nonexistent chat ID");
                var written = resources.Journal.WrittenBytes;
                AssertTrue(!groups.Change(source, create, "create-team").Changed, "duplicate creation is read-only");
                AssertEqual(written, resources.Journal.WrittenBytes, "duplicate has no resource append");
                RuntimeThrows<ChatConcurrencyException>(() => groups.Change(source,
                    new ChatGroupChange { Action = ChatGroupAction.Create, Name = "team" }, "duplicate-name"));
                var added = groups.Change(source, new ChangeChatGroupRequest { SessionId = source.Id, WorkspaceId = source.WorkspaceId,
                    Action = ChatGroupAction.Add, GroupId = group.Id, Revision = group.Revision, Members = new[] { member.Id, member.Id } });
                AssertEqual(1, added.Scope.Groups.Single().Members.Count, "typed UI preserves one membership per chat");
                RuntimeThrows<ChatConcurrencyException>(() => groups.Change(source, new ChangeChatGroupRequest {
                    SessionId = source.Id, WorkspaceId = source.WorkspaceId, Action = ChatGroupAction.Rename,
                    GroupId = group.Id, Revision = group.Revision, Name = "stale" }));
                var current = added.Scope.Groups.Single();
                RuntimeThrows<ChatConcurrencyException>(() => groups.Change(source, new ChatGroupChange { Action = ChatGroupAction.Add,
                    GroupId = current.Id, ExpectedRevision = current.Revision, Members = new[] { "foreign-chat" } }, "foreign-member"));
                var foreign = JsonConvert.DeserializeObject<ChatSession>(JsonConvert.SerializeObject(source));
                foreign.DocumentAuthorityId = "foreign";
                RuntimeThrows<ChatConcurrencyException>(() => groups.Read(foreign));
                using (resources.AcquireScope(DocumentArtifactStore.PlanScope(source)))
                    RuntimeThrows<IOException>(() => groups.Change(source, new ChatGroupChange { Action = ChatGroupAction.Delete,
                        GroupId = current.Id, ExpectedRevision = current.Revision }, "leased-delete"));
                groups.Change(source, new ChatGroupChange { Action = ChatGroupAction.Delete, GroupId = current.Id,
                    ExpectedRevision = current.Revision }, "delete-team");
                RuntimeThrows<ChatConcurrencyException>(() => groups.Change(source, create, "create-team"));
                AssertEqual(0, groups.Read(source).Count, "tombstone hides only the group");
                AssertEqual(sourceHead, store.Load(source.Id).StorageHeadHash, "UI group operations do not rewrite source history");
                AssertEqual(memberHead, store.Load(member.Id).StorageHeadHash, "membership and group deletion do not mutate participant runs/history");
                AssertTrue(!Directory.EnumerateFiles(paths.ResourceAuthorityDirectory, "*.jsonl").Any(), "no retiring resource journal is created");
            });
        }

        private static void JournalGroupConsumers(bool loseAcknowledgement, bool deleteSource = false, bool deleteDocument = false, bool moveDocument = false, bool checkUsage = false)
        {
            foreach (var office in new[] { true, false }) WithJournalInput(office, (paths, journal, source, scope) => {
                IConversationStore store = journal;
                var resources = new ResourceJournal(paths); var bodies = new ChatBlobStore(paths);
                var groups = NativeGroups(paths, journal, source, resources);
                var member = store.CreateTransient(source.Host, source.DocumentKey, source.DocumentTitle, "Member");
                store.Commit(member, new ConversationCommit()); var memberHead = member.StorageHeadHash;
                var inbox = new ConversationInboxService(journal);
                var communication = new AgentCommunicationService(() => 2);
                communication.Register(new AgentConversationScope { Key = scope, Groups = groups,
                    Contains = chat => chat.Host == source.Host && chat.DocumentKey == source.DocumentKey,
                    ReadCatalog = () => { throw new Exception("Native group membership must use the scoped alias index."); } });
                var commands = new[] {
                    new JObject { ["action"] = "create", ["name"] = "Team" },
                    new JObject { ["action"] = "add", ["group"] = "Team", ["agent_ids"] = new JArray(AgentChatAddress.IdFor(member.Id)) },
                    new JObject { ["action"] = "add", ["group"] = "Team", ["agent_ids"] = new JArray(AgentChatAddress.IdFor(member.Id)) },
                    new JObject { ["action"] = "list" },
                    new JObject { ["action"] = "rename", ["group"] = "Team", ["name"] = "Renamed" },
                    new JObject { ["action"] = "remove", ["group"] = "Renamed", ["agent_ids"] = new JArray(AgentChatAddress.IdFor(member.Id)) },
                    new JObject { ["action"] = "delete", ["group"] = "Renamed" }
                };
                var settings = new AppSettings { Model = "journal-groups", MaxCollaborationAgents = 2, StreamResponses = false,
                    SmartChatTitles = false, AutoCompressContext = false, AutoConfirmToolActions = true,
                    ContextWindowOverrideTokens = 65536, RequestTimeoutSeconds = 5, MaxAgentFormatRetries = 0 };
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths), new ToolStore(paths),
                    () => settings, paths: paths, resourceAuthorityStore: resources, resourceMutationJournal: resources,
                    resourceOrigin: (session, context) => journal.MutationOrigin(session, context));
                executor.AgentCommunication = communication; executor.AgentInbox = inbox;
                var files = new WorkspaceFileService(paths, resources, resources, resources);
                var workspace = office ? null : new WorkspaceStore(paths).OpenConnected(source.WorkspaceId);
                var flushes = 0; var faulted = false;
                var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
                settings.BaseUrl = "http://127.0.0.1:" + ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
                var round = 0; var count = loseAcknowledgement ? 1 : commands.Length;
                using (var listener = new HttpListener())
                {
                    listener.Prefixes.Add(settings.BaseUrl + "/"); listener.Start();
                    var provider = Task.Run(async () => {
                        try
                        {
                            while (true)
                            {
                                var request = await listener.GetContextAsync().ConfigureAwait(false);
                                using (var reader = new StreamReader(request.Request.InputStream)) await reader.ReadToEndAsync().ConfigureAwait(false);
                                var number = ++round;
                                AssertTrue(number <= count + 3, "finish is bounded and never retries the mutation");
                                if (!loseAcknowledgement && number == 3)
                                    AssertEqual(member.Id, groups.Read(source).Single().Members.Single(), "model alias resolves to exact scoped membership");
                                if (!loseAcknowledgement && number == 7)
                                    AssertEqual(0, groups.Read(source).Single().Members.Count, "model remove changes only the requested membership");
                                if (number == 1 && loseAcknowledgement) resources.Journal.Fault = point => {
                                    if (point != "after_flush" || ++flushes != 4) return;
                                    faulted = true; resources.Journal.Fault = null; throw new IOException("lost group authority acknowledgement");
                                };
                                var response = number <= count ? ModelProtocolWire.WriteAcceptedCall("Update the requested group.", null,
                                    new ConversationToolCall { Name = AgentCommunicationToolCatalog.Group, Arguments = commands[number - 1] }) :
                                    loseAcknowledgement ? ModelProtocolWire.WriteAcceptedDecision("finish", "The write outcome is uncertain.", "Inspect before retrying.",
                                        outcome: ConversationOutcome.UnableToComplete, results: new[] {
                                            new ConversationResultClaim("Group write not proven.", new[] { "E1" }, ConversationAssessment.Unverified) }) :
                                    ScriptedVerifiedFinish(store.Load(source.Id), "Group operations verified.", "Done.", "Group removed.",
                                        Enumerable.Repeat(AgentCommunicationToolCatalog.Group, count).ToArray());
                                await SteeringReply(request, response).ConfigureAwait(false);
                            }
                        }
                        catch (HttpListenerException) { }
                        catch (ObjectDisposedException) { }
                        catch (Exception error) { Console.WriteLine(error); listener.Abort(); throw; }
                    });
                    try
                    {
                        var id = Guid.NewGuid().ToString("N");
                        source.LastRun = new ChatRunRecord { RunId = id, TurnId = id, StartedUtc = DateTime.UtcNow,
                            ConversationPolicy = ConversationRunPolicy.For(ChatModes.Agent, autoConfirmToolActions: true) };
                        var user = new ChatMessage { Role = "user", Content = "Manage the group.", RunId = id };
                        source.Messages.Add(user); store.Commit(source, ConversationCommit.UserInput(user)); inbox.Bind(source);
                        if (office)
                        {
                            var client = new LlmClient(() => "fixture-key");
                            var service = new ConversationRunService(adapter, executor, store, journal,
                                (config, messages, options, stream, token) => client.CompleteAsync(config, messages, options, stream, token), null, inbox: inbox);
                            service.ExecuteAsync(source.Mode, user.Content, source, source.Context, settings,
                                executor.GetControllerTools().ToArray(), null, null, null, null, CancellationToken.None,
                                appendUserMessage: false).GetAwaiter().GetResult();
                        }
                        else
                        {
                            var ports = new WorkspaceConversationService.WorkspacePorts(paths, store, journal,
                                new DocumentArtifactStore(resources, resources, bodies), files, bodies,
                                new WorkspaceWebSnapshotStore(paths, files), workspace, source, settings, () => "fixture-key", null,
                                agentCommunication: communication, agentInbox: inbox, resourceAuthority: resources, resourceMutations: resources);
                            ConversationRunCoordinator.RunAsync(new AgentRunRequest(id, id, user.Content, new AgentRunLimits(12, 12)),
                                ports, ports.Tools, ports, CancellationToken.None).GetAwaiter().GetResult();
                        }
                        inbox.End(source, true, false);
                        var terminals = journal.ReadWindow(source.Id, long.MaxValue, 20).ToArray();
                        AssertEqual(count, terminals.Length, "every accepted group call has one canonical terminal");
                        AssertEqual(loseAcknowledgement, faulted, "fault occurs at authority acknowledgement");
                        AssertTrue(terminals.All(item => item.Fact.Result.ExecutionProgress.Outcome ==
                            (loseAcknowledgement ? ToolExecutionOutcome.Unknown : ToolExecutionOutcome.Ok)), "only exact runtime evidence proves success");
                        AssertEqual(loseAcknowledgement ? 1 : 5, terminals.Count(item => item.Fact.Result.AuthorityCommitId != null),
                            "changed results link exact resource outcomes; list and no-op add have no effect");
                        AssertEqual(memberHead, store.Load(member.Id).StorageHeadHash, "group consumers never start or rewrite participants");
                        var terminalHashes = terminals.Select(item => item.Position.Hash).ToArray();
                        var usage = checkUsage ? store.ListHeaders().Single(item => item.Id == source.Id) : null;
                        if (moveDocument)
                        {
                            store.MoveDocument(source.Host, source.DocumentKey, source.Host,
                                office ? "saved-unknown" : source.DocumentKey, "Relocated Unknown", null);
                            source = store.Load(source.Id);
                        }
                        var groupScope = source;
                        if (deleteSource || deleteDocument)
                        {
                            AssertTrue(deleteDocument ? store.DeleteDocument(source.Host, source.DocumentKey) : store.Delete(source.Host, source.DocumentKey, source.Id),
                                "terminal Unknown chat can be logically deleted without erasing evidence");
                            groupScope = new ChatSession { Id = null, Host = source.Host, DocumentKey = source.DocumentKey,
                                WorkspaceId = source.WorkspaceId, DocumentAuthorityId = source.DocumentAuthorityId };
                        }
                        journal.Rebuild(); resources = new ResourceJournal(paths);
                        if (checkUsage) AssertSameJournalUsage(usage, store.ListHeaders().Single(item => item.Id == source.Id));
                        if (moveDocument) AssertEqual(source.DocumentKey, store.Load(source.Id).DocumentKey, "relocation survives rebuild with Unknown evidence");
                        groups = NativeGroups(paths, journal, source, resources);
                        if (deleteSource || deleteDocument) AssertTrue(store.ReadMetadata(source.Id) == null, "retained resource proof cannot resurrect its source chat");
                        AssertEqual(loseAcknowledgement ? 1 : 0, groups.Read(groupScope).Count, "restart/rebuild preserves group or tombstone without replay");
                        AssertEqual(0, resources.Unresolved().Count, "resource outcome resolves its exact preparation even when acknowledgement is lost");
                        AssertTrue(terminalHashes.SequenceEqual(journal.ReadWindow(source.Id, long.MaxValue, 20).Select(item => item.Position.Hash)),
                            "rebuild does not upgrade source Unknown from durable resource proof");
                        if (!office)
                        {
                            var backup = Path.Combine(paths.Root, "group-backup"); JournalCasService(paths, StorageProtector.None).BackupJournals(backup);
                            var restored = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "group-restore"), _ => StorageProtector.None);
                            var copy = new ConversationJournal(restored, scope); var copyResources = new ResourceJournal(restored);
                            if (checkUsage) AssertSameJournalUsage(usage, ((IConversationStore)copy).ListHeaders().Single(item => item.Id == source.Id));
                            AssertEqual(loseAcknowledgement ? 1 : 0, NativeGroups(restored, copy, source, copyResources).Read(groupScope).Count,
                                "clean restore retains group CAS and tombstones");
                            if (deleteSource || deleteDocument) AssertTrue(((IConversationStore)copy).ReadMetadata(source.Id) == null, "restore preserves deletion alongside resource evidence");
                            AssertTrue(terminalHashes.SequenceEqual(copy.ReadWindow(source.Id, long.MaxValue, 20).Select(item => item.Position.Hash)),
                                "restore retains source evidence without replay");
                        }
                    }
                    finally { resources.Journal.Fault = null; try { listener.Stop(); } catch (ObjectDisposedException) { }
                        AssertTrue(provider.Wait(5000), "native group provider stops"); }
                }
            });
        }
    }
}
