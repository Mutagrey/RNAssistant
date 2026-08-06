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
                            new LlmToolCall { Id = "call-test", Name = "rnassistant_compat_echo", ArgumentsJson = "{\"value\":\"TOOL_OK\"}" }
                        }
                    });
                }
                if (options != null &&
                    (options.ResponseFormat == LlmResponseFormats.JsonObject || options.ResponseFormat == LlmResponseFormats.JsonSchema))
                {
                    return Task.FromResult(new LlmCompletionResult
                    {
                        Content = "{\"protocolVersion\":1,\"kind\":\"final\",\"decisionSummary\":\"FORMAT_OK\",\"goal\":null,\"plan\":null,\"tool\":null,\"message\":\"FORMAT_OK\"}"
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
        }
    }
}
