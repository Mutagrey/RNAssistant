using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void HtmlWorkspaceCheckpointsStayInternalUntilMutation()
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
                HtmlWorkspaceCheckpointId = revisionId,
                Activity = new ChatActivity { ToolId = write.ToolId, DataJson = writeResult.DataJson }
            };
            var fileResource = new ResourceGatewayService()
                .List(session, "chat", ChatHtmlResourceCatalog.FileKind, null, 10)
                .Items.Single();
            var read = new ToolCommand
            {
                ToolId = ResourceToolExecutor.ReadToolId,
                Arguments = { ["uri"] = fileResource.Reference.Uri, ["representation"] = "source" }
            };
            var readResult = new ResourceGatewayService().Read(
                session,
                fileResource.Reference.Uri,
                "source",
                0,
                8000).Result;
            var readMessage = new ChatMessage
            {
                Role = "assistant",
                HtmlWorkspaceCheckpointId = revisionId,
                ArtifactIds = new List<string> { revisionId },
                Activity = new ChatActivity { ToolId = read.ToolId, DataJson = Newtonsoft.Json.JsonConvert.SerializeObject(readResult) }
            };
            var duplicateMutationMessage = new ChatMessage
            {
                Role = "assistant",
                HtmlWorkspaceCheckpointId = revisionId,
                Activity = new ChatActivity { ToolId = HtmlArtifactToolExecutor.SetActiveToolId, DataJson = writeResult.DataJson }
            };
            session.Messages.Add(writeMessage);
            session.Messages.Add(duplicateMutationMessage);
            session.Messages.Add(readMessage);

            ChatArtifactService.LinkMessageArtifacts(session, 0);

            AssertTrue(writeMessage.ArtifactIds.Contains(revisionId), "html mutation links its revision artifact");
            AssertTrue(!duplicateMutationMessage.ArtifactIds.Contains(revisionId), "same html revision is linked only once");
            AssertTrue(!readMessage.ArtifactIds.Contains(revisionId), "html read does not expose checkpoint artifact");
        }
    }
}
