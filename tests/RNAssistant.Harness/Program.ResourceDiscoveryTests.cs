using System;
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
                AssertEqual(1, page.UnavailableResources, "one bad metadata record is isolated");
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
                File.WriteAllBytes(path, bytes);
                RuntimeThrows<ResourceRequestException>(() => executor.ResourceGateway.List(otherChat, "chat", "document-artifact", page.NextCursor, 1));
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
