using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office;
using RNAssistant.Harness;

namespace RNAssistant.MockDemo
{
    internal static partial class Program
    {
        private static int RunChatCreationTest()
        {
            var root = Path.Combine(Path.GetTempPath(), "RNAssistant.ChatCreation." + Guid.NewGuid().ToString("N"));
            AssistantController controller = null;
            try
            {
                var paths = AppDataPaths.CreateForRoot(root);
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var store = new ChatStore(paths);
                var old = store.Create(adapter.HostName, adapter.DocumentKey, adapter.DocumentTitle, "Cold history");
                for (var i = 0; i < 300; i++) old.Messages.Add(new ChatMessage { Role = "user", Content = "message " + i });
                store.Save(old);
                var oldFile = Directory.GetFiles(paths.ChatDirectory, "*.events.jsonl", SearchOption.AllDirectories).Single();
                var bytes = File.ReadAllBytes(oldFile);
                File.Delete(oldFile + ".projection.sqlite");
                controller = new AssistantController(adapter, paths,
                    (settings, messages, token) => Task.FromException<LlmCompletionResult>(new Exception("Creation must not call a model.")));
                var draft = controller.CreateChat("First draft");
                var second = controller.CreateChat("Replacement draft");
                var persistent = controller.CreatePersistentChat("Persisted chat");
                var archived = controller.CreateDocumentChat("Archived draft", "Word", "archive", "Archive.docx", "C:\\Archive.docx");
                foreach (var response in new[] { draft, second, persistent, archived })
                {
                    if (response.Chats != null || response.Documents != null || response.DetailDeferred || response.Messages.Count != 0 ||
                        response.CatalogChanges?.UpsertedChats.Count != 1 || response.CatalogChanges.UpsertedChats[0].Id != response.ActiveChatId)
                        throw new Exception("Creation must return its empty detail and one exact catalog change.");
                }
                if (draft.CatalogChanges.RemovedTransientChatIds.Count != 0 ||
                    second.CatalogChanges.RemovedTransientChatIds.Single() != draft.ActiveChatId ||
                    persistent.CatalogChanges.RemovedTransientChatIds.Single() != second.ActiveChatId ||
                    archived.CatalogChanges.RemovedTransientChatIds.Count != 0)
                    throw new Exception("Only the replaced transient draft may leave the catalog.");
                if (File.Exists(oldFile + ".projection.sqlite") || !bytes.SequenceEqual(File.ReadAllBytes(oldFile)))
                    throw new Exception("Creating a fresh chat must not index or change cold history.");
                if (store.Load(adapter.HostName, adapter.DocumentKey, persistent.ActiveChatId) == null)
                    throw new Exception("Host-row creation must remain durable.");
                Console.WriteLine("PASS chat-creation-controller: four creation paths, exact deltas, zero indexing of cold history");
                return 0;
            }
            catch (Exception ex) { Console.WriteLine("FAIL chat-creation-controller: " + ex); return 1; }
            finally { controller?.Dispose(); try { Directory.Delete(root, true); } catch (IOException) { } }
        }
    }
}
