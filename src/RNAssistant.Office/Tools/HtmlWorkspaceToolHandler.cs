using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Services;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class HtmlWorkspaceToolHandler : IReadOnlyToolHandler, IManagedMutationToolHandler, IPreparableToolHandler
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
                "html." + toolId.Substring("html.".Length)
                    .Replace('_', '.') + ".v3");
        }

        public Task<ToolPreparationResult> PrepareAsync(ToolHandlerContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var command = _service.PrepareCommand(_session, _toolId, context.Arguments);
                return Task.FromResult(new ToolPreparationResult(RuntimeResult.Ok("HTML operation prepared."),
                    command == null ? "{}" : JsonConvert.SerializeObject(command)));
            }
            catch (Exception ex) when (ex is ResourceRequestException || ex is System.IO.InvalidDataException || ex is InvalidOperationException)
            {
                return Task.FromResult(new ToolPreparationResult(RuntimeResult.Error(ex.Message), recovery:
                    new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.Replan)));
            }
        }

        public Task<ToolHandlerResult> ExecuteAsync(
            ToolHandlerContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (_toolId == HtmlWorkspaceToolCatalog.ListAssetsToolId)
                {
                    var listed = _service.Execute(_toolId, context.Arguments,
                        _session, context.MarkDispatchPossible, cancellationToken, null);
                    return Task.FromResult(context.Complete(Project(listed)));
                }
                using (DocumentAccessGate.BeginOperation())
                {
                    var command = HtmlWorkspaceCommand.Read(context.PreparedStateJson);
                    if (_toolId == HtmlWorkspaceToolCatalog.WriteFileToolId)
                    {
                        var refusal = GuardExistingFile(context, command);
                        if (refusal != null)
                            return Task.FromResult(context.Complete(Project(refusal)));
                    }
                    var outcome = _service.Execute(
                        _toolId, context.Arguments, _session,
                        context.MarkDispatchPossible, cancellationToken, command);
                    return Task.FromResult(context.Complete(Project(outcome)));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        private HtmlWorkspaceToolOutcome GuardExistingFile(ToolHandlerContext context, HtmlWorkspaceCommand command)
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
            var mutation = _service.Load(_session, command);
            var file = mutation.Workspace.Files
                .SingleOrDefault(item => item != null &&
                    string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
            if (file == null) return null;
            var artifact = mutation.Snapshot;
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
                ". Read its complete source with core.find/read before replacing it, or use core.patch for a focused change. A successful read omitted from model context does not authorize replacement.",
                null, "html_source_observation_required", false,
                new ToolRecoveryContract(ToolFailureKind.ConflictNoEffect,
                    ToolRetryPolicy.RefreshRequired, current.Identity,
                    ResourceRepresentations.Source, HtmlWorkspaceTargets.Member(ChatHtmlResourceCatalog.FileKind, file.Path, file.CreatedUtc, _service.WorkspaceTarget(_session, current))));
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
                HtmlWorkspaceIdentity.LogicalId(observedAddress.Segments[2]) == HtmlWorkspaceIdentity.LogicalId(currentAddress.Segments[2]) &&
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
            if (outcome.Status != HtmlWorkspaceOutcomeStatus.Ok)
            {
                outcome.Data["code"] = outcome.ErrorCode;
                outcome.Data["retryable"] = outcome.Retryable;
            }
            var data = outcome.Data.ToString(Formatting.None);
            RuntimeResult result;
            if (outcome.Status == HtmlWorkspaceOutcomeStatus.Ok)
                result = RuntimeResult.Ok(
                    outcome.Message, data, outcome.Resources);
            else if (outcome.Status == HtmlWorkspaceOutcomeStatus.Unknown)
                result = RuntimeResult.Unknown(
                    outcome.Message, data, outcome.Resources);
            else result = RuntimeResult.Error(
                outcome.Message, data, outcome.Resources);
            return new ToolHandlerResult(result, Effect(outcome.Effect),
                resourceReadBack: outcome.ReadBack,
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

    }
}
