using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    // Pure admission/projection. The application owner checks the current run
    // under its session lease, then appends this ordinary accepted user fact.
    public static class UserQuestionAnswers
    {
        public static string Accept(UserQuestionSet questions, UserQuestionAnswerCommand command)
        {
            if (questions == null || questions.Type != "rnassistant.questions" ||
                string.IsNullOrWhiteSpace(questions.QuestionSetId) ||
                command == null || command.QuestionSetId != questions.QuestionSetId)
                throw new ArgumentException("The question set is missing, stale or already answered.");
            if (questions.Questions == null || questions.Questions.Count < 1 || questions.Questions.Count > 3 ||
                command.Answers == null || command.Answers.Count != questions.Questions.Count ||
                command.Answers.Any(answer => answer == null || string.IsNullOrWhiteSpace(answer.QuestionId)) ||
                command.Answers.Select(answer => answer.QuestionId).Distinct(StringComparer.Ordinal).Count() != command.Answers.Count)
                throw new ArgumentException("Provide exactly one answer to each question.");
            var projection = new UserQuestionAnswersProjection { Answers = new List<UserQuestionAnswerProjection>() };
            foreach (var question in questions.Questions)
            {
                var answer = command.Answers.SingleOrDefault(item => item.QuestionId == question.Id);
                if (answer == null) throw new ArgumentException("Unknown or missing question ID.");
                var selected = answer.OptionIds ?? new List<string>();
                var text = (answer.FreeText ?? string.Empty).Trim();
                if (selected.Count > 4 || selected.Distinct(StringComparer.Ordinal).Count() != selected.Count ||
                    selected.Any(id => !question.Options.Any(option => option.Id == id)) ||
                    question.Selection == "single" && selected.Count > 1 ||
                    !question.AllowFreeText && text.Length > 0 || text.Length > 4000 ||
                    selected.Count == 0 && text.Length == 0)
                    throw new ArgumentException("Invalid choices or free text for question: " + question.Header);
                projection.Answers.Add(new UserQuestionAnswerProjection
                {
                    Question = question.Prompt,
                    Selections = question.Options.Where(option => selected.Contains(option.Id)).Select(option => option.Label).ToList(),
                    FreeText = text
                });
            }
            return "PLAN_ANSWERS:\n" + JsonConvert.SerializeObject(projection);
        }
    }
}
