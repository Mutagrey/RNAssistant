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
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void EditingMiddleUserMessageRewindsHistoryAndClearsHtmlWorkspace()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var attachmentStore = new AttachmentStore(paths);
                var session = new ChatSession
                {
                    Host = "Excel",
                    DocumentKey = "edit-document",
                    DocumentTitle = "Edit.xlsx",
                    Title = "Edit test"
                };
                var sessionId = session.Id;
                var pendingRemoved = false;
                var pendingCancelled = false;
                var service = new ChatHistoryEditService(
                    attachmentStore,
                    delegate(string id)
                    {
                        pendingRemoved = string.Equals(id, sessionId, StringComparison.OrdinalIgnoreCase);
                    },
                    delegate(ChatSession current, string reason)
                    {
                        pendingCancelled = current == session && reason.IndexOf("history changed", StringComparison.OrdinalIgnoreCase) >= 0;
                        foreach (var message in current.Messages.Where(item => item != null && item.Activity != null))
                        {
                            message.Activity.PendingId = null;
                            message.Activity.Status = "cancelled";
                            message.Activity.ExecutionStatus = "cancelled";
                            message.Activity.ResultMessage = reason;
                        }
                    });

                session.Messages.Add(new ChatMessage { Role = "user", Content = "Первый вопрос" });
                session.Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = string.Empty,
                    Activity = new ChatActivity
                    {
                        PendingId = "pending-before-edit",
                        Status = "waiting",
                        ExecutionStatus = "waiting"
                    }
                });

                session.HtmlWorkspace.Files.Add(new HtmlWorkspaceFile
                {
                    Id = "index",
                    Path = "index.html",
                    Kind = "html",
                    Content = "<h1>Before edited turn</h1>"
                });
                session.HtmlWorkspace.DataSources.Add(new HtmlWorkspaceDataSource { Id = "data", Name = "data", Json = "{\"version\":1}" });
                var workspaceBeforeTarget = HtmlWorkspaceArtifactService.CaptureCurrent(session, "Before edited turn");

                var targetAttachment = attachmentStore.Import(
                    "edit.txt",
                    "text/plain",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes("TARGET_ATTACHMENT")));
                var target = new ChatMessage
                {
                    Role = "user",
                    Content = "Второй вопрос",
                    PromptTokens = 10,
                    CompletionTokens = 2,
                    TotalTokens = 12,
                    UsageJson = "{}",
                    ReasoningContent = "stale",
                    ReasoningTokens = 3,
                    ReasoningTruncated = true,
                    RunId = "old-run",
                    Sequence = 4,
                    HtmlWorkspaceCheckpointId = workspaceBeforeTarget
                };
                target.Attachments.Add(targetAttachment);
                attachmentStore.Commit(sessionId, target);
                session.Messages.Add(target);
                session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Второй ответ" });

                var tailAttachment = attachmentStore.Import(
                    "tail.txt",
                    "text/plain",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes("TAIL_ATTACHMENT")));
                var tail = new ChatMessage { Role = "user", Content = "Третий вопрос" };
                tail.Attachments.Add(tailAttachment);
                attachmentStore.Commit(sessionId, tail);
                session.Messages.Add(tail);
                session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Третий ответ" });

                session.PendingAgentTask = new PendingAgentTask { Request = "stale pending" };
                session.LastRun = new ChatRunRecord { RunId = "stale-run", Status = "waiting" };
                session.HtmlWorkspace.Files[0].Content = "<h1>After edited turn</h1>";
                session.HtmlWorkspace.Files.Add(new HtmlWorkspaceFile { Id = "later", Path = "later.html", Kind = "html", Content = "later" });
                session.HtmlWorkspace.DataSources.Add(new HtmlWorkspaceDataSource { Id = "later-data", Name = "later-data", Json = "{}" });
                var workspaceAfterTarget = HtmlWorkspaceArtifactService.CaptureCurrent(session, "After edited turn");
                ChatArtifactService.LinkMessageArtifacts(session, 0);
                var targetAttachmentArtifactId = "attachment_" + targetAttachment.Id;
                var tailAttachmentArtifactId = "attachment_" + tailAttachment.Id;
                var staleCheckpoint = new ContextCheckpoint { ThroughMessageId = target.Id, SummaryMarkdown = "stale" };
                session.ContextCheckpoints.Add(staleCheckpoint);
                session.ActiveContextCheckpointId = staleCheckpoint.Id;

                var targetId = target.Id;
                var targetCreatedUtc = target.CreatedUtc;
                var targetPath = AbsoluteAttachmentPath(paths, targetAttachment);
                var tailPath = AbsoluteAttachmentPath(paths, tailAttachment);
                AssertTrue(File.Exists(targetPath), "target attachment committed");
                AssertTrue(File.Exists(tailPath), "tail attachment committed");

                var result = service.RewriteUserMessage(
                    session,
                    sessionId,
                    target.Id,
                    -1,
                    "  Второй вопрос после правки  ");

                AssertEqual(3, session.Messages.Count, "history rewound through edited message");
                AssertEqual(target, result.Message, "edited message instance preserved");
                AssertEqual(targetId, target.Id, "edited message id preserved");
                AssertEqual(targetCreatedUtc, target.CreatedUtc, "edited message time preserved");
                AssertEqual("Второй вопрос после правки", target.Content, "edited message text stored");
                AssertEqual(1, target.Attachments.Count, "edited message attachments preserved");
                AssertTrue(File.Exists(targetPath), "edited attachment file still exists");
                AssertTrue(!File.Exists(tailPath), "tail attachment file removed");
                AssertTrue(session.Messages.All(message => message == null || message.Content != "Третий вопрос"), "tail user turn removed");
                AssertTrue(target.PromptTokens == null && target.CompletionTokens == null && target.TotalTokens == null, "edited usage cleared");
                AssertTrue(target.UsageJson == null && target.ReasoningContent == null && target.ReasoningTokens == null, "edited reasoning cleared");
                AssertTrue(!target.ReasoningTruncated && target.RunId == null && target.Sequence == null, "edited run metadata cleared");
                AssertEqual(1, session.HtmlWorkspace.Files.Count, "html files restored to exact pre-turn revision");
                AssertEqual("<h1>Before edited turn</h1>", session.HtmlWorkspace.Files[0].Content, "pre-turn html content restored");
                AssertEqual(1, session.HtmlWorkspace.DataSources.Count, "html data restored to exact pre-turn revision");
                AssertEqual("{\"version\":1}", session.HtmlWorkspace.DataSources[0].Json, "pre-turn html data restored");
                AssertEqual(workspaceBeforeTarget, session.ActiveHtmlArtifactId, "pre-turn artifact is active");
                AssertTrue(session.Artifacts.Any(artifact => artifact.Id == workspaceBeforeTarget), "active html revision remains reachable");
                AssertTrue(!session.Artifacts.Any(artifact => artifact.Id == workspaceAfterTarget), "future html revision is pruned");
                AssertTrue(session.Artifacts.Any(artifact => artifact.Id == targetAttachmentArtifactId), "edited message attachment artifact remains");
                AssertTrue(!session.Artifacts.Any(artifact => artifact.Id == tailAttachmentArtifactId), "removed tail attachment artifact is pruned");
                AssertEqual(0, session.ContextCheckpoints.Count, "stale compacted context invalidated after edit");
                AssertTrue(string.IsNullOrWhiteSpace(session.ActiveContextCheckpointId), "no stale active checkpoint remains");
                AssertTrue(session.PendingAgentTask == null, "pending task cleared");
                AssertTrue(session.LastRun == null, "last run cleared");
                AssertTrue(pendingRemoved, "pending tool registry cleared");
                AssertTrue(pendingCancelled, "pending activity cancellation invoked");
                AssertEqual("cancelled", session.Messages[1].Activity.Status, "earlier pending activity cancelled");
            });
        }

        private static void EditingLatestUserMessageDoesNotDuplicateUserTurn()
        {
            var session = new ChatSession();
            var edited = new ChatMessage { Role = "user", Content = "Измененный вопрос" };
            session.Messages.Add(edited);
            var captured = new List<ChatMessage>();
            var service = new PlainChatService(
                delegate(
                    AppSettings settings,
                    IEnumerable<ChatMessage> messages,
                    LlmRequestOptions requestOptions,
                    Action<LlmStreamUpdate> streamProgress,
                    CancellationToken cancellationToken)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    captured = new List<ChatMessage>(messages ?? new ChatMessage[0]);
                    return Task.FromResult(new LlmCompletionResult
                    {
                        Content = "Обновленный ответ.",
                        PromptTokens = 10,
                        CompletionTokens = 2,
                        TotalTokens = 12
                    });
                });

            service.ExecuteAsync(
                edited.Content,
                session,
                new DocumentContext(),
                new AppSettings(),
                edited.Attachments,
                null,
                CancellationToken.None,
                false).GetAwaiter().GetResult();

            var users = session.Messages.Where(message =>
                message != null && string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)).ToList();
            AssertEqual(2, session.Messages.Count, "replay keeps a single exchange");
            AssertEqual(1, users.Count, "replay does not duplicate user turn");
            AssertEqual(edited.Id, users[0].Id, "replay preserves edited user id");
            AssertEqual("Обновленный ответ.", session.Messages.Last().Content, "replay appends assistant answer");
            AssertEqual(
                1,
                FlattenMessages(captured).Split(new[] { edited.Content }, StringSplitOptions.None).Length - 1,
                "edited prompt appears once");
        }

        private static void EditingLegacyTurnClearsUnversionedHtmlWorkspace()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var session = new ChatSession();
                var user = new ChatMessage { Role = "user", Content = "Пересобери страницу" };
                session.Messages.Add(user);
                session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Готово" });
                session.HtmlWorkspace.Files.Add(new HtmlWorkspaceFile
                {
                    Id = "index",
                    Path = "index.html",
                    Kind = "html",
                    Content = "<h1>Later state</h1>"
                });

                var service = new ChatHistoryEditService(new AttachmentStore(paths), delegate { }, delegate { });
                service.RewriteUserMessage(session, session.Id, user.Id, -1, "Сделай иначе");

                AssertEqual(0, session.HtmlWorkspace.Files.Count, "unversioned future html is not retained after edit");
                AssertTrue(string.IsNullOrWhiteSpace(session.ActiveHtmlArtifactId), "unversioned edit has no active html artifact");
            });
        }

        private static void EditingMessageValidationErrorsAreReported()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var service = new ChatHistoryEditService(
                    new AttachmentStore(paths),
                    delegate { },
                    delegate { });
                var session = new ChatSession();
                var user = new ChatMessage { Role = "user", Content = "Исходный вопрос" };
                var assistant = new ChatMessage { Role = "assistant", Content = "Ответ" };
                session.Messages.Add(user);
                session.Messages.Add(assistant);

                ExpectEditFailure(
                    service,
                    session,
                    "Пропавшее сообщение",
                    "missing-message",
                    0,
                    "Message was not found.",
                    "stale id does not fall back to valid index");
                AssertEqual("Исходный вопрос", user.Content, "stale id leaves indexed message unchanged");

                ExpectEditFailure(
                    service,
                    session,
                    "Нельзя менять assistant",
                    assistant.Id,
                    -1,
                    "Only user messages can be edited.",
                    "assistant edit rejected");
                ExpectEditFailure(
                    service,
                    session,
                    "   ",
                    user.Id,
                    -1,
                    "Message text is required.",
                    "blank edit rejected");
            });
        }

        private static void ChatRunLeaseSerializesHistoryMutations()
        {
            var registry = new ChatRunRegistry();
            var session = new ChatSession();
            var lease = registry.Start("chat-1", "run-1", session);

            AssertTrue(registry.IsRunning("chat-1"), "run registered");
            try
            {
                registry.Start("chat-1", "history-edit", session);
                throw new InvalidOperationException("parallel history edit unexpectedly acquired a lease");
            }
            catch (InvalidOperationException ex)
            {
                if (ex.Message.IndexOf("unexpectedly", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    throw;
                }
                AssertContains(ex.Message, "уже выполняется", "history edit rejected while run is active");
            }

            lease.Dispose();
            lease.Dispose();
            AssertTrue(!registry.IsRunning("chat-1"), "idempotent lease release removes run");

            using (registry.Start("chat-1", "history-edit", session))
            {
                AssertTrue(registry.IsRunning("chat-1"), "chat can be reserved after release");
            }
            AssertTrue(!registry.IsRunning("chat-1"), "history lease released");
        }

        private static void ConfirmedToolRunLeaseRejectsDuplicateAndSupportsCancellation()
        {
            var registry = new ChatRunRegistry();
            var session = new ChatSession();
            var cancellation = new CancellationTokenSource();
            var executions = 0;
            var lease = registry.Start("chat-confirm", "confirm-1", session, cancellation);
            executions += 1;

            try
            {
                registry.Start("chat-confirm", "confirm-2", session);
                executions += 1;
                throw new InvalidOperationException("duplicate confirm unexpectedly acquired a lease");
            }
            catch (InvalidOperationException ex)
            {
                if (ex.Message.IndexOf("unexpectedly", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    throw;
                }
                AssertContains(ex.Message, "уже выполняется", "duplicate confirm rejected");
            }

            AssertEqual(1, executions, "confirmed tool executes once");
            AssertTrue(registry.Cancel("chat-confirm", "confirm-1"), "confirm cancellation accepted");
            AssertTrue(cancellation.IsCancellationRequested, "confirm cancellation source signalled");
            lease.Dispose();
            AssertTrue(!registry.IsRunning("chat-confirm"), "confirm lease released");
        }

        private static string AbsoluteAttachmentPath(AppDataPaths paths, ChatAttachment attachment)
        {
            return Path.GetFullPath(Path.Combine(
                paths.AttachmentDirectory,
                attachment == null ? string.Empty : attachment.RelativePath ?? string.Empty));
        }

        private static void ExpectEditFailure(
            ChatHistoryEditService service,
            ChatSession session,
            string text,
            string messageId,
            int index,
            string expected,
            string name)
        {
            try
            {
                service.RewriteUserMessage(session, session.Id, messageId, index, text);
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
