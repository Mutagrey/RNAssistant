using System.Collections.Generic;
using Newtonsoft.Json;

namespace RNAssistant.Office.Contracts
{
    public sealed class ToolResultPresentationRequest : ChatPayload
    {
        [JsonProperty("runId")] public string RunId { get; set; }
        [JsonProperty("toolCallId")] public string ToolCallId { get; set; }
    }

    // Disposable UI blocks. They never become model results or execution evidence.
    public sealed class ToolResultPresentationDto
    {
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("runId")] public string RunId { get; set; }
        [JsonProperty("toolCallId")] public string ToolCallId { get; set; }
        [JsonProperty("blocks")] public List<ToolResultBlockDto> Blocks { get; set; } = new List<ToolResultBlockDto>();
    }

    public abstract class ToolResultBlockDto
    {
        [JsonProperty("kind")] public abstract string Kind { get; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("complete")] public bool Complete { get; set; } = true;
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
