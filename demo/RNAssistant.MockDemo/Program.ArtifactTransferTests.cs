using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Llm;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office;
using RNAssistant.Harness;
using RNAssistant.Office.Contracts;

namespace RNAssistant.MockDemo
{
    internal static partial class Program
    {
        private static int RunBrowserHtmlExportTest()
        {
            var root = Path.Combine(Path.GetTempPath(), "RNAssistant.BrowserExport." + Guid.NewGuid().ToString("N"));
            AssistantController controller = null;
            try {
                controller = new AssistantController(FakeOfficeAdapter.ForHost("Excel"), AppDataPaths.CreateForRoot(root),
                    (settings, messages, token) => Task.FromException<LlmCompletionResult>(new Exception("Export must not call a model.")));
                var chat = controller.Initialize().ActiveChatId;
                var before = controller.ListArtifactCatalog(new ArtifactCatalogRequest { ChatId = chat, Scope = "chat" });
                var package = new ArtifactTransferPackage { Title = "Offline bindings", EntryPath = "index.html" };
                package.Files.Add(new ArtifactTransferFile { Path = "index.html", Kind = "html", Content = "<button>Offline</button>" });
                package.Data.Add(new ArtifactTransferData { Name = "sales", View = "table", Json = "[{\"total\":42}]" });
                var bytes = package.Export();
                var upload = controller.BeginArtifactImport(new ResourceUploadOpenRequest { ChatId = chat, FileName = "project.rna-html.zip", ContentType = "application/zip", ByteLength = bytes.Length }, CancellationToken.None);
                using (var input = new MemoryStream(bytes, false)) using (controller.HandleResourceData("POST", upload.Url + "?offset=0&count=" + bytes.Length, CancellationToken.None, input).Body) { }
                controller.ImportArtifact(new ArtifactTransferRequest { ChatId = chat, ExpectedSessionRevision = before.SessionRevision,
                    UploadLeaseId = upload.LeaseId, OperationId = Guid.NewGuid().ToString("N") }, CancellationToken.None);
                var catalog = controller.ListArtifactCatalog(new ArtifactCatalogRequest { ChatId = chat, Scope = "document" });
                var item = catalog.Items.Single(value => value.Kind == ChatArtifactKinds.HtmlWorkspace); var parts = item.ResourceUri.Split('/');
                var target = controller.ResolveBrowserLocator(new BrowserResolveRequest { OwnerScope = new BrowserOwnerScope { Kind = BrowserOwnerKind.Document, ScopeId = item.DocumentId, ChatId = chat },
                    Locator = BrowserLocalLocator.ForResource(Uri.UnescapeDataString(parts[parts.Length - 3]) + "~" + parts.Last()) }).Target;
                var prepared = controller.PrepareBrowserHtmlExport(new BrowserProjectTransferRequest { Target = target }, CancellationToken.None);
                string body;
                using (var stream = controller.HandleResourceData("GET", prepared.Source.Url + "?offset=0&count=" + prepared.Source.Payload.ByteLength, CancellationToken.None).Body)
                using (var reader = new StreamReader(stream)) body = reader.ReadToEnd();
                var snapshot = JsonConvert.DeserializeObject<HtmlWorkspaceSnapshot>(body);
                if (snapshot.Files.Single().Content != package.Files.Single().Content || prepared.Target.ResourceUri != item.ResourceUri || prepared.Target.EntryPath != "index.html")
                    throw new Exception("Export changed the exact source or entry.");
                var binding = prepared.ResourceExport.Bindings.Single();
                using (var stream = controller.HandleResourceData("GET", binding.Lease.Url + "?offset=0&limit=10", CancellationToken.None).Body)
                using (var reader = new StreamReader(stream)) { var data = JObject.Parse(reader.ReadToEnd()); if ((int)data["rows"][0]["total"] != 42) throw new Exception("Export did not retain binding data."); }
                foreach (var id in new[] { prepared.Source.LeaseId, binding.Lease.LeaseId })
                    controller.CloseResourceData(new ResourceDataCloseRequest { ChatId = prepared.ChatId, WorkspaceId = "browser", LeaseId = id });
                var after = controller.ListArtifactCatalog(new ArtifactCatalogRequest { ChatId = chat, Scope = "document" });
                if (after.SessionRevision != catalog.SessionRevision || after.Items.Single(value => value.Kind == ChatArtifactKinds.HtmlWorkspace).ResourceUri != item.ResourceUri)
                    throw new Exception("Export mutated the document or active project.");
                target.ResourceUri += "-stale"; var rejected = false;
                try { controller.PrepareBrowserHtmlExport(new BrowserProjectTransferRequest { Target = target }, CancellationToken.None); }
                catch (InvalidOperationException) { rejected = true; }
                if (!rejected) throw new Exception("Stale export target was accepted.");
                Console.WriteLine("PASS Browser export controller: exact source/data, unchanged publication, lease close and stale refusal"); return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally { controller?.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        private static int RunArtifactTransferTest()
        {
            var root = Path.Combine(Path.GetTempPath(), "RNAssistant.ArtifactTransfer." + Guid.NewGuid().ToString("N"));
            AssistantController controller = null;
            try
            {
                controller = new AssistantController(FakeOfficeAdapter.ForHost("Excel"), AppDataPaths.CreateForRoot(root),
                    (settings, messages, token) => Task.FromException<LlmCompletionResult>(new Exception("Artifact transfer must not call the model.")));
                var chatId = controller.Initialize().ActiveChatId;
                var before = controller.ListArtifactCatalog(new ArtifactCatalogRequest { ChatId = chatId, Scope = "chat" });
                var bytes = Encoding.UTF8.GetBytes("<!doctype html><h1>Imported project</h1>");
                var upload = controller.BeginArtifactImport(new ResourceUploadOpenRequest { ChatId = chatId,
                    FileName = "index.html", ContentType = "text/html", ByteLength = bytes.Length }, CancellationToken.None);
                using (var input = new MemoryStream(bytes, false))
                using (var ack = controller.HandleResourceData("POST", upload.Url + "?offset=0&count=" + bytes.Length, CancellationToken.None, input).Body) { }
                controller.ImportArtifact(new ArtifactTransferRequest { ChatId = chatId, ExpectedSessionRevision = before.SessionRevision,
                    UploadLeaseId = upload.LeaseId, OperationId = Guid.NewGuid().ToString("N") }, CancellationToken.None);
                var catalog = controller.ListArtifactCatalog(new ArtifactCatalogRequest { ChatId = chatId, Scope = "document" });
                var item = catalog.Items.Single();
                if (!item.Linked || !item.Selected) throw new Exception("import was not attached and selected");
                var download = controller.ExportArtifact(new ArtifactTransferRequest { ChatId = chatId, DocumentId = item.DocumentId,
                    ResourceUri = item.ResourceUri }, CancellationToken.None);
                byte[] exported;
                using (var output = new MemoryStream()) {
                    for (var offset = 0L; offset < download.Data.Payload.ByteLength;) {
                        var count = (int)Math.Min(download.Data.MaxChunkBytes, download.Data.Payload.ByteLength - offset);
                        using (var part = controller.HandleResourceData("GET", download.Data.Url + "?offset=" + offset + "&count=" + count, CancellationToken.None).Body)
                            part.CopyTo(output);
                        offset += count;
                    }
                    exported = output.ToArray();
                }
                if (ArtifactTransferPackage.Import(exported).Files.Single().Content != Encoding.UTF8.GetString(bytes))
                    throw new Exception("downloaded package changed source bytes");
                controller.CloseArtifactTransfer(new ResourceUploadLeaseRequest { ChatId = chatId, LeaseId = download.Data.LeaseId });
                controller.CopyArtifact(new ArtifactTransferRequest { ChatId = chatId, DocumentId = item.DocumentId, ResourceUri = item.ResourceUri,
                    ExpectedSessionRevision = catalog.SessionRevision, OperationId = Guid.NewGuid().ToString("N") }, CancellationToken.None);
                var copied = controller.ListArtifactCatalog(new ArtifactCatalogRequest { ChatId = chatId, Scope = "chat" });
                if (copied.Items.Count != 2 || copied.Items.Select(i => i.ResourceUri).Distinct().Count() != 2)
                    throw new Exception("copy did not create an independent project");
                var removal = new ArtifactDeleteRequest { ChatId = chatId, ResourceUri = item.ResourceUri,
                    ExpectedSessionRevision = copied.SessionRevision, Confirmed = true };
                controller.DeleteDocumentArtifact(removal);
                var afterRemoval = controller.ListArtifactCatalog(new ArtifactCatalogRequest { ChatId = chatId, Scope = "document" });
                if (afterRemoval.Items.Count != 1 || afterRemoval.Items[0].ResourceUri == item.ResourceUri)
                    throw new Exception("removal did not preserve the independent copy");
                var rejected = false;
                try { controller.DeleteDocumentArtifact(removal); } catch (InvalidOperationException) { rejected = true; }
                if (!rejected) throw new Exception("stale repeated deletion was accepted");
                controller.Dispose();
                controller = new AssistantController(FakeOfficeAdapter.ForHost("Excel"), AppDataPaths.CreateForRoot(root),
                    (settings, messages, token) => Task.FromException<LlmCompletionResult>(new Exception("Removal must not call the model.")));
                var restarted = controller.ListArtifactCatalog(new ArtifactCatalogRequest { ChatId = chatId, Scope = "document" });
                if (restarted.Items.Count != 1 || restarted.Items[0].ResourceUri != afterRemoval.Items[0].ResourceUri)
                    throw new Exception("removed project returned after controller restart");
                Console.WriteLine("PASS artifact-transfer-controller: upload, import, download, independent copy, guarded removal and restart");
                return 0;
            }
            catch (Exception ex) { Console.WriteLine("FAIL artifact-transfer-controller: " + ex); return 1; }
            finally { controller?.Dispose(); try { Directory.Delete(root, true); } catch (IOException) { } }
        }
    }
}
