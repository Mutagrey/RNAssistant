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

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        public ChatStateResponse ConfirmAgentTool(string pendingId, string chatId = null)
        {
            return ConfirmAgentToolAsync(pendingId, chatId, null, CancellationToken.None).GetAwaiter().GetResult();
        }

        public async Task<ChatStateResponse> ConfirmAgentToolAsync(
            string pendingId,
            string chatId = null,
            Action<string, string, ChatActivity> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            PendingAgentTool pending;
            var session = ResolvePendingAgentTool(pendingId, chatId, out pending);
            RemovePendingAgentTool(pendingId);
            var settings = _settingsService.Load();
            var tools = _toolCatalog.GetVisibleTools().Where(s => s.Enabled).ToList();
            cancellationToken.ThrowIfCancellationRequested();
            var result = _toolExecutor.Execute(CloneCommand(pending.Command), tools, settings, false, true, session, cancellationToken);
            UpdatePendingActivity(session, pending.PendingId, pending.Command, result);
            if (result.Success && settings.AutoContinueAfterConfirmation != false)
            {
                var context = LoadContext(session);
                var skills = _skillCatalog.SelectRelevantSkills("continue confirmed agent task", context, 5);
                await _chatCompletionService.ContinueAfterToolAsync(
                    CloneCommand(pending.Command),
                    session,
                    context,
                    settings,
                    tools,
                    progress,
                    RegisterPendingAgentTool,
                    skills,
                    cancellationToken).ConfigureAwait(false);
            }
            _chatStore.Save(session);
            return ChatState(session);
        }

        public ChatStateResponse CancelAgentTool(string pendingId, string chatId = null)
        {
            PendingAgentTool pending;
            var session = ResolvePendingAgentTool(pendingId, chatId, out pending);
            RemovePendingAgentTool(pendingId);
            var result = ToolResult.Cancelled("Tool cancelled by user.");
            result.PendingId = pending.PendingId;
            UpdatePendingActivity(session, pending.PendingId, pending.Command, result);
            _chatStore.Save(session);
            return ChatState(session);
        }

        private string RegisterPendingAgentTool(ChatSession session, ToolCommand command, ToolResult result)
        {
            var pendingId = Guid.NewGuid().ToString("N");
            var pending = new PendingAgentTool
            {
                PendingId = pendingId,
                SessionId = ChatStore.GetSessionId(session),
                Command = CloneCommand(command)
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

        private static ToolCommand CloneCommand(ToolCommand command)
        {
            var clone = new ToolCommand
            {
                ToolId = command == null ? string.Empty : command.ToolId,
                Description = command == null ? string.Empty : command.Description
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
                    SessionId = ChatStore.GetSessionId(session),
                    Command = CommandFromActivity(activity)
                };
            }

            return null;
        }

        private static ChatActivity FindPendingActivity(ChatActivity activity, string pendingId)
        {
            if (activity == null)
            {
                return null;
            }

            if (string.Equals(activity.PendingId, pendingId, StringComparison.OrdinalIgnoreCase))
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

        private static ToolCommand CommandFromActivity(ChatActivity activity)
        {
            var command = new ToolCommand
            {
                ToolId = activity == null ? string.Empty : activity.ToolId,
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
            target.PendingId = source.PendingId;
            target.ToolId = source.ToolId;
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
        }
    }
}
