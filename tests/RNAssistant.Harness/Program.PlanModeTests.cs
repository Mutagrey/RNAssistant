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
        private static void PlanModeFiltersMutationsAndKeepsPlanningTools()
        {
            AssertEqual(ChatModes.Plan, ChatModes.Normalize("PLAN"), "plan mode normalizes");
            AssertContains(ConversationPromptComposer.BuildInstruction(ChatModes.Plan, new AppSettings()),
                "common.capabilities_read", "Plan includes progressive capability policy");
            AssertContains(ConversationPromptComposer.BuildInstruction(ChatModes.Plan, new AppSettings()),
                "Never substitute chat prose or an HTML workspace", "Plan requires the Markdown artifact");
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var selected = ConversationRunPolicy.For(ChatModes.Plan).SelectTools(tools);
                AssertTrue(selected.Any(item => item.Id == PlanDocumentToolExecutor.CreateToolId), "plan create available");
                AssertTrue(selected.Any(item => item.Id == TaskListToolExecutor.CreateToolId), "task list available");
                AssertTrue(selected.Any(item => item.Id == UserQuestionToolExecutor.AskToolId), "questions available");
                AssertTrue(selected.Any(item => item.Id == ResourceToolCatalog.ReadToolId), "resource read available");
                AssertTrue(selected.All(item => !item.MutatesDocument), "document mutations excluded");
                AssertTrue(!ConversationRunPolicy.For(ChatModes.Plan).AllowsConfirmation, "Plan cannot confirm mutations");
            });
        }

        private static void PlanModePersistsMarkdownAndAwaitsAnswers()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = NewSession(adapter);
                session.Mode = ChatModes.Plan;
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var created = executor.Execute(Command(PlanDocumentToolExecutor.CreateToolId,
                    "title", "Migration plan", "markdown", "# Goal\n\nShip safely.", "status", "draft"),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(created.Success, "plan document created");
                var data = JObject.Parse(created.DataJson);
                var planId = (string)data["planId"];
                var revisionId = (string)data["artifactId"];
                var createMessage = AgentTranscript.CreateLocalResultMessage(
                    Command(PlanDocumentToolExecutor.CreateToolId), created);
                session.Messages.Add(createMessage);
                ChatResourceReferenceService.LinkMessageResources(session, 0);
                var stale = executor.Execute(Command(PlanDocumentToolExecutor.UpdateToolId,
                    "id", planId, "expectedRevisionArtifactId", "stale", "markdown", "# Changed", "status", "ready"),
                    tools, new AppSettings(), false, false, session);
                AssertEqual("stale_plan_revision", stale.ErrorCode, "stale revision rejected");
                var updated = executor.Execute(Command(PlanDocumentToolExecutor.UpdateToolId,
                    "id", planId, "expectedRevisionArtifactId", revisionId, "markdown", "# Ready\n\nExecute.", "status", "ready"),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(updated.Success, "guarded revision succeeds");
                AssertContains(ConversationPromptComposer.BuildRuntimeContext(ChatModes.Plan, adapter, tools, null, null, session),
                    "revision_uri", "active plan URI enters runtime context");
                var updateMessage = AgentTranscript.CreateLocalResultMessage(
                    Command(PlanDocumentToolExecutor.UpdateToolId), updated);
                session.Messages.Add(updateMessage);
                ChatResourceReferenceService.LinkMessageResources(session, 1);
                session.Messages.Remove(updateMessage);
                ChatResourceReferenceService.RestoreActivePlanDocumentFromMessages(session);
                ChatResourceReferenceService.PruneUnreachable(session);
                AssertEqual(revisionId, session.ActivePlanDocumentArtifactId, "history rewind restores prior plan revision");

                var question = executor.Execute(Command(UserQuestionToolExecutor.AskToolId, "questions", new JArray(
                    new JObject
                    {
                        ["id"] = "scope", ["header"] = "Scope", ["prompt"] = "Choose scope", ["selection"] = "multiple",
                        ["options"] = new JArray(
                            new JObject { ["id"] = "core", ["label"] = "Core", ["description"] = "Core only" },
                            new JObject { ["id"] = "ui", ["label"] = "UI", ["description"] = "Include UI" })
                    })), tools, new AppSettings(), false, false, session);
                AssertTrue(question.Status == "awaiting_user", "question pauses for user input: " + question.Message);
            });
        }
    }
}
