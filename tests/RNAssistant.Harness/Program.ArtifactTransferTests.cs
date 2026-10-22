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
using UglyToad.PdfPig.Writer;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Core;

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
                fixture.Title = new string('Ж', 200);
                fixture.Files[1].Content += "\n/*" + new string('x', 2100001) + "*/";
                var first = catalog.Import(source, fixture.Export(), fixture.Title + ".rna-html.zip", "application/zip", Guid.NewGuid().ToString("N"));
                links.Change(source, LinkRequest(source, ChatResourceUri.CreateArtifactRevisionUri(source, first), false), chats.Save);
                AssertEqual(2, source.HtmlWorkspace.Files.Count, "imported HTML restores files");
                AssertEqual("index.html", source.HtmlWorkspace.ActiveFileId, "entry retained");
                var listing = catalog.List(target, new ArtifactCatalogRequest { ChatId = target.Id, Scope = "all", Kind = "html_workspace" });
                AssertEqual(1, listing.Items.Count, "closed source document is visible");
                AssertTrue(!listing.Items[0].Linked && target.ActiveHtmlArtifactId == null, "listing does not select or attach");
                var export = catalog.Export(target, new ArtifactTransferRequest { ChatId = target.Id, DocumentId = source.DocumentAuthorityId,
                    ResourceUri = listing.Items[0].ResourceUri });
                var decoded = ArtifactTransferPackage.Import(export.Bytes);
                AssertTrue(export.FileName.Length <= 180 && export.FileName.EndsWith(".rna-html.zip"), "bounded export preserves the package extension");
                AssertEqual(fixture.Title, decoded.Title, "filename bound does not truncate the project title");
                AssertEqual(fixture.Files[1].Content, decoded.Files[1].Content, "sources survive export");
                AssertEqual(fixture.Data[0].Json, decoded.Data[0].Json, "data preserved byte for byte");
                AssertEqual(fixture.Data[0].SchemaJson, decoded.Data[0].SchemaJson, "schema dependency preserved");
                var operation = Guid.NewGuid().ToString("N");
                var copied = catalog.Import(target, export.Bytes, export.FileName, export.ContentType, operation);
                AssertEqual(ChatArtifactKinds.HtmlWorkspace, copied.Kind, "a long project name still copies as an editable workspace");
                links.Change(target, LinkRequest(target, ChatResourceUri.CreateArtifactRevisionUri(target, copied), false), chats.Save);
                var loaded = new ChatStore(paths).Load(target.Id);
                AssertEqual(copied.Id, loaded.ActiveHtmlArtifactId, "copy survives restart");
                AssertTrue(copied.Id != first.Id, "independent logical identity");
                AssertTrue(loaded.HtmlWorkspace.DataSources.All(d => d.Binding.Policy == "exact" && DocumentArtifactStore.Owns(target, d.Binding.Resource)), "no original authority binding survives");
                var savedBinding = loaded.HtmlWorkspace.DataSources.Single().Binding;
                AssertEqual("records", savedBinding.View, "old transfer retains its saved view after restart");
                using (var plane = new ResourceDataPlaneService(executor.ResourceGateway))
                {
                    var opened = plane.Open(loaded, "workspace", savedBinding.Resource, savedBinding.View, savedBinding.ViewPath);
                    var batch = Newtonsoft.Json.Linq.JObject.Parse(Encoding.UTF8.GetString(
                        plane.Read(opened.LeaseId, 0, 500, System.Threading.CancellationToken.None)));
                    AssertEqual(12, (int)batch["rows"][0]["amount"], "saved records binding still reads transferred data");
                    plane.Close(loaded.Id, "workspace", opened.LeaseId);
                }
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
                foreach (var name in new[] { new string('m', 200) + ".md", new string('j', 200) + ".json", "raw.bin", "empty.bin" }) {
                    var binary = name.EndsWith(".bin");
                    var bytes = name == "empty.bin" ? new byte[0] : binary ? new byte[] { 0, 255, 3, 17 } : Encoding.UTF8.GetBytes(name.EndsWith(".md") ? "# Пример\n" : "{\"a\":1}");
                    var item = catalog.Import(session, bytes, name, "application/octet-stream", Guid.NewGuid().ToString("N"));
                    var exported = catalog.Export(session, new ArtifactTransferRequest { ChatId = session.Id, DocumentId = session.DocumentAuthorityId,
                        ResourceUri = ChatResourceUri.CreateArtifactRevisionUri(session, item) });
                    AssertTrue(bytes.SequenceEqual(exported.Bytes), "exact file bytes survive " + name);
                    AssertTrue(exported.FileName.Length <= 180 && exported.FileName.EndsWith(Path.GetExtension(name)), "bounded names preserve file extensions");
                    var found = executor.ResourceGateway.Find(session, item.Title, "document").Items.Single();
                    AssertEqual(!binary, found.Representations.Contains("text"), "discovery advertises retained text only for authored files");
                    if (!binary)
                        AssertEqual(Encoding.UTF8.GetString(bytes), executor.ResourceGateway.Read(session, new ResourceReadRequest {
                            Reference = ChatResourceUri.CreateArtifactRevision(session, item), Representation = "text", MaxChars = 1000 }).Result.Text,
                            "advertised text reads exact authored bytes");
                }
            });
        }

        private static void ArtifactTransferOriginalRepresentations()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) => {
                var paths = FixturePaths.Value; var chats = new ChatStore(paths);
                var registry = new DocumentAuthorityRegistry(paths);
                var source = NewSession(adapter); executor.BindResourceAuthority(source);
                var target = NewSession(adapter);
                target.DocumentAuthorityId = registry.Resolve("excel", "copies", Path.Combine(paths.Root, "copies.xlsx")).Id;
                var links = new ArtifactWorkingSetService(chats.DocumentArtifacts, new ResourceMutationJournal(paths));
                var catalog = new ArtifactCatalogService(registry, chats.DocumentArtifacts, links, new ResourceAuthorityStore(paths), new ChatBlobStore(paths));
                var ingestion = new ChatResourceIngestionService(new AttachmentStore(paths), chats.DocumentArtifacts);
                var pdf = new PdfDocumentBuilder();
                pdf.AddPage(300, 300).AddText("Retained PDF attachment text", 12, new PdfPoint(20, 250), pdf.AddStandard14Font(Standard14Font.Helvetica));
                pdf.AddPage(300, 300);
                var mp3 = new byte[427]; mp3[0] = 0x49; mp3[1] = 0x44; mp3[2] = 0x33; mp3[3] = 4;
                mp3[10] = 0xff; mp3[11] = 0xfb; mp3[12] = 0x90;
                var cases = new Dictionary<string, byte[]> {
                    ["report.pdf"] = pdf.Build(),
                    ["recording.mp3"] = mp3,
                    ["unicode.txt"] = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Сохранённый текст")).ToArray(),
                    ["bounded.txt"] = Encoding.UTF8.GetBytes(new string('x', 1000050)),
                    ["report.docx"] = OfficeZip(new Dictionary<string, string> { ["word/document.xml"] =
                        "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>Retained Word text</w:t></w:r></w:p></w:body></w:document>" })
                };
                foreach (var fixture in cases)
                {
                    var draft = ingestion.Stage(source, fixture.Key, fixture.Key.EndsWith(".txt") ? "text/plain" : "application/octet-stream", fixture.Value);
                    var message = new ChatMessage { Role = "user", Content = "Original", Attachments = ingestion.LoadDrafts(source, new[] { draft.Id }).ToList() };
                    source.Messages.Add(message); ingestion.CommitAndLink(source, message, source.Messages.Count - 1);
                    var original = chats.DocumentArtifacts.Read(source, message.ResourceRefs.Single());
                    var exported = catalog.Export(source, new ArtifactTransferRequest { ChatId = source.Id, DocumentId = source.DocumentAuthorityId, ResourceUri = message.ResourceRefs.Single().Uri });
                    var copied = catalog.Import(target, exported.Bytes, exported.FileName, exported.ContentType, Guid.NewGuid().ToString("N"), exported.Origin);
                    links.Change(target, LinkRequest(target, ChatResourceUri.CreateArtifactRevisionUri(target, copied), false), chats.Save);
                    target = new ChatStore(paths).Load(target.Id);
                    AssertTrue(target.Artifacts.All(item => item.InlineText == null), "opening the chat does not hydrate original bytes as UTF-8");
                    var exact = ChatResourceUri.CreateArtifactRevision(target, copied);
                    var retained = new ChatStore(paths).DocumentArtifacts.Read(target, exact);
                    AssertTrue(copied.Id != original.Id, "copy has independent identity: " + fixture.Key);
                    AssertTrue(fixture.Value.SequenceEqual(chats.DocumentArtifacts.ExportBytes(target, exact)), "copy preserves original bytes: " + fixture.Key);
                    var expected = original.OriginalAttachment; var actual = retained.OriginalAttachment;
                    AssertEqual(expected.Kind, actual.Kind, "attachment kind survives copy and restart: " + fixture.Key);
                    AssertEqual(expected.ExtractedTextSha256, actual.ExtractedTextSha256, "extracted text survives copy: " + fixture.Key);
                    AssertEqual(expected.PageCount, actual.PageCount, "PDF page count survives copy");
                    AssertEqual(expected.TextTruncated, actual.TextTruncated, "bounded extraction retains truncation evidence");
                    var found = executor.ResourceGateway.Find(target, fixture.Key, "document").Items.Single();
                    if (actual.ExtractedTextSha256 != null)
                    {
                        AssertTrue(found.Representations.Contains("text"), "copy advertises extracted text");
                        var read = executor.ResourceGateway.Read(target, new ResourceReadRequest { Reference = exact, Representation = "text", MaxChars = 1000 });
                        AssertTrue(read.Result.Text.Length > 0, "extracted copy is readable after restart");
                    }
                    if (actual.Kind == "pdf" || actual.Kind == "audio")
                        AssertTrue(found.Representations.Contains("media"), "copy advertises supported media");
                    if (actual.Kind == "pdf")
                        AssertTrue(expected.PageTextLengths.SequenceEqual(actual.PageTextLengths) && !string.IsNullOrEmpty(actual.ExtractionWarning), "PDF page and scan evidence survives copy");
                    if (actual.TextTruncated) AssertTrue(!string.IsNullOrEmpty(actual.ExtractionWarning), "partial text has an explicit warning");
                }
            });
        }
    }
}
