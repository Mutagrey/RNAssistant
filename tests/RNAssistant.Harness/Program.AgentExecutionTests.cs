using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Storage;
using RNAssistant.Office;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Office.WebView;
using RNAssistant.Desktop;
using RNAssistant.OfficeHosts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ChatExecutesTypicalHostTasks()
        {
            var scenarios = new[]
            {
                new HostTaskScenario
                {
                    Host = "Excel",
                    UserText = "Create a sales report sheet and chart.",
                    Response = AgentBlock(
                        Command("excel.add_sheet", "name", "Report"),
                        Command("excel.write_table", "sheet", "Report", "startAddress", "A1", "values", "[[\"Month\",\"Sales\"],[\"Jan\",10]]"),
                        Command("excel.add_chart", "sheet", "Report", "sourceRange", "A1:B2", "chartType", "column", "title", "Sales")),
                    ExpectedTools = new[] { "excel.add_sheet", "excel.write_table", "excel.add_chart" }
                },
                new HostTaskScenario
                {
                    Host = "Word",
                    UserText = "Insert an executive summary and add a review comment.",
                    Response = AgentBlock(
                        Command("word.insert_text", "text", "Executive summary"),
                        Command("word.add_comment", "text", "Review this paragraph.")),
                    ExpectedTools = new[] { "word.insert_text", "word.add_comment" }
                },
                new HostTaskScenario
                {
                    Host = "PowerPoint",
                    UserText = "Add a quarterly summary slide.",
                    Response = AgentBlock(
                        Command("powerpoint.add_slide", "title", "Q1 Summary", "body", "Revenue grew.")),
                    ExpectedTools = new[] { "powerpoint.add_slide" }
                },
                new HostTaskScenario
                {
                    Host = "Outlook",
                    UserText = "Read the selected email and draft a reply.",
                    Response = AgentBlock(
                        Command("outlook.read_selection", "maxChars", "12000"),
                        Command("outlook.draft_reply", "body", "Thanks, I will follow up.")),
                    ExpectedTools = new[] { "outlook.read_selection", "outlook.draft_reply" }
                }
            };

            for (var i = 0; i < scenarios.Length; i++)
            {
                var scenario = scenarios[i];
                WithTempExecutor(FakeOfficeAdapter.ForHost(scenario.Host), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var calls = new List<IReadOnlyList<ChatMessage>>();
                    var service = ChatServiceWithResponses(adapter, executor, calls, scenario.Response, "Done.");
                    var session = NewSession(adapter);
                    var context = NewContext(adapter);
                    context.Notes.Add(new ContextNote { Host = adapter.HostName, Kind = "selection", Title = "Pinned", Reference = "ref", Text = "Pinned context" });

                    var result = service.ExecuteAsync(
                        scenario.UserText,
                        session,
                        context,
                        new AppSettings { ContextCharLimit = 8000, AutoConfirmToolActions = true, RequireVerificationForMutations = false },
                        new List<ToolDefinition>(adapter.GetBuiltInTools()),
                        null).GetAwaiter().GetResult();

                    AssertEqual("Done.", result.AssistantText, scenario.Host + " assistant text");
                    AssertEqual(scenario.ExpectedTools.Length, adapter.Executed.Count, scenario.Host + " executed count");
                    for (var toolIndex = 0; toolIndex < scenario.ExpectedTools.Length; toolIndex++)
                    {
                        AssertEqual(scenario.ExpectedTools[toolIndex], adapter.Executed[toolIndex].ToolId, scenario.Host + " tool " + toolIndex);
                    }
                    AssertTrue(ContainsMessage(calls[0], "User-added context"), scenario.Host + " context prompt");
                    AssertTrue(ContainsMessage(session.Messages, "Agent plan"), scenario.Host + " plan recorded");
                    AssertTrue(ContainsMessage(session.Messages, "Agent step"), scenario.Host + " result recorded");
                });
            }
        }

        private static void ChatProseActionForcesToolFollowUp()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    calls,
                    "Sure, I can do that.",
                    AgentBlock(Command("excel.add_sheet", "name", "Report")),
                    "Done.");
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "Create a new sheet named Report.",
                    session,
                    NewContext(adapter),
                    new AppSettings { ContextCharLimit = 8000, AutoConfirmToolActions = true, RequireVerificationForMutations = false },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Done.", result.AssistantText, "assistant text");
                AssertEqual(3, calls.Count, "llm call count");
                AssertTrue(ContainsMessage(calls[1], "requires Office tool use"), "forced follow-up prompt");
                AssertEqual(1, adapter.Executed.Count, "adapter execution count");
                AssertEqual("excel.add_sheet", adapter.Executed[0].ToolId, "executed tool");
            });
        }

        private static void ChatMalformedActionResponseForcesRepair()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    calls,
                    RawResponse("```rnassistant-agent\n{\"steps\":[\n```"),
                    AgentBlock(Command("excel.add_sheet", "name", "Report")),
                    "Done.");
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "Create a new sheet named Report.",
                    session,
                    NewContext(adapter),
                    new AppSettings { ContextCharLimit = 8000, AutoConfirmToolActions = true, RequireVerificationForMutations = false },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Done.", result.AssistantText, "assistant text");
                AssertEqual(3, calls.Count, "llm call count");
                AssertTrue(ContainsMessage(calls[1], "previous RNAssistant planner output was invalid"), "repair prompt");
                AssertEqual(1, adapter.Executed.Count, "adapter execution count");
                AssertEqual("excel.add_sheet", adapter.Executed[0].ToolId, "executed tool");
            });
        }

        private static void ChatUsesEditableAgentFollowUpPrompt()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    calls,
                    "Sure, I can do that.",
                    AgentBlock(Command("excel.add_sheet", "name", "Report")),
                    "Done.");
                var session = NewSession(adapter);
                var settings = new AppSettings { ContextCharLimit = 8000, AutoConfirmToolActions = true, RequireVerificationForMutations = false };
                settings.AgentPrompts.ForceToolUsePrompt = "CUSTOM_FORCE_TOOL_PROMPT";

                var result = service.ExecuteAsync(
                    "Create a new sheet named Report.",
                    session,
                    NewContext(adapter),
                    settings,
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Done.", result.AssistantText, "assistant text");
                AssertContains(FlattenMessages(calls[1]), "CUSTOM_FORCE_TOOL_PROMPT", "custom force tool prompt");
            });
        }

        private static void AgentTranscriptCreatesActivityTree()
        {
            var plan = AgentTranscript.CreateAgentPlanActivity(new[] { Command("excel.add_sheet", "name", "Report") });
            AssertEqual("plan", plan.Kind, "plan kind");
            AssertEqual(1, plan.Children.Count, "plan child count");
            AssertEqual("excel.add_sheet", plan.Children[0].ToolId, "plan child tool");
            AssertContains(plan.Children[0].ArgumentsJson, "Report", "plan child args");

            var command = new ToolCommand { ToolId = "excel.make_report" };
            command.Arguments["sheet"] = "Report";
            var result = ToolResult.Ok(
                "Pipeline executed: excel.make_report",
                "{\"toolId\":\"excel.make_report\",\"steps\":[{\"id\":\"sheet\",\"toolId\":\"excel.add_sheet\",\"success\":true,\"message\":\"Added sheet\"},{\"id\":\"table\",\"toolId\":\"excel.write_table\",\"success\":false,\"message\":\"No table\"}]}");
            var activity = AgentTranscript.CreateToolActivity(command, result, "tool");

            AssertEqual("completed", activity.Status, "pipeline parent status");
            AssertEqual(2, activity.Children.Count, "pipeline child count");
            AssertEqual("sheet", activity.Children[0].Title, "first child title");
            AssertEqual("completed", activity.Children[0].Status, "first child status");
            AssertEqual("failed", activity.Children[1].Status, "second child status");

            var session = new ChatSession();
            AgentTranscript.AddLocalResultMessage(session, command, result);
            AssertEqual(1, session.Messages.Count, "transcript message count");
            AssertTrue(session.Messages[0].Activity != null, "transcript activity exists");
            AssertContains(session.Messages[0].Content, "Agent step", "fallback content");
            var promptMessages = PromptMessageBuilder.Build("system", string.Empty, session.Messages, 8000);
            AssertContains(promptMessages.Last().Content, "Agent activity summary", "prompt activity marker");
            AssertContains(promptMessages.Last().Content, "excel.make_report", "prompt activity data");
            AssertTrue(promptMessages.Last().Content.IndexOf("arguments:", StringComparison.OrdinalIgnoreCase) < 0, "prompt omits arguments");
        }
    }
}
