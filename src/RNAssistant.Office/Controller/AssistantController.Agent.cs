using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        public ChatStateResponse ConfirmAgentTool(string pendingId, string chatId = null)
        {
            return ConfirmAgentToolAsync(pendingId, chatId, null, CancellationToken.None, null).GetAwaiter().GetResult();
        }

        public async Task<ChatStateResponse> ConfirmAgentToolAsync(
            string pendingId,
            string chatId = null,
            Action<string, string, ChatActivity> progress = null,
            CancellationToken cancellationToken = default(CancellationToken),
            string runId = null)
        {
            PendingAgentTool pending;
            var session = ResolvePendingAgentTool(pendingId, chatId, out pending);
            var sessionId = session.Id;
            runId = string.IsNullOrWhiteSpace(runId) ? Guid.NewGuid().ToString("N") : runId;
            var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ChatRunLease runLease;
            RunCausalTrace causalTrace = null;
            try
            {
                runLease = _chatRuns.Start(sessionId, runId, session, runCancellation);
            }
            catch
            {
                runCancellation.Dispose();
                throw;
            }

            try
            {
                session = ReloadReservedSession(session);
                pending = FindPendingAgentTool(session, pendingId);
                if (pending == null)
                {
                    throw new InvalidOperationException("Pending tool was not found or was already resolved.");
                }
                ConversationProtocolContext.EnsureCanContinue(session, pending.Command);
                var settings = ResolveChatSettings(session);
                settings.EnsureAgentPromptsReviewed();
                var documentRuntimeKey = CaptureExpectedRuntimeDocumentKey(session);
                if (!MarkPendingActivityExecuting(session, pending.PendingId, runId))
                {
                    throw new InvalidOperationException("Pending tool was not found or was already resolved.");
                }
                SetToolCallReplay(session, pending.Command.ToolCallId, false, runId);
                RemovePendingAgentTool(pendingId);

                var turnId = session.LastRun == null || string.IsNullOrWhiteSpace(session.LastRun.TurnId)
                    ? (session.LastRun == null ? runId : session.LastRun.RunId)
                    : session.LastRun.TurnId;
                var previousState = session.LastRun.KernelState;
                session.LastRun = new ChatRunRecord
                {
                    RunId = runId,
                    TurnId = turnId,
                    RuntimeId = _runtimeId,
                    ResponseProtocolVersion = AgentResponseProtocol.CurrentVersion,
                    Status = "running",
                    Phase = "executing",
                    KernelState = previousState,
                    CurrentAction = "Выполняю подтверждённое действие.",
                    DocumentRuntimeKey = documentRuntimeKey,
                    IterationsUsed = pending.IterationsUsed,
                    ToolStepsUsed = pending.ToolStepsUsed,
                    StartedUtc = DateTime.UtcNow
                };
                // Kernel.Resume claims the durable pending state before execution.
                // Do not persist a second, controller-owned running transition.
                causalTrace = RunCausalTrace.Begin(_chatStore, session);
                RunCausalTrace.Record(new CausalTraceRecord { Stage = "run.started", Status = "running" });
                _chatRuns.UpdateSessionSnapshot(sessionId, runId, session);

                var firstRunMessageIndex = session.Messages == null ? 0 : session.Messages.Count;
                Action<string, string, ChatActivity> runProgress = (phase, message, activity) =>
                {
                    _chatRuns.Update(sessionId, runId, phase, message);
                    if (session.LastRun != null && string.Equals(session.LastRun.RunId, runId, StringComparison.OrdinalIgnoreCase))
                    {
                        session.LastRun.Phase = string.IsNullOrWhiteSpace(phase) ? session.LastRun.Phase : phase;
                        session.LastRun.CurrentAction = string.IsNullOrWhiteSpace(message) ? session.LastRun.CurrentAction : message;
                    }
                    if (activity != null)
                    {
                        AnnotateActivity(activity, runId, null);
                    }
                    if (string.Equals(phase, "tool_running", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(phase, "tool_result", StringComparison.OrdinalIgnoreCase))
                    {
                        AnnotateRunMessages(session, firstRunMessageIndex, runId);
                    }
                    PersistRunCheckpoint(session, runId, phase);
                    ReportExternalProgress(progress, phase, message, activity);
                };

                settings.ToolResultRole = PendingToolResultRole(session, pending.Command, settings.ToolResultRole);
                var continuationAttachments = pending.Attachments ?? LatestUserAttachments(session);
                var tools = _toolCatalog.GetFreshConversationTools().Where(tool => tool.Enabled).ToList();
                var skills = _skillCatalog.GetVisibleSkills().Where(skill => skill.Enabled).ToList();
                try
                {
                    ReportProgress(runProgress, "executing", "Выполняю подтверждённое действие...");
                    var confirmedCommand = CloneCommand(pending.Command);
                    var completion = await _conversationRunService.ConfirmAsync(
                        pendingId, confirmedCommand, session,
                        new ConversationRunInput(settings, null, tools, skills, continuationAttachments),
                        runProgress, RegisterPendingAgentTool, async token =>
                        {
                            tools = _toolCatalog.GetFreshConversationTools().Where(tool => tool.Enabled).ToList();
                            var context = LoadContext(session);
                            skills = _skillCatalog.GetVisibleSkills().Where(skill => skill.Enabled).ToList();
                            SetToolCallReplay(session, pending.Command.ToolCallId, true);
                            var attachmentRouting = AttachmentModelRoutingService.Select(
                                settings,
                                session,
                                continuationAttachments);
                            settings = attachmentRouting.Settings;
                            if (attachmentRouting.HasMedia)
                            {
                                ReportProgress(runProgress, "routing", attachmentRouting.ProgressMessage);
                            }
                            var latestUserMessage = (session.Messages ?? new List<ChatMessage>())
                                .LastOrDefault(message => message != null && !message.ProtocolMessage &&
                                    string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase));
                            await _attachmentAnalysisService.EnsureAsync(
                                latestUserMessage == null ? string.Empty : latestUserMessage.Content,
                                session,
                                latestUserMessage,
                                attachmentRouting,
                                runProgress,
                                token).ConfigureAwait(false);
                            continuationAttachments = attachmentRouting.PrimaryAttachments ?? new ChatAttachment[0];
                            return new ConversationRunInput(settings, context, tools, skills, continuationAttachments);
                        }, runCancellation.Token).ConfigureAwait(false);

                    AnnotateRunMessages(session, firstRunMessageIndex, runId);
                    HtmlWorkspaceArtifactService.StampUncheckpointed(session, firstRunMessageIndex, session.ActiveHtmlArtifactId);
                    ChatResourceReferenceService.LinkMessageResources(session, 0);
                    if (completion == null || !completion.WaitingForConfirmation)
                    {
                        ApplyTerminalRunResult(session);
                    }
                    SaveSessionChanges(session);
                    RunCausalTrace.Summary(session);
                    _chatRuns.UpdateSessionSnapshot(sessionId, runId, session);
                }
                catch (Exception ex) when (!(ex is RunStoreException))
                {
                    _toolCatalog.InvalidateDocumentVbaTools();
                    if (!ChatHistoryEditService.HasResultForLatestToolCall(
                        session == null ? null : session.Messages,
                        pending.Command.ToolCallId))
                    {
                        SetToolCallReplay(session, pending.Command.ToolCallId, false);
                    }
                    var pendingMessage = session.Messages.LastOrDefault(message => message.Activity != null &&
                        message.Activity.ToolCallId == pending.Command.ToolCallId);
                    if (pendingMessage != null) CloseRunningActivity(pendingMessage.Activity, ex is OperationCanceledException);
                    CloseRunningActivities(session, firstRunMessageIndex, ex is OperationCanceledException);
                    if (session.LastRun.KernelState != null)
                        session.LastRun.KernelState = session.LastRun.KernelState.Interrupt(ex is OperationCanceledException, ex.Message, runId);
                    RecordFailedTurn(session, ex);
                    if (session.LastRun != null)
                    {
                        session.LastRun.Status = ex is OperationCanceledException ? "cancelled" : "failed";
                        session.LastRun.Phase = session.LastRun.Status;
                        session.LastRun.CurrentAction = ex.Message;
                    }
                    AnnotateRunMessages(session, firstRunMessageIndex, runId);
                    HtmlWorkspaceArtifactService.StampUncheckpointed(session, firstRunMessageIndex, session.ActiveHtmlArtifactId);
                    ChatResourceReferenceService.LinkMessageResources(session, 0);
                    SaveSessionChanges(session);
                    RunCausalTrace.Summary(session);
                    _chatRuns.UpdateSessionSnapshot(sessionId, runId, session);
                    throw;
                }

                runLease.Dispose();
                var response = ChatState(session);
                response.ExecutionSummary = session.LastRun == null || session.LastRun.ExecutionSummary == null
                    ? null : session.LastRun.ExecutionSummary.Clone();
                RunCausalTrace.Projected("ChatStateResponse");
                return response;
            }
            finally
            {
                if (causalTrace != null) causalTrace.Dispose();
                runLease.Dispose();
            }
        }

        private static IReadOnlyList<ChatAttachment> LatestUserAttachments(ChatSession session)
        {
            var message = session == null || session.Messages == null
                ? null
                : session.Messages.LastOrDefault(item =>
                    item != null && string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
            return message == null || message.Attachments == null
                ? (IReadOnlyList<ChatAttachment>)new ChatAttachment[0]
                : message.Attachments;
        }

        public ChatStateResponse CancelAgentTool(string pendingId, string chatId = null)
        {
            PendingAgentTool pending;
            var session = ResolvePendingAgentTool(pendingId, chatId, out pending);
            using (ReserveChatOperation(session))
            {
                session = ReloadReservedSession(session);
                pending = FindPendingAgentTool(session, pendingId);
                if (pending == null)
                {
                    throw new InvalidOperationException("Pending tool was not found or was already resolved.");
                }
                RemovePendingAgentTool(pendingId);
                var result = ToolResult.Cancelled("Tool cancelled by user.");
                result.PendingId = pending.PendingId;
                UpdatePendingActivity(session, pending.PendingId, pending.Command, result);
                var protocolStart = session.Messages.Count;
                var settings = _settingsService.Load();
                settings.ToolResultRole = PendingToolResultRole(session, pending.Command, settings.ToolResultRole);
                session.Messages.Add(AgentJsonProtocol.CreateToolResultMessage(
                    CloneCommand(pending.Command),
                    result,
                    settings.ToolResultRole));
                AnnotateRunMessages(session, protocolStart, "cancel_" + Guid.NewGuid().ToString("N"));
                // Explicit user cancellation is terminal for this run: persist it, but do not invoke the model.
                if (session.LastRun != null)
                {
                    if (session.LastRun.KernelState != null)
                        session.LastRun.KernelState = session.LastRun.KernelState.Interrupt(true, result.Message);
                    session.LastRun.Status = "cancelled";
                    session.LastRun.Phase = "cancelled";
                    session.LastRun.CurrentAction = result.Message;
                }
                SaveSessionChanges(session);
            }
            return ChatState(session);
        }

        private string RegisterPendingAgentTool(ChatSession session, ToolCommand command, ToolResult result)
        {
            var pendingId = Guid.NewGuid().ToString("N");
            var pending = new PendingAgentTool
            {
                PendingId = pendingId,
                SessionId = session.Id,
                Command = CloneCommand(command),
                Attachments = new List<ChatAttachment>(LatestUserAttachments(session)),
                IterationsUsed = session.LastRun == null ? 0 : session.LastRun.IterationsUsed,
                ToolStepsUsed = session.LastRun == null ? 0 : session.LastRun.ToolStepsUsed,
                CatalogFingerprint = result == null ? string.Empty : result.ConfirmationCatalogSha256
            };

            lock (_syncRoot)
            {
                _pendingAgentTools[pendingId] = pending;
            }

            if (result != null)
            {
                result.PendingId = pendingId;
            }

            return pendingId;
        }

        private ChatSession ResolvePendingAgentTool(string pendingId, string chatId, out PendingAgentTool pending)
        {
            if (string.IsNullOrWhiteSpace(pendingId))
            {
                throw new InvalidOperationException("Pending tool id is required.");
            }

            pending = TryGetPendingAgentTool(pendingId);
            if (pending != null)
            {
                EnsurePendingChatMatches(pending, chatId);
                return LoadSession(pending.SessionId);
            }

            var session = LoadSession(chatId);
            pending = FindPendingAgentTool(session, pendingId);
            if (pending == null)
            {
                throw new InvalidOperationException("Pending tool was not found or was already resolved.");
            }

            return session;
        }

        private PendingAgentTool TryGetPendingAgentTool(string pendingId)
        {
            lock (_syncRoot)
            {
                PendingAgentTool pending;
                return _pendingAgentTools.TryGetValue(pendingId ?? string.Empty, out pending) ? pending : null;
            }
        }

        private void RemovePendingAgentTool(string pendingId)
        {
            lock (_syncRoot)
            {
                _pendingAgentTools.Remove(pendingId ?? string.Empty);
            }
        }

        private void RemovePendingAgentToolsForSession(string sessionId)
        {
            lock (_syncRoot)
            {
                var ids = _pendingAgentTools
                    .Where(pair => pair.Value != null &&
                        string.Equals(pair.Value.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                    .Select(pair => pair.Key)
                    .ToList();
                foreach (var id in ids)
                {
                    _pendingAgentTools.Remove(id);
                }
            }
        }

        private static void CancelPendingActivities(ChatSession session, string reason)
        {
            foreach (var message in session == null || session.Messages == null
                ? new List<ChatMessage>()
                : session.Messages)
            {
                CancelPendingActivity(message == null ? null : message.Activity, reason);
            }
            ChatHistoryEditService.ExcludeUnmatchedToolCalls(session == null ? null : session.Messages);
        }

        private static void CancelPendingActivity(ChatActivity activity, string reason)
        {
            if (activity == null)
            {
                return;
            }
            if (!string.IsNullOrWhiteSpace(activity.PendingId))
            {
                activity.PendingId = null;
                activity.ConfirmationCatalogSha256 = null;
                activity.Status = "cancelled";
                activity.ExecutionStatus = "cancelled";
                activity.ResultMessage = reason ?? "Pending action cancelled.";
            }
            foreach (var child in activity.Children ?? new List<ChatActivity>())
            {
                CancelPendingActivity(child, reason);
            }
        }

        private static ToolCommand CloneCommand(ToolCommand command)
        {
            var clone = new ToolCommand
            {
                ToolId = command == null ? string.Empty : command.ToolId,
                Description = command == null ? string.Empty : command.Description,
                ToolCallId = command == null ? string.Empty : command.ToolCallId,
                RuntimeGuardJson = command == null ? null : command.RuntimeGuardJson,
                RuntimeStepId = command == null ? null : command.RuntimeStepId
            };

            if (command != null && command.Arguments != null)
            {
                foreach (var pair in command.Arguments)
                {
                    clone.Arguments[pair.Key] = pair.Value;
                }
            }

            return clone;
        }

        private static string PendingToolResultRole(ChatSession session, ToolCommand command, string fallback)
        {
            var callId = command == null ? string.Empty : command.ToolCallId ?? string.Empty;
            for (var index = session == null || session.Messages == null ? -1 : session.Messages.Count - 1;
                 index >= 0;
                 index--)
            {
                var message = session.Messages[index];
                if (message == null || !message.ProtocolMessage || string.IsNullOrWhiteSpace(message.ToolResultRole)) continue;
                if (string.Equals(message.ToolCallId, callId, StringComparison.Ordinal))
                {
                    return ToolResultRoles.Normalize(message.ToolResultRole);
                }
                if (message.ToolCalls != null && message.ToolCalls.Any(call =>
                    call != null && string.Equals(call.Id, callId, StringComparison.Ordinal)))
                {
                    return ToolResultRoles.Normalize(message.ToolResultRole);
                }
            }
            return ToolResultRoles.Normalize(fallback);
        }

        private static void EnsurePendingChatMatches(PendingAgentTool pending, string chatId)
        {
            if (pending == null || string.IsNullOrWhiteSpace(chatId))
            {
                return;
            }

            if (!string.Equals(pending.SessionId, chatId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Pending tool belongs to another chat session.");
            }
        }

        private static PendingAgentTool FindPendingAgentTool(ChatSession session, string pendingId)
        {
            if (session == null || session.Messages == null)
            {
                return null;
            }

            foreach (var message in session.Messages)
            {
                var activity = FindPendingActivity(message == null ? null : message.Activity, pendingId);
                if (activity == null)
                {
                    continue;
                }

                var command = CommandFromActivity(activity);
                if (command == null) return null;

                return new PendingAgentTool
                {
                    PendingId = pendingId,
                    SessionId = session.Id,
                    Command = command,
                    Attachments = UserAttachmentsForRun(session, message.RunId),
                    IterationsUsed = session.LastRun == null ? 0 : session.LastRun.IterationsUsed,
                    ToolStepsUsed = session.LastRun == null ? 0 : session.LastRun.ToolStepsUsed,
                    CatalogFingerprint = activity.ConfirmationCatalogSha256
                };
            }

            return null;
        }

        private static IReadOnlyList<ChatAttachment> UserAttachmentsForRun(ChatSession session, string runId)
        {
            if (string.IsNullOrWhiteSpace(runId))
            {
                return LatestUserAttachments(session);
            }
            var user = session.Messages.LastOrDefault(message =>
                message != null &&
                string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(message.RunId, runId, StringComparison.OrdinalIgnoreCase));
            return user == null || user.Attachments == null
                ? LatestUserAttachments(session)
                : user.Attachments;
        }

        private static ChatActivity FindPendingActivity(ChatActivity activity, string pendingId)
        {
            if (activity == null)
            {
                return null;
            }

            if (string.Equals(activity.PendingId, pendingId, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(activity.Status) ||
                 string.Equals(activity.Status, "waiting", StringComparison.OrdinalIgnoreCase)))
            {
                return activity;
            }

            foreach (var child in activity.Children ?? new List<ChatActivity>())
            {
                var match = FindPendingActivity(child, pendingId);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

        private static bool HasPendingAgentConfirmation(ChatSession session)
        {
            return (session == null ? new List<ChatMessage>() : session.Messages ?? new List<ChatMessage>())
                .Any(message => HasPendingAgentConfirmation(message == null ? null : message.Activity));
        }

        private static bool HasPendingAgentConfirmation(ChatActivity activity)
        {
            if (activity == null) return false;
            if (!string.IsNullOrWhiteSpace(activity.PendingId) &&
                (string.IsNullOrWhiteSpace(activity.Status) ||
                 string.Equals(activity.Status, "waiting", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
            return (activity.Children ?? new List<ChatActivity>()).Any(HasPendingAgentConfirmation);
        }

        private static bool MarkPendingActivityExecuting(ChatSession session, string pendingId, string runId)
        {
            if (session == null || session.Messages == null || string.IsNullOrWhiteSpace(pendingId))
            {
                return false;
            }

            foreach (var message in session.Messages)
            {
                var activity = FindPendingActivity(message == null ? null : message.Activity, pendingId);
                if (activity == null)
                {
                    continue;
                }

                activity.Status = "running";
                activity.ExecutionStatus = "executing";
                activity.ResultMessage = "Выполняю подтверждённое действие.";
                message.RunId = runId;
                message.Sequence = 0;
                AnnotateActivity(activity, runId, 0);
                return true;
            }

            return false;
        }

        private static ToolCommand CommandFromActivity(ChatActivity activity)
        {
            if (activity == null || string.IsNullOrWhiteSpace(activity.ToolId) ||
                string.IsNullOrWhiteSpace(activity.ToolCallId))
            {
                return null;
            }
            var command = new ToolCommand
            {
                ToolId = activity.ToolId,
                ToolCallId = activity.ToolCallId,
                Description = activity.Title,
                RuntimeGuardJson = activity.RuntimeGuardJson,
                RuntimeStepId = activity.StepId
            };

            if (string.IsNullOrWhiteSpace(activity.ArgumentsJson))
            {
                return command;
            }

            try
            {
                var args = JObject.Parse(activity.ArgumentsJson);
                ToolArgumentNormalizer.AddProperties(args, command.Arguments);
            }
            catch (JsonException)
            {
                return null;
            }

            return command;
        }

        private static void SetToolCallReplay(
            ChatSession session,
            string toolCallId,
            bool enabled,
            string runId = null)
        {
            if (session == null || session.Messages == null || string.IsNullOrWhiteSpace(toolCallId)) return;
            var call = session.Messages.LastOrDefault(message =>
                message != null && message.ProtocolMessage &&
                string.Equals(message.ToolCallId, toolCallId, StringComparison.Ordinal) &&
                !IsToolResultProtocolMessage(message));
            if (call != null)
            {
                call.ExcludeFromModelContext = !enabled;
                if (!string.IsNullOrWhiteSpace(runId)) call.RunId = runId;
            }
        }

        private static bool IsToolResultProtocolMessage(ChatMessage message)
        {
            return message != null &&
                (string.Equals(message.Role, ToolResultRoles.Tool, StringComparison.OrdinalIgnoreCase) ||
                 (message.Content ?? string.Empty).StartsWith("TOOL_RESULT:", StringComparison.Ordinal));
        }

        private static void UpdatePendingActivity(ChatSession session, string pendingId, ToolCommand command, ToolResult result)
        {
            if (session == null || session.Messages == null || string.IsNullOrWhiteSpace(pendingId))
            {
                return;
            }

            var replacement = AgentTranscript.CreateToolActivity(command, result, "tool");
            replacement.PendingId = null;
            foreach (var message in session.Messages)
            {
                if (ReplacePendingActivity(message == null ? null : message.Activity, pendingId, replacement))
                {
                    if (message != null)
                    {
                        message.Content = BuildResolvedToolContent(replacement);
                        message.HtmlWorkspaceCheckpoint = ChatResourceUri.ResolveArtifactRevision(session, session.ActiveHtmlArtifactId);
                    }
                    return;
                }
            }
        }

        private static bool ReplacePendingActivity(ChatActivity activity, string pendingId, ChatActivity replacement)
        {
            if (activity == null)
            {
                return false;
            }

            if (string.Equals(activity.PendingId, pendingId, StringComparison.OrdinalIgnoreCase))
            {
                CopyActivity(replacement, activity);
                return true;
            }

            foreach (var child in activity.Children ?? new List<ChatActivity>())
            {
                if (ReplacePendingActivity(child, pendingId, replacement))
                {
                    return true;
                }
            }

            return false;
        }

        private static void CopyActivity(ChatActivity source, ChatActivity target)
        {
            if (source == null || target == null)
            {
                return;
            }

            target.Kind = source.Kind;
            target.Title = source.Title;
            target.Subtitle = source.Subtitle;
            target.Status = source.Status;
            target.ExecutionStatus = source.ExecutionStatus;
            target.ErrorCode = source.ErrorCode;
            target.Retryable = source.Retryable;
            target.PendingId = source.PendingId;
            target.ConfirmationCatalogSha256 = source.ConfirmationCatalogSha256;
            target.ToolId = source.ToolId;
            target.ToolCallId = source.ToolCallId;
            target.ArgumentsJson = source.ArgumentsJson;
            target.RuntimeGuardJson = source.RuntimeGuardJson;
            target.ResultMessage = source.ResultMessage;
            target.DataJson = source.DataJson;
            target.Children = source.Children ?? new List<ChatActivity>();
        }

        private static string BuildResolvedToolContent(ChatActivity activity)
        {
            return "Agent step: " + (activity == null ? "Tool step" : activity.Title) + Environment.NewLine +
                "Tool: " + (activity == null ? string.Empty : activity.ToolId) + Environment.NewLine +
                "Status: " + (activity == null ? string.Empty : activity.Status) + Environment.NewLine +
                "Result: " + (activity == null ? string.Empty : activity.ResultMessage);
        }

        private sealed class PendingAgentTool
        {
            public string PendingId { get; set; }
            public string SessionId { get; set; }
            public ToolCommand Command { get; set; }
            public IReadOnlyList<ChatAttachment> Attachments { get; set; }
            public int IterationsUsed { get; set; }
            public int ToolStepsUsed { get; set; }
            public string CatalogFingerprint { get; set; }
        }
    }
}
