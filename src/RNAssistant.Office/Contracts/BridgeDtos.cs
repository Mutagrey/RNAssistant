using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Office.Contracts
{
    public sealed class BridgeRequest
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("payload")]
        public JToken Payload { get; set; }

        [JsonProperty("bridgeToken")]
        public string BridgeToken { get; set; }
    }

    public sealed class FocusStateMessage
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("payload")]
        public FocusStatePayload Payload { get; set; }
    }

    public sealed class FocusStatePayload
    {
        [JsonProperty("wantsKeyboard")]
        public bool WantsKeyboard { get; set; }
    }

    public sealed class BridgeResponse
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("ok")]
        public bool Ok { get; set; }

        [JsonProperty("payload", NullValueHandling = NullValueHandling.Ignore)]
        public JToken Payload { get; set; }

        [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
        public string Error { get; set; }

        [JsonProperty("errorDetail", NullValueHandling = NullValueHandling.Ignore)]
        public string ErrorDetail { get; set; }

        [JsonProperty("cancelled", NullValueHandling = NullValueHandling.Ignore)]
        public bool? Cancelled { get; set; }
    }

    public sealed class CancellationResponse
    {
        [JsonProperty("cancelled")]
        public bool Cancelled { get; set; }
    }

    public sealed class DeleteResponse
    {
        [JsonProperty("deleted")]
        public bool Deleted { get; set; }
    }

    public sealed class ProgressMessage
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("payload")]
        public ProgressPayload Payload { get; set; }
    }

    public sealed class ProgressPayload
    {
        [JsonProperty("chatId")]
        public string ChatId { get; set; }

        [JsonProperty("runId")]
        public string RunId { get; set; }

        [JsonProperty("phase")]
        public string Phase { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("activity", NullValueHandling = NullValueHandling.Ignore)]
        public ChatActivity Activity { get; set; }

        [JsonProperty("reasoningDelta", NullValueHandling = NullValueHandling.Ignore)]
        public string ReasoningDelta { get; set; }

        [JsonProperty("reasoningComplete", NullValueHandling = NullValueHandling.Ignore)]
        public bool? ReasoningComplete { get; set; }

        [JsonProperty("contentDelta", NullValueHandling = NullValueHandling.Ignore)]
        public string ContentDelta { get; set; }
    }

    public sealed class ModelRequestDiagnosticsMessage
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("payload")]
        public ModelRequestDiagnosticsDto Payload { get; set; }
    }

    public sealed class ModelRequestDiagnosticsDto
    {
        [JsonProperty("requestId")]
        public string RequestId { get; set; }

        [JsonProperty("phase")]
        public string Phase { get; set; }

        [JsonProperty("model")]
        public string Model { get; set; }

        [JsonProperty("streamRequested")]
        public bool StreamRequested { get; set; }

        [JsonProperty("elapsedMs")]
        public long ElapsedMs { get; set; }

        [JsonProperty("preparationMs", NullValueHandling = NullValueHandling.Ignore)]
        public long? PreparationMs { get; set; }

        [JsonProperty("responseHeadersMs", NullValueHandling = NullValueHandling.Ignore)]
        public long? ResponseHeadersMs { get; set; }

        [JsonProperty("firstChunkMs", NullValueHandling = NullValueHandling.Ignore)]
        public long? FirstChunkMs { get; set; }

        [JsonProperty("totalMs", NullValueHandling = NullValueHandling.Ignore)]
        public long? TotalMs { get; set; }

        [JsonProperty("requestBytes", NullValueHandling = NullValueHandling.Ignore)]
        public long? RequestBytes { get; set; }

        [JsonProperty("statusCode", NullValueHandling = NullValueHandling.Ignore)]
        public int? StatusCode { get; set; }

        [JsonProperty("failureKind", NullValueHandling = NullValueHandling.Ignore)]
        public string FailureKind { get; set; }

        [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
        public string Error { get; set; }

        public static ModelRequestDiagnosticsDto From(LlmRequestDiagnosticUpdate source)
        {
            if (source == null) return null;
            return new ModelRequestDiagnosticsDto
            {
                RequestId = source.RequestId,
                Phase = source.Phase,
                Model = source.Model,
                StreamRequested = source.StreamRequested,
                ElapsedMs = source.ElapsedMs,
                PreparationMs = source.PreparationMs,
                ResponseHeadersMs = source.ResponseHeadersMs,
                FirstChunkMs = source.FirstChunkMs,
                TotalMs = source.TotalMs,
                RequestBytes = source.RequestBytes,
                StatusCode = source.StatusCode,
                FailureKind = source.FailureKind.HasValue ? source.FailureKind.Value.ToString() : null,
                Error = source.Error
            };
        }
    }

    public sealed class RuntimeLogResponse
    {
        [JsonProperty("content")]
        public string Content { get; set; }

        [JsonProperty("path")]
        public string Path { get; set; }
    }

    public sealed class ChatStateMessage
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("payload")]
        public ChatStateResponse Payload { get; set; }
    }

    public class ChatPayload
    {
        [JsonProperty("chatId")]
        public string ChatId { get; set; }
    }

    public sealed class PromptContextInspectorPayload : ChatPayload
    {
        [JsonProperty("text")]
        public string Text { get; set; }

        [JsonProperty("attachmentIds")]
        public IReadOnlyList<string> AttachmentIds { get; set; }

        [JsonProperty("includeRaw")]
        public bool IncludeRaw { get; set; }
    }

    public sealed class PromptContextInspectorResponse
    {
        [JsonProperty("chatId")]
        public string ChatId { get; set; }

        [JsonProperty("sessionRevision")]
        public long SessionRevision { get; set; }

        [JsonProperty("mode")]
        public string Mode { get; set; }

        [JsonProperty("model")]
        public string Model { get; set; }

        [JsonProperty("usedTokens")]
        public int UsedTokens { get; set; }

        [JsonProperty("inputLimitTokens")]
        public int InputLimitTokens { get; set; }

        [JsonProperty("contextWindowTokens")]
        public int ContextWindowTokens { get; set; }

        [JsonProperty("reservedOutputTokens")]
        public int ReservedOutputTokens { get; set; }

        [JsonProperty("safetyTokens")]
        public int SafetyTokens { get; set; }

        [JsonProperty("remainingInputTokens")]
        public int RemainingInputTokens { get; set; }

        [JsonProperty("percent")]
        public int Percent { get; set; }

        [JsonProperty("messageCount")]
        public int MessageCount { get; set; }

        [JsonProperty("overBudget")]
        public bool OverBudget { get; set; }

        [JsonProperty("estimated")]
        public bool Estimated { get; set; }

        [JsonProperty("estimateMultiplier")]
        public double EstimateMultiplier { get; set; }

        [JsonProperty("estimateInterceptTokens")]
        public int EstimateInterceptTokens { get; set; }

        [JsonProperty("manualEstimateMultiplier")]
        public double ManualEstimateMultiplier { get; set; }

        [JsonProperty("autoCalibrateEstimate")]
        public bool AutoCalibrateEstimate { get; set; }

        [JsonProperty("calibrationSamples")]
        public int CalibrationSamples { get; set; }

        [JsonProperty("estimateMethod")]
        public string EstimateMethod { get; set; }

        [JsonProperty("lastPromptTokens")]
        public int? LastPromptTokens { get; set; }

        [JsonProperty("lastPromptUtc")]
        public System.DateTime? LastPromptUtc { get; set; }

        [JsonProperty("lastRunId")]
        public string LastRunId { get; set; }

        [JsonProperty("notice")]
        public string Notice { get; set; }

        [JsonProperty("sections")]
        public IReadOnlyList<PromptContextSectionDto> Sections { get; set; }

        [JsonProperty("rawRequestJson")]
        public string RawRequestJson { get; set; }

        [JsonProperty("rawTruncated")]
        public bool RawTruncated { get; set; }

        [JsonProperty("generatedUtc")]
        public System.DateTime GeneratedUtc { get; set; }
    }

    public sealed class PromptContextSectionDto
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("tokens")]
        public int Tokens { get; set; }

        [JsonProperty("count")]
        public int Count { get; set; }

        [JsonProperty("detail")]
        public string Detail { get; set; }

        [JsonProperty("included")]
        public bool Included { get; set; }

        [JsonProperty("items")]
        public IReadOnlyList<PromptContextItemDto> Items { get; set; }
    }

    public sealed class PromptContextItemDto
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("kind")]
        public string Kind { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("subtitle")]
        public string Subtitle { get; set; }

        [JsonProperty("tokens")]
        public int Tokens { get; set; }

        [JsonProperty("characters")]
        public int Characters { get; set; }

        [JsonProperty("sizeBytes")]
        public long SizeBytes { get; set; }

        [JsonProperty("preview")]
        public string Preview { get; set; }

        [JsonProperty("reference")]
        public string Reference { get; set; }

        [JsonProperty("reason")]
        public string Reason { get; set; }
    }

    public sealed class DocumentPayload
    {
        [JsonProperty("host")]
        public string Host { get; set; }

        [JsonProperty("documentKey")]
        public string DocumentKey { get; set; }
    }

    public sealed class OpenOfficeDocumentDto
    {
        [JsonProperty("host")]
        public string Host { get; set; }

        [JsonProperty("documentKey")]
        public string DocumentKey { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("path")]
        public string Path { get; set; }

        [JsonProperty("isActive")]
        public bool IsActive { get; set; }
    }

    public sealed class CreateChatPayload
    {
        [JsonProperty("title")]
        public string Title { get; set; }
    }

    public sealed class CreateDocumentChatPayload
    {
        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("host")]
        public string Host { get; set; }

        [JsonProperty("documentKey")]
        public string DocumentKey { get; set; }

        [JsonProperty("documentTitle")]
        public string DocumentTitle { get; set; }

        [JsonProperty("documentPath")]
        public string DocumentPath { get; set; }
    }

    public sealed class RenameChatPayload : ChatPayload
    {
        [JsonProperty("title")]
        public string Title { get; set; }
    }

    public sealed class SetChatModelPayload : ChatPayload
    {
        [JsonProperty("model")]
        public string Model { get; set; }
    }

    public sealed class SetChatModePayload : ChatPayload
    {
        [JsonProperty("mode")]
        public string Mode { get; set; }
    }

    public sealed class SetChatHtmlModePayload : ChatPayload
    {
        [JsonProperty("enabled")]
        public bool? Enabled { get; set; }
    }

    public sealed class SetChatReasoningPayload : ChatPayload
    {
        [JsonProperty("enabled")]
        public bool? Enabled { get; set; }
    }

    public sealed class SendChatPayload : ChatPayload
    {
        [JsonProperty("text")]
        public string Text { get; set; }

        [JsonProperty("attachmentIds")]
        public List<string> AttachmentIds { get; set; }
    }

    public sealed class ImportAttachmentPayload
    {
        [JsonProperty("fileName")]
        public string FileName { get; set; }

        [JsonProperty("contentType")]
        public string ContentType { get; set; }

        [JsonProperty("base64")]
        public string Base64 { get; set; }
    }

    public sealed class DeleteDraftAttachmentPayload
    {
        [JsonProperty("id")]
        public string Id { get; set; }
    }

    public sealed class CancelRequestPayload
    {
        [JsonProperty("requestId")]
        public string RequestId { get; set; }
    }

    public sealed class CancelChatRunPayload : ChatPayload
    {
        [JsonProperty("runId")]
        public string RunId { get; set; }
    }

    public sealed class ChatEventPayloadRequest : ChatPayload
    {
        [JsonProperty("eventId")]
        public string EventId { get; set; }
    }

    public sealed class ChatTrajectoryResponse
    {
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("revision")] public long Revision { get; set; }
        [JsonProperty("totalEvents")] public int TotalEvents { get; set; }
        [JsonProperty("startSequence")] public long? StartSequence { get; set; }
        [JsonProperty("truncated")] public bool Truncated { get; set; }
        [JsonProperty("events")] public IReadOnlyList<SessionEventDto> Events { get; set; }
    }

    public sealed class SessionEventDto
    {
        private const int MaxInlineDataChars = 65536;

        [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonProperty("sequence")] public long Sequence { get; set; }
        [JsonProperty("eventId")] public string EventId { get; set; }
        [JsonProperty("createdUtc")] public System.DateTime CreatedUtc { get; set; }
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("runId")] public string RunId { get; set; }
        [JsonProperty("turnId")] public string TurnId { get; set; }
        [JsonProperty("stepId")] public string StepId { get; set; }
        [JsonProperty("previousHash")] public string PreviousHash { get; set; }
        [JsonProperty("hashAlgorithm")] public string HashAlgorithm { get; set; }
        [JsonProperty("protectionKeyId")] public string ProtectionKeyId { get; set; }
        [JsonProperty("hash")] public string Hash { get; set; }
        [JsonProperty("dataEncrypted")] public bool DataEncrypted { get; set; }
        [JsonProperty("dataJson")] public string DataJson { get; set; }
        [JsonProperty("dataTruncated")] public bool DataTruncated { get; set; }
        [JsonProperty("payloadSha256")] public string PayloadSha256 { get; set; }
        [JsonProperty("payloadByteLength")] public long? PayloadByteLength { get; set; }
        [JsonProperty("payloadContentType")] public string PayloadContentType { get; set; }
        [JsonProperty("payloadEncryption")] public string PayloadEncryption { get; set; }

        public static SessionEventDto From(SessionEvent sessionEvent)
        {
            if (sessionEvent == null) return null;
            var data = sessionEvent.Data == null ? string.Empty : sessionEvent.Data.ToString(Formatting.None);
            var bounded = data.Length <= MaxInlineDataChars ? data : data.Substring(0, MaxInlineDataChars);
            return new SessionEventDto
            {
                SchemaVersion = sessionEvent.SchemaVersion,
                Sequence = sessionEvent.Sequence,
                EventId = sessionEvent.EventId,
                CreatedUtc = sessionEvent.CreatedUtc,
                Type = sessionEvent.Type,
                RunId = sessionEvent.RunId,
                TurnId = sessionEvent.TurnId,
                StepId = sessionEvent.StepId,
                PreviousHash = sessionEvent.PreviousHash,
                HashAlgorithm = sessionEvent.HashAlgorithm,
                ProtectionKeyId = sessionEvent.ProtectionKeyId,
                Hash = sessionEvent.Hash,
                DataEncrypted = !string.IsNullOrWhiteSpace(sessionEvent.EncryptedData),
                DataJson = bounded,
                DataTruncated = bounded.Length < data.Length,
                PayloadSha256 = sessionEvent.Payload == null ? null : sessionEvent.Payload.Sha256,
                PayloadByteLength = sessionEvent.Payload == null ? (long?)null : sessionEvent.Payload.ByteLength,
                PayloadContentType = sessionEvent.Payload == null ? null : sessionEvent.Payload.ContentType,
                PayloadEncryption = sessionEvent.Payload == null ? null : sessionEvent.Payload.Encryption
            };
        }
    }

    public sealed class ChatEventPayloadResponse
    {
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("eventId")] public string EventId { get; set; }
        [JsonProperty("sha256")] public string Sha256 { get; set; }
        [JsonProperty("byteLength")] public long ByteLength { get; set; }
        [JsonProperty("contentType")] public string ContentType { get; set; }
        [JsonProperty("text")] public string Text { get; set; }
        [JsonProperty("textTruncated")] public bool TextTruncated { get; set; }
    }

    public sealed class HtmlFetchRequest
    {
        [JsonProperty("url")]
        public string Url { get; set; }

        [JsonProperty("method")]
        public string Method { get; set; }

        [JsonProperty("headers")]
        public Dictionary<string, string> Headers { get; set; }

        [JsonProperty("body")]
        public string Body { get; set; }
    }

    public sealed class HtmlOriginPayload
    {
        [JsonProperty("origin")]
        public string Origin { get; set; }
    }

    public sealed class HtmlOriginPermissionResponse
    {
        [JsonProperty("origin")]
        public string Origin { get; set; }

        [JsonProperty("allowed")]
        public bool Allowed { get; set; }
    }

    public sealed class HtmlFetchResponse
    {
        [JsonProperty("url")]
        public string Url { get; set; }

        [JsonProperty("status")]
        public int Status { get; set; }

        [JsonProperty("statusText")]
        public string StatusText { get; set; }

        [JsonProperty("headers")]
        public Dictionary<string, string> Headers { get; set; }

        [JsonProperty("body")]
        public string Body { get; set; }
    }

    public class MessageActionPayload : ChatPayload
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("index")]
        public int? Index { get; set; }
    }

    public sealed class EditMessagePayload : MessageActionPayload
    {
        [JsonProperty("text")]
        public string Text { get; set; }
    }

    public sealed class UpdateMessageActivityDataPayload : ChatPayload
    {
        [JsonProperty("messageId")]
        public string MessageId { get; set; }

        [JsonProperty("dataJson")]
        public string DataJson { get; set; }
    }

    public sealed class RunToolPayload
    {
        [JsonProperty("toolId")]
        public string ToolId { get; set; }

        [JsonProperty("arguments")]
        public IDictionary<string, object> Arguments { get; set; }

        [JsonProperty("dryRun")]
        public bool DryRun { get; set; }
    }

    public sealed class PendingAgentToolPayload : ChatPayload
    {
        [JsonProperty("pendingId")]
        public string PendingId { get; set; }
    }

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

    public sealed class VbaModulePayload
    {
        [JsonProperty("moduleName")]
        public string ModuleName { get; set; }

        [JsonProperty("code")]
        public string Code { get; set; }

        [JsonProperty("expectedCodeSha256")]
        public string ExpectedCodeSha256 { get; set; }
    }

    public sealed class VbaCreateModulePayload
    {
        [JsonProperty("moduleName")]
        public string ModuleName { get; set; }

        [JsonProperty("componentType")]
        public string ComponentType { get; set; }

        [JsonProperty("code")]
        public string Code { get; set; }
    }

    public sealed class VbaDeleteModulePayload
    {
        [JsonProperty("moduleName")]
        public string ModuleName { get; set; }
    }

    public sealed class RestoreVbaBackupPayload
    {
        [JsonProperty("backupId")]
        public string BackupId { get; set; }

        [JsonProperty("moduleName")]
        public string ModuleName { get; set; }
    }

    public sealed class RunVbaMacroPayload
    {
        [JsonProperty("macroName")]
        public string MacroName { get; set; }
    }

    public sealed class SelectionContextPayload : ChatPayload
    {
        [JsonProperty("mode")]
        public string Mode { get; set; }
    }

    public sealed class TextContextPayload : ChatPayload
    {
        [JsonProperty("kind")]
        public string Kind { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("reference")]
        public string Reference { get; set; }

        [JsonProperty("text")]
        public string Text { get; set; }

        [JsonProperty("detailsJson")]
        public string DetailsJson { get; set; }
    }

    public sealed class HtmlWorkspaceFilePayload : ChatPayload
    {
        [JsonProperty("path")]
        public string Path { get; set; }

        [JsonProperty("kind")]
        public string Kind { get; set; }

        [JsonProperty("content")]
        public string Content { get; set; }

        [JsonProperty("setActive")]
        public bool? SetActive { get; set; }
    }

    public sealed class HtmlWorkspaceDataPayload : ChatPayload
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("json")]
        public string Json { get; set; }
    }

    public sealed class HtmlWorkspaceDeleteFilePayload : ChatPayload
    {
        [JsonProperty("path")]
        public string Path { get; set; }
    }

    public sealed class HtmlWorkspaceDeleteDataPayload : ChatPayload
    {
        [JsonProperty("name")]
        public string Name { get; set; }
    }

    public sealed class HtmlWorkspaceActiveFilePayload : ChatPayload
    {
        [JsonProperty("path")]
        public string Path { get; set; }
    }

    public sealed class HtmlWorkspaceRestorePayload : ChatPayload
    {
        [JsonProperty("snapshotId")]
        public string SnapshotId { get; set; }
    }

    public sealed class RemoveContextItemPayload : ChatPayload
    {
        [JsonProperty("id")]
        public string Id { get; set; }
    }

    public sealed class QuickActionPayload
    {
        [JsonProperty("action")]
        public string Action { get; set; }
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

    public sealed class VbaProjectResponse
    {
        [JsonProperty("result")]
        public ToolResult Result { get; set; }

        [JsonProperty("backups")]
        public IReadOnlyList<VbaModuleBackup> Backups { get; set; }
    }

    public sealed class VbaToolPackageResponse
    {
        [JsonProperty("result")]
        public ToolResult Result { get; set; }

        [JsonProperty("tools")]
        public IReadOnlyList<ToolDefinition> Tools { get; set; }
    }

    public sealed class HtmlWorkspaceResponse
    {
        [JsonProperty("activeChatId")]
        public string ActiveChatId { get; set; }

        [JsonProperty("workspace")]
        public HtmlWorkspaceDto Workspace { get; set; }

        [JsonProperty("redoChoiceRequired")]
        public bool RedoChoiceRequired { get; set; }
    }

    public sealed class HtmlWorkspaceDto
    {
        [JsonProperty("activeFileId")] public string ActiveFileId { get; set; }
        [JsonProperty("files")] public IReadOnlyList<HtmlWorkspaceFile> Files { get; set; }
        [JsonProperty("dataSources")] public IReadOnlyList<HtmlWorkspaceDataSource> DataSources { get; set; }
        [JsonProperty("history")] public IReadOnlyList<HtmlWorkspaceSnapshotDto> History { get; set; }
        [JsonProperty("redoHistory")] public IReadOnlyList<HtmlWorkspaceSnapshotDto> RedoHistory { get; set; }
        [JsonProperty("redoBranches")] public IReadOnlyList<HtmlWorkspaceRedoBranchDto> RedoBranches { get; set; }
        [JsonProperty("updatedUtc")] public System.DateTime UpdatedUtc { get; set; }

        public static HtmlWorkspaceDto From(HtmlWorkspace workspace)
        {
            workspace = workspace ?? new HtmlWorkspace();
            var redoBranches = RedoBranchSummaries(workspace.RedoBranches);
            return new HtmlWorkspaceDto
            {
                ActiveFileId = workspace.ActiveFileId,
                Files = HtmlWorkspaceCopyService.CloneFiles(workspace.Files),
                DataSources = HtmlWorkspaceCopyService.CloneDataSources(workspace.DataSources),
                History = SnapshotSummaries(workspace.History),
                RedoHistory = redoBranches.Select(item => new HtmlWorkspaceSnapshotDto
                {
                    Id = item.Id,
                    Label = item.Label,
                    CreatedUtc = item.CreatedUtc
                }).ToList(),
                RedoBranches = redoBranches,
                UpdatedUtc = workspace.UpdatedUtc
            };
        }

        private static IReadOnlyList<HtmlWorkspaceRedoBranchDto> RedoBranchSummaries(IEnumerable<HtmlWorkspaceRedoBranch> branches)
        {
            return (branches ?? new HtmlWorkspaceRedoBranch[0])
                .Where(branch => branch != null)
                .Select(branch => new HtmlWorkspaceRedoBranchDto
                {
                    Id = branch.Id,
                    ParentArtifactId = branch.ParentArtifactId,
                    Label = branch.Label,
                    Revision = branch.Revision,
                    FileCount = branch.FileCount,
                    DataSourceCount = branch.DataSourceCount,
                    CreatedUtc = branch.CreatedUtc
                }).ToList();
        }

        private static IReadOnlyList<HtmlWorkspaceSnapshotDto> SnapshotSummaries(IEnumerable<HtmlWorkspaceSnapshot> snapshots)
        {
            var result = new List<HtmlWorkspaceSnapshotDto>();
            foreach (var snapshot in snapshots ?? new HtmlWorkspaceSnapshot[0])
            {
                if (snapshot == null) continue;
                result.Add(new HtmlWorkspaceSnapshotDto
                {
                    Id = snapshot.Id,
                    Label = snapshot.Label,
                    CreatedUtc = snapshot.CreatedUtc
                });
            }
            return result;
        }
    }

    public sealed class HtmlWorkspaceSnapshotDto
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("label")] public string Label { get; set; }
        [JsonProperty("createdUtc")] public System.DateTime CreatedUtc { get; set; }
    }

    public sealed class HtmlWorkspaceRedoBranchDto
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("parentArtifactId")] public string ParentArtifactId { get; set; }
        [JsonProperty("label")] public string Label { get; set; }
        [JsonProperty("revision")] public int Revision { get; set; }
        [JsonProperty("fileCount")] public int? FileCount { get; set; }
        [JsonProperty("dataSourceCount")] public int? DataSourceCount { get; set; }
        [JsonProperty("createdUtc")] public System.DateTime CreatedUtc { get; set; }
    }

    public sealed class QuickActionResponse
    {
        [JsonProperty("prompt")]
        public string Prompt { get; set; }
    }

    public sealed class OpenDocumentResponse
    {
        [JsonProperty("path")]
        public string Path { get; set; }

        [JsonProperty("launched")]
        public bool Launched { get; set; }

        [JsonProperty("state")]
        public ChatStateResponse State { get; set; }
    }

    public sealed class ChatArtifactDto
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("kind")] public string Kind { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("mimeType")] public string MimeType { get; set; }
        [JsonProperty("sourceMessageId")] public string SourceMessageId { get; set; }
        [JsonProperty("runId")] public string RunId { get; set; }
        [JsonProperty("revision")] public int Revision { get; set; }
        [JsonProperty("parentArtifactId")] public string ParentArtifactId { get; set; }
        [JsonProperty("relativePath")] public string RelativePath { get; set; }
        [JsonProperty("contentSha256")] public string ContentSha256 { get; set; }
        [JsonProperty("contentByteLength")] public long? ContentByteLength { get; set; }
        [JsonProperty("inlineText")] public string InlineText { get; set; }
        [JsonProperty("inlineTruncated")] public bool InlineTruncated { get; set; }
        [JsonProperty("metadataJson")] public string MetadataJson { get; set; }
        [JsonProperty("relatedArtifactIds")] public IReadOnlyList<string> RelatedArtifactIds { get; set; }
        [JsonProperty("createdUtc")] public System.DateTime CreatedUtc { get; set; }

        public static IReadOnlyList<ChatArtifactDto> From(IEnumerable<ChatArtifact> artifacts)
        {
            var result = new List<ChatArtifactDto>();
            foreach (var artifact in artifacts ?? new ChatArtifact[0])
            {
                if (artifact == null) continue;
                var inline = artifact.InlineText ?? string.Empty;
                var includeInline = !string.Equals(artifact.Kind, ChatArtifactKinds.HtmlWorkspace, System.StringComparison.OrdinalIgnoreCase);
                var bounded = includeInline && inline.Length > 24000 ? inline.Substring(0, 24000) : includeInline ? inline : null;
                result.Add(new ChatArtifactDto
                {
                    Id = artifact.Id,
                    Kind = artifact.Kind,
                    Title = artifact.Title,
                    MimeType = artifact.MimeType,
                    SourceMessageId = artifact.SourceMessageId,
                    RunId = artifact.RunId,
                    Revision = artifact.Revision,
                    ParentArtifactId = artifact.ParentArtifactId,
                    RelativePath = artifact.RelativePath,
                    ContentSha256 = artifact.ContentSha256,
                    ContentByteLength = artifact.ContentByteLength,
                    InlineText = bounded,
                    InlineTruncated = includeInline && bounded != null && bounded.Length < inline.Length,
                    MetadataJson = artifact.MetadataJson,
                    RelatedArtifactIds = artifact.RelatedArtifactIds ?? new List<string>(),
                    CreatedUtc = artifact.CreatedUtc
                });
            }
            return result;
        }
    }

    public class ChatStateResponse
    {
        [JsonProperty("activeChatId")]
        public string ActiveChatId { get; set; }

        [JsonProperty("activeChatModel")]
        public string ActiveChatModel { get; set; }

        [JsonProperty("activeChatMode")]
        public string ActiveChatMode { get; set; }

        [JsonProperty("activeChatHtmlMode")]
        public bool ActiveChatHtmlMode { get; set; }

        [JsonProperty("activeChatReasoning")]
        public bool ActiveChatReasoning { get; set; }

        [JsonProperty("chats")]
        public IReadOnlyList<ChatSessionSummary> Chats { get; set; }

        [JsonProperty("documents")]
        public IReadOnlyList<OpenOfficeDocumentDto> Documents { get; set; }

        [JsonProperty("context")]
        public DocumentContext Context { get; set; }

        [JsonProperty("messages")]
        public IReadOnlyList<ChatMessage> Messages { get; set; }

        [JsonProperty("artifacts")]
        public IReadOnlyList<ChatArtifactDto> Artifacts { get; set; }

        [JsonProperty("activeContextCheckpointId")]
        public string ActiveContextCheckpointId { get; set; }

        [JsonProperty("activeHtmlArtifactId")]
        public string ActiveHtmlArtifactId { get; set; }

        [JsonProperty("activePlanArtifactId")]
        public string ActivePlanArtifactId { get; set; }

        [JsonProperty("contextUsage")]
        public object ContextUsage { get; set; }

        [JsonProperty("htmlWorkspace")]
        public HtmlWorkspaceDto HtmlWorkspace { get; set; }
    }

    public sealed class InitResponse
    {
        [JsonProperty("appVersion")]
        public string AppVersion { get; set; }

        [JsonProperty("host")]
        public string Host { get; set; }

        [JsonProperty("documentKey")]
        public string DocumentKey { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("officeContext")]
        public OfficeContext OfficeContext { get; set; }

        [JsonProperty("activeChatId")]
        public string ActiveChatId { get; set; }

        [JsonProperty("activeChatModel")]
        public string ActiveChatModel { get; set; }

        [JsonProperty("activeChatMode")]
        public string ActiveChatMode { get; set; }

        [JsonProperty("activeChatHtmlMode")]
        public bool ActiveChatHtmlMode { get; set; }

        [JsonProperty("activeChatReasoning")]
        public bool ActiveChatReasoning { get; set; }

        [JsonProperty("chats")]
        public IReadOnlyList<ChatSessionSummary> Chats { get; set; }

        [JsonProperty("documents")]
        public IReadOnlyList<OpenOfficeDocumentDto> Documents { get; set; }

        [JsonProperty("settings")]
        public AppSettings Settings { get; set; }

        [JsonProperty("hasApiKey")]
        public bool HasApiKey { get; set; }

        [JsonProperty("hasHistorySecret")]
        public bool HasHistorySecret { get; set; }

        [JsonProperty("tools")]
        public IReadOnlyList<ToolDefinition> Tools { get; set; }

        [JsonProperty("toolsPath")]
        public string ToolsPath { get; set; }

        [JsonProperty("skills")]
        public IReadOnlyList<SkillDefinition> Skills { get; set; }

        [JsonProperty("skillsPath")]
        public string SkillsPath { get; set; }

        [JsonProperty("context")]
        public DocumentContext Context { get; set; }

        [JsonProperty("messages")]
        public IReadOnlyList<ChatMessage> Messages { get; set; }

        [JsonProperty("artifacts")]
        public IReadOnlyList<ChatArtifactDto> Artifacts { get; set; }

        [JsonProperty("activeContextCheckpointId")]
        public string ActiveContextCheckpointId { get; set; }

        [JsonProperty("activeHtmlArtifactId")]
        public string ActiveHtmlArtifactId { get; set; }

        [JsonProperty("activePlanArtifactId")]
        public string ActivePlanArtifactId { get; set; }

        [JsonProperty("contextUsage")]
        public object ContextUsage { get; set; }

        [JsonProperty("htmlWorkspace")]
        public HtmlWorkspaceDto HtmlWorkspace { get; set; }

        [JsonProperty("quickAction")]
        public string QuickAction { get; set; }

        [JsonProperty("bridgeToken")]
        public string BridgeToken { get; set; }
    }

    public sealed class SendChatResponse : ChatStateResponse
    {
        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("toolResults")]
        public IReadOnlyList<object> ToolResults { get; set; }
    }

    public sealed class AttachmentResponse
    {
        public ChatAttachment Attachment { get; set; }
    }
}
