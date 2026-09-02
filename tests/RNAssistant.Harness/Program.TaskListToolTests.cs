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
                AssertTrue(tools.Any(tool => tool.Id == TaskListToolCatalog.SetToolId), "task-list semantic set exposed to Agent");
                AssertTrue(NativeToolRuntimeAdapter.Owns(
                    TaskListToolCatalog.SetToolId),
                    "task-list tools use the native ToolRuntime");
                var taskPolicy = tools.Single(tool =>
                    tool.Id == TaskListToolCatalog.SetToolId).Policy;
                AssertTrue(taskPolicy != null &&
                    taskPolicy.Effect == ToolEffect.Write &&
                    taskPolicy.Verification == ToolVerification.Tool &&
                    !taskPolicy.RequiresConfirmation &&
                    taskPolicy.AllowedModes.SequenceEqual(
                        new[] { "agent", "plan" }),
                    "task-list carries exact source-owned mode policy");
                var legacyPlanIds = new[] { "common.plan_create", "common.plan_update", "common.plan_delete", "common.plan_read" };
                AssertTrue(tools.All(tool => !legacyPlanIds.Contains(tool.Id, StringComparer.OrdinalIgnoreCase)), "legacy plan tools are removed");
                AssertTrue(tools.All(tool => tool.Id != "common.task_list_create" &&
                    tool.Id != "common.task_list_update" &&
                    tool.Id != "common.task_list_close"),
                    "replaced Task List lifecycle ids are absent");
                var planningSkill = BuiltInSkillProvider.GetSkills(adapter).Single(skill => skill.Id == "common.task_tracking");
                AssertContains(planningSkill.BodyMarkdown, TaskListToolCatalog.SetToolId, "tracking skill explains semantic set");
                string contractError;
                AssertTrue(!ModelToolResultProjection.ValidateAcceptedCall(
                    new ToolCall("old-task-list", "common.task_list_update", "{}"),
                    out contractError),
                    "pre-11O2 Task List calls require an explicit new chat/reset");
                var create = Command(
                    TaskListToolCatalog.SetToolId,
                    "action", "save",
                    "goal", "Prepare workbook report",
                    "steps", new JArray(
                        new JObject { ["text"] = "Inspect source data", ["status"] = "in_progress" },
                        new JObject { ["text"] = "Write the report", ["status"] = "pending" },
                        new JObject { ["text"] = "Verify the report", ["status"] = "pending" }));

                var created = executor.ExecuteManual(create, tools, new AppSettings(), false, false, session);
                AssertTrue(created.Success, "plan create succeeds");
                var createdData = JObject.Parse(created.DataJson);
                var planId = (string)createdData["taskList"]["id"];
                var firstArtifactId = (string)createdData["artifactId"];
                var firstStepIds = createdData["taskList"]["steps"]
                    .Select(step => (string)step["id"]).ToArray();
                AssertTrue(!string.IsNullOrWhiteSpace(planId), "stable plan id returned");
                AssertTrue(firstStepIds.All(id => !string.IsNullOrWhiteSpace(id)),
                    "runtime generates every stable step id");
                AssertEqual(firstArtifactId, session.ActiveTaskListArtifactId, "created task list becomes active");
                AssertEqual(1, session.Artifacts.Count(item => item.Kind == ChatArtifactKinds.TaskList), "first task-list revision stored");

                var createMessage = AgentTranscript.CreateLocalResultMessage(create, created);
                session.Messages.Add(createMessage);
                ChatResourceReferenceService.LinkMessageResources(session, 0);
                AssertTrue(ReferencesArtifact(session, createMessage, firstArtifactId), "created plan linked to tool result message");
                var projectionCommand = Command(TaskListToolCatalog.SetToolId);
                projectionCommand.ToolCallId = "task-list-projection";
                var projectedCreate = ModelToolResultProjection.Project(
                    AgentJsonProtocol.CreateToolResultMessage(
                        projectionCommand,
                        RNAssistant.Core.Tools.Contracts.ToolResult.Ok(
                            created.Message, created.DataJson)));
                AssertTrue(projectedCreate.Content.IndexOf("artifactId", StringComparison.Ordinal) < 0 &&
                    projectedCreate.Content.IndexOf(planId, StringComparison.Ordinal) < 0 &&
                    firstStepIds.All(id => projectedCreate.Content.IndexOf(id, StringComparison.Ordinal) < 0) &&
                    projectedCreate.Content.IndexOf("Prepare workbook report", StringComparison.Ordinal) >= 0,
                    "model Task List result omits runtime list, artifact, revision, and step ids");

                var update = Command(TaskListToolCatalog.SetToolId,
                    "action", "save",
                    "goal", "Prepare verified workbook report",
                    "steps", new JArray(
                        new JObject { ["text"] = "Inspect source data", ["status"] = "completed" },
                        new JObject { ["text"] = "Write the report", ["status"] = "in_progress" },
                        new JObject { ["text"] = "Verify the report", ["status"] = "pending" }));
                var updated = executor.ExecuteManual(update, tools, new AppSettings(), false, false, session);
                AssertTrue(updated.Success, "complete semantic task-list save succeeds");
                var updatedData = JObject.Parse(updated.DataJson);
                var secondArtifactId = (string)updatedData["artifactId"];
                AssertEqual(2L, (long)updatedData["revision"], "plan revision increments");
                AssertTrue(firstStepIds.SequenceEqual(updatedData["taskList"]["steps"]
                    .Select(step => (string)step["id"])),
                    "runtime preserves step identity across semantic saves");
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
                    new JObject { ["text"] = "Inspect source data", ["status"] = "completed" },
                    new JObject { ["text"] = "Write the report", ["status"] = "completed" },
                    new JObject { ["text"] = "Verify the report", ["status"] = "completed" });
                var finalUpdate = executor.ExecuteManual(Command(TaskListToolCatalog.SetToolId,
                    "action", "save", "goal", "Prepare workbook report",
                    "steps", completedSteps), tools, new AppSettings(), false, false, session);
                AssertTrue(finalUpdate.Success, "final task-list update succeeds");
                var closed = executor.ExecuteManual(Command(TaskListToolCatalog.SetToolId,
                    "action", "close", "outcome", "completed"), tools, new AppSettings(), false, false, session);
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
                        TaskListToolCatalog.SetToolId,
                        "action", "save",
                        "goal", "Invalid duplicate plan",
                        "steps", new JArray(
                            new JObject { ["id"] = "caller-owned", ["text"] = "First" },
                            new JObject { ["text"] = "Second" },
                            new JObject { ["text"] = "Third" })),
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(),
                    new AppSettings(),
                    false,
                    false,
                    session);

                AssertTrue(!result.Success, "caller-owned task step id rejected");
                AssertEqual("invalid_arguments", result.ErrorCode,
                    "task-list semantic schema rejects internal identity");
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
                var call = new ToolCall("task-list-native-save",
                    TaskListToolCatalog.SetToolId,
                    "{\"action\":\"save\",\"goal\":\"Native tracking\",\"steps\":[" +
                    "{\"text\":\"First\",\"status\":\"in_progress\"}," +
                    "{\"text\":\"Second\"}," +
                    "{\"text\":\"Third\"}]}");
                var policy = runtime.Describe(call);
                AssertTrue(policy != null && policy.MayHaveSideEffects &&
                    policy.Policy.Verification == ToolVerification.Tool,
                    "Task List set has one exact verified-write registration");

                var created = ExecuteNative(runtime, call, policy);
                AssertEqual(ToolExecutionOutcome.Ok, created.Outcome,
                    "native Task List save creates the missing list");
                AssertEqual(ToolDispatchEvidence.MayHaveDispatched,
                    created.Evidence.Dispatch,
                    "native Task List save records its session mutation boundary");
                AssertEqual(ToolEffectEvidence.VerifiedChange,
                    created.Evidence.Effect,
                    "native Task List save verifies the exact active revision");
                var createdData = JObject.Parse(created.Result.DataJson);
                AssertEqual((string)createdData["artifactId"],
                    session.ActiveTaskListArtifactId,
                    "verified result identifies the exact active Task List artifact");

                var invalidCloseCall = new ToolCall("task-list-native-invalid-close",
                    TaskListToolCatalog.SetToolId,
                    "{\"action\":\"close\",\"outcome\":\"completed\"}");
                var invalidClose = ExecuteNative(runtime, invalidCloseCall,
                    runtime.Describe(invalidCloseCall));
                AssertEqual(ToolExecutionOutcome.Error, invalidClose.Outcome,
                    "non-terminal close stays a known error");
                AssertEqual(ToolDispatchEvidence.NotDispatched,
                    invalidClose.Evidence.Dispatch,
                    "terminal-state rejection occurs before the mutation boundary");

                var artifactCount = session.Artifacts.Count;
                var dryRun = executor.ExecuteManual(Command(
                    TaskListToolCatalog.SetToolId,
                    "action", "save", "goal", "Preview",
                    "steps", new JArray(
                        new JObject { ["text"] = "First" },
                        new JObject { ["text"] = "Second" },
                        new JObject { ["text"] = "Third" })),
                    tools, new AppSettings(),
                    true, true, session);
                AssertTrue(dryRun.Success && session.Artifacts.Count == artifactCount,
                    "native Task List dry-run validates schema without mutation");

                var updateCall = new ToolCall("task-list-native-update",
                    TaskListToolCatalog.SetToolId,
                    "{\"action\":\"save\",\"goal\":\"Native tracking\",\"steps\":[" +
                    "{\"text\":\"First\",\"status\":\"completed\"}," +
                    "{\"text\":\"Second\",\"status\":\"completed\"}," +
                    "{\"text\":\"Third\",\"status\":\"completed\"}]}");
                var updated = ExecuteNative(runtime, updateCall,
                    runtime.Describe(updateCall));
                AssertEqual(ToolEffectEvidence.VerifiedChange,
                    updated.Evidence.Effect,
                    "native Task List update verifies its appended revision");

                var closeCall = new ToolCall("task-list-native-close",
                    TaskListToolCatalog.SetToolId,
                    "{\"action\":\"close\",\"outcome\":\"completed\"}");
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
                    "COMMON.TASK_LIST_SET", "{}")) == null,
                    "native Task List ownership has no case alias");
            });
        }
    }
}
