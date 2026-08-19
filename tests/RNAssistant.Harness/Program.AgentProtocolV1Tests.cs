using System;
using System.Collections.Generic;
using System.Linq;
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

            var multiToolSchema = JObject.Parse(AgentDecisionSchemaBuilder.Build(new[]
            {
                new ToolDefinition { Id = "compat.read", ArgumentSchemaJson = EmptyFormalToolSchema }
            }));
            AssertEqual("array", (string)multiToolSchema.SelectToken("properties.tool.anyOf[0].type"), "tool decision schema uses an array");
            AssertEqual(AgentDecisionProtocol.MaxToolCallsPerDecision, (int)multiToolSchema.SelectToken("properties.tool.anyOf[0].maxItems"), "tool decision schema bounds batch size");

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
            AssertEqual(true, (bool)nativeBody["parallel_tool_calls"], "multi-tool calls enabled");
            AssertEqual("rna_excel_get_context", (string)nativeBody.SelectToken("tools[0].function.name"), "native function name");
            AssertEqual(true, (bool)nativeBody.SelectToken("tools[0].function.strict"), "native function strict flag");

            settings.ModelCapabilities["test-model"] = new ModelCapabilitySettings { SupportsReasoning = true };
            var reasoningBody = LlmClient.BuildRequestBody(settings, messages, 10, new LlmRequestOptions { ReasoningEnabled = true });
            AssertEqual("medium", (string)reasoningBody["reasoning_effort"], "reasoning toggle enables medium effort");
            var noReasoningBody = LlmClient.BuildRequestBody(settings, messages, 10, new LlmRequestOptions { ReasoningEnabled = false });
            AssertEqual("none", (string)noReasoningBody["reasoning_effort"], "reasoning toggle disables effort");

            settings.ReasoningRequestMode = ReasoningRequestModes.EnableThinking;
            var enableThinkingBody = LlmClient.BuildRequestBody(settings, messages, 10, new LlmRequestOptions { ReasoningEnabled = true });
            AssertEqual(true, (bool)enableThinkingBody["enable_thinking"], "reasoning toggle supports enable_thinking boolean");
            var disableThinkingBody = LlmClient.BuildRequestBody(settings, messages, 10, new LlmRequestOptions { ReasoningEnabled = false });
            AssertEqual(false, (bool)disableThinkingBody["enable_thinking"], "reasoning toggle disables enable_thinking boolean");

            settings.ReasoningRequestMode = ReasoningRequestModes.ChatTemplateKwargs;
            var kwargsBody = LlmClient.BuildRequestBody(settings, messages, 10, new LlmRequestOptions { ReasoningEnabled = true });
            AssertEqual(true, (bool)kwargsBody.SelectToken("chat_template_kwargs.enable_thinking"), "reasoning toggle supports vLLM chat template kwargs");

            settings.ReasoningRequestMode = ReasoningRequestModes.ReasoningEnabled;
            var reasoningEnabledBody = LlmClient.BuildRequestBody(settings, messages, 10, new LlmRequestOptions { ReasoningEnabled = false });
            AssertEqual(false, (bool)reasoningEnabledBody.SelectToken("reasoning.enabled"), "reasoning toggle supports reasoning enabled object");

            settings.ReasoningRequestMode = ReasoningRequestModes.CustomJson;
            settings.ReasoningCustomJson = "{\"reasoning\":{\"effort\":\"high\",\"summary\":\"auto\"},\"provider_flag\":true}";
            var customReasoningBody = LlmClient.BuildRequestBody(settings, messages, 10, new LlmRequestOptions { ReasoningEnabled = true });
            AssertEqual("high", (string)customReasoningBody.SelectToken("reasoning.effort"), "custom reasoning json merges nested object");
            AssertEqual("auto", (string)customReasoningBody.SelectToken("reasoning.summary"), "custom reasoning json preserves values");
            AssertEqual(true, (bool)customReasoningBody["provider_flag"], "custom reasoning json merges top-level fields");
            var customReasoningDisabledBody = LlmClient.BuildRequestBody(settings, messages, 10, new LlmRequestOptions { ReasoningEnabled = false });
            AssertTrue(customReasoningDisabledBody["reasoning"] == null && customReasoningDisabledBody["provider_flag"] == null, "custom reasoning json omitted when toggle is off");

            var invalidCustomRejected = false;
            settings.ReasoningCustomJson = "{invalid}";
            try
            {
                LlmClient.BuildRequestBody(settings, messages, 10, new LlmRequestOptions { ReasoningEnabled = true });
            }
            catch (InvalidOperationException ex)
            {
                invalidCustomRejected = true;
                AssertContains(ex.Message, "valid JSON object", "custom reasoning invalid json diagnostic");
            }
            AssertTrue(invalidCustomRejected, "custom reasoning rejects invalid json");

            var reservedCustomRejected = false;
            settings.ReasoningCustomJson = "{\"model\":\"other-model\"}";
            try
            {
                LlmClient.BuildRequestBody(settings, messages, 10, new LlmRequestOptions { ReasoningEnabled = true });
            }
            catch (InvalidOperationException ex)
            {
                reservedCustomRejected = true;
                AssertContains(ex.Message, "reserved request field", "custom reasoning reserved field diagnostic");
            }
            AssertTrue(reservedCustomRejected, "custom reasoning protects request fields");
            settings.ReasoningCustomJson = "{}";

            settings.ModelCapabilities["test-model"].ReasoningRequestMode = ReasoningRequestModes.EnableThinking;
            var modelOverrideBody = LlmClient.BuildRequestBody(settings, messages, 10, new LlmRequestOptions { ReasoningEnabled = true });
            AssertEqual(true, (bool)modelOverrideBody["enable_thinking"], "model reasoning request mode overrides global setting");
            AssertTrue(modelOverrideBody["reasoning"] == null, "model reasoning override omits global transport");
            settings.ModelCapabilities["test-model"].ReasoningRequestMode = null;
            settings.ReasoningRequestMode = ReasoningRequestModes.Auto;

            settings.Model = "text-model";
            settings.ModelCapabilities["text-model"] = new ModelCapabilitySettings { SupportsReasoning = false };
            var unsupportedReasoningBody = LlmClient.BuildRequestBody(settings, messages, 10, new LlmRequestOptions { ReasoningEnabled = true });
            AssertTrue(unsupportedReasoningBody["reasoning_effort"] == null, "non-reasoning model omits reasoning effort");
            settings.Model = "test-model";

            var nativeDecisionSchema = JObject.Parse(AgentDecisionSchemaBuilder.Build(new ToolDefinition[0], false));
            AssertTrue(!((JArray)nativeDecisionSchema.SelectToken("properties.kind.enum")).Values<string>().Contains("tool"), "native content schema excludes tool decisions");

            var continuationDecisionSchema = JObject.Parse(AgentDecisionSchemaBuilder.Build(new ToolDefinition[0], true, false));
            AssertTrue(!((JArray)continuationDecisionSchema.SelectToken("properties.kind.enum")).Values<string>().Contains("plan"), "continuation schema excludes plan decisions");
            AssertEqual("null", (string)continuationDecisionSchema.SelectToken("properties.plan.type"), "continuation schema requires null plan field");

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

            var apiTools = ToolSchemaSupport.BuildApiTools(new[] { localTool });
            var visibleNative = new AgentPlannerResponseParser().ParseNative(
                new LlmCompletionResult
                {
                    Content = "Контекст определен. Читаю текущий диапазон.",
                    ToolCalls = new List<LlmToolCall>
                    {
                        new LlmToolCall { Id = "call_visible", Name = apiTools[0].ApiName, ArgumentsJson = "{}" }
                    }
                },
                new[] { localTool },
                apiTools);
            AssertTrue(visibleNative.Success, "native progress text parses with tool call");
            AssertEqual("Контекст определен. Читаю текущий диапазон.", visibleNative.Response.DecisionSummary, "native progress text remains visible");

            var multiNative = new AgentPlannerResponseParser().ParseNative(
                new LlmCompletionResult
                {
                    Content = "Читаю независимые части книги.",
                    ToolCalls = new List<LlmToolCall>
                    {
                        new LlmToolCall { Id = "call_multi_1", Name = apiTools[0].ApiName, ArgumentsJson = "{}" },
                        new LlmToolCall { Id = "call_multi_2", Name = apiTools[0].ApiName, ArgumentsJson = "{}" }
                    }
                },
                new[] { localTool },
                apiTools);
            AssertTrue(multiNative.Success, "native multi-tool response parses");
            AssertEqual(2, multiNative.Response.Tools.Count, "native multi-tool count");
            AssertEqual("call_multi_2", multiNative.Response.Tools[1].ToolCallId, "native multi-tool call id");
        }

        private static void LlmSerializesOpenAiToolRoundTrip()
        {
            var call = new LlmToolCall
            {
                Id = "call_1",
                Name = "rna_excel_get_context",
                ArgumentsJson = "{}"
            };
            var source = new[]
            {
                new ChatMessage { Role = "assistant", Content = string.Empty, ToolCalls = new List<LlmToolCall> { call } },
                new ChatMessage { Role = "tool", ToolCallId = "call_1", ToolName = call.Name, Content = "{\"ok\":true}" },
                new ChatMessage { Role = "assistant", Content = "EXCLUDED_DIAGNOSTIC", ExcludeFromModelContext = true }
            };
            var serialized = new LlmMessageBuilder().Build(source, null).Messages;
            var json = JArray.FromObject(serialized);

            AssertEqual("assistant", (string)json.SelectToken("[0].role"), "assistant tool-call role");
            AssertEqual(string.Empty, (string)json.SelectToken("[0].content"), "assistant tool-call content is a string");
            AssertEqual("call_1", (string)json.SelectToken("[0].tool_calls[0].id"), "assistant tool-call id");
            AssertEqual("tool", (string)json.SelectToken("[1].role"), "tool result role");
            AssertEqual("call_1", (string)json.SelectToken("[1].tool_call_id"), "tool result call id");
            AssertContains((string)json.SelectToken("[1].content"), "\"ok\":true", "tool result content");
            AssertEqual(2, json.Count, "excluded diagnostic is not serialized");
        }

        private static void AgentProtocolHistoryNormalizesDuplicateNativeCallIds()
        {
            var commands = new[]
            {
                new ToolCommand { ToolId = "excel.get_context", ToolCallId = "duplicate_call" },
                new ToolCommand { ToolId = "excel.get_selection", ToolCallId = "duplicate_call" }
            };
            var attempt = new AgentPlannerAttempt
            {
                Text = "Читаю контекст и выделение.",
                Completion = new LlmCompletionResult
                {
                    ToolCalls = new List<LlmToolCall>
                    {
                        new LlmToolCall { Id = "duplicate_call", Name = "rna_excel_get_context", ArgumentsJson = "{}" },
                        new LlmToolCall { Id = "duplicate_call", Name = "rna_excel_get_selection", ArgumentsJson = "{}" }
                    }
                },
                RequestOptions = new LlmRequestOptions
                {
                    Tools = new[]
                    {
                        new LlmToolDefinition { ToolId = "excel.get_context", ApiName = "rna_excel_get_context" },
                        new LlmToolDefinition { ToolId = "excel.get_selection", ApiName = "rna_excel_get_selection" }
                    }
                }
            };
            var messages = new List<ChatMessage>();
            AgentProtocolHistory.AppendToolExchanges(
                messages,
                attempt,
                new[]
                {
                    new AgentToolExchange(commands[0], ToolResult.Ok("context")),
                    new AgentToolExchange(commands[1], ToolResult.Ok("selection"))
                },
                new AppSettings { ToolResultRole = "tool" });

            var assistant = messages[0];
            AssertEqual(2, assistant.ToolCalls.Count, "native duplicate-id batch call count");
            AssertTrue(!string.Equals(assistant.ToolCalls[0].Id, assistant.ToolCalls[1].Id, StringComparison.Ordinal), "duplicate native ids are normalized");
            AssertEqual("rna_excel_get_context", assistant.ToolCalls[0].Name, "first native call keeps positional name");
            AssertEqual("rna_excel_get_selection", assistant.ToolCalls[1].Name, "second native call keeps positional name");
            AssertEqual(assistant.ToolCalls[0].Id, messages[1].ToolCallId, "first normalized result id matches");
            AssertEqual(assistant.ToolCalls[1].Id, messages[2].ToolCallId, "second normalized result id matches");
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
                AssertContains(FlattenMessages(calls[0]), "one or more native function calls", "native transport prompt");
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
                        AssertEqual(assistant.ToolCalls[0].Name, resultMessage.ToolName, "synthetic JSON tool name");
                        new LlmMessageBuilder().Build(calls[1], null);
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
                        modes.Add(requestOptions.ResponseFormat);
                        calls.Add(new List<ChatMessage>(messages));
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
                AssertContains(FlattenMessages(calls[0]), "responseMode: json_schema", "initial prompt mode");
                AssertContains(FlattenMessages(calls[1]), "responseMode: json_object", "fallback prompt mode switches with transport");
            });
        }

        private static void AgentTimeoutDoesNotTriggerSchemaFallback()
        {
            var calls = 0;
            var runner = new AgentPlannerCompletionRunner(delegate
            {
                calls += 1;
                return Task.FromException<LlmCompletionResult>(new LlmRequestException(
                    LlmFailureKind.Timeout,
                    "timeout"));
            });
            var thrown = false;
            try
            {
                runner.CompleteAsync(
                    new AppSettings(),
                    new[] { new ChatMessage { Role = "user", Content = "test" } },
                    new ToolDefinition[0],
                    new AgentRunState(),
                    null,
                    null,
                    "thinking",
                    "repairing",
                    "repair",
                    CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (LlmRequestException ex)
            {
                thrown = ex.Kind == LlmFailureKind.Timeout;
            }
            AssertTrue(thrown, "timeout propagated");
            AssertEqual(1, calls, "timeout is not retried as json_object");
        }

        private static void AgentSchemaRejectionTriggersFallback()
        {
            var modes = new List<string>();
            var runner = new AgentPlannerCompletionRunner(delegate(
                AppSettings settings,
                IEnumerable<ChatMessage> messages,
                LlmRequestOptions requestOptions,
                Action<LlmStreamUpdate> progress,
                CancellationToken cancellationToken)
            {
                modes.Add(requestOptions.ResponseFormat);
                if (modes.Count == 1)
                {
                    return Task.FromException<LlmCompletionResult>(new LlmRequestException(
                        LlmFailureKind.ResponseFormatUnsupported,
                        "response_format is unsupported"));
                }
                return Task.FromResult(new LlmCompletionResult { Content = FinalBlock("ok") });
            });

            var attempt = runner.CompleteAsync(
                new AppSettings(),
                new[] { new ChatMessage { Role = "user", Content = "test" } },
                new ToolDefinition[0],
                new AgentRunState(),
                null,
                null,
                "thinking",
                "repairing",
                "repair",
                CancellationToken.None).GetAwaiter().GetResult();

            AssertTrue(attempt.ParseResult.Success, "explicit schema rejection falls back successfully");
            AssertEqual(2, modes.Count, "schema rejection retries once");
            AssertEqual(LlmResponseFormats.JsonSchema, modes[0], "schema rejection starts in schema mode");
            AssertEqual(LlmResponseFormats.JsonObject, modes[1], "schema rejection retries in object mode");
        }

        private static void AgentProviderRefusalRepairsWithSameTools()
        {
            var tool = new ToolDefinition
            {
                Id = "excel.get_context",
                Name = "Get context",
                Host = "Excel",
                Enabled = true,
                AgentCanRun = true,
                ArgumentSchemaJson = EmptyFormalToolSchema
            };
            var requestTools = new List<string[]>();
            var messages = new List<string>();
            var optionsSeen = new List<LlmRequestOptions>();
            var turn = 0;
            var runner = new AgentPlannerCompletionRunner(delegate(
                AppSettings settings,
                IEnumerable<ChatMessage> requestMessages,
                LlmRequestOptions requestOptions,
                Action<LlmStreamUpdate> progress,
                CancellationToken cancellationToken)
            {
                requestTools.Add(requestOptions.Tools.Select(item => item.ToolId).ToArray());
                messages.Add(FlattenMessages(requestMessages));
                optionsSeen.Add(requestOptions);
                turn += 1;
                return Task.FromResult(turn == 1
                    ? new LlmCompletionResult { RefusalContent = "safety proxy response" }
                    : new LlmCompletionResult { Content = FinalBlock("Recovered.") });
            });
            var prepared = AgentPlannerCompletionRunner.BuildOptions(
                AgentResponseModes.JsonObject,
                new[] { tool },
                new LlmRunCache());

            var attempt = runner.CompleteAsync(
                new AppSettings { AgentResponseMode = AgentResponseModes.JsonObject, MaxAgentFormatRetries = 2 },
                new[] { new ChatMessage { Role = "user", Content = "Read workbook" } },
                new[] { tool },
                new AgentRunState { ResponseMode = AgentResponseModes.JsonObject },
                prepared,
                null,
                "thinking",
                "repairing",
                "repair",
                CancellationToken.None).GetAwaiter().GetResult();

            AssertTrue(attempt.ParseResult.Success, "provider refusal is repaired");
            AssertEqual("Recovered.", attempt.ParseResult.Response.Message, "repaired final response");
            AssertEqual(2, requestTools.Count, "one refusal repair request");
            AssertEqual(string.Join(",", requestTools[0]), string.Join(",", requestTools[1]), "tool slice survives refusal repair");
            AssertTrue(object.ReferenceEquals(optionsSeen[0].RunCache, optionsSeen[1].RunCache), "run cache survives refusal repair");
            AssertContains(messages[1], "upstream refusal is not executable output", "refusal repair guidance");
            AssertTrue(messages[1].IndexOf("safety proxy response", StringComparison.Ordinal) < 0, "raw refusal is not replayed");
        }

        private static void AgentTransientInvalidResponseRetriesWithSameTools()
        {
            var tool = new ToolDefinition
            {
                Id = "excel.get_context",
                Name = "Get context",
                Host = "Excel",
                Enabled = true,
                AgentCanRun = true,
                ArgumentSchemaJson = EmptyFormalToolSchema
            };
            var calls = new List<Tuple<string, string, LlmRequestOptions>>();
            var turn = 0;
            var runner = new AgentPlannerCompletionRunner(delegate(
                AppSettings settings,
                IEnumerable<ChatMessage> requestMessages,
                LlmRequestOptions requestOptions,
                Action<LlmStreamUpdate> progress,
                CancellationToken cancellationToken)
            {
                calls.Add(Tuple.Create(
                    FlattenMessages(requestMessages),
                    string.Join(",", requestOptions.Tools.Select(item => item.ToolId)),
                    requestOptions));
                turn += 1;
                if (turn == 1)
                {
                    return Task.FromException<LlmCompletionResult>(new LlmRequestException(
                        LlmFailureKind.InvalidResponse,
                        "temporary malformed gateway response"));
                }
                return Task.FromResult(new LlmCompletionResult { Content = FinalBlock("Recovered.") });
            });

            var attempt = runner.CompleteAsync(
                new AppSettings { AgentResponseMode = AgentResponseModes.JsonObject },
                new[] { new ChatMessage { Role = "user", Content = "Read workbook" } },
                new[] { tool },
                new AgentRunState { ResponseMode = AgentResponseModes.JsonObject },
                null,
                null,
                "thinking",
                "repairing",
                "repair",
                CancellationToken.None).GetAwaiter().GetResult();

            AssertTrue(attempt.ParseResult.Success, "transient invalid response recovers");
            AssertEqual(2, calls.Count, "invalid response retried once");
            AssertEqual(calls[0].Item1, calls[1].Item1, "transport retry preserves prompt");
            AssertEqual(calls[0].Item2, calls[1].Item2, "transport retry preserves tool slice");
            AssertTrue(object.ReferenceEquals(calls[0].Item3, calls[1].Item3), "transport retry preserves request options");
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
