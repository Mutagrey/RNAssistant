using System.Collections.Generic;
using Newtonsoft.Json;

namespace RNAssistant.Office.Contracts
{
    public sealed class RunChangesRequest : ChatPayload
    {
        [JsonProperty("runId")] public string RunId { get; set; }
    }

    public sealed class RunChangesDto
    {
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("runId")] public string RunId { get; set; }
        [JsonProperty("complete")] public bool Complete { get; set; } = true;
        [JsonProperty("items")] public List<RunTextChangeDto> Items { get; set; } = new List<RunTextChangeDto>();
    }

    // Disposable presentation of retained source pairs, never execution authority.
    public sealed class RunTextChangeDto
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("scope")] public string Scope { get; set; }
        [JsonProperty("beforeTitle")] public string BeforeTitle { get; set; }
        [JsonProperty("beforeExists")] public bool BeforeExists { get; set; }
        [JsonProperty("afterExists")] public bool AfterExists { get; set; }
        [JsonProperty("before")] public string Before { get; set; }
        [JsonProperty("after")] public string After { get; set; }
        // Planned source remains separate from the verified After field.
        [JsonProperty("intendedAfter")] public string IntendedAfter { get; set; }
        [JsonProperty("intendedAfterExists")] public bool? IntendedAfterExists { get; set; }
        [JsonProperty("availability")] public string Availability { get; set; }
    }
}
