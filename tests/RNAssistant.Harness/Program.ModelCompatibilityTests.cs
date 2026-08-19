using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ModelCompatibilityChecksRolesAndFormats()
        {
            var calls = new List<Tuple<List<ChatMessage>, LlmRequestOptions>>();
            var service = new ModelCompatibilityService(delegate(
                AppSettings settings,
                IEnumerable<ChatMessage> messages,
                LlmRequestOptions options,
                Action<LlmStreamUpdate> progress,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var copied = new List<ChatMessage>(messages ?? new ChatMessage[0]);
                calls.Add(Tuple.Create(copied, options));
                if (options != null && options.NativeTools)
                {
                    return Task.FromResult(new LlmCompletionResult
                    {
                        ToolCalls = new List<LlmToolCall>
                        {
                            new LlmToolCall { Id = "call-test-a", Name = "rnassistant_compat_echo_a", ArgumentsJson = "{\"value\":\"A\"}" },
                            new LlmToolCall { Id = "call-test-b", Name = "rnassistant_compat_echo_b", ArgumentsJson = "{\"value\":\"B\"}" }
                        }
                    });
                }
                if (options != null &&
                    (options.ResponseFormat == LlmResponseFormats.JsonObject || options.ResponseFormat == LlmResponseFormats.JsonSchema))
                {
                    return Task.FromResult(new LlmCompletionResult
                    {
                        Content = "{\"protocolVersion\":1,\"kind\":\"tool\",\"decisionSummary\":\"MULTI_OK\",\"goal\":null,\"plan\":null,\"tool\":[{\"toolId\":\"compat.echo_a\",\"arguments\":{\"value\":\"A\"}},{\"toolId\":\"compat.echo_b\",\"arguments\":{\"value\":\"B\"}}],\"message\":null}"
                    });
                }
                return Task.FromResult(new LlmCompletionResult { Content = "OK" });
            });

            var result = service.TestAsync(new AppSettings
            {
                SystemPromptRole = "developer",
                ToolResultRole = "tool",
                AgentResponseMode = AgentResponseModes.JsonSchema
            }, CancellationToken.None).GetAwaiter().GetResult();

            AssertTrue(result.Compatible, "selected configuration passes");
            AssertEqual(7, result.Checks.Count, "compatibility check count");
            AssertEqual(7, calls.Count, "compatibility request count");
            AssertTrue(calls.Any(call => call.Item1.Any(message => message.Role == "system")), "system role probed");
            AssertTrue(calls.Any(call => call.Item1.Any(message => message.Role == "developer")), "developer role probed");
            AssertTrue(calls.Any(call => call.Item1.Any(message => message.Role == "tool" && message.ToolCallId == "call_rnassistant_compat")), "matching tool history probed");
            AssertTrue(calls.Any(call => call.Item2.ResponseFormat == LlmResponseFormats.JsonObject), "json_object probed");
            AssertTrue(calls.Any(call => call.Item2.ResponseFormat == LlmResponseFormats.JsonSchema), "json_schema probed");
            AssertTrue(calls.Any(call => call.Item2.NativeTools), "native tools probed");
            AssertTrue(result.Checks.Any(check => check.Id == "json_object" && check.Passed), "json_object multi-tool capability probed");
            AssertTrue(result.Checks.Any(check => check.Id == "json_schema" && check.Passed), "json_schema multi-tool capability probed");
            AssertTrue(result.Checks.Any(check => check.Id == "native_multi_tool_calls" && check.Passed), "native multi-tool capability probed");
        }
    }
}
