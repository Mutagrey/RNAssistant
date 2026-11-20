using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Storage;
using RNAssistant.Harness;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;

namespace RNAssistant.MockDemo
{
    internal static partial class Program
    {
        private static async Task<int> RunSmartChatTitleTestAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "rna-office-title-" + Guid.NewGuid().ToString("N"));
            try
            {
                const string prompt = "Создай лист для квартального отчёта";
                const string answer = "Лист для квартального отчёта создан.";
                const string title = "Квартальный отчёт";
                var paths = AppDataPaths.CreateForRoot(root);
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                new SettingsService(paths).Save(new AppSettings { AutoConfirmToolActions = false,
                    SmartChatTitles = true, AutoCompressContext = false, ContextWindowOverrideTokens = 131072 });
                IConversationStore store;
                ChatCreationSessions(paths, adapter, out store);
                var calls = 0;
                var titleCalls = 0;
                string chatId = null;
                var titleRequest = new TaskCompletionSource<RunLifecycle?>(TaskCreationOptions.RunContinuationsAsynchronously);
                var published = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Func<AppSettings, IEnumerable<ChatMessage>, CancellationToken, Task<LlmCompletionResult>> model =
                    (_, messages, token) =>
                    {
                        var request = messages.ToArray();
                        if (request.Any(message => message.Content?.Contains("# Chat title") == true))
                        {
                            Interlocked.Increment(ref titleCalls);
                            var lifecycle = store.Load(chatId).LastRun.KernelState?.Summary?.Lifecycle;
                            if (request.Any(message => message.Content?.Contains(answer.TrimEnd('.')) == true))
                                titleRequest.TrySetResult(lifecycle);
                            return Task.FromResult(new LlmCompletionResult { Content = title });
                        }

                        var number = Interlocked.Increment(ref calls);
                        var response = number <= 2
                            ? ModelProtocolWire.WriteAcceptedCall("Prepare the requested sheet.", null, new ConversationToolCall {
                                Name = number == 1 ? "core.tools.read" : "excel.sheet.add",
                                Arguments = number == 1 ? new JObject { ["id"] = "excel.sheet.add" } : new JObject { ["name"] = "Quarter" }
                            })
                            : ModelProtocolWire.WriteAcceptedDecision("finish", "Report the created sheet.", answer,
                                outcome: ConversationOutcome.Completed, results: new[] { new ConversationResultClaim(
                                    "Created the requested sheet.", ModelReferenceProjection.Delivered(request), ConversationAssessment.Satisfied) });
                        return Task.FromResult(new LlmCompletionResult { Content = response });
                    };

                string pendingId;
                using (var controller = new AssistantController(adapter, paths, model))
                {
                    chatId = controller.Initialize().ActiveChatId;
                    await SubmitAndWaitAsync(controller, prompt, chatId).ConfigureAwait(false);
                    var paused = store.Load(chatId);
                    pendingId = paused.LastRun.KernelState?.Summary?.PendingConfirmation?.PendingId;
                    if (pendingId == null || paused.LastRun.ProvisionalTitle != "Лист для квартального отчёта")
                        throw new InvalidOperationException("The first Office turn did not retain its draft name while awaiting approval.");
                }

                using (var controller = new AssistantController(adapter, paths, model))
                {
                    await controller.ConfirmAgentToolAsync(pendingId, chatId, chatStateChanged: state =>
                    {
                        var updated = state.CatalogChanges?.UpsertedChats?.SingleOrDefault();
                        if (updated?.Id == chatId && updated.Title == title && updated.Revision == store.Load(chatId).Revision)
                            published.TrySetResult(true);
                    }).ConfigureAwait(false);
                    if (await Task.WhenAny(published.Task, Task.Delay(5000)).ConfigureAwait(false) != published.Task ||
                        !titleRequest.Task.IsCompleted || await titleRequest.Task.ConfigureAwait(false) != RunLifecycle.Completed ||
                        titleCalls != 1 || store.Load(chatId).Title != title)
                    {
                        var current = store.Load(chatId);
                        throw new InvalidOperationException("Office smart title did not publish. Run: " +
                            current.LastRun.Status + " / " + current.LastRun.CurrentAction + ", model calls: " + calls +
                            ", title calls: " + titleCalls + ", final answer used: " + titleRequest.Task.IsCompleted +
                            ", notice received: " + published.Task.IsCompleted + ", title: " + current.Title);
                    }
                }
                Console.WriteLine("PASS Office smart title after confirmation and controller restart");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
