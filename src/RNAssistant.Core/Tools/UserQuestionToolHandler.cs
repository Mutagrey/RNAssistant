using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Core.Tools
{
    public sealed class UserQuestionToolHandler : IReadOnlyToolHandler
    {
        public static readonly ToolBinding Binding =
            new ToolBinding("conversation.questions.ask.intent.v2");

        public Task<ToolHandlerResult> ExecuteAsync(
            ToolHandlerContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                object raw;
                var questions = context.Arguments.TryGetValue(
                        "questions", out raw)
                    ? raw as JArray
                    : null;
                if (questions == null)
                    throw new InvalidOperationException(
                        "questions must be a native JSON array.");
                UserQuestionToolCatalog.Validate(questions);
                var projectedQuestions = questions.ToObject<System.Collections.Generic.List<UserQuestion>>();
                foreach (var question in projectedQuestions)
                {
                    question.Id = "question_" + Guid.NewGuid().ToString("N");
                    foreach (var option in question.Options)
                        option.Id = "option_" + Guid.NewGuid().ToString("N");
                }
                var data = JsonConvert.SerializeObject(new UserQuestionSet
                { QuestionSetId = "questions_" + Guid.NewGuid().ToString("N"), Questions = projectedQuestions });
                return Task.FromResult(new ToolHandlerResult(
                    RuntimeResult.Ok(
                        "Ответьте на ключевые вопросы.", data),
                    ToolEffectEvidence.None,
                    awaitingUser: true));
            }
            catch (InvalidOperationException ex)
            {
                return Task.FromResult(new ToolHandlerResult(
                    RuntimeResult.Error(ex.Message, new JObject
                    {
                        ["code"] = "invalid_questions",
                        ["retryable"] = true
                    }.ToString(Formatting.None)),
                    ToolEffectEvidence.None,
                    recovery: new ToolRecoveryContract(
                        ToolFailureKind.RejectedNoEffect,
                        ToolRetryPolicy.Replan)));
            }
        }

    }
}
