using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Services;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Core.Tools
{
    public sealed class PlanDocumentToolHandler : IManagedMutationToolHandler
    {
        private readonly string _toolId;
        private readonly ChatSession _session;
        private readonly PlanDocumentService _service;

        public PlanDocumentToolHandler(string toolId, ChatSession session)
        {
            if (!PlanDocumentToolCatalog.Owns(toolId))
                throw new ArgumentException(
                    "An exact Plan document tool id is required.", nameof(toolId));
            _toolId = toolId;
            _session = session;
            _service = new PlanDocumentService();
        }

        public static ToolBinding BindingFor(string toolId)
        {
            if (string.Equals(toolId, PlanDocumentToolCatalog.SaveToolId,
                StringComparison.Ordinal))
                return new ToolBinding("document.plan.save.intent.v3");
            if (string.Equals(toolId, PlanDocumentToolCatalog.RestoreToolId,
                StringComparison.Ordinal))
                return new ToolBinding("document.plan.restore.intent.v3");
            if (string.Equals(toolId, PlanDocumentToolCatalog.DeleteToolId,
                StringComparison.Ordinal))
                return new ToolBinding("document.plan.delete.intent.v3");
            return null;
        }

        public Task<ToolHandlerResult> ExecuteAsync(
            ToolHandlerContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_session == null)
                return Rejected(
                    "Plan document requires an active chat.",
                    "plan_session_required");
            try
            {
                return Task.FromResult(context.Complete(Project(Execute(context), context)));
            }
            catch (InvalidOperationException ex) when (!context.MayHaveDispatched)
            {
                return Rejected(ex.Message,
                    "invalid_plan_document", ToolRetryPolicy.Replan, true);
            }
        }

        private static Task<ToolHandlerResult> Rejected(string message, string code,
            ToolRetryPolicy retryPolicy = ToolRetryPolicy.None, bool retryable = false)
        { return Task.FromResult(new ToolHandlerResult(RuntimeResult.Error(message, ErrorData(code, retryable)),
            ToolEffectEvidence.None, recovery: new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, retryPolicy))); }

        private PlanDocumentMutation Execute(ToolHandlerContext context)
        {
            var intent = PlanDocumentArguments.Read(context.Arguments);
            if (string.Equals(_toolId, PlanDocumentToolCatalog.SaveToolId,
                StringComparison.Ordinal))
            {
                return _service.Save(_session,
                    intent.Title,
                    intent.Markdown,
                    intent.Status,
                    context.MarkDispatchPossible, PlanDocumentService.CreationId(_session, context.Execution),
                    intent.StartNew);
            }
            if (string.Equals(_toolId, PlanDocumentToolCatalog.RestoreToolId,
                StringComparison.Ordinal))
            {
                return _service.RestoreVersion(_session,
                    intent.Version,
                    context.MarkDispatchPossible);
            }
            return _service.DeleteActive(_session, context.MarkDispatchPossible);
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
                        mutation.Message, failed), ToolEffectEvidence.None,
                        recovery: new ToolRecoveryContract(
                            ToolFailureKind.RejectedNoEffect,
                            ToolRetryPolicy.Replan));
            }
            if (!context.MayHaveDispatched)
                return new ToolHandlerResult(RuntimeResult.Error(
                    "Plan document mutation returned without a dispatch boundary.",
                    ErrorData("plan_dispatch_evidence_missing", false)),
                    ToolEffectEvidence.None,
                    recovery: new ToolRecoveryContract(
                        ToolFailureKind.ToolDefect, ToolRetryPolicy.None));
            if (!Verified(mutation))
                return new ToolHandlerResult(RuntimeResult.Unknown(
                    "Plan document state could not be verified after mutation.",
                    ErrorData("plan_verification_failed", false)),
                    ToolEffectEvidence.Unknown);

            mutation.Artifact.RunId = context.Execution.RunId;
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

    }
    internal sealed class PlanDocumentArguments
    {
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("markdown")] public string Markdown { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("startNew", NullValueHandling = NullValueHandling.Ignore)] public bool StartNew { get; set; }
        [JsonProperty("version")] public int Version { get; set; }
        internal static PlanDocumentArguments Read(System.Collections.Generic.IDictionary<string, object> arguments)
        { return JObject.FromObject(arguments).ToObject<PlanDocumentArguments>(); }
    }
}
