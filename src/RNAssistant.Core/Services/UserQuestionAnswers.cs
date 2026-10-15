using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Agent;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;

namespace RNAssistant.Core.Services
{
    // Shared admission/projection from the accepted question fact. The application
    // owner holds its session lease, then appends an ordinary accepted user fact.
    public static class UserQuestionAnswers
    {
        public static PendingUserQuestions Pending(ChatSession session, ChatBlobStore payloads)
        {
            var run = session?.LastRun;
            var summary = run?.KernelState?.Summary;
            if (summary?.Lifecycle != RunLifecycle.Completed || summary.Reason != "awaiting_user") return null;
            var fact = session.Messages.LastOrDefault(item => item.RunId == run.RunId &&
                item.ProtocolMessage && !string.IsNullOrWhiteSpace(item.ToolCallId));
            if (fact?.ToolName != UserQuestionToolCatalog.AskToolId)
                throw new InvalidOperationException("The waiting run has no accepted question result.");
            if (fact.ResultPayload != null)
            {
                if (payloads == null || fact.ResultPayload.ByteLength > 1024 * 1024)
                    throw new InvalidOperationException("The accepted question payload is unavailable or too large.");
                fact = JsonConvert.DeserializeObject<ChatMessage>(JsonConvert.SerializeObject(fact));
                fact.Content = payloads.ReadText(fact.ResultPayload.ToBlobReference()) ??
                    throw new InvalidOperationException("The accepted question payload is missing.");
            }
            ToolResultWireReadResult wire; string error;
            if (!ToolResultHistoryReader.TryRead(fact, out wire, out error) || wire.Result.Status != ToolResultStatus.Ok)
                throw new InvalidOperationException("The accepted question result is invalid: " + error);
            var questions = JsonConvert.DeserializeObject<UserQuestionSet>(wire.Result.DataJson);
            if (questions?.Type != "rnassistant.questions" || string.IsNullOrWhiteSpace(questions.QuestionSetId) ||
                questions.Questions == null || questions.Questions.Count < 1 || questions.Questions.Count > 3)
                throw new InvalidOperationException("The accepted questions are missing.");
            return new PendingUserQuestions { RunId = run.RunId, QuestionSet = questions };
        }

        public static string Accept(ChatSession session, UserQuestionAnswerCommand command, ChatBlobStore payloads)
        {
            var pending = Pending(session, payloads);
            if (pending == null || command == null || pending.RunId != command.RunId)
                throw new ArgumentException("The question run is stale, missing or already answered.");
            return Accept(pending.QuestionSet, command);
        }

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
