using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void SourceComparisonBoundsAndMembership()
        {
            var reads = 0;
            var left = new[] { new SourceComparisonMember { Path = "keep", Fingerprint = "a", Length = 1, Read = () => { reads++; return "a"; } },
                new SourceComparisonMember { Path = "gone", Fingerprint = "b", Length = 1, Read = () => "b" } };
            var right = new[] { left[0], new SourceComparisonMember { Path = "empty", Fingerprint = "empty", Length = 0, Read = () => "" },
                new SourceComparisonMember { Path = "large", Fingerprint = "large", Length = 256001, Read = () => { throw new Exception("Must not read oversized payload"); } } };
            var request = new PluginSourceComparisonRequest { BeforeId = "old", AfterId = "new" };
            Func<SourceComparisonDto> read = () => SourceComparisonService.Read(request, "old", "new", left, right);
            var result = read();
            AssertEqual(0, reads, "listing does not materialize member bodies");
            AssertEqual(SourceFileChange.Added, result.Files.Single(f => f.Path == "empty").Change, "empty file is added, not unchanged");
            AssertEqual(SourceFileChange.Removed, result.Files.Single(f => f.Path == "gone").Change, "missing file is removed");
            request.Path = "keep"; AssertTrue(read().Complete && reads == 2, "only selected pair is read");
            request.Path = "large"; result = read();
            AssertTrue(!result.Complete && result.BeforeText == null && result.AfterText == null, "oversized files have no misleading partial diff");
            request.Path = "absent"; RuntimeThrows<InvalidOperationException>(() => read());
            RuntimeThrows<System.Text.DecoderFallbackException>(() => SourceComparisonService.Utf8(new byte[] { 0xff }));
        }

        private static void SourceComparisonHtmlExactSources()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), (executor, adapter) => {
                var paths = FixturePaths.Value; var chats = new ChatStore(paths); var documents = new DocumentAuthorityRegistry(paths);
                var session = NewSession(adapter);
                session.DocumentAuthorityId = documents.Resolve("word", "source", Path.Combine(paths.Root, "source.docx")).Id;
                var catalog = new ArtifactCatalogService(documents, chats.DocumentArtifacts,
                    new ArtifactWorkingSetService(chats.DocumentArtifacts, new ResourceMutationJournal(paths)),
                    new ResourceAuthorityStore(paths), new ChatBlobStore(paths));
                AssertTrue(WriteSharedHtmlFixture(executor, session, "<h1>before</h1>").Success, "first saved source");
                var first = ChatResourceUri.CreateArtifactRevisionUri(session, session.Artifacts.Single(a => a.Id == session.ActiveHtmlArtifactId));
                executor.MutateLocalResources(session, "html.source.write", new Dictionary<string, object>(), () => {
                    session.HtmlWorkspace.DataSources.Add(new HtmlWorkspaceDataSource { Name = "unavailable", Binding = new HtmlWorkspaceDataBinding {
                        Resource = new ResourceRef(ResourceUri.Create("excel", session.DocumentAuthorityId, "range", "Sheet1", "A1"), "missing"), Policy = "exact", View = "text" } });
                    return HtmlWorkspaceToolService.UpsertFile(session, "index.html", "html", "<h1>after</h1>", true);
                });
                var after = ChatResourceUri.CreateArtifactRevisionUri(session, session.Artifacts.Single(a => a.Id == session.ActiveHtmlArtifactId));
                var selected = session.ActiveHtmlArtifactId; var revision = session.Revision;
                var request = new HtmlSourceComparisonRequest { ChatId = session.Id, DocumentId = session.DocumentAuthorityId,
                    BeforeId = first, AfterId = after, Path = "index.html" };
                var result = catalog.CompareHtmlSources(session, request);
                AssertEqual("<h1>before</h1>", result.BeforeText, "exact old source despite missing preview data");
                AssertEqual("<h1>after</h1>", result.AfterText, "exact new source");
                AssertEqual(SourceFileChange.Changed, result.Files.Single().Change, "source change marked");
                AssertEqual(selected, session.ActiveHtmlArtifactId, "comparison does not change selected publication");
                AssertEqual(revision, session.Revision, "read does not mutate the chat");
                var other = NewSession(adapter); other.DocumentAuthorityId = session.DocumentAuthorityId;
                AssertTrue(WriteSharedHtmlFixture(executor, other, "other project").Success, "another project");
                request.BeforeId = ChatResourceUri.CreateArtifactRevisionUri(other, other.Artifacts.Single(a => a.Id == other.ActiveHtmlArtifactId));
                RuntimeThrows<InvalidOperationException>(() => catalog.CompareHtmlSources(session, request));
                request.BeforeId = first + "/source"; RuntimeThrows<InvalidOperationException>(() => catalog.CompareHtmlSources(session, request));
                request.BeforeId = first; request.ChatId = "foreign"; RuntimeThrows<InvalidOperationException>(() => catalog.CompareHtmlSources(session, request));
                request.ChatId = session.Id;
                request.DocumentId = documents.Resolve("word", "other", Path.Combine(paths.Root, "other.docx")).Id;
                RuntimeThrows<InvalidDataException>(() => catalog.CompareHtmlSources(session, request));
            });
        }

        private static void SourceComparisonPluginPublishedPair()
        {
            WithPluginPaths(paths => { using (var service = new PluginApplicationService(paths)) {
                var item = PluginFixture(service, paths, false); var first = item.ReleaseId;
                var source = Path.Combine(item.SourceRoot, "app.js"); var original = File.ReadAllText(source);
                File.WriteAllText(source, "new source\r\n");
                var manifestPath = Path.Combine(item.SourceRoot, "plugin.json"); var manifest = JObject.Parse(File.ReadAllText(manifestPath));
                foreach (var name in new[] { "binary.bin", "large.txt", "empty.txt" }) ((JArray)manifest["files"]).Add(name);
                File.WriteAllBytes(Path.Combine(item.SourceRoot, "binary.bin"), new byte[] { 0xff, 0 });
                File.WriteAllText(Path.Combine(item.SourceRoot, "large.txt"), new string('x', 256001));
                File.WriteAllText(Path.Combine(item.SourceRoot, "empty.txt"), ""); File.WriteAllText(manifestPath, manifest.ToString());
                var candidate = service.Check(item.PluginId);
                var preview = service.Open(new PluginOpenRequest { PluginId = item.PluginId, CandidateId = candidate.CandidateId });
                var request = new PluginSourceComparisonRequest { PluginId = item.PluginId, BeforeId = first, AfterId = preview.ReleaseId, Path = "app.js" };
                PluginFailure(() => service.CompareSources(request), "plugin_release_missing");
                service.CloseInstanceAsync(preview.InstanceId).GetAwaiter().GetResult();
                item = service.Apply(new PluginApplyRequest { PluginId = item.PluginId, CandidateId = candidate.CandidateId });
                request.AfterId = item.ReleaseId;
                File.WriteAllText(source, "dirty working source");
                var result = service.CompareSources(request);
                AssertEqual(original, result.BeforeText, "old published bytes");
                AssertEqual("new source\r\n", result.AfterText, "saved source, not dirty working tree; EOL retained");
                AssertTrue(result.BeforeLabel != result.AfterLabel, "same manifest version still has distinct publication labels");
                request.Path = "binary.bin"; AssertTrue(!service.CompareSources(request).Complete, "binary file explicitly unavailable");
                request.Path = "large.txt"; AssertTrue(!service.CompareSources(request).Complete, "large file explicitly bounded");
                request.Path = "empty.txt"; result = service.CompareSources(request);
                AssertTrue(result.Complete && result.AfterText == "" && result.Files.Single(f => f.Path == request.Path).Change == SourceFileChange.Added,
                    "added empty file remains a file-level change");
                AssertEqual(item.ReleaseId, service.ReleaseHistory(new PluginReleaseHistoryRequest { PluginId = item.PluginId }).Items.Single(v => v.Active).ReleaseId,
                    "active release is unchanged");
                AssertEqual("dirty working source", File.ReadAllText(source), "comparison never restores source files");
            } });
        }
    }
}
