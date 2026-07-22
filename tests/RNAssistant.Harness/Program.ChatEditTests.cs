using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office;
using RNAssistant.OfficeHosts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void EditingMiddleUserMessageRewindsHistoryAndClearsHtmlWorkspace()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var store = new ChatStore(paths);
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var responses = new Queue<string>(new[]
                {
                    "Ответ 1.",
                    "Ответ 2.",
                    "Ответ 3.",
                    "Ответ после правки."
                });
                var controller = new AssistantController(
                    adapter,
                    paths,
                    delegate(AppSettings settings, IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        calls.Add(new List<ChatMessage>(messages ?? new ChatMessage[0]));
                        return Task.FromResult(new LlmCompletionResult
                        {
                            Content = responses.Dequeue(),
                            PromptTokens = 10,
                            CompletionTokens = 2,
                            TotalTokens = 12
                        });
                    });

                controller.SaveSettings(new AppSettings { SmartChatTitles = false, ContextCharLimit = 8000 }, null);
                var created = controller.CreateChat("Новый чат");
                controller.SetChatMode(created.ActiveChatId, ChatModes.Chat);

                controller.SendChatAsync("Первый вопрос", created.ActiveChatId).GetAwaiter().GetResult();

                var targetDraft = controller.ImportAttachment(
                    "edit.txt",
                    "text/plain",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes("TARGET_ATTACHMENT")));
                controller.SendChatAsync(
                    "Второй вопрос",
                    created.ActiveChatId,
                    new[] { targetDraft.Attachment.Id }).GetAwaiter().GetResult();

                var tailDraft = controller.ImportAttachment(
                    "tail.txt",
                    "text/plain",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes("TAIL_ATTACHMENT")));
                controller.SendChatAsync(
                    "Третий вопрос",
                    created.ActiveChatId,
                    new[] { tailDraft.Attachment.Id }).GetAwaiter().GetResult();

                var before = store.Load(created.ActiveChatId);
                var target = before.Messages.First(message =>
                    message != null &&
                    string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(message.Content, "Второй вопрос", StringComparison.Ordinal));
                var tail = before.Messages.First(message =>
                    message != null &&
                    string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(message.Content, "Третий вопрос", StringComparison.Ordinal));
                var targetAttachment = target.Attachments.Single();
                var tailAttachment = tail.Attachments.Single();
                var targetPath = AbsoluteAttachmentPath(paths, targetAttachment);
                var tailPath = AbsoluteAttachmentPath(paths, tailAttachment);
                AssertTrue(File.Exists(targetPath), "target attachment committed");
                AssertTrue(File.Exists(tailPath), "tail attachment committed");

                before.HtmlWorkspace.Files.Add(new HtmlWorkspaceFile
                {
                    Id = "index",
                    Path = "index.html",
                    Kind = "html",
                    Content = "<p>stale html</p>"
                });
                store.Save(before);

                var edited = controller.EditMessageAsync(
                    "Второй вопрос после правки",
                    target.Id,
                    -1,
                    created.ActiveChatId).GetAwaiter().GetResult();

                var after = store.Load(created.ActiveChatId);
                var editedTarget = after.Messages.First(message =>
                    message != null &&
                    string.Equals(message.Id, target.Id, StringComparison.OrdinalIgnoreCase));

                AssertEqual(4, after.Messages.Count, "rewound message count");
                AssertEqual(target.Id, editedTarget.Id, "edited message id preserved");
                AssertEqual(target.CreatedUtc, editedTarget.CreatedUtc, "edited message time preserved");
                AssertEqual("Второй вопрос после правки", editedTarget.Content, "edited message text stored");
                AssertEqual(1, editedTarget.Attachments.Count, "edited message attachments preserved");
                AssertEqual(targetAttachment.FileName, editedTarget.Attachments[0].FileName, "edited attachment file name");
                AssertTrue(File.Exists(AbsoluteAttachmentPath(paths, editedTarget.Attachments[0])), "edited attachment file still exists");
                AssertTrue(!File.Exists(tailPath), "deleted tail attachment removed");
                AssertTrue(after.Messages.All(message =>
                    message == null ||
                    (message.Content ?? string.Empty).IndexOf("Третий вопрос", StringComparison.OrdinalIgnoreCase) < 0),
                    "tail user turn removed");
                AssertEqual(0, after.HtmlWorkspace.Files.Count, "stored html workspace files cleared");
                AssertEqual(0, after.HtmlWorkspace.DataSources.Count, "stored html workspace data cleared");
                AssertEqual(0, edited.HtmlWorkspace.Files.Count, "response html workspace files cleared");
                AssertEqual(0, edited.HtmlWorkspace.DataSources.Count, "response html workspace data cleared");
                AssertEqual("Ответ после правки.", after.Messages.Last().Content, "fresh assistant response stored");

                var replayPrompt = FlattenMessages(calls[calls.Count - 1]);
                AssertContains(replayPrompt, "Второй вопрос после правки", "edited prompt included");
                AssertTrue(replayPrompt.IndexOf("Третий вопрос", StringComparison.OrdinalIgnoreCase) < 0, "tail prompt removed");
            });
        }

        private static void EditingLatestUserMessageDoesNotDuplicateUserTurn()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var store = new ChatStore(paths);
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var responses = new Queue<string>(new[]
                {
                    "Исходный ответ.",
                    "Обновленный ответ."
                });
                var controller = new AssistantController(
                    adapter,
                    paths,
                    delegate(AppSettings settings, IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        calls.Add(new List<ChatMessage>(messages ?? new ChatMessage[0]));
                        return Task.FromResult(new LlmCompletionResult
                        {
                            Content = responses.Dequeue(),
                            PromptTokens = 10,
                            CompletionTokens = 2,
                            TotalTokens = 12
                        });
                    });

                controller.SaveSettings(new AppSettings { SmartChatTitles = false, ContextCharLimit = 8000 }, null);
                var created = controller.CreateChat("Новый чат");
                controller.SetChatMode(created.ActiveChatId, ChatModes.Chat);
                controller.SendChatAsync("Исходный вопрос", created.ActiveChatId).GetAwaiter().GetResult();

                var before = store.Load(created.ActiveChatId);
                var target = before.Messages.Single(message =>
                    message != null &&
                    string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase));

                controller.EditMessageAsync(
                    "Измененный вопрос",
                    target.Id,
                    -1,
                    created.ActiveChatId).GetAwaiter().GetResult();

                var after = store.Load(created.ActiveChatId);
                var users = after.Messages.Where(message =>
                    message != null &&
                    string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)).ToList();

                AssertEqual(2, after.Messages.Count, "latest edit keeps single exchange");
                AssertEqual(1, users.Count, "latest edit does not duplicate user turn");
                AssertEqual(target.Id, users[0].Id, "latest edit preserves user id");
                AssertEqual("Измененный вопрос", users[0].Content, "latest edit updates text");
                AssertEqual("Обновленный ответ.", after.Messages.Last().Content, "latest edit reruns assistant");

                var replayPrompt = FlattenMessages(calls[calls.Count - 1]);
                AssertEqual(
                    1,
                    replayPrompt.Split(new[] { "Измененный вопрос" }, StringSplitOptions.None).Length - 1,
                    "edited latest user prompt is not duplicated");
            });
        }

        private static void EditingMessageValidationErrorsAreReported()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var store = new ChatStore(paths);
                var controller = new AssistantController(
                    adapter,
                    paths,
                    delegate(AppSettings settings, IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return Task.FromResult(new LlmCompletionResult
                        {
                            Content = "Обычный ответ.",
                            PromptTokens = 10,
                            CompletionTokens = 2,
                            TotalTokens = 12
                        });
                    });

                controller.SaveSettings(new AppSettings { SmartChatTitles = false, ContextCharLimit = 8000 }, null);
                var created = controller.CreateChat("Новый чат");
                controller.SetChatMode(created.ActiveChatId, ChatModes.Chat);
                controller.SendChatAsync("Тестовое сообщение", created.ActiveChatId).GetAwaiter().GetResult();

                var session = store.Load(created.ActiveChatId);
                var user = session.Messages.First(message =>
                    message != null &&
                    string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase));
                var assistant = session.Messages.First(message =>
                    message != null &&
                    string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase));

                ExpectEditFailure(
                    controller,
                    "Нельзя менять assistant",
                    assistant.Id,
                    -1,
                    created.ActiveChatId,
                    "Only user messages can be edited.",
                    "assistant edit rejected");
                ExpectEditFailure(
                    controller,
                    "Пропавшее сообщение",
                    "missing-message",
                    -1,
                    created.ActiveChatId,
                    "Message was not found.",
                    "missing message rejected");
                ExpectEditFailure(
                    controller,
                    "   ",
                    user.Id,
                    -1,
                    created.ActiveChatId,
                    "Message text is required.",
                    "blank edit rejected");
            });
        }

        private static void EditingMessageClearsPendingToolsWaitingActivitiesAndLastRun()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Word");
                adapter.SetVbaModule("Module1", "Sub OldMacro()\nEnd Sub", "StdModule");
                var store = new ChatStore(paths);
                var responses = new Queue<string>(new[]
                {
                    AgentBlock(Command("word.vba_replace_module", "moduleName", "Module1", "code", "Sub ChangedMacro()\nEnd Sub")),
                    FinalBlock("Ответ после редактирования.")
                });
                var controller = new AssistantController(
                    adapter,
                    paths,
                    delegate(AppSettings settings, IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return Task.FromResult(new LlmCompletionResult
                        {
                            Content = responses.Dequeue(),
                            PromptTokens = 10,
                            CompletionTokens = 2,
                            TotalTokens = 12
                        });
                    });

                controller.SaveSettings(new AppSettings
                {
                    SmartChatTitles = false,
                    ContextCharLimit = 12000,
                    AutoConfirmToolActions = false
                }, null);

                var created = controller.CreateChat("Новый чат");
                controller.SetChatMode(created.ActiveChatId, ChatModes.Agent);
                var first = controller.SendChatAsync("Замени VBA-модуль.", created.ActiveChatId).GetAwaiter().GetResult();
                var pendingId = first.Messages
                    .Where(message => message != null && message.Activity != null && !string.IsNullOrWhiteSpace(message.Activity.PendingId))
                    .Select(message => message.Activity.PendingId)
                    .FirstOrDefault();
                AssertTrue(!string.IsNullOrWhiteSpace(pendingId), "pending tool registered");

                var session = store.Load(created.ActiveChatId);
                session.Messages.Add(new ChatMessage { Role = "user", Content = "Поздний вопрос" });
                session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Поздний ответ" });
                session.PendingAgentTask = new PendingAgentTask
                {
                    Request = "Pending request",
                    LastQuestion = "Need confirmation",
                    Kind = "clarify",
                    UpdatedUtc = DateTime.UtcNow
                };
                session.LastRun = new ChatRunRecord
                {
                    RunId = "stale-run",
                    RuntimeId = "runtime-old",
                    Status = "waiting",
                    Phase = "waiting",
                    CurrentAction = "Waiting for confirmation",
                    StartedUtc = DateTime.UtcNow
                };
                store.Save(session);

                var laterUser = session.Messages.First(message =>
                    message != null &&
                    string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(message.Content, "Поздний вопрос", StringComparison.Ordinal));

                controller.EditMessageAsync(
                    "Поздний вопрос после правки",
                    laterUser.Id,
                    -1,
                    created.ActiveChatId).GetAwaiter().GetResult();

                var after = store.Load(created.ActiveChatId);
                var preservedPendingActivity = after.Messages
                    .Where(message => message != null && message.Activity != null)
                    .Select(message => message.Activity)
                    .FirstOrDefault();

                AssertTrue(preservedPendingActivity != null, "earlier activity preserved");
                AssertTrue(preservedPendingActivity.PendingId == null, "pending id cleared from activity");
                AssertEqual("cancelled", preservedPendingActivity.Status, "pending activity status cleared");
                AssertEqual("cancelled", preservedPendingActivity.ExecutionStatus, "pending activity execution status cleared");
                AssertContains(preservedPendingActivity.ResultMessage, "chat history changed", "pending activity cancellation reason");
                AssertTrue(after.PendingAgentTask == null, "pending agent task cleared");
                AssertTrue(after.LastRun == null, "last run cleared");
                AssertEqual("Ответ после редактирования.", after.Messages.Last().Content, "edit rerun completed");

                try
                {
                    controller.ConfirmAgentTool(pendingId, created.ActiveChatId);
                    throw new InvalidOperationException("pending tool confirmation unexpectedly succeeded");
                }
                catch (InvalidOperationException ex)
                {
                    AssertContains(ex.Message, "not found", "pending tool registry cleared");
                }
            });
        }

        private static string AbsoluteAttachmentPath(AppDataPaths paths, ChatAttachment attachment)
        {
            return Path.GetFullPath(Path.Combine(paths.AttachmentDirectory, attachment == null ? string.Empty : attachment.RelativePath ?? string.Empty));
        }

        private static void ExpectEditFailure(
            AssistantController controller,
            string text,
            string messageId,
            int index,
            string chatId,
            string expected,
            string name)
        {
            try
            {
                controller.EditMessageAsync(text, messageId, index, chatId).GetAwaiter().GetResult();
                throw new InvalidOperationException(name + " unexpectedly succeeded");
            }
            catch (InvalidOperationException ex)
            {
                if ((ex.Message ?? string.Empty).IndexOf("unexpectedly succeeded", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    throw;
                }
                AssertContains(ex.Message, expected, name);
            }
        }
    }
}
