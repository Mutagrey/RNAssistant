using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void HtmlWorkspaceBranchesUseUniqueMonotonicRevisions()
        {
            var session = new ChatSession();
            HtmlArtifactToolExecutor.UpsertFile(session, "index.html", "html", "root", true);
            var rootId = session.ActiveHtmlArtifactId;
            AssertEqual(1, session.Artifacts.Single(item => item.Id == rootId).Revision, "root revision");

            HtmlArtifactToolExecutor.UpsertFile(session, "index.html", "html", "branch A", true);
            var branchAId = session.ActiveHtmlArtifactId;
            AssertEqual(2, session.Artifacts.Single(item => item.Id == branchAId).Revision, "first branch revision");
            HtmlArtifactToolExecutor.UpsertFile(session, "index.html", "html", "branch A child", true);
            var branchAChildId = session.ActiveHtmlArtifactId;
            AssertEqual(3, session.Artifacts.Single(item => item.Id == branchAChildId).Revision, "first branch child revision");

            HtmlArtifactToolExecutor.RestoreSnapshot(session, rootId);
            HtmlArtifactToolExecutor.UpsertFile(session, "index.html", "html", "branch B", true);
            var branchBId = session.ActiveHtmlArtifactId;
            var branchB = session.Artifacts.Single(item => item.Id == branchBId);
            AssertEqual(4, branchB.Revision, "alternative branch uses the next global revision");
            AssertEqual(rootId, branchB.ParentArtifactId, "alternative branch keeps the exact active parent");

            HtmlArtifactToolExecutor.RestoreSnapshot(session, rootId);
            AssertEqual(2, session.HtmlWorkspace.RedoBranches.Count, "both direct branches remain available");
            HtmlArtifactToolExecutor.RedoSnapshot(session, branchBId);
            HtmlArtifactToolExecutor.UpsertFile(session, "index.html", "html", "branch B child", true);
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
                HtmlArtifactToolExecutor.UpsertFile(incompatible, "index.html", "html", "must not write", true));
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
            var executor = new HtmlArtifactToolExecutor();
            var write = new ToolCommand { ToolId = HtmlArtifactToolExecutor.UpsertToolId };
            write.Arguments["resourceType"] = "file";
            write.Arguments["name"] = "index.html";
            write.Arguments["content"] = "<h1>Report</h1>";
            write.Arguments["setActive"] = true;
            var writeResult = executor.ExecuteControllerTool(write, session, false);
            AssertTrue(writeResult.Success, "html mutation succeeds");
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
            var read = new ToolCommand
            {
                ToolId = ResourceToolCatalog.ReadToolId,
                Arguments = { ["uri"] = fileResource.Reference.Uri, ["representation"] = "source" }
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
                Activity = new ChatActivity { ToolId = HtmlArtifactToolExecutor.SetActiveToolId, DataJson = writeResult.DataJson }
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
