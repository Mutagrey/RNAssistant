using System;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
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
            AssertEqual(1, upload.History.Count, "original has one exact history entry");

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
            AssertEqual(2, plan.History.Count, "Plan history count");
            var planRevision = plan.History.Single(item => item.ArtifactId == plan2.Id);
            AssertTrue(planRevision.ParentResourceUri.EndsWith("/artifact/plan-r1/revision/1", StringComparison.Ordinal),
                "Plan exact parent URI");

            var html = projection.Heads.Single(item => item.Kind == ChatArtifactKinds.HtmlWorkspace);
            AssertEqual(htmlLeft.Id, html.ArtifactId, "HTML active pointer wins a newer alternative branch revision");
            AssertEqual(3, html.History.Count, "HTML branch history count");
            AssertEqual("head", html.History.Single(item => item.ArtifactId == htmlLeft.Id).Relation, "HTML head relation");
            AssertEqual("ancestor", html.History.Single(item => item.ArtifactId == html1.Id).Relation, "HTML ancestor relation");
            AssertEqual("branch", html.History.Single(item => item.ArtifactId == htmlRight.Id).Relation, "HTML alternative branch relation");
            AssertTrue(html.History.All(item => item.ResourceUri.Contains("/artifact/" + item.ArtifactId + "/revision/")),
                "history URIs stay exact per artifact");
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
            AssertEqual(1, item.History.Count, "derived resource is one immutable exact row");
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
    }
}
