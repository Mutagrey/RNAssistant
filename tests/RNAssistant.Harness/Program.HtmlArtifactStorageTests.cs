using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void UploadedHtmlImportPreservesExactSource()
        {
            WithTempPaths(paths =>
            {
                var session = NewSession(FakeOfficeAdapter.ForHost("Word"));
                var sourceText = "<!doctype html><main data-safe=\"yes\">" + new string('x', 33000) + "</main>";
                var attachmentStore = new AttachmentStore(paths);
                var attachment = attachmentStore.Import(
                    "landing.html",
                    "text/html; charset=utf-8",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(sourceText)),
                    session.Id);
                var message = new ChatMessage
                {
                    Id = "uploaded-html-message",
                    Role = "user",
                    Content = "HTML upload",
                    Attachments = new List<ChatAttachment> { attachment }
                };
                session.Messages.Add(message);
                attachmentStore.CommitToCas(message);
                ChatResourceReferenceService.LinkMessageResources(session, 0);
                var sourceArtifact = session.Artifacts.Single(item => item.Id == "attachment_" + attachment.Id);
                var sourceUri = ArtifactUri(session, sourceArtifact);
                var sourceMetadata = sourceArtifact.MetadataJson;
                var sourceHash = sourceArtifact.ContentSha256;
                var sourceArtifactCount = session.Artifacts.Count;
                var service = new UploadedHtmlResourceService(
                    new ResourceGatewayService(null, attachmentStore.ReadExtractedText),
                    attachmentStore.ReadExtractedText);

                var preview = service.Preview(session, sourceUri);
                AssertEqual(sourceUri, preview.SourceResourceUri, "preview retains the exact source URI");
                AssertEqual(32000, preview.ReturnedCharacters, "uploaded HTML preview is bounded");
                AssertEqual(sourceText.Length, preview.TotalCharacters, "preview reports the complete source length");
                AssertTrue(preview.Truncated && !preview.Complete, "bounded source is explicitly labelled truncated");
                AssertEqual(sourceArtifactCount, session.Artifacts.Count, "preview creates no artifact revision");
                AssertEqual(0, session.HtmlWorkspace.Files.Count, "preview never inserts uploaded HTML into the workspace");

                var imported = service.Import(session, sourceUri, string.Empty, "pages/landing.html");
                AssertEqual("pages/landing.html", imported.ImportedPath, "explicit import target path");
                AssertEqual(sourceUri, imported.ImportedFromResourceUri, "import result retains exact provenance");
                AssertEqual(sourceText, session.HtmlWorkspace.Files.Single().Content, "import uses the complete decoded source");
                AssertEqual(sourceHash, sourceArtifact.ContentSha256, "immutable original hash is unchanged");
                AssertEqual(sourceMetadata, sourceArtifact.MetadataJson, "immutable original metadata is unchanged");
                var importedRevision = session.Artifacts.Single(item => item.Id == imported.RevisionArtifactId);
                var importedMetadata = JObject.Parse(importedRevision.MetadataJson);
                AssertEqual(sourceUri, (string)importedMetadata["importedFromUri"], "revision records exact source URI");
                AssertTrue(importedRevision.RelatedArtifactIds.Contains(sourceArtifact.Id),
                    "revision keeps source artifact reachable");
                var importedHead = ArtifactLibraryProjectionService.Project(session).Heads
                    .Single(item => item.Kind == ChatArtifactKinds.HtmlWorkspace);
                AssertEqual(sourceUri, importedHead.DerivedFromResourceUri, "library exposes import provenance");

                HtmlWorkspaceToolService.UpsertFile(
                    session,
                    "pages/landing.html",
                    "html",
                    sourceText.Replace("yes", "edited"),
                    true);
                var editedHead = ArtifactLibraryProjectionService.Project(session).Heads
                    .Single(item => item.Kind == ChatArtifactKinds.HtmlWorkspace);
                AssertEqual(sourceUri, editedHead.DerivedFromResourceUri, "descendant revisions retain import provenance");

                var artifactCount = session.Artifacts.Count;
                var fileCount = session.HtmlWorkspace.Files.Count;
                RuntimeThrows<InvalidOperationException>(() =>
                    service.Import(session, sourceUri, "stale-active", "pages/other.html"));
                AssertEqual(artifactCount, session.Artifacts.Count, "stale import appends no revision");
                AssertEqual(fileCount, session.HtmlWorkspace.Files.Count, "stale import mutates no file");
                RuntimeThrows<InvalidOperationException>(() =>
                    service.Import(session, sourceUri, session.ActiveHtmlArtifactId, "pages/landing.html"));
                AssertEqual(artifactCount, session.Artifacts.Count, "path collision appends no revision");
            });
        }

        private static void HtmlExportCheckpointOwnsExactBindingPayload()
        {
            WithTempPaths(paths =>
            {
                const string firstJson = "{\"version\":1,\"value\":9007199254740993}";
                var exportJson = "{\"duplicate\":1,\"duplicate\":2,\"value\":9007199254740993,\"text\":\"" +
                    new string('x', 40000) + "\"}";
                var store = new ChatStore(paths);
                var session = store.Create("Excel", "html-export", "Export.xlsx", "HTML export");
                HtmlWorkspaceToolService.UpsertFile(session, "index.html", "html", "<main>exact</main>", true);
                HtmlWorkspaceToolService.UpsertDataSource(session, "bound", firstJson);
                var data = session.HtmlWorkspace.DataSources.Single();
                data.Binding = new HtmlWorkspaceDataBinding
                {
                    ToolId = "excel.read_range",
                    ArgumentsJson = "{\"sheet\":\"Data\",\"address\":\"A1:B2\",\"content\":\"values\"}",
                    PayloadCompleteness = "bounded",
                    ContentSha256 = TextPatternEngine.Sha256(firstJson)
                };
                HtmlWorkspaceArtifactService.CaptureCurrent(session, "HTML bound data");
                store.Save(session);
                var persistedHead = session.ActiveHtmlArtifactId;
                var artifactCount = session.Artifacts.Count;

                data.Json = exportJson;
                data.Binding.ContentSha256 = TextPatternEngine.Sha256(exportJson);
                data.Binding.PayloadCompleteness = "truncated";
                session.Messages.Add(new ChatMessage { Role = "user", Content = "save unrelated state" });
                store.Save(session);
                AssertEqual(persistedHead, session.ActiveHtmlArtifactId,
                    "storage save does not manufacture an HTML revision outside the domain owner");
                AssertEqual(artifactCount, session.Artifacts.Count,
                    "storage save appends no hidden workspace revision");

                var loaded = store.Load(session.Id);
                AssertEqual(firstJson, loaded.HtmlWorkspace.DataSources.Single().Json,
                    "uncheckpointed binding refresh is not mistaken for durable recovery state");
                loaded.HtmlWorkspace.DataSources.Single().Json = exportJson;
                loaded.HtmlWorkspace.DataSources.Single().Binding.ContentSha256 = TextPatternEngine.Sha256(exportJson);
                loaded.HtmlWorkspace.DataSources.Single().Binding.PayloadCompleteness = "truncated";
                var beforeExport = loaded.ActiveHtmlArtifactId;
                var exportArtifactId = HtmlWorkspaceArtifactService.PrepareExport(loaded, beforeExport);
                var exportArtifact = loaded.Artifacts.Single(item => item.Id == exportArtifactId);
                AssertEqual(beforeExport, exportArtifact.ParentArtifactId, "export checkpoint keeps the exact active parent");
                var exportSnapshot = JsonConvert.DeserializeObject<HtmlWorkspaceSnapshot>(exportArtifact.InlineText);
                AssertEqual(exportJson, exportSnapshot.DataSources.Single().Json,
                    "export checkpoint preserves the complete JSON string byte-for-byte");
                AssertEqual("truncated", exportSnapshot.DataSources.Single().Binding.PayloadCompleteness,
                    "export checkpoint preserves explicit source completeness");
                var exportCount = loaded.Artifacts.Count;
                RuntimeThrows<InvalidOperationException>(() =>
                    HtmlWorkspaceArtifactService.PrepareExport(loaded, null));
                RuntimeThrows<InvalidOperationException>(() =>
                    HtmlWorkspaceArtifactService.PrepareExport(loaded, "stale-head"));
                AssertEqual(exportCount, loaded.Artifacts.Count, "stale export appends no revision");

                store.Save(loaded);
                var replayed = store.Load(loaded.Id);
                var replayedExport = replayed.Artifacts.Single(item => item.Id == exportArtifactId);
                AssertTrue(!string.IsNullOrWhiteSpace(replayedExport.ContentSha256),
                    "export checkpoint has verified CAS identity");
                AssertEqual(exportJson, replayed.HtmlWorkspace.DataSources.Single().Json,
                    "export checkpoint replays the exact payload");
                HtmlWorkspaceToolService.UpsertFile(replayed, "index.html", "html", "<main>later</main>", true);
                store.Save(replayed);
                string error;
                AssertTrue(store.TryActivateHtmlWorkspaceRevision(replayed, exportArtifactId, out error),
                    "export checkpoint is an explicit recovery target");
                AssertEqual(exportJson, replayed.HtmlWorkspace.DataSources.Single().Json,
                    "recovery restores the exact exported binding payload");
            });
        }

        private static void HtmlWorkspaceBranchesUseUniqueMonotonicRevisions()
        {
            var session = new ChatSession();
            HtmlWorkspaceToolService.UpsertFile(session, "index.html", "html", "root", true);
            var rootId = session.ActiveHtmlArtifactId;
            AssertEqual(1, session.Artifacts.Single(item => item.Id == rootId).Revision, "root revision");

            HtmlWorkspaceToolService.UpsertFile(session, "index.html", "html", "branch A", true);
            var branchAId = session.ActiveHtmlArtifactId;
            AssertEqual(2, session.Artifacts.Single(item => item.Id == branchAId).Revision, "first branch revision");
            HtmlWorkspaceToolService.UpsertFile(session, "index.html", "html", "branch A child", true);
            var branchAChildId = session.ActiveHtmlArtifactId;
            AssertEqual(3, session.Artifacts.Single(item => item.Id == branchAChildId).Revision, "first branch child revision");

            HtmlWorkspaceToolService.RestoreSnapshot(session, rootId);
            HtmlWorkspaceToolService.UpsertFile(session, "index.html", "html", "branch B", true);
            var branchBId = session.ActiveHtmlArtifactId;
            var branchB = session.Artifacts.Single(item => item.Id == branchBId);
            AssertEqual(4, branchB.Revision, "alternative branch uses the next global revision");
            AssertEqual(rootId, branchB.ParentArtifactId, "alternative branch keeps the exact active parent");

            HtmlWorkspaceToolService.RestoreSnapshot(session, rootId);
            AssertEqual(2, session.HtmlWorkspace.RedoBranches.Count, "both direct branches remain available");
            HtmlWorkspaceToolService.RedoSnapshot(session, branchBId);
            HtmlWorkspaceToolService.UpsertFile(session, "index.html", "html", "branch B child", true);
            var branchBChildId = session.ActiveHtmlArtifactId;
            var branchBChild = session.Artifacts.Single(item => item.Id == branchBChildId);
            AssertEqual(5, branchBChild.Revision, "continued alternative branch remains globally monotonic");
            AssertEqual(branchBId, branchBChild.ParentArtifactId, "continued branch keeps the exact parent");
            AssertEqual("1,2,3,4,5", string.Join(",", session.Artifacts
                .Where(item => item.Kind == ChatArtifactKinds.HtmlWorkspace)
                .Select(item => item.Revision)
                .OrderBy(revision => revision)), "workspace revisions are unique and monotonic");

            var library = ArtifactLibraryProjectionService.Project(session).Heads
                .Single(item => item.Kind == ChatArtifactKinds.HtmlWorkspace);
            AssertEqual(branchBChildId, library.ArtifactId, "library uses the explicit active branch head");
            AssertEqual(5, library.History.Count, "library preserves the complete branch lineage");
            AssertEqual("branch", library.History.Single(item => item.ArtifactId == branchAId).Relation,
                "inactive first branch remains explicit");
            AssertEqual("branch", library.History.Single(item => item.ArtifactId == branchAChildId).Relation,
                "inactive first branch descendant remains explicit");

            var incompatible = new ChatSession();
            incompatible.Artifacts.Add(new ChatArtifact
            {
                Id = "duplicate-revision-a",
                Kind = ChatArtifactKinds.HtmlWorkspace,
                Revision = 1
            });
            incompatible.Artifacts.Add(new ChatArtifact
            {
                Id = "duplicate-revision-b",
                Kind = ChatArtifactKinds.HtmlWorkspace,
                Revision = 1
            });
            var artifactCount = incompatible.Artifacts.Count;
            var error = RuntimeThrows<InvalidOperationException>(() =>
                HtmlWorkspaceToolService.UpsertFile(incompatible, "index.html", "html", "must not write", true));
            AssertTrue(error.Message.IndexOf("ambiguous", StringComparison.OrdinalIgnoreCase) >= 0,
                "ambiguous lineage requires reset");
            AssertEqual(artifactCount, incompatible.Artifacts.Count, "rejected lineage appends no artifact");
            AssertEqual(0, incompatible.HtmlWorkspace.Files.Count, "rejected lineage mutates no workspace file");
        }

        private static void HtmlWorkspaceMessagesUseCanonicalResourceReferences()
        {
            var empty = new ChatSession();
            var emptyId = HtmlWorkspaceArtifactService.CaptureCurrent(empty, "Before chat turn");
            AssertTrue(string.IsNullOrWhiteSpace(emptyId), "empty pre-turn workspace needs no artifact");
            AssertEqual(0, empty.Artifacts.Count, "empty pre-turn workspace creates no artifact");

            var session = new ChatSession();
            var executor = new HtmlWorkspaceToolService();
            var write = new ToolInvocation { ToolId = HtmlWorkspaceToolCatalog.UpsertToolId };
            write.Arguments["resourceType"] = "file";
            write.Arguments["name"] = "index.html";
            write.Arguments["content"] = "<h1>Report</h1>";
            write.Arguments["setActive"] = true;
            var writeResult = executor.Execute(
                write.ToolId, write.Arguments, session, delegate { },
                CancellationToken.None);
            AssertEqual(HtmlWorkspaceOutcomeStatus.Ok, writeResult.Status,
                "html mutation succeeds");
            var revisionId = session.ActiveHtmlArtifactId;

            var writeMessage = new ChatMessage
            {
                Role = "assistant",
                HtmlWorkspaceCheckpoint = HtmlCheckpoint(session, revisionId),
                Activity = new ChatActivity { ToolId = write.ToolId, DataJson = writeResult.DataJson }
            };
            var fileResource = new ResourceGatewayService()
                .List(session, "chat", ChatHtmlResourceCatalog.FileKind, null, 10)
                .Items.Single();
            var read = new ToolInvocation
            {
                ToolId = ResourceToolCatalog.ReadToolId,
                Arguments = { ["target"] = "HTML file: index.html", ["representation"] = "source" }
            };
            var readResult = ReadResource(
                new ResourceGatewayService(),
                session,
                fileResource.Reference.Uri,
                "source",
                null,
                8000).Result;
            var readMessage = new ChatMessage
            {
                Role = "assistant",
                HtmlWorkspaceCheckpoint = HtmlCheckpoint(session, revisionId),
                ResourceRefs = new List<ResourceRef> { HtmlCheckpoint(session, revisionId) },
                Activity = new ChatActivity { ToolId = read.ToolId, DataJson = Newtonsoft.Json.JsonConvert.SerializeObject(readResult) }
            };
            var duplicateMutationMessage = new ChatMessage
            {
                Role = "assistant",
                HtmlWorkspaceCheckpoint = HtmlCheckpoint(session, revisionId),
                Activity = new ChatActivity { ToolId = HtmlWorkspaceToolCatalog.SetActiveToolId, DataJson = writeResult.DataJson }
            };
            session.Messages.Add(writeMessage);
            session.Messages.Add(duplicateMutationMessage);
            session.Messages.Add(readMessage);

            ChatResourceReferenceService.LinkMessageResources(session, 0);

            AssertTrue(ReferencesArtifact(session, writeMessage, revisionId), "html mutation links its canonical revision resource");
            AssertTrue(!ReferencesArtifact(session, duplicateMutationMessage, revisionId), "same html revision is linked only once");
            AssertTrue(!ReferencesArtifact(session, readMessage, revisionId), "html read does not promote the checkpoint to a mutation reference");
        }
    }
}
