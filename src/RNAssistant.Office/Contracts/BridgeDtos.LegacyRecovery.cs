using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Contracts
{
    public sealed class LegacyRecoveryListRequest
    {
        [JsonProperty("offset")] public int Offset { get; set; }
        [JsonProperty("limit")] public int Limit { get; set; } = 25;
    }

    public sealed class LegacyTranscriptRequest
    {
        [JsonProperty("sourceId", Required = Required.Always)] public string SourceId { get; set; }
        [JsonProperty("expectedRevision", Required = Required.Always)] public long ExpectedRevision { get; set; }
        [JsonProperty("messageIndex")] public int MessageIndex { get; set; }
        [JsonProperty("characterOffset")] public int CharacterOffset { get; set; }
        [JsonProperty("maxCharacters")] public int MaxCharacters { get; set; } = 32000;
    }

    public sealed class LegacyPluginReconnectRequest
    {
        [JsonProperty("pluginId", Required = Required.Always)] public string PluginId { get; set; }
    }

    public sealed class LegacyChatImportRequest
    {
        [JsonProperty("sourceId", Required = Required.Always)] public string SourceId { get; set; }
        [JsonProperty("expectedRevision", Required = Required.Always)] public long ExpectedRevision { get; set; }
    }

    public sealed class LegacyChatImportReadRequest
    {
        [JsonProperty("chatId", Required = Required.Always)] public string ChatId { get; set; }
        [JsonProperty("sourceId", Required = Required.Always)] public string SourceId { get; set; }
        [JsonProperty("expectedRevision", Required = Required.Always)] public long ExpectedRevision { get; set; }
    }

    public sealed class LegacyChatImportResultDto
    {
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("sourceId")] public string SourceId { get; set; }
        [JsonProperty("sourceRevision")] public long SourceRevision { get; set; }
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("host")] public string Host { get; set; }
        [JsonProperty("documentKey")] public string DocumentKey { get; set; }
        [JsonProperty("workspaceId")] public string WorkspaceId { get; set; }
        [JsonProperty("documentAuthorityId")] public string DocumentAuthorityId { get; set; }
        [JsonProperty("messageCount")] public int MessageCount { get; set; }
        [JsonProperty("revision")] public long Revision { get; set; }
        [JsonProperty("targetDeleted")] public bool TargetDeleted { get; set; }

        internal static LegacyChatImportResultDto From(LegacyChatImportResult value)
        {
            return value == null ? null : new LegacyChatImportResultDto { Status = value.Status,
                SourceId = value.SourceId, SourceRevision = value.SourceRevision, ChatId = value.ChatId,
                Host = value.Host, DocumentKey = value.DocumentKey, WorkspaceId = value.WorkspaceId,
                DocumentAuthorityId = value.DocumentAuthorityId, MessageCount = value.MessageCount, Revision = value.Revision,
                TargetDeleted = value.TargetDeleted };
        }
    }

    public sealed class LegacyChatRecoveryItemDto
    {
        [JsonProperty("sourceId")] public string SourceId { get; set; }
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("host")] public string Host { get; set; }
        [JsonProperty("documentTitle")] public string DocumentTitle { get; set; }
        [JsonProperty("documentPath")] public string DocumentPath { get; set; }
        [JsonProperty("revision")] public long Revision { get; set; }
        [JsonProperty("messageCount")] public int MessageCount { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("error")] public string Error { get; set; }

        internal static LegacyChatRecoveryItemDto From(LegacyChatRecoveryItem value)
        {
            return new LegacyChatRecoveryItemDto { SourceId = value.SourceId, ChatId = value.ChatId,
                Title = value.Title, Host = value.Host, DocumentTitle = value.DocumentTitle,
                DocumentPath = value.DocumentPath, Revision = value.Revision, MessageCount = value.MessageCount,
                Status = value.Status, Error = value.Error };
        }
    }

    public sealed class LegacyChatRecoveryPageDto
    {
        [JsonProperty("rootPath")] public string RootPath { get; set; }
        [JsonProperty("total")] public int Total { get; set; }
        [JsonProperty("nextOffset")] public int? NextOffset { get; set; }
        [JsonProperty("items")] public IReadOnlyList<LegacyChatRecoveryItemDto> Items { get; set; }

        internal static LegacyChatRecoveryPageDto From(LegacyChatRecoveryPage value)
        {
            return new LegacyChatRecoveryPageDto { RootPath = value.RootPath, Total = value.Total,
                NextOffset = value.NextOffset, Items = value.Items.Select(LegacyChatRecoveryItemDto.From).ToArray() };
        }
    }

    public sealed class LegacyChatTranscriptPageDto
    {
        [JsonProperty("sourceId")] public string SourceId { get; set; }
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("revision")] public long Revision { get; set; }
        [JsonProperty("messageStartIndex")] public int MessageStartIndex { get; set; }
        [JsonProperty("totalMessages")] public int TotalMessages { get; set; }
        [JsonProperty("nextMessageIndex")] public int? NextMessageIndex { get; set; }
        [JsonProperty("nextCharacterOffset")] public int NextCharacterOffset { get; set; }
        [JsonProperty("text")] public string Text { get; set; }

        internal static LegacyChatTranscriptPageDto From(LegacyChatTranscriptPage value)
        {
            return new LegacyChatTranscriptPageDto { SourceId = value.SourceId, ChatId = value.ChatId,
                Title = value.Title, Revision = value.Revision, MessageStartIndex = value.MessageStartIndex,
                TotalMessages = value.TotalMessages, NextMessageIndex = value.NextMessageIndex,
                NextCharacterOffset = value.NextCharacterOffset, Text = value.Text };
        }
    }

    public sealed class LegacyPluginRecoveryItemDto
    {
        [JsonProperty("pluginId")] public string PluginId { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("sourceRoot")] public string SourceRoot { get; set; }
        [JsonProperty("sourceWorkspaceId")] public string SourceWorkspaceId { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("error")] public string Error { get; set; }
        [JsonProperty("canConnect")] public bool CanConnect { get; set; }
        [JsonProperty("canImport")] public bool CanImport { get; set; }
        [JsonProperty("registrationRevision")] public string RegistrationRevision { get; set; }

        internal static LegacyPluginRecoveryItemDto From(LegacyPluginRecoveryItem value)
        {
            return new LegacyPluginRecoveryItemDto { PluginId = value.PluginId, Name = value.Name,
                SourceRoot = value.SourceRoot, SourceWorkspaceId = value.SourceWorkspaceId,
                Status = value.Status, Error = value.Error, CanConnect = value.CanConnect,
                CanImport = value.CanImport, RegistrationRevision = value.RegistrationRevision };
        }
    }

    public sealed class LegacyPluginRecoveryPageDto
    {
        [JsonProperty("rootPath")] public string RootPath { get; set; }
        [JsonProperty("total")] public int Total { get; set; }
        [JsonProperty("nextOffset")] public int? NextOffset { get; set; }
        [JsonProperty("items")] public IReadOnlyList<LegacyPluginRecoveryItemDto> Items { get; set; }

        internal static LegacyPluginRecoveryPageDto From(LegacyPluginRecoveryPage value)
        {
            return new LegacyPluginRecoveryPageDto { RootPath = value.RootPath, Total = value.Total,
                NextOffset = value.NextOffset, Items = value.Items.Select(LegacyPluginRecoveryItemDto.From).ToArray() };
        }
    }
}
