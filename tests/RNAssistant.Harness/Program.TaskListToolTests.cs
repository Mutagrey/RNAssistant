using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office;
using RNAssistant.Office.Runtime;
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
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                AssertTrue(tools.Any(tool => tool.Id == TaskListToolCatalog.CreateToolId), "task-list create exposed to Agent");
                AssertTrue(NativeToolRuntimeAdapter.Owns(
                    TaskListToolCatalog.CreateToolId),
                    "task-list tools use the native ToolRuntime");
                var taskPolicy = tools.Single(tool =>
                    tool.Id == TaskListToolCatalog.CreateToolId).Policy;
                AssertTrue(taskPolicy != null &&
                    taskPolicy.Effect == ToolEffect.Write &&
                    taskPolicy.Verification == ToolVerification.Tool &&
                    !taskPolicy.RequiresConfirmation &&
                    taskPolicy.AllowedModes.SequenceEqual(
                        new[] { "agent", "plan" }),
                    "task-list carries exact source-owned mode policy");
                var legacyPlanIds = new[] { "common.plan_create", "common.plan_update", "common.plan_delete", "common.plan_read" };
                AssertTrue(tools.All(tool => !legacyPlanIds.Contains(tool.Id, StringComparer.OrdinalIgnoreCase)), "legacy plan tools are removed");
                AssertTrue(tools.Any(tool => tool.Id == TaskListToolCatalog.UpdateToolId), "task-list update exposed to Agent");
                AssertTrue(tools.Any(tool => tool.Id == TaskListToolCatalog.CloseToolId), "task-list close exposed to Agent");
                var planningSkill = BuiltInSkillProvider.GetSkills(adapter).Single(skill => skill.Id == "common.task_tracking");
                AssertContains(planningSkill.BodyMarkdown, TaskListToolCatalog.CreateToolId, "tracking skill explains create");
                AssertContains(planningSkill.BodyMarkdown, TaskListToolCatalog.UpdateToolId, "tracking skill explains update");
                var create = Command(
                    TaskListToolCatalog.CreateToolId,
                    "goal", "Prepare workbook report",
                    "steps", new JArray(
                        new JObject { ["id"] = "inspect", ["text"] = "Inspect source data", ["status"] = "in_progress" },
                        new JObject { ["id"] = "write", ["text"] = "Write the report", ["status"] = "pending" },
                        new JObject { ["id"] = "verify", ["text"] = "Verify the report", ["status"] = "pending" }));

                var created = executor.ExecuteManual(create, tools, new AppSettings(), false, false, session);
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

                var update = Command(TaskListToolCatalog.UpdateToolId, "id", planId, "goal", "Prepare verified workbook report");
                var updated = executor.ExecuteManual(update, tools, new AppSettings(), false, false, session);
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
                var removedRead = executor.ExecuteManual(Command("common.plan_read"), tools, new AppSettings(), false, false, session);
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
                var finalUpdate = executor.ExecuteManual(Command(TaskListToolCatalog.UpdateToolId, "id", planId, "steps", completedSteps), tools, new AppSettings(), false, false, session);
                AssertTrue(finalUpdate.Success, "final task-list update succeeds");
                var closed = executor.ExecuteManual(Command(TaskListToolCatalog.CloseToolId, "id", planId, "outcome", "completed"), tools, new AppSettings(), false, false, session);
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
                var result = executor.ExecuteManual(
                    Command(
                        TaskListToolCatalog.CreateToolId,
                        "goal", "Invalid duplicate plan",
                        "steps", new JArray(
                            new JObject { ["id"] = "same", ["text"] = "First" },
                            new JObject { ["id"] = "same", ["text"] = "Second" },
                            new JObject { ["id"] = "third", ["text"] = "Third" })),
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(),
                    new AppSettings(),
                    false,
                    false,
                    session);

                AssertTrue(!result.Success, "duplicate plan step ids rejected");
                AssertContains(result.Message, "Duplicate task-list step id", "duplicate task-list diagnostic");
                AssertEqual(0, session.Artifacts.Count, "invalid plan not stored");
            });
        }

        private static void TaskListUsesVerifiedNativeRuntime()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = NewSession(adapter);
                var tools = OfficeToolCatalog.ForHost(adapter.HostName)
                    .Concat(executor.GetControllerTools()).ToList();
                var definitions = tools.Where(tool =>
                    TaskListToolCatalog.Owns(tool.Id)).ToArray();
                var runtime = executor.CreateNativeRuntime(session,
                    definitions, new AppSettings(), ChatModes.Agent, false);
                var call = new ToolCall("task-list-native-create",
                    TaskListToolCatalog.CreateToolId,
                    "{\"goal\":\"Native tracking\",\"steps\":[" +
                    "{\"id\":\"one\",\"text\":\"First\",\"status\":\"in_progress\"}," +
                    "{\"id\":\"two\",\"text\":\"Second\"}," +
                    "{\"id\":\"three\",\"text\":\"Third\"}]}");
                var policy = runtime.Describe(call);
                AssertTrue(policy != null && policy.MayHaveSideEffects &&
                    policy.Policy.Verification == ToolVerification.Tool,
                    "Task List create has one exact verified-write registration");

                var created = ExecuteNative(runtime, call, policy);
                AssertEqual(ToolExecutionOutcome.Ok, created.Outcome,
                    "native Task List create succeeds");
                AssertEqual(ToolDispatchEvidence.MayHaveDispatched,
                    created.Evidence.Dispatch,
                    "native Task List create records its session mutation boundary");
                AssertEqual(ToolEffectEvidence.VerifiedChange,
                    created.Evidence.Effect,
                    "native Task List create verifies the exact active revision");
                var createdData = JObject.Parse(created.Result.DataJson);
                AssertEqual((string)createdData["artifactId"],
                    session.ActiveTaskListArtifactId,
                    "verified result identifies the exact active Task List artifact");

                var duplicateCall = new ToolCall("task-list-native-duplicate",
                    TaskListToolCatalog.CreateToolId, call.ArgumentsJson);
                var duplicate = ExecuteNative(runtime, duplicateCall,
                    runtime.Describe(duplicateCall));
                AssertEqual(ToolExecutionOutcome.Error, duplicate.Outcome,
                    "active-list rejection stays a known error");
                AssertEqual(ToolDispatchEvidence.NotDispatched,
                    duplicate.Evidence.Dispatch,
                    "active-list rejection occurs before the mutation boundary");

                var artifactCount = session.Artifacts.Count;
                var dryRun = executor.ExecuteManual(Command(
                    TaskListToolCatalog.UpdateToolId,
                    "id", (string)createdData["taskList"]["id"],
                    "goal", "Preview"), tools, new AppSettings(),
                    true, true, session);
                AssertTrue(dryRun.Success && session.Artifacts.Count == artifactCount,
                    "native Task List dry-run validates schema without mutation");

                var taskListId = (string)createdData["taskList"]["id"];
                var updateCall = new ToolCall("task-list-native-update",
                    TaskListToolCatalog.UpdateToolId,
                    "{\"id\":\"" + taskListId + "\",\"steps\":[" +
                    "{\"id\":\"one\",\"text\":\"First\",\"status\":\"completed\"}," +
                    "{\"id\":\"two\",\"text\":\"Second\",\"status\":\"completed\"}," +
                    "{\"id\":\"three\",\"text\":\"Third\",\"status\":\"completed\"}]}");
                var updated = ExecuteNative(runtime, updateCall,
                    runtime.Describe(updateCall));
                AssertEqual(ToolEffectEvidence.VerifiedChange,
                    updated.Evidence.Effect,
                    "native Task List update verifies its appended revision");

                var closeCall = new ToolCall("task-list-native-close",
                    TaskListToolCatalog.CloseToolId,
                    "{\"id\":\"" + taskListId +
                    "\",\"outcome\":\"completed\"}");
                var closed = ExecuteNative(runtime, closeCall,
                    runtime.Describe(closeCall));
                AssertEqual(ToolExecutionOutcome.Ok, closed.Outcome,
                    "native Task List close succeeds");
                AssertEqual(ToolEffectEvidence.VerifiedChange,
                    closed.Evidence.Effect,
                    "native Task List close verifies its terminal revision");
                AssertTrue(string.IsNullOrWhiteSpace(
                    session.ActiveTaskListArtifactId),
                    "native close verifies the cleared active pointer");
                AssertTrue(runtime.Describe(new ToolCall("wrong-case",
                    "COMMON.TASK_LIST_CREATE", "{}")) == null,
                    "native Task List ownership has no case alias");
            });
        }
    }
}
