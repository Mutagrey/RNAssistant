using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void DocumentDiscoveryPagesBoundMetadata()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), (executor, adapter) =>
            {
                var session = NewSession(adapter); session.DocumentAuthorityId = DocumentAuthorityId.Create().Id;
                var paths = FixturePaths.Value;
                var chats = new ChatStore(paths);
                var ingestion = new ChatResourceIngestionService(new AttachmentStore(paths), chats.DocumentArtifacts);
                for (var index = 0; index < 73; index++)
                {
                    var draft = ingestion.Stage(session, "Reference " + index + ".md", "text/markdown", Encoding.UTF8.GetBytes("# Section " + index));
                    var message = new ChatMessage { Role = "user", Attachments = ingestion.LoadDrafts(session, new[] { draft.Id }).ToList() };
                    session.Messages.Add(message); ingestion.CommitAndLink(session, message, session.Messages.Count - 1);
                }
                var scope = ResourceAuthorityScopeId.Document(new DocumentAuthorityId(session.DocumentAuthorityId));
                var authority = new ResourceAuthorityStore(paths);
                var generation = authority.Capture(scope).Generation;
                var unrelated = Enumerable.Range(0, 1000).Select(index => {
                    var identity = new ResourceIdentity(ResourceUri.Create("state", scope.Kind, scope.Id, "plan-operation-fixture-" + index));
                    return new ResourceHeadChange(identity, null, ResourceHeadState.Unknown(identity, generation + 1, "test-receipt"));
                }).ToArray();
                authority.Publish(ResourceAuthorityCommit.Create(scope, generation, null, unrelated, AuthorityCommitReason.DerivedPublication));
                var noOwner = new ResourceGatewayService(new[] { new ChatArtifactResourceProvider() }).Find(session, null, "document");
                AssertTrue(noOwner.Partial && !noOwner.Empty, "missing document owner cannot invent an empty catalog");
                var counted = new DiscoveryPageStore(authority);
                var owner = new DocumentArtifactStore(counted, counted, executor.Payloads);
                var provider = new ChatArtifactResourceProvider(payloads: executor.Payloads, documentArtifacts: owner);
                var page = provider.List(session, "document-artifact", null, 5);
                AssertEqual(5, page.Items.Count, "one provider page hydrates only its requested source slots");
                AssertTrue(counted.ViewReads <= 10 && counted.HeadPages == 1, "no all-library metadata scan or full authority capture before a small page");
                AssertTrue(page.Total == 73 && !page.TotalIsExact && page.NextCursor != null, "root count is explicitly distinguished from filtered result total");
                var beforeIdentity = counted.ViewReads;
                AssertEqual(page.Items[0].Reference.Uri, provider.ResolveIdentity(session, page.Items[0].Reference.Identity).Uri,
                    "an exact snapshot identity resolves by its own authority head");
                AssertEqual(1, counted.ViewReads - beforeIdentity, "identity resolution reads one record without scanning the document");
                var ids = new HashSet<string>(page.Items.Select(item => item.Reference.Uri), StringComparer.Ordinal);
                while (page.NextCursor != null)
                {
                    var reads = counted.ViewReads;
                    page = provider.List(session, "document-artifact", page.NextCursor, 5);
                    AssertTrue(counted.ViewReads - reads <= 10, "each continuation repeats only bounded metadata IO");
                    foreach (var item in page.Items) AssertTrue(ids.Add(item.Reference.Uri), "source pages never duplicate a resource");
                }
                AssertTrue(ids.Count == 73 && !page.Truncated, "all roots are reachable without scanning receipt history");
                AssertEqual(73, new DocumentArtifactStore(new ResourceAuthorityStore(paths), new ResourceAuthorityStore(paths), executor.Payloads)
                    .InspectCurrentMetadata(session, 70, 5).Total, "cold replay rebuilds the same ordered authority projection");
                var gateway = new ResourceGatewayService(new[] { provider });
                var target = ResourceGatewayService.IntentTarget(provider.List(session, "document-artifact", null, 1).Items.Single());
                var search = gateway.Find(session, "Section 72", "document");
                AssertTrue(search.Items.Any(item => item.Title == "Reference 72.md"), "content search crosses source page boundaries");
                AssertTrue(gateway.ResolveIntentTarget(session, target).Reference != null, "complete paged discovery still resolves a semantic target");
                var beforeChange = provider.List(session, "document-artifact", null, 5);
                var current = authority.Capture(scope);
                var extra = new ResourceIdentity(ResourceUri.Create("state", scope.Kind, scope.Id, "new-observation"));
                authority.Publish(ResourceAuthorityCommit.Create(scope, current.Generation, null,
                    new[] { new ResourceHeadChange(extra, null, ResourceHeadState.Unknown(extra, current.Generation + 1, "test-drift")) }, AuthorityCommitReason.DerivedPublication));
                AssertEqual("resource_revision_changed", RuntimeThrows<ResourceRequestException>(() => provider.List(session,
                    "document-artifact", beforeChange.NextCursor, 5)).ErrorCode, "another writer invalidates continuation even without changing selected roots");
            });
        }

        private sealed class DiscoveryPageStore : IResourceAuthorityStore, IResourceRevisionStore
        {
            private readonly ResourceAuthorityStore _inner;
            internal int ViewReads; internal int HeadPages;
            internal DiscoveryPageStore(ResourceAuthorityStore inner) { _inner = inner; }
            public event EventHandler<ResourceAuthorityChangedEventArgs> Changed { add { _inner.Changed += value; } remove { _inner.Changed -= value; } }
            public ResourceAuthoritySnapshot Capture(ResourceAuthorityScopeId scope) { throw new InvalidOperationException("Discovery must not copy the full authority."); }
            public ResourceAuthoritySnapshotSet CaptureMany(IReadOnlyList<ResourceAuthorityScopeId> scopes) { throw new InvalidOperationException("Discovery must not copy the full authority."); }
            public ResourceHeadState GetHead(ResourceAuthorityScopeId scope, ResourceIdentity identity) { return _inner.GetHead(scope, identity); }
            public ResourceHeadPage ReadHeads(ResourceAuthorityScopeId scope, IReadOnlyList<ResourceHeadRange> ranges, int offset, int limit)
            { HeadPages++; return _inner.ReadHeads(scope, ranges, offset, limit); }
            public AuthorityCommitResult Publish(ResourceAuthorityCommit commit) { return _inner.Publish(commit); }
            public void RegisterRevision(ResourceAuthorityScopeId scope, ResourceRevisionMetadata revision) { _inner.RegisterRevision(scope, revision); }
            public ResourceRevisionMetadata GetRevision(ResourceAuthorityScopeId scope, ResourceRef reference) { return _inner.GetRevision(scope, reference); }
            public void RegisterView(ResourceAuthorityScopeId scope, ResourceRevisionView view) { _inner.RegisterView(scope, view); }
            public ResourceRevisionView GetView(ResourceAuthorityScopeId scope, ResourceRef reference, string view) { ViewReads++; return _inner.GetView(scope, reference, view); }
        }

        private static void DocumentDiscoveryCurrentHeads()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                var definition = executor.GetControllerTools().Single(item => item.Id == MarkdownDocumentToolCatalog.SaveToolId);
                var runtime = executor.CreateNativeRuntime(session, new[] { definition }, new AppSettings(), ChatModes.Agent, false);
                var call = new ToolCall("first", definition.Id, "{\"title\":\"Guide.md\",\"description\":\"Current guide\",\"markdown\":\"# Earlier\"}");
                var first = ExecuteNative(runtime, call, runtime.Describe(call));
                AssertEqual(ToolExecutionOutcome.Ok, first.Outcome, "first Markdown published");
                var target = (string)Newtonsoft.Json.Linq.JObject.Parse(first.Result.DataJson)["target"];
                var args = new Newtonsoft.Json.Linq.JObject { ["target"] = target, ["title"] = "Guide.md", ["description"] = "Current guide", ["markdown"] = "# Current" };
                call = new ToolCall("second", definition.Id, args.ToString(Newtonsoft.Json.Formatting.None));
                var second = ExecuteNative(runtime, call, runtime.Describe(call));
                AssertEqual(ToolExecutionOutcome.Ok, second.Outcome, "second Markdown published");
                args.Remove("target"); args["title"] = "Independent.md";
                call = new ToolCall("independent", definition.Id, args.ToString(Newtonsoft.Json.Formatting.None));
                AssertEqual(ToolExecutionOutcome.Ok, ExecuteNative(runtime, call, runtime.Describe(call)).Outcome, "independent Markdown published");
                var paths = FixturePaths.Value;
                var owner = new ChatStore(paths).DocumentArtifacts;
                var authority = new ResourceAuthorityStore(paths);
                var scope = ResourceAuthorityScopeId.Document(new DocumentAuthorityId(session.DocumentAuthorityId));
                var firstRef = first.Result.Resources.Single(item => DocumentArtifactStore.Owns(session, item));
                var secondRef = second.Result.Resources.Single(item => DocumentArtifactStore.Owns(session, item));
                var oldMetadata = authority.GetView(scope, firstRef, "artifact-authored-record");
                var oldPath = executor.Payloads.PathFor(oldMetadata.Payload.Sha256);
                var oldBytes = File.ReadAllBytes(oldPath);
                File.Delete(oldPath);
                var found = executor.ResourceGateway.List(session, "chat", "document-artifact", null, 50);
                AssertTrue(found.Items.Count == 2 && found.UnavailableResources == 0, "unrequested missing historical metadata cannot poison current discovery");
                AssertTrue(found.Items.Any(item => item.Reference.Uri == secondRef.Uri) && !found.Items.Any(item => item.Reference.Uri == firstRef.Uri), "authority chooses the exact current snapshot");
                File.WriteAllBytes(oldPath, oldBytes);
                var currentPath = executor.Payloads.PathFor(authority.GetView(scope, secondRef, "artifact-authored-record").Payload.Sha256);
                var currentBytes = File.ReadAllBytes(currentPath);
                File.WriteAllText(currentPath, "corrupt");
                found = executor.ResourceGateway.List(session, "chat", "document-artifact", null, 50);
                AssertTrue(found.Items.Count == 1 && found.UnavailableResources == 1 && found.Items.Single().Title == "Independent.md", "corrupt current metadata cannot fall back to an older healthy revision");
                AssertEqual("# Earlier", owner.Read(session, firstRef).InlineText, "historical exact reads still work");
                File.WriteAllBytes(currentPath, currentBytes);
                var page = executor.ResourceGateway.List(session, "chat", "document-artifact", null, 1);
                var unrelated = new ResourceIdentity(ResourceUri.Create("state", scope.Kind, scope.Id, "test-discovery-generation"));
                var captured = authority.Capture(scope);
                authority.Publish(ResourceAuthorityCommit.Create(scope, captured.Generation, null,
                    new[] { new ResourceHeadChange(unrelated, null, ResourceHeadState.Unknown(unrelated, captured.Generation + 1, "test-generation")) }, AuthorityCommitReason.DerivedPublication));
                RuntimeThrows<ResourceRequestException>(() => executor.ResourceGateway.List(session, "chat", "document-artifact", page.NextCursor, 1));
                AssertEqual(2, executor.ResourceGateway.List(session, "chat", "document-artifact", null, 50).Items.Count, "generation-only drift leaves descriptors intact but invalidates continuation");
                page = executor.ResourceGateway.List(session, "chat", "document-artifact", null, 1);
                var logical = MarkdownDocumentIdentity.Identity(session, MarkdownDocumentIdentity.LogicalId(owner.Read(session, secondRef, false).Id));
                var before = authority.Capture(scope);
                authority.Publish(ResourceAuthorityCommit.Create(scope, before.Generation, null,
                    new[] { new ResourceHeadChange(logical, before.GetHead(logical), ResourceHeadState.Unknown(logical, before.Generation + 1, "test-unknown-head")) }, AuthorityCommitReason.DerivedPublication));
                RuntimeThrows<ResourceRequestException>(() => executor.ResourceGateway.List(session, "chat", "document-artifact", page.NextCursor, 1));
                found = executor.ResourceGateway.List(session, "chat", "document-artifact", null, 50);
                AssertTrue(found.Items.Count == 1 && found.UnavailableResources == 1, "unknown logical head is isolated without selecting latest retained snapshot");
                AssertEqual("# Current", owner.Read(session, secondRef).InlineText, "unknown currentness does not destroy exact history");
            });
        }

        private static void DocumentDiscoveryPartialResources()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), (executor, adapter) =>
            {
                var paths = FixturePaths.Value;
                var session = NewSession(adapter);
                session.DocumentAuthorityId = DocumentAuthorityId.Create().Id;
                var store = new ChatStore(paths);
                var authority = new ResourceAuthorityStore(paths);
                var scope = ResourceAuthorityScopeId.Document(new DocumentAuthorityId(session.DocumentAuthorityId));
                var ingestion = new ChatResourceIngestionService(new AttachmentStore(paths), store.DocumentArtifacts);
                foreach (var title in new[] { "Healthy.md", "Damaged.md", "Other.md" })
                {
                    var draft = ingestion.Stage(session, title, "text/markdown", Encoding.UTF8.GetBytes("# " + title + " body-needle"));
                    var message = new ChatMessage { Role = "user", Attachments = ingestion.LoadDrafts(session, new[] { draft.Id }).ToList() };
                    session.Messages.Add(message);
                    ingestion.CommitAndLink(session, message, session.Messages.Count - 1);
                }
                var otherChat = NewSession(adapter); otherChat.DocumentAuthorityId = session.DocumentAuthorityId;
                var exact = store.DocumentArtifacts.List(session).Single(item => item.Title == "Damaged.md");
                var reference = ChatResourceUri.CreateArtifactRevision(session, exact);
                var view = authority.GetView(scope, reference, "artifact-original-record");
                var path = executor.Payloads.PathFor(view.Payload.Sha256);
                var bytes = File.ReadAllBytes(path);
                File.Delete(path);
                var page = executor.ResourceGateway.List(otherChat, "chat", "document-artifact", null, 1);
                var observed = page.UnavailableResources;
                var continuation = page;
                while (continuation.NextCursor != null)
                {
                    continuation = executor.ResourceGateway.List(otherChat, "chat", "document-artifact", continuation.NextCursor, 1);
                    observed += continuation.UnavailableResources;
                }
                AssertEqual(1, observed, "one bad metadata record is isolated on its source page");
                AssertTrue(page.NextCursor != null, "healthy descriptors still paginate");
                var found = executor.ResourceGateway.Find(otherChat, "Healthy", "document");
                AssertTrue(found.Items.Any(item => item.Title == "Healthy.md") && found.Partial && !found.Complete && !found.Empty,
                    "another chat sees healthy results with explicit partial coverage");
                AssertTrue(found.UnavailableScopes.Contains("document"), "model knows the unavailable scope");
                AssertContains(found.AvailabilityHint, "Repeat only after availability changes", "partial discovery supplies a runtime-owned recovery route");
                AssertTrue(!executor.ResourceGateway.Find(otherChat, "absent-needle", "document").Empty, "partial negative is not absence");
                var target = found.Items.Single(item => item.Title == "Healthy.md").Target;
                var rejected = RuntimeThrows<ResourceRequestException>(() => executor.ResourceGateway.ResolveIntentTarget(otherChat, target));
                AssertEqual("resource_scope_incomplete", rejected.ErrorCode, "partial catalog cannot establish target uniqueness");
                RuntimeThrows<InvalidDataException>(() => store.DocumentArtifacts.List(session));
                var healthy = store.DocumentArtifacts.InspectMetadataList(session).Single(item => item.Title == "Healthy.md");
                var healthyRef = ChatResourceUri.CreateArtifactRevision(session, healthy);
                AssertTrue(executor.ResourceGateway.Resolve(otherChat, healthyRef.Uri).Resource.Title == "Healthy.md", "exact reference ignores unrelated metadata loss");
                var generation = authority.Capture(scope).Generation;
                page = executor.ResourceGateway.List(otherChat, "chat", "document-artifact", null, 1);
                File.WriteAllBytes(path, bytes);
                AssertTrue(executor.ResourceGateway.List(otherChat, "chat", "document-artifact", page.NextCursor, 1) != null,
                    "metadata recovery cannot shift generation-bound source slots or replay omitted earlier slots");
                AssertEqual(generation, authority.Capture(scope).Generation, "metadata recovery does not publish a replacement head");
                AssertEqual(healthyRef.Uri, executor.ResourceGateway.ResolveIntentTarget(otherChat, target).Reference.Uri, "recovered scope resolves the same target");
                var recovered = executor.ResourceGateway.Find(otherChat, "absent-needle", "document");
                AssertTrue(recovered.Empty && recovered.Complete && !recovered.Partial, "full negative becomes valid after exact recovery");
                File.Delete(executor.Payloads.PathFor(exact.ContentSha256));
                var searched = executor.ResourceGateway.Search(otherChat, "chat", "body-needle", "document-artifact", 20, 600);
                AssertTrue(searched.Matches.Count == 2 && searched.UnavailableResources == 1, "one missing body preserves healthy content matches");
                var partialSearch = executor.ResourceGateway.Find(otherChat, "body-needle", "document");
                AssertTrue(partialSearch.Partial && !partialSearch.Complete, "body loss reaches model completeness");
                var before = authority.Capture(scope);
                authority.Publish(ResourceAuthorityCommit.Create(scope, before.Generation, null,
                    new[] { new ResourceHeadChange(reference.Identity, before.GetHead(reference.Identity),
                        ResourceHeadState.Unknown(reference.Identity, before.Generation + 1, "test-unknown-original")) }, AuthorityCommitReason.DerivedPublication));
                AssertEqual(1, store.DocumentArtifacts.InspectCurrentMetadata(session).UnavailableResources, "unknown original head cannot silently vanish");
            });
        }
    }
}
