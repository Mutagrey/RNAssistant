using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static ArtifactTransferPackage HtmlProjectSourceFixture()
        {
            var package = TransferFixture(); package.Data.Clear();
            package.Files.Add(new ArtifactTransferFile { Path = "assets/site.css", Kind = "css", Content = "h1 { color: green; }\r\n" });
            return package;
        }

        private static void HtmlProjectImportExactAndIndependent()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), (executor, adapter) =>
            {
                var paths = FixturePaths.Value; var chats = new ChatStore(paths);
                var registry = new DocumentAuthorityRegistry(paths);
                var links = new ArtifactWorkingSetService(chats.DocumentArtifacts, new ResourceMutationJournal(paths));
                var catalog = new ArtifactCatalogService(registry, chats.DocumentArtifacts, links, new ResourceAuthorityStore(paths), new ChatBlobStore(paths));
                var author = NewSession(adapter); author.DocumentAuthorityId = registry.Resolve("word", "source", Path.Combine(paths.Root, "source.docx")).Id;
                var reader = NewSession(adapter); reader.DocumentAuthorityId = registry.Resolve("word", "reader", Path.Combine(paths.Root, "reader.docx")).Id;
                AssertTrue(WriteSharedHtmlFixture(executor, author, "<h1>version one</h1>").Success, "publish source");
                var first = ChatResourceUri.CreateArtifactRevisionUri(author, author.Artifacts.Single(a => a.Id == author.ActiveHtmlArtifactId));
                AssertTrue(WriteSharedHtmlFixture(executor, author, "<h1>version two</h1>").Success, "advance source head");
                var selected = author.ActiveHtmlArtifactId;
                var source = catalog.ReadHtmlProjectSource(reader, author.DocumentAuthorityId, first);
                AssertEqual("<h1>version one</h1>", source.Files.Single().Content, "copy reads exact historical source in the named document");
                var result = new HtmlProjectImportService(paths).Import(source, CancellationToken.None);
                AssertEqual(HtmlProjectImportStatus.Created, result.Status, "verified import");
                AssertEqual(selected, author.ActiveHtmlArtifactId, "import never retargets source workspace");
                AssertTrue(reader.ArtifactLinks.Count == 0 && reader.ActiveHtmlArtifactId == null, "copy does not attach to the reader chat");
                var store = new WorkspaceStore(paths); var reopened = store.OpenConnected(result.WorkspaceId);
                AssertEqual("index.html", reopened.HtmlEntryPath, "entry configuration survives reopen");
                AssertTrue(store.GetHtmlProjectDefinition(reopened).Definition.Members.SequenceEqual(source.Files.Select(file => file.Path).OrderBy(path => path, StringComparer.Ordinal)),
                    "import persists only the exact transferred member-set");
                AssertEqual(source.Title, reopened.Name, "project has the source title, not its private directory ID");
                AssertEqual(source.Files.Single().Content, File.ReadAllText(Path.Combine(reopened.RootPath, "index.html")), "exact source bytes copied");
                AssertEqual(result.WorkspaceId, store.ListConnected(ConversationStore(chats)).Single().WorkspaceId, "one existing workspace catalog entry");
                var files = new WorkspaceFileService(paths); var saved = files.CaptureFile(reopened, "index.html", 1024);
                File.WriteAllText(Path.Combine(reopened.RootPath, "index.html"), "local edit");
                AssertEqual("<h1>version one</h1>", files.ReadExactText(reopened, "index.html", saved.Reference).Text, "file authority retains its own exact CAS history");
                AssertEqual("<h1>version one</h1>", catalog.ReadHtmlProjectSource(reader, author.DocumentAuthorityId, first).Files.Single().Content, "editing the project does not change the saved artifact");
                var refused = false;
                try { catalog.ReadHtmlProjectSource(reader, reader.DocumentAuthorityId, first); }
                catch (Exception) { refused = true; }
                AssertTrue(refused, "a source URI cannot be read under a foreign document binding");
            });
        }

        private static void HtmlProjectImportValidatesBeforeWrites()
        {
            WithTempPaths(paths =>
            {
                var service = new HtmlProjectImportService(paths);
                var cases = new Action<ArtifactTransferPackage>[] {
                    p => p.Data.Add(new ArtifactTransferData { Name = "document-data" }),
                    p => p.ExternalDependencies.Add("https://example.test/app.js"),
                    p => p.Files[1].Path = "../escape.js",
                    p => p.Files[1].Path = ".rnassistant/workspace.json",
                    p => p.Files[1].Path = ".git/config",
                    p => p.Files[1].Path = "CON.js",
                    p => p.Files[1].Path = "bad\"name.js",
                    p => p.Files[2].Path = "Assets/site.css",
                    p => p.Files[2].Path = "assets",
                    p => p.EntryPath = "missing.html",
                    p => p.Files[1].Content = "RN.resources.open('rows')",
                    p => p.Files[1].Content = "echarts.init(document.body)",
                    p => p.Files[1].Content = "/* rn-vendor:test@1.0.0 */",
                    p => p.Files[1].Content = "\ud800",
                    p => p.Files[1].Content = new string('x', HtmlProjectImportService.MaximumSourceBytes + 1)
                };
                foreach (var change in cases)
                {
                    var package = HtmlProjectSourceFixture(); change(package);
                    var result = service.Import(package, CancellationToken.None);
                    AssertEqual(HtmlProjectImportStatus.Rejected, result.Status, "unsupported package rejected before writes");
                    AssertTrue(!Directory.Exists(Path.Combine(paths.Root, "html-projects")), "preflight creates no project folder");
                }
                var cancelled = new CancellationToken(true);
                AssertEqual(HtmlProjectImportStatus.Rejected, service.Import(HtmlProjectSourceFixture(), cancelled).Status, "cancel before dispatch");
                AssertTrue(!Directory.Exists(Path.Combine(paths.Root, "html-projects")), "cancel creates no project folder");
                var created = service.Import(HtmlProjectSourceFixture(), CancellationToken.None);
                AssertEqual(HtmlProjectImportStatus.Created, created.Status, "valid multi-file source accepted");
                AssertEqual("h1 { color: green; }\r\n", File.ReadAllText(Path.Combine(created.RootPath, "assets/site.css")), "nested paths and line endings preserved");
            });
        }

        private static void HtmlProjectImportUnknownIsNotPublishedOrReplayed()
        {
            WithTempPaths(paths =>
            {
                var files = new WorkspaceFileService(paths); var dispatches = 0;
                files.FaultPoint = stage => { if (stage == "after-dispatch") { dispatches++; throw new IOException("lost write receipt"); } };
                var source = HtmlProjectSourceFixture();
                var result = new HtmlProjectImportService(paths, files).Import(source, CancellationToken.None);
                AssertEqual(HtmlProjectImportStatus.Unknown, result.Status, "possible effect is not reported as created or rejected");
                AssertEqual(1, dispatches, "write is never retried");
                AssertTrue(File.Exists(Path.Combine(result.RootPath, "index.html")), "partial files remain available for explicit inspection");
                AssertEqual(0, new WorkspaceStore(paths).ListConnected(ConversationStore(new ChatStore(paths))).Length, "partial workspace stays out of the project catalog after restart");
                AssertTrue(result.Message.Contains(result.RootPath), "screen-readable recovery points to the exact folder");
                AssertEqual(source.Files[0].Content, File.ReadAllText(Path.Combine(result.RootPath, "index.html")), "no rollback deletes or rewrites source bytes");
            });
            WithTempPaths(paths =>
            {
                var files = new WorkspaceFileService(paths); var leases = 0;
                var package = HtmlProjectSourceFixture(); package.Files.RemoveRange(1, package.Files.Count - 1);
                files.FaultPoint = stage => { if (stage == "before-file-lease" && ++leases == 2)
                    File.WriteAllText(Path.Combine(Directory.GetDirectories(Path.Combine(paths.Root, "html-projects")).Single(), "index.html"), "external edit"); };
                var result = new HtmlProjectImportService(paths, files).Import(package, CancellationToken.None);
                AssertEqual(HtmlProjectImportStatus.Unknown, result.Status, "whole-project read-back detects changed bytes");
                AssertEqual(0, new WorkspaceStore(paths).ListConnected(ConversationStore(new ChatStore(paths))).Length, "failed read-back is not published");
                AssertEqual("external edit", File.ReadAllText(Path.Combine(result.RootPath, "index.html")), "conflicting bytes are preserved");
            });
        }
    }
}
