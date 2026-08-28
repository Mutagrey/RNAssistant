using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Office.Contracts
{
    public class ModelCatalogPayload
    {
        [JsonProperty("settings")]
        public AppSettings Settings { get; set; }

        [JsonProperty("apiKey")]
        public string ApiKey { get; set; }
    }

    public sealed class SaveSettingsPayload : ModelCatalogPayload
    {
        [JsonProperty("historySecret")]
        public string HistorySecret { get; set; }

        [JsonProperty("reviewAgentPrompts")]
        public bool ReviewAgentPrompts { get; set; }
    }

    public sealed class SaveToolsPayload
    {
        [JsonProperty("tools")]
        public List<ToolDefinition> Tools { get; set; }
    }

    public sealed class VbaToolPackagePayload
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("dryRun")]
        public bool DryRun { get; set; }
    }

    public sealed class SaveSkillsPayload
    {
        [JsonProperty("skills")]
        public List<SkillDefinition> Skills { get; set; }
    }

    public class SkillReferencePayload
    {
        [JsonProperty("skillId")]
        public string SkillId { get; set; }

        [JsonProperty("path")]
        public string Path { get; set; }
    }

    public sealed class SaveSkillReferencePayload : SkillReferencePayload
    {
        [JsonProperty("content")]
        public string Content { get; set; }
    }

    public sealed class SkillReferenceResponse
    {
        [JsonProperty("skillId")]
        public string SkillId { get; set; }

        [JsonProperty("path")]
        public string Path { get; set; }

        [JsonProperty("content")]
        public string Content { get; set; }

        [JsonProperty("deleted")]
        public bool Deleted { get; set; }

        [JsonProperty("packageRevision")]
        public string PackageRevision { get; set; }

        [JsonProperty("reference")]
        public SkillReferenceMetadata Reference { get; set; }

        [JsonProperty("references")]
        public List<SkillReferenceMetadata> References { get; set; }
    }

    public sealed class SettingsResponse
    {
        [JsonProperty("appVersion")]
        public string AppVersion { get; set; }

        [JsonProperty("settings")]
        public AppSettings Settings { get; set; }

        [JsonProperty("hasApiKey")]
        public bool HasApiKey { get; set; }

        [JsonProperty("hasHistorySecret")]
        public bool HasHistorySecret { get; set; }
    }

    public sealed class ModelCatalogResponse
    {
        [JsonProperty("configUrl")]
        public string ConfigUrl { get; set; }

        [JsonProperty("catalog")]
        public JToken Catalog { get; set; }
    }

    public sealed class ModelConnectionTestResponse
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("summary")]
        public string Summary { get; set; }

        [JsonProperty("endpoint")]
        public string Endpoint { get; set; }

        [JsonProperty("model")]
        public string Model { get; set; }

        [JsonProperty("streamRequested")]
        public bool StreamRequested { get; set; }

        [JsonProperty("durationMs")]
        public long DurationMs { get; set; }

        [JsonProperty("diagnostics", NullValueHandling = NullValueHandling.Ignore)]
        public ModelRequestDiagnosticsDto Diagnostics { get; set; }

        [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
        public string Error { get; set; }
    }

    public sealed class ModelCompatibilityResponse
    {
        [JsonProperty("compatible")]
        public bool Compatible { get; set; }

        [JsonProperty("endpoint")]
        public string Endpoint { get; set; }

        [JsonProperty("model")]
        public string Model { get; set; }

        [JsonProperty("instructionRole")]
        public string InstructionRole { get; set; }

        [JsonProperty("responseMode")]
        public string ResponseMode { get; set; }

        [JsonProperty("toolResultRole")]
        public string ToolResultRole { get; set; }

        [JsonProperty("summary")]
        public string Summary { get; set; }

        [JsonProperty("checks")]
        public IReadOnlyList<ModelCompatibilityCheckDto> Checks { get; set; }
    }

    public sealed class ModelCompatibilityCheckDto
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("passed")]
        public bool Passed { get; set; }

        [JsonProperty("required")]
        public bool Required { get; set; }

        [JsonProperty("durationMs")]
        public long DurationMs { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }
    }
}
