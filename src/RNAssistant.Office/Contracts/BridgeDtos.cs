using System.Collections.Generic;
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
    }

    public sealed class RestoreVbaBackupPayload
    {
        [JsonProperty("backupId")]
        public string BackupId { get; set; }

        [JsonProperty("moduleName")]
        public string ModuleName { get; set; }
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
    }

    public sealed class HtmlWorkspaceDto
    {
        [JsonProperty("activeFileId")] public string ActiveFileId { get; set; }
        [JsonProperty("files")] public IReadOnlyList<HtmlWorkspaceFile> Files { get; set; }
        [JsonProperty("dataSources")] public IReadOnlyList<HtmlWorkspaceDataSource> DataSources { get; set; }
        [JsonProperty("history")] public IReadOnlyList<HtmlWorkspaceSnapshotDto> History { get; set; }
        [JsonProperty("redoHistory")] public IReadOnlyList<HtmlWorkspaceSnapshotDto> RedoHistory { get; set; }
        [JsonProperty("updatedUtc")] public System.DateTime UpdatedUtc { get; set; }

        public static HtmlWorkspaceDto From(HtmlWorkspace workspace)
        {
            workspace = workspace ?? new HtmlWorkspace();
            return new HtmlWorkspaceDto
            {
                ActiveFileId = workspace.ActiveFileId,
                Files = HtmlWorkspaceCopyService.CloneFiles(workspace.Files),
                DataSources = HtmlWorkspaceCopyService.CloneDataSources(workspace.DataSources),
                History = SnapshotSummaries(workspace.History),
                RedoHistory = SnapshotSummaries(workspace.RedoHistory),
                UpdatedUtc = workspace.UpdatedUtc
            };
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
