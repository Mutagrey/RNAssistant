using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Contracts
{
    public sealed class ModelContextQuery : ChatPayload
    {
        [JsonProperty("beforeSequence")] public long? BeforeSequence { get; set; }
        [JsonProperty("knownRevision")] public long? KnownRevision { get; set; }
        [JsonProperty("requestEventId")] public string RequestEventId { get; set; }
        [JsonProperty("alias")] public string Alias { get; set; }
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
        [JsonProperty("alias")] public string Alias { get; set; }
    }
    [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public enum ModelContextCurrentness { Unknown, Current, Superseded, Removed, Unavailable }

    public sealed class ModelContextResourceObservationDto
    {
        [JsonProperty("requestAlias")] public string RequestAlias { get; set; }
        [JsonProperty("operationAlias")] public string OperationAlias { get; set; }
        [JsonProperty("evidenceAlias")] public string EvidenceAlias { get; set; }
        [JsonProperty("view")] public string View { get; set; }
        [JsonProperty("coverage")] public ResourceCoverage Coverage { get; set; }
        [JsonProperty("observationComplete")] public bool? ObservationComplete { get; set; }
        [JsonProperty("bodyIncludedInMessage")] public bool? BodyIncludedInMessage { get; set; }
    }

    public sealed class ModelContextIdentityDto
    {
        [JsonProperty("alias")] public string Alias { get; set; }
        [JsonProperty("canonicalId")] public string CanonicalId { get; set; }
    }

    public sealed class ModelContextResourceDto
    {
        [JsonProperty("alias")] public string Alias { get; set; }
        [JsonProperty("resource")] public ResourceRef Resource { get; set; }
        [JsonProperty("scope")] public ResourceAuthorityScopeId Scope { get; set; }
        [JsonProperty("currentness")] public ModelContextCurrentness Currentness { get; set; }
        [JsonProperty("revisionRecorded")] public bool? RevisionRecorded { get; set; }
        [JsonProperty("observations")] public IReadOnlyList<ModelContextResourceObservationDto> Observations { get; set; }
    }
    public sealed class ModelContextResponse
    {
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("revision")] public long Revision { get; set; }
        [JsonProperty("unchanged")] public bool Unchanged { get; set; }
        [JsonProperty("nextBeforeSequence")] public long? NextBeforeSequence { get; set; }
        [JsonProperty("events")] public IReadOnlyList<ModelContextEventDto> Events { get; set; }
        [JsonProperty("selectedAlias")] public string SelectedAlias { get; set; }
        [JsonProperty("selectedResourceAlias")] public string SelectedResourceAlias { get; set; }
        [JsonProperty("resources")] public IReadOnlyList<ModelContextResourceDto> Resources { get; set; }
        [JsonProperty("operations")] public IReadOnlyList<ModelContextIdentityDto> Operations { get; set; }
        [JsonProperty("evidence")] public IReadOnlyList<ModelContextIdentityDto> Evidence { get; set; }
    }
    public sealed class ModelContextPayloadResponse
    {
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("eventId")] public string EventId { get; set; }
        [JsonProperty("data")] public ResourceDownloadOpenResponse Data { get; set; }
    }
}
