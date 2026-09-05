using RNAssistant.Core.Tools;
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

        [JsonProperty("contentReset", NullValueHandling = NullValueHandling.Ignore)]
        public bool? ContentReset { get; set; }

        [JsonProperty("reasoningReset", NullValueHandling = NullValueHandling.Ignore)]
        public bool? ReasoningReset { get; set; }
    }

    public sealed class ChatStateMessage
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("scope")]
        public string Scope { get; set; }

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

        [JsonProperty("resourceDraftIds")]
        public IReadOnlyList<string> ResourceDraftIds { get; set; }

        [JsonProperty("includeRaw")]
        public bool IncludeRaw { get; set; }
    }

    public sealed class PromptContextInspectorResponse
    {
        [JsonProperty("resourceContextReceipt")]
        public ContextReceipt ResourceContextReceipt { get; set; }

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

    public sealed class SetChatReasoningPayload : ChatPayload
    {
        [JsonProperty("enabled")]
        public bool? Enabled { get; set; }
    }

    public sealed class SendChatPayload : ChatPayload
    {
        [JsonProperty("text")]
        public string Text { get; set; }

        [JsonProperty("resourceDraftIds")]
        public List<string> ResourceDraftIds { get; set; }
    }

    public sealed class StageChatResourcePayload : ChatPayload
    {
        [JsonProperty("fileName")]
        public string FileName { get; set; }

        [JsonProperty("contentType")]
        public string ContentType { get; set; }

        [JsonProperty("base64")]
        public string Base64 { get; set; }
    }

    public sealed class DiscardChatResourceDraftPayload : ChatPayload
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
        [JsonProperty("resourceUri")] public string ResourceUri { get; set; }

        public static IReadOnlyList<ChatArtifactDto> From(ChatSession session)
        {
            if (session == null) return From((IEnumerable<ChatArtifact>)null);
            var artifacts = (session.Artifacts ?? new List<ChatArtifact>())
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Id))
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() == 1)
                .Select(group => group.Single())
                .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
            var result = From(artifacts.Values).ToList();
            foreach (var item in result)
            {
                ChatArtifact artifact;
                if (item != null && artifacts.TryGetValue(item.Id ?? string.Empty, out artifact))
                {
                    item.ResourceUri = ChatResourceUri.CreateArtifactRevisionUri(session, artifact);
                }
            }
            return result;
        }

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
        [JsonProperty("sessionRevision")]
        public long SessionRevision { get; set; }

        [JsonProperty("runViewState", NullValueHandling = NullValueHandling.Ignore)]
        public RunViewState RunViewState { get; set; }

        [JsonProperty("activeChatId")]
        public string ActiveChatId { get; set; }

        [JsonProperty("activeChatModel")]
        public string ActiveChatModel { get; set; }

        [JsonProperty("activeChatMode")]
        public string ActiveChatMode { get; set; }

        [JsonProperty("activeChatReasoning")]
        public bool ActiveChatReasoning { get; set; }

        [JsonProperty("chats")]
        public IReadOnlyList<ChatSessionSummary> Chats { get; set; }

        [JsonProperty("documents")]
        public IReadOnlyList<OpenOfficeDocumentDto> Documents { get; set; }

        [JsonProperty("context", NullValueHandling = NullValueHandling.Ignore)]
        public DocumentContext Context { get; set; }

        [JsonProperty("messages", NullValueHandling = NullValueHandling.Ignore)]
        public IReadOnlyList<ChatMessage> Messages { get; set; }

        [JsonProperty("artifacts", NullValueHandling = NullValueHandling.Ignore)]
        public IReadOnlyList<ChatArtifactDto> Artifacts { get; set; }

        [JsonProperty("artifactLibrary", NullValueHandling = NullValueHandling.Ignore)]
        public ArtifactLibraryProjectionDto ArtifactLibrary { get; set; }

        [JsonProperty("activeContextCheckpointId")]
        public string ActiveContextCheckpointId { get; set; }

        [JsonProperty("activeHtmlArtifactId")]
        public string ActiveHtmlArtifactId { get; set; }

        [JsonProperty("activeTaskListArtifactId")]
        public string ActiveTaskListArtifactId { get; set; }

        [JsonProperty("activePlanDocumentArtifactId")]
        public string ActivePlanDocumentArtifactId { get; set; }

        [JsonProperty("contextUsage", NullValueHandling = NullValueHandling.Ignore)]
        public object ContextUsage { get; set; }

        [JsonProperty("htmlWorkspace", NullValueHandling = NullValueHandling.Ignore)]
        public HtmlWorkspaceDto HtmlWorkspace { get; set; }
    }

    public sealed class InitResponse
    {
        [JsonProperty("sessionRevision")]
        public long SessionRevision { get; set; }

        [JsonProperty("runViewState", NullValueHandling = NullValueHandling.Ignore)]
        public RunViewState RunViewState { get; set; }

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
        public ToolLibraryResponse Tools { get; set; }

        [JsonProperty("toolsPath")]
        public string ToolsPath { get; set; }

        [JsonProperty("skills")]
        public SkillLibraryResponse Skills { get; set; }

        [JsonProperty("skillsPath")]
        public string SkillsPath { get; set; }

        [JsonProperty("context")]
        public DocumentContext Context { get; set; }

        [JsonProperty("messages")]
        public IReadOnlyList<ChatMessage> Messages { get; set; }

        [JsonProperty("artifacts")]
        public IReadOnlyList<ChatArtifactDto> Artifacts { get; set; }

        [JsonProperty("artifactLibrary")]
        public ArtifactLibraryProjectionDto ArtifactLibrary { get; set; }

        [JsonProperty("activeContextCheckpointId")]
        public string ActiveContextCheckpointId { get; set; }

        [JsonProperty("activeHtmlArtifactId")]
        public string ActiveHtmlArtifactId { get; set; }

        [JsonProperty("activeTaskListArtifactId")]
        public string ActiveTaskListArtifactId { get; set; }

        [JsonProperty("activePlanDocumentArtifactId")]
        public string ActivePlanDocumentArtifactId { get; set; }

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

        [JsonProperty("tools")]
        public ToolLibraryResponse Tools { get; set; }

        [JsonProperty("skills")]
        public SkillLibraryResponse Skills { get; set; }
    }

    public sealed class ChatResourceDraftResponse
    {
        [JsonProperty("resource")]
        public ChatAttachment Resource { get; set; }
    }
}
