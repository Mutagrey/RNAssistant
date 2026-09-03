using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Runtime;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class VbaToolHandler : IPreparableToolHandler
    {
        private readonly string _toolId;
        private readonly VbaToolExecutor _executor;
        private readonly HostRuntime _runtime;
        private readonly ChatSession _session;

        internal VbaToolHandler(string toolId, VbaToolExecutor executor,
            HostRuntime runtime, ChatSession session)
        {
            if (!VbaToolCatalog.Owns(toolId))
                throw new ArgumentException(
                    "An exact public VBA tool id is required.", nameof(toolId));
            _toolId = toolId;
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _session = session;
        }

        internal static ToolBinding BindingFor(string toolId)
        {
            if (string.Equals(toolId, VbaToolCatalog.RestoreBackup,
                StringComparison.Ordinal))
                return new ToolBinding("vba.public.vba.restore.backup.intent.v2");
            if (string.Equals(toolId, VbaToolCatalog.WriteModule,
                StringComparison.Ordinal))
                return new ToolBinding("vba.public.vba.write.module.intent.v2");
            if (string.Equals(toolId, VbaToolCatalog.RenameModule,
                StringComparison.Ordinal))
                return new ToolBinding("vba.public.vba.rename.module.intent.v1");
            if (string.Equals(toolId, VbaToolCatalog.ApplyPatch,
                StringComparison.Ordinal))
                return new ToolBinding("vba.public.vba.apply.patch.intent.v2");
            if (string.Equals(toolId, VbaToolCatalog.DeleteModule,
                StringComparison.Ordinal))
                return new ToolBinding("vba.public.vba.delete.module.intent.v2");
            if (string.Equals(toolId, VbaToolCatalog.RunMacro,
                StringComparison.Ordinal))
                return new ToolBinding("vba.public.office.run.macro.intent.v2");
            return null;
        }

        public Task<ToolPreparationResult> PrepareAsync(
            ToolHandlerContext context, CancellationToken cancellationToken)
        {
            try
            {
                var preparation = _runtime.ReadDocument(
                    Target(_session), cancellationToken, delegate
                    {
                        return _executor.PrepareNativeTool(
                            _toolId, context.Arguments, context.Execution,
                            _session == null ? string.Empty : _session.Id,
                            cancellationToken);
                    });
                if (preparation == null || preparation.Outcome == null)
                    throw new InvalidOperationException(
                        "VBA preparation returned no outcome.");
                return Task.FromResult(new ToolPreparationResult(
                    Result(preparation.Outcome), preparation.StateJson));
            }
            catch (OfficeDocumentGuardException ex)
            {
                return PreparationFailure(ex.Message, ex.ErrorCode, ex.Retryable);
            }
            catch (HostRuntime.MutationLockException ex)
            {
                return PreparationFailure(ex.Message,
                    ex.Retryable ? "tool_mutation_busy" :
                        "tool_mutation_lock_unavailable",
                    ex.Retryable);
            }
        }

        public Task<ToolHandlerResult> ExecuteAsync(
            ToolHandlerContext context, CancellationToken cancellationToken)
        {
            try
            {
                var outcome = _runtime.ExecuteDocumentMutation(
                    Target(_session), cancellationToken, delegate
                    {
                        return _executor.ExecuteNativeTool(
                            _toolId, context.Arguments, context.Execution,
                            _session == null ? string.Empty : _session.Id,
                            context.PreparedStateJson,
                            context.MarkDispatchPossible, cancellationToken);
                    });
                if (outcome == null)
                    throw new InvalidOperationException(
                        "VBA execution returned no outcome.");
                if (string.Equals(_toolId, VbaToolCatalog.RunMacro,
                    StringComparison.Ordinal) && context.MayHaveDispatched)
                {
                    return Task.FromResult(new ToolHandlerResult(
                        RuntimeResult.Unknown(outcome.Message, outcome.DataJson),
                        ToolEffectEvidence.Unknown));
                }
                return Task.FromResult(new ToolHandlerResult(
                    Result(outcome), Effect(outcome, context.MayHaveDispatched)));
            }
            catch (OfficeDocumentGuardException ex)
                when (!context.MayHaveDispatched)
            {
                return Failure(ex.Message, ex.ErrorCode, ex.Retryable);
            }
            catch (HostRuntime.MutationLockException ex)
                when (!context.MayHaveDispatched)
            {
                return Failure(ex.Message,
                    ex.Retryable ? "tool_mutation_busy" :
                        "tool_mutation_lock_unavailable",
                    ex.Retryable);
            }
        }

        private static RuntimeResult Result(VbaNativeOutcome outcome)
        {
            if (outcome.Status == VbaNativeOutcomeStatus.Ok)
                return RuntimeResult.Ok(outcome.Message, outcome.DataJson);
            if (outcome.Status == VbaNativeOutcomeStatus.Unknown)
                return RuntimeResult.Unknown(outcome.Message, outcome.DataJson);
            return RuntimeResult.Error(outcome.Message, outcome.DataJson);
        }

        private static ToolEffectEvidence Effect(
            VbaNativeOutcome outcome, bool dispatched)
        {
            if (outcome.Status == VbaNativeOutcomeStatus.Unknown)
                return ToolEffectEvidence.Unknown;
            if (outcome.Status == VbaNativeOutcomeStatus.Ok)
                return dispatched ? ToolEffectEvidence.VerifiedChange :
                    ToolEffectEvidence.VerifiedNoChange;
            return dispatched ? ToolEffectEvidence.VerifiedNoChange :
                ToolEffectEvidence.None;
        }

        private static OfficeDocumentExecutionExpectation Target(
            ChatSession session)
        {
            if (session == null) return null;
            return new OfficeDocumentExecutionExpectation
            {
                Host = session.Host,
                DocumentKey = session.DocumentKey,
                RuntimeDocumentKey = session.LastRun == null
                    ? string.Empty : session.LastRun.DocumentRuntimeKey
            };
        }

        private static Task<ToolPreparationResult> PreparationFailure(
            string message, string code, bool retryable)
        {
            return Task.FromResult(new ToolPreparationResult(
                RuntimeResult.Error(message,
                    JsonConvert.SerializeObject(new { code, retryable }))));
        }

        private static Task<ToolHandlerResult> Failure(
            string message, string code, bool retryable)
        {
            return Task.FromResult(new ToolHandlerResult(
                RuntimeResult.Error(message,
                    JsonConvert.SerializeObject(new { code, retryable })),
                ToolEffectEvidence.None));
        }
    }
}
