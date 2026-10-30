using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    // Stateless UI consumer of the same Core scenarios/assertions as the CLI.
    // Each bridge call runs one scenario with its controls, allowing cancellation between cases.
    internal sealed class ModelDiagnosticsService
    {
        internal const int PreviewCharacters = 65536;
        private readonly ModelDiagnosticRunner _runner;
        public ModelDiagnosticsService(LlmCompletionDelegate complete) { _runner = new ModelDiagnosticRunner(complete); }

        public static ModelDiagnosticCatalogDto Catalog(AppSettings settings)
        {
            var all = ModelDiagnosticScenarios.All("catalog");
            var controls = new HashSet<string>(all.SelectMany(item => ModelDiagnosticScenarios.Controls(item.Id)));
            return new ModelDiagnosticCatalogDto { ScenarioRevision = ModelDiagnosticScenarios.Revision,
                Endpoint = settings.BaseUrl, Model = settings.Model,
                Scenarios = all.Select(item => new ModelDiagnosticScenarioDto { Id = item.Id, Capability = item.Capability,
                    Assertion = item.Assertion, ResponseFormat = item.ResponseFormat, Streaming = item.Streaming,
                    Controls = ModelDiagnosticScenarios.Controls(item.Id), IsControl = controls.Contains(item.Id) }).ToList() };
        }

        public async Task<ModelDiagnosticRunDto> RunAsync(AppSettings saved, RunModelDiagnosticRequest request,
            string apiKey, CancellationToken cancellation, ModelDiagnosticExecutionKind kind = ModelDiagnosticExecutionKind.RealEndpoint)
        {
            if (saved == null || request == null) throw new ArgumentNullException();
            Uri endpoint;
            if (request.Endpoint != saved.BaseUrl || !Uri.TryCreate(saved.BaseUrl, UriKind.Absolute, out endpoint) ||
                (endpoint.Scheme != "http" && endpoint.Scheme != "https") || endpoint.UserInfo.Length != 0 ||
                endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
                throw new ArgumentException("Подключение изменилось или адрес недопустим. Обновите список сценариев.");
            if (string.IsNullOrWhiteSpace(request.Model) || request.Model.Length > 256 ||
                string.IsNullOrWhiteSpace(request.Seed) || request.Seed.Length > 128 ||
                request.TimeoutSeconds < 10 || request.TimeoutSeconds > 60)
                throw new ArgumentException("Нужны модель, seed до 128 символов и таймаут от 10 до 60 секунд.");
            var scenarios = ModelDiagnosticScenarios.Select(request.Seed, new[] { request.ScenarioId });
            var settings = saved.Clone();
            settings.Model = request.Model; settings.MaxTokens = 512; settings.Temperature = 0; settings.TopP = 1;
            settings.RequestTimeoutSeconds = request.TimeoutSeconds; settings.ContextWindowOverrideTokens = 8192;
            settings.DebugModelTraffic = false;
            var results = new List<ModelDiagnosticResult>();
            foreach (var scenario in scenarios)
            {
                cancellation.ThrowIfCancellationRequested();
                var result = await _runner.RunAsync(settings, scenario, kind, cancellation).ConfigureAwait(false);
                if (scenario.ParityControlId != null)
                    ModelDiagnosticRunner.CompareStreamControl(result, results.SingleOrDefault(item => item.ScenarioId == scenario.ParityControlId));
                results.Add(result);
            }
            var secrets = new[] { apiKey }.Concat((settings.CustomHeaders ?? new Dictionary<string, string>()).Values)
                .Where(value => !string.IsNullOrEmpty(value)).Distinct().OrderByDescending(value => value.Length).ToArray();
            return new ModelDiagnosticRunDto { ScenarioRevision = ModelDiagnosticScenarios.Revision,
                Seed = Text(request.Seed, secrets).Text, Exchanges = results.Select(result => Export(result, secrets)).ToList() };
        }

        internal static ModelDiagnosticExchangeDto Export(ModelDiagnosticResult source, string[] secrets)
        {
            var result = JsonConvert.DeserializeObject<ModelDiagnosticResult>(JsonConvert.SerializeObject(source));
            var exchange = new ModelDiagnosticExchangeDto { Result = result,
                Request = Text(source.RequestUtf8 == null ? null : Encoding.UTF8.GetString(source.RequestUtf8), secrets),
                Response = Text(source.ResponsePayload, secrets) };
            // Redact before bounding so a secret crossing the preview boundary is never exposed.
            var fields = new[] { "Model", "Endpoint", "Reason", "FailureKind", "ParserErrorCode", "ActualContent",
                "ActualNativeCallsJson", "ParsedResponseJson", "ExpectedJson", "ExpectedText" };
            foreach (var property in typeof(ModelDiagnosticResult).GetProperties().Where(property => fields.Contains(property.Name)))
            {
                var safe = Text((string)property.GetValue(result), secrets);
                if (safe == null) continue;
                property.SetValue(result, safe.Text);
                exchange.ResultRedacted |= safe.Redacted;
                exchange.ResultTruncated |= safe.Truncated;
            }
            return exchange;
        }

        private static ModelDiagnosticTextDto Text(string value, string[] secrets)
        {
            if (value == null) return null;
            var safe = value;
            foreach (var secret in secrets) safe = ModelDiagnosticExport.RedactJson(safe, secret);
            var count = Math.Min(safe.Length, PreviewCharacters);
            if (count > 0 && count < safe.Length && char.IsHighSurrogate(safe[count - 1])) count--;
            return new ModelDiagnosticTextDto { Text = safe.Substring(0, count), Characters = value.Length,
                Truncated = count < safe.Length, Redacted = safe != value };
        }
    }
}
