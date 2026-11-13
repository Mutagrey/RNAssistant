using System;
using System.IO;
using System.Linq;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void BrowserTargetsRequireExactOwnerAndKind()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), (executor, adapter) => {
                var paths = FixturePaths.Value; var chats = new ChatStore(paths); var documents = new DocumentAuthorityRegistry(paths);
                var source = NewSession(adapter);
                source.DocumentAuthorityId = documents.Resolve("word", "browser", Path.Combine(paths.Root, "source.docx")).Id;
                var resolverEmpty = BrowserTargetResolver.Live(source);
                AssertEqual(BrowserTargetState.Empty, resolverEmpty.State, "empty editor has no invented snapshot");
                AssertTrue(resolverEmpty.Capabilities.Edit && !resolverEmpty.Capabilities.Preview, "empty editor can create but not preview");
                AssertTrue(WriteSharedHtmlFixture(executor, source, "<h1>one</h1>").Success, "publish first source");
                var original = source.Artifacts.Single(a => a.Id == source.ActiveHtmlArtifactId);
                var exact = ChatResourceUri.CreateArtifactRevisionUri(source, original);
                AssertTrue(WriteSharedHtmlFixture(executor, source, "<h1>two</h1>").Success, "advance head");
                var selected = source.ActiveHtmlArtifactId;
                var metadata = chats.DocumentArtifacts.Read(ConversationMetadata.From(source), ChatResourceUri.ArtifactSnapshot(new ResourceRef(exact)), false);
                AssertTrue(string.IsNullOrEmpty(metadata.InlineText), "resolver needs metadata only");
                var target = BrowserTargetResolver.Saved(source, metadata, exact);
                AssertEqual(exact, target.ResourceUri, "saved target remains exact after head advances");
                AssertEqual(BrowserTargetKind.HtmlArtifact, target.Kind, "saved snapshot has a distinct target kind");
                AssertTrue(!target.Capabilities.Preview && target.Capabilities.PreviewReason == BrowserRefusalCode.NativeUnavailable && target.Capabilities.Source && !target.Capabilities.Edit &&
                    !target.Capabilities.Restore && !target.Capabilities.Annotations, "snapshot is immutable regardless of its current head");
                RuntimeThrows<InvalidOperationException>(() => BrowserTargetResolver.Saved(source, metadata, exact + "/source"));
                var foreign = NewSession(adapter); foreign.DocumentAuthorityId = "other-document";
                RuntimeThrows<InvalidOperationException>(() => BrowserTargetResolver.Saved(foreign, metadata, exact));
                var unsupported = new ChatArtifact { Id = "not-html", Kind = "plugin", Title = "spoof.html", MimeType = "text/html", Revision = 1 };
                RuntimeThrows<InvalidOperationException>(() => BrowserTargetResolver.Saved(source, unsupported, ChatResourceUri.CreateArtifactRevisionUri(source, unsupported)));

                var catalog = new ArtifactCatalogService(documents, chats.DocumentArtifacts,
                    new ArtifactWorkingSetService(chats.DocumentArtifacts, new ResourceMutationJournal(paths)),
                    new ResourceAuthorityStore(paths), new ChatBlobStore(paths));
                var export = catalog.Export(source, new ArtifactTransferRequest { ChatId = source.Id,
                    DocumentId = source.DocumentAuthorityId, ResourceUri = exact, Preview = true });
                AssertEqual(exact, export.BrowserTarget.ResourceUri, "existing preview response carries the verified target");
                AssertEqual(selected, source.ActiveHtmlArtifactId, "resolving a snapshot never selects it for editing");
                var live = BrowserTargetResolver.Live(source);
                AssertEqual(selected, live.ArtifactId, "live editor uses its addressed owner's selected publication");
                AssertTrue(live.Capabilities.Edit && !live.Capabilities.Annotations && !live.Capabilities.CreateProject, "source editing retains its owner while native capabilities await attachment");
                source.HtmlWorkspaceRecovery.CanMutate = false;
                var blocked = BrowserTargetResolver.Live(source);
                AssertEqual(BrowserTargetState.Ready, blocked.State, "retained source stays readable during recovery");
                AssertTrue(!blocked.Capabilities.Preview && blocked.Capabilities.Source && !blocked.Capabilities.Edit &&
                    !blocked.Capabilities.Restore && !blocked.Capabilities.Annotations, "read-only source grants no mutation or annotation");
                source.HtmlWorkspaceRecovery.CanMutate = true;
                source.Artifacts.Add(source.Artifacts.Single(a => a.Id == selected));
                RuntimeThrows<InvalidOperationException>(() => BrowserTargetResolver.Live(source));
                source.Artifacts.RemoveAll(a => a.Id == selected);
                AssertEqual(BrowserTargetState.Unavailable, BrowserTargetResolver.Live(source).State, "missing selected source does not choose another snapshot");
            });
        }
    }
}
