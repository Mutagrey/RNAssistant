using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;

namespace RNAssistant.Cli
{
    internal static class ModelDiagnosticsCommand
    {
        private sealed class DiagnosticReport
        {
            public int ReportVersion { get; set; } = 2;
            public int ScenarioRevision { get; set; } = ModelDiagnosticScenarios.Revision;
            public string Endpoint { get; set; }
            public string Model { get; set; }
            public string Seed { get; set; }
            public DateTime StartedUtc { get; set; }
            public string ResponseFormat { get; set; } = "per scenario";
            public string ReferenceContract { get; set; } = "synthetic T IDs; production reference contract unchanged";
            public string FinishResultField { get; set; } = "summary";
            public ModelDiagnosticVerdict TargetCriterion { get; set; } = ModelDiagnosticVerdict.NotRun;
            public ModelDiagnosticVerdict NativeGeneration { get; set; } = ModelDiagnosticVerdict.NotRun;
            public ModelDiagnosticVerdict V8Parser { get; set; } = ModelDiagnosticVerdict.NotRun;
            public ModelDiagnosticVerdict Sse { get; set; } = ModelDiagnosticVerdict.NotRun;
            public List<DiagnosticExport> Results { get; set; } = new List<DiagnosticExport>();
        }

        private sealed class DiagnosticExport
        {
            public int Repetition { get; set; }
            public string RequestFile { get; set; }
            public string ResponseFile { get; set; }
            public bool RequestRedacted { get; set; }
            public bool ResponseRedacted { get; set; }
            public bool ResultRedacted { get; set; }
            public ModelDiagnosticResult Result { get; set; }
        }

        internal static int Run(Dictionary<string, string> options)
        {
            var allowed = new HashSet<string>(new[] { "base-url", "model", "output", "cases", "seed", "repeat", "timeout", "synthetic" });
            if (options.Keys.Any(key => !allowed.Contains(key))) throw new ArgumentException("Unknown diagnostics option.");
            var endpoint = Value(options, "base-url", Environment.GetEnvironmentVariable("RNA_BASE_URL"));
            var model = Value(options, "model", Environment.GetEnvironmentVariable("RNA_MODEL"));
            var output = Value(options, "output", null);
            Uri uri;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https") ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("Diagnostics requires an HTTP(S) endpoint without credentials, query or fragment.");
            if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(output))
                throw new ArgumentException("Diagnostics requires --model and --output.");
            var repeat = Number(options, "repeat", 1, 1, 10);
            var timeout = Number(options, "timeout", 30, 10, 300);
            var seed = Value(options, "seed", Guid.NewGuid().ToString("N"));
            var available = ModelDiagnosticScenarios.All(seed);
            var selected = Value(options, "cases", string.Join(",", available.Select(item => item.Id))).Split(',');
            var known = new HashSet<string>(available.Select(item => item.Id), StringComparer.Ordinal);
            if (selected.Length == 0 || selected.Distinct().Count() != selected.Length || selected.Any(id => !known.Contains(id)))
                throw new ArgumentException("Select distinct diagnostics cases from " + string.Join(",", available.Select(item => item.Id)) + ".");
            var selection = new HashSet<string>(selected, StringComparer.Ordinal);
            if (selection.Contains("C4")) selection.Add("C4-control");
            if (selection.Contains("C3")) selection.Add("C3-valid");
            if (selection.Contains("A2")) selection.Add("A2-system");
            output = Path.GetFullPath(output);
            if (Directory.Exists(output) || File.Exists(output)) throw new ArgumentException("Diagnostics output must be a new directory.");
            Directory.CreateDirectory(output);
            var apiKey = Environment.GetEnvironmentVariable("RNA_API_KEY");
            var client = new LlmClient(() => apiKey);
            var runner = new ModelDiagnosticRunner(client.CompleteAsync);
            var kind = options.ContainsKey("synthetic") ? ModelDiagnosticExecutionKind.Synthetic : ModelDiagnosticExecutionKind.RealEndpoint;
            var settings = new AppSettings { BaseUrl = endpoint, Model = model, StreamResponses = false,
                DebugModelTraffic = false, Temperature = 0, MaxTokens = 512, RequestTimeoutSeconds = timeout,
                ContextWindowOverrideTokens = 8192 };
            var report = new DiagnosticReport { Endpoint = endpoint, Model = model, Seed = seed, StartedUtc = DateTime.UtcNow };
            var jsonSettings = new JsonSerializerSettings { Formatting = Formatting.Indented };
            jsonSettings.Converters.Add(new StringEnumConverter());
            for (var iteration = 1; iteration <= repeat; iteration++)
            foreach (var scenario in ModelDiagnosticScenarios.All(seed + "/" + iteration).Where(item => selection.Contains(item.Id)))
            {
                var result = runner.RunAsync(settings, scenario, kind, CancellationToken.None).GetAwaiter().GetResult();
                if (scenario.ParityControlId != null)
                    ModelDiagnosticRunner.CompareStreamControl(result, report.Results.LastOrDefault(item =>
                        item.Repetition == iteration && item.Result.ScenarioId == scenario.ParityControlId)?.Result);
                var stem = scenario.Id + "-" + iteration.ToString("00");
                var row = new DiagnosticExport { Repetition = iteration, Result = result };
                if (result.RequestUtf8 != null)
                {
                    var raw = Encoding.UTF8.GetString(result.RequestUtf8);
                    var safe = RedactJson(raw, apiKey);
                    row.RequestRedacted = safe != raw;
                    row.RequestFile = stem + ".request.json";
                    File.WriteAllBytes(Path.Combine(output, row.RequestFile), row.RequestRedacted ? Encoding.UTF8.GetBytes(safe) : result.RequestUtf8);
                }
                if (result.ResponsePayload != null)
                {
                    var safe = RedactJson(result.ResponsePayload, apiKey);
                    row.ResponseRedacted = safe != result.ResponsePayload;
                    row.ResponseFile = stem + ".response.txt";
                    File.WriteAllBytes(Path.Combine(output, row.ResponseFile), !row.ResponseRedacted && result.ResponseBytes != null
                        ? result.ResponseBytes : Encoding.UTF8.GetBytes(safe));
                }
                var resultJson = JsonConvert.SerializeObject(result, jsonSettings);
                row.ResultRedacted = RedactJson(resultJson, apiKey) != resultJson;
                report.Results.Add(row);
                report.NativeGeneration = Aggregate(report.Results.Where(item => item.Result.Capability == ModelDiagnosticCapability.NativeGeneration)
                    .Select(item => item.Result.Verdict));
                report.V8Parser = Aggregate(report.Results.Where(item => item.Result.Assertion == ModelDiagnosticAssertion.V8)
                    .Select(item => item.Result.ParserVerdict));
                report.Sse = Aggregate(report.Results.Where(item => item.Result.ScenarioId == "C4")
                    .Select(item => item.Result.Verdict == ModelDiagnosticVerdict.Fail ? ModelDiagnosticVerdict.Fail : item.Result.ParityVerdict));
                File.WriteAllText(Path.Combine(output, "report.json"),
                    RedactJson(JsonConvert.SerializeObject(report, jsonSettings), apiKey), new UTF8Encoding(false));
                Console.WriteLine(scenario.Id + " #" + iteration + " " + kind + " " + result.Verdict +
                    " · " + result.Reason + " · HTTP " + result.HttpStatusCode + " · " + result.DurationMs + " ms");
            }
            Console.WriteLine("Report: " + output);
            return report.Results.All(item => item.Result.Verdict == ModelDiagnosticVerdict.Pass) ? 0 : 1;
        }

