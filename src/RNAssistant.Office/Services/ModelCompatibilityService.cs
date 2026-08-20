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
            var checks = new List<ModelCompatibilityCheckDto>();
            using (var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                total.CancelAfter(TimeSpan.FromSeconds(90));
                checks.Add(await ProbeTextAsync(settings, instructionRole, total.Token).ConfigureAwait(false));
                checks.Add(await ProbeAgentJsonAsync(settings, total.Token).ConfigureAwait(false));
                checks.Add(await ProbeToolResultAsync(settings, total.Token).ConfigureAwait(false));
            }
            var compatible = checks.All(check => check.Passed);
            return new ModelCompatibilityResponse
            {
                Compatible = compatible,
                Endpoint = settings.BaseUrl ?? string.Empty,
                Model = settings.Model ?? string.Empty,
                InstructionRole = instructionRole,
                Summary = compatible
                    ? "Модель совместима с простым Agent JSON-потоком."
                    : "Модель не прошла обязательную проверку Agent JSON-потока.",
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
                completion => completion != null && !string.IsNullOrWhiteSpace(completion.Content)
                    ? null
                    : "Endpoint returned no text.",
                cancellationToken);
        }

        private Task<ModelCompatibilityCheckDto> ProbeAgentJsonAsync(AppSettings settings, CancellationToken cancellationToken)
        {
            var tool = new ToolDefinition
            {
                Id = "compat.echo",
                Description = "Compatibility probe.",
                ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}},\"required\":[\"value\"],\"additionalProperties\":false}"
            };
            return RunAsync(
                settings,
                "agent_json",
                "Agent JSON",
                new[]
                {
                    new ChatMessage
                    {
                        Role = "user",
                        Content = "Return exactly one JSON object: {\"message\":\"TOOL_OK\",\"tool_calls\":[{\"id\":\"call_1\",\"name\":\"compat.echo\",\"arguments\":{\"value\":\"A\"}}]}"
                    }
                },
                new LlmRequestOptions { ResponseFormat = LlmResponseFormats.JsonObject },
                completion =>
                {
                    var parsed = new AgentResponseParser().Parse(completion == null ? null : completion.Content, new[] { tool });
                    return parsed.Success && parsed.Response.ToolCalls.Count == 1
                        ? null
                        : parsed.Error ?? "Endpoint returned no tool call.";
                },
                cancellationToken);
        }

        private Task<ModelCompatibilityCheckDto> ProbeToolResultAsync(AppSettings settings, CancellationToken cancellationToken)
        {
            return RunAsync(
                settings,
                "tool_result_json",
                "TOOL_RESULT JSON",
                new[]
                {
                    new ChatMessage { Role = "user", Content = "TOOL_RESULT:\n{\"ok\":true,\"name\":\"compat.echo\",\"data\":{\"value\":\"A\"},\"error\":null}\nReply with {\"message\":\"RESULT_OK\",\"tool_calls\":[]}." }
                },
                new LlmRequestOptions { ResponseFormat = LlmResponseFormats.JsonObject },
                completion =>
                {
                    var parsed = new AgentResponseParser().Parse(completion == null ? null : completion.Content, new ToolDefinition[0]);
                    return parsed.Success && parsed.Response.ToolCalls.Count == 0
                        ? null
                        : parsed.Error ?? "Endpoint did not consume the tool result.";
                },
                cancellationToken);
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
