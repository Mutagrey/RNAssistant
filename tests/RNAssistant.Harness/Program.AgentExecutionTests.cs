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
        private static void ChatProseGreetingRequiresStrictRepair()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var photographedResponse =
                    "```rnassistant-agent\n" +
                    "{\"USER_REQUEST\":\"Привет\",\"ROUTE\":{\"app\":\"Excel\",\"mode\":\"answer\",\"requiresTool\":false}," +
                    "\"AVAILABLE_TOOLS\":[],\"plan\":{\"steps\":[],\"response\":\"Здравствуйте! Чем могу помочь?\"}}\n" +
                    "```";
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    calls,
                    RawResponse(photographedResponse),
                    FinalBlock("Здравствуйте! Чем могу помочь?"));

                var result = service.ExecuteAsync(
                    "Привет",
                    NewSession(adapter),
                    NewContext(adapter),
                    new AppSettings { ContextCharLimit = 8000 },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Здравствуйте! Чем могу помочь?", result.AssistantText, "repaired greeting answer");
                AssertEqual(2, calls.Count, "prose greeting invokes strict repair");
                AssertEqual(0, adapter.Executed.Count, "prose greeting executes no tools");
            });
        }

        private static void ChatGeneralAnswerSkipsOfficeReadsAndTools()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var service = ChatServiceWithResponses(adapter, executor, calls, FinalBlock("Таблица — это данные в строках и столбцах."));

                var result = service.ExecuteAsync(
                    "Что такое таблица?",
                    NewSession(adapter),
                    NewContext(adapter),
                    new AppSettings { ContextCharLimit = 8000 },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertContains(result.AssistantText, "строках", "general answer");
                AssertEqual(0, adapter.DocumentSnapshotReadCount, "document snapshot reads");
                AssertEqual(0, adapter.Executed.Count, "executed tools");
                AssertContains(FlattenMessages(calls[0]), "requiresTool: false", "answer route");
                AssertTrue(FlattenMessages(calls[0]).IndexOf("excel.read_range", StringComparison.OrdinalIgnoreCase) < 0, "tool catalog is empty");
            });
        }

        private static void ChatRoutingAvoidsSubstringFalsePositives()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var buildCalls = new List<IReadOnlyList<ChatMessage>>();
                var buildService = ChatServiceWithResponses(
                    adapter,
                    executor,
                    buildCalls,
                    AgentBlock(Command("excel.add_sheet", "name", "Report")),
                    FinalBlock("Done."));

                buildService.ExecuteAsync(
                    "Build a sales report.",
                    NewSession(adapter),
                    NewContext(adapter),
                    new AppSettings { ContextCharLimit = 8000, AutoConfirmToolActions = true, RequireVerificationForMutations = false },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertContains(FlattenMessages(buildCalls[0]), "taskType: content", "build routes to Office content");
                AssertTrue(FlattenMessages(buildCalls[0]).IndexOf("HTML MODE IS ENABLED", StringComparison.OrdinalIgnoreCase) < 0, "build does not match ui substring");
                AssertEqual("excel.add_sheet", adapter.Executed[0].ToolId, "build report tool");
            });

            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var service = ChatServiceWithResponses(adapter, executor, calls, FinalBlock("Explanation."));

                service.ExecuteAsync(
                    "Clearly explain address notation.",
                    NewSession(adapter),
                    NewContext(adapter),
                    new AppSettings { ContextCharLimit = 8000 },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertContains(FlattenMessages(calls[0]), "requiresTool: false", "clear/add substrings do not route as actions");
                AssertEqual(0, adapter.Executed.Count, "false positive route executes no tool");
            });
        }

        private static void ChatCurrentDocumentQuestionUsesReadTool()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    calls,
                    AgentBlock(Command("excel.get_context")),
                    FinalBlock("В таблице есть данные."));

                var result = service.ExecuteAsync(
                    "Что в текущей таблице?",
                    NewSession(adapter),
                    NewContext(adapter),
                    new AppSettings { ContextCharLimit = 8000 },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertContains(FlattenMessages(calls[0]), "requiresTool: true", "current document question requires read");
                AssertEqual(1, adapter.Executed.Count, "current document read executed");
                AssertEqual("excel.get_context", adapter.Executed[0].ToolId, "current document read tool");
                AssertContains(result.AssistantText, "данные", "current document final answer");
            });
        }

        private static void ChatExecutesTypicalHostTasks()
        {
            var scenarios = new[]
            {
                new HostTaskScenario
                {
                    Host = "Excel",
                    UserText = "Create a sales report sheet and chart.",
                    Responses = new[]
                    {
                        AgentBlock(Command("excel.add_sheet", "name", "Report")),
                        AgentBlock(Command("excel.write_table", "sheet", "Report", "startAddress", "A1", "values", "[[\"Month\",\"Sales\"],[\"Jan\",10]]")),
                        AgentBlock(Command("excel.add_chart", "sheet", "Report", "sourceRange", "A1:B2", "chartType", "column", "title", "Sales"))
                    },
                    ExpectedTools = new[] { "excel.add_sheet", "excel.write_table", "excel.add_chart" }
                },
                new HostTaskScenario
                {
                    Host = "Word",
                    UserText = "Insert an executive summary and add a review comment.",
                    Responses = new[]
                    {
                        AgentBlock(Command("word.insert_text", "text", "Executive summary")),
                        AgentBlock(Command("word.add_comment", "text", "Review this paragraph."))
                    },
                    ExpectedTools = new[] { "word.insert_text", "word.add_comment" }
                },
                new HostTaskScenario
                {
                    Host = "PowerPoint",
                    UserText = "Add a quarterly summary slide.",
                    Responses = new[]
                    {
                        AgentBlock(Command("powerpoint.add_slide", "title", "Q1 Summary", "body", "Revenue grew."))
                    },
                    ExpectedTools = new[] { "powerpoint.add_slide" }
                },
                new HostTaskScenario
                {
                    Host = "Outlook",
                    UserText = "Read the selected email and draft a reply.",
                    Responses = new[]
                    {
                        AgentBlock(Command("outlook.read_selection", "maxChars", "12000")),
                        AgentBlock(Command("outlook.create_reply_draft", "body", "Thanks, I will follow up."))
                    },
                    ExpectedTools = new[] { "outlook.read_selection", "outlook.create_reply_draft" }
                }
            };

            for (var i = 0; i < scenarios.Length; i++)
            {
                    var scenario = scenarios[i];
                WithTempExecutor(FakeOfficeAdapter.ForHost(scenario.Host), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var calls = new List<IReadOnlyList<ChatMessage>>();
                    var responses = new List<string>(scenario.Responses ?? new string[0]) { "Done." };
                    var service = ChatServiceWithResponses(adapter, executor, calls, responses.ToArray());
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
                    AssertEqual(0, adapter.DocumentSnapshotReadCount, scenario.Host + " eager snapshot reads");
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

        private static void ChatBuiltInMutationFollowsSafetyMetadata()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    calls,
                    AgentBlock(Command("excel.write_range", "address", "A1", "value", "Ready")),
                    FinalBlock("Done."));

                var result = service.ExecuteAsync(
                    "Write Ready to A1.",
                    NewSession(adapter),
                    NewContext(adapter),
                    new AppSettings
                    {
                        AutoConfirmToolActions = false,
                        RequireVerificationForMutations = false
                    },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Done.", result.AssistantText, "built-in mutation final answer");
                AssertEqual(1, adapter.Executed.Count, "built-in mutation execution count");
                AssertEqual("excel.write_range", adapter.Executed[0].ToolId, "built-in mutation tool");
            });
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
                session.Messages.Add(new ChatMessage { Role = "user", Content = "Earlier tool context" });
                session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Earlier tool answer" });
                var attachment = new ChatAttachment
                {
                    FileName = "instruction.txt",
                    Kind = "text",
                    ExtractedText = "ATTACHMENT_CORRECTION_SENTINEL"
                };

                var result = service.ExecuteAsync(
                    "Create a new sheet named Report.",
                    session,
                    NewContext(adapter),
                    new AppSettings { ContextCharLimit = 8000, AutoConfirmToolActions = true, RequireVerificationForMutations = false },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    new[] { attachment },
                    null).GetAwaiter().GetResult();

                AssertEqual("Done.", result.AssistantText, "assistant text");
                AssertEqual(3, calls.Count, "llm call count");
                AssertTrue(ContainsMessage(calls[1], "requires Office tool use"), "forced follow-up prompt");
                AssertTrue(ContainsMessage(calls[1], "Earlier tool context"), "forced follow-up keeps history");
                AssertEqual(1, calls[1].Sum(message => message.Attachments.Count(item => item.FileName == "instruction.txt")), "forced follow-up keeps current attachment");
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
                AssertTrue(ContainsMessage(calls[1], "Create a new sheet named Report."), "repair keeps original request");
                AssertTrue(ContainsMessage(calls[1], "excel.add_sheet"), "repair keeps available tools");
                AssertEqual(1, adapter.Executed.Count, "adapter execution count");
                AssertEqual("excel.add_sheet", adapter.Executed[0].ToolId, "executed tool");
            });
        }

        private static void ChatRepairThenFinalStillForcesTool()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    calls,
                    RawResponse("{broken"),
                    FinalBlock("I will do it without a tool."),
                    AgentBlock(Command("excel.add_sheet", "name", "Report")),
                    FinalBlock("Done."));

                var result = service.ExecuteAsync(
                    "Create a new sheet named Report.",
                    NewSession(adapter),
                    NewContext(adapter),
                    new AppSettings { ContextCharLimit = 8000, AutoConfirmToolActions = true, RequireVerificationForMutations = false },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual(4, calls.Count, "repair then correction call count");
                AssertTrue(ContainsMessage(calls[1], "previous RNAssistant planner output was invalid"), "format repair requested");
                AssertTrue(ContainsMessage(calls[2], "requires Office tool use"), "tool correction requested after repair");
                AssertEqual(1, adapter.Executed.Count, "tool executed after repair and correction");
                AssertEqual("excel.add_sheet", adapter.Executed[0].ToolId, "corrected tool id");
                AssertEqual("Done.", result.AssistantText, "final answer");
            });
        }

        private static void ChatInvalidToolCorrectionDoesNotFallbackToFinal()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var session = NewSession(adapter);
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    calls,
                    FinalBlock("I can do that without tools."),
                    RawResponse("invalid correction"),
                    RawResponse("invalid correction repair"));

                var result = service.ExecuteAsync(
                    "Create a new sheet named Report.",
                    session,
                    NewContext(adapter),
                    new AppSettings { ContextCharLimit = 8000, AutoConfirmToolActions = true, RequireVerificationForMutations = false },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual(3, calls.Count, "invalid correction repair call count");
                AssertEqual(0, adapter.Executed.Count, "invalid correction executes no tools");
                AssertContains(result.AssistantText, "not_json_object", "invalid correction result");
                AssertEqual("Planner correction invalid", session.Messages.Last().Activity.Title, "correction diagnostic");
            });
        }

        private static void ChatRepeatedFinalForRequiredToolFailsClosed()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = NewSession(adapter);
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    null,
                    FinalBlock("No tool needed."),
                    FinalBlock("Still no tool needed."));

                var result = service.ExecuteAsync(
                    "Create a new sheet named Report.",
                    session,
                    NewContext(adapter),
                    new AppSettings { ContextCharLimit = 8000, AutoConfirmToolActions = true, RequireVerificationForMutations = false },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual(0, adapter.Executed.Count, "repeated final executes no tools");
                AssertContains(result.AssistantText, "required_tool_plan", "required tool quality error");
                AssertEqual("Planner tool use required", session.Messages.Last().Activity.Title, "quality diagnostic");
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
        }
    }
}
