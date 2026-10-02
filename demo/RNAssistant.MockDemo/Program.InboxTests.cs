using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office;
using RNAssistant.Harness;
using RNAssistant.Office.Contracts;

namespace RNAssistant.MockDemo
{
    internal static partial class Program
    {
        private static async Task<int> RunInboxTestAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "RNAssistant.Inbox." + Guid.NewGuid().ToString("N"));
            AssistantController controller = null;
            try
            {
                var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var calls = 0; var sawSteer = false;
                controller = new AssistantController(FakeOfficeAdapter.ForHost("Excel"), AppDataPaths.CreateForRoot(root),
                    async (settings, messages, token) =>
                    {
                        if (Interlocked.Increment(ref calls) == 1)
                        {
                            started.TrySetResult(true);
                            await Task.Delay(10000, token).ConfigureAwait(false);
                            throw new InvalidOperationException("steer did not interrupt transport");
                        }
                        if (messages.Any(m => (m.Content ?? "").Contains("steer-marker"))) sawSteer = true;
                        return new LlmCompletionResult { Content = "{\"message\":\"Done\",\"final\":true,\"tool_calls\":[]}" };
                    });
                var init = controller.Initialize(); var chatId = init.ActiveChatId;
                controller.SubmitChatInput(new SubmitChatInputPayload { ChatId = chatId, OperationId = "initial-marker",
                    Text = "initial-marker", Delivery = InputDelivery.Steer }, null, null);
                var run = controller.InboxWorkersDrained;
                if (await Task.WhenAny(started.Task, Task.Delay(10000)) != started.Task)
                    throw new Exception("initial run never reached model");
                var bytes = System.Text.Encoding.UTF8.GetBytes("inbox attachment evidence");
                var upload = controller.BeginChatResourceUpload(new ResourceUploadOpenRequest { ChatId = chatId,
                    FileName = "inbox.txt", ContentType = "text/plain", ByteLength = bytes.Length });
                using (var body = new MemoryStream(bytes, false))
                using (var response = controller.HandleResourceData("POST", upload.Url + "?offset=0&count=" + bytes.Length,
                    CancellationToken.None, body).Body) { }
                var attachment = await controller.CompleteChatResourceUploadAsync(new ResourceUploadLeaseRequest { ChatId = chatId, LeaseId = upload.LeaseId });
                var selection = controller.StageSelectionInput("full", chatId);
                controller.SubmitChatInput(new SubmitChatInputPayload { ChatId = chatId, OperationId = "queued-marker",
                    Text = "queued-marker", Delivery = InputDelivery.Queue }, null, null);
                controller.SubmitChatInput(new SubmitChatInputPayload { ChatId = chatId, OperationId = "steer-marker",
                    Text = "steer-marker", Delivery = InputDelivery.Steer,
                    ResourceDraftIds = new System.Collections.Generic.List<string> { attachment.Resource.Id, selection.Resource.Id } }, null, null);
                if (await Task.WhenAny(run, Task.Delay(10000)) != run) throw new Exception("steered run stalled");
                await run;
                var drained = controller.InboxWorkersDrained;
                if (await Task.WhenAny(drained, Task.Delay(10000)) != drained) throw new Exception("queue stalled");
                await drained;
                if (!sawSteer) throw new Exception("materialized model request omitted steer");
                var inbox = controller.GetChatInbox(chatId);
                if (inbox.Items.Count != 0) throw new Exception("queue not drained: " + inbox.PauseReason);
                var chat = controller.GetChatState(chatId);
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(chat);
                if (!json.Contains("queued-marker") || !json.Contains("steer-marker")) throw new Exception("messages missing from durable chat");
                Console.WriteLine("PASS inbox controller: transport interruption, exact next request, attachments/selection during run, durable queue drain");
                return 0;
            }
            catch (Exception error) { Console.WriteLine("FAIL inbox controller: " + error); return 1; }
            finally
            {
                if (controller != null) { controller.StopInboxWorkers(); await controller.InboxWorkersDrained; controller.Dispose(); }
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}
