using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Agent;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void CompactionPublishesSharedContext()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), (executor, adapter) =>
            {
                var a = NewSession(adapter); a.DocumentAuthorityId = DocumentAuthorityId.Create().Id;
                var authority = new ResourceAuthorityStore(FixturePaths.Value);
                var scope = ResourceAuthorityScopeId.Document(new DocumentAuthorityId(a.DocumentAuthorityId));
                Action append = () => { for (var i = 0; i < 8; i++) a.Messages.Add(new ChatMessage {
                    Role = i % 2 == 0 ? "user" : "assistant", Content = "Keep the layout and document the findings." }); };
                var drift = false;
                LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
                {
                    if (drift)
                    {
                        var before = authority.Capture(scope);
                        var identity = new ResourceIdentity(ResourceUri.Create("state", "document", a.DocumentAuthorityId, "shared-publication-drift"));
                        authority.Publish(ResourceAuthorityCommit.Create(scope, before.Generation, null, new[] {
                            new ResourceHeadChange(identity, null, ResourceHeadState.Unknown(identity, before.Generation + 1, "test-drift")) }, AuthorityCommitReason.DerivedPublication));
                    }
                    return Task.FromResult(CompactionReply(messages, "Remember the layout requirement."));
                };
                var service = new ContextCompactionService(completion, executor.ResourceAuthority, executor.Payloads);
                append();
                var first = service.EnsureWithinBudgetAsync(a, new AppSettings(), null, true, null, CancellationToken.None).GetAwaiter().GetResult();
                AssertTrue(first.SharedResource != null && first.SharedPublicationIssue == null, "real compaction publishes through document owner");
                var b = NewSession(adapter); b.DocumentAuthorityId = a.DocumentAuthorityId;
                AssertEqual(1, executor.ResourceGateway.Find(b, "Shared context", "document").Items.Count, "empty second chat can discover publication");
                append(); drift = true;
                var second = service.EnsureWithinBudgetAsync(a, new AppSettings(), null, true, null, CancellationToken.None).GetAwaiter().GetResult();
                AssertTrue(second.SharedResource == null && second.SharedPublicationIssue.Contains("changed"), "concurrent writer produces explicit shared-publication failure");
                AssertEqual(second.Id, a.ActiveContextCheckpointId, "local compaction still completes without pretending shared publication succeeded");
                AssertEqual(first.SharedResource.Uri, executor.ResourceGateway.Find(b, "Shared context", "document").ResourceRefs.Single().Uri,
                    "failed newer publication preserves the prior shared head");
            });
        }

        private static void SharedContextPublicationAndReads()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), (executor, adapter) =>
            {
                var paths = FixturePaths.Value;
                var chats = new ChatStore(paths);
                var authority = new ResourceAuthorityStore(paths);
                var a = NewSession(adapter); a.DocumentAuthorityId = DocumentAuthorityId.Create().Id; a.Title = "Research";
                var scope = ResourceAuthorityScopeId.Document(new DocumentAuthorityId(a.DocumentAuthorityId));
                var source = new ResourceRef("rna://vba/" + a.DocumentAuthorityId + "/module", "r1");
                var body = PayloadRef.FromBlob(executor.Payloads.StoreText("Exact observed source", "text/plain"));
                authority.RegisterRevision(scope, new ResourceRevisionMetadata(source, body.Sha256, body));
                authority.Publish(ResourceAuthorityCommit.Create(scope, 0, null, new[] {
                    new ResourceHeadChange(source.Identity, null, ResourceHeadState.Known(source, 1)) }, AuthorityCommitReason.DerivedPublication));
                var user = new ChatMessage { Role = "user", Content = "Keep the original layout." + new string('x', 80000) };
                var observed = new ChatMessage { Role = "tool", Content = "Exact observed source" };
                a.Messages.AddRange(new[] { user, observed });
                var checkpoint = new ContextCheckpoint { Claims = new List<StructuredContextClaim> {
                    new StructuredContextClaim { ClaimId = "constraint", Kind = "constraint", Text = "Preserve layout.",
                        SourceRoles = new List<string> { "user" }, SourceMessageIds = new List<string> { user.Id } },
                    new StructuredContextClaim { ClaimId = "observed", Kind = "observation", Text = "Observed source conclusion.",
                        SourceRoles = new List<string> { "tool" }, SourceMessageIds = new List<string> { observed.Id },
                        Evidence = new List<ResourceEvidence> { new ResourceEvidence("source-evidence", scope, source, "text", ResourceCoverage.Whole(), true, 1, body) } } } };
                var artifact = chats.DocumentArtifacts.PublishContext(a, checkpoint, 1);
                var exact = ChatResourceUri.CreateArtifactRevision(a, artifact);
                var b = NewSession(adapter); b.DocumentAuthorityId = a.DocumentAuthorityId;
                var found = executor.ResourceGateway.Find(b, "Shared context", "document").Items.Single();
                AssertEqual("shared context", found.Type, "second chat discovers a typed shared resource");
                var tool = executor.GetControllerTools().Single(item => item.Id == ResourceToolCatalog.ReadToolId);
                var runtime = executor.CreateNativeRuntime(b, new[] { tool }, new AppSettings(), ChatModes.Chat, false);
                var call = new ToolCall("shared-read", tool.Id, new JObject { ["target"] = found.Target, ["representation"] = "text" }.ToString());
                var record = ExecuteNative(runtime, call, runtime.Describe(call));
                AssertEqual(ToolExecutionOutcome.Ok, record.Outcome, "native reader reaches document-owned archive");
                var replies = new Queue<string>(new[] {
                    new JObject { ["message"] = "Read shared context.", ["final"] = false, ["tool_calls"] = new JArray(new JObject {
                        ["name"] = tool.Id, ["arguments"] = JObject.Parse(call.ArgumentsJson) }) }.ToString(),
                    "{\"message\":\"Shared context read.\",\"final\":true,\"tool_calls\":[]}" });
                var requests = new List<IReadOnlyList<ChatMessage>>();
                b.Mode = ChatModes.Chat;
                var runService = CreateConversationRunService(adapter, executor, (settings, messages, options, stream, cancellationToken) => {
                    requests.Add(messages.ToList()); return Task.FromResult(new LlmCompletionResult { Content = replies.Dequeue() }); });
                runService.ExecuteAsync(ChatModes.Chat, "Read the shared context.", b, NewContext(adapter), new AppSettings(),
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(), null).GetAwaiter().GetResult();
                AssertTrue(requests.Count >= 2, "native run obtains a second model request after the shared read");
                AssertContains(FlattenSimple(requests[1]), "Observed source conclusion", "real model session filters an externalized shared archive after hydration");
                AssertTrue(!FlattenSimple(requests[1]).Contains(new string('x', 241)), "full retained source transcript is not sent to the model");
                var message = AgentJsonProtocol.CreateToolResultMessage(new ToolInvocation { ToolId = tool.Id, ToolCallId = call.Id },
                    new ToolResultMaterialization(record.Result, resourceEvidence: record.ResourceEvidence), int.MaxValue, "tool");
                var direct = ModelToolResultProjection.Project(message);
                AssertTrue(!direct.Content.Contains("Observed source conclusion") && !direct.Content.Contains(user.Id), "direct projection cannot expose raw archive");
                Func<ModelAuthoritySnapshot> capture = () => new ModelAuthoritySnapshot(authority.CaptureMany(new[] { scope }),
                    "tools", new SkillCatalogSnapshot(null), new SchemaRegistrySnapshot(null), 0);
                Func<ChatMessage, ModelContextSnapshot> compile = input => new ModelContextCompiler(executor.Payloads).Compile(capture(),
                    new ChatMessage[0], new[] { input }, null, new ToolCatalogEntry[0], new AppSettings(), 3000);
                var current = compile(message);
                AssertContains(current.Messages.Single().Content, "Observed source conclusion", "compiler admits current supported claim");
                AssertContains(current.Messages.Single().Content, "source-", "projection carries source citations without durable ids");
                AssertTrue(!current.Messages.Single().Content.Contains(source.Uri) && !current.Messages.Single().Content.Contains(user.Id), "runtime provenance stays private to runtime");
                var before = authority.Capture(scope);
                authority.RegisterRevision(scope, new ResourceRevisionMetadata(new ResourceRef(source.Uri, "r2"), body.Sha256, body));
                authority.Publish(ResourceAuthorityCommit.Create(scope, before.Generation, null, new[] {
                    new ResourceHeadChange(source.Identity, before.GetHead(source.Identity), ResourceHeadState.Known(new ResourceRef(source.Uri, "r2"), before.Generation + 1)) }, AuthorityCommitReason.DerivedPublication));
                foreach (var input in new[] { message, current.Messages.Single() })
                {
                    var changed = compile(input).Messages.Single().Content;
                    AssertTrue(!changed.Contains("Observed source conclusion"), "raw and already projected reads both recheck changed sources");
                    AssertContains(changed, "Preserve layout", "independent user constraint survives source changes");
                }
                RuntimeThrows<ResourceRequestException>(() => executor.ResourceGateway.Read(b, new ResourceReadRequest { Reference = exact, Representation = "records" }));
                var generation = authority.Capture(scope).Generation;
                RuntimeThrows<ResourceAuthorityConflictException>(() => chats.DocumentArtifacts.PublishContext(a, new ContextCheckpoint { Claims = checkpoint.Claims }, generation - 1));
                AssertEqual(1, chats.DocumentArtifacts.InspectCurrentMetadata(b).Items.Count, "failed publication leaves one current lineage");
                var newer = chats.DocumentArtifacts.PublishContext(a, new ContextCheckpoint { Claims = checkpoint.Claims }, generation);
                AssertEqual(1, chats.DocumentArtifacts.InspectCurrentMetadata(b).Items.Count, "new snapshot replaces only its current discovery head");
                AssertTrue(newer.Id != artifact.Id, "history keeps exact distinct revisions");
                ArtifactWorkingSet.Set(b, artifact, true);
                AssertTrue(ArtifactWorkingSet.IsDetached(b, newer), "unlink follows the shared context lineage across revisions");
                var imported = compile(message).Messages.Single().ContextClaims;
                var copied = chats.DocumentArtifacts.PublishContext(b, new ContextCheckpoint { Claims = imported }, authority.Capture(scope).Generation);
                AssertEqual(2, chats.DocumentArtifacts.InspectCurrentMetadata(b).Items.Count, "another chat publishes its own lineage without overwriting the origin");
                chats.Save(a); chats.Delete(a.Host, a.DocumentKey, a.Id);
                var gc = CasService(paths, new ChatStore(paths), new VbaJournalStore(paths), () => StorageProtector.None).Collect();
                AssertTrue(gc.Completed && gc.Health.MissingBlobCount == 0, "document roots retain source revisions and bodies through GC: " + Newtonsoft.Json.JsonConvert.SerializeObject(gc));
                var retained = new ChatStore(paths).DocumentArtifacts.Read(b, exact);
                AssertContains(retained.InlineText, "Keep the original layout", "source transcript survives origin deletion and CAS collection");
                AssertTrue(executor.Payloads.HasStoredReference(body.ToBlobReference()), "exact source evidence payload remains retained");
                AssertContains(chats.DocumentArtifacts.Read(b, ChatResourceUri.CreateArtifactRevision(b, copied)).InlineText, "Keep the original layout", "republication preserves inherited source transcript");
            });
        }
    }
}
