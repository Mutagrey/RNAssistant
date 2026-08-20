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
        private static void PlanCrudCreatesRevisionsAndRewindsCleanly()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = NewSession(adapter);
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                AssertTrue(tools.Any(tool => tool.Id == PlanToolExecutor.CreateToolId), "plan create exposed to Agent");
                AssertTrue(tools.Any(tool => tool.Id == PlanToolExecutor.ReadToolId), "plan read exposed to Agent");
                AssertTrue(tools.Any(tool => tool.Id == PlanToolExecutor.UpdateToolId), "plan update exposed to Agent");
                AssertTrue(tools.Any(tool => tool.Id == PlanToolExecutor.DeleteToolId), "plan delete exposed to Agent");
                var planningSkill = BuiltInSkillProvider.GetSkills(adapter).Single(skill => skill.Id == "common.task_planning");
                AssertContains(planningSkill.BodyMarkdown, PlanToolExecutor.CreateToolId, "planning skill explains create");
                AssertContains(planningSkill.BodyMarkdown, PlanToolExecutor.UpdateToolId, "planning skill explains update");
                var create = Command(
                    PlanToolExecutor.CreateToolId,
                    "goal", "Prepare workbook report",
                    "steps", new JArray(
                        new JObject { ["id"] = "inspect", ["text"] = "Inspect source data", ["status"] = "in_progress" },
                        new JObject { ["id"] = "write", ["text"] = "Write the report", ["status"] = "pending" }));

                var created = executor.Execute(create, tools, new AppSettings(), false, false, session);
                AssertTrue(created.Success, "plan create succeeds");
                var createdData = JObject.Parse(created.DataJson);
                var planId = (string)createdData["plan"]["id"];
                var firstArtifactId = (string)createdData["artifactId"];
                AssertTrue(!string.IsNullOrWhiteSpace(planId), "stable plan id returned");
                AssertEqual(firstArtifactId, session.ActivePlanArtifactId, "created plan becomes active");
                AssertEqual(1, session.Artifacts.Count(item => item.Kind == ChatArtifactKinds.Plan), "first plan revision stored");

                var createMessage = AgentTranscript.CreateLocalResultMessage(create, created);
                session.Messages.Add(createMessage);
                ChatArtifactService.LinkMessageArtifacts(session, 0);
                AssertTrue(createMessage.ArtifactIds.Contains(firstArtifactId), "created plan linked to tool result message");

                var update = Command(PlanToolExecutor.UpdateToolId, "id", planId, "goal", "Prepare verified workbook report");
                var updated = executor.Execute(update, tools, new AppSettings(), false, false, session);
                AssertTrue(updated.Success, "partial plan update succeeds");
                var updatedData = JObject.Parse(updated.DataJson);
                var secondArtifactId = (string)updatedData["artifactId"];
                AssertEqual(2L, (long)updatedData["revision"], "plan revision increments");
                AssertEqual(2, updatedData["plan"]["steps"].Count(), "omitted steps preserved");
                AssertEqual(firstArtifactId, session.Artifacts.Single(item => item.Id == secondArtifactId).ParentArtifactId, "revision parent linked");
                AssertEqual(secondArtifactId, session.ActivePlanArtifactId, "updated revision becomes active");

                var updateMessage = AgentTranscript.CreateLocalResultMessage(update, updated);
                session.Messages.Add(updateMessage);
                ChatArtifactService.LinkMessageArtifacts(session, 1);
                AssertTrue(updateMessage.ArtifactIds.Contains(secondArtifactId), "updated plan linked to tool result message");

                var read = executor.Execute(Command(PlanToolExecutor.ReadToolId), tools, new AppSettings(), false, false, session);
                AssertTrue(read.Success, "active plan read succeeds");
                AssertContains(read.DataJson, "Prepare verified workbook report", "active revision returned");

                session.Messages.Remove(updateMessage);
                ChatArtifactService.RestoreActivePlanFromMessages(session);
                ChatArtifactService.PruneUnreachable(session);
                AssertEqual(firstArtifactId, session.ActivePlanArtifactId, "history rewind restores prior plan revision");
                AssertTrue(session.Artifacts.All(item => item.Id != secondArtifactId), "future plan revision pruned");

                var deleted = executor.Execute(Command(PlanToolExecutor.DeleteToolId, "id", planId), tools, new AppSettings(), false, false, session);
                AssertTrue(deleted.Success, "plan delete succeeds");
                AssertEqual(0, session.Artifacts.Count(item => item.Kind == ChatArtifactKinds.Plan), "all plan revisions deleted");
                AssertTrue(string.IsNullOrWhiteSpace(session.ActivePlanArtifactId), "deleted plan is no longer active");
                AssertTrue(!createMessage.ArtifactIds.Contains(firstArtifactId), "deleted plan unlinked from messages");
            });
        }

        private static void PlanCrudRejectsAmbiguousSteps()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = NewSession(adapter);
                var result = executor.Execute(
                    Command(
                        PlanToolExecutor.CreateToolId,
                        "goal", "Invalid duplicate plan",
                        "steps", new JArray(
                            new JObject { ["id"] = "same", ["text"] = "First" },
                            new JObject { ["id"] = "same", ["text"] = "Second" })),
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(),
                    new AppSettings(),
                    false,
                    false,
                    session);

                AssertTrue(!result.Success, "duplicate plan step ids rejected");
                AssertContains(result.Message, "Duplicate plan step id", "duplicate plan diagnostic");
                AssertEqual(0, session.Artifacts.Count, "invalid plan not stored");
            });
        }
    }
}
