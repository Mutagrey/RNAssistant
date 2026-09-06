using System.Collections.Generic;
using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Contracts
{
    public sealed class PromptLibraryResponse
    {
        public const string ContractType = "rnassistant.promptLibrary";
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("contractVersion")] public int ContractVersion { get; set; }
        [JsonProperty("publication")] public ResourceRef Publication { get; set; }
        [JsonProperty("items")] public IReadOnlyList<PromptMetadataDto> Items { get; set; }
    }

    public sealed class PromptMetadataDto
    {
        [JsonProperty("key")] public string Key { get; set; }
        [JsonProperty("resource")] public ResourceRef Resource { get; set; }
    }

    public sealed class PromptSourceReadRequest
    {
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("resource")] public ResourceRef Resource { get; set; }
    }

    public sealed class PromptSourceReadResponse
    {
        public const string ContractType = "rnassistant.promptSource";
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("contractVersion")] public int ContractVersion { get; set; }
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("resource")] public ResourceRef Resource { get; set; }
        [JsonProperty("totalCharacters")] public int TotalCharacters { get; set; }
        [JsonProperty("data")] public ResourceDownloadOpenResponse Data { get; set; }
    }

    public sealed class PromptMutationUploadRequest
    {
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("byteLength")] public long ByteLength { get; set; }
    }

    // Complete typed upload body, never an inline bridge control message.
    public sealed class PromptMutationBatch
    {
        public const string ContractType = "rnassistant.promptMutation";
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("contractVersion")] public int ContractVersion { get; set; }
        [JsonProperty("changes")] public IReadOnlyList<PromptFieldChange> Changes { get; set; }
    }

    public sealed class PromptFieldChange
    {
        [JsonProperty("resource")] public ResourceRef Resource { get; set; }
        [JsonProperty("value")] public string Value { get; set; }
    }
}
