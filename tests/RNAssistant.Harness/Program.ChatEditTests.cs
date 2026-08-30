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
using RuntimeToolResult = RNAssistant.Core.Tools.Contracts.ToolResult;

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
                    Convert.ToBase64String(Encoding.UTF8.GetBytes("TARGET_ATTACHMENT")),
                    session.Id);
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
                    ResponseProtocolVersion = AgentResponseProtocol.CurrentVersion,
                    ResponseStatus = AgentResponseStatuses.Completed,
                    RunId = "old-run",
                    Sequence = 4,
                    HtmlWorkspaceCheckpoint = HtmlCheckpoint(session, workspaceBeforeTarget)
                };
                target.Attachments.Add(targetAttachment);
                attachmentStore.CommitToCas(target);
                session.Messages.Add(target);
                session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Второй ответ" });

                var tailAttachment = attachmentStore.Import(
                    "tail.txt",
                    "text/plain",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes("TAIL_ATTACHMENT")),
                    session.Id);
                var tail = new ChatMessage { Role = "user", Content = "Третий вопрос" };
                tail.Attachments.Add(tailAttachment);
                attachmentStore.CommitToCas(tail);
                session.Messages.Add(tail);
                session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Третий ответ" });

                session.LastRun = new ChatRunRecord { RunId = "stale-run", Status = "waiting" };
                session.HtmlWorkspace.Files[0].Content = "<h1>After edited turn</h1>";
                session.HtmlWorkspace.Files.Add(new HtmlWorkspaceFile { Id = "later", Path = "later.html", Kind = "html", Content = "later" });
                session.HtmlWorkspace.DataSources.Add(new HtmlWorkspaceDataSource { Id = "later-data", Name = "later-data", Json = "{}" });
                var workspaceAfterTarget = HtmlWorkspaceArtifactService.CaptureCurrent(session, "After edited turn");
                ChatResourceReferenceService.LinkMessageResources(session, 0);
                var targetAttachmentArtifactId = "attachment_" + targetAttachment.Id;
                var tailAttachmentArtifactId = "attachment_" + tailAttachment.Id;
                var staleCheckpoint = new ContextCheckpoint { ThroughMessageId = target.Id, SummaryMarkdown = "stale" };
                session.ContextCheckpoints.Add(staleCheckpoint);
                session.ActiveContextCheckpointId = staleCheckpoint.Id;

                var targetId = target.Id;
                var targetCreatedUtc = target.CreatedUtc;
                var targetPath = AbsoluteAttachmentPath(paths, targetAttachment);
                var tailPath = AbsoluteAttachmentPath(paths, tailAttachment);
                AssertTrue(File.Exists(targetPath), "target attachment blob committed");
                AssertTrue(File.Exists(tailPath), "tail attachment blob committed");

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
                AssertTrue(File.Exists(targetPath), "edited attachment blob still exists");
                AssertTrue(File.Exists(tailPath), "tail blob remains immutable while history is rewritten");
                AssertTrue(result.RemovedMessages.Contains(tail), "removed tail is returned for post-save cleanup");
                foreach (var removed in result.RemovedMessages) attachmentStore.DeleteMessage(removed);
                AssertTrue(File.Exists(tailPath), "logical deletion does not remove a potentially shared CAS blob");
                AssertTrue(session.Messages.All(message => message == null || message.Content != "Третий вопрос"), "tail user turn removed");
                AssertTrue(target.PromptTokens == null && target.CompletionTokens == null && target.TotalTokens == null, "edited usage cleared");
                AssertTrue(target.UsageJson == null && target.ReasoningContent == null && target.ReasoningTokens == null, "edited reasoning cleared");
                AssertTrue(!target.ReasoningTruncated && target.RunId == null && target.Sequence == null, "edited run metadata cleared");
                AssertTrue(target.ResponseProtocolVersion == 0 && string.IsNullOrWhiteSpace(target.ResponseStatus),
                    "edited user message clears stale response metadata");
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
                AssertTrue(session.LastRun == null, "last run cleared");
                AssertTrue(pendingRemoved, "pending tool registry cleared");
                AssertTrue(pendingCancelled, "pending activity cancellation invoked");
                AssertEqual("cancelled", session.Messages[1].Activity.Status, "earlier pending activity cancelled");
            });
        }

        private static void ReplayingUnchangedUserMessageRewindsHistory()
        {
            var session = new ChatSession();
            var target = new ChatMessage { Role = "user", Content = "Повтори запрос" };
            var oldAnswer = new ChatMessage { Role = "assistant", Content = "Старый ответ" };
            session.Messages.Add(target);
            session.Messages.Add(oldAnswer);

            var service = new ChatHistoryEditService(delegate { }, delegate { });
            var result = service.RewriteUserMessage(
                session,
                session.Id,
                target.Id,
                -1,
                "  Повтори запрос  ");

            AssertEqual(1, session.Messages.Count, "unchanged replay rewinds the old answer");
            AssertEqual(target, result.Message, "unchanged replay preserves the user message");
            AssertEqual("Повтори запрос", target.Content, "unchanged replay keeps normalized text");
            AssertTrue(result.RemovedMessages.Contains(oldAnswer), "unchanged replay returns the removed answer");
        }

        private static void EditingLatestUserMessageDoesNotDuplicateUserTurn()
        {
            WithTempExecutor((executor, adapter) =>
            {
                var session = NewSession(adapter);
                session.Mode = ChatModes.Chat;
                var edited = new ChatMessage { Role = "user", Content = "Измененный вопрос" };
                session.Messages.Add(edited);
                var captured = new List<ChatMessage>();
                var service = CreateConversationRunService(
                    adapter,
                    executor,
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
                            Content = "{\"message\":\"Обновленный ответ.\",\"tool_calls\":[]}",
                            PromptTokens = 10,
                            CompletionTokens = 2,
                            TotalTokens = 12
                        });
                    });

                service.ExecuteAsync(
                    ChatModes.Chat,
                    edited.Content,
                    session,
                    new DocumentContext(),
                    new AppSettings(),
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(),
                    edited.Attachments,
                    null,
                    null,
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
            });
        }

        private static void EditingTurnWithoutCheckpointClearsUnversionedHtmlWorkspace()
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

                var service = new ChatHistoryEditService(delegate { }, delegate { });
                service.RewriteUserMessage(session, session.Id, user.Id, -1, "Сделай иначе");

                AssertEqual(0, session.HtmlWorkspace.Files.Count, "unversioned future html is not retained after edit");
                AssertTrue(string.IsNullOrWhiteSpace(session.ActiveHtmlArtifactId), "unversioned edit has no active html artifact");
            });
        }

        private static void EditingWithUnavailableHtmlCheckpointFailsClosed()
        {
            var session = new ChatSession();
            var broken = new ChatArtifact
            {
                Kind = ChatArtifactKinds.HtmlWorkspace,
                Title = "Unavailable workspace",
                MimeType = "application/vnd.rnassistant.html-workspace+json",
                InlineText = null
            };
            session.Artifacts.Add(broken);
            var user = new ChatMessage
            {
                Role = "user",
                Content = "Пересобери страницу",
                HtmlWorkspaceCheckpoint = ArtifactReference(session, broken)
            };
            session.Messages.Add(user);
            session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Готово" });

            var service = new ChatHistoryEditService(delegate { }, delegate { }, (current, artifactId) => false);
            service.RewriteUserMessage(session, session.Id, user.Id, -1, "Сделай иначе");

            AssertEqual(broken.Id, session.ActiveHtmlArtifactId, "broken checkpoint identity is preserved");
            AssertEqual(HtmlWorkspaceRecoveryStatuses.Degraded, session.HtmlWorkspaceRecovery.Status,
                "unavailable checkpoint enters recovery mode");
            AssertEqual(HtmlWorkspaceRecoveryIssues.ActiveBodyUnavailable, session.HtmlWorkspaceRecovery.Issue,
                "missing checkpoint body is reported");
            AssertTrue(!session.HtmlWorkspaceRecovery.CanMutate, "HTML mutation stays blocked until explicit recovery");
            AssertTrue(session.Artifacts.Any(item => item.Id == broken.Id), "broken checkpoint metadata remains reachable");
        }

        private static void EditingMessageValidationErrorsAreReported()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var service = new ChatHistoryEditService(
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
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var registry = new ChatRunRegistry(paths);
                var secondRegistry = new ChatRunRegistry(paths);
                var session = new ChatSession { Title = "Before run" };
                var lease = registry.Start("chat-1", "run-1", session);
                session.Title = "Mutated live object";

                AssertTrue(registry.IsRunning("chat-1"), "run registered");
                AssertTrue(secondRegistry.IsExternallyRunning("chat-1"), "cross-registry lock is visible");
                AssertTrue(secondRegistry.HasExternalRuns(), "maintenance detects a run in another registry");
                AssertEqual("Before run", registry.Get("chat-1").Session.Title, "run snapshot is immutable");
                var exposedSnapshot = registry.Get("chat-1");
                exposedSnapshot.Session.Title = "Mutated returned snapshot";
                AssertEqual("Before run", registry.Get("chat-1").Session.Title, "returned snapshot cannot mutate registry state");
                try
                {
                    secondRegistry.Start("chat-1", "history-edit", session);
                    throw new InvalidOperationException("parallel history edit unexpectedly acquired a lease");
                }
                catch (InvalidOperationException ex)
                {
                    if (ex.Message.IndexOf("unexpectedly", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        throw;
                    }
                    AssertContains(ex.Message, "другом окне", "history edit rejected across registries");
                }

                lease.Dispose();
                lease.Dispose();
                AssertTrue(!registry.IsRunning("chat-1"), "idempotent lease release removes run");
                AssertTrue(!secondRegistry.IsExternallyRunning("chat-1"), "cross-registry lock is released");
                AssertTrue(!secondRegistry.HasExternalRuns(), "maintenance sees no run after release");

                using (registry.ReserveMaintenance())
                using (registry.ReserveMaintenance())
                {
                    AssertTrue(!registry.HasRuns(), "maintenance lock is reentrant for composed operations");
                }

                using (secondRegistry.Start("chat-1", "history-edit", session))
                {
                    AssertTrue(secondRegistry.IsRunning("chat-1"), "chat can be reserved after release");
                }
                AssertTrue(!secondRegistry.IsRunning("chat-1"), "history lease released");
            });
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

            var shutdownCancellation = new CancellationTokenSource();
            var shutdownLease = registry.Start("chat-shutdown", "run-shutdown", session, shutdownCancellation);
            registry.CancelAll();
            AssertTrue(shutdownCancellation.IsCancellationRequested, "runtime shutdown cancels active run");
            AssertTrue(registry.IsRunning("chat-shutdown"), "runtime shutdown keeps lease until run exits");
            shutdownLease.Dispose();
            AssertTrue(!registry.IsRunning("chat-shutdown"), "cancelled run releases lease on exit");
        }

        private static void ToolExchangeDeletionIsScoped()
        {
            var firstCall = new ChatMessage
            {
                Role = "assistant",
                ProtocolMessage = true,
                ToolCallId = "call_1",
                ToolCalls = new List<LlmToolCall> { new LlmToolCall { Id = "call_1", Name = "excel.inspect" } }
            };
            var firstResult = AgentJsonProtocol.CreateToolResultMessage(
                new ToolCommand { ToolCallId = firstCall.ToolCallId, ToolId = "excel.inspect" },
                RuntimeToolResult.Ok("Read"), ToolResultRoles.Developer);
            var secondCall = new ChatMessage
            {
                Role = "assistant",
                ProtocolMessage = true,
                ToolCallId = "call_1",
                ToolCalls = new List<LlmToolCall> { new LlmToolCall { Id = "call_1", Name = "excel.inspect" } }
            };
            var secondActivity = new ChatMessage
            {
                Role = "assistant",
                Activity = new ChatActivity { ToolCallId = "call_1", Kind = "tool" }
            };
            var secondResult = AgentJsonProtocol.CreateToolResultMessage(
                new ToolCommand { ToolCallId = secondCall.ToolCallId, ToolId = "excel.inspect" },
                RuntimeToolResult.Ok("Read"), ToolResultRoles.Developer);
            var messages = new List<ChatMessage>
            {
                new ChatMessage { Role = "user", Content = "First" },
                firstCall,
                firstResult,
                secondCall,
                secondActivity,
                secondResult,
                new ChatMessage { Role = "assistant", Content = "Second done" }
            };

            var selected = ChatHistoryEditService.SelectMessagesForDeletion(messages, 4);
            AssertEqual(3, selected.Count, "one contiguous tool exchange selected");
            AssertTrue(selected.Contains(secondCall) && selected.Contains(secondActivity) && selected.Contains(secondResult),
                "selected exchange is complete");
            AssertTrue(!selected.Contains(firstCall) && !selected.Contains(firstResult),
                "reused call id in the same run is preserved");

            var mismatchedResult = AgentJsonProtocol.CreateToolResultMessage(
                new ToolCommand { ToolCallId = "unrelated-call", ToolId = "excel.inspect" },
                RuntimeToolResult.Ok("Read"), ToolResultRoles.Developer);
            // A body claiming call_1 cannot add this different metadata call to its exchange.
            mismatchedResult.Content = secondResult.Content;
            AssertTrue(ToolProtocolMessages.Ids(mismatchedResult).SetEquals(new[] { "unrelated-call" }),
                "result body cannot introduce IDs beside the canonical metadata ID");
            messages.Insert(5, mismatchedResult);
            var selectedWithMismatchedBody = ChatHistoryEditService.SelectMessagesForDeletion(messages, 4);
            AssertTrue(selectedWithMismatchedBody.SequenceEqual(selected),
                "a mismatched result body cannot expand contiguous exchange selection");
            messages.Remove(mismatchedResult);

            var danglingCall = new ChatMessage
            {
                Role = "assistant",
                ProtocolMessage = true,
                ToolCallId = "call_1",
                ToolCalls = new List<LlmToolCall> { new LlmToolCall { Id = "call_1", Name = "excel.inspect" } }
            };
            messages.Insert(messages.Count - 1, danglingCall);
            ChatHistoryEditService.ExcludeUnmatchedToolCalls(messages);
            AssertTrue(!firstCall.ExcludeFromModelContext && !secondCall.ExcludeFromModelContext,
                "completed calls remain replayable");
            AssertTrue(danglingCall.ExcludeFromModelContext,
                "later reused id cannot borrow an earlier result");
            AssertTrue(!ChatHistoryEditService.HasResultForLatestToolCall(messages, "call_1"),
                "latest reused id is reported as unmatched");

            var multiCall = new ChatMessage
            {
                Role = "assistant",
                ProtocolMessage = true,
                ToolCalls = new List<LlmToolCall>
                {
                    new LlmToolCall { Id = "multi_1", Name = "excel.inspect" },
                    new LlmToolCall { Id = "multi_2", Name = "excel.inspect" }
                }
            };
            var multiResult1 = AgentJsonProtocol.CreateToolResultMessage(
                new ToolCommand { ToolCallId = "multi_1", ToolId = "excel.inspect" },
                RuntimeToolResult.Ok("Read"), ToolResultRoles.Tool);
            var incompleteMulti = new List<ChatMessage> { multiCall, multiResult1 };
            ChatHistoryEditService.ExcludeUnmatchedToolCalls(incompleteMulti);
            AssertTrue(multiCall.ExcludeFromModelContext && multiResult1.ExcludeFromModelContext,
                "partial multi-call exchange is excluded as one unit");
            AssertTrue(!ChatHistoryEditService.HasResultForLatestToolCall(incompleteMulti, "multi_2"),
                "result matching is exact for multi-call envelopes");

            var completeCall = new ChatMessage
            {
                Role = "assistant",
                ProtocolMessage = true,
                ToolCalls = new List<LlmToolCall>
                {
                    new LlmToolCall { Id = "complete_1", Name = "excel.inspect" },
                    new LlmToolCall { Id = "complete_2", Name = "excel.inspect" }
                }
            };
            var completeResult1 = AgentJsonProtocol.CreateToolResultMessage(
                new ToolCommand { ToolCallId = "complete_1", ToolId = "excel.inspect" },
                RuntimeToolResult.Ok("Read"), ToolResultRoles.Tool);
            var completeResult2 = AgentJsonProtocol.CreateToolResultMessage(
                new ToolCommand { ToolCallId = "complete_2", ToolId = "excel.inspect" },
                RuntimeToolResult.Ok("Read"), ToolResultRoles.Tool);
            var completeMulti = new List<ChatMessage> { completeCall, completeResult1, completeResult2 };
            var completeSelection = ChatHistoryEditService.SelectMessagesForDeletion(completeMulti, 1);
            AssertEqual(3, completeSelection.Count, "deleting one multi-call result selects the full envelope");
            AssertTrue(ChatHistoryEditService.HasResultForLatestToolCall(completeMulti, "complete_2"),
                "completed multi-call result is matched by exact id");
        }

        private static string AbsoluteAttachmentPath(AppDataPaths paths, ChatAttachment attachment)
        {
            var hash = attachment == null ? string.Empty : attachment.ContentSha256 ?? string.Empty;
            return Path.GetFullPath(Path.Combine(
                paths.ChatBlobDirectory,
                hash.Length >= 2 ? hash.Substring(0, 2) : "00",
                hash + ".blob"));
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
