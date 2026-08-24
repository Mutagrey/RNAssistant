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
            AssertEqual(0, parsed.Response.ToolCalls.Count, "final has no tool");
        }

        private static void SimpleAgentParsesToolCall()
        {
            var tool = new ToolDefinition { Id = "excel.add_sheet" };
            var parsed = new AgentResponseParser().Parse(
                "{\"message\":\"Добавляю лист.\",\"tool_calls\":[{\"id\":\"call_1\",\"name\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Report\"}}]}",
                new[] { tool });
            AssertTrue(parsed.Success, "tool response parses");
            AssertEqual(1, parsed.Response.ToolCalls.Count, "one tool parsed");
            AssertEqual("excel.add_sheet", parsed.Response.ToolCalls[0].Name, "tool name");
            AssertEqual("Report", Convert.ToString(parsed.Response.ToolCalls[0].Arguments["name"]), "tool argument");
        }

        private static void SimpleAgentParsesMultipleToolCalls()
        {
            var tools = new[]
            {
                new ToolDefinition { Id = "excel.inspect" },
                new ToolDefinition { Id = "excel.read_range" }
            };
            var parsed = new AgentResponseParser().Parse(
                "{\"message\":\"Inspecting.\",\"tool_calls\":[" +
                "{\"id\":\"call_sheets\",\"name\":\"excel.inspect\",\"arguments\":{\"kind\":\"sheets\"}}," +
                "{\"id\":\"call_range\",\"name\":\"excel.read_range\",\"arguments\":{}}]}",
                tools);
            AssertTrue(parsed.Success, "multiple tool calls parse");
            AssertEqual(2, parsed.Response.ToolCalls.Count, "both tools parsed");
            AssertEqual("call_range", parsed.Response.ToolCalls[1].Id, "call order preserved");
        }

        private static void SimpleAgentRejectsBatchedConfirmationCalls()
        {
            var tool = new ToolDefinition
            {
                Id = "common.vba_apply_patch",
                RequiresConfirmation = true
            };
            var parsed = new AgentResponseParser().Parse(
                "{\"message\":\"Applying patches.\",\"tool_calls\":[" +
                "{\"id\":\"call_patch_1\",\"name\":\"common.vba_apply_patch\",\"arguments\":{}}," +
                "{\"id\":\"call_patch_2\",\"name\":\"common.vba_apply_patch\",\"arguments\":{}}]}",
                new[] { tool });

            AssertTrue(!parsed.Success, "confirmation calls cannot be batched");
            AssertContains(parsed.Error, "one at a time", "batch rejection explains recovery");
            AssertContains(parsed.Error, "TOOL_RESULT", "batch rejection requires fresh result");
        }

        private static void SimpleAgentRejectsToolCallWithoutMessage()
        {
            var parsed = new AgentResponseParser().Parse(
                "{\"message\":\"\",\"tool_calls\":[{\"id\":\"call_1\",\"name\":\"excel.inspect\",\"arguments\":{\"kind\":\"sheets\"}}]}",
                new[] { new ToolDefinition { Id = "excel.inspect" } });
            AssertTrue(!parsed.Success, "tool step without visible message is rejected");
            AssertContains(parsed.Error, "non-empty message", "missing step message diagnostic");
        }

        private static void SimpleAgentRejectsDuplicateToolCallIds()
        {
            var parsed = new AgentResponseParser().Parse(
                "{\"message\":\"Inspecting.\",\"tool_calls\":[" +
                "{\"id\":\"call_same\",\"name\":\"excel.inspect\",\"arguments\":{\"kind\":\"sheets\"}}," +
                "{\"id\":\"call_same\",\"name\":\"excel.inspect\",\"arguments\":{\"kind\":\"sheets\"}}]}",
                new[] { new ToolDefinition { Id = "excel.inspect" } });
            AssertTrue(!parsed.Success, "duplicate call ids rejected");
            AssertContains(parsed.Error, "unique", "duplicate id diagnostic");

            var reused = new AgentResponseParser().Parse(
                "{\"message\":\"Inspecting.\",\"tool_calls\":[{\"id\":\"call_same\",\"name\":\"excel.inspect\",\"arguments\":{\"kind\":\"sheets\"}}]}",
                new[] { new ToolDefinition { Id = "excel.inspect" } });
            AssertTrue(reused.Success, "call ids may be reused in a later response");
        }

        private static void SimpleAgentRequiresExactToolNames()
        {
            var parsed = new AgentResponseParser().Parse(
                "{\"message\":\"Working.\",\"tool_calls\":[{\"id\":\"call_1\",\"name\":\"Excel.Inspect\",\"arguments\":{\"kind\":\"sheets\"}}]}",
                new[] { new ToolDefinition { Id = "excel.inspect" } });
            AssertTrue(!parsed.Success, "case aliases are rejected");
            AssertContains(parsed.Error, "Unknown tool", "exact name diagnostic");
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
            AssertContains(prompt, "\"description\":\"Worksheet name.\"", "argument description present");
            AssertContains(prompt, "excel.add_sheet", "first tool present");
            AssertContains(prompt, "excel.read_range", "second tool present");
            AssertContains(prompt, "common.test", "skill id present");
            AssertContains(prompt, "Test workflow", "skill description present");
            AssertContains(prompt, "\"revision\":\"" + SkillRevision.Compute(skills[0]) + "\"", "skill revision present");
            AssertContains(prompt, "\"bodyChars\":27", "skill body size present");
            AssertContains(prompt, "\"referenceCount\":0", "skill reference count present");
            AssertTrue(prompt.IndexOf("TEST_SKILL_SENTINEL", StringComparison.Ordinal) < 0, "full skill is not in catalog");
            AssertContains(prompt, "common.skills_read", "skill loading guidance present");
            AssertContains(prompt, "`loaded=true`", "skill loading state is explicit");
            AssertContains(prompt, "several tool_calls", "multi-tool guidance present");
            AssertContains(prompt, "data.truncated=true", "bounded tool-result guidance present");
            AssertTrue(prompt.IndexOf("ROUTE:", StringComparison.OrdinalIgnoreCase) < 0, "no route wrapper");
            AssertTrue(prompt.IndexOf("NEXT_ACTION_POLICY", StringComparison.OrdinalIgnoreCase) < 0, "no action heuristic");
        }

        private static void AgentContinuesWithLocalToolsForClosedDocument()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = NewSession(adapter);
                session.DocumentKey = "closed-doc";
                session.DocumentTitle = "Closed.xlsx";
                var context = new DocumentContext
                {
                    Host = session.Host,
                    DocumentKey = session.DocumentKey,
                    Title = session.DocumentTitle
                };
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var responses = new Queue<string>(new[]
                {
                    "{\"message\":\"Создаю локальный HTML.\",\"tool_calls\":[{\"id\":\"call_html\",\"name\":\"common.html_workspace_upsert\",\"arguments\":{\"resourceType\":\"file\",\"name\":\"index.html\",\"content\":\"<main>Offline</main>\"}}]}",
                    "{\"message\":\"Локальный HTML готов.\",\"tool_calls\":[]}"
                });
                var calls = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
                {
                    calls.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };

                var result = new AgentRunService(adapter, executor, completion).ExecuteAsync(
                    "Создай HTML без обращения к Excel.",
                    session,
                    context,
                    new AppSettings(),
                    tools,
                    null,
                    null,
                    null,
                    CancellationToken.None).GetAwaiter().GetResult();

                AssertEqual("Локальный HTML готов.", result.AssistantText, "closed-document local result");
                AssertTrue(session.HtmlWorkspace != null && session.HtmlWorkspace.Files.Any(file =>
                    file != null && string.Equals(file.Path, "index.html", StringComparison.OrdinalIgnoreCase)),
                    "closed-document HTML file saved");
                var prompt = FlattenSimple(calls[0]);
                AssertContains(prompt, "\"key\":\"closed-doc\"", "archived document identity in prompt");
                AssertContains(prompt, "\"office_tools_available\":false", "Office availability in prompt");
                AssertContains(prompt, "common.html_workspace_upsert", "local HTML tool remains available");
                AssertTrue(prompt.IndexOf("excel.read_range", StringComparison.OrdinalIgnoreCase) < 0,
                    "Office tools omitted for a closed document");
                AssertTrue(prompt.IndexOf("common.html_data_bind", StringComparison.OrdinalIgnoreCase) < 0,
                    "Office-backed HTML binding omitted for a closed document");
                AssertEqual(0, adapter.Executed.Count, "local tool does not enter Office adapter");

                var blocked = executor.Execute(
                    Command("excel.read_range", "sheet", "Data", "range", "A1:B2"),
                    tools,
                    new AppSettings(),
                    false,
                    false,
                    session);
                AssertEqual("active_document_changed", blocked.ErrorCode,
                    "closed-document Office call remains guarded");
                AssertEqual(0, adapter.Executed.Count, "guarded Office tool never reaches adapter");
            });
        }

        private static void SimpleAgentLoadsFullSkillThroughTool()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var skill = new SkillDefinition
                {
                    Id = "common.test",
                    Name = "Test",
                    Description = "Test workflow",
                    Version = "2.0.0",
                    BodyMarkdown = "Follow TEST_SKILL_SENTINEL.\n" + new string('x', 15000) + "\nTEST_SKILL_END.",
                    Enabled = true
                };
                var responses = new Queue<string>(new[]
                {
                    "{\"message\":\"Читаю подходящий skill.\",\"tool_calls\":[{\"id\":\"call_skill\",\"name\":\"common.skills_read\",\"arguments\":{\"id\":\"common.test\"}}]}",
                    "{\"message\":\"Инструкции учтены.\",\"tool_calls\":[]}"
                });
                var calls = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (completionSettings, messages, options, stream, cancellationToken) =>
                {
                    calls.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var result = new AgentRunService(adapter, executor, completion).ExecuteAsync(
                    "Do the test workflow.", NewSession(adapter), NewContext(adapter), new AppSettings(),
                    tools, null, null, null, new[] { skill }, CancellationToken.None, true).GetAwaiter().GetResult();

                AssertEqual("Инструкции учтены.", result.AssistantText, "skill-assisted response");
                var revision = SkillRevision.Compute(skill);
                AssertTrue(FlattenSimple(calls[0]).IndexOf("TEST_SKILL_SENTINEL", StringComparison.Ordinal) < 0,
                    "first request contains only catalog");
                AssertContains(FlattenSimple(calls[0]), "\"revision\":\"" + revision + "\"", "catalog carries skill revision");
                var replay = FlattenSimple(calls[1]);
                AssertContains(replay, "TEST_SKILL_SENTINEL", "full instructions returned by tool");
                AssertContains(replay, "TEST_SKILL_END", "skill body is not cut by the generic tool-result limit");
                AssertContains(replay, "\"format\":\"markdown\"", "loaded skill format");
                AssertContains(replay, "\"version\":\"2.0.0\"", "loaded skill version");
                AssertContains(replay, "\"revision\":\"" + revision + "\"", "loaded skill revision matches catalog");
                AssertContains(replay, "\"loaded\":true", "loaded skill result is explicit");
                AssertContains(replay, "\"complete\":true", "loaded skill body is complete");
                AssertTrue(replay.IndexOf("\"truncated\":true", StringComparison.Ordinal) < 0,
                    "loaded skill is not duplicated into a truncated result");
            });
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
                    ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}},\"required\":[],\"additionalProperties\":false}"
                }
            };
            var prompt = FlattenSimple(new AgentPromptComposer().BuildMessages(
                "Test", adapter, tools, null, new DocumentContext(), new AppSettings(), NewSession(adapter), null));
            AssertContains(prompt, "excel.good", "valid tool included");
            AssertTrue(prompt.IndexOf("excel.bad", StringComparison.OrdinalIgnoreCase) < 0, "invalid tool excluded");
        }

        private static void StrictToolSchemaValidatesMetadataAndConstraints()
        {
            var tool = new ToolDefinition
            {
                Id = "common.strict_test",
                ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{\"count\":{\"type\":\"integer\",\"description\":\"Item count.\",\"default\":2,\"minimum\":1,\"maximum\":3}},\"required\":[],\"additionalProperties\":false}"
            };
            Newtonsoft.Json.Linq.JObject schema;
            string error;
            AssertTrue(ToolSchemaSupport.TryParse(tool, out schema, out error), "strict schema parses");

            var arguments = new Newtonsoft.Json.Linq.JObject();
            AssertTrue(ToolSchemaSupport.ValidateArguments(arguments, schema, true, out error), "default is applied");
            AssertEqual(2L, Convert.ToInt64(arguments["count"]), "declared default value");
            arguments["count"] = Newtonsoft.Json.Linq.JValue.CreateNull();
            ToolSchemaSupport.RemoveOptionalNulls(arguments, schema);
            AssertTrue(ToolSchemaSupport.ValidateArguments(arguments, schema, true, out error), "strict-output null is treated as omitted");
            AssertEqual(2L, Convert.ToInt64(arguments["count"]), "runtime reapplies the code-owned default after null removal");
            arguments["count"] = 4;
            AssertTrue(!ToolSchemaSupport.ValidateArguments(arguments, schema, false, out error), "maximum is enforced");

            tool.ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":[\"string\",\"null\"],\"description\":\"Nullable value.\"}},\"required\":[],\"additionalProperties\":false}";
            AssertTrue(ToolSchemaSupport.TryParse(tool, out schema, out error), "explicitly nullable schema parses");
            arguments = new Newtonsoft.Json.Linq.JObject { ["value"] = Newtonsoft.Json.Linq.JValue.CreateNull() };
            ToolSchemaSupport.RemoveOptionalNulls(arguments, schema);
            AssertTrue(arguments.Property("value") != null, "explicitly allowed null is preserved");
            AssertTrue(ToolSchemaSupport.ValidateArguments(arguments, schema, false, out error), "explicit null remains valid");

            var tableCommand = new ToolCommand();
            tableCommand.Arguments["values"] = new Newtonsoft.Json.Linq.JArray(
                new Newtonsoft.Json.Linq.JArray("A", "B"),
                new Newtonsoft.Json.Linq.JArray("C", "D"),
                new Newtonsoft.Json.Linq.JArray("E", "F"));
            ResolvedTableArguments table;
            AssertTrue(TableArgumentResolver.TryResolve(tableCommand, 2, 2, out table, out error), "table dimensions are inferred");
            AssertEqual(3, table.Rows, "inferred table rows");
            AssertEqual(2, table.Columns, "inferred table columns");
            tableCommand.Arguments["rows"] = 2;
            AssertTrue(!TableArgumentResolver.TryResolve(tableCommand, 2, 2, out table, out error), "undersized explicit table dimensions fail before COM");
            AssertContains(error, "omit", "table dimension recovery hint");

            tool.ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{\"count\":{\"type\":\"integer\"}},\"required\":[],\"additionalProperties\":false}";
            AssertTrue(!ToolSchemaSupport.TryParse(tool, out schema, out error), "undocumented argument is rejected");
            AssertContains(error, "description", "missing description diagnostic");
        }

        private static void ControllerToolCatalogUsesStrictSchemas()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                foreach (var tool in executor.GetControllerTools())
                {
                    Newtonsoft.Json.Linq.JObject schema;
                    string error;
                    AssertTrue(ToolSchemaSupport.TryParse(tool, out schema, out error), tool.Id + ": " + error);
                }
            });
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

        private static void SimpleAgentPromptIsRequestLocal()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var requests = new List<IReadOnlyList<ChatMessage>>();
                var responses = new Queue<string>(new[]
                {
                    "{\"message\":\"Читаю листы.\",\"tool_calls\":[{\"id\":\"call_sheets\",\"name\":\"excel.inspect\",\"arguments\":{\"kind\":\"sheets\"}}]}",
                    "{\"message\":\"Готово.\",\"tool_calls\":[]}"
                });
                LlmCompletionDelegate completion = (completionSettings, messages, options, stream, cancellationToken) =>
                {
                    requests.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var session = NewSession(adapter);
                session.HtmlModeEnabled = true;
                var settings = new AppSettings { SystemPrompt = "SYSTEM_PROMPT_SENTINEL" };
                var result = new AgentRunService(adapter, executor, completion).ExecuteAsync(
                    "List sheets.", session, NewContext(adapter), settings,
                    adapter.GetBuiltInTools().ToList(), null).GetAwaiter().GetResult();

                AssertEqual("Готово.", result.AssistantText, "agent completed");
                AssertEqual(2, requests.Count, "two model requests");
                foreach (var request in requests)
                {
                    AssertEqual(1, request.Count(message =>
                        (message.Content ?? string.Empty).IndexOf("SYSTEM_PROMPT_SENTINEL", StringComparison.Ordinal) >= 0),
                        "system prompt appears once per request");
                    AssertEqual(1, request.Count(message =>
                        (message.Content ?? string.Empty).IndexOf("RUNTIME_CONTEXT:", StringComparison.Ordinal) >= 0),
                        "runtime context appears once per request");
                    AssertTrue(request.Any(message =>
                        (message.Content ?? string.Empty).IndexOf("\"html_workspace_preferred\":true", StringComparison.Ordinal) >= 0),
                        "runtime context exposes HTML preference");
                }
                AssertTrue(!session.Messages.Any(message =>
                    (message.Content ?? string.Empty).IndexOf("SYSTEM_PROMPT_SENTINEL", StringComparison.Ordinal) >= 0 ||
                    (message.Content ?? string.Empty).IndexOf("RUNTIME_CONTEXT:", StringComparison.Ordinal) >= 0),
                    "prompt is not persisted in chat history");
            });
        }

        private static void SimpleAgentRepairsInvalidResponse()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                const string invalid = "PLAIN_INVALID_RESPONSE_SENTINEL";
                var responses = new Queue<LlmCompletionResult>(new[]
                {
                    new LlmCompletionResult { Content = invalid, ReasoningContent = "INVALID_REASONING_SENTINEL" },
                    new LlmCompletionResult { Content = "{\"message\":\"Не могу выполнить этот запрос.\",\"tool_calls\":[]}" }
                });
                var requests = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
                {
                    requests.Add(messages.ToList());
                    return Task.FromResult(responses.Dequeue());
                };
                var session = NewSession(adapter);
                var result = new AgentRunService(adapter, executor, completion).ExecuteAsync(
                    "Restricted request.", session, NewContext(adapter), new AppSettings(),
                    adapter.GetBuiltInTools().ToList(), null).GetAwaiter().GetResult();

                AssertEqual(2, requests.Count, "one repair request");
                AssertEqual("Не могу выполнить этот запрос.", result.AssistantText, "formatted refusal accepted");
                var repair = requests[1].Last();
                AssertContains(repair.Content, "FORMAT_REPAIR", "repair instruction added");
                AssertContains(repair.Content, "refuse", "refusal can remain a final answer");
                AssertTrue(FlattenSimple(requests[1]).IndexOf(invalid, StringComparison.Ordinal) < 0,
                    "invalid raw response is not copied into repair prompt");
                AssertTrue(!session.Messages.Any(message =>
                    (message.Content ?? string.Empty).IndexOf(invalid, StringComparison.Ordinal) >= 0 ||
                    (message.Content ?? string.Empty).IndexOf("FORMAT_REPAIR", StringComparison.Ordinal) >= 0 ||
                    (message.ReasoningContent ?? string.Empty).IndexOf("INVALID_REASONING_SENTINEL", StringComparison.Ordinal) >= 0),
                    "invalid completion and repair instruction are not persisted");
            });
        }

        private static void SimpleAgentFailedRepairDoesNotPolluteContext()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var responses = new Queue<string>(new[] { "INVALID_FIRST", "INVALID_SECOND", "INVALID_THIRD" });
                var calls = 0;
                LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
                {
                    calls += 1;
                    return Task.FromResult(new LlmCompletionResult
                    {
                        Content = responses.Dequeue(),
                        ReasoningContent = "INVALID_DIAGNOSTIC_REASONING"
                    });
                };
                var session = NewSession(adapter);
                var result = new AgentRunService(adapter, executor, completion).ExecuteAsync(
                    "Do something.", session, NewContext(adapter), new AppSettings { MaxAgentFormatRetries = 2 },
                    adapter.GetBuiltInTools().ToList(), null).GetAwaiter().GetResult();

                AssertEqual(3, calls, "initial request plus configured repair retries");
                AssertContains(result.AssistantText, "после 2 попыток", "clear bounded-repair diagnostic");
                AssertTrue(session.Messages.Last().Activity != null, "diagnostic activity recorded");
                AssertTrue(session.Messages.Last().ExcludeFromModelContext, "diagnostic excluded from replay");
                AssertTrue(!session.Messages.Any(message =>
                    (message.Content ?? string.Empty).IndexOf("INVALID_FIRST", StringComparison.Ordinal) >= 0 ||
                    (message.Content ?? string.Empty).IndexOf("INVALID_SECOND", StringComparison.Ordinal) >= 0 ||
                    (message.Content ?? string.Empty).IndexOf("INVALID_THIRD", StringComparison.Ordinal) >= 0 ||
                    (message.Content ?? string.Empty).IndexOf("FORMAT_REPAIR", StringComparison.Ordinal) >= 0 ||
                    (message.ReasoningContent ?? string.Empty).IndexOf("INVALID_DIAGNOSTIC_REASONING", StringComparison.Ordinal) >= 0),
                    "failed completions do not enter stored context");
            });
        }

        private static void SimpleAgentClampsFormatRepairLimit()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = 0;
                LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
                {
                    calls += 1;
                    return Task.FromResult(new LlmCompletionResult { Content = "INVALID" });
                };
                var result = new AgentRunService(adapter, executor, completion).ExecuteAsync(
                    "Do something.", NewSession(adapter), NewContext(adapter), new AppSettings { MaxAgentFormatRetries = 99 },
                    adapter.GetBuiltInTools().ToList(), null).GetAwaiter().GetResult();

                AssertEqual(21, calls, "initial request plus at most twenty repairs");
                AssertContains(result.AssistantText, "после 20 попыток", "clamped repair diagnostic");
            });
        }

        private static void SimpleAgentExposesSafeVbaEditingTools()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                IReadOnlyList<ChatMessage> request = null;
                LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
                {
                    request = messages.ToList();
                    return Task.FromResult(new LlmCompletionResult { Content = "{\"message\":\"Готово.\",\"tool_calls\":[]}" });
                };
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                new AgentRunService(adapter, executor, completion).ExecuteAsync(
                    "Inspect VBA.", NewSession(adapter), NewContext(adapter), new AppSettings(), tools, null)
                    .GetAwaiter().GetResult();

                var prompt = FlattenSimple(request);
                AssertContains(prompt, "\"name\":\"common.vba_read_module\"", "common VBA read exposed");
                AssertContains(prompt, "\"name\":\"common.vba_search_code\"", "common VBA search exposed");
                AssertContains(prompt, "\"name\":\"common.vba_apply_patch\"", "common safe VBA patch exposed");
                AssertContains(prompt, "\"name\":\"common.vba_write_module\"", "common VBA upsert exposed");
                AssertContains(prompt, "\"name\":\"common.vba_delete_module\"", "common VBA delete exposed");
                AssertTrue(prompt.IndexOf("\"name\":\"common.vba_create_module\"", StringComparison.Ordinal) < 0,
                    "redundant create alias is hidden from the model");
                AssertTrue(prompt.IndexOf("\"name\":\"common.vba_replace_text\"", StringComparison.Ordinal) < 0,
                    "redundant replace alias is hidden from the model");
                AssertTrue(prompt.IndexOf("\"name\":\"common.vba_read_lines\"", StringComparison.Ordinal) < 0,
                    "range reads use the single read_module contract");
                AssertTrue(prompt.IndexOf("\"expectedCodeSha256\"", StringComparison.Ordinal) < 0,
                    "model-facing VBA schemas do not require a hash argument");
                AssertTrue(prompt.IndexOf("\"name\":\"excel.vba_read_module\"", StringComparison.Ordinal) < 0,
                    "raw host VBA read backend remains hidden");
                AssertTrue(prompt.IndexOf("\"name\":\"excel.vba_replace_module\"", StringComparison.Ordinal) < 0,
                    "raw whole-module backend remains hidden");
                AssertTrue(prompt.IndexOf("\"name\":\"excel.run_macro\"", StringComparison.Ordinal) < 0,
                    "macro execution backend remains hidden");
            });
        }

        private static void SimpleAgentRejectsHiddenVbaBackendCalls()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var responses = new Queue<string>(new[]
                {
                    "{\"message\":\"Запускаю макрос напрямую.\",\"tool_calls\":[{\"id\":\"call_macro\",\"name\":\"excel.run_macro\",\"arguments\":{\"macroName\":\"MigrateApiKey\"}}]}",
                    "{\"message\":\"Скрытый backend недоступен; нужен безопасный публичный инструмент.\",\"tool_calls\":[]}"
                });
                var calls = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
                {
                    calls.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var result = new AgentRunService(adapter, executor, completion).ExecuteAsync(
                    "Bypass a failed patch.", NewSession(adapter), NewContext(adapter), new AppSettings(), tools, null)
                    .GetAwaiter().GetResult();

                AssertEqual(2, calls.Count, "hidden backend response repaired once");
                AssertEqual(0, adapter.Executed.Count(command => command.ToolId == "excel.run_macro"), "hidden macro was not executed");
                AssertContains(FlattenSimple(calls[1]), "Unknown tool: excel.run_macro", "repair explains hidden tool rejection");
                AssertContains(result.AssistantText, "недоступен", "model recovers without hidden execution");
            });
        }

        private static void SimpleAgentExecutesMultipleToolsSequentially()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var responses = new Queue<string>(new[]
                {
                    "{\"message\":\"Создаю два независимых листа.\",\"tool_calls\":[" +
                    "{\"id\":\"call_first\",\"name\":\"excel.add_sheet\",\"arguments\":{\"name\":\"First\"}}," +
                    "{\"id\":\"call_second\",\"name\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Second\"}}]}",
                    "{\"message\":\"Оба листа созданы.\",\"tool_calls\":[]}"
                });
                IReadOnlyList<ChatMessage> secondTurn = null;
                var progressActivities = new List<ChatActivity>();
                var callCount = 0;
                LlmCompletionDelegate completion = (completionSettings, messages, options, stream, cancellationToken) =>
                {
                    callCount += 1;
                    if (callCount == 2) secondTurn = messages.ToList();
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var session = NewSession(adapter);
                var result = new AgentRunService(adapter, executor, completion).ExecuteAsync(
                    "Создай листы First и Second.", session, NewContext(adapter),
                    new AppSettings { AutoConfirmToolActions = true, MaxAgentIterations = 4 },
                    adapter.GetBuiltInTools().ToList(), (phase, message, activity) =>
                    {
                        if (activity != null) progressActivities.Add(activity);
                    }).GetAwaiter().GetResult();

                AssertEqual("Оба листа созданы.", result.AssistantText, "multi-tool final response");
                AssertTrue(adapter.HasSheet("First") && adapter.HasSheet("Second"), "both tools executed");
                AssertEqual("excel.add_sheet", adapter.Executed[adapter.Executed.Count - 2].ToolId, "first execution recorded");
                AssertEqual("First", Convert.ToString(adapter.Executed[adapter.Executed.Count - 2].Arguments["name"]), "first call order");
                AssertEqual("Second", Convert.ToString(adapter.Executed[adapter.Executed.Count - 1].Arguments["name"]), "second call order");
                var replay = FlattenSimple(secondTurn);
                AssertEqual(2, replay.Split(new[] { "TOOL_RESULT:" }, StringSplitOptions.None).Length - 1, "two results replayed");
                AssertContains(replay, "call_first", "first call id replayed");
                AssertContains(replay, "call_second", "second call id replayed");
                var activities = session.Messages
                    .Where(message => message != null && message.Activity != null && message.Activity.Kind == "tool")
                    .Select(message => message.Activity)
                    .ToList();
                AssertEqual(2, activities.Count, "two visible tool activities");
                AssertTrue(!string.IsNullOrWhiteSpace(activities[0].StepId), "model step id stored");
                AssertEqual(activities[0].StepId, activities[1].StepId, "batch tools share one model step");
                AssertEqual("Создаю два независимых листа.", activities[0].StepMessage, "model step message stored");
                var marker = progressActivities.First(activity => activity.Kind == "step");
                var running = progressActivities.First(activity => activity.Kind == "tool" && activity.Status == "running");
                AssertEqual(marker.StepId, running.StepId, "live tool belongs to visible model step");
            });
        }

        private static void SimpleAgentConfirmationReplaysOnlyFinalResult()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var responses = new Queue<string>(new[]
                {
                    "{\"message\":\"Создаю skill.\",\"tool_calls\":[" +
                    "{\"id\":\"call_skill\",\"name\":\"common.skills_upsert\",\"arguments\":{\"id\":\"common.test\",\"description\":\"Test\",\"bodyMarkdown\":\"# Test\"}}]}",
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
                session.LastRun = new ChatRunRecord { Status = "running" };
                var settings = new AppSettings { AutoConfirmToolActions = false, SystemPromptRole = "user" };
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var first = service.ExecuteAsync(
                    "Create a test skill.", session, NewContext(adapter), settings, tools,
                    (Action<string, string, ChatActivity>)null,
                    (pendingSession, pendingCommand, result) => "pending_1").GetAwaiter().GetResult();

                AssertContains(first.AssistantText, "Создаю", "waiting response returned");
                AssertTrue(!session.Messages.Any(message => message.ProtocolMessage &&
                    (message.Content ?? string.Empty).IndexOf("waiting_confirmation", StringComparison.OrdinalIgnoreCase) >= 0),
                    "waiting result not replayed");
                AssertEqual("call_skill", session.Messages.Last(message => message.Activity != null).Activity.ToolCallId,
                    "pending activity keeps tool call id");
                var pendingActivity = session.Messages.Last(message => message.Activity != null).Activity;
                var expectedCatalogFingerprint = AgentRunService.ToolExecutionFingerprint(
                    AgentRunService.PrepareToolsForRun(tools),
                    "common.skills_upsert");
                AssertEqual(expectedCatalogFingerprint, pendingActivity.ConfirmationCatalogSha256,
                    "pending activity persists executable tool fingerprint");
                var changedTools = tools.Select(tool => tool.Clone()).ToList();
                changedTools.First(tool => string.Equals(
                    tool.Id, "common.skills_upsert", StringComparison.OrdinalIgnoreCase)).ArgumentSchemaJson =
                        "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}";
                AssertTrue(!string.Equals(
                        expectedCatalogFingerprint,
                        AgentRunService.ToolExecutionFingerprint(
                            AgentRunService.PrepareToolsForRun(changedTools),
                            "common.skills_upsert"),
                        StringComparison.OrdinalIgnoreCase),
                    "tool fingerprint changes with a replaced executable definition");
                AssertEqual(1, session.LastRun.IterationsUsed, "confirmation stores iteration cursor");
                AssertEqual(1, session.LastRun.ToolStepsUsed, "confirmation reserves one logical tool step");
                var initialIterationsUsed = session.LastRun.IterationsUsed;
                var initialToolStepsUsed = session.LastRun.ToolStepsUsed;
                foreach (var message in session.Messages)
                {
                    message.RunId = "initial_run";
                }

                var confirmedCommand = new ToolCommand { ToolId = "common.skills_upsert", ToolCallId = "call_skill" };
                confirmedCommand.Arguments["id"] = "common.test";
                var final = service.ContinueAfterToolAsync(
                    confirmedCommand,
                    ToolResult.Ok("Skill saved.", "{\"id\":\"common.test\"}"),
                    session,
                    NewContext(adapter),
                    settings,
                    tools,
                    null,
                    null,
                    cancellationToken: CancellationToken.None,
                    initialIterationsUsed: initialIterationsUsed,
                    initialToolStepsUsed: initialToolStepsUsed).GetAwaiter().GetResult();

                AssertEqual("Skill сохранён.", final.AssistantText, "continued final response");
                AssertEqual(2, session.LastRun.IterationsUsed, "confirmation continuation keeps cumulative iteration budget");
                AssertEqual(1, session.LastRun.ToolStepsUsed, "confirmed result replaces reserved logical tool step");
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

        private static void SimpleAgentConfirmationFailureContinues()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var responses = new Queue<string>(new[]
                {
                    "{\"message\":\"Создаю skill.\",\"tool_calls\":[{\"id\":\"call_skill_failure\",\"name\":\"common.skills_upsert\",\"arguments\":{\"id\":\"common.failure_test\",\"description\":\"Test\",\"bodyMarkdown\":\"# Test\"}}]}",
                    "{\"message\":\"Skill уже существует; выберу другой id.\",\"tool_calls\":[]}"
                });
                var calls = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (completionSettings, messages, options, stream, cancellationToken) =>
                {
                    calls.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var service = new AgentRunService(adapter, executor, completion);
                var session = NewSession(adapter);
                var settings = new AppSettings { AutoConfirmToolActions = false };
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                service.ExecuteAsync(
                    "Create a test skill.", session, NewContext(adapter), settings, tools,
                    (Action<string, string, ChatActivity>)null,
                    (pendingSession, pendingCommand, result) => "pending_failure").GetAwaiter().GetResult();

                var command = new ToolCommand { ToolId = "common.skills_upsert", ToolCallId = "call_skill_failure" };
                command.Arguments["id"] = "common.failure_test";
                var failure = ToolResult.Fail(
                    "Skill already exists: common.failure_test.",
                    null,
                    "skill_already_exists",
                    false);
                var final = service.ContinueAfterToolAsync(
                    command,
                    failure,
                    session,
                    NewContext(adapter),
                    settings,
                    tools,
                    null,
                    null).GetAwaiter().GetResult();

                AssertEqual("Skill уже существует; выберу другой id.", final.AssistantText, "agent continues after confirmed failure");
                AssertEqual(2, calls.Count, "failure triggers next model turn");
                var replay = FlattenSimple(calls[1]);
                AssertContains(replay, "\"ok\":false", "confirmed failure replayed");
                AssertContains(replay, "skill_already_exists", "confirmed failure code replayed");
                AssertTrue(replay.IndexOf("waiting_confirmation", StringComparison.OrdinalIgnoreCase) < 0, "waiting result is not replayed after failure");
            });
        }

        private static void ModelRefusalIsTerminalInAgentAndChat()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = 0;
                LlmCompletionDelegate completion = (completionSettings, messages, options, stream, cancellationToken) =>
                {
                    calls += 1;
                    return Task.FromResult(new LlmCompletionResult
                    {
                        Content = string.Empty,
                        RefusalContent = "Запрос отклонён провайдером."
                    });
                };

                var agentSession = NewSession(adapter);
                var agent = new AgentRunService(adapter, executor, completion).ExecuteAsync(
                    "Restricted request.", agentSession, NewContext(adapter), new AppSettings(),
                    new ToolDefinition[0], (Action<string, string, ChatActivity>)null).GetAwaiter().GetResult();
                AssertEqual("Запрос отклонён провайдером.", agent.AssistantText, "agent refusal text");
                AssertEqual(1, calls, "agent refusal does not enter format repair");

                var chatSession = NewSession(adapter);
                chatSession.Mode = ChatModes.Chat;
                var chat = new PlainChatService(completion).ExecuteAsync(
                    "Restricted request.", chatSession, NewContext(adapter), new AppSettings(), null, null,
                    CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual("Запрос отклонён провайдером.", chat.AssistantText, "chat refusal text");
                AssertEqual(2, calls, "plain chat makes one request");

                var emptyService = new PlainChatService((completionSettings, messages, options, stream, cancellationToken) =>
                    Task.FromResult(new LlmCompletionResult { Content = string.Empty }));
                try
                {
                    emptyService.ExecuteAsync(
                        "Empty response.", NewSession(adapter), NewContext(adapter), new AppSettings(), null, null,
                        CancellationToken.None).GetAwaiter().GetResult();
                    throw new InvalidOperationException("empty plain response unexpectedly succeeded");
                }
                catch (InvalidOperationException ex)
                {
                    if (ex.Message.IndexOf("unexpectedly", StringComparison.OrdinalIgnoreCase) >= 0) throw;
                    AssertContains(ex.Message, "empty response", "plain chat rejects empty provider response");
                }
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
            session.Messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = "Inspecting.",
                ProtocolMessage = true,
                RunId = "run_tool",
                ToolCalls = new List<LlmToolCall>
                {
                    new LlmToolCall
                    {
                        Id = "call_1",
                        Type = "function",
                        Name = "rna_excel_read_range",
                        ArgumentsJson = "{\"range\":\"COMPACTION_TOOL_ARGUMENT\"}"
                    },
                    new LlmToolCall
                    {
                        Id = "call_2",
                        Type = "function",
                        Name = "rna_excel_read_range",
                        ArgumentsJson = "{\"range\":\"COMPACTION_TOOL_ARGUMENT_2\"}"
                    }
                }
            });
            session.Messages.Add(new ChatMessage
            {
                Role = "tool",
                ToolCallId = "call_1",
                Content = "{\"ok\":true,\"tool_call_id\":\"call_1\",\"name\":\"excel.read_range\"}",
                ProtocolMessage = true,
                RunId = "run_tool"
            });
            session.Messages.Add(new ChatMessage
            {
                Role = "tool",
                ToolCallId = "call_2",
                Content = "{\"ok\":true,\"tool_call_id\":\"call_2\",\"name\":\"excel.read_range\"}",
                ProtocolMessage = true,
                RunId = "run_tool"
            });
            session.Messages.Add(new ChatMessage { Role = "assistant", Content = "I will inspect the data." });
            session.Messages.Add(new ChatMessage { Role = "user", Content = "Keep the original formatting." });
            session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Understood." });

            var checkpoint = new ContextCompactionService(completion).EnsureWithinBudgetAsync(
                session, new AppSettings(), null, true, null, CancellationToken.None).GetAwaiter().GetResult();

            AssertTrue(checkpoint != null, "checkpoint created");
            AssertEqual("Goal preserved; first step complete.", checkpoint.SummaryMarkdown, "summary used directly");
            var request = FlattenSimple(captured);
            AssertContains(request, "\"required\":[\"summary\"]", "single-field schema requested");
            AssertContains(request, "COMPACTION_TOOL_ARGUMENT", "native tool arguments preserved for compaction");
            AssertContains(request, "COMPACTION_TOOL_ARGUMENT_2", "all native tool calls preserved for compaction");
            AssertTrue(request.IndexOf("\"goals\"", StringComparison.Ordinal) < 0, "no fixed summary sections");
            AssertContains(
                ContextCompactionService.BuildActiveWindow(session)[0].Content,
                "Full skill bodies present only in compacted earlier context are not loaded",
                "compacted context invalidates skill body loading");
        }

        private static void CompactionPreservesToolProtocolPairs()
        {
            IReadOnlyList<ChatMessage> captured = null;
            LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
            {
                captured = messages.ToList();
                return Task.FromResult(new LlmCompletionResult { Content = "{\"summary\":\"Earlier context.\"}" });
            };
            var session = NewSession(FakeOfficeAdapter.ForHost("Excel"));
            session.Messages.Add(new ChatMessage { Role = "user", Content = "First request." });
            var beforePair = new ChatMessage { Role = "assistant", Content = "First answer." };
            session.Messages.Add(beforePair);
            var call = new ChatMessage
            {
                Role = "assistant",
                Content = "Reading.",
                ProtocolMessage = true,
                RunId = "run_pair",
                ToolCallId = "call_pair",
                ToolCalls = new List<LlmToolCall>
                {
                    new LlmToolCall
                    {
                        Id = "call_pair",
                        Type = "function",
                        Name = "rna_excel_read_range",
                        ArgumentsJson = "{\"marker\":\"PAIR_ARGUMENT\"}"
                    },
                    new LlmToolCall
                    {
                        Id = "call_pair_missing",
                        Type = "function",
                        Name = "rna_excel_read_range",
                        ArgumentsJson = "{\"marker\":\"PAIR_MISSING_ARGUMENT\"}"
                    }
                }
            };
            session.Messages.Add(call);
            var result = new ChatMessage
            {
                Role = "developer",
                Content = "TOOL_RESULT:\n{\"ok\":true,\"tool_call_id\":\"call_pair\",\"name\":\"excel.read_range\"}",
                ProtocolMessage = true,
                RunId = "run_pair"
            };
            session.Messages.Add(result);
            session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Done." });
            session.Messages.Add(new ChatMessage { Role = "user", Content = "Recent request." });

            var checkpoint = new ContextCompactionService(completion).EnsureWithinBudgetAsync(
                session, new AppSettings(), null, true, null, CancellationToken.None).GetAwaiter().GetResult();

            AssertTrue(checkpoint != null, "checkpoint created without splitting pair");
            AssertEqual(beforePair.Id, checkpoint.ThroughMessageId, "checkpoint stops before tool call");
            AssertTrue(FlattenSimple(captured).IndexOf("PAIR_ARGUMENT", StringComparison.Ordinal) < 0,
                "tool call is not summarized without its result");
            AssertTrue(FlattenSimple(captured).IndexOf("PAIR_MISSING_ARGUMENT", StringComparison.Ordinal) < 0,
                "multi-call envelope is not summarized with a missing result");
            var replay = ContextCompactionService.BuildActiveWindow(session);
            var callIndex = replay.FindIndex(message => message != null && message.ToolCallId == "call_pair");
            var resultIndex = replay.FindIndex(message => message != null &&
                (message.Content ?? string.Empty).StartsWith("TOOL_RESULT:", StringComparison.Ordinal));
            AssertTrue(callIndex >= 0 && resultIndex == callIndex + 1, "tool call and result remain adjacent in replay tail");

            IReadOnlyList<ChatMessage> danglingCaptured = null;
            LlmCompletionDelegate danglingCompletion = (settings, messages, options, stream, cancellationToken) =>
            {
                danglingCaptured = messages.ToList();
                return Task.FromResult(new LlmCompletionResult { Content = "{\"summary\":\"Safe prefix.\"}" });
            };
            var dangling = NewSession(FakeOfficeAdapter.ForHost("Excel"));
            dangling.Messages.Add(new ChatMessage { Role = "user", Content = "Old request." });
            dangling.Messages.Add(new ChatMessage { Role = "assistant", Content = "Old answer." });
            var safeThrough = new ChatMessage { Role = "user", Content = "Next request." };
            dangling.Messages.Add(safeThrough);
            dangling.Messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = "Running.",
                ProtocolMessage = true,
                RunId = "interrupted-run",
                ToolCallId = "dangling-call",
                ToolCalls = new List<LlmToolCall>
                {
                    new LlmToolCall
                    {
                        Id = "dangling-call",
                        Type = "function",
                        Name = "rna_excel_read_range",
                        ArgumentsJson = "{\"marker\":\"DANGLING_ARGUMENT\"}"
                    }
                }
            });
            dangling.Messages.Add(new ChatMessage { Role = "assistant", Content = "Interrupted diagnostic." });
            dangling.Messages.Add(new ChatMessage { Role = "user", Content = "Continue safely." });

            var danglingCheckpoint = new ContextCompactionService(danglingCompletion).EnsureWithinBudgetAsync(
                dangling, new AppSettings(), null, true, null, CancellationToken.None).GetAwaiter().GetResult();

            AssertTrue(danglingCheckpoint != null, "checkpoint created before dangling call");
            AssertEqual(safeThrough.Id, danglingCheckpoint.ThroughMessageId, "checkpoint excludes call without result");
            AssertTrue(FlattenSimple(danglingCaptured).IndexOf("DANGLING_ARGUMENT", StringComparison.Ordinal) < 0,
                "dangling tool call is not summarized");

            var oversized = NewSession(FakeOfficeAdapter.ForHost("Excel"));
            oversized.Messages.Add(new ChatMessage { Role = "user", Content = new string('x', 500000) });
            oversized.Messages.Add(new ChatMessage { Role = "assistant", Content = "Recent answer." });
            var oversizedCheckpoint = new ContextCompactionService(completion).EnsureWithinBudgetAsync(
                oversized, new AppSettings(), null, true, null, CancellationToken.None).GetAwaiter().GetResult();
            AssertTrue(oversizedCheckpoint == null, "oversized message is not partially marked as summarized");
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
