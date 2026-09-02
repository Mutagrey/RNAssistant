using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Tools;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class ToolAuthoringReadToolHandler : IToolHandler
    {
        private readonly string _toolId;
        private readonly ToolAuthoringService _service;

        internal ToolAuthoringReadToolHandler(
            string toolId, ToolAuthoringService service)
        {
            if (!string.Equals(toolId,
                    ToolAuthoringCatalog.DefinitionReadToolId,
                    StringComparison.Ordinal))
                throw new ArgumentException(
                    "A read-only tool authoring id is required.",
                    nameof(toolId));
            _toolId = toolId;
            _service = service ?? throw new ArgumentNullException(
                nameof(service));
        }

        internal static ToolBinding BindingFor(string toolId)
        {
            if (string.Equals(toolId,
                ToolAuthoringCatalog.DefinitionReadToolId,
                StringComparison.Ordinal))
                return new ToolBinding("tools.definition-read.exact.v1");
            return null;
        }

        public Task<ToolHandlerResult> ExecuteAsync(
            ToolHandlerContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = _service.Read(context.Arguments);
            return Task.FromResult(
                ToolAuthoringToolProjection.Project(outcome));
        }
    }

    internal sealed class ToolAuthoringMutationToolHandler :
        IPreparableToolHandler
    {
        private readonly string _toolId;
        private readonly ToolAuthoringService _service;

        internal ToolAuthoringMutationToolHandler(
            string toolId, ToolAuthoringService service)
        {
            if (!ToolAuthoringCatalog.IsMutation(toolId))
                throw new ArgumentException(
                    "A tool authoring mutation id is required.",
                    nameof(toolId));
            _toolId = toolId;
            _service = service ?? throw new ArgumentNullException(
                nameof(service));
        }

        internal static ToolBinding BindingFor(string toolId)
        {
            if (string.Equals(toolId,
                ToolAuthoringCatalog.UpsertToolId,
                StringComparison.Ordinal))
                return new ToolBinding("tools.upsert.intent.v1");
            if (string.Equals(toolId,
                ToolAuthoringCatalog.DeleteToolId,
                StringComparison.Ordinal))
                return new ToolBinding("tools.delete.v1");
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
                ToolAuthoringToolProjection.Result(preparation.Outcome),
                preparation.Outcome.Status == ToolAuthoringOutcomeStatus.Ok
                    ? preparation.PreparedStateJson : null));
        }

        public Task<ToolHandlerResult> ExecuteAsync(
            ToolHandlerContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ToolAuthoringToolProjection.Project(
                _service.ExecuteMutation(
                    _toolId, context.Arguments,
                    context.PreparedStateJson,
                    context.MarkDispatchPossible)));
        }
    }

    internal static class ToolAuthoringToolProjection
    {
        internal static ToolHandlerResult Project(
            ToolAuthoringOutcome outcome)
        {
            return new ToolHandlerResult(
                Result(outcome), Effect(outcome.Effect));
        }

        internal static RuntimeResult Result(ToolAuthoringOutcome outcome)
        {
            if (outcome == null)
                throw new InvalidOperationException(
                    "Tool authoring service returned no outcome.");
            var data = outcome.Status == ToolAuthoringOutcomeStatus.Ok
                ? outcome.DataJson : ErrorData(outcome);
            if (outcome.Status == ToolAuthoringOutcomeStatus.Ok)
                return RuntimeResult.Ok(outcome.Message, data);
            if (outcome.Status == ToolAuthoringOutcomeStatus.Unknown)
                return RuntimeResult.Unknown(outcome.Message, data);
            return RuntimeResult.Error(outcome.Message, data);
        }

        private static ToolEffectEvidence Effect(
            ToolAuthoringEffect effect)
        {
            switch (effect)
            {
                case ToolAuthoringEffect.VerifiedNoChange:
                    return ToolEffectEvidence.VerifiedNoChange;
                case ToolAuthoringEffect.VerifiedChange:
                    return ToolEffectEvidence.VerifiedChange;
                case ToolAuthoringEffect.Unknown:
                    return ToolEffectEvidence.Unknown;
                default:
                    return ToolEffectEvidence.None;
            }
        }

        private static string ErrorData(ToolAuthoringOutcome outcome)
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
