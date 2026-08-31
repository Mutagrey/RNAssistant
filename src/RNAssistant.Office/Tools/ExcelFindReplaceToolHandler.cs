using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.Office.Runtime;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class ExcelFindReplaceToolHandler : IToolHandler
    {
        internal static readonly ToolBinding FindBinding =
            new ToolBinding("excel.find.cells.v1");
        internal static readonly ToolBinding ReplaceBinding =
            new ToolBinding("excel.replace.cells.v1");

        private readonly string _toolId;
        private readonly ExcelFindReplaceToolAdapter _adapter;
        private readonly HostRuntime _runtime;
        private readonly ChatSession _session;

        internal ExcelFindReplaceToolHandler(
            string toolId,
            ExcelFindReplaceToolAdapter adapter,
            HostRuntime runtime,
            ChatSession session)
        {
            if (!ExcelFindReplaceToolIds.Owns(toolId))
                throw new ArgumentException(
                    "An exact Excel find/replace tool id is required.", nameof(toolId));
            _toolId = toolId;
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _session = session;
        }

        internal static ToolBinding BindingFor(string toolId)
        {
            return string.Equals(
                toolId, ExcelFindReplaceToolIds.FindCells, StringComparison.Ordinal)
                ? FindBinding : ReplaceBinding;
        }

        public Task<ToolHandlerResult> ExecuteAsync(
            ToolHandlerContext context,
            CancellationToken cancellationToken)
        {
            if (_session == null)
                return Failure(
                    "Excel find/replace requires an active chat session.",
                    "excel_find_replace_session_required", false);
            return string.Equals(
                _toolId, ExcelFindReplaceToolIds.FindCells, StringComparison.Ordinal)
                ? Find(context, cancellationToken)
                : Replace(context, cancellationToken);
        }

        private Task<ToolHandlerResult> Find(
            ToolHandlerContext context,
            CancellationToken cancellationToken)
        {
            try
            {
                var outcome = _runtime.ReadDocument(Target(_session), cancellationToken, delegate
                {
                    context.MarkDispatchPossible();
                    return _adapter.Find(context.Arguments, cancellationToken);
                });
                var result = outcome.Success
                    ? RuntimeResult.Ok(outcome.Message, outcome.DataJson)
                    : RuntimeResult.Error(outcome.Message, outcome.DataJson);
                return Task.FromResult(
                    new ToolHandlerResult(result, ToolEffectEvidence.None));
            }
            catch (OfficeDocumentGuardException ex)
            {
                return Failure(ex.Message, ex.ErrorCode, ex.Retryable);
            }
            catch (HostRuntime.MutationLockException ex)
            {
                return Failure(ex.Message,
                    ex.Retryable ? "tool_mutation_busy" : "tool_mutation_lock_unavailable",
                    ex.Retryable);
            }
        }

        private Task<ToolHandlerResult> Replace(
            ToolHandlerContext context,
            CancellationToken cancellationToken)
        {
            try
            {
                var outcome = _runtime.ExecuteDocumentMutation(
                    Target(_session), cancellationToken, delegate
                    {
                        return _adapter.Replace(
                            context.Arguments,
                            context.MarkDispatchPossible,
                            cancellationToken);
                    });
                RuntimeResult result;
                if (outcome.Status == ExcelReplaceOutcomeStatus.Ok)
                    result = RuntimeResult.Ok(outcome.Message, outcome.DataJson);
                else if (outcome.Status == ExcelReplaceOutcomeStatus.Unknown)
                    result = RuntimeResult.Unknown(outcome.Message, outcome.DataJson);
                else result = RuntimeResult.Error(outcome.Message, outcome.DataJson);
                return Task.FromResult(new ToolHandlerResult(result, Effect(outcome.Effect)));
            }
            catch (OfficeDocumentGuardException ex) when (!context.MayHaveDispatched)
            {
                return Failure(ex.Message, ex.ErrorCode, ex.Retryable);
            }
            catch (HostRuntime.MutationLockException ex) when (!context.MayHaveDispatched)
            {
                return Failure(ex.Message,
                    ex.Retryable ? "tool_mutation_busy" : "tool_mutation_lock_unavailable",
                    ex.Retryable);
            }
        }

        private static ToolEffectEvidence Effect(ExcelReplaceEffect effect)
        {
            switch (effect)
            {
                case ExcelReplaceEffect.VerifiedNoChange:
                    return ToolEffectEvidence.VerifiedNoChange;
                case ExcelReplaceEffect.VerifiedChange:
                    return ToolEffectEvidence.VerifiedChange;
                case ExcelReplaceEffect.Unknown:
                    return ToolEffectEvidence.Unknown;
                default:
                    return ToolEffectEvidence.None;
            }
        }

        private static OfficeDocumentExecutionExpectation Target(ChatSession session)
        {
            return new OfficeDocumentExecutionExpectation
            {
                Host = session.Host,
                DocumentKey = session.DocumentKey,
                RuntimeDocumentKey = session.LastRun == null
                    ? string.Empty : session.LastRun.DocumentRuntimeKey
            };
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
