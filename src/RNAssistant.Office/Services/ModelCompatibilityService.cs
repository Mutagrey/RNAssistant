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

        public async Task<ModelCompatibilityResponse> TestAsync(
            AppSettings settings,
            CancellationToken cancellationToken)
        {
            settings = (settings ?? new AppSettings()).Clone();
            settings.MaxTokens = Math.Max(16, Math.Min(64, settings.MaxTokens));
            settings.StreamResponses = false;
            settings.Temperature = 0;
            settings.TopP = 1;

            var responseMode = NormalizeResponseMode(settings.AgentResponseMode);
            var instructionRole = NormalizeInstructionRole(settings.SystemPromptRole);
            var toolResultRole = NormalizeToolResultRole(settings.ToolResultRole);
            var checks = new List<ModelCompatibilityCheckDto>();
            using (var totalCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                totalCancellation.CancelAfter(TimeSpan.FromSeconds(120));
                try
                {
                    var token = totalCancellation.Token;
                    checks.Add(await ProbeTextRoleAsync(settings, "user", "user_role", "Роль user", true, token).ConfigureAwait(false));
                    checks.Add(await ProbeTextRoleAsync(settings, "system", "system_role", "Инструкция system", instructionRole == "system", token).ConfigureAwait(false));
                    checks.Add(await ProbeTextRoleAsync(settings, "developer", "developer_role", "Инструкция developer", instructionRole == "developer", token).ConfigureAwait(false));
                    checks.Add(await ProbeJsonAsync(settings, false, responseMode == AgentResponseModes.JsonObject, token).ConfigureAwait(false));
                    checks.Add(await ProbeJsonAsync(settings, true, responseMode != AgentResponseModes.JsonObject, token).ConfigureAwait(false));
                    checks.Add(await ProbeToolResultRoleAsync(settings, toolResultRole, true, token).ConfigureAwait(false));
                    checks.Add(await ProbeNativeToolAsync(settings, responseMode == AgentResponseModes.NativeToolCalls, token).ConfigureAwait(false));
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    checks.Add(Check("compatibility_total_timeout", "Общий лимит проверки", true, false, 120000,
                        "Проверка остановлена после общего лимита 120 с."));
                }
            }

            var compatible = checks.All(check => !check.Required || check.Passed);
            return new ModelCompatibilityResponse
            {
                Compatible = compatible,
                Endpoint = settings.BaseUrl ?? string.Empty,
                Model = settings.Model ?? string.Empty,
                ResponseMode = responseMode,
                InstructionRole = instructionRole,
                ToolResultRole = toolResultRole,
                Summary = compatible
                    ? "Текущая конфигурация совместима. Необязательные красные проверки относятся к другим режимам."
                    : "Текущая конфигурация несовместима: исправьте обязательные красные проверки или выберите поддерживаемые роли/формат.",
                Checks = checks
            };
        }

        private Task<ModelCompatibilityCheckDto> ProbeTextRoleAsync(
            AppSettings settings,
            string role,
            string id,
            string title,
            bool required,
            CancellationToken cancellationToken)
        {
            var messages = new List<ChatMessage>();
            if (!string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
            {
                messages.Add(new ChatMessage { Role = role, Content = "This is an RNAssistant compatibility probe. Reply with ROLE_OK." });
            }
            messages.Add(new ChatMessage { Role = "user", Content = "Reply with ROLE_OK and no explanation." });
            return RunAsync(
                settings,
                id,
                title,
                required,
                messages,
                new LlmRequestOptions { ResponseFormat = LlmResponseFormats.Text },
                completion => HasOutput(completion) ? null : "Endpoint returned no text or tool call.",
                cancellationToken);
        }

        private Task<ModelCompatibilityCheckDto> ProbeJsonAsync(
            AppSettings settings,
            bool schema,
            bool required,
            CancellationToken cancellationToken)
        {
            var probeTools = new[]
            {
                new ToolDefinition
                {
                    Id = "compat.echo",
                    Description = "Compatibility probe tool.",
                    ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}},\"required\":[\"value\"],\"additionalProperties\":false}"
                }
            };
            var format = schema ? LlmResponseFormats.JsonSchema : LlmResponseFormats.JsonObject;
            var options = new LlmRequestOptions { ResponseFormat = format };
            if (schema)
            {
                options.ResponseSchemaName = AgentDecisionProtocol.SchemaName;
                options.ResponseSchemaJson = AgentDecisionSchemaBuilder.Build(probeTools, true);
            }
            return RunAsync(
                settings,
                schema ? "json_schema" : "json_object",
                schema ? "Формат json_schema" : "Формат json_object",
                required,
                new[]
                {
                    new ChatMessage
                    {
                        Role = "user",
                        Content = "Return exactly this AgentDecision v1 object and no surrounding text: {\"protocolVersion\":1,\"kind\":\"final\",\"decisionSummary\":\"FORMAT_OK\",\"goal\":null,\"plan\":null,\"tool\":null,\"message\":\"FORMAT_OK\"}"
                    }
                },
                options,
                completion => ValidateAgentDecisionJson(completion, probeTools),
                cancellationToken);
        }

        private Task<ModelCompatibilityCheckDto> ProbeToolResultRoleAsync(
            AppSettings settings,
            string role,
            bool required,
            CancellationToken cancellationToken)
        {
            var messages = new List<ChatMessage>
            {
                new ChatMessage { Role = "user", Content = "Use the following local compatibility result, then reply with TOOL_OK." }
            };
            if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
            {
                messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = string.Empty,
                    ToolCalls = new List<LlmToolCall>
                    {
                        new LlmToolCall
                        {
                            Id = "call_rnassistant_compat",
                            Name = "rnassistant_compat_echo",
                            ArgumentsJson = "{\"value\":\"TOOL_OK\"}"
                        }
                    }
                });
                messages.Add(new ChatMessage
                {
                    Role = "tool",
                    ToolCallId = "call_rnassistant_compat",
                    ToolName = "rnassistant_compat_echo",
                    Content = "{\"ok\":true,\"value\":\"TOOL_OK\"}"
                });
            }
            else
            {
                messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = "{\"protocolVersion\":1,\"kind\":\"tool\",\"decisionSummary\":\"Compatibility probe\",\"goal\":null,\"plan\":null,\"tool\":{\"toolId\":\"compat.echo\",\"arguments\":{\"value\":\"TOOL_OK\"}},\"message\":null}"
                });
                messages.Add(new ChatMessage
                {
                    Role = role,
                    Content = "TOOL_RESULT: {\"ok\":true,\"value\":\"TOOL_OK\"}"
                });
            }
            messages.Add(new ChatMessage { Role = "user", Content = "Reply with TOOL_OK." });
            return RunAsync(
                settings,
                "tool_result_" + role,
                "Результат tool через роль " + role,
                required,
                messages,
                new LlmRequestOptions { ResponseFormat = LlmResponseFormats.Text },
                completion => HasOutput(completion) ? null : "Endpoint returned no continuation after tool result.",
                cancellationToken);
        }

        private Task<ModelCompatibilityCheckDto> ProbeNativeToolAsync(
            AppSettings settings,
            bool required,
            CancellationToken cancellationToken)
        {
            var apiName = "rnassistant_compat_echo";
            var options = new LlmRequestOptions
            {
                ResponseFormat = LlmResponseFormats.JsonSchema,
                ResponseSchemaName = AgentDecisionProtocol.SchemaName,
                ResponseSchemaJson = AgentDecisionSchemaBuilder.Build(new ToolDefinition[0], false),
                NativeTools = true,
                Tools = new[]
                {
                    new LlmToolDefinition
                    {
                        ToolId = "compat.echo",
                        ApiName = apiName,
                        Description = "Compatibility probe. Call once with value TOOL_OK.",
                        ParametersSchemaJson = "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}},\"required\":[\"value\"],\"additionalProperties\":false}"
                    }
                }
            };
            return RunAsync(
                settings,
                "native_tool_calls",
                "Native tool_calls + json_schema",
                required,
                new[] { new ChatMessage { Role = "user", Content = "Call rnassistant_compat_echo exactly once with value TOOL_OK." } },
                options,
                completion => completion != null && completion.ToolCalls != null && completion.ToolCalls.Count == 1 &&
                    string.Equals(completion.ToolCalls[0].Name, apiName, StringComparison.OrdinalIgnoreCase)
                        ? null
                        : "Endpoint did not return exactly one expected native tool call.",
                cancellationToken);
        }

        private async Task<ModelCompatibilityCheckDto> RunAsync(
            AppSettings settings,
            string id,
            string title,
            bool required,
            IEnumerable<ChatMessage> messages,
            LlmRequestOptions options,
            Func<LlmCompletionResult, string> validate,
            CancellationToken cancellationToken)
        {
            var watch = Stopwatch.StartNew();
            using (var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var configuredTimeout = settings == null || settings.RequestTimeoutSeconds <= 0
                    ? 30
                    : settings.RequestTimeoutSeconds;
                var timeoutSeconds = Math.Max(10, Math.Min(45, configuredTimeout));
                probeCancellation.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var completion = await _completeAsync(settings, messages, options, null, probeCancellation.Token).ConfigureAwait(false);
                    var validationError = validate == null ? null : validate(completion);
                    return Check(id, title, required, string.IsNullOrWhiteSpace(validationError), watch.ElapsedMilliseconds,
                        string.IsNullOrWhiteSpace(validationError) ? "Поддерживается." : validationError);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return Check(id, title, required, false, watch.ElapsedMilliseconds,
                        "Таймаут проверки: endpoint не ответил за " + timeoutSeconds + " с.");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return Check(id, title, required, false, watch.ElapsedMilliseconds, BoundError(ex.Message));
                }
            }
        }

        private static ModelCompatibilityCheckDto Check(
            string id,
            string title,
            bool required,
            bool passed,
            long durationMs,
            string message)
        {
            return new ModelCompatibilityCheckDto
            {
                Id = id,
                Title = title,
                Required = required,
                Passed = passed,
                DurationMs = durationMs,
                Message = message ?? string.Empty
            };
        }

        private static string ValidateAgentDecisionJson(
            LlmCompletionResult completion,
            IEnumerable<ToolDefinition> tools)
        {
            if (completion == null || string.IsNullOrWhiteSpace(completion.Content)) return "Endpoint returned empty JSON content.";
            var parsed = new AgentPlannerResponseParser().Parse(completion.Content, tools);
            if (!parsed.Success)
            {
                return "AgentDecision parser rejected the response: " + parsed.ErrorCode + ". " + parsed.ErrorMessage;
            }
            if (!string.Equals(parsed.Response.Kind, AgentResponseKinds.Final, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(parsed.Response.Message) ||
                parsed.Response.Message.IndexOf("FORMAT_OK", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return "Endpoint returned JSON, but not the requested terminal AgentDecision.";
            }
            return null;
        }

        private static bool HasOutput(LlmCompletionResult completion)
        {
            return completion != null &&
                (!string.IsNullOrWhiteSpace(completion.Content) || completion.ToolCalls != null && completion.ToolCalls.Count > 0);
        }

        private static string NormalizeResponseMode(string value)
        {
            if (string.Equals(value, AgentResponseModes.NativeToolCalls, StringComparison.OrdinalIgnoreCase)) return AgentResponseModes.NativeToolCalls;
            if (string.Equals(value, AgentResponseModes.JsonObject, StringComparison.OrdinalIgnoreCase)) return AgentResponseModes.JsonObject;
            return AgentResponseModes.JsonSchema;
        }

        private static string NormalizeInstructionRole(string value)
        {
            if (string.Equals(value, "system", StringComparison.OrdinalIgnoreCase)) return "system";
            if (string.Equals(value, "user", StringComparison.OrdinalIgnoreCase)) return "user";
            return "developer";
        }

        private static string NormalizeToolResultRole(string value)
        {
            if (string.Equals(value, "developer", StringComparison.OrdinalIgnoreCase)) return "developer";
            if (string.Equals(value, "user", StringComparison.OrdinalIgnoreCase)) return "user";
            return "tool";
        }

        private static string BoundError(string value)
        {
            value = (value ?? "Unknown error.").Replace('\r', ' ').Replace('\n', ' ').Trim();
            return value.Length <= 800 ? value : value.Substring(0, 800) + "…";
        }
    }
}
