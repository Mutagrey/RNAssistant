using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Tools;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class SkillAuthoringToolHandler : IPreparableToolHandler
    {
        private readonly string _toolId;
        private readonly SkillAuthoringService _service;

        internal SkillAuthoringToolHandler(
            string toolId, SkillAuthoringService service)
        {
            if (!SkillAuthoringCatalog.Owns(toolId))
                throw new ArgumentException(
                    "An exact skill authoring id is required.",
                    nameof(toolId));
            _toolId = toolId;
            _service = service ?? throw new ArgumentNullException(
                nameof(service));
        }

        internal static ToolBinding BindingFor(string toolId)
        {
            if (string.Equals(toolId,
                SkillAuthoringCatalog.UpsertToolId,
                StringComparison.Ordinal))
                return new ToolBinding("skills.core-upsert.intent.v1");
            if (string.Equals(toolId,
                SkillAuthoringCatalog.DeleteToolId,
                StringComparison.Ordinal))
                return new ToolBinding("skills.core-delete.intent.v1");
            if (string.Equals(toolId,
                SkillAuthoringCatalog.ReferenceUpsertToolId,
                StringComparison.Ordinal))
                return new ToolBinding("skills.reference-upsert.intent.v1");
            if (string.Equals(toolId,
                SkillAuthoringCatalog.ReferenceDeleteToolId,
                StringComparison.Ordinal))
                return new ToolBinding("skills.reference-delete.intent.v1");
            return null;
        }

        public Task<ToolPreparationResult> PrepareAsync(
            ToolHandlerContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var preparation = _service.PrepareMutation(
                _toolId, context.Arguments);
            return Task.FromResult(new ToolPreparationResult(
                Result(preparation.Outcome),
                preparation.Outcome.Status ==
                    SkillAuthoringOutcomeStatus.Ok
                        ? preparation.PreparedStateJson : null));
        }

        public Task<ToolHandlerResult> ExecuteAsync(
            ToolHandlerContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = _service.ExecuteMutation(
                _toolId, context.Arguments,
                context.PreparedStateJson,
                context.MarkDispatchPossible);
            return Task.FromResult(context.Complete(new ToolHandlerResult(
                Result(outcome), Effect(outcome.Effect))));
        }

        private static RuntimeResult Result(SkillAuthoringOutcome outcome)
        {
            if (outcome == null)
                throw new InvalidOperationException(
                    "Skill authoring service returned no outcome.");
            var data = outcome.Status == SkillAuthoringOutcomeStatus.Ok
                ? outcome.DataJson : ErrorData(outcome);
            if (outcome.Status == SkillAuthoringOutcomeStatus.Ok)
                return RuntimeResult.Ok(outcome.Message, data);
            if (outcome.Status == SkillAuthoringOutcomeStatus.Unknown)
                return RuntimeResult.Unknown(outcome.Message, data);
            return RuntimeResult.Error(outcome.Message, data);
        }

        private static ToolEffectEvidence Effect(
            SkillAuthoringEffect effect)
        {
            switch (effect)
            {
                case SkillAuthoringEffect.VerifiedNoChange:
                    return ToolEffectEvidence.VerifiedNoChange;
                case SkillAuthoringEffect.VerifiedChange:
                    return ToolEffectEvidence.VerifiedChange;
                case SkillAuthoringEffect.Unknown:
                    return ToolEffectEvidence.Unknown;
                default:
                    return ToolEffectEvidence.None;
            }
        }

        private static string ErrorData(SkillAuthoringOutcome outcome)
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
            data["contractVersion"] =
                SkillAuthoringOutcome.CurrentContractVersion;
            data["code"] = outcome.ErrorCode;
            data["retryable"] = outcome.Retryable;
            return data.ToString(Formatting.None);
        }
    }
}
