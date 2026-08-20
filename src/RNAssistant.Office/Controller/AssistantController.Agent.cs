using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
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
                EnsureCurrentDocument(session);
                if (!MarkPendingActivityExecuting(session, pending.PendingId))
                {
                    throw new InvalidOperationException("Pending tool was not found or was already resolved.");
                }
                RemovePendingAgentTool(pendingId);

                session.LastRun = new ChatRunRecord
                {
                    RunId = runId,
                    RuntimeId = RuntimeId,
                    Status = "running",
                    Phase = "executing",
                    CurrentAction = "Executing confirmed tool.",
                    StartedUtc = DateTime.UtcNow
                };
                SaveSessionChanges(session);

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
                    if (progress != null)
                    {
                        progress(phase, message, activity);
                    }
                };

                var firstRunMessageIndex = session.Messages == null ? 0 : session.Messages.Count;
                var settings = _settingsService.Load();
                var tools = _toolCatalog.GetVisibleTools().Where(tool => tool.Enabled).ToList();
                var pendingResolved = false;
                try
                {
                    ReportProgress(runProgress, "executing", "Executing confirmed tool...");
                    var result = _toolExecutor.Execute(
                        CloneCommand(pending.Command),
                        tools,
                        settings,
                        false,
                        true,
                        session,
                        runCancellation.Token);
                    UpdatePendingActivity(session, pending.PendingId, pending.Command, result);
                    pendingResolved = true;
                    if (result.Success)
                    {
                        tools = _toolCatalog.GetVisibleTools().Where(tool => tool.Enabled).ToList();
                        var context = LoadContext(session);
                        var skills = _skillCatalog.GetVisibleSkills().Where(skill => skill.Enabled).ToList();
                        await _agentRunService.ContinueAfterToolAsync(
                            CloneCommand(pending.Command),
                            result,
                            session,
                            context,
                            settings,
                            tools,
                            pending.Attachments ?? LatestUserAttachments(session),
                            runProgress,
                            RegisterPendingAgentTool,
                            skills,
                            runCancellation.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        session.Messages.Add(AgentJsonProtocol.CreateToolResultMessage(CloneCommand(pending.Command), result));
                    }

                    AnnotateRunMessages(session, firstRunMessageIndex, runId);
                    HtmlWorkspaceArtifactService.StampUncheckpointed(session, firstRunMessageIndex, session.ActiveHtmlArtifactId);
                    ChatArtifactService.LinkMessageArtifacts(session, 0);
                    session.LastRun = null;
                    SaveSessionChanges(session);
                }
                catch (Exception ex)
                {
                    if (!pendingResolved)
                    {
                        var failedResult = ex is OperationCanceledException
                            ? ToolResult.Cancelled("Confirmed tool execution was cancelled.")
                            : ToolResult.Fail(ex.Message, null, "confirmed_tool_failed", false);
                        UpdatePendingActivity(session, pending.PendingId, pending.Command, failedResult);
                    }
                    RecordFailedTurn(session, ex);
                    if (session.LastRun != null)
                    {
                        session.LastRun.Status = ex is OperationCanceledException ? "cancelled" : "failed";
                        session.LastRun.Phase = session.LastRun.Status;
                        session.LastRun.CurrentAction = ex.Message;
                    }
                    AnnotateRunMessages(session, firstRunMessageIndex, runId);
                    HtmlWorkspaceArtifactService.StampUncheckpointed(session, firstRunMessageIndex, session.ActiveHtmlArtifactId);
                    ChatArtifactService.LinkMessageArtifacts(session, 0);
                    SaveSessionChanges(session);
                    throw;
                }

                runLease.Dispose();
                return ChatState(session);
            }
            finally
            {
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
                RemovePendingAgentTool(pendingId);
                var result = ToolResult.Cancelled("Tool cancelled by user.");
                result.PendingId = pending.PendingId;
                UpdatePendingActivity(session, pending.PendingId, pending.Command, result);
                var protocolStart = session.Messages.Count;
                session.Messages.Add(AgentJsonProtocol.CreateToolResultMessage(CloneCommand(pending.Command), result));
                AnnotateRunMessages(session, protocolStart, "cancel_" + Guid.NewGuid().ToString("N"));
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
                Attachments = new List<ChatAttachment>(LatestUserAttachments(session))
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
                ToolCallId = command == null ? string.Empty : command.ToolCallId
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

                return new PendingAgentTool
                {
                    PendingId = pendingId,
                    SessionId = session.Id,
                    Command = CommandFromActivity(activity),
                    Attachments = UserAttachmentsForRun(session, message.RunId)
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
                ? (IReadOnlyList<ChatAttachment>)new ChatAttachment[0]
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

        private static bool MarkPendingActivityExecuting(ChatSession session, string pendingId)
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
                activity.ResultMessage = "Executing confirmed tool.";
                return true;
            }

            return false;
        }

        private static ToolCommand CommandFromActivity(ChatActivity activity)
        {
            var command = new ToolCommand
            {
                ToolId = activity == null ? string.Empty : activity.ToolId,
                ToolCallId = activity == null ? string.Empty : activity.ToolCallId,
                Description = activity == null ? string.Empty : activity.Title
            };

            if (activity == null || string.IsNullOrWhiteSpace(activity.ArgumentsJson))
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
            }

            return command;
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
                        message.HtmlWorkspaceCheckpointId = session.ActiveHtmlArtifactId;
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
            target.ToolId = source.ToolId;
            target.ToolCallId = source.ToolCallId;
            target.ArgumentsJson = source.ArgumentsJson;
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
        }
    }
}
