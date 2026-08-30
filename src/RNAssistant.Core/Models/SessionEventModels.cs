using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Core.Models
{
    public static class SessionEventTypes
    {
        public const string SessionCreated = "session.created";
        public const string SessionForked = "session.forked";
        public const string SessionCommit = "session.commit";
        public const string TurnStarted = "turn.started";
        public const string TurnEnded = "turn.ended";
        public const string StepStarted = "step.started";
        public const string StepEnded = "step.ended";
        public const string AssistantChunk = "assistant.chunk";
        public const string LlmRequest = "llm.request";
        public const string LlmResponse = "llm.response";
        public const string LlmFailure = "llm.failure";
        public const string AgentResponseRejected = "agent.response.rejected";
        public const string ModelResponseAccepted = "model.response.accepted";
        public const string ToolPackExtensionAccepted = "tool_pack.extension.accepted";
        public const string ToolPackExtensionRejected = "tool_pack.extension.rejected";
        public const string RunStartedObservation = "run.started";
        public const string RunSummaryCreated = "run.summary.created";
        public const string UiProjected = "ui.projected";
        public const string ToolExecutionStartedObservation = "tool.execution.started";
        public const string ToolExecutionCompletedObservation = "tool.execution.completed";
        public const string DomainEffectPrepared = "domain.effect.prepared";
        public const string DomainEffectDispatched = "domain.effect.dispatched";
        public const string DomainEffectVerified = "domain.effect.verified";
    }

    public static class SessionOperationTypes
    {
        public const string SessionMetadataSet = "session.metadata.set";
        public const string ContextSet = "context.set";
        public const string RunStarted = "run.started";
        public const string RunUpdated = "run.updated";
        public const string RunEnded = "run.ended";
        public const string MessageUpdated = "message.updated";
        public const string UserMessageAppended = "user.message.appended";
        public const string AssistantMessageAppended = "assistant.message.appended";
        public const string ToolCallRecorded = "tool.call.recorded";
        public const string ToolResultRecorded = "tool.result.recorded";
        public const string ToolExecutionStarted = "tool.execution.started";
        public const string ToolExecutionFinished = "tool.execution.finished";
        public const string MessageRemove = "message.remove";
        public const string MessagesReorder = "messages.reorder";
        public const string ArtifactRevisionCreated = "artifact.revision.created";
        public const string ArtifactRemove = "artifact.remove";
        public const string ArtifactsReorder = "artifacts.reorder";
        public const string ActiveReferencesSet = "active_references.set";
    }

    public sealed class SessionEvent
    {
        public const int CurrentSchemaVersion = 3;

        public int SchemaVersion { get; set; }
        public string SessionId { get; set; }
        public long Sequence { get; set; }
        public string EventId { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string Type { get; set; }
        public string RunId { get; set; }
        public string TurnId { get; set; }
        public string StepId { get; set; }
        public string PreviousHash { get; set; }
        public string HashAlgorithm { get; set; }
        public string ProtectionKeyId { get; set; }
        public string Hash { get; set; }
        public JToken Data { get; set; }
        public string EncryptedData { get; set; }
        public ChatBlobReference Payload { get; set; }
        [JsonIgnore]
        internal long StorageByteOffset { get; set; }

        public SessionEvent()
        {
            SchemaVersion = CurrentSchemaVersion;
            EventId = Guid.NewGuid().ToString("N");
            CreatedUtc = DateTime.UtcNow;
            HashAlgorithm = HistoryIntegrityModes.Sha256;
        }

        public bool ShouldSerializeData()
        {
            return string.IsNullOrWhiteSpace(EncryptedData);
        }

        public bool ShouldSerializeEncryptedData()
        {
            return !string.IsNullOrWhiteSpace(EncryptedData);
        }
    }

    public sealed class SessionOperation
    {
        public string Type { get; set; }
        public JObject Data { get; set; }
    }

    public sealed class ToolPackSchemaRevision
    {
        public string Id { get; set; }
        public string Revision { get; set; }
    }

    public sealed class ToolPackExtensionEventData
    {
        public const int CurrentContractVersion = 1;

        public int ContractVersion { get; set; }
        public string Mode { get; set; }
        public string Host { get; set; }
        public string Profile { get; set; }
        public string CatalogRevision { get; set; }
        public string PreviousSnapshotRevision { get; set; }
        public string SnapshotRevision { get; set; }
        public bool Admitted { get; set; }
        public string Code { get; set; }
        public List<ToolPackSchemaRevision> RequestedSchemas { get; set; }

        public ToolPackExtensionEventData()
        {
            ContractVersion = CurrentContractVersion;
            RequestedSchemas = new List<ToolPackSchemaRevision>();
        }
    }

    public sealed class ChatBlobReference
    {
        public string Sha256 { get; set; }
        public long ByteLength { get; set; }
        public string ContentType { get; set; }
        public string Encryption { get; set; }
        public string ProtectionKeyId { get; set; }
    }
}
