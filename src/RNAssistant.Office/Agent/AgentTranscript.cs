using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;

namespace RNAssistant.Office
{
    internal static class AgentTranscript
    {
        private const int MaxTranscriptReasoningChars = 24000;
        private const int MaxTranscriptArgumentsChars = 64000;
        private const int MaxTranscriptDataChars = 128000;
        private const int MaxRenderableTranscriptDataChars = ChatArtifactLimits.MaximumTextCharacters;
        private const int MaxTranscriptMessageChars = 16000;

        internal static ChatMessage CreateRunningToolMessage(ChatSession session, ToolInvocation command,
            string stepId, string stepMessage)
        {
            return new ChatMessage
            {
                Role = "assistant",
                Content = string.Empty,
                ExcludeFromModelContext = true,
                HtmlWorkspaceCheckpoint = ChatResourceUri.ResolveArtifactRevision(session, session.ActiveHtmlArtifactId),
                Activity = CreateRunningToolActivity(command, stepId, stepMessage)
            };
        }

        internal static void CompleteToolActivityMessage(ChatSession session, ChatMessage activityMessage,
            ToolInvocation command, ToolRunResult result, string stepId, string stepMessage)
        {
            var completed = CreateLocalResultMessage(command, result, stepId, stepMessage);
            activityMessage.Content = completed.Content;
            activityMessage.Activity = completed.Activity;
            activityMessage.ResourceRefs = CloneResourceRefs(result.ModelResourceRefs);
            LinkChartArtifactsToActivity(session, activityMessage);
            activityMessage.HtmlWorkspaceCheckpoint = ChatResourceUri.ResolveArtifactRevision(session, session.ActiveHtmlArtifactId);
        }

        private static void LinkChartArtifactsToActivity(ChatSession session, ChatMessage activityMessage)
        {
            if (session == null || activityMessage == null) return;
            var referencedIds = new HashSet<string>(
                ChatResourceUri.CurrentArtifactIds(session, activityMessage.ResourceRefs),
                StringComparer.OrdinalIgnoreCase);
            foreach (var artifact in (session.Artifacts ?? new List<ChatArtifact>()).Where(item => item != null &&
                referencedIds.Contains(item.Id) &&
                string.Equals(item.Kind, ChatArtifactKinds.Chart, StringComparison.OrdinalIgnoreCase)))
            {
                if (string.IsNullOrWhiteSpace(artifact.SourceMessageId)) artifact.SourceMessageId = activityMessage.Id;
                if (string.IsNullOrWhiteSpace(artifact.RunId)) artifact.RunId = activityMessage.RunId;
            }
        }

        internal static List<ResourceRef> CloneResourceRefs(IEnumerable<ResourceRef> references)
        {
            return (references ?? new ResourceRef[0])
                .Where(reference => reference != null && !string.IsNullOrWhiteSpace(reference.Uri))
                .GroupBy(reference => reference.Uri + "\n" + (reference.Revision ?? string.Empty), StringComparer.Ordinal)
                .Select(group => new ResourceRef(group.First().Uri, group.First().Revision))
                .ToList();
        }

        public static ChatMessage CreateLocalResultMessage(
            ToolInvocation command,
            ToolRunResult result,
            string stepId = null,
            string stepMessage = null)
        {
            var activity = CreateToolActivity(command, result, "tool");
            activity.StepId = stepId;
            activity.StepMessage = stepMessage;
            return new ChatMessage
            {
                Role = "assistant",
                Content = CreateToolFallbackContent(activity),
                ExcludeFromModelContext = true,
                Activity = activity
            };
        }

        public static ChatActivity CreateRunningToolActivity(
            ToolInvocation command,
            string stepId,
            string stepMessage)
        {
            var activity = CreateToolActivity(command,
                ToolRunResult.Running(), "tool");
            activity.StepId = stepId;
            activity.StepMessage = stepMessage;
            activity.Status = "running";
            activity.ExecutionStatus = "executing";
            activity.ResultMessage = null;
            return activity;
        }

        public static ChatMessage CreateAssistantMessage(
            string content,
            LlmCompletionResult completion,
            ChatActivity activity = null,
            string responseStatus = null)
        {
            var reasoning = completion == null ? null : completion.ReasoningContent;
            var transcriptReasoningTruncated = !string.IsNullOrEmpty(reasoning) && reasoning.Length > MaxTranscriptReasoningChars;
            return new ChatMessage
            {
                Role = "assistant",
                Content = content ?? string.Empty,
                ExcludeFromModelContext = activity != null,
                ResponseProtocolVersion = AgentResponseStatuses.IsKnown(responseStatus)
                    ? AgentResponseProtocol.CurrentVersion
                    : 0,
                ResponseStatus = AgentResponseStatuses.IsKnown(responseStatus) ? responseStatus : null,
                Activity = activity,
                PromptTokens = completion == null ? null : completion.PromptTokens,
                CompletionTokens = completion == null ? null : completion.CompletionTokens,
                TotalTokens = completion == null ? null : completion.TotalTokens,
                UsageJson = completion == null ? null : completion.UsageJson,
                ReasoningContent = transcriptReasoningTruncated
                    ? reasoning.Substring(0, MaxTranscriptReasoningChars)
                    : reasoning,
                ReasoningTokens = completion == null ? null : completion.ReasoningTokens,
                ReasoningTruncated = completion != null && (completion.ReasoningTruncated || transcriptReasoningTruncated)
            };
        }

