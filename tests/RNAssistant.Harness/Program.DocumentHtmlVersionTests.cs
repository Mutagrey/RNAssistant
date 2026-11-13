using System;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void DocumentHtmlCheckpointMigration()
        {
            WithTempPaths(paths => {
                var authority = new ResourceAuthorityStore(paths); var blobs = new ChatBlobStore(paths);
                var fault = new VersionReceiptFault(authority); var owner = new DocumentArtifactStore(fault, authority, blobs);
                var chat = new ChatSession { Id = "legacy-html-author", DocumentAuthorityId = "doc-html-versions" };
                var metadata = ConversationMetadata.From(chat); var scope = ResourceAuthorityScopeId.Document(new DocumentAuthorityId(chat.DocumentAuthorityId));
                var logical = "html_ws_" + new string('a', 64); var identity = HtmlWorkspaceIdentity.Identity(chat, logical);
                Func<int, ChatArtifact> publish = version => {
                    var snapshot = new HtmlWorkspaceSnapshot { ActiveFileId = "main", CreatedUtc = new DateTime(2026, 10, version, 0, 0, 0, DateTimeKind.Utc) };
                    snapshot.Files.Add(new HtmlWorkspaceFile { Id = "main", Path = "index.html", Kind = "html", Content = "<h1>" + version + "</h1>" });
                    var artifact = new ChatArtifact { Kind = ChatArtifactKinds.HtmlWorkspace, DocumentAuthorityId = chat.DocumentAuthorityId,
                        Id = ChatResourceUri.CreateSnapshotId(logical, version), Revision = version, CreatedUtc = snapshot.CreatedUtc,
                        Title = "Legacy project", MimeType = "application/json", InlineText = JsonConvert.SerializeObject(snapshot) };
                    var retained = owner.RetainAuthoredSnapshot(chat, artifact, "legacy-attempt-" + version, "legacy-operation-" + version);
                    var frozen = authority.Capture(scope); var before = frozen.GetHead(identity);
                    var head = new ResourceRef(identity.Uri, "head-" + version);
                    var payload = PayloadRef.FromBlob(blobs.StoreText(JsonConvert.SerializeObject(retained.Revision), "application/json"));
                    authority.RegisterRevision(scope, new ResourceRevisionMetadata(head, payload.Sha256, payload, before?.Revision,
                        dependencies: new[] { new ResourceDependency(retained.Revision, kind: "immutable-snapshot") }));
                    authority.Publish(ResourceAuthorityCommit.Create(scope, frozen.Generation, null, new[] {
                        new ResourceHeadChange(identity, before, ResourceHeadState.Known(head, frozen.Generation + 1)),
                        new ResourceHeadChange(retained.Identity, null, ResourceHeadState.Known(retained.Revision, frozen.Generation + 1)) }, AuthorityCommitReason.MutationEffect));
                    return artifact;
                };
                var historical = new[] { publish(1), publish(2), publish(3) };
                fault.LoseNext = true;
                var unknown = false; try { owner.HtmlVersionState(metadata, logical); } catch (SavedVersionException error) { unknown = error.MayHavePublished; }
                AssertTrue(unknown, "migration cutoff was committed before its lost acknowledgement");
                var fourth = publish(4);
                owner = new DocumentArtifactStore(authority, authority, blobs);
                var state = owner.HtmlVersionState(metadata, logical); var page = owner.HtmlVersions(metadata, logical, null);
                AssertEqual(3, page.Items.Count, "all earlier revisions migrate; a later automatic revision does not");
                AssertTrue(page.Items.Reverse().Select(e => e.ContentReference.Uri).SequenceEqual(historical.Select(a => ChatResourceUri.CreateArtifactRevision(chat, a).Uri)), "original exact links and order preserved");
                AssertTrue(page.Items.Reverse().Select(e => e.CreatedUtc).SequenceEqual(historical.Select(a => a.CreatedUtc)), "original dates preserved");
                var current = owner.CurrentHtmlSnapshot(metadata, logical);
                var saved = owner.SaveHtmlVersion(metadata, logical, state.StateRevision, current.Uri, "save-fourth", new SavedVersionActor { Kind = SavedVersionActorKind.User });
                AssertEqual(current.Uri, saved.Version.ContentReference.Uri, "Save reuses the already durable aggregate");
                AssertEqual(4L, saved.Version.Ordinal, "one new explicit ordinal");
                state = owner.HtmlVersionState(metadata, logical);
                SavedFailure(() => owner.DeleteHtmlVersion(metadata, logical, saved.VersionId, state.StateRevision, current.Uri, "delete-active", true), "version_active");
                var older = page.Items.Last();
                owner.DeleteHtmlVersion(metadata, logical, older.VersionId, state.StateRevision, current.Uri, "delete-older", true);
                AssertEqual(historical[0].Id, owner.Read(metadata, older.ContentReference).Id, "tombstone preserves delivered historical evidence");
                var service = new DocumentHtmlVersionService(paths, owner);
                var binding = new BrowserOwnerScope { Kind = BrowserOwnerKind.Document, ScopeId = chat.DocumentAuthorityId, ChatId = chat.Id };
                var removed = service.Resolve(metadata, new BrowserResolveRequest { OwnerScope = binding, Locator = BrowserLocalLocator.ForResource(logical, older.VersionId) });
                AssertEqual(BrowserTargetState.Removed, removed.Target.State, "saved locator never substitutes the head");
                var afterTarget = service.Resolve(metadata, new BrowserResolveRequest { OwnerScope = binding, Locator = BrowserLocalLocator.ForResource(logical, saved.VersionId) }).Target;
                AssertEqual(3, service.SavedBrowserTargets(metadata, afterTarget, 8).Count, "saved completion excludes removed entries without aggregate reads");
                var comparison = new BrowserSourceComparisonRequest { Target = afterTarget, BeforeId = page.Items.First().VersionId, AfterId = saved.VersionId, Path = "index.html" };
                var compared = service.Compare(metadata, comparison);
                AssertEqual("<h1>3</h1>", compared.BeforeText, "checkpoint comparison resolves the selected entry");
                AssertEqual("<h1>4</h1>", compared.AfterText, "checkpoint comparison retains the displayed exact publication");
                comparison.BeforeId = older.VersionId;
                SavedFailure(() => service.Compare(metadata, comparison), "version_removed");
                var fifth = publish(5);
                AssertEqual(3, owner.HtmlVersions(metadata, logical, null).Items.Count, "new automatic revision does not enter default checkpoints");
                var replay = owner.SaveHtmlVersion(metadata, logical, state.StateRevision, current.Uri, "save-fourth", new SavedVersionActor { Kind = SavedVersionActorKind.User });
                AssertEqual(saved.VersionId, replay.VersionId, "same operation reconciles without selecting a newer current revision");
                SavedFailure(() => owner.SaveHtmlVersion(metadata, logical, owner.HtmlVersionState(metadata, logical).StateRevision, current.Uri,
                    "stale-head", new SavedVersionActor { Kind = SavedVersionActorKind.User }), "version_source_changed");
                AssertEqual(fifth.Id, owner.Read(metadata, owner.CurrentHtmlSnapshot(metadata, logical)).Id, "automatic durability and current head unchanged by metadata saves");
            });
        }
        private static void NewHtmlAutomaticRevisionsStayTechnical()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), (executor, adapter) => {
                var chat = NewSession(adapter); var store = new ChatStore(FixturePaths.Value).DocumentArtifacts;
                AssertTrue(WriteSharedHtmlFixture(executor, chat, "<h1>new</h1>").Success, "initial mutation");
                var metadata = ConversationMetadata.From(chat); var logical = HtmlWorkspaceIdentity.LogicalId(chat.ActiveHtmlArtifactId);
                AssertEqual(0, store.HtmlVersions(metadata, logical, null).Items.Count, "new project starts without artificial legacy checkpoints");
                var current = store.CurrentHtmlSnapshot(metadata, logical);
                var saved = store.SaveHtmlVersion(metadata, logical, store.HtmlVersionState(metadata, logical).StateRevision,
                    current.Uri, "explicit-new", new SavedVersionActor { Kind = SavedVersionActorKind.User });
                AssertTrue(WriteSharedHtmlFixture(executor, chat, "<h1>changed</h1>").Success, "next automatic mutation");
                AssertEqual(1, store.HtmlVersions(metadata, logical, null).Items.Count, "technical history is retained separately from picker");
                AssertTrue(store.CurrentHtmlSnapshot(metadata, logical).Uri != saved.Version.ContentReference.Uri, "working head advanced durably");
            });
        }
    }
}
