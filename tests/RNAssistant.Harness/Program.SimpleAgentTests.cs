using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void SimpleAgentParsesFinalJson()
        {
            var parsed = new AgentResponseParser().Parse(
                "{\"message\":\"Готово.\",\"tool_calls\":[]}",
                new ToolDefinition[0]);
            AssertTrue(parsed.Success, "final response parses");
            AssertEqual("Готово.", parsed.Response.Message, "final message");
            AssertTrue(parsed.Response.ToolCall == null, "final has no tool");
        }

        private static void SimpleAgentParsesToolCall()
        {
            var tool = new ToolDefinition { Id = "excel.add_sheet" };
            var parsed = new AgentResponseParser().Parse(
                "{\"message\":\"Добавляю лист.\",\"tool_calls\":[{\"id\":\"call_1\",\"name\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Report\"}}]}",
                new[] { tool });
            AssertTrue(parsed.Success, "tool response parses");
            AssertEqual("excel.add_sheet", parsed.Response.ToolCall.Name, "tool name");
            AssertEqual("Report", Convert.ToString(parsed.Response.ToolCall.Arguments["name"]), "tool argument");
        }

        private static void SimpleAgentRejectsMissingToolCallId()
        {
            var parsed = new AgentResponseParser().Parse(
                "{\"message\":\"Working.\",\"tool_calls\":[{\"name\":\"excel.add_sheet\",\"arguments\":{}}]}",
                new[] { new ToolDefinition { Id = "excel.add_sheet" } });
            AssertTrue(!parsed.Success, "tool call id is required");
            AssertContains(parsed.Error, "id, name", "missing id diagnostic");
        }

        private static void SimpleAgentPromptContainsToolsAndSkills()
        {
            var adapter = FakeOfficeAdapter.ForHost("Excel");
            var tools = adapter.GetBuiltInTools().Where(tool => tool.Id == "excel.add_sheet" || tool.Id == "excel.read_range").ToList();
            var skills = new[]
            {
                new SkillDefinition
                {
                    Id = "common.test",
                    Name = "Test",
                    Description = "Test workflow",
                    BodyMarkdown = "Follow TEST_SKILL_SENTINEL.",
                    Enabled = true
                }
            };
            var messages = new AgentPromptComposer().BuildMessages(
                "Create a report.", adapter, tools, skills, new DocumentContext(), new AppSettings(),
                NewSession(adapter), null);
            var prompt = FlattenSimple(messages);
            AssertContains(prompt, "\"type\":\"function\"", "native-like tool JSON");
            AssertContains(prompt, "excel.add_sheet", "first tool present");
            AssertContains(prompt, "excel.read_range", "second tool present");
            AssertContains(prompt, "TEST_SKILL_SENTINEL", "full skill present");
            AssertTrue(prompt.IndexOf("ROUTE:", StringComparison.OrdinalIgnoreCase) < 0, "no route wrapper");
            AssertTrue(prompt.IndexOf("NEXT_ACTION_POLICY", StringComparison.OrdinalIgnoreCase) < 0, "no action heuristic");
        }

        private static void SimpleAgentPromptSkipsInvalidToolSchema()
        {
            var adapter = FakeOfficeAdapter.ForHost("Excel");
            var tools = new[]
            {
                new ToolDefinition
                {
                    Id = "excel.good",
                    Description = "Good",
                    Enabled = true,
                    AgentCanRun = true,
                    ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}"
                },
                new ToolDefinition
                {
                    Id = "excel.bad",
                    Description = "Bad",
                    Enabled = true,
                    AgentCanRun = true,
                    ArgumentSchemaJson = "{}"
                }
            };
            var prompt = FlattenSimple(new AgentPromptComposer().BuildMessages(
                "Test", adapter, tools, null, new DocumentContext(), new AppSettings(), NewSession(adapter), null));
            AssertContains(prompt, "excel.good", "valid tool included");
            AssertTrue(prompt.IndexOf("excel.bad", StringComparison.OrdinalIgnoreCase) < 0, "invalid tool excluded");
        }

        private static void SimpleAgentExecutesToolAndReceivesJsonResult()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var responses = new Queue<string>(new[]
                {
                    "{\"message\":\"Добавляю лист.\",\"tool_calls\":[{\"id\":\"call_add\",\"name\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Report\"}}]}",
                    "{\"message\":\"Лист Report создан.\",\"tool_calls\":[]}"
                });
                var calls = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (completionSettings, messages, options, stream, cancellationToken) =>
                {
                    calls.Add(messages.ToList());
                    AssertEqual(LlmResponseFormats.JsonObject, options.ResponseFormat, "single response format");
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var service = new AgentRunService(adapter, executor, completion);
                var result = service.ExecuteAsync(
                    "Создай лист Report.", NewSession(adapter), NewContext(adapter),
                    new AppSettings { AutoConfirmToolActions = true, MaxAgentIterations = 4 },
                    adapter.GetBuiltInTools().ToList(), null).GetAwaiter().GetResult();

                AssertEqual("Лист Report создан.", result.AssistantText, "final response");
                AssertTrue(adapter.HasSheet("Report"), "tool executed");
                AssertEqual(2, calls.Count, "two model turns");
                var second = FlattenSimple(calls[1]);
                AssertContains(second, "TOOL_RESULT", "tool result label");
                AssertContains(second, "\"ok\":true", "tool result ok");
                AssertContains(second, "\"name\":\"excel.add_sheet\"", "tool result name");
                AssertContains(second, "\"message\":", "tool result message");
            });
        }

        private static void SimpleAgentInvalidResponseFailsDirectly()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = 0;
                LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
                {
                    calls += 1;
                    return Task.FromResult(new LlmCompletionResult { Content = "not json" });
                };
                var session = NewSession(adapter);
                var result = new AgentRunService(adapter, executor, completion).ExecuteAsync(
                    "Do something.", session, NewContext(adapter), new AppSettings(),
                    adapter.GetBuiltInTools().ToList(), null).GetAwaiter().GetResult();
                AssertEqual(1, calls, "no repair loop");
                AssertContains(result.AssistantText, "Ответ агента не выполнен", "clear diagnostic");
                AssertTrue(session.Messages.Last().Activity != null, "diagnostic activity recorded");
            });
        }

        private static void SimpleAgentConfirmationReplaysOnlyFinalResult()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var responses = new Queue<string>(new[]
                {
                    "{\"message\":\"Сохраняю skill.\",\"tool_calls\":[{\"id\":\"call_skill\",\"name\":\"common.skills_save\",\"arguments\":{\"id\":\"common.test\",\"description\":\"Test\",\"bodyMarkdown\":\"# Test\"}}]}",
                    "{\"message\":\"Skill сохранён.\",\"tool_calls\":[]}"
                });
                var calls = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (completionSettings, messages, options, stream, cancellationToken) =>
                {
                    calls.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var service = new AgentRunService(adapter, executor, completion);
                var session = NewSession(adapter);
                var settings = new AppSettings { AutoConfirmToolActions = false, SystemPromptRole = "user" };
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var first = service.ExecuteAsync(
                    "Create a test skill.", session, NewContext(adapter), settings, tools,
                    (Action<string, string, ChatActivity>)null,
                    (pendingSession, pendingCommand, result) => "pending_1").GetAwaiter().GetResult();

                AssertContains(first.AssistantText, "Сохраняю", "waiting response returned");
                AssertTrue(!session.Messages.Any(message => message.ProtocolMessage &&
                    (message.Content ?? string.Empty).IndexOf("waiting_confirmation", StringComparison.OrdinalIgnoreCase) >= 0),
                    "waiting result not replayed");
                AssertEqual("call_skill", session.Messages.Last(message => message.Activity != null).Activity.ToolCallId,
                    "pending activity keeps tool call id");
                foreach (var message in session.Messages)
                {
                    message.RunId = "initial_run";
                }

                var confirmedCommand = new ToolCommand { ToolId = "common.skills_save", ToolCallId = "call_skill" };
                confirmedCommand.Arguments["id"] = "common.test";
                var final = service.ContinueAfterToolAsync(
                    confirmedCommand,
                    ToolResult.Ok("Skill saved.", "{\"id\":\"common.test\"}"),
                    session,
                    NewContext(adapter),
                    settings,
                    tools,
                    null,
                    null).GetAwaiter().GetResult();

                AssertEqual("Skill сохранён.", final.AssistantText, "continued final response");
                var replay = FlattenSimple(calls[1]);
                AssertContains(replay, "RUNTIME_CONTEXT", "user-role continuation keeps runtime context");
                AssertEqual(1, replay.Split(new[] { "TOOL_RESULT:" }, StringSplitOptions.None).Length - 1, "one result replayed");
                AssertContains(replay, "\"ok\":true", "confirmed result replayed");
                AssertTrue(replay.IndexOf("waiting_confirmation", StringComparison.OrdinalIgnoreCase) < 0, "no stale waiting result");
                AssertTrue(replay.IndexOf("Create a test skill.", StringComparison.Ordinal) < replay.IndexOf("call_skill", StringComparison.Ordinal),
                    "user request precedes tool call in replay");
                AssertTrue(replay.IndexOf("call_skill", StringComparison.Ordinal) < replay.IndexOf("TOOL_RESULT:", StringComparison.Ordinal),
                    "tool call precedes result in replay");
            });
        }

        private static void SimpleChatHasNoAgentContext()
        {
            var adapter = FakeOfficeAdapter.ForHost("Excel");
            IReadOnlyList<ChatMessage> captured = null;
            LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
            {
                captured = messages.ToList();
                return Task.FromResult(new LlmCompletionResult { Content = "Обычный ответ." });
            };
            var session = NewSession(adapter);
            session.Mode = ChatModes.Chat;
            session.Messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = "{\"message\":\"AGENT_PROTOCOL_SENTINEL\",\"tool_calls\":[]}",
                ProtocolMessage = true,
                RunId = "old_agent_run"
            });
            session.Messages.Add(new ChatMessage
            {
                Role = "user",
                Content = "TOOL_RESULT:\n{\"ok\":true,\"name\":\"secret.tool\"}",
                ProtocolMessage = true,
                RunId = "old_agent_run"
            });
            var result = new PlainChatService(completion).ExecuteAsync(
                "Привет", session, new DocumentContext(), new AppSettings(), null, null,
                CancellationToken.None).GetAwaiter().GetResult();
            AssertEqual("Обычный ответ.", result.AssistantText, "plain response");
            var prompt = FlattenSimple(captured);
            AssertTrue(prompt.IndexOf("RUNTIME_CONTEXT", StringComparison.OrdinalIgnoreCase) < 0, "no agent context");
            AssertTrue(prompt.IndexOf("tool_calls", StringComparison.OrdinalIgnoreCase) < 0, "no tool protocol");
            AssertTrue(prompt.IndexOf("AGENT_PROTOCOL_SENTINEL", StringComparison.OrdinalIgnoreCase) < 0, "no agent replay");
            AssertTrue(prompt.IndexOf("secret.tool", StringComparison.OrdinalIgnoreCase) < 0, "no tool result replay");
        }

        private static void SimpleCompactionUsesOneSummaryField()
        {
            IReadOnlyList<ChatMessage> captured = null;
            LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
            {
                captured = messages.ToList();
                return Task.FromResult(new LlmCompletionResult
                {
                    Content = "{\"summary\":\"Goal preserved; first step complete.\"}"
                });
            };
            var session = NewSession(FakeOfficeAdapter.ForHost("Excel"));
            session.Messages.Add(new ChatMessage { Role = "user", Content = "Create a report." });
            session.Messages.Add(new ChatMessage { Role = "assistant", Content = "I will inspect the data." });
            session.Messages.Add(new ChatMessage { Role = "user", Content = "Keep the original formatting." });
            session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Understood." });

            var checkpoint = new ContextCompactionService(completion).EnsureWithinBudgetAsync(
                session, new AppSettings(), null, true, null, CancellationToken.None).GetAwaiter().GetResult();

            AssertTrue(checkpoint != null, "checkpoint created");
            AssertEqual("Goal preserved; first step complete.", checkpoint.SummaryMarkdown, "summary used directly");
            var request = FlattenSimple(captured);
            AssertContains(request, "\"required\":[\"summary\"]", "single-field schema requested");
            AssertTrue(request.IndexOf("\"goals\"", StringComparison.Ordinal) < 0, "no fixed summary sections");
        }

        private static string FlattenSimple(IEnumerable<ChatMessage> messages)
        {
            return string.Join("\n", (messages ?? new ChatMessage[0])
                .Where(message => message != null)
                .Select(message => message.Content ?? string.Empty)
                .ToArray());
        }
    }
}