        public static object DescribeResult(ToolInvocation command, ToolRunResult result)
        {
            return new
            {
                toolId = command == null ? string.Empty : command.ToolId,
                description = command == null ? string.Empty : command.Description,
                success = result != null && result.Success,
                status = result == null ? string.Empty : result.Status,
                errorCode = result == null ? string.Empty : result.ErrorCode,
                retryable = result == null ? null : result.Retryable,
                pendingId = result == null ? string.Empty : result.PendingId,
                message = result == null ? string.Empty : BoundText(result.Message, MaxTranscriptMessageChars),
                dataJson = result == null ? null : BoundJson(result.DataJson, MaxTranscriptDataChars, false, true)
            };
        }

        public static ChatActivity CreateToolActivity(ToolInvocation command, ToolRunResult result, string kind)
        {
            var success = result != null && result.Success;
            var message = result == null ? string.Empty : BoundText(result.Message, MaxTranscriptMessageChars);
            var executionStatus = NormalizeExecutionStatus(result);
            var title = command == null
                ? "Tool step"
                : !string.IsNullOrWhiteSpace(command.Description)
                    ? command.Description
                    : command.ToolId;

            var rawDataJson = result == null ? null : result.DataJson;
            var activity = new ChatActivity
            {
                Kind = string.IsNullOrWhiteSpace(kind) ? "tool" : kind,
                Title = title,
                Subtitle = command == null ? string.Empty : command.ToolId,
                Status = ToActivityStatus(result),
                ExecutionStatus = executionStatus,
                ErrorCode = result == null ? null : result.ErrorCode,
                Retryable = result == null ? null : result.Retryable,
                PendingId = result == null ? null : result.PendingId,
                ConfirmationCatalogSha256 = result == null ? null : result.CatalogRevision,
                ToolId = command == null ? string.Empty : command.ToolId,
                ToolCallId = command == null ? string.Empty : command.ToolCallId,
                ArgumentsJson = command == null
                    ? null
                    : BoundJson(
                        JsonConvert.SerializeObject(command.Arguments, Formatting.Indented),
                        MaxTranscriptArgumentsChars,
                        result != null && IsAwaitingConfirmation(result),
                        false),
                RuntimeGuardJson = command == null ? null : command.RuntimeGuardJson,
                ResultMessage = message,
                DataJson = BoundActivityData(result, rawDataJson)
            };

            return activity;
        }

        private static string BoundActivityData(ToolRunResult result, string dataJson)
        {
            var reference = result == null ? null : result.ModelResultResourceRef;
            if (reference == null || string.IsNullOrWhiteSpace(reference.Uri))
            {
                return BoundJson(dataJson, MaxTranscriptDataChars, false, true);
            }
            return JsonConvert.SerializeObject(new
            {
                externalized = true,
                originalCharacters = (dataJson ?? string.Empty).Length,
                resource = new
                {
                    uri = reference.Uri,
                    revision = reference.Revision,
                    kind = result.ModelResultResourceKind
                }
            });
        }

        private static string BoundJson(
            string json,
            int maxCharacters,
            bool preserveFull,
            bool preserveRenderableArtifact)
        {
            if (preserveFull || string.IsNullOrEmpty(json) || json.Length <= maxCharacters ||
                preserveRenderableArtifact && json.Length <= MaxRenderableTranscriptDataChars && IsRenderableArtifact(json))
            {
                return json;
            }
            return JsonConvert.SerializeObject(new
            {
                truncated = true,
                originalCharacters = json.Length,
                preview = json.Substring(0, Math.Min(4096, json.Length))
            });
        }

        private static bool IsRenderableArtifact(string json)
        {
            try
            {
                var root = JObject.Parse(json ?? string.Empty);
                var type = (string)root["type"] ?? (string)root["Type"] ?? string.Empty;
                return string.Equals(type, "rnassistant.chart", StringComparison.OrdinalIgnoreCase);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string BoundText(string value, int maxCharacters)
        {
            value = value ?? string.Empty;
            return value.Length <= maxCharacters
                ? value
                : value.Substring(0, maxCharacters) + "\n...[truncated]";
        }

        public static bool IsAwaitingConfirmation(ToolRunResult result)
        {
            var status = NormalizeExecutionStatus(result);
            return string.Equals(status, "awaiting_confirmation",
                StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsAwaitingUser(ToolRunResult result)
        {
            return string.Equals(NormalizeExecutionStatus(result), "awaiting_user", StringComparison.OrdinalIgnoreCase);
        }

        private static string ToActivityStatus(ToolRunResult result)
        {
            if (IsAwaitingUser(result))
            {
                return "waiting";
            }
            if (result != null && result.Success)
            {
                return "completed";
            }

            var status = NormalizeExecutionStatus(result);
            if (string.Equals(status, "awaiting_confirmation",
                StringComparison.OrdinalIgnoreCase))
            {
                return "waiting";
            }
            if (string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return "cancelled";
            }

            return "failed";
        }

        private static string NormalizeExecutionStatus(ToolRunResult result)
        {
            if (result == null)
            {
                return "failed";
            }

            if (!string.IsNullOrWhiteSpace(result.Status))
            {
                return result.Status;
            }

            return result.Success ? "completed" : "failed";
        }

        private static string CreateToolFallbackContent(ChatActivity activity)
        {
            var builder = new StringBuilder();
            builder.Append("Agent step: ");
            builder.AppendLine(string.IsNullOrWhiteSpace(activity == null ? null : activity.Title) ? "Tool step" : activity.Title);
            if (!string.IsNullOrWhiteSpace(activity == null ? null : activity.ToolId))
            {
                builder.AppendLine("Tool: " + activity.ToolId);
            }
            builder.AppendLine("Status: " + (activity == null ? "completed" : activity.Status));
            return builder.ToString();
        }

    }
}
