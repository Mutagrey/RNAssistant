using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.Word;
using RNAssistant.Office.Runtime;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class WordToolHandler : IToolHandler
    {
        private readonly string _toolId;
        private readonly WordToolAdapter _adapter;
        private readonly HostRuntime _runtime;
        private readonly ChatSession _session;

        internal WordToolHandler(
            string toolId,
            WordToolAdapter adapter,
            HostRuntime runtime,
            ChatSession session)
        {
            if (!WordToolIds.Owns(toolId))
                throw new ArgumentException(
                    "An exact Word tool id is required.", nameof(toolId));
            _toolId = toolId;
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _session = session;
        }

        internal static ToolBinding BindingFor(string toolId)
        {
            if (!WordToolIds.Owns(toolId)) return null;
            return new ToolBinding(
                "word." + toolId.Substring("word.".Length).Replace('_', '.') +
                ".v1");
        }

        public Task<ToolHandlerResult> ExecuteAsync(
            ToolHandlerContext context,
            CancellationToken cancellationToken)
        {
            if (_session == null)
                return Failure(
                    "Word operations require an active chat session.",
                    "word_session_required", false);
            try
            {
                var outcome = WordToolIds.IsRead(_toolId)
                    ? _runtime.ReadDocument(
                        Target(_session), cancellationToken, delegate
                        {
                            context.MarkDispatchPossible();
                            return _adapter.Execute(
                                _toolId, context.Arguments, null,
                                cancellationToken);
                        })
                    : _runtime.ExecuteDocumentMutation(
                        Target(_session), cancellationToken, delegate
                        {
                            return _adapter.Execute(
                                _toolId, context.Arguments,
                                context.MarkDispatchPossible,
                                cancellationToken);
                        });
                return Task.FromResult(Result(outcome));
            }
            catch (OfficeDocumentGuardException ex)
                when (!context.MayHaveDispatched)
            {
                return Failure(ex.Message, ex.ErrorCode, ex.Retryable);
            }
            catch (HostRuntime.MutationLockException ex)
                when (!context.MayHaveDispatched)
            {
                return Failure(
                    ex.Message,
                    ex.Retryable ? "tool_mutation_busy" :
                        "tool_mutation_lock_unavailable",
                    ex.Retryable);
            }
        }

        private static ToolHandlerResult Result(WordOutcome outcome)
        {
            if (outcome == null)
                throw new InvalidOperationException(
                    "Word operation returned no outcome.");
            RuntimeResult result;
            if (outcome.Status == WordOutcomeStatus.Ok)
                result = RuntimeResult.Ok(outcome.Message, outcome.DataJson);
            else if (outcome.Status == WordOutcomeStatus.Unknown)
                result = RuntimeResult.Unknown(outcome.Message, outcome.DataJson);
            else result = RuntimeResult.Error(outcome.Message, outcome.DataJson);
            return new ToolHandlerResult(result, Effect(outcome.Effect));
        }

        private static ToolEffectEvidence Effect(WordEffect effect)
        {
            switch (effect)
            {
                case WordEffect.VerifiedNoChange:
                    return ToolEffectEvidence.VerifiedNoChange;
                case WordEffect.VerifiedChange:
                    return ToolEffectEvidence.VerifiedChange;
                case WordEffect.Unknown:
                    return ToolEffectEvidence.Unknown;
                default:
                    return ToolEffectEvidence.None;
            }
        }

        private static OfficeDocumentExecutionExpectation Target(
            ChatSession session)
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
