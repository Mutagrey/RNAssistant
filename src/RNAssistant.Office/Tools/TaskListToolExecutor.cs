using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Office.Tools
{
    internal sealed class TaskListToolExecutor
    {
        public const string CreateToolId = "common.task_list_create";
        public const string UpdateToolId = "common.task_list_update";
        public const string CloseToolId = "common.task_list_close";

        private const int MaxSteps = 32;
        private const int MaxGoalChars = 500;
        private const int MaxStepChars = 500;

        public IEnumerable<ToolDefinition> GetControllerTools()
        {
            yield return ControllerToolDefinition.Create(CreateToolId, "Common",
                "Task list: Create the visible checklist for the current active chat task. Use for at least three meaningful stages, not individual tool calls.",
                CreateSchema(), mutatesLocalState: true, name: "task_list_create", scope: "session");
            yield return ControllerToolDefinition.Create(UpdateToolId, "Common",
                "Task list: Replace the complete steps of the active checklist after material progress. Stable step ids must be preserved.",
                UpdateSchema(), mutatesLocalState: true, name: "task_list_update", scope: "session");
            yield return ControllerToolDefinition.Create(CloseToolId, "Common",
                "Task list: Close and hide the active checklist while preserving its final revision in chat history.",
                CloseSchema(), mutatesLocalState: true, name: "task_list_close", scope: "session");
        }

        public ToolResult ExecuteControllerTool(ToolCommand command, ChatSession session, bool dryRun)
        {
            if (command == null) return ToolResult.Fail("Tool command is empty.");
            if (session == null) return ToolResult.Fail("Task-list tools require an active chat session.", null, "task_list_session_required", false);
            session.Artifacts = session.Artifacts ?? new List<ChatArtifact>();

            try
            {
                if (string.Equals(command.ToolId, CreateToolId, StringComparison.OrdinalIgnoreCase))
                {
                    return Create(command, session, dryRun);
                }
                if (string.Equals(command.ToolId, UpdateToolId, StringComparison.OrdinalIgnoreCase))
                {
                    return Update(command, session, dryRun);
                }
                if (string.Equals(command.ToolId, CloseToolId, StringComparison.OrdinalIgnoreCase))
                {
                    return Close(command, session, dryRun);
                }
            }
            catch (JsonException ex)
            {
                return ToolResult.Fail("Task-list JSON is invalid: " + ex.Message, null, "invalid_task_list", true);
            }
            catch (InvalidOperationException ex)
            {
                return ToolResult.Fail(ex.Message, null, "invalid_task_list", true);
            }

            return ToolResult.Fail("Unknown task-list tool: " + command.ToolId);
        }

        private static ToolResult Create(ToolCommand command, ChatSession session, bool dryRun)
        {
            if (!string.IsNullOrWhiteSpace(session.ActiveTaskListArtifactId))
            {
                return ToolResult.Fail("Close the active task list before creating another one.", null, "task_list_already_active", false);
            }
            var plan = new ChatTaskList
            {
                Id = "tasks_" + Guid.NewGuid().ToString("N"),
                Goal = ToolArgumentReader.String(command.Arguments, "goal", string.Empty),
                Steps = ReadSteps(command, "steps")
            };
            ValidatePlan(plan);
            if (dryRun)
            {
                return ToolResult.Ok("Dry run: would create a visible task list.", Payload(plan, null).ToString(Formatting.None));
            }

            var artifact = CreateArtifact(plan, null, 1);
            session.Artifacts.Add(artifact);
            session.ActiveTaskListArtifactId = artifact.Id;
            return ToolResult.Ok("Task list created: " + plan.Goal, Payload(plan, artifact).ToString(Formatting.None));
        }

        private static ToolResult Update(ToolCommand command, ChatSession session, bool dryRun)
        {
            var id = ToolArgumentReader.String(command.Arguments, "id", string.Empty);
            ChatTaskList current;
            var previous = FindRevision(session, id, out current);
            if (previous == null)
            {
                return ToolResult.Fail("Task list not found: " + id, null, "task_list_not_found", false);
            }
            if (!string.Equals(session.ActiveTaskListArtifactId, previous.Id, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(current.Status, "active", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Fail("Task list is not active: " + id, null, "task_list_not_active", false);
            }
            var hasGoal = HasArgument(command, "goal");
            var hasSteps = HasArgument(command, "steps");
            if (!hasGoal && !hasSteps)
            {
                return ToolResult.Fail("Task-list update requires goal and/or steps.", null, "task_list_update_empty", true);
            }

            var updated = Clone(current);
            if (hasGoal) updated.Goal = ToolArgumentReader.String(command.Arguments, "goal", string.Empty);
            if (hasSteps) updated.Steps = ReadSteps(command, "steps");
            ValidatePlan(updated);
            if (dryRun)
            {
                return ToolResult.Ok("Dry run: would update task list " + updated.Id + ".", Payload(updated, previous).ToString(Formatting.None));
            }

            var artifact = CreateArtifact(updated, previous, Math.Max(1, previous.Revision) + 1);
            session.Artifacts.Add(artifact);
            session.ActiveTaskListArtifactId = artifact.Id;
            return ToolResult.Ok("Task list updated: " + updated.Goal, Payload(updated, artifact).ToString(Formatting.None));
        }

        private static ToolResult Close(ToolCommand command, ChatSession session, bool dryRun)
        {
            var id = ToolArgumentReader.String(command.Arguments, "id", string.Empty);
            ChatTaskList selected;
            var selectedArtifact = FindRevision(session, id, out selected);
            if (selectedArtifact == null)
            {
                return ToolResult.Fail("Task list not found: " + id, null, "task_list_not_found", false);
            }
            if (!string.Equals(selectedArtifact.Id, session.ActiveTaskListArtifactId, StringComparison.OrdinalIgnoreCase))
                return ToolResult.Fail("Only the active task list can be closed.", null, "task_list_not_active", false);
            var outcome = NormalizeOutcome(ToolArgumentReader.String(command.Arguments, "outcome", string.Empty));
            var closed = Clone(selected);
            closed.Status = outcome;
            ValidatePlan(closed);
            if (outcome == "completed" && closed.Steps.Any(step => step.Status != "completed" && step.Status != "cancelled"))
                return ToolResult.Fail("A completed task list cannot contain pending, in_progress, or blocked steps.", null, "task_list_not_terminal", false);
            if (dryRun)
            {
                return ToolResult.Ok("Dry run: would close task list " + selected.Id + ".", Payload(closed, selectedArtifact).ToString(Formatting.None));
            }
            var artifact = CreateArtifact(closed, selectedArtifact, Math.Max(1, selectedArtifact.Revision) + 1);
            session.Artifacts.Add(artifact);
            session.ActiveTaskListArtifactId = null;
            return ToolResult.Ok("Task list closed: " + selected.Goal, Payload(closed, artifact).ToString(Formatting.None));
        }

        private static ChatArtifact CreateArtifact(ChatTaskList plan, ChatArtifact parent, int revision)
        {
            return new ChatArtifact
            {
                Id = plan.Id + "_r" + revision + "_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Kind = ChatArtifactKinds.TaskList,
                Title = plan.Goal,
                MimeType = "application/vnd.rnassistant.task-list+json",
                Revision = revision,
                ParentArtifactId = parent == null ? null : parent.Id,
                InlineText = JsonConvert.SerializeObject(plan),
                MetadataJson = JsonConvert.SerializeObject(new { taskListId = plan.Id, status = plan.Status })
            };
        }

        private static ChatArtifact FindRevision(ChatSession session, string id, out ChatTaskList plan)
        {
            plan = null;
            var revisions = TaskListRevisions(session).ToList();
            PlanRevision selected = null;
            if (string.IsNullOrWhiteSpace(id))
            {
                selected = revisions.FirstOrDefault(item => string.Equals(item.Artifact.Id, session.ActiveTaskListArtifactId, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                var exactArtifact = revisions.FirstOrDefault(item => string.Equals(item.Artifact.Id, id, StringComparison.OrdinalIgnoreCase));
                var planId = exactArtifact == null ? id : exactArtifact.Plan.Id;
                selected = revisions.Where(item => string.Equals(item.Plan.Id, planId, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(item => item.Artifact.Revision)
                    .ThenByDescending(item => item.Artifact.CreatedUtc)
                    .FirstOrDefault();
            }
            if (selected == null) return null;
            plan = selected.Plan;
            return selected.Artifact;
        }

        private static IEnumerable<PlanRevision> TaskListRevisions(ChatSession session)
        {
            var artifacts = ((session == null ? null : session.Artifacts) ?? new List<ChatArtifact>())
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Id))
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() == 1)
                .Select(group => group.Single());
            foreach (var artifact in artifacts)
            {
                if (artifact == null || !string.Equals(artifact.Kind, ChatArtifactKinds.TaskList, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(artifact.InlineText)) continue;
                ChatTaskList plan;
                try
                {
                    plan = JsonConvert.DeserializeObject<ChatTaskList>(artifact.InlineText);
                }
                catch (JsonException)
                {
                    continue;
                }
                if (plan == null || string.IsNullOrWhiteSpace(plan.Id)) continue;
                yield return new PlanRevision { Artifact = artifact, Plan = plan };
            }
        }

        private static List<ChatTaskStep> ReadSteps(ToolCommand command, string name)
        {
            var raw = ToolArgumentReader.String(command.Arguments, name, "[]");
            return JsonConvert.DeserializeObject<List<ChatTaskStep>>(raw) ?? new List<ChatTaskStep>();
        }

        private static void ValidatePlan(ChatTaskList plan)
        {
            if (plan == null || string.IsNullOrWhiteSpace(plan.Id)) throw new InvalidOperationException("Task-list id is required.");
            plan.Goal = (plan.Goal ?? string.Empty).Trim();
            if (plan.Goal.Length == 0 || plan.Goal.Length > MaxGoalChars) throw new InvalidOperationException("Task-list goal must contain 1-" + MaxGoalChars + " characters.");
            plan.Steps = plan.Steps ?? new List<ChatTaskStep>();
            if (plan.Steps.Count < 3 || plan.Steps.Count > MaxSteps) throw new InvalidOperationException("Task list must contain 3-" + MaxSteps + " steps.");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var inProgress = 0;
            foreach (var step in plan.Steps)
            {
                if (step == null) throw new InvalidOperationException("Task-list steps must be objects.");
                step.Id = (step.Id ?? string.Empty).Trim();
                step.Text = (step.Text ?? string.Empty).Trim();
                step.Status = NormalizeStatus(step.Status);
                if (step.Status == "in_progress") inProgress++;
                if (step.Id.Length == 0 || step.Id.Length > 80 || step.Id.Any(char.IsWhiteSpace)) throw new InvalidOperationException("Each plan step needs a unique non-whitespace id of at most 80 characters.");
                if (!ids.Add(step.Id)) throw new InvalidOperationException("Duplicate task-list step id: " + step.Id);
                if (step.Text.Length == 0 || step.Text.Length > MaxStepChars) throw new InvalidOperationException("Each plan step text must contain 1-" + MaxStepChars + " characters.");
            }
            if (inProgress > 1) throw new InvalidOperationException("A task list can have at most one in_progress step.");
        }

        private static string NormalizeStatus(string value)
        {
            var status = string.IsNullOrWhiteSpace(value) ? "pending" : value.Trim().ToLowerInvariant();
            switch (status)
            {
                case "pending":
                case "in_progress":
                case "completed":
                case "blocked":
                case "cancelled":
                    return status;
                default:
                    throw new InvalidOperationException("Unknown task-list step status: " + status);
            }
        }

        private static string NormalizeOutcome(string value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (value == "completed" || value == "cancelled" || value == "superseded") return value;
            throw new InvalidOperationException("Unknown task-list outcome: " + value);
        }

        private static ChatTaskList Clone(ChatTaskList value)
        {
            return JsonConvert.DeserializeObject<ChatTaskList>(JsonConvert.SerializeObject(value)) ?? new ChatTaskList();
        }

        private static JObject Payload(ChatTaskList plan, ChatArtifact artifact)
        {
            return new JObject
            {
                ["artifactId"] = artifact == null ? null : artifact.Id,
                ["revision"] = artifact == null ? 0 : artifact.Revision,
                ["taskList"] = plan == null ? null : JObject.FromObject(plan)
            };
        }

        private static bool HasArgument(ToolCommand command, string name)
        {
            return command != null && command.Arguments != null && command.Arguments.ContainsKey(name);
        }

        private static string CreateSchema()
        {
            return PlanPayloadSchema(false);
        }

        private static string UpdateSchema()
        {
            return PlanPayloadSchema(true);
        }

        private static string PlanPayloadSchema(bool update)
        {
            var properties = new JObject
            {
                ["id"] = new JObject { ["type"] = "string", ["description"] = "Stable task-list id returned by task_list_create, or any revision artifact id." },
                ["goal"] = new JObject { ["type"] = "string", ["description"] = "Concise user-visible goal for the current task.", ["minLength"] = 1, ["maxLength"] = MaxGoalChars },
                ["steps"] = new JObject
                {
                    ["type"] = "array",
                    ["description"] = update ? "Complete replacement list of task steps." : "Complete ordered meaningful task stages.",
                    ["minItems"] = 3,
                    ["maxItems"] = MaxSteps,
                    ["items"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["id"] = new JObject { ["type"] = "string", ["description"] = "Stable unique step id without whitespace." },
                            ["text"] = new JObject { ["type"] = "string", ["description"] = "Concise user-visible step description.", ["minLength"] = 1, ["maxLength"] = MaxStepChars },
                            ["status"] = new JObject { ["type"] = "string", ["description"] = "Explicit current step status.", ["enum"] = new JArray("pending", "in_progress", "completed", "blocked", "cancelled"), ["default"] = "pending" }
                        },
                        ["required"] = new JArray("id", "text"),
                        ["additionalProperties"] = false
                    }
                }
            };
            if (!update) properties.Remove("id");
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = update ? new JArray("id") : new JArray("goal", "steps"),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }

        private static string CloseSchema()
        {
            return "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\",\"description\":\"Stable task-list id or any revision id.\",\"minLength\":1},\"outcome\":{\"type\":\"string\",\"enum\":[\"completed\",\"cancelled\",\"superseded\"],\"description\":\"Why the task list is being closed.\"}},\"required\":[\"id\",\"outcome\"],\"additionalProperties\":false}";
        }

        private sealed class PlanRevision
        {
            public ChatArtifact Artifact { get; set; }
            public ChatTaskList Plan { get; set; }
        }
    }
}
