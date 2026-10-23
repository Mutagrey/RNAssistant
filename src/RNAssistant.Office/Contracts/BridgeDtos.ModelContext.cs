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
        [JsonProperty("verifyResourceAlias")] public string VerifyResourceAlias { get; set; }
        [JsonProperty("verifyRetainedView")] public string VerifyRetainedView { get; set; }
        [JsonProperty("verifyViewVariantOrdinal")] public int? VerifyViewVariantOrdinal { get; set; }
        [JsonProperty("verifyViewVariantSnapshotCount")] public int? VerifyViewVariantSnapshotCount { get; set; }
        [JsonProperty("viewInventoryAlias")] public string ViewInventoryAlias { get; set; }
        [JsonProperty("viewInventoryOffset")] public int? ViewInventoryOffset { get; set; }
        [JsonProperty("viewInventorySnapshotCount")] public int? ViewInventorySnapshotCount { get; set; }
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

    [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public enum ModelContextPayloadAvailability { Unknown, NotRetained, Unchecked, Verified, Unavailable }

    public sealed class ModelContextResourceObservationDto
    {
        [JsonProperty("requestAlias")] public string RequestAlias { get; set; }
        [JsonProperty("operationAlias")] public string OperationAlias { get; set; }
        [JsonProperty("evidenceAlias")] public string EvidenceAlias { get; set; }
        [JsonProperty("view")] public string View { get; set; }
        [JsonProperty("coverage")] public ResourceCoverage Coverage { get; set; }
        [JsonProperty("observationComplete")] public bool? ObservationComplete { get; set; }
        [JsonProperty("readMetadata")] public ResourceReadMetadata ReadMetadata { get; set; }
        [JsonProperty("bodyIncludedInMessage")] public bool? BodyIncludedInMessage { get; set; }
        [JsonProperty("estimatedWireMessageTokens", NullValueHandling = NullValueHandling.Ignore)]
        public int? EstimatedWireMessageTokens { get; set; }
    }

    public sealed class ModelContextIdentityDto
    {
        [JsonProperty("alias")] public string Alias { get; set; }
        [JsonProperty("canonicalId")] public string CanonicalId { get; set; }
    }

    public sealed class ModelContextRetainedViewDto
    {
        [JsonProperty("view")] public string View { get; set; }
        [JsonProperty("coverage")] public ResourceCoverage Coverage { get; set; }
        [JsonProperty("payloadAvailability")] public ModelContextPayloadAvailability PayloadAvailability { get; set; }
        [JsonProperty("partsCount")] public int PartsCount { get; set; }
        [JsonProperty("partsAvailability", NullValueHandling = NullValueHandling.Ignore)]
        public ModelContextPayloadAvailability? PartsAvailability { get; set; }
        [JsonProperty("checkedUtc", NullValueHandling = NullValueHandling.Ignore)] public DateTime? CheckedUtc { get; set; }
        [JsonProperty("estimatedTextBodyTokens", NullValueHandling = NullValueHandling.Ignore)]
        public long? EstimatedTextBodyTokens { get; set; }
        [JsonIgnore] internal ResourceRevisionView Retained { get; set; }
    }

    public sealed class ModelContextViewInventoryItemDto
    {
        [JsonProperty("ordinal")] public int Ordinal { get; set; }
        [JsonProperty("view")] public string View { get; set; }
        [JsonProperty("coverage")] public ResourceCoverage Coverage { get; set; }
        [JsonProperty("payloadRegistered")] public bool PayloadRegistered { get; set; }
        [JsonProperty("partsCount")] public int PartsCount { get; set; }
        [JsonProperty("estimatedTextBodyTokens", NullValueHandling = NullValueHandling.Ignore)]
        public long? EstimatedTextBodyTokens { get; set; }
    }

    public sealed class ModelContextViewInventoryDto
    {
        [JsonProperty("resourceAlias")] public string ResourceAlias { get; set; }
        [JsonProperty("snapshotCount")] public int SnapshotCount { get; set; }
        [JsonProperty("nextOffset")] public int? NextOffset { get; set; }
        [JsonProperty("items")] public IReadOnlyList<ModelContextViewInventoryItemDto> Items { get; set; }
    }

    public sealed class ModelContextViewVariantVerificationDto
    {
        [JsonProperty("resourceAlias")] public string ResourceAlias { get; set; }
        [JsonProperty("ordinal")] public int Ordinal { get; set; }
        [JsonProperty("snapshotCount")] public int SnapshotCount { get; set; }
        [JsonProperty("variant")] public ModelContextRetainedViewDto Variant { get; set; }
    }

    public sealed class ModelContextResourceDto
    {
        [JsonProperty("alias")] public string Alias { get; set; }
        [JsonProperty("resource")] public ResourceRef Resource { get; set; }
        [JsonProperty("scope")] public ResourceAuthorityScopeId Scope { get; set; }
        [JsonProperty("currentness")] public ModelContextCurrentness Currentness { get; set; }
        [JsonProperty("revisionRecorded")] public bool? RevisionRecorded { get; set; }
        [JsonProperty("revisionMetadata")] public ResourceRevisionMetadata RevisionMetadata { get; set; }
        [JsonProperty("payloadAvailability")] public ModelContextPayloadAvailability PayloadAvailability { get; set; }
        [JsonProperty("payloadCheckedUtc")] public DateTime? PayloadCheckedUtc { get; set; }
        [JsonProperty("retainedViews", NullValueHandling = NullValueHandling.Ignore)]
        public IReadOnlyList<ModelContextRetainedViewDto> RetainedViews { get; set; }
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
        [JsonProperty("viewInventory", NullValueHandling = NullValueHandling.Ignore)]
        public ModelContextViewInventoryDto ViewInventory { get; set; }
        [JsonProperty("viewVariantVerification", NullValueHandling = NullValueHandling.Ignore)]
        public ModelContextViewVariantVerificationDto ViewVariantVerification { get; set; }
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
