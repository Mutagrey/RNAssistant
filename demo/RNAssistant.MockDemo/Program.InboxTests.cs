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
        private static async Task<ChatStateResponse> SubmitAndWaitAsync(AssistantController controller, string text, string chatId)
        {
            controller.SubmitChatInput(new SubmitChatInputPayload { ChatId = chatId,
                OperationId = Guid.NewGuid().ToString("N"), Text = text, Delivery = InputDelivery.Queue }, null, null);
            await controller.InboxWorkersDrained.ConfigureAwait(false);
            return controller.GetChatState(chatId);
        }

        private static async Task<int> RunUnifiedAgentTestAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "RNAssistant.Unified." + Guid.NewGuid().ToString("N"));
            AssistantController controller = null;
            try
            {
                var paths = AppDataPaths.CreateForRoot(root);
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var responses = new System.Collections.Generic.Queue<string>();
                var calls = 0;
                Func<AppSettings, System.Collections.Generic.IEnumerable<ChatMessage>, CancellationToken, Task<LlmCompletionResult>> model =
                    (settings, messages, token) => { calls++; return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() }); };
                Func<string, object, string> tool = (name, arguments) => Newtonsoft.Json.JsonConvert.SerializeObject(new {
                    message = "", action = "tool", tool_calls = new[] { new { name, arguments } } });
                const string done = "{\"message\":\"Done\",\"action\":\"done\",\"tool_calls\":[]}";
                new SettingsService(paths).Save(new AppSettings { AutoConfirmToolActions = false, ContextWindowOverrideTokens = 131072 });
                controller = new AssistantController(adapter, paths, model);
                var id = controller.Initialize().ActiveChatId;
                responses.Enqueue(done);
                var direct = await SubmitAndWaitAsync(controller, "Direct answer", id);
                if (calls != 1 || direct.RunViewState.Lifecycle != "completed") throw new Exception("direct answer did not finish once");
                responses.Enqueue(tool("common.capabilities_read", new { id = "excel.add_sheet" }));
                responses.Enqueue(tool("excel.add_sheet", new { name = "ApprovedSheet" }));
                await SubmitAndWaitAsync(controller, "Create sheet", id);
                var store = new ChatStore(paths);
                var pending = store.Load(id).LastRun.KernelState.Summary.PendingConfirmation;
                if (pending == null || adapter.HasSheet("ApprovedSheet")) throw new Exception("write bypassed approval");
                var controls = controller.GetSettings().Settings;
                controls.AutoConfirmToolActions = true;
                controller.SaveSettings(new SaveSettingsPayload { ChatId = id, Settings = controls,
                    ExpectedPromptPublication = controller.GetSettings().Prompts.Publication }, CancellationToken.None);
                if (adapter.HasSheet("ApprovedSheet") || store.Load(id).LastRun.ConversationPolicy.AutoConfirmToolActions)
                    throw new Exception("settings toggle changed the accepted pending run");
                controller.Dispose();
                controller = new AssistantController(adapter, paths, model);
                responses.Enqueue(done);
                var approved = await controller.ConfirmAgentToolAsync(pending.PendingId, id);
                if (!adapter.HasSheet("ApprovedSheet") || approved.RunViewState.Lifecycle != "completed")
                    throw new Exception("controller lost accepted policy across approval/restart");
                if (adapter.ExcelSheetRequests.Count(request => request.ToolId == "excel.add_sheet") != 1)
                    throw new Exception("approved mutation dispatched more than once");
                responses.Enqueue(tool("excel.add_sheet", new { name = "AutomaticSheet" }));
                responses.Enqueue(done);
                var automatic = await SubmitAndWaitAsync(controller, "Create sheet automatically", id);
                if (!adapter.HasSheet("AutomaticSheet") || automatic.RunViewState.Lifecycle != "completed" ||
                    !store.Load(id).LastRun.ConversationPolicy.AutoConfirmToolActions)
                    throw new Exception("new controller run did not capture automatic approval");

                controller.SetChatMutationPolicy(id, RNAssistant.Core.Services.MutationPolicy.Deny);
                controller.SetChatMode(id, ChatModes.Plan);
                responses.Enqueue(tool("common.capabilities_read", new { id = "common.plan_doc_save" }));
                responses.Enqueue(tool("common.plan_doc_save", new { title = "Review", markdown = "# Review", status = "draft" }));
                responses.Enqueue(tool("common.capabilities_read", new { id = "common.task_list_set" }));
                responses.Enqueue(tool("common.task_list_set", new { action = "save", goal = "Review", steps = new[] { new { text = "Inspect", status = "pending" } } }));
                responses.Enqueue(done);
                var planned = await SubmitAndWaitAsync(controller, "Save the plan and task list", id);
                var saved = store.Load(id);
                if (planned.ActiveChatMode != ChatModes.Plan || saved.MutationPolicy != RNAssistant.Core.Services.MutationPolicy.Deny ||
                    string.IsNullOrEmpty(saved.ActivePlanDocumentArtifactId) || string.IsNullOrEmpty(saved.ActiveTaskListArtifactId) ||
                    planned.RunViewState.ExecutionHealth != "clean") throw new Exception("Plan/Deny did not preserve both planning artifacts");

                responses.Enqueue(tool("common.capabilities_read", new { id = "common.questions_ask" }));
                responses.Enqueue(tool("common.questions_ask", new { questions = new[] { new {
                    header = "Format", prompt = "Choose format " + new string('x', 9000), selection = "single", allowFreeText = true,
                    options = new[] { new { label = "Markdown", description = "Text" }, new { label = "HTML", description = "Web" } }
                } } }));
                await SubmitAndWaitAsync(controller, "Ask before continuing", id);
                var questionState = store.Load(id);
                if (!questionState.Messages.Any(m => m.ToolName == "common.questions_ask" && m.ResultPayload != null))
                    throw new Exception("large question fixture did not reach CAS");
                controller.Dispose();
                controller = new AssistantController(adapter, paths, model);
                var question = RNAssistant.Core.Services.UserQuestionAnswers.Pending(store.Load(id), new ChatBlobStore(paths));
                var answer = new SubmitChatInputPayload { ChatId = id, OperationId = "typed-answer", Delivery = InputDelivery.Steer,
                    Answers = new UserQuestionAnswerCommand { RunId = question.RunId, QuestionSetId = question.QuestionSet.QuestionSetId,
                        Answers = question.QuestionSet.Questions.Select(q => new UserQuestionAnswer {
                            QuestionId = q.Id, OptionIds = new System.Collections.Generic.List<string> { q.Options[0].Id }, FreeText = "Examples"
                        }).ToList() } };
                var beforeAnswer = calls;
                var expectedSet = answer.Answers.QuestionSetId;
                answer.Answers.QuestionSetId = "stale";
                var invalidAnswer = false;
                try { controller.SubmitChatInput(answer, null, null); }
                catch (ArgumentException) { invalidAnswer = true; }
                if (!invalidAnswer || calls != beforeAnswer) throw new Exception("stale question was accepted");
                answer.Answers.QuestionSetId = expectedSet;
                responses.Enqueue(done);
                var receipt = controller.SubmitChatInput(answer, null, null).AcceptedInput;
                await controller.InboxWorkersDrained.ConfigureAwait(false);
                var repeated = controller.SubmitChatInput(answer, null, null).AcceptedInput;
                if (receipt.Id != repeated.Id || calls != beforeAnswer + 1 || store.Load(id).LastRun.KernelState.Summary.Lifecycle != RNAssistant.Core.Agent.RunLifecycle.Completed ||
                    !receipt.Text.Contains("Markdown") || receipt.Text.Contains(expectedSet))
                    throw new Exception("typed answer receipt or semantic projection was not stable");
                answer.Answers.Answers[0].FreeText = "Changed";
                invalidAnswer = false;
                try { controller.SubmitChatInput(answer, null, null); }
                catch (ArgumentException) { invalidAnswer = true; }
                if (!invalidAnswer || calls != beforeAnswer + 1) throw new Exception("conflicting answer receipt replayed input");
                answer.OperationId = "another-answer";
                invalidAnswer = false;
                try { controller.SubmitChatInput(answer, null, null); }
                catch (ArgumentException) { invalidAnswer = true; }
                if (!invalidAnswer || calls != beforeAnswer + 1) throw new Exception("already answered question was accepted");

                controller.SetChatMode(id, ChatModes.Agent);
                controller.SetChatMutationPolicy(id, RNAssistant.Core.Services.MutationPolicy.Ask);
                controls = controller.GetSettings().Settings;
                controls.AutoConfirmToolActions = false;
                controller.SaveSettings(new SaveSettingsPayload { ChatId = id, Settings = controls,
                    ExpectedPromptPublication = controller.GetSettings().Prompts.Publication }, CancellationToken.None);
                responses.Enqueue(tool("common.capabilities_read", new { id = "excel.add_sheet" }));
                responses.Enqueue(tool("excel.add_sheet", new { name = "CancelledSheet" }));
                await SubmitAndWaitAsync(controller, "Create another sheet", id);
                pending = store.Load(id).LastRun.KernelState.Summary.PendingConfirmation;
                if (pending == null) throw new Exception("second mutation did not wait");
                var before = calls;
                controller.SetChatMutationPolicy(id, RNAssistant.Core.Services.MutationPolicy.Deny);
                var rejected = false;
                try { await controller.ConfirmAgentToolAsync(pending.PendingId, id); }
                catch (InvalidOperationException) { rejected = true; }
                if (!rejected || adapter.HasSheet("CancelledSheet") || calls != before) throw new Exception("stale approval was not cancelled");
                controller.SetChatMutationPolicy(id, RNAssistant.Core.Services.MutationPolicy.Ask);
                responses.Enqueue(tool("common.capabilities_read", new { id = "excel.add_sheet" }));
                responses.Enqueue(tool("excel.add_sheet", new { name = "UserCancelledSheet" }));
                await SubmitAndWaitAsync(controller, "Ask permission for a sheet", id);
                pending = store.Load(id).LastRun.KernelState.Summary.PendingConfirmation;
                if (pending == null) throw new Exception("explicit cancellation fixture did not wait");
                before = calls;
                var cancelled = controller.CancelAgentTool(pending.PendingId, id);
                var cancelledSession = store.Load(id);
                var cancellation = store.ReadEvents(cancelledSession.Host, cancelledSession.DocumentKey, id)
                    .Last(item => item.Type == SessionEventTypes.SessionCommit);
                if (cancelled.RunViewState.Lifecycle != "cancelled" || calls != before || adapter.HasSheet("UserCancelledSheet") ||
                    !cancellation.Data["Operations"].Any(op => (string)op["Type"] == SessionOperationTypes.ToolResultRecorded &&
                        (string)op["Data"]?["Value"]?["ToolCallId"] == pending.Call.Id))
                    throw new Exception("user cancellation lost its explicit result or dispatched a tool");
                Console.WriteLine("PASS unified controller: direct answer, approval/restart, Plan/Deny artifacts, typed CAS questions/restart, idempotent answers, stale/conflicting input rejected, policy change cancels pending");
                return 0;
            }
            catch (Exception error) { Console.WriteLine("FAIL unified controller: " + error); return 1; }
            finally { controller?.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

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
                        return new LlmCompletionResult { Content = "{\"action\":\"finish\",\"reason\":\"The request is answered.\",\"outcome\":\"completed\",\"results\":[],\"message\":\"Done\"}" };
                    });
                var init = controller.Initialize(); var chatId = init.ActiveChatId;
                controller.SubmitChatInput(new SubmitChatInputPayload { ChatId = chatId, OperationId = "initial-marker",
                    Text = "initial-marker", Delivery = InputDelivery.Queue }, null, null);
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
                    SteerRunId = new ChatStore(AppDataPaths.CreateForRoot(root)).Load(chatId).LastRun.RunId,
                    ResourceDraftIds = new System.Collections.Generic.List<string> { attachment.Resource.Id, selection.Resource.Id } }, null, null);
                if (await Task.WhenAny(run, Task.Delay(10000)) != run) throw new Exception("steered run stalled");
                await run;
                var drained = controller.InboxWorkersDrained;
                if (await Task.WhenAny(drained, Task.Delay(10000)) != drained) throw new Exception("queue stalled");
                await drained;
                if (!sawSteer) throw new Exception("materialized model request omitted steer");
                var inbox = controller.GetChatInbox(chatId);
                if (inbox.Items.Count != 0) throw new Exception("queue not drained: " + inbox.PauseReason + "; " +
                    new ChatStore(AppDataPaths.CreateForRoot(root)).Load(chatId).LastRun.KernelState.Summary.AssistantMessage);
                var chat = controller.GetChatState(chatId);
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(chat);
                if (!json.Contains("queued-marker") || !json.Contains("steer-marker")) throw new Exception("messages missing from durable chat");
                Console.WriteLine("PASS inbox controller: transport interruption, exact next request, attachments/selection during run, durable queue drain");
                return await RunCrossControllerInboxTestAsync().ConfigureAwait(false);
            }
            catch (Exception error) { Console.WriteLine("FAIL inbox controller: " + error); return 1; }
            finally
            {
                if (controller != null) { controller.StopInboxWorkers(); await controller.InboxWorkersDrained; controller.Dispose(); }
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        private static async Task<int> RunCrossControllerInboxTestAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "RNAssistant.Inbox.Cross." + Guid.NewGuid().ToString("N"));
            AssistantController owner = null, sender = null;
            var releaseModel = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var calls = 0;
                var sawSteerInOwnerTurn = false;
                var sawQueueInOwnerTurn = false;
                var sawQueueInNextTurn = false;
                var paths = AppDataPaths.CreateForRoot(root);
                owner = new AssistantController(FakeOfficeAdapter.ForHost("Excel"), paths,
                    async (settings, messages, token) =>
                    {
                        var call = Interlocked.Increment(ref calls);
                        if (call == 1)
                        {
                            started.TrySetResult(true);
                            try { await Task.Delay(5000, token).ConfigureAwait(false); }
                            catch (OperationCanceledException)
                            {
                                cancelled.TrySetResult(true);
                                await releaseModel.Task.ConfigureAwait(false);
                                throw;
                            }
                            throw new InvalidOperationException("Other controller's steer did not interrupt the model request.");
                        }
                        var hasSteer = messages.Any(m => (m.Content ?? "").Contains("cross-steer"));
                        var hasQueue = messages.Any(m => (m.Content ?? "").Contains("cross-queue"));
                        if (call == 2) { sawSteerInOwnerTurn = hasSteer; sawQueueInOwnerTurn = hasQueue; }
                        if (call == 3) sawQueueInNextTurn = hasQueue;
                        return new LlmCompletionResult { Content = "{\"action\":\"finish\",\"reason\":\"The request is answered.\",\"outcome\":\"completed\",\"results\":[],\"message\":\"Done\"}" };
                    });
                var chatId = owner.Initialize().ActiveChatId;
                owner.SubmitChatInput(new SubmitChatInputPayload { ChatId = chatId, OperationId = "cross-initial",
                    Text = "cross-initial", Delivery = InputDelivery.Queue }, null, null);
                var ownerRun = owner.InboxWorkersDrained;
                if (await Task.WhenAny(started.Task, Task.Delay(5000)) != started.Task)
                    throw new Exception("Owner's model request did not start.");
                sender = new AssistantController(FakeOfficeAdapter.ForHost("Excel"), paths,
                    (settings, messages, token) => { throw new Exception("Sender must not start a competing run."); });
                sender.Initialize();
                sender.SubmitChatInput(new SubmitChatInputPayload { ChatId = chatId, OperationId = "cross-queued",
                    Text = "cross-queue", Delivery = InputDelivery.Queue }, null, null);
                sender.SubmitChatInput(new SubmitChatInputPayload { ChatId = chatId, OperationId = "cross-steered",
                    Text = "cross-steer", Delivery = InputDelivery.Steer,
                    SteerRunId = new ChatStore(paths).Load(chatId).LastRun.RunId }, null, null);
                if (sender.GetChatInbox(chatId).Items.Count != 2 || owner.GetChatInbox(chatId).Items.Count != 2)
                    throw new Exception("Controllers did not project the same pending inputs.");
                if (await Task.WhenAny(cancelled.Task, Task.Delay(5000)) != cancelled.Task)
                    throw new Exception("Owner did not interrupt its model-request token.");
                releaseModel.TrySetResult(true);
                if (await Task.WhenAny(ownerRun, Task.Delay(5000)) != ownerRun)
                    throw new Exception("Owner did not complete after steer.");
                await ownerRun;
                if (calls != 3 || !sawSteerInOwnerTurn || sawQueueInOwnerTurn || !sawQueueInNextTurn ||
                    owner.GetChatInbox(chatId).Items.Count != 0)
                    throw new Exception("Cross-controller steer/queue was not delivered in separate ordered turns.");
                Console.WriteLine("PASS inbox cross-controller: steer interrupts owner model and FIFO queue drains");
                return 0;
            }
            catch (Exception error) { Console.WriteLine("FAIL inbox cross-controller: " + error); return 1; }
            finally
            {
                releaseModel.TrySetResult(true);
                if (sender != null) { sender.StopInboxWorkers(); await sender.InboxWorkersDrained; sender.Dispose(); }
                if (owner != null) { owner.StopInboxWorkers(); await owner.InboxWorkersDrained; owner.Dispose(); }
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}
