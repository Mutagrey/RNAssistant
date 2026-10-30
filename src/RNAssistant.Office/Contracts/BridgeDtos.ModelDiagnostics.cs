using System.Collections.Generic;
using Newtonsoft.Json;
using RNAssistant.Core.Llm;

namespace RNAssistant.Office.Contracts
{
    public sealed class ModelDiagnosticCatalogDto
    {
        [JsonProperty("scenarioRevision")] public int ScenarioRevision { get; set; }
        [JsonProperty("endpoint")] public string Endpoint { get; set; }
        [JsonProperty("model")] public string Model { get; set; }
        [JsonProperty("scenarios")] public List<ModelDiagnosticScenarioDto> Scenarios { get; set; }
    }

    public sealed class ModelDiagnosticScenarioDto
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("capability")] public ModelDiagnosticCapability Capability { get; set; }
        [JsonProperty("assertion")] public ModelDiagnosticAssertion Assertion { get; set; }
        [JsonProperty("responseFormat")] public string ResponseFormat { get; set; }
        [JsonProperty("streaming")] public bool Streaming { get; set; }
        [JsonProperty("controls")] public IReadOnlyList<string> Controls { get; set; }
        [JsonProperty("isControl")] public bool IsControl { get; set; }
    }

    public sealed class RunModelDiagnosticRequest
    {
        [JsonProperty("scenarioId")] public string ScenarioId { get; set; }
        [JsonProperty("seed")] public string Seed { get; set; }
        [JsonProperty("endpoint")] public string Endpoint { get; set; }
        [JsonProperty("model")] public string Model { get; set; }
        [JsonProperty("timeoutSeconds")] public int TimeoutSeconds { get; set; } = 30;
    }

    public sealed class ModelDiagnosticRunDto
    {
        [JsonProperty("scenarioRevision")] public int ScenarioRevision { get; set; }
        [JsonProperty("seed")] public string Seed { get; set; }
        [JsonProperty("exchanges")] public List<ModelDiagnosticExchangeDto> Exchanges { get; set; }
    }

    public sealed class ModelDiagnosticExchangeDto
    {
        [JsonProperty("result")] public ModelDiagnosticResult Result { get; set; }
        [JsonProperty("request")] public ModelDiagnosticTextDto Request { get; set; }
        [JsonProperty("response")] public ModelDiagnosticTextDto Response { get; set; }
        [JsonProperty("resultTruncated")] public bool ResultTruncated { get; set; }
        [JsonProperty("resultRedacted")] public bool ResultRedacted { get; set; }
    }

    public sealed class ModelDiagnosticTextDto
    {
        [JsonProperty("text")] public string Text { get; set; }
        [JsonProperty("characters")] public int Characters { get; set; }
        [JsonProperty("truncated")] public bool Truncated { get; set; }
        [JsonProperty("redacted")] public bool Redacted { get; set; }
    }
}
