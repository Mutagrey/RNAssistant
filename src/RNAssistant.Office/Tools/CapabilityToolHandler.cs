using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class CapabilityToolHandler : IToolHandler
    {
        private readonly string _toolId;
        private readonly CapabilityCatalogService _service;
        private readonly IReadOnlyList<ToolCatalogEntry> _catalog;
        private readonly IReadOnlyList<SkillDefinition> _skills;
        private readonly bool _manualRun;

        internal CapabilityToolHandler(
            string toolId,
            CapabilityCatalogService service,
            IReadOnlyList<ToolCatalogEntry> catalog,
            IReadOnlyList<SkillDefinition> skills,
            bool manualRun)
        {
            if (!CapabilityToolCatalog.Owns(toolId))
                throw new ArgumentException(
                    "An exact capability tool id is required.", nameof(toolId));
            _toolId = toolId;
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _catalog = catalog ?? new ToolCatalogEntry[0];
            _skills = skills;
            _manualRun = manualRun;
        }

        internal static ToolBinding BindingFor(string toolId)
        {
            if (string.Equals(toolId, CapabilityToolCatalog.SearchToolId,
                StringComparison.Ordinal))
                return new ToolBinding("capabilities.search.v1");
            if (string.Equals(toolId, CapabilityToolCatalog.ReadToolId,
                StringComparison.Ordinal))
                return new ToolBinding("capabilities.read.v1");
            return null;
        }

        public Task<ToolHandlerResult> ExecuteAsync(
            ToolHandlerContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = _service.Execute(
                _toolId, context.Arguments, _catalog, _skills, _manualRun);
            if (outcome == null)
                throw new InvalidOperationException(
                    "Capability service returned no outcome.");
            var result = outcome.Status == CapabilityOutcomeStatus.Ok
                ? RuntimeResult.Ok(outcome.Message, outcome.DataJson)
                : RuntimeResult.Error(outcome.Message, ErrorData(outcome));
            return Task.FromResult(new ToolHandlerResult(
                result, ToolEffectEvidence.None));
        }

        private static string ErrorData(CapabilityToolOutcome outcome)
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
