using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ArtifactLibraryProjectsImmutableClasses()
        {
            var session = NewSession(FakeOfficeAdapter.ForHost("Excel"));
            session.Revision = 17;
            var original = Artifact("upload-audio", ChatArtifactKinds.Attachment, 7, null, 1);
            original.MimeType = "audio/wav";
            original.ContentByteLength = 4096;
            original.MetadataJson = JsonConvert.SerializeObject(new { attachmentId = "draft-1", Kind = "audio" });
            var firstChart = Artifact("chart-1", ChatArtifactKinds.Chart, 1, null, 2);
            var secondChart = Artifact("chart-2", ChatArtifactKinds.Chart, 2, firstChart.Id, 3);
            var generatedImage = Artifact("generated-image", ChatArtifactKinds.Image, 4, null, 4);
            session.Artifacts.AddRange(new[] { original, firstChart, secondChart, generatedImage });

            var projection = ArtifactLibraryProjectionService.Project(session);

            AssertEqual(17L, projection.SessionRevision, "library session revision");
            AssertEqual(4, projection.Heads.Count, "immutable artifacts remain separate heads");
            var upload = projection.Heads.Single(item => item.ArtifactId == original.Id);
            AssertEqual(ArtifactLibraryResourceClasses.ImmutableOriginal, upload.ResourceClass, "upload class");
            AssertEqual(ArtifactLibraryGroups.FilesMedia, upload.Group, "upload group");
            AssertEqual("audio", upload.DisplayKind, "attachment display kind");
            AssertEqual("Original", upload.VersionLabel, "upload label ignores stored revision");
            AssertTrue(upload.ResourceUri.EndsWith("/revision/7", StringComparison.Ordinal), "upload exact URI");
            AssertEqual(1, upload.HistoryCount, "original has one exact history entry");
            AssertTrue(upload.History == null, "ordinary projection omits exact history");

            var charts = projection.Heads.Where(item => item.Kind == ChatArtifactKinds.Chart).ToList();
            AssertEqual(2, charts.Count, "snapshot parent links do not collapse library rows");
            AssertTrue(charts.All(item => item.ResourceClass == ArtifactLibraryResourceClasses.ImmutableSnapshot), "chart class");
            AssertTrue(charts.All(item => item.VersionLabel == null), "snapshots have no version badge");
            AssertEqual(ArtifactLibraryResourceClasses.ImmutableSnapshot,
                projection.Heads.Single(item => item.ArtifactId == generatedImage.Id).ResourceClass,
                "generated image is not an uploaded original");
        }

        private static void ArtifactLibraryProjectsExactHeadsAndHistory()
        {
            var session = NewSession(FakeOfficeAdapter.ForHost("Excel"));
            var plan1 = Artifact("plan-r1", ChatArtifactKinds.PlanDocument, 1, null, 1);
            plan1.MetadataJson = JsonConvert.SerializeObject(new { planId = "release-plan", status = "draft" });
            var plan2 = Artifact("plan-r2", ChatArtifactKinds.PlanDocument, 2, plan1.Id, 2);
            plan2.MetadataJson = JsonConvert.SerializeObject(new { planId = "release-plan", status = "ready" });
            var html1 = Artifact("html-r1", ChatArtifactKinds.HtmlWorkspace, 1, null, 3);
            var htmlLeft = Artifact("html-left-r2", ChatArtifactKinds.HtmlWorkspace, 2, html1.Id, 4);
            var htmlRight = Artifact("html-right-r3", ChatArtifactKinds.HtmlWorkspace, 3, html1.Id, 5);
            session.Artifacts.AddRange(new[] { plan1, plan2, html1, htmlLeft, htmlRight });
            session.ActivePlanDocumentArtifactId = plan2.Id;
            session.ActiveHtmlArtifactId = htmlLeft.Id;

            var projection = ArtifactLibraryProjectionService.Project(session);
            var plan = projection.Heads.Single(item => item.LogicalId == "release-plan");
            AssertEqual(plan2.Id, plan.ArtifactId, "active Plan head");
            AssertEqual("plan", plan.DisplayKind, "Plan display kind");
            AssertEqual("v2", plan.VersionLabel, "Plan version label");
            AssertEqual("ready", plan.Status, "Plan status");
            AssertEqual(2, plan.HistoryCount, "Plan history count");
            var planHistory = ArtifactLibraryProjectionService.History(session, new ArtifactLibraryHistoryRequest
            { ChatId = session.Id, ExpectedSessionRevision = session.Revision, HeadArtifactId = plan.ArtifactId });
            var planRevision = planHistory.Items.Single(item => item.ArtifactId == plan2.Id);
            AssertTrue(planRevision.ParentResourceUri.EndsWith("/artifact/plan-r1/revision/1", StringComparison.Ordinal),
                "Plan exact parent URI");

            var html = projection.Heads.Single(item => item.Kind == ChatArtifactKinds.HtmlWorkspace);
            AssertEqual(htmlLeft.Id, html.ArtifactId, "HTML active pointer wins a newer alternative branch revision");
            AssertEqual(3, html.HistoryCount, "HTML branch history count");
            var htmlHistory = ArtifactLibraryProjectionService.History(session, new ArtifactLibraryHistoryRequest
            { ChatId = session.Id, ExpectedSessionRevision = session.Revision, HeadArtifactId = html.ArtifactId });
            AssertEqual("head", htmlHistory.Items.Single(item => item.ArtifactId == htmlLeft.Id).Relation, "HTML head relation");
            AssertEqual("ancestor", htmlHistory.Items.Single(item => item.ArtifactId == html1.Id).Relation, "HTML ancestor relation");
            AssertEqual("branch", htmlHistory.Items.Single(item => item.ArtifactId == htmlRight.Id).Relation, "HTML alternative branch relation");
            AssertTrue(htmlHistory.Items.All(item => item.ResourceUri.Contains("/artifact/" + item.ArtifactId + "/revision/")),
                "history URIs stay exact per artifact");
            var visible = new ChatMessageViewDto { ResourceRefs = new[] { ChatResourceUri.CreateArtifactRevision(session, htmlRight) } };
            var state = ArtifactLibraryProjectionService.ProjectState(session, new[] { visible });
            var pinned = state.Artifacts.Single(item => item.Id == htmlRight.Id);
            AssertEqual(htmlLeft.Id, pinned.LibraryHead.ArtifactId, "pinned HTML card knows the selected branch head");
            AssertTrue(pinned.ResourceUri.EndsWith("/artifact/html-right-r3/revision/3", StringComparison.Ordinal),
                "pinned HTML card keeps its exact branch URI");
            AssertTrue(pinned.LibraryRevision.ParentResourceUri.EndsWith("/artifact/html-r1/revision/1", StringComparison.Ordinal),
                "pinned HTML card keeps its exact parent relation");
        }

        private static void ArtifactLibraryProjectsDerivedResources()
        {
            var session = NewSession(FakeOfficeAdapter.ForHost("Excel"));
            var derived = Artifact("ocr-text", ChatArtifactKinds.Markdown, 1, null, 1);
            derived.MetadataJson = JsonConvert.SerializeObject(new
            {
                derivedFromUri = "rna://chat/source/artifact/upload/revision/1",
                producer = "ocr"
            });
            session.Artifacts.Add(derived);

            var item = ArtifactLibraryProjectionService.Project(session).Heads.Single();
            AssertEqual(ArtifactLibraryResourceClasses.DerivedResource, item.ResourceClass, "derived class overrides kind");
            AssertEqual(ArtifactLibraryGroups.GeneratedSnapshots, item.Group, "derived group");
            AssertEqual("Derived", item.VersionLabel, "derived label");
            AssertEqual("rna://chat/source/artifact/upload/revision/1", item.DerivedFromResourceUri, "derived source URI");
            AssertEqual(1, item.HistoryCount, "derived resource is one immutable exact row");
        }

        private static ChatArtifact Artifact(string id, string kind, int revision, string parentId, int minute)
        {
            return new ChatArtifact
            {
                Id = id,
                Kind = kind,
                Title = id,
                Revision = revision,
                ParentArtifactId = parentId,
                CreatedUtc = new DateTime(2026, 8, 31, 10, minute, 0, DateTimeKind.Utc),
                SourceMessageId = "message-" + id,
                RunId = "run-" + id
            };
        }

        private static void ArtifactLibrarySyntheticTransport()
        {
            var session = NewSession(FakeOfficeAdapter.ForHost("Excel"));
            session.Revision = 44;
            for (var head = 0; head < 1000; head++)
            {
                string parent = null;
                for (var revision = 1; revision <= 6; revision++)
                {
                    var id = "plan-" + head + "-r" + revision;
                    var item = Artifact(id, ChatArtifactKinds.PlanDocument, revision, parent, 1);
                    item.MetadataJson = JsonConvert.SerializeObject(new { planId = "plan-" + head, status = "ready" });
                    session.Artifacts.Add(item);
                    parent = id;
                }
            }
            session.ActivePlanDocumentArtifactId = "plan-999-r6";
            var presentation = ArtifactLibraryProjectionService.ProjectState(session, new ChatMessageViewDto[0]);
            var after = new { artifacts = presentation.Artifacts, artifactLibrary = presentation.Library };
            var bytes = Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(after));
            Console.WriteLine("artifact library synthetic current bytes: " + bytes);
            var chatStateBytes = Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(new ChatStateResponse
            { ActiveChatId = session.Id, Messages = new ChatMessageViewDto[0],
                Artifacts = presentation.Artifacts, ArtifactLibrary = presentation.Library }));
            Console.WriteLine("artifact library synthetic ChatState bytes: " + chatStateBytes);
            AssertTrue(bytes < 200000, "ordinary artifact payload stays bounded for 1000 heads and 6000 revisions");
            AssertTrue(chatStateBytes < 200000, "ordinary ChatState stays bounded for 1000 heads and 6000 revisions");
            AssertTrue(presentation.Artifacts.Count <= ArtifactLibraryProjectionService.PageSize + 1,
                "ordinary ChatState omits unloaded revision cards");
            AssertTrue(presentation.Library.Heads.All(item => item.History == null),
                "ordinary ChatState omits exact history bodies");
            AssertTrue(presentation.Library.Heads.Count <= ArtifactLibraryProjectionService.PageSize + 1,
                "initial heads page includes at most the active Plan outside the page");
            AssertTrue(presentation.Library.Heads.Any(item => item.ArtifactId == session.ActivePlanDocumentArtifactId),
                "active Plan remains available");
            AssertTrue(presentation.Artifacts.Any(item => item.Id == session.ActivePlanDocumentArtifactId),
                "active Plan card remains available");
            var headRequest = new ArtifactLibraryPageRequest { ChatId = session.Id,
                ExpectedSessionRevision = session.Revision, Cursor = presentation.Library.NextCursor };
            var page = ArtifactLibraryProjectionService.Page(session, headRequest);
            AssertEqual(50, page.Heads.Count, "head page bound");
            var old = ArtifactLibraryProjectionService.History(session, new ArtifactLibraryHistoryRequest
            { ChatId = session.Id, ExpectedSessionRevision = session.Revision,
                HeadArtifactId = "plan-999-r6", TargetArtifactId = "plan-999-r1" });
            AssertEqual(1, old.Items.Count, "targeted old revision count");
            AssertEqual("plan-999-r1", old.Items[0].ArtifactId, "targeted old revision identity");
            AssertTrue(old.Items[0].ResourceUri.EndsWith("/artifact/plan-999-r1/revision/1", StringComparison.Ordinal),
                "targeted old revision exact URI");
            session.Artifacts[5].Title = "changed without session revision";
            RuntimeThrows<InvalidOperationException>(() => ArtifactLibraryProjectionService.Page(session, headRequest));
            session.Revision++;
            RuntimeThrows<InvalidOperationException>(() => ArtifactLibraryProjectionService.Page(session, headRequest));
        }

        private static void ArtifactLibraryHistoryPagesRejectStale()
        {
            var session = NewSession(FakeOfficeAdapter.ForHost("Excel"));
            session.Revision = 20;
            string parent = null;
            for (var number = 1; number <= 121; number++)
            {
                var id = "long-plan-r" + number;
                var item = Artifact(id, ChatArtifactKinds.PlanDocument, number, parent, 1);
                item.MetadataJson = JsonConvert.SerializeObject(new { planId = "long-plan" });
                session.Artifacts.Add(item);
                parent = id;
            }
            session.ActivePlanDocumentArtifactId = parent;
            var request = new ArtifactLibraryHistoryRequest { ChatId = session.Id,
                ExpectedSessionRevision = session.Revision, HeadArtifactId = parent };
            var first = ArtifactLibraryProjectionService.History(session, request);
            AssertEqual(121, first.TotalCount, "history total count");
            AssertEqual(50, first.Items.Count, "history first page bound");
            AssertTrue(first.NextCursor != null, "history has continuation");
            request.Cursor = first.NextCursor;
            var second = ArtifactLibraryProjectionService.History(session, request);
            AssertEqual(50, second.Items.Count, "history second page bound");
            request.Cursor = second.NextCursor;
            var last = ArtifactLibraryProjectionService.History(session, request);
            AssertEqual(21, last.Items.Count, "history last page count");
            AssertEqual("long-plan-r1", last.Items.Last().ArtifactId, "oldest exact revision on last page");
            session.Artifacts[0].ParentArtifactId = "unexpected-parent";
            RuntimeThrows<InvalidOperationException>(() => ArtifactLibraryProjectionService.History(session, request));
            session.Revision++;
            RuntimeThrows<InvalidOperationException>(() => ArtifactLibraryProjectionService.History(session, request));
        }
    }
}
