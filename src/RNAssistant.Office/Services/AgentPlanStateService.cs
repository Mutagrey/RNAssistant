using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal static class AgentPlanStateService
    {
        public static ChatActivity Restore(ChatSession session, AgentRunState state)
        {
            if (state == null)
            {
                return null;
            }

            var activity = FindLatestPlan(session, null);
            if (activity == null)
            {
                return null;
            }
            Restore(activity, state);
            return activity;
        }

        public static ChatActivity BeginCurrent(ChatSession session, AgentRunState state)
        {
            return SetCurrentStatus(session, state, "running");
        }

        public static ChatActivity ApplyResult(ChatSession session, AgentRunState state, ToolResult result, bool willRetry)
        {
            var status = result != null && result.Success
                ? "completed"
                : IsWaiting(result)
                    ? "waiting"
                    : willRetry
                        ? "running"
                        : string.Equals(result == null ? null : result.Status, "cancelled", StringComparison.OrdinalIgnoreCase)
                            ? "cancelled"
                            : "failed";
            return SetCurrentStatus(session, state, status);
        }

        public static ChatActivity MarkLatestCurrent(ChatSession session, string status)
        {
            var state = new AgentRunState();
            return Restore(session, state) == null ? null : SetCurrentStatus(session, state, status);
        }

        public static ChatActivity MarkCurrentForRun(ChatSession session, string runId, string status)
        {
            var activity = FindLatestPlan(session, runId);
            if (activity == null)
            {
                return null;
            }
            var state = new AgentRunState();
            Restore(activity, state);
            return SetCurrentStatus(session, state, status);
        }

        public static ChatActivity ApplyLatestResult(ChatSession session, ToolResult result, bool willRetry)
        {
            var state = new AgentRunState();
            return Restore(session, state) == null ? null : ApplyResult(session, state, result, willRetry);
        }

        public static string ProgressText(ChatActivity plan)
        {
            var steps = plan == null ? new List<ChatActivity>() : plan.Children ?? new List<ChatActivity>();
            var completed = steps.Count(item => item != null && string.Equals(item.Status, "completed", StringComparison.OrdinalIgnoreCase));
            var current = steps.FirstOrDefault(item => item != null &&
                (string.Equals(item.Status, "running", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(item.Status, "waiting", StringComparison.OrdinalIgnoreCase)))
                ?? steps.FirstOrDefault(item => item != null && string.Equals(item.Status, "pending", StringComparison.OrdinalIgnoreCase));
            return current == null
                ? "План: " + completed + "/" + steps.Count + " выполнено."
                : "План: " + completed + "/" + steps.Count + " · " + current.Title;
        }

        public static ChatActivity Snapshot(ChatActivity plan)
        {
            return plan == null
                ? null
                : JsonConvert.DeserializeObject<ChatActivity>(JsonConvert.SerializeObject(plan));
        }

        private static ChatActivity SetCurrentStatus(ChatSession session, AgentRunState state, string status)
        {
            if (state == null)
            {
                return null;
            }
            if (state.PlanActivity == null)
            {
                Restore(session, state);
            }
            if (state.PlanActivity == null || state.Plan == null || state.Plan.Count == 0)
            {
                return null;
            }

            var step = state.Plan.FirstOrDefault(item => item != null &&
                (string.Equals(item.Status, "running", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(item.Status, "waiting", StringComparison.OrdinalIgnoreCase)))
                ?? state.Plan.FirstOrDefault(item => item != null && string.Equals(item.Status, "pending", StringComparison.OrdinalIgnoreCase));
            if (step == null)
            {
                return state.PlanActivity;
            }

            step.Status = NormalizeStepStatus(status);
            var activityStep = (state.PlanActivity.Children ?? new List<ChatActivity>()).FirstOrDefault(item =>
                item != null && string.Equals(item.Subtitle, step.Id, StringComparison.OrdinalIgnoreCase));
            if (activityStep != null)
            {
                activityStep.Status = step.Status;
            }

            UpdatePlanActivity(state);
            return state.PlanActivity;
        }

        private static void UpdatePlanActivity(AgentRunState state)
        {
            var statuses = (state.Plan ?? new List<AgentPlanStep>())
                .Where(item => item != null)
                .Select(item => NormalizeStepStatus(item.Status))
                .ToList();
            state.PlanActivity.Status = statuses.Any(status => string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                ? "failed"
                : statuses.Any(status => string.Equals(status, "waiting", StringComparison.OrdinalIgnoreCase))
                    ? "waiting"
                    : statuses.Any(status => string.Equals(status, "running", StringComparison.OrdinalIgnoreCase))
                        ? "running"
                        : statuses.Count > 0 && statuses.All(status => string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                            ? "completed"
                            : statuses.Any(status => string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase))
                                ? "cancelled"
                                : "planned";
            state.PlanActivity.DataJson = JsonConvert.SerializeObject(new
            {
                protocolVersion = AgentDecisionProtocol.Version,
                goal = state.WorkingGoal,
                plan = state.Plan
            });
        }

        private static ChatActivity FindLatestPlan(ChatSession session, string runId)
        {
            return session == null || session.Messages == null
                ? null
                : session.Messages.Select(message => message == null ? null : message.Activity)
                    .LastOrDefault(activity => activity != null &&
                        string.Equals(activity.Kind, "plan", StringComparison.OrdinalIgnoreCase) &&
                        (string.IsNullOrWhiteSpace(runId) || string.Equals(activity.RunId, runId, StringComparison.OrdinalIgnoreCase)));
        }

        private static void Restore(ChatActivity activity, AgentRunState state)
        {
            state.PlanDeclared = true;
            state.WorkingGoal = activity.Title;
            state.PlanActivity = activity;
            state.Plan = (activity.Children ?? new List<ChatActivity>())
                .Where(item => item != null && string.Equals(item.Kind, "plan_step", StringComparison.OrdinalIgnoreCase))
                .Select(item => new AgentPlanStep
                {
                    Id = item.Subtitle,
                    Title = item.Title,
                    Status = NormalizeStepStatus(item.Status)
                })
                .ToList();
        }

        private static bool IsWaiting(ToolResult result)
        {
            return result != null &&
                (string.Equals(result.Status, "waiting_confirmation", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(result.Status, "skipped_auto_run", StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeStepStatus(string status)
        {
            if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)) return "completed";
            if (string.Equals(status, "running", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "in_progress", StringComparison.OrdinalIgnoreCase)) return "running";
            if (string.Equals(status, "waiting", StringComparison.OrdinalIgnoreCase)) return "waiting";
            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)) return "failed";
            if (string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase)) return "cancelled";
            return "pending";
        }
    }
}
