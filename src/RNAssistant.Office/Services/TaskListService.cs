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

        internal TaskListMutation Create(ChatSession session, string goal,
            List<ChatTaskStep> steps, Action beforeMutation)
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
                Steps = steps ?? new List<ChatTaskStep>()
            };
            Validate(taskList);
            var artifact = CreateArtifact(taskList, null, 1);
            Commit(session, artifact, false, beforeMutation);
            return TaskListMutation.Ok(
                "Task list created: " + taskList.Goal, taskList, artifact, false);
        }

        internal TaskListMutation Update(ChatSession session, string id,
            string goal, bool hasGoal, List<ChatTaskStep> steps, bool hasSteps,
            Action beforeMutation)
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
                !string.Equals(current.Status, "active",
                    StringComparison.OrdinalIgnoreCase))
            {
                return TaskListMutation.Fail("Task list is not active: " + id,
                    "task_list_not_active", false);
            }
            if (!hasGoal && !hasSteps)
            {
                return TaskListMutation.Fail(
                    "Task-list update requires goal and/or steps.",
                    "task_list_update_empty", true);
            }

            var updated = Clone(current);
            if (hasGoal) updated.Goal = goal;
            if (hasSteps) updated.Steps = steps ?? new List<ChatTaskStep>();
            Validate(updated);
            var artifact = CreateArtifact(updated, previous,
                Math.Max(1, previous.Revision) + 1);
            Commit(session, artifact, false, beforeMutation);
            return TaskListMutation.Ok(
                "Task list updated: " + updated.Goal, updated, artifact, false);
        }

        internal TaskListMutation Close(ChatSession session, string id,
            string outcome, Action beforeMutation)
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

            var closed = Clone(selected);
            closed.Status = NormalizeOutcome(outcome);
            Validate(closed);
            if (closed.Status == "completed" && closed.Steps.Any(step =>
                step.Status != "completed" && step.Status != "cancelled"))
            {
                return TaskListMutation.Fail(
                    "A completed task list cannot contain pending, in_progress, or blocked steps.",
                    "task_list_not_terminal", false);
            }
            var artifact = CreateArtifact(closed, selectedArtifact,
                Math.Max(1, selectedArtifact.Revision) + 1);
            Commit(session, artifact, true, beforeMutation);
            return TaskListMutation.Ok(
                "Task list closed: " + selected.Goal, closed, artifact, true);
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
                MetadataJson = JsonConvert.SerializeObject(new
                {
                    taskListId = taskList.Id,
                    status = taskList.Status
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
            if (taskList.Steps.Count < 3 || taskList.Steps.Count > MaxSteps)
                throw new InvalidOperationException(
                    "Task list must contain 3-" + MaxSteps + " steps.");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var inProgress = 0;
            foreach (var step in taskList.Steps)
            {
                if (step == null)
                    throw new InvalidOperationException(
                        "Task-list steps must be objects.");
                step.Id = (step.Id ?? string.Empty).Trim();
                step.Text = (step.Text ?? string.Empty).Trim();
                step.Status = NormalizeStatus(step.Status);
                if (step.Status == "in_progress") inProgress++;
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
            if (inProgress > 1)
                throw new InvalidOperationException(
                    "A task list can have at most one in_progress step.");
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
                value == "superseded") return value;
            throw new InvalidOperationException(
                "Unknown task-list outcome: " + value);
        }

        private static ChatTaskList Clone(ChatTaskList value)
        {
            return new ChatTaskList
            {
                ProtocolVersion = value.ProtocolVersion,
                Id = value.Id,
                Goal = value.Goal,
                Status = value.Status,
                Steps = (value.Steps ?? new List<ChatTaskStep>())
                    .Select(step => step == null ? null : new ChatTaskStep
                    {
                        Id = step.Id,
                        Text = step.Text,
                        Status = step.Status
                    }).ToList()
            };
        }

        private static void RequireSession(ChatSession session)
        {
            if (session == null)
                throw new InvalidOperationException(
                    "Task-list tools require an active chat session.");
        }

        private sealed class TaskListRevision
        {
            internal ChatArtifact Artifact { get; set; }
            internal ChatTaskList TaskList { get; set; }
        }
    }

    internal sealed class TaskListMutation
    {
        internal bool Success { get; private set; }
        internal string Message { get; private set; }
        internal string ErrorCode { get; private set; }
        internal bool? Retryable { get; private set; }
        internal ChatTaskList TaskList { get; private set; }
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
            string message, string errorCode, bool? retryable)
        {
            return new TaskListMutation
            {
                Success = false,
                Message = message,
                ErrorCode = errorCode,
                Retryable = retryable
            };
        }
    }
}
