using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Tools;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class PromptReadToolHandler : IToolHandler
    {
        internal static readonly ToolBinding Binding =
            new ToolBinding("prompts.read.v1");

        private readonly PromptSettingsService _service;

        internal PromptReadToolHandler(PromptSettingsService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        public Task<ToolHandlerResult> ExecuteAsync(
            ToolHandlerContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(PromptToolProjection.Project(
                _service.Read(context.Arguments)));
        }
    }

    internal sealed class PromptSaveToolHandler : IPreparableToolHandler
    {
        internal static readonly ToolBinding Binding =
            new ToolBinding("prompts.save.v1");

        private readonly PromptSettingsService _service;

        internal PromptSaveToolHandler(PromptSettingsService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        public Task<ToolPreparationResult> PrepareAsync(
            ToolHandlerContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var preparation = _service.PrepareSave(context.Arguments);
            var result = PromptToolProjection.Result(preparation.Outcome);
            return Task.FromResult(new ToolPreparationResult(
                result, preparation.Outcome.Status == PromptOutcomeStatus.Ok
                    ? preparation.PreparedStateJson : null));
        }

        public Task<ToolHandlerResult> ExecuteAsync(
            ToolHandlerContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(PromptToolProjection.Project(
                _service.Save(context.Arguments, context.PreparedStateJson,
                    context.MarkDispatchPossible)));
        }
    }

    internal static class PromptToolProjection
    {
        internal static ToolHandlerResult Project(PromptToolOutcome outcome)
        {
            return new ToolHandlerResult(Result(outcome), Effect(outcome.Effect));
        }

        internal static RuntimeResult Result(PromptToolOutcome outcome)
        {
            if (outcome == null)
                throw new InvalidOperationException(
                    "Prompt settings service returned no outcome.");
            var data = outcome.Status == PromptOutcomeStatus.Ok
                ? outcome.DataJson : ErrorData(outcome);
            if (outcome.Status == PromptOutcomeStatus.Ok)
                return RuntimeResult.Ok(outcome.Message, data);
            if (outcome.Status == PromptOutcomeStatus.Unknown)
                return RuntimeResult.Unknown(outcome.Message, data);
            return RuntimeResult.Error(outcome.Message, data);
        }

        private static ToolEffectEvidence Effect(PromptToolEffect effect)
        {
            switch (effect)
            {
                case PromptToolEffect.VerifiedNoChange:
                    return ToolEffectEvidence.VerifiedNoChange;
                case PromptToolEffect.VerifiedChange:
                    return ToolEffectEvidence.VerifiedChange;
                case PromptToolEffect.Unknown:
                    return ToolEffectEvidence.Unknown;
                default:
                    return ToolEffectEvidence.None;
            }
        }

        private static string ErrorData(PromptToolOutcome outcome)
        {
            JObject data;
            try
            {
                data = string.IsNullOrWhiteSpace(outcome.DataJson)
                    ? new JObject() : JObject.Parse(outcome.DataJson);
            }
            catch (JsonException)
            {
                data = new JObject { ["details"] = outcome.DataJson };
            }
            data["code"] = outcome.ErrorCode;
            data["retryable"] = outcome.Retryable;
            return data.ToString(Formatting.None);
        }
    }
}
