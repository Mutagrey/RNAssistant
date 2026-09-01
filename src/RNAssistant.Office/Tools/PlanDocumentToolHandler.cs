using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Services;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class PlanDocumentToolHandler : IToolHandler
    {
        private readonly string _toolId;
        private readonly ChatSession _session;
        private readonly PlanDocumentService _service;

        internal PlanDocumentToolHandler(string toolId, ChatSession session)
        {
            if (!PlanDocumentToolCatalog.Owns(toolId))
                throw new ArgumentException(
                    "An exact Plan document tool id is required.", nameof(toolId));
            _toolId = toolId;
            _session = session;
            _service = new PlanDocumentService();
        }

        internal static ToolBinding BindingFor(string toolId)
        {
            if (string.Equals(toolId, PlanDocumentToolCatalog.CreateToolId,
                StringComparison.Ordinal))
                return new ToolBinding("conversation.plan.document.create.v1");
            if (string.Equals(toolId, PlanDocumentToolCatalog.UpdateToolId,
                StringComparison.Ordinal))
                return new ToolBinding("conversation.plan.document.update.v1");
            if (string.Equals(toolId, PlanDocumentToolCatalog.RestoreToolId,
                StringComparison.Ordinal))
                return new ToolBinding("conversation.plan.document.restore.v1");
            if (string.Equals(toolId, PlanDocumentToolCatalog.DeleteToolId,
                StringComparison.Ordinal))
                return new ToolBinding("conversation.plan.document.delete.v1");
            return null;
        }

        public Task<ToolHandlerResult> ExecuteAsync(
            ToolHandlerContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_session == null)
                return Failure("Plan document requires an active chat.",
                    "plan_session_required", false);
            try
            {
                using (DocumentAccessGate.BeginOperation())
                {
                    return Task.FromResult(Project(Execute(context), context));
                }
            }
            catch (InvalidOperationException ex) when (!context.MayHaveDispatched)
            {
                return Failure(ex.Message, "invalid_plan_document", true);
            }
        }

        private PlanDocumentMutation Execute(ToolHandlerContext context)
        {
            if (string.Equals(_toolId, PlanDocumentToolCatalog.CreateToolId,
                StringComparison.Ordinal))
            {
                return _service.Create(_session,
                    ToolArgumentReader.String(context.Arguments, "title", string.Empty),
                    ToolArgumentReader.String(context.Arguments, "markdown", string.Empty),
                    ToolArgumentReader.String(context.Arguments, "status", "draft"),
                    context.MarkDispatchPossible);
            }
            if (string.Equals(_toolId, PlanDocumentToolCatalog.UpdateToolId,
                StringComparison.Ordinal))
            {
                return _service.Update(_session,
                    ToolArgumentReader.String(context.Arguments, "id", string.Empty),
                    ToolArgumentReader.String(context.Arguments,
                        "expectedRevisionArtifactId", string.Empty),
                    ToolArgumentReader.String(context.Arguments, "title", string.Empty),
                    context.Arguments.ContainsKey("title"),
                    ToolArgumentReader.String(context.Arguments, "markdown", string.Empty),
                    ToolArgumentReader.String(context.Arguments, "status", "draft"),
                    context.MarkDispatchPossible);
            }
            if (string.Equals(_toolId, PlanDocumentToolCatalog.RestoreToolId,
                StringComparison.Ordinal))
            {
                return _service.Restore(_session,
                    ToolArgumentReader.String(context.Arguments, "id", string.Empty),
                    ToolArgumentReader.String(context.Arguments,
                        "expectedRevisionArtifactId", string.Empty),
                    ToolArgumentReader.String(context.Arguments,
                        "sourceRevisionArtifactId", string.Empty),
                    context.MarkDispatchPossible);
            }
            return _service.Delete(_session,
                ToolArgumentReader.String(context.Arguments, "id", string.Empty),
                ToolArgumentReader.String(context.Arguments,
                    "expectedRevisionArtifactId", string.Empty),
                context.MarkDispatchPossible);
        }

        private ToolHandlerResult Project(
            PlanDocumentMutation mutation, ToolHandlerContext context)
        {
            if (mutation == null)
                throw new InvalidOperationException(
                    "Plan document service returned no outcome.");
            if (!mutation.Success)
            {
                var failed = ErrorData(mutation.ErrorCode, mutation.Retryable);
                return context.MayHaveDispatched
                    ? new ToolHandlerResult(RuntimeResult.Unknown(
                        mutation.Message, failed), ToolEffectEvidence.Unknown)
                    : new ToolHandlerResult(RuntimeResult.Error(
                        mutation.Message, failed), ToolEffectEvidence.None);
            }
            if (!context.MayHaveDispatched)
                return new ToolHandlerResult(RuntimeResult.Error(
                    "Plan document mutation returned without a dispatch boundary.",
                    ErrorData("plan_dispatch_evidence_missing", false)),
                    ToolEffectEvidence.None);
            if (!Verified(mutation))
                return new ToolHandlerResult(RuntimeResult.Unknown(
                    "Plan document state could not be verified after mutation.",
                    ErrorData("plan_verification_failed", false)),
                    ToolEffectEvidence.Unknown);

            var payload = Payload(mutation.PlanId, mutation.Status,
                mutation.Artifact);
            if (!string.IsNullOrWhiteSpace(mutation.RestoredFromArtifactId))
                payload["restoredFromArtifactId"] =
                    mutation.RestoredFromArtifactId;
            if (mutation.Removed)
            {
                payload["removed"] = true;
                payload["removedRevisions"] = mutation.AffectedRevisions;
                payload["referencingMessageIds"] =
                    JArray.FromObject(mutation.ReferencingMessageIds);
            }
            return new ToolHandlerResult(RuntimeResult.Ok(
                mutation.Message, payload.ToString(Formatting.None)),
                ToolEffectEvidence.VerifiedChange);
        }

        private bool Verified(PlanDocumentMutation mutation)
        {
            var artifact = mutation.Artifact;
            if (artifact == null || !_session.Artifacts.Any(item =>
                object.ReferenceEquals(item, artifact))) return false;
            return mutation.Removed
                ? string.IsNullOrWhiteSpace(_session.ActivePlanDocumentArtifactId)
                : string.Equals(_session.ActivePlanDocumentArtifactId,
                    artifact.Id, StringComparison.Ordinal);
        }

        private static JObject Payload(
            string id, string status, ChatArtifact artifact)
        {
            return new JObject
            {
                ["planId"] = id,
                ["status"] = status,
                ["artifactId"] = artifact == null ? null : artifact.Id,
                ["revision"] = artifact == null ? 0 : artifact.Revision
            };
        }

        private static string ErrorData(string code, bool? retryable)
        {
            return JsonConvert.SerializeObject(new { code, retryable });
        }

        private static Task<ToolHandlerResult> Failure(
            string message, string code, bool retryable)
        {
            return Task.FromResult(new ToolHandlerResult(
                RuntimeResult.Error(message, ErrorData(code, retryable)),
                ToolEffectEvidence.None));
        }
    }
}