        private static ModelDiagnosticVerdict Aggregate(IEnumerable<ModelDiagnosticVerdict> source)
        {
            var values = source.ToArray();
            foreach (var verdict in new[] { ModelDiagnosticVerdict.Fail, ModelDiagnosticVerdict.Inconclusive,
                ModelDiagnosticVerdict.Unsupported, ModelDiagnosticVerdict.NotRun })
                if (values.Contains(verdict)) return verdict;
            return values.Length == 0 ? ModelDiagnosticVerdict.NotRun : ModelDiagnosticVerdict.Pass;
        }

        private static string Redact(string text, string secret)
        {
            if (string.IsNullOrEmpty(secret)) return text;
            var escaped = JsonConvert.ToString(secret);
            return text.Replace(escaped.Substring(1, escaped.Length - 2), "[REDACTED]").Replace(secret, "[REDACTED]");
        }
        private static string RedactJson(string text, string secret)
        {
            if (string.IsNullOrEmpty(secret)) return text;
            JToken root;
            try { root = JToken.Parse(text); }
            catch (JsonException) { return Redact(text, secret); }
            var changed = false;
            var tokens = root is JContainer ? ((JContainer)root).DescendantsAndSelf().ToArray() : new[] { root };
            foreach (var token in tokens)
            {
                var property = token as JProperty;
                if (property != null && Redact(property.Name, secret) != property.Name)
                    return "\"[REDACTED: sensitive property name]\"";
                var value = token as JValue;
                if (value == null || value.Type != JTokenType.String) continue;
                var original = (string)value;
                var safe = Redact(original, secret);
                if (safe == original) continue;
                value.Value = safe; changed = true;
            }
            return changed ? root.ToString(Formatting.Indented) : text;
        }
        private static string Value(Dictionary<string, string> options, string key, string fallback)
        { string value; return options.TryGetValue(key, out value) ? value : fallback; }
        private static int Number(Dictionary<string, string> options, string key, int fallback, int min, int max)
        {
            string raw; int value;
            if (!options.TryGetValue(key, out raw)) return fallback;
            if (!int.TryParse(raw, out value) || value < min || value > max) throw new ArgumentException("Invalid --" + key + ".");
            return value;
        }
    }
}
