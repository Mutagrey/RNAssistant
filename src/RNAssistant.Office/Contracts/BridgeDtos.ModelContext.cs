using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using RNAssistant.Core.Llm;

namespace RNAssistant.Office.Contracts
{
    public sealed class ModelContextQuery : ChatPayload
    {
        [JsonProperty("beforeSequence")] public long? BeforeSequence { get; set; }
        [JsonProperty("knownRevision")] public long? KnownRevision { get; set; }
        [JsonProperty("requestEventId")] public string RequestEventId { get; set; }
    }
    public sealed class ModelContextPayloadQuery : ChatPayload
    {
        [JsonProperty("eventId")] public string EventId { get; set; }
        [JsonProperty("originalIndex")] public int? OriginalIndex { get; set; }
    }
    public sealed class ModelContextEventDto
    {
        [JsonProperty("eventId")] public string EventId { get; set; }
        [JsonProperty("sequence")] public long Sequence { get; set; }
        [JsonProperty("createdUtc")] public DateTime CreatedUtc { get; set; }
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("hasPayload")] public bool HasPayload { get; set; }
        [JsonProperty("trace")] public LlmTraceRecord Trace { get; set; }
    }
    public sealed class ModelContextResponse
    {
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("revision")] public long Revision { get; set; }
        [JsonProperty("unchanged")] public bool Unchanged { get; set; }
        [JsonProperty("nextBeforeSequence")] public long? NextBeforeSequence { get; set; }
        [JsonProperty("events")] public IReadOnlyList<ModelContextEventDto> Events { get; set; }
    }
    public sealed class ModelContextPayloadResponse
    {
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("eventId")] public string EventId { get; set; }
        [JsonProperty("data")] public ResourceDownloadOpenResponse Data { get; set; }
    }
}
