using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Office.WebView;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void HtmlActionsRejectStaleStateBeforeDispatch()
        {
            WithTempPaths(paths =>
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var persisted = 0;
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths), new ToolStore(paths),
                    persistResourceFacts: saved => persisted++);
                var session = NewSession(adapter);
                var request = new HtmlWorkspaceDeleteFilePayload { ChatId = session.Id,
                    ExpectedActiveHtmlArtifactId = "", ExpectedSessionRevision = session.Revision, Path = "index.html" };
                HtmlWorkspaceActionGuard.Validate(session, request); // Explicit empty workspace is valid for initial import.
                request.ExpectedActiveHtmlArtifactId = null;
                AssertEqual("RESOURCE_REVISION_CHANGED", RuntimeThrows<ResourceRequestException>(() => HtmlWorkspaceActionGuard.Validate(session, request)).ErrorCode,
                    "missing snapshot must not alias the empty workspace");
                request.ExpectedActiveHtmlArtifactId = "";
                request.ExpectedSessionRevision = null;
                RuntimeThrows<ResourceRequestException>(() => HtmlWorkspaceActionGuard.Validate(session, request));
                request.ExpectedSessionRevision = session.Revision;
                request.ChatId = "other";
                AssertEqual("RESOURCE_ACCESS_DENIED", RuntimeThrows<ResourceRequestException>(() => HtmlWorkspaceActionGuard.Validate(session, request)).ErrorCode,
                    "explicit addressed chat must match");
                request.ChatId = session.Id;
                executor.MutateLocalResources(session, "common.html_workspace_write_file", null,
                    () => HtmlWorkspaceToolService.UpsertFile(session, "index.html", "html", "<main>keep</main>", true));
                RuntimeThrows<ResourceRequestException>(() => HtmlWorkspaceActionGuard.Validate(session, request));
                request.ExpectedActiveHtmlArtifactId = session.ActiveHtmlArtifactId;
                HtmlWorkspaceActionGuard.Validate(session, request);
                // A competing operation can restore the same snapshot, while the chat revision still advances.
                session.Revision++;
                var scope = executor.ResourceAuthority.Scope(session, true);
                var identity = HtmlWorkspaceIdentity.Identity(session, HtmlWorkspaceIdentity.LogicalId(session.ActiveHtmlArtifactId));
                var head = executor.ResourceAuthority.Store.Capture(scope).GetHead(identity);
                var dispatched = false;
                AssertEqual("RESOURCE_REVISION_CHANGED", RuntimeThrows<ResourceRequestException>(() =>
                    executor.MutateLocalResources(session, "common.html_workspace_delete", null, () => {
                        dispatched = true;
                        return HtmlWorkspaceToolService.DeleteFile(session, request.Path);
                    }, validateBeforeDispatch: () => HtmlWorkspaceActionGuard.Validate(session, request))).ErrorCode,
                    "ABA chat revision is rejected inside the dispatch gate");
                AssertTrue(!dispatched, "stale action never enters the domain writer");
                AssertEqual("<main>keep</main>", session.HtmlWorkspace.Files.Single().Content, "file remains intact");
                var after = executor.ResourceAuthority.Store.Capture(scope).GetHead(identity);
                AssertEqual(head.Revision.Revision, after.Revision.Revision, "rejected action does not publish a revision");
                AssertEqual(HeadKnowledge.Known, after.Knowledge, "pre-dispatch rejection does not mark the head unknown");
                AssertEqual(1, persisted, "rejected action does not persist an effect");
                AssertEqual(0, new ResourceMutationJournal(paths).Unresolved().Count, "abandoned preparation leaves no unresolved effect");
                request.ExpectedSessionRevision = session.Revision;
                executor.MutateLocalResources(session, "common.html_workspace_delete", null,
                    () => HtmlWorkspaceToolService.DeleteFile(session, request.Path),
                    validateBeforeDispatch: () => HtmlWorkspaceActionGuard.Validate(session, request));
                AssertEqual(0, session.HtmlWorkspace.Files.Count, "fresh explicit request succeeds after rejected preparation releases its lease");
                AssertEqual(2, persisted, "one publication for the accepted delete");
            });
        }

        private static void HtmlActionsBridgePreservesGuards()
        {
            var controller = new AssistantController();
            var bridge = new AssistantWebBridge(controller, null);
            foreach (var method in new[] { "deleteHtmlWorkspaceFile", "deleteHtmlWorkspaceData", "setActiveHtmlWorkspaceFile",
                "restoreHtmlWorkspaceSnapshot", "redoHtmlWorkspaceSnapshot", "importUploadedHtmlToWorkspace", "prepareHtmlWorkspaceExport" })
            {
                var payload = new JObject { ["chatId"] = "chat-html", ["expectedSessionRevision"] = 17,
                    ["expectedActiveHtmlArtifactId"] = "html-r3", ["path"] = "index.html", ["name"] = "sales",
                    ["snapshotId"] = "selected", ["sourceResourceUri"] = "rna://exact/original", ["targetPath"] = "import.html" };
                var response = JObject.Parse(bridge.HandleMessageAsync(new JObject { ["id"] = method, ["type"] = method,
                    ["bridgeToken"] = BridgeToken(bridge), ["payload"] = payload }.ToString()).GetAwaiter().GetResult());
                AssertTrue(response["ok"].Value<bool>(), method + " routes typed controls");
                AssertEqual("chat-html", controller.LastHtmlAction.ChatId, method + " preserves source chat");
                AssertEqual(17L, controller.LastHtmlAction.ExpectedSessionRevision.Value, method + " preserves revision");
                AssertEqual("html-r3", controller.LastHtmlAction.ExpectedActiveHtmlArtifactId, method + " preserves active snapshot");
                if (controller.LastHtmlAction is HtmlWorkspaceRestorePayload restore)
                    AssertEqual("selected", restore.SnapshotId, method + " preserves selected undo/redo target");
            }
        }
    }
}
