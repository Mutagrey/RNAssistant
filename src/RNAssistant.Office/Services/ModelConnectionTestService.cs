using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    internal sealed class ModelConnectionTestService
    {
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);
        private readonly LlmCompletionDelegate _completeAsync;

        public ModelConnectionTestService(LlmCompletionDelegate completeAsync)
        {
            _completeAsync = completeAsync ?? throw new ArgumentNullException("completeAsync");
        }

        public async Task<ModelConnectionTestResponse> TestAsync(AppSettings settings, CancellationToken cancellationToken)
        {
            settings = (settings ?? new AppSettings()).Clone();
            settings.MaxTokens = 16;
            settings.RequestTimeoutSeconds = (int)ProbeTimeout.TotalSeconds;
            settings.Temperature = 0;
            settings.TopP = 1;

            LlmRequestDiagnosticUpdate lastDiagnostics = null;
            var watch = Stopwatch.StartNew();
            try
            {
                var completion = await _completeAsync(
                    settings,
                    new[] { new ChatMessage { Role = "user", Content = "Reply with PONG." } },
                    new LlmRequestOptions
                    {
                        ResponseFormat = LlmResponseFormats.Text,
                        ReasoningEnabled = false,
                        DiagnosticProgress = update => lastDiagnostics = update
                    },
                    null,
                    cancellationToken).ConfigureAwait(false);

                var content = completion == null ? string.Empty : (completion.Content ?? string.Empty).Trim();
                return Result(
                    settings,
                    !string.IsNullOrWhiteSpace(content),
                    string.IsNullOrWhiteSpace(content) ? "Модель вернула пустой ответ." : "Модель ответила.",
                    watch.ElapsedMilliseconds,
                    lastDiagnostics,
                    string.IsNullOrWhiteSpace(content) ? "Empty model response." : null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (LlmRequestException ex) when (ex.Kind == LlmFailureKind.Timeout)
            {
                return Result(settings, false, "Модель не ответила за 30 секунд.", watch.ElapsedMilliseconds,
                    lastDiagnostics, BoundError(ex.Message));
            }
            catch (Exception ex)
            {
                return Result(settings, false, "Проверка завершилась ошибкой.", watch.ElapsedMilliseconds,
                    lastDiagnostics, BoundError(ex.Message));
            }
        }

        private static ModelConnectionTestResponse Result(
            AppSettings settings,
            bool success,
            string summary,
            long durationMs,
            LlmRequestDiagnosticUpdate diagnostics,
            string error)
        {
            return new ModelConnectionTestResponse
            {
                Success = success,
                Summary = summary,
                Endpoint = settings.BaseUrl ?? string.Empty,
                Model = settings.Model ?? string.Empty,
                StreamRequested = settings.StreamResponses,
                DurationMs = durationMs,
                Diagnostics = ModelRequestDiagnosticsDto.From(diagnostics),
                Error = BoundError(error)
            };
        }

        private static string BoundError(string value)
        {
            value = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            return value.Length <= 600 ? value : value.Substring(0, 600) + "…";
        }
    }
}
