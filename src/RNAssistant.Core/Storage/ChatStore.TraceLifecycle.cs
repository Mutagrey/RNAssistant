using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    public sealed partial class ChatStore
    {
        private static string CurrentRunId(ChatSession session)
        {
            return session == null || session.LastRun == null ? null : session.LastRun.RunId;
        }

        private static string CurrentTurnId(ChatSession session)
        {
            return session == null || session.LastRun == null
                ? null
                : RunTurnId(session.LastRun);
        }

        private static void AddTurnLifecycleEvents(
            ICollection<PendingSessionEvent> pending,
            ChatRunRecord before,
            ChatRunRecord after)
        {
            var beforeTurnId = RunTurnId(before);
            var afterTurnId = RunTurnId(after);
            if (string.IsNullOrWhiteSpace(beforeTurnId) && string.IsNullOrWhiteSpace(afterTurnId)) return;

            if (string.IsNullOrWhiteSpace(beforeTurnId))
            {
                pending.Add(TurnStarted(after));
                if (IsTerminalRunStatus(after == null ? null : after.Status))
                {
                    pending.Add(TurnEnded(after, after.Status));
                }
                return;
            }

            if (string.IsNullOrWhiteSpace(afterTurnId))
            {
                if (IsTerminalRunStatus(before == null ? null : before.Status)) return;
                var status = string.Equals(before == null ? null : before.Status, "waiting_confirmation", StringComparison.OrdinalIgnoreCase)
                    ? "cancelled"
                    : "completed";
                pending.Add(TurnEnded(before, status));
                return;
            }

            if (!string.Equals(beforeTurnId, afterTurnId, StringComparison.OrdinalIgnoreCase))
            {
                if (!IsTerminalRunStatus(before == null ? null : before.Status))
                {
                    pending.Add(TurnEnded(before, "superseded"));
                }
                pending.Add(TurnStarted(after));
                if (IsTerminalRunStatus(after == null ? null : after.Status))
                {
                    pending.Add(TurnEnded(after, after.Status));
                }
                return;
            }

            if (!IsTerminalRunStatus(before == null ? null : before.Status) &&
                IsTerminalRunStatus(after == null ? null : after.Status))
            {
                pending.Add(TurnEnded(after, after.Status));
            }
        }

        private static PendingSessionEvent TurnStarted(ChatRunRecord run)
        {
            var turnId = RunTurnId(run);
            return PendingEvent(SessionEventTypes.TurnStarted,
                BuildTurnLifecycleData(run, "running"), null,
                run == null ? null : run.RunId, turnId, null);
        }

        private static PendingSessionEvent TurnEnded(ChatRunRecord run, string status)
        {
            var turnId = RunTurnId(run);
            return PendingEvent(SessionEventTypes.TurnEnded,
                BuildTurnLifecycleData(run, string.IsNullOrWhiteSpace(status) ? "completed" : status), null,
                run == null ? null : run.RunId, turnId, null);
        }

        private static JObject BuildTurnLifecycleData(ChatRunRecord run, string status)
        {
            return new JObject
            {
                ["RunId"] = run == null || string.IsNullOrWhiteSpace(run.RunId) ? JValue.CreateNull() : new JValue(run.RunId),
                ["TurnId"] = string.IsNullOrWhiteSpace(RunTurnId(run)) ? JValue.CreateNull() : new JValue(RunTurnId(run)),
                ["Status"] = status ?? string.Empty,
                ["ResponseProtocolVersion"] = run == null ? 0 : run.ResponseProtocolVersion,
                ["Phase"] = run == null || string.IsNullOrWhiteSpace(run.Phase) ? JValue.CreateNull() : new JValue(run.Phase),
                ["StartedUtc"] = run == null || run.StartedUtc == default(DateTime)
                    ? JValue.CreateNull()
                    : new JValue(run.StartedUtc.ToUniversalTime())
            };
        }

        private static PendingSessionEvent PendingEvent(
            string type,
            JToken data,
            ChatBlobReference payload,
            string runId,
            string turnId,
            string stepId)
        {
            return new PendingSessionEvent
            {
                EventId = Guid.NewGuid().ToString("N"),
                CreatedUtc = DateTime.UtcNow,
                Type = type,
                Data = data,
                Payload = payload,
                RunId = runId,
                TurnId = turnId,
                StepId = stepId
            };
        }

        private static SessionEvent LastEvent(EventLogReadResult log)
        {
            return log == null || log.Events.Count == 0
                ? null
                : log.Events[log.Events.Count - 1];
        }

        private static bool IsTerminalRunStatus(string status)
        {
            return string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "awaiting_user", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "blocked", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "refused", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "planned", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "interrupted", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "superseded", StringComparison.OrdinalIgnoreCase);
        }

        private static string RunTurnId(ChatRunRecord run)
        {
            if (run == null) return null;
            return string.IsNullOrWhiteSpace(run.TurnId) ? run.RunId : run.TurnId;
        }

        private static string ResolveStepId(string stepId, JToken data)
        {
            if (!string.IsNullOrWhiteSpace(stepId)) return stepId;
            var source = data as JObject;
            return source == null ? null : (string)(source["RequestId"] ?? source["requestId"]);
        }

        private static JObject BuildStepLifecycleData(
            JToken data,
            string status,
            bool synthetic,
            string sourceEventId)
        {
            var source = data as JObject;
            return new JObject
            {
                ["RequestId"] = JsonString(source, "RequestId", "requestId"),
                ["Purpose"] = JsonString(source, "Purpose", "purpose"),
                ["Model"] = JsonString(source, "Model", "model"),
                ["ResponseFormat"] = JsonString(source, "ResponseFormat", "responseFormat"),
                ["Status"] = status ?? string.Empty,
                ["Synthetic"] = synthetic,
                ["FailureKind"] = JsonString(source, "FailureKind", "failureKind"),
                ["Error"] = JsonString(source, "Error", "error"),
                ["SourceEventId"] = string.IsNullOrWhiteSpace(sourceEventId)
                    ? JValue.CreateNull()
                    : new JValue(sourceEventId)
            };
        }

        private static string StepTerminalStatus(string eventType, JToken data)
        {
            if (string.Equals(eventType, SessionEventTypes.LlmResponse, StringComparison.Ordinal)) return "completed";
            var source = data as JObject;
            var failureKind = source == null ? null : (string)(source["FailureKind"] ?? source["failureKind"]);
            return !string.IsNullOrWhiteSpace(failureKind) &&
                (failureKind.IndexOf("cancel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 failureKind.IndexOf("OperationCanceled", StringComparison.OrdinalIgnoreCase) >= 0)
                    ? "cancelled"
                    : "failed";
        }

        private static JToken JsonString(JObject source, string primary, string alternate)
        {
            if (source == null) return JValue.CreateNull();
            var value = source[primary] ?? source[alternate];
            return value == null || value.Type == JTokenType.Null
                ? JValue.CreateNull()
                : new JValue((string)value);
        }

        private static List<string> OpenStepIds(IEnumerable<SessionEvent> events, string runId)
        {
            var open = new List<string>();
            foreach (var sessionEvent in events ?? new List<SessionEvent>())
            {
                if (sessionEvent == null ||
                    !string.Equals(sessionEvent.RunId, runId, StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(sessionEvent.StepId)) continue;
                if (string.Equals(sessionEvent.Type, SessionEventTypes.StepStarted, StringComparison.Ordinal))
                {
                    if (!open.Contains(sessionEvent.StepId, StringComparer.OrdinalIgnoreCase)) open.Add(sessionEvent.StepId);
                }
                else if (string.Equals(sessionEvent.Type, SessionEventTypes.StepEnded, StringComparison.Ordinal))
                {
                    open.RemoveAll(value => string.Equals(value, sessionEvent.StepId, StringComparison.OrdinalIgnoreCase));
                }
            }
            return open;
        }

        private static string TurnIdForRun(IEnumerable<SessionEvent> events, string runId)
        {
            return (events ?? new List<SessionEvent>())
                .Where(item => item != null &&
                    string.Equals(item.RunId, runId, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(item.TurnId))
                .Select(item => item.TurnId)
                .LastOrDefault() ?? runId;
        }
    }
}
