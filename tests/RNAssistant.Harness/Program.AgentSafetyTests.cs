using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void DefaultPromptsAreStructuredMarkdown()
        {
            var settings = new AppSettings();
            AssertTrue(settings.SystemPrompt.StartsWith("# RNAssistant Agent", StringComparison.Ordinal), "agent prompt Markdown heading");
            AssertContains(settings.SystemPrompt, "## Response contract", "agent prompt structured section");
            AssertTrue(settings.ChatSystemPrompt.StartsWith("# RNAssistant Chat", StringComparison.Ordinal), "chat prompt Markdown heading");
            AssertTrue(settings.ContextCompactionPrompt.StartsWith("# Context compaction", StringComparison.Ordinal), "compaction prompt Markdown heading");
            AssertTrue(settings.ChatTitlePrompt.StartsWith("# Chat title", StringComparison.Ordinal), "title prompt Markdown heading");
        }

        private static void AgentSupportsSelectableResponseFormats()
        {
            var settings = new AppSettings { StreamResponses = false };
            var messages = new List<object> { new { role = "user", content = "test" } };
            var objectBody = LlmClient.BuildRequestBody(settings, messages, 10, new LlmRequestOptions
            {
                ResponseFormat = LlmResponseFormats.JsonObject
            });
            AssertEqual("json_object", (string)objectBody.SelectToken("response_format.type"), "json_object request type");

            var schemaJson = AgentResponseSchemaBuilder.Build(new ToolDefinition[0]);
            var schemaBody = LlmClient.BuildRequestBody(settings, messages, 10, new LlmRequestOptions
            {
                ResponseFormat = LlmResponseFormats.JsonSchema,
                ResponseSchemaName = AgentResponseSchemaBuilder.SchemaName,
                ResponseSchemaJson = schemaJson
            });
            AssertEqual("json_schema", (string)schemaBody.SelectToken("response_format.type"), "json_schema request type");
            AssertEqual(AgentResponseSchemaBuilder.SchemaName,
                (string)schemaBody.SelectToken("response_format.json_schema.name"), "schema name");
            AssertTrue(schemaBody.SelectToken("response_format.json_schema.strict").Value<bool>(), "strict response schema");
        }

        private static void AgentJsonSchemaMirrorsToolContracts()
        {
            var tool = new ToolDefinition
            {
                Id = "excel.read_range",
                Description = "Read cells.",
                ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{\"range\":{\"type\":\"string\",\"description\":\"A1 range.\"}},\"required\":[\"range\"],\"additionalProperties\":false}"
            };
            var schema = JObject.Parse(AgentResponseSchemaBuilder.Build(new[] { tool }));
            var call = schema.SelectToken("properties.tool_calls.items.anyOf[0]");
            AssertEqual("excel.read_range", (string)call.SelectToken("properties.name.const"), "exact tool name in schema");
            AssertEqual("string", (string)call.SelectToken("properties.arguments.properties.range.type"), "tool argument schema copied");
            AssertTrue(call.SelectToken("properties.arguments.additionalProperties").Value<bool>() == false, "tool arguments remain strict");
            AssertTrue(schema["additionalProperties"].Value<bool>() == false, "agent response root is strict");
        }

        private static void AgentJsonSchemaSupportsTypeNamedArguments()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tool = executor.GetControllerTools().Single(candidate => candidate.Id == "common.tools_create");
                var schema = JObject.Parse(AgentResponseSchemaBuilder.Build(new[] { tool }));
                AssertEqual("string",
                    (string)schema.SelectToken("properties.tool_calls.items.anyOf[0].properties.arguments.properties.parameters.properties.type.type"),
                    "schema property named type");
            });
        }

        private static void AgentSupportsSelectableToolResultRoles()
        {
            var call = new AgentToolCall
            {
                Id = "call_1",
                Name = "excel.read_range",
                Arguments = new Dictionary<string, object> { ["range"] = "A1" }
            };
            var command = new ToolCommand { ToolId = call.Name, ToolCallId = call.Id };
            var result = ToolResult.Ok("read", "{\"value\":1}");

            foreach (var role in new[] { ToolResultRoles.User, ToolResultRoles.Developer })
            {
                var callMessage = AgentJsonProtocol.CreateToolCallMessage(call, "Reading.", null, role);
                var resultMessage = AgentJsonProtocol.CreateToolResultMessage(command, result, role);
                AssertTrue(callMessage.ToolCalls.Count == 0, role + " uses JSON envelope history");
                AssertEqual(role, resultMessage.Role, role + " result role");
                AssertContains(resultMessage.Content, "TOOL_RESULT:", role + " result prefix");
            }

            var nativeCall = AgentJsonProtocol.CreateToolCallMessage(call, "Reading.", null, ToolResultRoles.Tool);
            var nativeResult = AgentJsonProtocol.CreateToolResultMessage(command, result, ToolResultRoles.Tool);
            var api = new LlmMessageBuilder().Build(new[] { nativeCall, nativeResult }, new AppSettings());
            var assistant = (JObject)api.Messages[0];
            var toolMessage = (JObject)api.Messages[1];
            AssertEqual("assistant", (string)assistant["role"], "native call role");
            AssertEqual("call_1", (string)assistant.SelectToken("tool_calls[0].id"), "native call id");
            AssertEqual("tool", (string)toolMessage["role"], "native result role");
            AssertEqual("call_1", (string)toolMessage["tool_call_id"], "native result matches call");
            AssertTrue(string.IsNullOrWhiteSpace((string)toolMessage["name"]) == false, "native tool name is API-safe");
        }

        private static void AgentJsonSchemaFallbackIsRequestLocal()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var formats = new List<string>();
                LlmCompletionDelegate completion = (completionSettings, messages, options, stream, cancellationToken) =>
                {
                    formats.Add(options.ResponseFormat);
                    if (formats.Count == 1)
                    {
                        throw new LlmRequestException(LlmFailureKind.ResponseFormatUnsupported, "json_schema unsupported");
                    }
                    return Task.FromResult(new LlmCompletionResult
                    {
                        Content = "{\"message\":\"Done.\",\"tool_calls\":[]}"
                    });
                };
                var settings = new AppSettings
                {
                    AgentResponseMode = AgentResponseModes.JsonSchema,
                    FallbackToJsonObject = true
                };
                var result = new AgentRunService(adapter, executor, completion).ExecuteAsync(
                    "Test.", NewSession(adapter), NewContext(adapter), settings, new ToolDefinition[0],
                    null, null, null, CancellationToken.None).GetAwaiter().GetResult();

                AssertEqual("Done.", result.AssistantText, "fallback completes request");
                AssertEqual(2, formats.Count, "fallback makes one retry");
                AssertEqual(LlmResponseFormats.JsonSchema, formats[0], "selected format is tried first");
                AssertEqual(LlmResponseFormats.JsonObject, formats[1], "fallback uses json_object");
                AssertEqual(AgentResponseModes.JsonSchema, settings.AgentResponseMode, "saved selection is unchanged");
            });
        }

        private static void AgentToolResultDataIsBounded()
        {
            var command = new ToolCommand { ToolId = "excel.read_range", ToolCallId = "call_large" };
            var data = JsonConvert.SerializeObject(new { value = new string('x', 50000) });
            var result = JObject.Parse(AgentJsonProtocol.BuildToolResult(command, ToolResult.Ok("read", data), 256));

            AssertTrue(result.SelectToken("data.truncated").Value<bool>(), "oversized data is marked truncated");
            AssertTrue(result.SelectToken("data.original_chars").Value<int>() > 49000, "original size retained");
            AssertTrue(((string)result.SelectToken("data.preview") ?? string.Empty).Length < 1000, "preview is bounded");
            AssertEqual("call_large", (string)result["tool_call_id"], "tool call id retained");
        }

        private static void AgentToolResultFitsRemainingPromptBudget()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.QueueResult("excel.list_sheets", ToolResult.Ok(
                    "large read",
                    JsonConvert.SerializeObject(new { value = new string('x', 100000) })));
                var responses = new Queue<string>(new[]
                {
                    "{\"message\":\"Читаю.\",\"tool_calls\":[{\"id\":\"call_large\",\"name\":\"excel.list_sheets\",\"arguments\":{}}]}",
                    "{\"message\":\"Диапазон результата нужно сузить.\",\"tool_calls\":[]}"
                });
                var calls = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (completionSettings, messages, options, stream, cancellationToken) =>
                {
                    calls.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var settings = new AppSettings
                {
                    ContextWindowOverrideTokens = 12000,
                    MaxTokens = 512
                };
                var tools = adapter.GetBuiltInTools().Where(tool => tool.Id == "excel.list_sheets").ToList();

                var turn = new AgentRunService(adapter, executor, completion).ExecuteAsync(
                    "List sheets.", NewSession(adapter), NewContext(adapter), settings, tools,
                    null, null, null, null, CancellationToken.None, true).GetAwaiter().GetResult();

                AssertEqual("Диапазон результата нужно сузить.", turn.AssistantText, "agent continues after bounded result");
                AssertEqual(2, calls.Count, "two model calls");
                var replay = FlattenSimple(calls[1]);
                AssertContains(replay, "\"truncated\":true", "bounded marker reaches model");
                var estimated = ModelContextBudget.EstimateMessagesTokens(calls[1]) +
                    ModelContextBudget.EstimateRequestOptionsTokens(new LlmRequestOptions { ResponseFormat = LlmResponseFormats.JsonObject });
                AssertTrue(estimated <= ModelContextBudget.InputBudgetTokens(settings), "next prompt stays within budget");
            });
        }

        private static void ModelCompatibilityAcceptsExactSentinels()
        {
            var responses = new Queue<string>(new[]
            {
                "ROLE_OK",
                "{\"message\":\"TOOL_OK\",\"tool_calls\":[{\"id\":\"call_1\",\"name\":\"compat.echo\",\"arguments\":{\"value\":\"A\"}}]}",
                "{\"message\":\"RESULT_OK\",\"tool_calls\":[]}"
            });
            var requests = new List<Tuple<List<ChatMessage>, LlmRequestOptions>>();
            LlmCompletionDelegate completion = (providerSettings, messages, options, stream, cancellationToken) =>
            {
                requests.Add(Tuple.Create(messages.ToList(), options));
                return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
            };

            var settings = new AppSettings
            {
                SystemPromptRole = "system",
                AgentResponseMode = AgentResponseModes.JsonSchema,
                ToolResultRole = ToolResultRoles.Tool
            };
            var result = new ModelCompatibilityService(completion).TestAsync(settings, CancellationToken.None)
                .GetAwaiter().GetResult();

            AssertTrue(result.Compatible, "exact compatibility flow accepted");
            AssertTrue(result.Checks.All(check => check.Passed), "all exact probes pass");
            AssertEqual("system", result.InstructionRole, "selected instruction role reported");
            AssertEqual(AgentResponseModes.JsonSchema, result.ResponseMode, "selected response mode reported");
            AssertEqual(ToolResultRoles.Tool, result.ToolResultRole, "selected tool result role reported");
            AssertEqual(LlmResponseFormats.JsonSchema, requests[1].Item2.ResponseFormat, "compatibility uses selected schema mode");
            AssertTrue(requests[2].Item1.Any(message => string.Equals(message.Role, "tool", StringComparison.Ordinal)),
                "compatibility uses selected tool role");
            AssertTrue(requests[2].Item1.Any(message => message.ToolCalls != null && message.ToolCalls.Count == 1),
                "compatibility sends matched assistant tool call");
        }

        private static void ModelCompatibilityRejectsLooseResponses()
        {
            var responses = new Queue<string>(new[]
            {
                "Any non-empty response",
                "{\"message\":\"TOOL_OK\",\"tool_calls\":[{\"id\":\"call_1\",\"name\":\"compat.echo\",\"arguments\":{\"value\":\"WRONG\"}}]}",
                "{\"message\":\"Any final message\",\"tool_calls\":[]}"
            });
            LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
                Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });

            var result = new ModelCompatibilityService(completion).TestAsync(new AppSettings(), CancellationToken.None)
                .GetAwaiter().GetResult();

            AssertTrue(!result.Compatible, "loose compatibility flow rejected");
            AssertTrue(result.Checks.All(check => !check.Passed), "each loose probe fails");
        }
    }
}
