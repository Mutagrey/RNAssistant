using System.Collections.Generic;
using Newtonsoft.Json;

namespace RNAssistant.Core.Models
{
    // IDs belong to the runtime/UI exchange; model projections contain only
    // semantic questions, choices and answers.
    public sealed class UserQuestionSet
    {
        [JsonProperty("type")] public string Type { get; set; } = "rnassistant.questions";
        [JsonProperty("questionSetId")] public string QuestionSetId { get; set; }
        [JsonProperty("questions")] public List<UserQuestion> Questions { get; set; }
    }

    public sealed class UserQuestion
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("header")] public string Header { get; set; }
        [JsonProperty("prompt")] public string Prompt { get; set; }
        [JsonProperty("selection")] public string Selection { get; set; }
        [JsonProperty("allowFreeText")] public bool AllowFreeText { get; set; } = true;
        [JsonProperty("options")] public List<UserQuestionOption> Options { get; set; }
    }

    public sealed class UserQuestionOption
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("label")] public string Label { get; set; }
        [JsonProperty("description")] public string Description { get; set; }
        [JsonProperty("recommended")] public bool Recommended { get; set; }
    }

    public sealed class UserQuestionAnswerCommand
    {
        [JsonProperty("runId")] public string RunId { get; set; }
        [JsonProperty("questionSetId")] public string QuestionSetId { get; set; }
        [JsonProperty("answers")] public List<UserQuestionAnswer> Answers { get; set; }
    }

    public sealed class UserQuestionAnswer
    {
        [JsonProperty("questionId")] public string QuestionId { get; set; }
        [JsonProperty("optionIds")] public List<string> OptionIds { get; set; } = new List<string>();
        [JsonProperty("freeText")] public string FreeText { get; set; }
    }

    public sealed class UserQuestionAnswersProjection
    {
        [JsonProperty("answers")] public List<UserQuestionAnswerProjection> Answers { get; set; }
    }

    public sealed class UserQuestionAnswerProjection
    {
        [JsonProperty("question")] public string Question { get; set; }
        [JsonProperty("selections")] public List<string> Selections { get; set; }
        [JsonProperty("freeText")] public string FreeText { get; set; }
    }
}
