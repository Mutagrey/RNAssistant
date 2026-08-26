using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void TaskListCrudCreatesRevisionsAndClosesCleanly()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = NewSession(adapter);
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                AssertTrue(tools.Any(tool => tool.Id == TaskListToolExecutor.CreateToolId), "task-list create exposed to Agent");
                var legacyPlanIds = new[] { "common.plan_create", "common.plan_update", "common.plan_delete", "common.plan_read" };
                AssertTrue(tools.All(tool => !legacyPlanIds.Contains(tool.Id, StringComparer.OrdinalIgnoreCase)), "legacy plan tools are removed");
                AssertTrue(tools.Any(tool => tool.Id == TaskListToolExecutor.UpdateToolId), "task-list update exposed to Agent");
                AssertTrue(tools.Any(tool => tool.Id == TaskListToolExecutor.CloseToolId), "task-list close exposed to Agent");
                var planningSkill = BuiltInSkillProvider.GetSkills(adapter).Single(skill => skill.Id == "common.task_tracking");
                AssertContains(planningSkill.BodyMarkdown, TaskListToolExecutor.CreateToolId, "tracking skill explains create");
                AssertContains(planningSkill.BodyMarkdown, TaskListToolExecutor.UpdateToolId, "tracking skill explains update");
                var create = Command(
                    TaskListToolExecutor.CreateToolId,
                    "goal", "Prepare workbook report",
                    "steps", new JArray(
                        new JObject { ["id"] = "inspect", ["text"] = "Inspect source data", ["status"] = "in_progress" },
                        new JObject { ["id"] = "write", ["text"] = "Write the report", ["status"] = "pending" },
                        new JObject { ["id"] = "verify", ["text"] = "Verify the report", ["status"] = "pending" }));

                var created = executor.Execute(create, tools, new AppSettings(), false, false, session);
                AssertTrue(created.Success, "plan create succeeds");
                var createdData = JObject.Parse(created.DataJson);
                var planId = (string)createdData["taskList"]["id"];
                var firstArtifactId = (string)createdData["artifactId"];
                AssertTrue(!string.IsNullOrWhiteSpace(planId), "stable plan id returned");
                AssertEqual(firstArtifactId, session.ActiveTaskListArtifactId, "created task list becomes active");
                AssertEqual(1, session.Artifacts.Count(item => item.Kind == ChatArtifactKinds.TaskList), "first task-list revision stored");

                var createMessage = AgentTranscript.CreateLocalResultMessage(create, created);
                session.Messages.Add(createMessage);
                ChatResourceReferenceService.LinkMessageResources(session, 0);
                AssertTrue(ReferencesArtifact(session, createMessage, firstArtifactId), "created plan linked to tool result message");

                var update = Command(TaskListToolExecutor.UpdateToolId, "id", planId, "goal", "Prepare verified workbook report");
                var updated = executor.Execute(update, tools, new AppSettings(), false, false, session);
                AssertTrue(updated.Success, "partial plan update succeeds");
                var updatedData = JObject.Parse(updated.DataJson);
                var secondArtifactId = (string)updatedData["artifactId"];
                AssertEqual(2L, (long)updatedData["revision"], "plan revision increments");
                AssertEqual(3, updatedData["taskList"]["steps"].Count(), "omitted steps preserved");
                AssertEqual(firstArtifactId, session.Artifacts.Single(item => item.Id == secondArtifactId).ParentArtifactId, "revision parent linked");
                AssertEqual(secondArtifactId, session.ActiveTaskListArtifactId, "updated revision becomes active");

                var updateMessage = AgentTranscript.CreateLocalResultMessage(update, updated);
                session.Messages.Add(updateMessage);
                ChatResourceReferenceService.LinkMessageResources(session, 1);
                AssertTrue(ReferencesArtifact(session, updateMessage, secondArtifactId), "updated plan linked to tool result message");

                var updatedArtifact = session.Artifacts.Single(item => item.Id == secondArtifactId);
                var planUri = ArtifactUri(session, updatedArtifact);
                var read = ReadResource(new ResourceGatewayService(), session, planUri, "text", null, 32000).Result;
                AssertContains(read.Text, "Prepare verified workbook report", "active plan revision reads through resources");
                var removedRead = executor.Execute(Command("common.plan_read"), tools, new AppSettings(), false, false, session);
                AssertEqual("unknown_tool", removedRead.ErrorCode, "legacy plan id stays unknown");

                session.Messages.Remove(updateMessage);
                ChatResourceReferenceService.RestoreActiveTaskListFromMessages(session);
                ChatResourceReferenceService.PruneUnreachable(session);
                AssertEqual(firstArtifactId, session.ActiveTaskListArtifactId, "history rewind restores prior task-list revision");
                AssertTrue(session.Artifacts.All(item => item.Id != secondArtifactId), "future plan revision pruned");

                var completedSteps = new JArray(
                    new JObject { ["id"] = "inspect", ["text"] = "Inspect source data", ["status"] = "completed" },
                    new JObject { ["id"] = "write", ["text"] = "Write the report", ["status"] = "completed" },
                    new JObject { ["id"] = "verify", ["text"] = "Verify the report", ["status"] = "completed" });
                var finalUpdate = executor.Execute(Command(TaskListToolExecutor.UpdateToolId, "id", planId, "steps", completedSteps), tools, new AppSettings(), false, false, session);
                AssertTrue(finalUpdate.Success, "final task-list update succeeds");
                var closed = executor.Execute(Command(TaskListToolExecutor.CloseToolId, "id", planId, "outcome", "completed"), tools, new AppSettings(), false, false, session);
                AssertTrue(closed.Success, "task-list close succeeds");
                AssertTrue(string.IsNullOrWhiteSpace(session.ActiveTaskListArtifactId), "closed task list is hidden");
                AssertTrue(session.Artifacts.Count(item => item.Kind == ChatArtifactKinds.TaskList) >= 3, "closed task-list history remains stored");
            });
        }

        private static void TaskListCrudRejectsAmbiguousSteps()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = NewSession(adapter);
                var result = executor.Execute(
                    Command(
                        TaskListToolExecutor.CreateToolId,
                        "goal", "Invalid duplicate plan",
                        "steps", new JArray(
                            new JObject { ["id"] = "same", ["text"] = "First" },
                            new JObject { ["id"] = "same", ["text"] = "Second" },
                            new JObject { ["id"] = "third", ["text"] = "Third" })),
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(),
                    new AppSettings(),
                    false,
                    false,
                    session);

                AssertTrue(!result.Success, "duplicate plan step ids rejected");
                AssertContains(result.Message, "Duplicate task-list step id", "duplicate task-list diagnostic");
                AssertEqual(0, session.Artifacts.Count, "invalid plan not stored");
            });
        }
    }
}
