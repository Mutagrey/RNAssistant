using RNAssistant.Core.Tools;
using System;
using System.Collections.Generic;
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

    public sealed class SaveSettingsPayload
    {
        [JsonProperty("settings")] public SettingsControlsDto Settings { get; set; }
        [JsonProperty("apiKey")] public string ApiKey { get; set; }
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("expectedPromptPublication")] public ResourceRef ExpectedPromptPublication { get; set; }
        [JsonProperty("uploadLeaseId")] public string UploadLeaseId { get; set; }
        [JsonProperty("sha256")] public string Sha256 { get; set; }

        [JsonProperty("historySecret")]
        public string HistorySecret { get; set; }

    }

    public sealed class VbaToolPackagePayload
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("dryRun")]
        public bool DryRun { get; set; }
    }

    public sealed class SettingsResponse
    {
        [JsonProperty("appVersion")]
        public string AppVersion { get; set; }

        [JsonProperty("settings")]
        public SettingsControlsDto Settings { get; set; }

        [JsonProperty("prompts")] public PromptLibraryResponse Prompts { get; set; }

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

}
