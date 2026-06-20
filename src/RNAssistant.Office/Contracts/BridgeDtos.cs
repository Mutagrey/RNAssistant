using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

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
        [JsonProperty("phase")]
        public string Phase { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("activity", NullValueHandling = NullValueHandling.Ignore)]
        public ChatActivity Activity { get; set; }
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

    public sealed class CreateChatPayload
    {
        [JsonProperty("title")]
        public string Title { get; set; }
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

    public sealed class SendChatPayload : ChatPayload
    {
        [JsonProperty("text")]
        public string Text { get; set; }
    }

    public sealed class CancelRequestPayload
    {
        [JsonProperty("requestId")]
        public string RequestId { get; set; }
    }

    public sealed class MessageActionPayload : ChatPayload
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("index")]
        public int? Index { get; set; }
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

    public sealed class SaveSkillsPayload
    {
        [JsonProperty("skills")]
        public List<SkillDefinition> Skills { get; set; }
    }

    public sealed class VbaProjectPayload
    {
        [JsonProperty("maxChars")]
        public int? MaxChars { get; set; }
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

    public sealed class VbaContextPayload : ChatPayload
    {
        [JsonProperty("maxChars")]
        public int? MaxChars { get; set; }
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

    public sealed class VbaProjectResponse
    {
        [JsonProperty("result")]
        public ToolResult Result { get; set; }

        [JsonProperty("backups")]
        public IReadOnlyList<VbaModuleBackup> Backups { get; set; }
    }

    public sealed class QuickActionResponse
    {
        [JsonProperty("prompt")]
        public string Prompt { get; set; }
    }

    public class ChatStateResponse
    {
        [JsonProperty("activeChatId")]
        public string ActiveChatId { get; set; }

        [JsonProperty("activeChatModel")]
        public string ActiveChatModel { get; set; }

        [JsonProperty("chats")]
        public IReadOnlyList<ChatSessionSummary> Chats { get; set; }

        [JsonProperty("context")]
        public DocumentContext Context { get; set; }

        [JsonProperty("messages")]
        public IReadOnlyList<ChatMessage> Messages { get; set; }

        [JsonProperty("contextUsage")]
        public object ContextUsage { get; set; }
    }

    public sealed class InitResponse
    {
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

        [JsonProperty("chats")]
        public IReadOnlyList<ChatSessionSummary> Chats { get; set; }

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

        [JsonProperty("contextUsage")]
        public object ContextUsage { get; set; }

        [JsonProperty("quickAction")]
        public string QuickAction { get; set; }
    }

    public sealed class SendChatResponse : ChatStateResponse
    {
        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("toolResults")]
        public IReadOnlyList<object> ToolResults { get; set; }
    }
}
