using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal sealed class TaskListService
    {
        internal const int MaxSteps = 32;
        internal const int MaxGoalCharacters = 500;
        internal const int MaxStepCharacters = 500;

        private static bool IsOpen(ChatTaskList task)
        { return task != null && (task.Status == "active" || task.Status == "blocked"); }

        internal TaskListMutation Set(ChatSession session, string goal,
            List<ChatTaskStep> steps, Action beforeMutation, string reason = null)
        {
            RequireSession(session);
            if (string.IsNullOrWhiteSpace(session.ActiveTaskListArtifactId))
                return Create(session, goal, BindStepIds(steps, null), beforeMutation, reason);

            ChatTaskList current;
            var currentArtifact = FindRevision(session, null, out current);
            if (currentArtifact == null || current == null ||
                !IsOpen(current))
            {
                return TaskListMutation.Fail(
                    "The active task list is unavailable or ambiguous; reset the chat before saving.",
                    "task_list_active_revision_invalid", false);
            }
            if (current.Steps == null || current.Steps.Any(step => step == null))
                return TaskListMutation.Fail(
                    "The active task-list steps are unavailable; reset the chat before saving.",
                    "task_list_active_revision_invalid", false);
            return Update(session, currentArtifact.Id,
                BindStepIds(steps, current), beforeMutation, goal, reason);
        }

        internal TaskListMutation UpdateStatuses(ChatSession session, string goal,
            List<TaskListStatusUpdate> updates, Action beforeMutation)
        {
            RequireSession(session);
            ChatTaskList current;
            var currentArtifact = FindRevision(session, null, out current);
            if (currentArtifact == null || current == null ||
                !IsOpen(current) ||
                current.Steps == null || current.Steps.Any(step => step == null))
                return TaskListMutation.Fail(
                    "This chat has no unambiguous active task list to update.",
                    "task_list_active_revision_invalid", false);
            if (goal != null && !string.Equals(goal.Trim(), current.Goal,
                    StringComparison.Ordinal))
                return TaskListMutation.Fail(
                    "The supplied goal does not match the active task list. Use currentTaskList, or save to revise the plan.",
                    "task_list_goal_changed", false, current);
            var invalidUpdates = ValidateStatusUpdates(current, updates, true);
            if (invalidUpdates != null) return invalidUpdates;

            var steps = Clone(current).Steps;
            foreach (var update in updates)
            {
                steps[update.Index - 1].Status = NormalizeStatus(update.Status);
                if (update.Note != null) steps[update.Index - 1].Note = update.Note;
            }
            return Update(session, currentArtifact.Id, steps, beforeMutation);
        }

        internal TaskListMutation CloseActive(ChatSession session,
            string outcome, List<TaskListStatusUpdate> updates,
            Action beforeMutation, string reason = null)
        {
            RequireSession(session);
            ChatTaskList current;
            var currentArtifact = FindRevision(session, null, out current);
            if (currentArtifact == null)
            {
                return TaskListMutation.Fail(
                    "This chat has no unambiguous active task list to close.",
                    "task_list_not_found", false);
            }
            return Close(session, currentArtifact.Id, outcome, updates,
                beforeMutation, reason);
        }

        private TaskListMutation Create(ChatSession session, string goal,
            List<ChatTaskStep> steps, Action beforeMutation, string reason)
        {
            RequireSession(session);
            if (!string.IsNullOrWhiteSpace(session.ActiveTaskListArtifactId))
            {
                return TaskListMutation.Fail(
                    "Close the active task list before creating another one.",
                    "task_list_already_active", false);
            }
            var taskList = new ChatTaskList
            {
                Id = "tasks_" + Guid.NewGuid().ToString("N"),
                Goal = goal,
                Reason = reason,
                Steps = steps ?? new List<ChatTaskStep>()
            };
            Validate(taskList);
            var artifact = CreateArtifact(taskList, null, 1);
            Commit(session, artifact, false, beforeMutation);
            return TaskListMutation.Ok(
                "Task list created: " + taskList.Goal, taskList, artifact, false);
        }

        private TaskListMutation Update(ChatSession session, string id,
            List<ChatTaskStep> steps, Action beforeMutation, string goal = null, string reason = null)
        {
            RequireSession(session);
            ChatTaskList current;
            var previous = FindRevision(session, id, out current);
            if (previous == null)
            {
                return TaskListMutation.Fail("Task list not found: " + id,
                    "task_list_not_found", false);
            }
            if (!string.Equals(session.ActiveTaskListArtifactId, previous.Id,
                    StringComparison.OrdinalIgnoreCase) ||
                !IsOpen(current))
            {
                return TaskListMutation.Fail("Task list is not active: " + id,
                    "task_list_not_active", false);
            }
            var updated = Clone(current);
            updated.Status = "active";
            updated.Blocker = null;
            if (goal != null) updated.Goal = goal;
            if (reason != null) updated.Reason = reason;
            updated.Steps = steps ?? new List<ChatTaskStep>();
            Validate(updated);
            var artifact = CreateArtifact(updated, previous,
                Math.Max(1, previous.Revision) + 1);
            Commit(session, artifact, false, beforeMutation);
            return TaskListMutation.Ok(
                "Task list updated: " + updated.Goal, updated, artifact, false);
        }

        private TaskListMutation Close(ChatSession session, string id,
            string outcome, List<TaskListStatusUpdate> updates,
            Action beforeMutation, string reason)
        {
            RequireSession(session);
            ChatTaskList selected;
            var selectedArtifact = FindRevision(session, id, out selected);
            if (selectedArtifact == null)
            {
                return TaskListMutation.Fail("Task list not found: " + id,
                    "task_list_not_found", false);
            }
            if (!string.Equals(selectedArtifact.Id,
                session.ActiveTaskListArtifactId,
                StringComparison.OrdinalIgnoreCase))
            {
                return TaskListMutation.Fail(
                    "Only the active task list can be closed.",
                    "task_list_not_active", false);
            }

            var terminalStatus = NormalizeOutcome(outcome);
            var invalidUpdates = ValidateStatusUpdates(selected, updates, false);
            if (invalidUpdates != null) return invalidUpdates;
            var closed = Clone(selected);
            closed.Status = terminalStatus;
            foreach (var update in updates ?? new List<TaskListStatusUpdate>())
            {
                closed.Steps[update.Index - 1].Status = NormalizeStatus(update.Status);
                if (update.Note != null) closed.Steps[update.Index - 1].Note = update.Note;
            }
            closed.Blocker = null;
            closed.Reason = terminalStatus == "blocked" ? selected.Reason : reason;
            if (terminalStatus == "blocked")
            {
                if (string.IsNullOrWhiteSpace(reason))
                    return TaskListMutation.Fail("A blocked task needs a concrete reason.",
                        "task_list_blocker_required", false, selected);
                closed.Blocker = reason.Trim();
            }
            Validate(closed);
            var artifact = CreateArtifact(closed, selectedArtifact,
                Math.Max(1, selectedArtifact.Revision) + 1);
            Commit(session, artifact, terminalStatus != "blocked", beforeMutation);
            return TaskListMutation.Ok(
                (terminalStatus == "blocked" ? "Task blocked; unfinished steps retained: " : "Task list closed: ") + selected.Goal,
                closed, artifact, terminalStatus != "blocked");
        }

        private static TaskListMutation ValidateStatusUpdates(
            ChatTaskList current, List<TaskListStatusUpdate> updates,
            bool requireAny)
        {
            if (!requireAny && (updates == null || updates.Count == 0))
                return null;
            if (current == null || current.Steps == null ||
                updates == null || updates.Count == 0 || updates.Count > MaxSteps ||
                updates.Any(update => update == null || update.Index < 1 ||
                    update.Index > current.Steps.Count ||
                    string.IsNullOrWhiteSpace(update.Status)) ||
                updates.Select(update => update.Index).Distinct().Count() != updates.Count)
                return TaskListMutation.Fail(
                    "Status updates need distinct 1-based step indexes from currentTaskList and a status for each.",
                    "task_list_status_updates_invalid", false, current);
            return null;
        }

        private static void Commit(ChatSession session, ChatArtifact artifact,
            bool close, Action beforeMutation)
        {
            if (beforeMutation == null)
                throw new ArgumentNullException(nameof(beforeMutation));
            beforeMutation();
            session.Artifacts = session.Artifacts ?? new List<ChatArtifact>();
            session.Artifacts.Add(artifact);
            session.ActiveTaskListArtifactId = close ? null : artifact.Id;
        }

        private static ChatArtifact CreateArtifact(
            ChatTaskList taskList, ChatArtifact parent, int revision)
        {
            return new ChatArtifact
            {
                Id = taskList.Id + "_r" + revision + "_" +
                    Guid.NewGuid().ToString("N").Substring(0, 8),
                Kind = ChatArtifactKinds.TaskList,
                Title = taskList.Goal,
                MimeType = "application/vnd.rnassistant.task-list+json",
                Revision = revision,
                ParentArtifactId = parent == null ? null : parent.Id,
                InlineText = JsonConvert.SerializeObject(taskList),
                MetadataJson = JsonConvert.SerializeObject(new TaskListRevisionMetadata {
                    TaskListId = taskList.Id, Status = taskList.Status
                })
            };
        }

        private static ChatArtifact FindRevision(
            ChatSession session, string id, out ChatTaskList taskList)
        {
            taskList = null;
            var revisions = TaskListRevisions(session).ToList();
            TaskListRevision selected;
            if (string.IsNullOrWhiteSpace(id))
            {
                selected = revisions.FirstOrDefault(item => string.Equals(
                    item.Artifact.Id, session.ActiveTaskListArtifactId,
                    StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                var exactArtifact = revisions.FirstOrDefault(item =>
                    string.Equals(item.Artifact.Id, id,
                        StringComparison.OrdinalIgnoreCase));
                var taskListId = exactArtifact == null ? id : exactArtifact.TaskList.Id;
                selected = revisions.Where(item => string.Equals(
                        item.TaskList.Id, taskListId,
                        StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(item => item.Artifact.Revision)
                    .ThenByDescending(item => item.Artifact.CreatedUtc)
                    .FirstOrDefault();
            }
            if (selected == null) return null;
            taskList = selected.TaskList;
            return selected.Artifact;
        }

        private static IEnumerable<TaskListRevision> TaskListRevisions(
            ChatSession session)
        {
            var artifacts = ((session == null ? null : session.Artifacts) ??
                    new List<ChatArtifact>())
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Id))
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() == 1)
                .Select(group => group.Single());
            foreach (var artifact in artifacts)
            {
                if (!string.Equals(artifact.Kind, ChatArtifactKinds.TaskList,
                        StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(artifact.InlineText)) continue;
                ChatTaskList taskList;
                try
                {
                    taskList = JsonConvert.DeserializeObject<ChatTaskList>(
                        artifact.InlineText);
                }
                catch (JsonException)
                {
                    continue;
                }
                if (taskList == null || string.IsNullOrWhiteSpace(taskList.Id))
                    continue;
                yield return new TaskListRevision
                {
                    Artifact = artifact,
                    TaskList = taskList
                };
            }
        }

        private static void Validate(ChatTaskList taskList)
        {
            if (taskList == null || string.IsNullOrWhiteSpace(taskList.Id))
                throw new InvalidOperationException("Task-list id is required.");
            taskList.Goal = (taskList.Goal ?? string.Empty).Trim();
            if (taskList.Goal.Length == 0 ||
                taskList.Goal.Length > MaxGoalCharacters)
                throw new InvalidOperationException(
                    "Task-list goal must contain 1-" + MaxGoalCharacters +
                    " characters.");
            taskList.Steps = taskList.Steps ?? new List<ChatTaskStep>();
            if (taskList.Steps.Count < 1 || taskList.Steps.Count > MaxSteps)
                throw new InvalidOperationException(
                    "Task list must contain 1-" + MaxSteps + " steps.");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (taskList.Reason?.Length > MaxGoalCharacters || taskList.Blocker?.Length > MaxGoalCharacters)
                throw new InvalidOperationException("Task-list reason is too long.");
            foreach (var step in taskList.Steps)
            {
                if (step == null)
                    throw new InvalidOperationException(
                        "Task-list steps must be objects.");
                step.Id = (step.Id ?? string.Empty).Trim();
                step.Text = (step.Text ?? string.Empty).Trim();
                step.Status = NormalizeStatus(step.Status);
                if (step.Note?.Length > MaxStepCharacters)
                    throw new InvalidOperationException("Task-list step note is too long.");
                if (step.Id.Length == 0 || step.Id.Length > 80 ||
                    step.Id.Any(char.IsWhiteSpace))
                    throw new InvalidOperationException(
                        "Each plan step needs a unique non-whitespace id of at most 80 characters.");
                if (!ids.Add(step.Id))
                    throw new InvalidOperationException(
                        "Duplicate task-list step id: " + step.Id);
                if (step.Text.Length == 0 ||
                    step.Text.Length > MaxStepCharacters)
                    throw new InvalidOperationException(
                        "Each plan step text must contain 1-" +
                        MaxStepCharacters + " characters.");
            }
        }

        private static string NormalizeStatus(string value)
        {
            var status = string.IsNullOrWhiteSpace(value)
                ? "pending" : value.Trim().ToLowerInvariant();
            switch (status)
            {
                case "pending":
                case "in_progress":
                case "completed":
                case "blocked":
                case "cancelled":
                    return status;
                default:
                    throw new InvalidOperationException(
                        "Unknown task-list step status: " + status);
            }
        }

        private static string NormalizeOutcome(string value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (value == "completed" || value == "cancelled" ||
                value == "superseded" || value == "blocked") return value;
            throw new InvalidOperationException(
                "Unknown task-list outcome: " + value);
        }

        private static List<ChatTaskStep> BindStepIds(IEnumerable<ChatTaskStep> requested,
            ChatTaskList current)
        {
            var prior = (current == null ? null : current.Steps) ??
                new List<ChatTaskStep>();
            var result = new List<ChatTaskStep>();
            var unused = new List<ChatTaskStep>(prior);
            foreach (var step in requested ?? new ChatTaskStep[0])
            {
                var text = step == null ? string.Empty : (step.Text ?? string.Empty).Trim();
                // Carry progress only for the same stage, never by its old position.
                var matches = unused.Where(item => item.Text == text).ToList();
                var previous = matches.Count == 1 ? matches[0] : null;
                if (previous != null) unused.Remove(previous);
                result.Add(new ChatTaskStep
                {
                    Id = previous?.Id ?? "step_" + Guid.NewGuid().ToString("N"),
                    Text = text,
                    Status = step?.Status ?? previous?.Status,
                    Note = step?.Note ?? previous?.Note
                });
            }
            return result;
        }

        private static ChatTaskList Clone(ChatTaskList value)
        {
            return new ChatTaskList
            {
                ProtocolVersion = value.ProtocolVersion,
                Id = value.Id,
                Goal = value.Goal,
                Status = value.Status,
                Blocker = value.Blocker,
                Reason = value.Reason,
                Steps = (value.Steps ?? new List<ChatTaskStep>())
                    .Select(step => step == null ? null : new ChatTaskStep
                    {
                        Id = step.Id,
                        Text = step.Text,
                        Status = step.Status,
                        Note = step.Note
                    }).ToList()
            };
        }

        private static void RequireSession(ChatSession session)
        {
            if (session == null)
                throw new InvalidOperationException(
                    "Task-list tools require an active chat session.");
        }

        internal static ChatTaskList Active(ChatSession session)
        { ChatTaskList task; return session == null || string.IsNullOrEmpty(session.ActiveTaskListArtifactId) ||
            FindRevision(session, null, out task) == null ? null : task; }

        internal static TaskListStateProjection ProjectCurrent(ChatSession session)
        {
            if (string.IsNullOrEmpty(session?.ActiveTaskListArtifactId)) return null;
            var task = Active(session);
            return Project(task);
        }

        internal static TaskListStateProjection Project(ChatTaskList task)
        {
            return new TaskListStateProjection
            {
                Available = task != null, Goal = task?.Goal, Status = task?.Status,
                Blocker = task?.Blocker, Reason = task?.Reason,
                Steps = task?.Steps?.Select((step, index) => new TaskStepProjection
                {
                    Index = index + 1, Text = step.Text, Status = step.Status, Note = step.Note
                }).ToList()
            };
        }

        private sealed class TaskListRevision
        {
            internal ChatArtifact Artifact { get; set; }
            internal ChatTaskList TaskList { get; set; }
        }
    }

    internal sealed class TaskListStateProjection
    {
        [JsonProperty("available")] public bool Available { get; set; }
        [JsonProperty("goal")] public string Goal { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("blocker")] public string Blocker { get; set; }
        [JsonProperty("statusSource")] public string StatusSource { get { return "agent_assessment"; } }
        [JsonProperty("reason")] public string Reason { get; set; }
        [JsonProperty("steps")] public List<TaskStepProjection> Steps { get; set; }
    }
    internal sealed class TaskStepProjection
    {
        [JsonProperty("index")] public int Index { get; set; }
        [JsonProperty("text")] public string Text { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("note", NullValueHandling = NullValueHandling.Ignore)] public string Note { get; set; }
    }
    internal sealed class TaskListRevisionMetadata
    {
        [JsonProperty("taskListId")] public string TaskListId { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
    }

    internal sealed class TaskListStatusUpdate
    {
        [JsonProperty("index")]
        public int Index { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("note", NullValueHandling = NullValueHandling.Ignore)]
        public string Note { get; set; }
    }

    internal sealed class TaskListMutation
    {
        internal bool Success { get; private set; }
        internal string Message { get; private set; }
        internal string ErrorCode { get; private set; }
        internal bool? Retryable { get; private set; }
        internal ChatTaskList TaskList { get; private set; }
        internal ChatTaskList CurrentTaskList { get; private set; }
        internal ChatArtifact Artifact { get; private set; }
        internal bool Closed { get; private set; }

        private TaskListMutation() { }

        internal static TaskListMutation Ok(string message,
            ChatTaskList taskList, ChatArtifact artifact, bool closed)
        {
            return new TaskListMutation
            {
                Success = true,
                Message = message,
                TaskList = taskList,
                Artifact = artifact,
                Closed = closed
            };
        }

        internal static TaskListMutation Fail(
            string message, string errorCode, bool? retryable,
            ChatTaskList currentTaskList = null)
        {
            return new TaskListMutation
            {
                Success = false,
                Message = message,
                ErrorCode = errorCode,
                Retryable = retryable,
                CurrentTaskList = currentTaskList
            };
        }
    }
}
