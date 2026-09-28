using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Services;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class HtmlWorkspaceToolHandler : IManagedMutationToolHandler
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
                    .Replace('_', '.') + ".v2");
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
                    if (_toolId == HtmlWorkspaceToolCatalog.WriteFileToolId)
                    {
                        var refusal = GuardExistingFile(context);
                        if (refusal != null)
                            return Task.FromResult(context.Complete(Project(refusal)));
                    }
                    var outcome = _service.Execute(
                        _toolId, context.Arguments, _session,
                        context.MarkDispatchPossible, cancellationToken);
                    return Task.FromResult(context.Complete(Project(outcome)));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        private HtmlWorkspaceToolOutcome GuardExistingFile(ToolHandlerContext context)
        {
            var accepted = (_session.Messages ?? new List<ChatMessage>()).SingleOrDefault(message =>
                message.AcceptedCallOrigin != null &&
                message.ToolCallId == context.Execution.Call.Id);
            if (accepted == null) return null; // Manual/editor writes have their own guard.
            if (accepted.RunId != context.Execution.RunId ||
                accepted.AcceptedCallOrigin.StepId != context.Execution.StepId)
                throw new InvalidOperationException("HTML source observation belongs to another accepted execution.");

            var path = HtmlWorkspaceToolService.NormalizeWorkspacePath(
                ToolArgumentReader.String(context.Arguments, "path", string.Empty));
            var file = (_session.HtmlWorkspace?.Files ?? new List<HtmlWorkspaceFile>())
                .SingleOrDefault(item => item != null &&
                    string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
            if (file == null) return null;
            var artifact = (_session.Artifacts ?? new List<ChatArtifact>())
                .SingleOrDefault(item => item != null && item.Id == _session.ActiveHtmlArtifactId);
            if (artifact == null)
                throw new InvalidOperationException("The current HTML artifact is unavailable.");
            var current = ChatHtmlResourceCatalog.FileReference(_session, artifact, file.Id);
            var hash = TextPatternEngine.Sha256(file.Content ?? string.Empty);
            var acceptedIndex = _session.Messages.IndexOf(accepted);
            var priorInputs = _session.Messages.Take(acceptedIndex + 1).Where(message =>
                message != null && message.Role == "assistant" &&
                message.AcceptedCallOrigin != null && message.RunId == accepted.RunId);
            // Accepted call evidence proves the complete source was actually shown
            // to the model. Keep that proof for this run even if a later request
            // compacts the read or another workspace member advances the root.
            foreach (var input in priorInputs)
            {
                var observations = (input.ResourceEvidence ?? new List<ResourceEvidence>())
                    .Where(evidence => MatchesCurrentFile(evidence, current, hash)).ToArray();
                if (HasCompleteSource(observations, (file.Content ?? string.Empty).Length)) return null;
            }

            return HtmlWorkspaceToolOutcome.Error(
                "The model has not seen complete source for the current HTML file contents in this run: " + path +
                ". Read its complete source with common.resources_find/read before replacing it, or use common.html_workspace_apply_patch for a focused change. A successful read omitted from model context does not authorize replacement.",
                null, "html_source_observation_required", false,
                new ToolRecoveryContract(ToolFailureKind.ConflictNoEffect,
                    ToolRetryPolicy.RefreshRequired, current.Identity,
                    ResourceRepresentations.Source, "HTML file: " + path));
        }

        private static bool MatchesCurrentFile(ResourceEvidence evidence, ResourceRef current, string hash)
        {
            if (evidence == null || evidence.Resource == null || evidence.Coverage == null ||
                evidence.View != ResourceRepresentations.Source ||
                !string.Equals(evidence.ContentSha256, hash, StringComparison.OrdinalIgnoreCase)) return false;
            ResourceAddress observedAddress;
            ResourceAddress currentAddress;
            if (!ResourceUri.TryParse(evidence.Resource.Uri, out observedAddress) ||
                !ResourceUri.TryParse(current.Uri, out currentAddress) ||
                observedAddress.Provider != currentAddress.Provider ||
                observedAddress.Segments.Count != 8 || currentAddress.Segments.Count != 8) return false;
            return observedAddress.Segments[0] == currentAddress.Segments[0] &&
                observedAddress.Segments[5] == "member" &&
                observedAddress.Segments[6] == "file" &&
                observedAddress.Segments[7] == currentAddress.Segments[7];
        }

        private static bool HasCompleteSource(IReadOnlyList<ResourceEvidence> evidence, int length)
        {
            if (evidence.Any(item => item.Complete &&
                item.Coverage.Kind == ResourceCoverageKinds.Whole)) return true;
            var chunks = evidence.Where(item => item.Coverage.Kind == ResourceCoverageKinds.CharacterRange &&
                    item.Coverage.Start.HasValue && item.Coverage.End.HasValue)
                .OrderBy(item => item.Coverage.Start.Value).ToArray();
            if (!chunks.Any(item => item.Complete && item.Coverage.End == length)) return false;
            long covered = 0;
            foreach (var chunk in chunks)
            {
                if (chunk.Coverage.Start < 0 || chunk.Coverage.End < chunk.Coverage.Start ||
                    chunk.Coverage.End > length || chunk.Coverage.Start > covered) return false;
                covered = Math.Max(covered, chunk.Coverage.End.Value);
            }
            return covered == length;
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
            return new ToolHandlerResult(result, Effect(outcome.Effect),
                recovery: outcome.Recovery);
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
