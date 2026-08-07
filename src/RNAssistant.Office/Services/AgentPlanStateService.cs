using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal static class AgentPlanStateService
    {
        public static ChatActivity ApplyDecision(
            ChatSession session,
            AgentRunState state,
            AgentPlannerResponse response,
            out bool updatedExisting)
        {
            updatedExisting = false;
            if (state == null || response == null)
            {
                return null;
            }

            // A fresh user turn must not inherit the latest completed plan from
            // the transcript. Real continuations restore their state explicitly.
            if (state.PlanActivity == null && state.PlanDeclared)
            {
                Restore(session, state);
            }
            if (state.PlanActivity == null)
            {
                state.PlanDeclared = true;
                state.WorkingGoal = FirstNonEmpty(response.Goal, response.DecisionSummary, "Рабочий план");
                state.Plan = CloneSteps(response.Plan);
                state.PlanActivity = new ChatActivity
                {
                    Kind = "plan",
                    Title = state.WorkingGoal,
                    Subtitle = response.DecisionSummary,
                    Status = "planned"
                };
            }
            else
            {
                updatedExisting = true;
                state.PlanDeclared = true;
                state.WorkingGoal = FirstNonEmpty(response.Goal, state.WorkingGoal, state.PlanActivity.Title, response.DecisionSummary, "Рабочий план");
                state.Plan = Reconcile(state.Plan, response.Plan);
                state.PlanActivity.Title = state.WorkingGoal;
                state.PlanActivity.Subtitle = FirstNonEmpty(response.DecisionSummary, state.PlanActivity.Subtitle);
            }

            SyncActivitySteps(state);
            UpdatePlanActivity(state);
            return state.PlanActivity;
        }

        public static ChatActivity CreateUpdateActivity(AgentPlannerResponse response, ChatActivity plan)
        {
            return new ChatActivity
            {
                Kind = "diagnostic",
                Title = FirstNonEmpty(response == null ? null : response.DecisionSummary, "План обновлён"),
                Subtitle = "План обновлён",
                Status = "completed",
                ExecutionStatus = "plan_updated",
                ResultMessage = ProgressText(plan),
                DataJson = plan == null ? null : plan.DataJson
            };
        }

        public static ChatActivity ApplyTerminalDecision(AgentRunState state, string kind)
        {
            if (state == null || state.PlanActivity == null || state.Plan == null)
            {
                return null;
            }

            var final = string.Equals(kind, AgentResponseKinds.Final, StringComparison.OrdinalIgnoreCase);
            var cancelled = string.Equals(kind, AgentResponseKinds.CannotComplete, StringComparison.OrdinalIgnoreCase);
            foreach (var step in state.Plan.Where(item => item != null))
            {
                var status = NormalizeStepStatus(step.Status);
                if (status == "completed" || status == "failed" || status == "cancelled") continue;
                if (final && status == "running") step.Status = "pending";
                else if (cancelled) step.Status = "cancelled";
                else if (status == "running") step.Status = "waiting";
            }
            SyncActivitySteps(state);
            UpdatePlanActivity(state);
            if (final && HasUnfinishedSteps(state))
            {
                state.PlanActivity.Status = "incomplete";
                state.PlanActivity.ExecutionStatus = "terminal_with_pending_steps";
            }
            return state.PlanActivity;
        }

        public static bool HasUnfinishedSteps(AgentRunState state)
        {
            return state != null && (state.Plan ?? new List<AgentPlanStep>()).Any(step => step != null &&
                !string.Equals(NormalizeStepStatus(step.Status), "completed", StringComparison.OrdinalIgnoreCase));
        }

        public static string Fingerprint(AgentPlannerResponse response)
        {
            if (response == null) return string.Empty;
            return string.Join("|", new[]
            {
                response.Goal ?? string.Empty,
                string.Join(";", (response.Plan ?? new List<AgentPlanStep>())
                    .Where(step => step != null)
                    .Select(step => (step.Id ?? string.Empty).Trim() + ":" + (step.Title ?? string.Empty).Trim())
                    .ToArray())
            }).ToLowerInvariant();
        }

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
            if (state.PlanActivity == null && state.PlanDeclared)
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

        private static List<AgentPlanStep> Reconcile(
            IEnumerable<AgentPlanStep> existing,
            IEnumerable<AgentPlanStep> declared)
        {
            var oldSteps = (existing ?? new AgentPlanStep[0]).Where(item => item != null).ToList();
            var nextSteps = (declared ?? new AgentPlanStep[0]).Where(item => item != null).ToList();
            if (nextSteps.Count == 0)
            {
                return CloneSteps(oldSteps);
            }

            var byId = oldSteps
                .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var result = new List<AgentPlanStep>();
            var declaredIds = new HashSet<string>(nextSteps.Select(item => item.Id ?? string.Empty), StringComparer.OrdinalIgnoreCase);
            foreach (var old in oldSteps.Where(item =>
                string.Equals(NormalizeStepStatus(item.Status), "completed", StringComparison.OrdinalIgnoreCase) &&
                !declaredIds.Contains(item.Id ?? string.Empty)))
            {
                result.Add(CloneStep(old));
            }
            foreach (var step in nextSteps)
            {
                AgentPlanStep previous;
                byId.TryGetValue(step.Id ?? string.Empty, out previous);
                var status = previous == null ? "pending" : NormalizeStepStatus(previous.Status);
                if (status == "failed" || status == "cancelled") status = "pending";
                result.Add(new AgentPlanStep
                {
                    Id = step.Id,
                    Title = step.Title,
                    Status = status
                });
            }
            return result;
        }

        private static List<AgentPlanStep> CloneSteps(IEnumerable<AgentPlanStep> source)
        {
            return (source ?? new AgentPlanStep[0]).Where(item => item != null).Select(CloneStep).ToList();
        }

        private static AgentPlanStep CloneStep(AgentPlanStep step)
        {
            return new AgentPlanStep
            {
                Id = step == null ? null : step.Id,
                Title = step == null ? null : step.Title,
                Status = NormalizeStepStatus(step == null ? null : step.Status)
            };
        }

        private static void SyncActivitySteps(AgentRunState state)
        {
            if (state == null || state.PlanActivity == null) return;
            state.PlanActivity.Children = (state.Plan ?? new List<AgentPlanStep>())
                .Where(step => step != null)
                .Select(step => new ChatActivity
                {
                    Kind = "plan_step",
                    Title = step.Title,
                    Subtitle = step.Id,
                    Status = NormalizeStepStatus(step.Status)
                })
                .ToList();
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values ?? new string[0])
            {
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            return string.Empty;
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
