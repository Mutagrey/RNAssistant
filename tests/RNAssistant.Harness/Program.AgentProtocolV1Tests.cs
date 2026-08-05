using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
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
        private static void LlmRequestBodySupportsAllAgentResponseModes()
        {
            var settings = new AppSettings { Model = "test-model", StreamResponses = false };
            var messages = new List<object> { new JObject { ["role"] = "user", ["content"] = "test" } };
            var schema = AgentDecisionSchemaBuilder.Build(new ToolDefinition[0]);

            var jsonObjectBody = LlmClient.BuildRequestBody(settings, messages, 10, new LlmRequestOptions
            {
                ResponseFormat = LlmResponseFormats.JsonObject
            });
            AssertEqual("json_object", (string)jsonObjectBody.SelectToken("response_format.type"), "json_object request type");
            AssertTrue(jsonObjectBody["tools"] == null, "json_object request has no native tools");

            var jsonSchemaBody = LlmClient.BuildRequestBody(settings, messages, 10, new LlmRequestOptions
            {
                ResponseFormat = LlmResponseFormats.JsonSchema,
                ResponseSchemaName = AgentDecisionProtocol.SchemaName,
                ResponseSchemaJson = schema
            });
            AssertEqual("json_schema", (string)jsonSchemaBody.SelectToken("response_format.type"), "json_schema request type");
            AssertEqual(true, (bool)jsonSchemaBody.SelectToken("response_format.json_schema.strict"), "response schema strict flag");
            AssertEqual(AgentDecisionProtocol.SchemaName, (string)jsonSchemaBody.SelectToken("response_format.json_schema.name"), "response schema name");

            var nativeBody = LlmClient.BuildRequestBody(settings, messages, 10, new LlmRequestOptions
            {
                ResponseFormat = LlmResponseFormats.JsonSchema,
                ResponseSchemaName = AgentDecisionProtocol.SchemaName,
                ResponseSchemaJson = schema,
                NativeTools = true,
                Tools = new[]
                {
                    new LlmToolDefinition
                    {
                        ToolId = "excel.get_context",
                        ApiName = "rna_excel_get_context",
                        Description = "Read context",
                        ParametersSchemaJson = EmptyFormalToolSchema
                    }
                }
            });
            AssertEqual("auto", (string)nativeBody["tool_choice"], "native tool choice");
            AssertEqual(false, (bool)nativeBody["parallel_tool_calls"], "parallel calls disabled");
            AssertEqual("rna_excel_get_context", (string)nativeBody.SelectToken("tools[0].function.name"), "native function name");
            AssertEqual(true, (bool)nativeBody.SelectToken("tools[0].function.strict"), "native function strict flag");

            var nativeDecisionSchema = JObject.Parse(AgentDecisionSchemaBuilder.Build(new ToolDefinition[0], false));
            AssertTrue(!((JArray)nativeDecisionSchema.SelectToken("properties.kind.enum")).Values<string>().Contains("tool"), "native content schema excludes tool decisions");

            var localTool = new ToolDefinition
            {
                Id = "excel.get_context",
                Host = "Excel",
                BuiltIn = true,
                ArgumentSchemaJson = EmptyFormalToolSchema
            };
            var contentTool = new AgentPlannerResponseParser().ParseNative(
                new LlmCompletionResult { Content = AgentBlock(Command(localTool.Id)) },
                new[] { localTool },
                ToolSchemaSupport.BuildApiTools(new[] { localTool }));
            AssertEqual("native_tool_call_required", contentTool.ErrorCode, "native mode rejects content-based tool selection");
        }

        private static void LlmSerializesOpenAiToolRoundTrip()
        {
            var client = new LlmClient(() => string.Empty);
            var method = typeof(LlmClient).GetMethod("ToApiMessages", BindingFlags.Instance | BindingFlags.NonPublic);
            var call = new LlmToolCall
            {
                Id = "call_1",
                Name = "rna_excel_get_context",
                ArgumentsJson = "{}"
            };
            var source = new[]
            {
                new ChatMessage { Role = "assistant", Content = string.Empty, ToolCalls = new List<LlmToolCall> { call } },
                new ChatMessage { Role = "tool", ToolCallId = "call_1", ToolName = call.Name, Content = "{\"ok\":true}" }
            };
            var serialized = (List<object>)method.Invoke(client, new object[] { source });
            var json = JArray.FromObject(serialized);

            AssertEqual("assistant", (string)json.SelectToken("[0].role"), "assistant tool-call role");
            AssertEqual("call_1", (string)json.SelectToken("[0].tool_calls[0].id"), "assistant tool-call id");
            AssertEqual("tool", (string)json.SelectToken("[1].role"), "tool result role");
            AssertEqual("call_1", (string)json.SelectToken("[1].tool_call_id"), "tool result call id");
            AssertContains((string)json.SelectToken("[1].content"), "\"ok\":true", "tool result content");
        }

        private static void AgentNativeToolCallRoundTripUsesMatchingCallId()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = new List<List<ChatMessage>>();
                var options = new List<LlmRequestOptions>();
                var turn = 0;
                var service = new ChatCompletionService(
                    adapter,
                    executor,
                    delegate(
                        AppSettings settings,
                        IEnumerable<ChatMessage> messages,
                        LlmRequestOptions requestOptions,
                        Action<LlmStreamUpdate> progress,
                        CancellationToken cancellationToken)
                    {
                        calls.Add(new List<ChatMessage>(messages));
                        options.Add(requestOptions);
                        if (turn++ == 0)
                        {
                            var apiTool = requestOptions.Tools.First(item => string.Equals(item.ToolId, "excel.get_context", StringComparison.OrdinalIgnoreCase));
                            return Task.FromResult(new LlmCompletionResult
                            {
                                Content = string.Empty,
                                ToolCalls = new List<LlmToolCall>
                                {
                                    new LlmToolCall { Id = "native_call_1", Name = apiTool.ApiName, ArgumentsJson = "{}" }
                                }
                            });
                        }
                        return Task.FromResult(new LlmCompletionResult { Content = FinalBlock("Context read.") });
                    });

                var result = service.ExecuteAsync(
                    "What is in the current workbook?",
                    NewSession(adapter),
                    NewContext(adapter),
                    new AppSettings { AgentResponseMode = AgentResponseModes.NativeToolCalls, ToolResultRole = "tool" },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Context read.", result.AssistantText, "native final response");
                AssertTrue(options[0].NativeTools, "native tools enabled");
                AssertEqual(LlmResponseFormats.JsonSchema, options[0].ResponseFormat, "native final response format");
                AssertContains(FlattenMessages(calls[0]), "emit exactly one native API function call", "native transport prompt");
                var assistantCall = calls[1].First(message => message.ToolCalls != null && message.ToolCalls.Count == 1);
                var toolResult = calls[1].First(message => string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase));
                AssertEqual("native_call_1", assistantCall.ToolCalls[0].Id, "native assistant call id");
                AssertEqual("native_call_1", toolResult.ToolCallId, "native result call id");
                AssertContains(toolResult.Content, "\"toolId\":\"excel.get_context\"", "normalized native observation");
            });
        }

        private static void AgentJsonToolResultRolesAreSelectable()
        {
            foreach (var role in new[] { "tool", "developer", "user" })
            {
                WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var calls = new List<List<ChatMessage>>();
                    var turn = 0;
                    var service = new ChatCompletionService(
                        adapter,
                        executor,
                        delegate(
                            AppSettings settings,
                            IEnumerable<ChatMessage> messages,
                            LlmRequestOptions requestOptions,
                            Action<LlmStreamUpdate> progress,
                            CancellationToken cancellationToken)
                        {
                            calls.Add(new List<ChatMessage>(messages));
                            return Task.FromResult(new LlmCompletionResult
                            {
                                Content = turn++ == 0 ? AgentBlock(Command("excel.get_context")) : FinalBlock("Done.")
                            });
                        });

                    service.ExecuteAsync(
                        "Read the current workbook.",
                        NewSession(adapter),
                        NewContext(adapter),
                        new AppSettings { AgentResponseMode = AgentResponseModes.JsonObject, ToolResultRole = role },
                        new List<ToolDefinition>(adapter.GetBuiltInTools()),
                        null).GetAwaiter().GetResult();

                    if (string.Equals(role, "tool", StringComparison.Ordinal))
                    {
                        var resultMessage = calls[1].First(message => string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase));
                        var assistant = calls[1].First(message => message.ToolCalls != null && message.ToolCalls.Count == 1);
                        AssertEqual(assistant.ToolCalls[0].Id, resultMessage.ToolCallId, "synthetic JSON call id");
                    }
                    else
                    {
                        var resultMessage = calls[1].First(message => string.Equals(message.Role, role, StringComparison.OrdinalIgnoreCase) && (message.Content ?? string.Empty).StartsWith("TOOL_RESULT:", StringComparison.Ordinal));
                        AssertContains(resultMessage.Content, "\"toolId\":\"excel.get_context\"", role + " result payload");
                    }
                });
            }
        }

        private static void AgentJsonSchemaFallbackPersistsForTheRun()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var modes = new List<string>();
                var turn = 0;
                var service = new ChatCompletionService(
                    adapter,
                    executor,
                    delegate(
                        AppSettings settings,
                        IEnumerable<ChatMessage> messages,
                        LlmRequestOptions requestOptions,
                        Action<LlmStreamUpdate> progress,
                        CancellationToken cancellationToken)
                    {
                        modes.Add(requestOptions.ResponseFormat);
                        turn += 1;
                        if (turn == 1) return Task.FromResult(new LlmCompletionResult { Content = "endpoint ignored schema" });
                        if (turn == 2) return Task.FromResult(new LlmCompletionResult { Content = AgentBlock(Command("excel.get_context")) });
                        return Task.FromResult(new LlmCompletionResult { Content = FinalBlock("Fallback complete.") });
                    });

                var result = service.ExecuteAsync(
                    "Read the current workbook.",
                    NewSession(adapter),
                    NewContext(adapter),
                    new AppSettings(),
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Fallback complete.", result.AssistantText, "fallback final response");
                AssertEqual(3, modes.Count, "fallback call count");
                AssertEqual(LlmResponseFormats.JsonSchema, modes[0], "initial schema mode");
                AssertEqual(LlmResponseFormats.JsonObject, modes[1], "fallback object mode");
                AssertEqual(LlmResponseFormats.JsonObject, modes[2], "fallback mode persisted after tool execution");
            });
        }

        private static void InvalidCustomToolSchemaIsIgnored()
        {
            WithTempPaths(delegate(RNAssistant.Core.Storage.AppDataPaths paths)
            {
                var store = new RNAssistant.Core.Storage.ToolStore(paths);
                var saved = store.SaveOne(new ToolDefinition
                {
                    Id = "excel.invalid_schema",
                    Host = "Excel",
                    Name = "Invalid schema",
                    ArgumentSchemaJson = "{\"sheet\":\"Report\"}",
                    Executor = "pipeline",
                    PipelineJson = "{\"steps\":[{\"toolId\":\"excel.list_sheets\",\"arguments\":{}}]}",
                    Enabled = true,
                    BuiltIn = false
                });
                AssertTrue(saved == null, "invalid schema not loaded after save");
                AssertTrue(!store.Load().Any(tool => tool.Id == "excel.invalid_schema"), "invalid schema omitted from store");
            });
        }
    }
}
