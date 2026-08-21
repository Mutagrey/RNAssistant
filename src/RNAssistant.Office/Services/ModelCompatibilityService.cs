using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
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
            return RunAsync(
                settings,
                "agent_json",
                "Agent " + responseMode,
                new[]
                {
                    new ChatMessage
                    {
                        Role = "user",
                        Content = "Return exactly one JSON object: {\"message\":\"TOOL_OK\",\"tool_calls\":[{\"id\":\"call_1\",\"name\":\"compat.echo\",\"arguments\":{\"value\":\"A\"}}]}"
                    }
                },
                AgentOptions(responseMode, new[] { tool }),
                completion =>
                {
                    var parsed = new AgentResponseParser().Parse(completion == null ? null : completion.Content, new[] { tool });
                    if (!parsed.Success) return parsed.Error ?? "Endpoint returned no tool call.";
                    if (!string.Equals(parsed.Response.Message, "TOOL_OK", StringComparison.Ordinal) || parsed.Response.ToolCalls.Count != 1)
                    {
                        return "Endpoint did not return the exact Agent JSON sentinel.";
                    }
                    var call = parsed.Response.ToolCalls[0];
                    object value;
                    return string.Equals(call.Id, "call_1", StringComparison.Ordinal) &&
                           string.Equals(call.Name, "compat.echo", StringComparison.Ordinal) &&
                           call.Arguments != null && call.Arguments.Count == 1 &&
                           string.Equals(call.Arguments.Keys.Single(), "value", StringComparison.Ordinal) &&
                           call.Arguments.TryGetValue("value", out value) &&
                           string.Equals(Convert.ToString(value), "A", StringComparison.Ordinal)
                        ? null
                        : "Endpoint changed the required tool id, name, or arguments.";
                },
                cancellationToken);
        }

        private Task<ModelCompatibilityCheckDto> ProbeToolResultAsync(
            AppSettings settings,
            string responseMode,
            string toolResultRole,
            CancellationToken cancellationToken)
        {
            var messages = ToolResultProbeMessages(toolResultRole);
            return RunAsync(
                settings,
                "tool_result_json",
                "Tool result · " + toolResultRole,
                messages,
                AgentOptions(responseMode, new ToolDefinition[0]),
                completion =>
                {
                    var parsed = new AgentResponseParser().Parse(completion == null ? null : completion.Content, new ToolDefinition[0]);
                    return parsed.Success && parsed.Response.ToolCalls.Count == 0 &&
                           string.Equals(parsed.Response.Message, "RESULT_OK", StringComparison.Ordinal)
                        ? null
                        : parsed.Error ?? "Endpoint did not return the exact RESULT_OK sentinel after TOOL_RESULT.";
                },
                cancellationToken);
        }

        private static IEnumerable<ChatMessage> ToolResultProbeMessages(string role)
        {
            role = ToolResultRoles.Normalize(role);
            const string resultJson = "{\"ok\":true,\"tool_call_id\":\"call_1\",\"name\":\"compat.echo\",\"status\":\"success\",\"message\":\"\",\"data\":{\"value\":\"A\"},\"error\":null}";
            var messages = new List<ChatMessage>();
            if (string.Equals(role, ToolResultRoles.Tool, StringComparison.Ordinal))
            {
                var apiName = AgentJsonProtocol.ApiToolName("compat.echo");
                messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = "TOOL_OK",
                    ProtocolMessage = true,
                    ToolCalls = new List<LlmToolCall>
                    {
                        new LlmToolCall
                        {
                            Id = "call_1",
                            Type = "function",
                            Name = apiName,
                            ArgumentsJson = "{\"value\":\"A\"}"
                        }
                    }
                });
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
                    Role = "assistant",
                    Content = "{\"message\":\"TOOL_OK\",\"tool_calls\":[{\"id\":\"call_1\",\"name\":\"compat.echo\",\"arguments\":{\"value\":\"A\"}}]}",
                    ProtocolMessage = true
                });
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
                Content = "Reply with {\"message\":\"RESULT_OK\",\"tool_calls\":[]}."
            });
            return messages;
        }

        private static LlmRequestOptions AgentOptions(string responseMode, IEnumerable<ToolDefinition> tools)
        {
            var jsonSchema = string.Equals(
                AgentResponseModes.Normalize(responseMode),
                AgentResponseModes.JsonSchema,
                StringComparison.Ordinal);
            return new LlmRequestOptions
            {
                ResponseFormat = jsonSchema ? LlmResponseFormats.JsonSchema : LlmResponseFormats.JsonObject,
                ResponseSchemaName = jsonSchema ? AgentResponseSchemaBuilder.SchemaName : null,
                ResponseSchemaJson = jsonSchema ? AgentResponseSchemaBuilder.Build(tools) : null
            };
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
                    var error = validate(completion);
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
