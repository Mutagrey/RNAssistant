using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static ArtifactTransferPackage TransferFixture()
        {
            return new ArtifactTransferPackage { Title = "Dashboard", EntryPath = "index.html", Files = new List<ArtifactTransferFile> {
                new ArtifactTransferFile { Path = "index.html", Kind = "html", Content = "<!doctype html><h1>Тест</h1>" },
                new ArtifactTransferFile { Path = "assets/app.js", Kind = "js", Content = "console.log('ok');" } },
                Data = new List<ArtifactTransferData> { new ArtifactTransferData { Name = "sales", Json = "[{\"amount\":12}]", View = "records", ViewPath = "$", SchemaJson = "{\"type\":\"array\"}" } } };
        }
        private static void ArtifactTransferRoundtrip()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) => {
                var paths = FixturePaths.Value;
                var chats = new ChatStore(paths);
                var links = new ArtifactWorkingSetService(chats.DocumentArtifacts, new ResourceMutationJournal(paths));
                var registry = new DocumentAuthorityRegistry(paths);
                var source = NewSession(adapter);
                source.DocumentAuthorityId = registry.Resolve("excel", "source", Path.Combine(paths.Root, "source.xlsx")).Id;
                var target = NewSession(adapter);
                target.DocumentAuthorityId = registry.Resolve("excel", "target", Path.Combine(paths.Root, "target.xlsx")).Id;
                var catalog = new ArtifactCatalogService(registry, chats.DocumentArtifacts, links, new ResourceAuthorityStore(paths), new ChatBlobStore(paths));
                var fixture = TransferFixture();
                var first = catalog.Import(source, fixture.Export(), "fixture.rna-html.zip", "application/zip", Guid.NewGuid().ToString("N"));
                links.Change(source, LinkRequest(source, ChatResourceUri.CreateArtifactRevisionUri(source, first), false), chats.Save);
                AssertEqual(2, source.HtmlWorkspace.Files.Count, "imported HTML restores files");
                AssertEqual("index.html", source.HtmlWorkspace.ActiveFileId, "entry retained");
                var listing = catalog.List(target, new ArtifactCatalogRequest { ChatId = target.Id, Scope = "all", Kind = "html_workspace" });
                AssertEqual(1, listing.Items.Count, "closed source document is visible");
                AssertTrue(!listing.Items[0].Linked && target.ActiveHtmlArtifactId == null, "listing does not select or attach");
                var export = catalog.Export(target, new ArtifactTransferRequest { ChatId = target.Id, DocumentId = source.DocumentAuthorityId,
                    ResourceUri = listing.Items[0].ResourceUri });
                var decoded = ArtifactTransferPackage.Import(export.Bytes);
                AssertEqual(fixture.Files[1].Content, decoded.Files[1].Content, "sources survive export");
                AssertEqual(fixture.Data[0].Json, decoded.Data[0].Json, "data preserved byte for byte");
                AssertEqual(fixture.Data[0].SchemaJson, decoded.Data[0].SchemaJson, "schema dependency preserved");
                var operation = Guid.NewGuid().ToString("N");
                var copied = catalog.Import(target, export.Bytes, export.FileName, export.ContentType, operation);
                links.Change(target, LinkRequest(target, ChatResourceUri.CreateArtifactRevisionUri(target, copied), false), chats.Save);
                var loaded = new ChatStore(paths).Load(target.Id);
                AssertEqual(copied.Id, loaded.ActiveHtmlArtifactId, "copy survives restart");
                AssertTrue(copied.Id != first.Id, "independent logical identity");
                AssertTrue(loaded.HtmlWorkspace.DataSources.All(d => d.Binding.Policy == "exact" && DocumentArtifactStore.Owns(target, d.Binding.Resource)), "no original authority binding survives");
                var before = chats.DocumentArtifacts.List(target).Count;
                var rejected = false;
                try { catalog.Import(target, export.Bytes, export.FileName, export.ContentType, operation); } catch (InvalidOperationException) { rejected = true; }
                AssertTrue(rejected && chats.DocumentArtifacts.List(target).Count == before, "repeated transfer does not create a duplicate");
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                AssertTrue(executor.ExecuteManual(Command("common.html_workspace_write_file", "path", "continued.css", "content", "h1 { color: green; }"),
                    tools, new AppSettings(), false, false, loaded).Success, "imported project can be continued through normal tools");
            });
        }
        private static void ArtifactTransferSavedOfficeData()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) => {
                var paths = FixturePaths.Value; var chats = new ChatStore(paths);
                var source = NewSession(adapter); executor.BindResourceAuthority(source);
                var links = new ArtifactWorkingSetService(chats.DocumentArtifacts, new ResourceMutationJournal(paths));
                var authority = new ResourceAuthorityStore(paths); var blobs = new ChatBlobStore(paths);
                var catalog = new ArtifactCatalogService(new DocumentAuthorityRegistry(paths), chats.DocumentArtifacts, links, authority, blobs);
                var first = catalog.Import(source, TransferFixture().Export(), "source.rna-html.zip", "application/zip", Guid.NewGuid().ToString("N"));
                links.Change(source, LinkRequest(source, ChatResourceUri.CreateArtifactRevisionUri(source, first), false), chats.Save);
                var scope = ResourceAuthorityScopeId.Document(new DocumentAuthorityId(source.DocumentAuthorityId));
                var exact = new ResourceRef(ResourceUri.Create("excel", source.DocumentAuthorityId, "range", "Sheet1", "A1:B2"), "saved-data-1");
                var data = "[{\"month\":\"May\",\"amount\":321}]";
                var payload = PayloadRef.FromBlob(blobs.StoreText(data, "application/json"));
                authority.RegisterRevision(scope, new ResourceRevisionMetadata(exact, payload.Sha256, payload));
                authority.RegisterView(scope, new ResourceRevisionView(exact, "text", payload.Sha256, payload, ResourceCoverage.Whole()));
                var before = authority.Capture(scope);
                authority.Publish(ResourceAuthorityCommit.Create(scope, before.Generation, null,
                    new[] { new ResourceHeadChange(exact.Identity, null, ResourceHeadState.Known(exact, before.Generation + 1, "test")) }, AuthorityCommitReason.InitialObservation));
                executor.MutateLocalResources(source, "common.html_data_bind", new Dictionary<string, object>(), () => {
                    source.HtmlWorkspace.DataSources.Single().Binding = new HtmlWorkspaceDataBinding { Resource = new ResourceRef(exact.Identity.Uri), Policy = "head", View = "records", ViewPath = "$" };
                    return HtmlWorkspaceArtifactService.CaptureCurrent(source, "Saved Office data");
                });
                var current = source.Artifacts.Single(a => a.Id == source.ActiveHtmlArtifactId);
                var request = new ArtifactTransferRequest { ChatId = source.Id, DocumentId = source.DocumentAuthorityId,
                    ResourceUri = ChatResourceUri.CreateArtifactRevisionUri(source, current) };
                var exported = catalog.Export(source, request);
                AssertEqual(data, ArtifactTransferPackage.Import(exported.Bytes).Data.Single().Json, "head binding uses complete retained data without live Office reads");
                File.Delete(blobs.PathFor(payload.Sha256));
                RuntimeThrows<InvalidDataException>(() => catalog.Export(source, request));
            });
        }

        private static void ArtifactTransferRejectsInvalidPackage()
        {
            var fixture = TransferFixture();
            fixture.Files[0].Path = "../index.html";
            var rejected = false;
            try { fixture.Export(); } catch (InvalidDataException) { rejected = true; }
            AssertTrue(rejected, "unsafe path rejected");
            fixture = TransferFixture();
            var bytes = fixture.Export();
            using (var stream = new MemoryStream()) {
                stream.Write(bytes, 0, bytes.Length);
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, true)) zip.GetEntry("data/0.json").Delete();
                rejected = false;
                try { ArtifactTransferPackage.Import(stream.ToArray()); } catch (InvalidDataException) { rejected = true; }
                AssertTrue(rejected, "missing data cannot become a partial project");
            }
            fixture = TransferFixture(); fixture.Version = 2;
            rejected = false;
            try { fixture.Export(); } catch (InvalidDataException) { rejected = true; }
            AssertTrue(rejected, "unsupported version rejected");
        }
        private static void ArtifactTransferFiles()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) => {
                var paths = FixturePaths.Value; var chats = new ChatStore(paths); var session = NewSession(adapter);
                session.DocumentAuthorityId = new DocumentAuthorityRegistry(paths).Resolve("excel", "files", Path.Combine(paths.Root, "files.xlsx")).Id;
                var catalog = new ArtifactCatalogService(new DocumentAuthorityRegistry(paths), chats.DocumentArtifacts,
                    new ArtifactWorkingSetService(chats.DocumentArtifacts, new ResourceMutationJournal(paths)), new ResourceAuthorityStore(paths), new ChatBlobStore(paths));
                foreach (var name in new[] { "notes.md", "data.json", "raw.bin" }) {
                    var bytes = name.EndsWith(".bin") ? new byte[] { 0, 255, 3, 17 } : Encoding.UTF8.GetBytes(name.EndsWith(".md") ? "# Пример\n" : "{\"a\":1}");
                    var item = catalog.Import(session, bytes, name, "application/octet-stream", Guid.NewGuid().ToString("N"));
                    var exported = catalog.Export(session, new ArtifactTransferRequest { ChatId = session.Id, DocumentId = session.DocumentAuthorityId,
                        ResourceUri = ChatResourceUri.CreateArtifactRevisionUri(session, item) });
                    AssertTrue(bytes.SequenceEqual(exported.Bytes), "exact file bytes survive " + name);
                }
            });
        }
    }
}
