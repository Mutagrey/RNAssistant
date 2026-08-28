using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    internal sealed class ModelCompatibilityService
    {
        private readonly LlmCompletionDelegate _completeAsync;

        public ModelCompatibilityService(LlmCompletionDelegate completeAsync)
        {
            _completeAsync = completeAsync ?? throw new ArgumentNullException("completeAsync");
        }

        public async Task<ModelCompatibilityResponse> TestAsync(AppSettings settings, CancellationToken cancellationToken)
        {
            settings = (settings ?? new AppSettings()).Clone();
            settings.MaxTokens = Math.Max(256, settings.MaxTokens);
            settings.StreamResponses = false;
            settings.Temperature = 0;
            settings.TopP = 1;
            var instructionRole = NormalizeInstructionRole(settings.SystemPromptRole);
            var responseMode = AgentResponseModes.Normalize(settings.AgentResponseMode);
            var toolResultRole = ToolResultRoles.Normalize(settings.ToolResultRole);
            var checks = new List<ModelCompatibilityCheckDto>();
            using (var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                total.CancelAfter(TimeSpan.FromSeconds(90));
                checks.Add(await ProbeTextAsync(settings, instructionRole, total.Token).ConfigureAwait(false));
                checks.Add(await ProbeAgentJsonAsync(settings, responseMode, total.Token).ConfigureAwait(false));
                checks.Add(await ProbeToolResultAsync(settings, responseMode, toolResultRole, total.Token).ConfigureAwait(false));
            }
            var compatible = checks.All(check => check.Passed);
            return new ModelCompatibilityResponse
            {
                Compatible = compatible,
                Endpoint = settings.BaseUrl ?? string.Empty,
                Model = settings.Model ?? string.Empty,
                InstructionRole = instructionRole,
                ResponseMode = responseMode,
                ToolResultRole = toolResultRole,
                Summary = compatible
                    ? "Модель совместима с выбранным Agent-протоколом."
                    : "Модель не прошла обязательную проверку выбранного Agent-протокола.",
                Checks = checks
            };
        }

        private Task<ModelCompatibilityCheckDto> ProbeTextAsync(
            AppSettings settings,
            string instructionRole,
            CancellationToken cancellationToken)
        {
            return RunAsync(
                settings,
                "instruction_role",
                "Инструкция " + instructionRole,
                new[]
                {
                    new ChatMessage { Role = instructionRole, Content = "Reply with ROLE_OK." },
                    new ChatMessage { Role = "user", Content = "Reply with ROLE_OK." }
                },
                new LlmRequestOptions { ResponseFormat = LlmResponseFormats.Text },
                completion => completion != null && string.Equals((completion.Content ?? string.Empty).Trim(), "ROLE_OK", StringComparison.Ordinal)
                    ? null
                    : "Endpoint did not follow the selected instruction role exactly (expected ROLE_OK).",
                cancellationToken);
        }

        private Task<ModelCompatibilityCheckDto> ProbeAgentJsonAsync(
            AppSettings settings,
            string responseMode,
            CancellationToken cancellationToken)
        {
            var tool = new ToolDefinition
            {
                Id = "compat.echo",
                Description = "Compatibility probe.",
                ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\",\"description\":\"Compatibility probe value.\"}},\"required\":[\"value\"],\"additionalProperties\":false}"
            };
            var sentinel = ModelProtocolWire.Write("TOOL_OK", new[] { ProbeCall() });
            return RunAsync(
                settings,
                "agent_json",
                "Agent " + responseMode,
                new[]
                {
                    new ChatMessage
                    {
                        Role = "user",
                        Content = "Return exactly one JSON object: " + sentinel
                    }
                },
                ModelProtocolWire.CreateRequestOptions(responseMode, new[] { tool }),
                completion => ValidateSentinel(completion, sentinel, new[] { tool },
                    new ModelProtocolCallContext(new string[0])),
                cancellationToken);
        }

        private Task<ModelCompatibilityCheckDto> ProbeToolResultAsync(
            AppSettings settings,
            string responseMode,
            string toolResultRole,
            CancellationToken cancellationToken)
        {
            var sentinel = ModelProtocolWire.Write("RESULT_OK", new ConversationToolCall[0]);
            var messages = ToolResultProbeMessages(toolResultRole, sentinel);
            return RunAsync(
                settings,
                "tool_result_json",
                "Tool result · " + toolResultRole,
                messages,
                ModelProtocolWire.CreateRequestOptions(responseMode, new ToolDefinition[0]),
                completion => ValidateSentinel(completion, sentinel, new ToolDefinition[0],
                    new ModelProtocolCallContext(new string[0])),
                cancellationToken);
        }

        private static ConversationToolCall ProbeCall()
        {
            return new ConversationToolCall
            {
                Name = "compat.echo", Arguments = new Dictionary<string, object> { ["value"] = "A" }
            };
        }

        private static string ValidateSentinel(LlmCompletionResult completion, string sentinel,
            IReadOnlyList<ToolDefinition> tools, ModelProtocolCallContext context)
        {
            var actual = ModelProtocolWire.Parse(completion == null ? null : completion.Content, tools, tools, context);
            if (!actual.Success) return actual.Error;
            var expected = ModelProtocolWire.Parse(sentinel, tools, tools, context);
            if (!expected.Success) throw new InvalidOperationException("Invalid local compatibility sentinel: " + expected.Error);
            // Compare validated responses, not DTO serialization as a wire contract.
            // Exact message/calls must match; the v4 parser rejects additional fields.
            return JToken.DeepEquals(JToken.FromObject(actual.Response), JToken.FromObject(expected.Response))
                ? null : "Endpoint changed the required compatibility sentinel.";
        }

        private static IEnumerable<ChatMessage> ToolResultProbeMessages(string role, string finalSentinel)
        {
            role = ToolResultRoles.Normalize(role);
            const string resultJson = "{\"ok\":true,\"tool_call_id\":\"call_1\",\"name\":\"compat.echo\",\"status\":\"success\",\"message\":\"\",\"data\":{\"value\":\"A\"},\"error\":null}";
            var messages = new List<ChatMessage>
            {
                // Synthetic probe history has a local ID; it never authorizes a
                // real tool execution or asks the provider to allocate identity.
                AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall
                    { Id = "call_1", Name = ProbeCall().Name, Arguments = ProbeCall().Arguments },
                    "TOOL_OK", null, role, new AcceptedToolCallOrigin("compatibility-probe", "synthetic-attempt", 0))
            };
            if (string.Equals(role, ToolResultRoles.Tool, StringComparison.Ordinal))
            {
                var apiName = AgentJsonProtocol.ApiToolName("compat.echo");
                messages.Add(new ChatMessage
                {
                    Role = ToolResultRoles.Tool,
                    ToolCallId = "call_1",
                    ToolName = apiName,
                    Content = resultJson,
                    ProtocolMessage = true
                });
            }
            else
            {
                messages.Add(new ChatMessage
                {
                    Role = role,
                    Content = "TOOL_RESULT:\n" + resultJson,
                    ProtocolMessage = true
                });
            }
            messages.Add(new ChatMessage
            {
                Role = "user",
                Content = "Reply with " + finalSentinel + "."
            });
            return messages;
        }

        private async Task<ModelCompatibilityCheckDto> RunAsync(
            AppSettings settings,
            string id,
            string title,
            IEnumerable<ChatMessage> messages,
            LlmRequestOptions options,
            Func<LlmCompletionResult, string> validate,
            CancellationToken cancellationToken)
        {
            var watch = Stopwatch.StartNew();
            using (var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                probe.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, Math.Min(30, settings.RequestTimeoutSeconds))));
                try
                {
                    var completion = await _completeAsync(settings, messages, options, null, probe.Token).ConfigureAwait(false);
                    var error = completion != null && !string.IsNullOrWhiteSpace(completion.RefusalContent)
                        ? "Endpoint refused the compatibility probe."
                        : validate(completion);
                    return Check(id, title, string.IsNullOrWhiteSpace(error), watch.ElapsedMilliseconds,
                        string.IsNullOrWhiteSpace(error) ? "Поддерживается." : error);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return Check(id, title, false, watch.ElapsedMilliseconds, "Таймаут проверки.");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return Check(id, title, false, watch.ElapsedMilliseconds, BoundError(ex.Message));
                }
            }
        }

        private static ModelCompatibilityCheckDto Check(string id, string title, bool passed, long durationMs, string message)
        {
            return new ModelCompatibilityCheckDto
            {
                Id = id,
                Title = title,
                Required = true,
                Passed = passed,
                DurationMs = durationMs,
                Message = message ?? string.Empty
            };
        }

        private static string NormalizeInstructionRole(string value)
        {
            if (string.Equals(value, "system", StringComparison.OrdinalIgnoreCase)) return "system";
            if (string.Equals(value, "user", StringComparison.OrdinalIgnoreCase)) return "user";
            return "developer";
        }

        private static string BoundError(string value)
        {
            value = (value ?? "Unknown error.").Replace('\r', ' ').Replace('\n', ' ').Trim();
            return value.Length <= 800 ? value : value.Substring(0, 800) + "…";
        }
    }
}
