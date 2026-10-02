using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Llm;
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
                Console.WriteLine("PASS artifact-transfer-controller: empty chat, upload, import, download and copy");
                return 0;
            }
            catch (Exception ex) { Console.WriteLine("FAIL artifact-transfer-controller: " + ex); return 1; }
            finally { controller?.Dispose(); try { Directory.Delete(root, true); } catch (IOException) { } }
        }
    }
}
