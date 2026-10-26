using System.Collections.Generic;
using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Contracts
{
    public sealed class ToolResultPresentationRequest : ChatPayload
    {
        [JsonProperty("runId")] public string RunId { get; set; }
        [JsonProperty("toolCallId")] public string ToolCallId { get; set; }
    }

    public sealed class ProjectToolResultPresentationRequest
    {
        [JsonProperty("workspaceId", Required = Required.Always)] public string WorkspaceId { get; set; }
        [JsonProperty("sessionId", Required = Required.Always)] public string SessionId { get; set; }
        [JsonProperty("runId", Required = Required.Always)] public string RunId { get; set; }
        [JsonProperty("toolCallId", Required = Required.Always)] public string ToolCallId { get; set; }
    }

    // Disposable UI blocks. They never become model results or execution evidence.
    public sealed class ToolResultPresentationDto
    {
        [JsonProperty("workspaceId", NullValueHandling = NullValueHandling.Ignore)] public string WorkspaceId { get; set; }
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("runId")] public string RunId { get; set; }
        [JsonProperty("toolCallId")] public string ToolCallId { get; set; }
        [JsonProperty("executionReference", NullValueHandling = NullValueHandling.Ignore)] public string ExecutionReference { get; set; }
        [JsonProperty("action", NullValueHandling = NullValueHandling.Ignore)] public string Action { get; set; }
        [JsonProperty("target", NullValueHandling = NullValueHandling.Ignore)] public string Target { get; set; }
        [JsonProperty("rawJson", NullValueHandling = NullValueHandling.Ignore)] public string RawJson { get; set; }
        [JsonProperty("blocks")] public List<ToolResultBlockDto> Blocks { get; set; } = new List<ToolResultBlockDto>();
        [JsonProperty("questions")] public UserQuestionSet Questions { get; set; }
        [JsonProperty("questionsUnavailableReason")] public string QuestionsUnavailableReason { get; set; }
    }

    public abstract class ToolResultBlockDto
    {
        [JsonProperty("kind")] public abstract string Kind { get; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("complete")] public bool Complete { get; set; } = true;
        [JsonProperty("note", NullValueHandling = NullValueHandling.Ignore)] public string Note { get; set; }
        [JsonProperty("source", NullValueHandling = NullValueHandling.Ignore)] public ToolSourcePreviewDto Source { get; set; }
    }
    // A bounded view of this retained result, not proof of delivery to a model.
    public sealed class ToolSourcePreviewDto
    {
        [JsonProperty("reference", NullValueHandling = NullValueHandling.Ignore)] public string Reference { get; set; }
        [JsonProperty("target")] public string Target { get; set; }
        [JsonProperty("view", NullValueHandling = NullValueHandling.Ignore)] public string View { get; set; }
        [JsonProperty("coverage", NullValueHandling = NullValueHandling.Ignore)] public ResourceCoverage Coverage { get; set; }
        [JsonProperty("line", NullValueHandling = NullValueHandling.Ignore)] public int? Line { get; set; }
        [JsonProperty("column", NullValueHandling = NullValueHandling.Ignore)] public int? Column { get; set; }
        [JsonProperty("pageIndex", NullValueHandling = NullValueHandling.Ignore)] public int? PageIndex { get; set; }
        [JsonProperty("excerpt", NullValueHandling = NullValueHandling.Ignore)] public string Excerpt { get; set; }
        [JsonProperty("excerptTruncated")] public bool ExcerptTruncated { get; set; }
    }
    public sealed class ToolTextBlockDto : ToolResultBlockDto
    {
        public override string Kind => "text";
        [JsonProperty("text")] public string Text { get; set; }
    }
    public sealed class ToolListBlockDto : ToolResultBlockDto
    {
        public override string Kind => "list";
        [JsonProperty("items")] public List<ToolListItemDto> Items { get; set; } = new List<ToolListItemDto>();
    }
    public sealed class ToolListItemDto
    {
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("detail")] public string Detail { get; set; }
        [JsonProperty("source", NullValueHandling = NullValueHandling.Ignore)] public ToolSourcePreviewDto Source { get; set; }
    }
    public sealed class ToolTableBlockDto : ToolResultBlockDto
    {
        public override string Kind => "table";
        [JsonProperty("columns")] public List<string> Columns { get; set; } = new List<string>();
        [JsonProperty("rows")] public List<List<string>> Rows { get; set; } = new List<List<string>>();
    }
    public sealed class ToolChangesBlockDto : ToolResultBlockDto
    {
        public override string Kind => "text_changes";
        [JsonProperty("changes")] public RunChangesDto Changes { get; set; }
    }
}
