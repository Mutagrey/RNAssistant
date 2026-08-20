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
                    new AppSettings { FallbackToJsonObject = false },
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
                    new AppSettings(),
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertContains(result.AssistantText, "строках", "general answer");
                AssertEqual(0, adapter.DocumentSnapshotReadCount, "document snapshot reads");
                AssertEqual(0, adapter.Executed.Count, "executed tools");
                AssertContains(FlattenMessages(calls[0]), "requiresTool: false", "answer route");
                AssertContains(FlattenMessages(calls[0]), "excel.read_range", "model can choose a tool when the request actually needs one");
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
                    new AppSettings { AutoConfirmToolActions = true, RequireVerificationForMutations = false, FallbackToJsonObject = false },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertContains(FlattenMessages(buildCalls[0]), "mode: agent", "runtime uses one language-neutral agent route");
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
                    new AppSettings(),
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertContains(FlattenMessages(calls[0]), "requiresTool: false", "runtime does not classify substrings");
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
                    new AppSettings(),
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertContains(FlattenMessages(calls[0]), "requiresTool: false", "model chooses the read without a keyword route");
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
                        new AppSettings { AutoConfirmToolActions = true, RequireVerificationForMutations = false },
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
                    AssertTrue(ContainsMessage(session.Messages, "Run " + scenario.ExpectedTools[0]), scenario.Host + " tool decision recorded");
                    AssertTrue(ContainsMessage(session.Messages, "Agent step"), scenario.Host + " result recorded");
                    var protocol = session.Messages.Where(message => message != null && message.ProtocolMessage).ToList();
                    AssertEqual(scenario.ExpectedTools.Length * 2, protocol.Count, scenario.Host + " persisted protocol pair count");
                    foreach (var toolResult in protocol.Where(message => string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase)))
                    {
                        AssertTrue(protocol.Any(message =>
                            string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) &&
                            message.ToolCalls.Any(call => string.Equals(call.Id, toolResult.ToolCallId, StringComparison.Ordinal))),
                            scenario.Host + " tool result has matching assistant tool call");
                    }
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
                    FinalBlock("Sure, I can do that."));
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
                    new AppSettings { AutoConfirmToolActions = true, RequireVerificationForMutations = false, FallbackToJsonObject = false },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    new[] { attachment },
                    null).GetAwaiter().GetResult();

                AssertEqual("Sure, I can do that.", result.AssistantText, "model terminal response");
                AssertEqual(1, calls.Count, "runtime does not classify action words");
                AssertTrue(ContainsMessage(calls[0], "Earlier tool context"), "request keeps history");
                AssertEqual(1, calls[0].Sum(message => message.Attachments.Count(item => item.FileName == "instruction.txt")), "request keeps current attachment");
                AssertEqual(0, adapter.Executed.Count, "no tool was selected");
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
                    new AppSettings { AutoConfirmToolActions = true, RequireVerificationForMutations = false, FallbackToJsonObject = false },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Done.", result.AssistantText, "assistant text");
                AssertEqual(3, calls.Count, "llm call count");
                AssertTrue(ContainsMessage(calls[1], "Correct only the reported AgentDecision v1 validation error"), "repair prompt");
                AssertTrue(!ContainsMessage(calls[1], "```rnassistant-agent"), "malformed response omitted from repair context");
                AssertTrue(ContainsMessage(calls[1], "Create a new sheet named Report."), "repair keeps original request");
                AssertTrue(ContainsMessage(calls[1], "excel.add_sheet"), "repair keeps available tools");
                AssertEqual(1, adapter.Executed.Count, "adapter execution count");
                AssertEqual("excel.add_sheet", adapter.Executed[0].ToolId, "executed tool");
            });
        }

        private static void ChatRepeatedMalformedResponsesRecoverWithoutReplayPollution()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                const string firstGarbage = "FORMAT_GARBAGE_ONE";
                const string secondGarbage = "FORMAT_GARBAGE_TWO";
                const string laterGarbage = "FORMAT_GARBAGE_LATER";
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    calls,
                    RawResponse(firstGarbage + ": safety proxy prose"),
                    RawResponse("{\"refusal\":\"" + secondGarbage + "\"}"),
                    AgentBlock(Command("excel.get_context")),
                    RawResponse(laterGarbage + ": another non-JSON answer"),
                    FinalBlock("Context read after recovery."));
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "What is in the current workbook?",
                    session,
                    NewContext(adapter),
                    new AppSettings
                    {
                        FallbackToJsonObject = false,
                        MaxAgentFormatRetries = 2
                    },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Context read after recovery.", result.AssistantText, "final response after repeated format recovery");
                AssertEqual(5, calls.Count, "two initial retries and one later-turn retry");
                AssertEqual(1, adapter.Executed.Count, "tool executes once after recovery");
                AssertTrue(!ContainsMessage(calls[1], firstGarbage), "first rejected response omitted from first retry");
                AssertTrue(!ContainsMessage(calls[2], firstGarbage) && !ContainsMessage(calls[2], secondGarbage), "retries rebuild from clean base context");
                AssertTrue(!ContainsMessage(calls[3], firstGarbage) && !ContainsMessage(calls[3], secondGarbage), "rejected responses absent after tool turn");
                AssertTrue(!ContainsMessage(calls[4], laterGarbage), "later rejected response omitted from its retry");

                var diagnostics = session.Messages
                    .Where(message => message != null && message.Activity != null &&
                        (message.Activity.ExecutionStatus ?? string.Empty).StartsWith("format_rejected", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                AssertEqual(3, diagnostics.Count, "all recovered responses remain visible as diagnostics");
                AssertTrue(diagnostics.All(message => message.ExcludeFromModelContext), "recovery diagnostics explicitly excluded from model context");
                var diagnosticJson = JsonConvert.SerializeObject(diagnostics.Select(message => message.Activity));
                AssertContains(diagnosticJson, firstGarbage, "first raw response available in diagnostic details");
                AssertContains(diagnosticJson, secondGarbage, "second raw response available in diagnostic details");
                AssertContains(diagnosticJson, laterGarbage, "later raw response available in diagnostic details");
                AssertTrue(!ContainsMessage(PromptBudgetComposer.ConversationHistory(session), firstGarbage), "diagnostics absent from rebuilt conversation history");
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
                    FinalBlock("I will do it without a tool."));

                var result = service.ExecuteAsync(
                    "Create a new sheet named Report.",
                    NewSession(adapter),
                    NewContext(adapter),
                    new AppSettings { AutoConfirmToolActions = true, RequireVerificationForMutations = false, FallbackToJsonObject = false },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual(2, calls.Count, "format repair call count");
                AssertTrue(ContainsMessage(calls[1], "Correct only the reported AgentDecision v1 validation error"), "format repair requested");
                AssertEqual(0, adapter.Executed.Count, "valid repaired terminal decision executes no tool");
                AssertEqual("I will do it without a tool.", result.AssistantText, "repaired final answer");
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
                    FinalBlock("I can do that without tools."));

                var result = service.ExecuteAsync(
                    "Create a new sheet named Report.",
                    session,
                    NewContext(adapter),
                    new AppSettings { AutoConfirmToolActions = true, RequireVerificationForMutations = false, FallbackToJsonObject = false, MaxAgentFormatRetries = 1 },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual(1, calls.Count, "valid final needs no correction");
                AssertEqual(0, adapter.Executed.Count, "terminal decision executes no tools");
                AssertEqual("I can do that without tools.", result.AssistantText, "terminal response");
                AssertTrue(session.Messages.Last().Activity == null, "no correction diagnostic");
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
                    FinalBlock("No tool needed."));

                var result = service.ExecuteAsync(
                    "Create a new sheet named Report.",
                    session,
                    NewContext(adapter),
                    new AppSettings { AutoConfirmToolActions = true, RequireVerificationForMutations = false },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual(0, adapter.Executed.Count, "repeated final executes no tools");
                AssertEqual("No tool needed.", result.AssistantText, "model owns answer-versus-action semantics");
                AssertTrue(session.Messages.Last().Activity == null, "no lexical routing diagnostic");
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
                    FinalBlock("Not finished."),
                    AgentBlock(Command(
                        "common.html_workspace_upsert_file",
                        "path", "index.html",
                        "kind", "html",
                        "content", "<main>Ready</main>",
                        "setActive", true)),
                    FinalBlock("Done."));
                var session = NewSession(adapter);
                session.HtmlModeEnabled = true;
                var settings = new AppSettings { AutoConfirmToolActions = true, RequireVerificationForMutations = false };
                settings.AgentPrompts.ForceToolUsePrompt = "CUSTOM_FORCE_TOOL_PROMPT";

                var result = service.ExecuteAsync(
                    "Create a new sheet named Report.",
                    session,
                    NewContext(adapter),
                    settings,
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Done.", result.AssistantText, "assistant text");
                AssertEqual(3, calls.Count, "explicit workspace requirement triggers one correction");
                AssertContains(FlattenMessages(calls[1]), "CUSTOM_FORCE_TOOL_PROMPT", "custom force tool prompt");
                AssertTrue(session.HtmlWorkspace.Files.Any(file => file.Path == "index.html"), "corrected tool executes");
            });
        }

        private static void AgentTranscriptCreatesActivityTree()
        {
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

        private static void AgentPlanRuntimeStatusesStayOnCurrentStep()
        {
            var plan = new ChatActivity
            {
                Kind = "plan",
                Title = "Подготовить отчёт",
                Status = "planned"
            };
            plan.Children.Add(new ChatActivity { Kind = "plan_step", Title = "Проверить данные", Subtitle = "inspect", Status = "pending" });
            plan.Children.Add(new ChatActivity { Kind = "plan_step", Title = "Обновить отчёт", Subtitle = "update", Status = "pending" });
            var session = new ChatSession();
            session.Messages.Add(AgentTranscript.CreateAssistantMessage("План готов.", null, plan));
            var state = new AgentRunState();

            AssertTrue(AgentPlanStateService.Restore(session, state) != null, "plan restored");
            AgentPlanStateService.BeginCurrent(session, state);
            AssertEqual("running", plan.Children[0].Status, "first step running");

            AgentPlanStateService.ApplyResult(session, state, ToolResult.Fail("retry", null, "temporary", true), true);
            AssertEqual("running", plan.Children[0].Status, "retry stays on first step");
            AssertEqual("pending", plan.Children[1].Status, "retry does not advance");

            AgentPlanStateService.ApplyResult(session, state, ToolResult.Ok("done"), false);
            AssertEqual("completed", plan.Children[0].Status, "first step completed");
            AgentPlanStateService.BeginCurrent(session, state);
            AssertEqual("running", plan.Children[1].Status, "second step running");

            AgentPlanStateService.ApplyResult(session, state, ToolResult.WaitingConfirmation("confirm"), false);
            AssertEqual("waiting", plan.Children[1].Status, "confirmation keeps current step");
            AssertEqual("waiting", plan.Status, "plan exposes waiting status");

            var snapshot = AgentPlanStateService.Snapshot(plan);
            AssertTrue(!object.ReferenceEquals(plan, snapshot), "progress uses plan snapshot");
            AgentPlanStateService.ApplyLatestResult(session, ToolResult.Cancelled("cancelled"), false);
            AssertEqual("cancelled", plan.Children[1].Status, "cancelled current step");

            var revised = new AgentPlannerResponse
            {
                Kind = AgentResponseKinds.Plan,
                Goal = "Подготовить и опубликовать отчёт",
                DecisionSummary = "Обновляю оставшиеся шаги."
            };
            revised.Plan.Add(new AgentPlanStep { Id = "inspect", Title = "Проверить данные повторно", Status = "pending" });
            revised.Plan.Add(new AgentPlanStep { Id = "publish", Title = "Опубликовать отчёт", Status = "pending" });
            bool updatedExisting;
            AgentPlanStateService.ApplyDecision(session, state, revised, out updatedExisting);
            AssertTrue(!updatedExisting, "repeated plan cannot update existing activity");
            AssertEqual(2, state.Plan.Count, "initial plan steps remain fixed");
            AssertEqual("completed", state.Plan[0].Status, "completed stable id preserved");
            AssertEqual("waiting", state.Plan[1].Status, "existing runtime status is preserved");
            AssertEqual("Подготовить отчёт", plan.Title, "initial goal remains fixed");

            AgentPlanStateService.ApplyTerminalDecision(state, AgentResponseKinds.Final);
            AssertEqual("incomplete", plan.Status, "terminal final does not invent completion evidence");
            AssertEqual("completed", plan.Children[0].Status, "verified completed step stays completed");
            AssertEqual("waiting", plan.Children[1].Status, "unfinished step remains waiting on final");
            AssertEqual("terminal_with_pending_steps", plan.ExecutionStatus, "incomplete terminal plan is explicit");

            var noPlanState = new AgentRunState();
            AssertTrue(AgentPlanStateService.BeginCurrent(session, noPlanState) == null, "fresh tool-only turn does not restore previous plan");
            AssertTrue(noPlanState.PlanActivity == null, "previous plan stays isolated from tool-only turn");

            var freshState = new AgentRunState();
            var unrelated = new AgentPlannerResponse
            {
                Kind = AgentResponseKinds.Plan,
                Goal = "Новая независимая задача",
                DecisionSummary = "Строю новый план."
            };
            unrelated.Plan.Add(new AgentPlanStep { Id = "new_step", Title = "Новый шаг", Status = "pending" });
            AgentPlanStateService.ApplyDecision(session, freshState, unrelated, out updatedExisting);
            AssertTrue(!updatedExisting, "fresh turn does not revise previous completed plan");
            AssertTrue(!object.ReferenceEquals(plan, freshState.PlanActivity), "fresh turn owns a separate plan activity");
            AssertEqual("Новая независимая задача", freshState.PlanActivity.Title, "fresh plan goal is isolated");
        }

        private static void ChatImplementPlanResumesExistingPlan()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = NewSession(adapter);
                var plan = new ChatActivity
                {
                    Kind = "plan",
                    Title = "Проверить текущую книгу",
                    Status = "planned"
                };
                plan.Children.Add(new ChatActivity
                {
                    Kind = "plan_step",
                    Title = "Прочитать книгу",
                    Subtitle = "inspect",
                    Status = "pending"
                });
                session.Messages.Add(AgentTranscript.CreateAssistantMessage("План готов.", null, plan));
                session.PendingAgentTask = new PendingAgentTask
                {
                    Request = "Проверь текущую книгу.",
                    Kind = "incomplete_plan",
                    UpdatedUtc = DateTime.UtcNow
                };

                var calls = new List<IReadOnlyList<ChatMessage>>();
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    calls,
                    AgentBlock(Command("excel.get_context")),
                    FinalBlock("Книга проверена."));

                var result = service.ExecuteAsync(
                    "Реализуй план",
                    session,
                    NewContext(adapter),
                    new AppSettings { FallbackToJsonObject = false },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Книга проверена.", result.AssistantText, "continued plan final response");
                AssertEqual(1, adapter.Executed.Count, "continued plan executes next tool");
                AssertEqual(1, session.Messages.Count(message => message.Activity != null && message.Activity.Kind == "plan"), "continued run reuses existing plan");
                AssertEqual("completed", plan.Children[0].Status, "continued run advances existing step");
                AssertContains(FlattenMessages(calls[0]), "kind=plan is unavailable", "continued run starts with next action instead of a new plan");
                AssertContains(FlattenMessages(calls[0]), "USER_MESSAGE", "continued run retains current-message marker");
            });
        }

        private static void ChatRepeatedPlanWithoutObservationIsCorrected()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var firstPlan = "{\"protocolVersion\":1,\"kind\":\"plan\",\"decisionSummary\":\"Сначала прочитаю книгу.\"," +
                    "\"goal\":\"Проверить текущую книгу\",\"plan\":[{\"id\":\"inspect\",\"title\":\"Прочитать книгу\"}],\"tool\":null,\"message\":null}";
                var rephrasedPlan = "{\"protocolVersion\":1,\"kind\":\"plan\",\"decisionSummary\":\"Уточняю порядок работы.\"," +
                    "\"goal\":\"Изучить текущую книгу\",\"plan\":[{\"id\":\"inspect\",\"title\":\"Изучить содержимое книги\"}],\"tool\":null,\"message\":null}";
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    calls,
                    RawResponse(firstPlan),
                    RawResponse(rephrasedPlan),
                    AgentBlock(Command("excel.get_context")),
                    FinalBlock("Книга проверена."));
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "Проверь текущую книгу.",
                    session,
                    NewContext(adapter),
                    new AppSettings { FallbackToJsonObject = false },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Книга проверена.", result.AssistantText, "run recovers from rephrased plan loop");
                AssertEqual(1, adapter.Executed.Count, "tool executes after plan correction");
                AssertEqual(1, session.Messages.Count(message => message.Activity != null && message.Activity.Kind == "plan"), "only original plan is visible");
                AssertTrue(!session.Messages.Any(message => message.Activity != null && message.Activity.ExecutionStatus == "plan_updated"), "rephrased plan is not shown as an update");
                AssertTrue(!session.Messages.Any(message => message.Activity != null && message.Activity.ExecutionStatus == "repeated_plan_no_progress"), "single violation is recovered locally");
                AssertContains(FlattenMessages(calls[1]), "kind=plan is unavailable", "next turn explicitly forbids another plan");
                AssertContains(FlattenMessages(calls[2]), "Keep the current plan and choose", "ignored schema violation receives bounded correction");
            });
        }

        private static void ChatRepeatedPlanAfterObservationIsRejected()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var firstPlan = "{\"protocolVersion\":1,\"kind\":\"plan\",\"decisionSummary\":\"Сначала прочитаю книгу.\"," +
                    "\"goal\":\"Проверить текущую книгу\",\"plan\":[{\"id\":\"inspect\",\"title\":\"Прочитать книгу\"}],\"tool\":null,\"message\":null}";
                var revisedPlan = "{\"kind\":\"plan\",\"decisionSummary\":\"Уточняю план после анализа.\"," +
                    "\"goal\":\"Проверить и описать текущую книгу\",\"plan\":[{\"id\":\"inspect\",\"action\":\"Прочитать книгу\",\"expected\":\"контекст\"},{\"id\":\"finish\",\"action\":\"Подготовить вывод\"}]}";
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    calls,
                    RawResponse(firstPlan),
                    AgentBlock(Command("excel.get_context")),
                    RawResponse(revisedPlan),
                    FinalBlock("Книга проверена."));
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "Проверь текущую книгу.",
                    session,
                    NewContext(adapter),
                    new AppSettings { FallbackToJsonObject = false },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Книга проверена.", result.AssistantText, "fixed-plan run final response");
                AssertEqual(1, adapter.Executed.Count, "fixed-plan run executes intended tool once");
                var plans = session.Messages.Where(message => message.Activity != null && message.Activity.Kind == "plan").ToList();
                AssertEqual(1, plans.Count, "canonical plan activity remains single");
                AssertEqual("Проверить текущую книгу", plans[0].Activity.Title, "initial goal remains fixed");
                AssertEqual("completed", plans[0].Activity.Status, "executed initial plan completes");
                AssertEqual("completed", plans[0].Activity.Children[0].Status, "executed step remains completed");
                AssertTrue(!session.Messages.Any(message => message.Activity != null && message.Activity.ExecutionStatus == "plan_updated"), "replan is not persisted");
                AssertEqual("Проверить текущую книгу", session.Messages.Last().Goal, "final message retains initial goal");
                AssertContains(FlattenMessages(calls[2]), "unavailable_for_this_run", "new observation does not reopen planning");
                AssertContains(FlattenMessages(calls[3]), "plan_not_allowed", "replan receives bounded format repair");
            });
        }

        private static void ChatUnchangedPlanAfterObservationIsIgnored()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var plan = "{\"protocolVersion\":1,\"kind\":\"plan\",\"decisionSummary\":\"Готовлю отчёт.\"," +
                    "\"goal\":\"Создать лист отчёта\",\"plan\":[{\"id\":\"inspect\",\"title\":\"Проверить книгу\"},{\"id\":\"create\",\"title\":\"Создать лист\"}],\"tool\":null,\"message\":null}";
                var repeated = "{\"protocolVersion\":1,\"kind\":\"plan\",\"decisionSummary\":\"Продолжаю по плану.\"," +
                    "\"goal\":\"Создать лист отчёта\",\"plan\":[{\"id\":\"inspect\",\"title\":\"Проверить книгу ещё раз\"},{\"id\":\"create\",\"title\":\"Создать лист\"}],\"tool\":null,\"message\":null}";
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    calls,
                    RawResponse(plan),
                    AgentBlock(Command("excel.get_context")),
                    RawResponse(repeated),
                    AgentBlock(Command("excel.add_sheet", "name", "Report")),
                    FinalBlock("Лист создан."));
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "Создай новый лист отчёта.",
                    session,
                    NewContext(adapter),
                    new AppSettings
                    {
                        AutoConfirmToolActions = true,
                        RequireVerificationForMutations = false,
                        FallbackToJsonObject = false
                    },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Лист создан.", result.AssistantText, "unchanged replan final response");
                AssertEqual(2, adapter.Executed.Count, "unchanged replan does not consume an Office action");
                AssertEqual("excel.get_context", adapter.Executed[0].ToolId, "initial inspection executes");
                AssertEqual("excel.add_sheet", adapter.Executed[1].ToolId, "mutation follows unchanged plan");
                AssertEqual(1, session.Messages.Count(message => message.Activity != null && message.Activity.Kind == "plan"), "one canonical plan remains visible");
                AssertTrue(!session.Messages.Any(message => message.Activity != null && message.Activity.ExecutionStatus == "plan_updated"), "title-only replan is not persisted");
                AssertContains(FlattenMessages(calls[2]), "unavailable_for_this_run", "plan remains unavailable after observation");
                AssertContains(FlattenMessages(calls[3]), "plan_not_allowed", "unchanged plan is repaired into an action");
            });
        }

        private static void ChatDuplicateSuccessfulReadIsCorrected()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    calls,
                    AgentBlock(Command("excel.get_context")),
                    AgentBlock(Command("excel.get_context")),
                    AgentBlock(Command("excel.add_sheet", "name", "Report")),
                    FinalBlock("Лист создан."));

                var result = service.ExecuteAsync(
                    "Создай новый лист отчёта.",
                    NewSession(adapter),
                    NewContext(adapter),
                    new AppSettings
                    {
                        AutoConfirmToolActions = true,
                        RequireVerificationForMutations = false,
                        FallbackToJsonObject = false
                    },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Лист создан.", result.AssistantText, "duplicate read recovery final response");
                AssertEqual(2, adapter.Executed.Count, "duplicate successful read is not executed twice");
                AssertEqual(1, adapter.Executed.Count(command => command.ToolId == "excel.get_context"), "workbook context executes once");
                AssertEqual("excel.add_sheet", adapter.Executed[1].ToolId, "agent advances to mutation");
                AssertContains(FlattenMessages(calls[1]), "excel.get_context succeeded", "successful read is retained as an observation");
                AssertTrue(!PlannerAvailableTools(FlattenMessages(calls[1])).Contains("excel.get_context"), "completed parameterless read is removed from available tools");
            });
        }

        private static void CompletedSummaryReadLeavesAvailableTools()
        {
            var adapter = FakeOfficeAdapter.ForHost("Excel");
            var tools = adapter.GetBuiltInTools().ToList();
            var summary = tools.First(tool => tool.Id == "excel.workbook_summary");
            var mutation = tools.First(tool => tool.Id == "excel.add_sheet");
            var guard = new AgentDecisionProgressGuard();

            AssertTrue(guard.PlanDecisionAllowed(new AgentRunState()), "plan is available before the first action");
            AssertTrue(!guard.PlanDecisionAllowed(new AgentRunState { TotalToolSteps = 1 }), "plan is unavailable after an action");

            guard.RecordToolResult(
                new ToolCommand { ToolId = summary.Id },
                summary,
                ToolResult.Ok("summary"),
                tools);
            AssertTrue(!guard.FilterAvailableTools(tools).Any(tool => tool.Id == summary.Id), "completed workbook summary is hidden");

            guard.RecordToolResult(
                new ToolCommand { ToolId = mutation.Id },
                mutation,
                ToolResult.Ok("sheet added"),
                tools);
            AssertTrue(guard.FilterAvailableTools(tools).Any(tool => tool.Id == summary.Id), "mutation invalidates old workbook summary");
        }

        private static string PlannerAvailableTools(string prompt)
        {
            var start = (prompt ?? string.Empty).IndexOf("AVAILABLE_TOOLS:", StringComparison.Ordinal);
            var end = (prompt ?? string.Empty).IndexOf("OBSERVATIONS:", start < 0 ? 0 : start, StringComparison.Ordinal);
            if (start < 0) return string.Empty;
            return end > start ? prompt.Substring(start, end - start) : prompt.Substring(start);
        }
    }
}
