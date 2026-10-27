using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Harness;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;

namespace RNAssistant.MockDemo
{
    internal static partial class Program
    {
        private static async Task<int> RunConfirmationInputTestsAsync()
        {
            try
            {
                foreach (var role in new[] { ToolResultRoles.User, ToolResultRoles.Developer, ToolResultRoles.Tool })
                {
                    foreach (var routing in new[] { "helper", "changed-helper", "native" })
                        await CheckConfirmationInputAsync(role, routing, false).ConfigureAwait(false);
                }
                await CheckConfirmationInputAsync(ToolResultRoles.User, "helper", true).ConfigureAwait(false);
                Console.WriteLine("PASS confirmation input: 9 role/routing/restart cases and image steer; durable analysis and exact effects");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        private static async Task CheckConfirmationInputAsync(string role, string routing, bool steer)
        {
            var root = Path.Combine(Path.GetTempPath(), "rna-confirmation-input-" + Guid.NewGuid().ToString("N"));
            var paths = AppDataPaths.CreateForRoot(root);
            var adapter = FakeOfficeAdapter.ForHost("Excel");
            var store = new ChatStore(paths);
            var responses = new Queue<string>();
            var helperCalls = 0;
            var primaryCalls = 0;
            var expectedAnalysis = string.Empty;
            string chatId = null;
            var settings = new AppSettings { Model = "text", ToolResultRole = role, AutoConfirmToolActions = false,
                ContextWindowOverrideTokens = 131072 };
            settings.ModelCapabilities["text"] = new ModelCapabilitySettings { SupportsImages = routing == "native" };
            settings.ModelCapabilities["vision1"] = new ModelCapabilitySettings { SupportsImages = true };
            settings.ModelCapabilities["vision2"] = new ModelCapabilitySettings { SupportsImages = true };
            settings.AttachmentModelPriority.Add("vision1");
            new SettingsService(paths).Save(settings);
            Func<AppSettings, IEnumerable<ChatMessage>, CancellationToken, Task<LlmCompletionResult>> model = (current, messages, token) =>
            {
                if (current.Model.StartsWith("vision", StringComparison.Ordinal))
                {
                    expectedAnalysis = current.Model + " image analysis " + (++helperCalls);
                    return Task.FromResult(new LlmCompletionResult { Content = expectedAnalysis });
                }
                primaryCalls++;
                var request = messages.ToList();
                var persisted = ConversationInputMessage.Latest(store.Load(chatId));
                if (routing == "native")
                {
                    if (!request.Any(message => message.Attachments != null && message.Attachments.Any(attachment => attachment.Kind == "image")))
                        throw new Exception(role + ": native image disappeared at request " + primaryCalls);
                }
                else if (!request.Any(message => message.AttachmentAnalysis?.Content?.Contains(expectedAnalysis) == true) ||
                    persisted?.AttachmentAnalysis?.Content?.Contains(expectedAnalysis) != true)
                    throw new Exception(role + "/" + routing + ": analysis missing from request or durable input at request " + primaryCalls);
                var response = responses.Dequeue();
                if (response == "fixture-finish")
                {
                    var refs = RNAssistant.Core.ModelProtocol.ModelReferenceProjection.Delivered(request.ToArray());
                    // The superseded pending write is not evidence of a completed mutation.
                    response = RNAssistant.Core.ModelProtocol.ModelProtocolWire.WriteAcceptedDecision("finish", "Report the observed outcome.",
                        steer ? "The pending write was superseded." : "Done",
                        results: refs.Count == 0 ? new RNAssistant.Core.ModelProtocol.ConversationResultClaim[0] :
                            new[] { new RNAssistant.Core.ModelProtocol.ConversationResultClaim("Requested change", new[] { steer ? refs.First() : refs.Last() },
                                steer ? RNAssistant.Core.ModelProtocol.ConversationAssessment.Unverified :
                                    RNAssistant.Core.ModelProtocol.ConversationAssessment.Satisfied) },
                        outcome: steer ? RNAssistant.Core.ModelProtocol.ConversationOutcome.UnableToComplete :
                            RNAssistant.Core.ModelProtocol.ConversationOutcome.Completed);
                }
                return Task.FromResult(new LlmCompletionResult { Content = response });
            };
            AssistantController controller = null;
            try
            {
                controller = new AssistantController(adapter, paths, model);
                chatId = controller.Initialize().ActiveChatId;
                var attachments = new AttachmentStore(paths);
                Func<string, string> stage = name =>
                {
                    var draft = attachments.Import(name, "image/png", Convert.FromBase64String(
                        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j20kAAAAASUVORK5CYII="), chatId);
                    attachments.SaveDraftMetadata(draft);
                    return draft.Id;
                };
                Func<string, object, string> tool = (name, arguments) => JsonConvert.SerializeObject(new
                    { message = "", action = "execute", reason = "Perform the checked step.", tool_calls = new[] { new { name, arguments } } });
                const string done = "fixture-finish";
                responses.Enqueue(tool("common.capabilities_read", new { id = "excel.add_sheet" }));
                responses.Enqueue(tool("excel.add_sheet", new { name = "ImageSheet1" }));
                controller.SubmitChatInput(new SubmitChatInputPayload { ChatId = chatId, OperationId = "image-a",
                    Text = "Use this image to create a sheet", ResourceDraftIds = new List<string> { stage("first.png") },
                    Delivery = InputDelivery.Queue }, null, null);
                await controller.InboxWorkersDrained.ConfigureAwait(false);
                var initial = store.Load(chatId);
                var pending = initial.LastRun.KernelState?.Summary.PendingConfirmation;
                if (pending == null || primaryCalls != 2) throw new Exception("Fixture did not reach approval: " +
                    role + "/" + routing + ", requests=" + primaryCalls + ", " + initial.LastRun.Status + ": " + initial.LastRun.CurrentAction);
                if (controller.GetChatState(chatId).RunViewState?.RunId != initial.LastRun.RunId)
                    throw new Exception("Pending UI state lost the original run ID.");
                var firstUserId = ConversationInputMessage.Latest(initial).Id;

                if (steer)
                {
                    responses.Enqueue(done);
                    controller.SubmitChatInput(new SubmitChatInputPayload { ChatId = chatId, OperationId = "image-b",
                        Text = "Use the replacement image; do not create a sheet", ResourceDraftIds = new List<string> { stage("second.png") },
                        Delivery = InputDelivery.Steer, SteerRunId = initial.LastRun.RunId,
                        SteerPendingId = pending.PendingId }, null, null);
                    await controller.InboxWorkersDrained.ConfigureAwait(false);
                    var latest = ConversationInputMessage.Latest(store.Load(chatId));
                    if (helperCalls != 2 || latest.Id == firstUserId || latest.AttachmentAnalysis?.Content?.Contains(expectedAnalysis) != true ||
                        adapter.ExcelSheetRequests.Any(request => request.ToolId == "excel.add_sheet"))
                        throw new Exception("Image steer reused old input or dispatched superseded mutation.");
                }
                else
                {
                    if (role == ToolResultRoles.User && routing == "helper")
                    {
                        var staleRunRefused = false;
                        try { await controller.ConfirmAgentToolAsync(pending.PendingId, chatId, runId: "another-run"); }
                        catch (InvalidOperationException) { staleRunRefused = true; }
                        if (!staleRunRefused || adapter.HasSheet("ImageSheet1"))
                            throw new Exception("Stale confirmation run ID was accepted.");
                    }
                    if (routing == "changed-helper")
                    {
                        var controls = controller.GetSettings();
                        controls.Settings.AttachmentModelPriority = new List<string> { "vision2" };
                        controller.SaveSettings(new SaveSettingsPayload { ChatId = chatId, Settings = controls.Settings,
                            ExpectedPromptPublication = controls.Prompts.Publication }, CancellationToken.None);
                    }
                    responses.Enqueue(tool("excel.add_sheet", new { name = "ImageSheet2" }));
                    await controller.ConfirmAgentToolAsync(pending.PendingId, chatId).ConfigureAwait(false);
                    var saved = store.Load(chatId);
                    var call = saved.Messages.Single(message => message.AcceptedCallOrigin != null && message.ToolCallId == pending.Call.Id);
                    if (call.RunId != saved.LastRun.RunId || call.ExcludeFromModelContext)
                        throw new Exception("Confirmation replay update was not persisted.");
                    pending = saved.LastRun.KernelState.Summary.PendingConfirmation;
                    if (pending == null) throw new Exception("Second approval was not reached.");
                    controller.Dispose();
                    controller = new AssistantController(adapter, paths, model);
                    responses.Enqueue(done);
                    await controller.ConfirmAgentToolAsync(pending.PendingId, chatId).ConfigureAwait(false);
                    var expectedHelpers = routing == "native" ? 0 : routing == "changed-helper" ? 2 : 1;
                    if (helperCalls != expectedHelpers || primaryCalls != 4 || !adapter.HasSheet("ImageSheet1") || !adapter.HasSheet("ImageSheet2") ||
                        adapter.ExcelSheetRequests.Count(request => request.ToolId == "excel.add_sheet") != 2)
                        throw new Exception(role + "/" + routing + ": repeated helper or mutation, or lost continuation: helper=" +
                            helperCalls + ", primary=" + primaryCalls + ", sheets=" + adapter.HasSheet("ImageSheet1") + "/" +
                            adapter.HasSheet("ImageSheet2") + ", writes=" + adapter.ExcelSheetRequests.Count(request => request.ToolId == "excel.add_sheet") +
                            ", status=" + store.Load(chatId).LastRun.Status + ", action=" + store.Load(chatId).LastRun.CurrentAction);
                }
                var finalRun = store.Load(chatId).LastRun;
                if (finalRun.KernelState.Summary.Lifecycle != RNAssistant.Core.Agent.RunLifecycle.Completed)
                    throw new Exception("Confirmation input scenario did not complete: " + role + "/" + routing +
                        ", steer=" + steer + ", lifecycle=" + finalRun.KernelState.Summary.Lifecycle +
                        ", reason=" + finalRun.KernelState.Summary.Reason + ", action=" + finalRun.CurrentAction);
            }
            finally
            {
                if (controller != null) { controller.StopInboxWorkers(); await controller.InboxWorkersDrained.ConfigureAwait(false); controller.Dispose(); }
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}
