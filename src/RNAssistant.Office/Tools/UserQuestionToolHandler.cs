using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Tools;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class UserQuestionToolHandler : IToolHandler
    {
        internal static readonly ToolBinding Binding =
            new ToolBinding("conversation.questions.ask.v1");

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
                var data = new JObject
                {
                    ["type"] = "rnassistant.questions",
                    ["questionSetId"] = "questions_" +
                        Guid.NewGuid().ToString("N"),
                    ["questions"] = questions.DeepClone()
                }.ToString(Formatting.None);
                return Task.FromResult(new ToolHandlerResult(
                    RuntimeResult.Ok(
                        "Ответьте на ключевые вопросы плана.", data),
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
                    ToolEffectEvidence.None));
            }
        }
    }
}
