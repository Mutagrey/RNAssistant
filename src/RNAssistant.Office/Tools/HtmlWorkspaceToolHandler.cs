using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Runtime;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class HtmlWorkspaceToolHandler : IToolHandler
    {
        private readonly string _toolId;
        private readonly ChatSession _session;
        private readonly HtmlWorkspaceToolService _service;

        internal HtmlWorkspaceToolHandler(
            string toolId,
            ChatSession session,
            HtmlWorkspaceToolService service)
        {
            if (!HtmlWorkspaceToolCatalog.Owns(toolId))
                throw new ArgumentException(
                    "An exact HTML workspace tool id is required.",
                    nameof(toolId));
            _toolId = toolId;
            _session = session;
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        internal static ToolBinding BindingFor(string toolId)
        {
            if (!HtmlWorkspaceToolCatalog.Owns(toolId)) return null;
            return new ToolBinding(
                "html." + toolId.Substring("common.html_".Length)
                    .Replace('_', '.') + ".v1");
        }

        public Task<ToolHandlerResult> ExecuteAsync(
            ToolHandlerContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using (DocumentAccessGate.BeginOperation())
                {
                    var outcome = _service.Execute(
                        _toolId, context.Arguments, _session,
                        context.MarkDispatchPossible, cancellationToken);
                    return Task.FromResult(Project(outcome));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        private static ToolHandlerResult Project(
            HtmlWorkspaceToolOutcome outcome)
        {
            if (outcome == null)
                throw new InvalidOperationException(
                    "HTML workspace service returned no outcome.");
            var data = outcome.Status == HtmlWorkspaceOutcomeStatus.Ok
                ? outcome.DataJson
                : ErrorData(outcome);
            var resources = Resources(outcome.DataJson);
            RuntimeResult result;
            if (outcome.Status == HtmlWorkspaceOutcomeStatus.Ok)
                result = RuntimeResult.Ok(
                    outcome.Message, data, resources);
            else if (outcome.Status == HtmlWorkspaceOutcomeStatus.Unknown)
                result = RuntimeResult.Unknown(
                    outcome.Message, data, resources);
            else result = RuntimeResult.Error(
                outcome.Message, data, resources);
            return new ToolHandlerResult(result, Effect(outcome.Effect));
        }

        private static ToolEffectEvidence Effect(HtmlWorkspaceEffect effect)
        {
            switch (effect)
            {
                case HtmlWorkspaceEffect.VerifiedNoChange:
                    return ToolEffectEvidence.VerifiedNoChange;
                case HtmlWorkspaceEffect.VerifiedChange:
                    return ToolEffectEvidence.VerifiedChange;
                case HtmlWorkspaceEffect.Unknown:
                    return ToolEffectEvidence.Unknown;
                default:
                    return ToolEffectEvidence.None;
            }
        }

        private static string ErrorData(HtmlWorkspaceToolOutcome outcome)
        {
            JObject data = null;
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

        private static ResourceRef[] Resources(string dataJson)
        {
            try
            {
                var data = string.IsNullOrWhiteSpace(dataJson)
                    ? null : JObject.Parse(dataJson);
                if (data == null) return new ResourceRef[0];
                var result = new List<ResourceRef>();
                AddReference(result, data["artifactRef"] as JObject);
                foreach (var member in (data["members"] as JArray ??
                    new JArray()).OfType<JObject>())
                {
                    var uri = (string)member["uri"];
                    if (!string.IsNullOrWhiteSpace(uri))
                        result.Add(new ResourceRef(
                            uri, (string)member["revision"]));
                }
                return result
                    .GroupBy(item => item.Uri + "\n" +
                        (item.Revision ?? string.Empty), StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToArray();
            }
            catch (JsonException)
            {
                return new ResourceRef[0];
            }
        }

        private static void AddReference(
            ICollection<ResourceRef> target, JObject value)
        {
            if (target == null || value == null) return;
            var uri = (string)value["uri"];
            if (!string.IsNullOrWhiteSpace(uri))
                target.Add(new ResourceRef(uri, (string)value["revision"]));
        }
    }
}
