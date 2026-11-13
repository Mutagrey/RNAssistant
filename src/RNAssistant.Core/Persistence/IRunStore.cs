using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Agent;
using RNAssistant.Core.ModelProtocol;

namespace RNAssistant.Core.Persistence
{
    public interface IRunStore
    {
        // Mandatory ordered append with a per-turn compare-and-swap cursor. Zero
        // means no run facts exist. Return a strictly newer cursor only after
        // durability. Stale continuations must fail before another dispatch.
        Task<long> AppendAsync(AgentRunEvent fact, long expectedRevision, CancellationToken cancellationToken);
        // Exact proposed call only: history length must not disable ordinary work.
        Task<RNAssistant.Core.Tools.ToolExecutionProgress> ReadReplayGuardAsync(ToolCall call,
            long expectedRevision, CancellationToken cancellationToken);
        // Read canonical durable outcomes for this exact run/session before a
        // model-authored finish can be accepted. Never infer them from a prompt.
        Task<FinishAdmission> EvaluateFinishAsync(string runId, string turnId,
            IReadOnlyList<string> acceptedCallIds, IReadOnlyList<ConversationResultClaim> results,
            long expectedRevision, CancellationToken cancellationToken);
    }

    public sealed class FinishAdmission
    {
        public bool Accepted { get; private set; }
        public string Code { get; private set; }
        public string Message { get; private set; }
        public int? ClaimIndex { get; private set; }

        private FinishAdmission(bool accepted, string code, string message, int? claimIndex = null)
        { Accepted = accepted; Code = code; Message = message; ClaimIndex = claimIndex; }

        public static FinishAdmission Allow() { return new FinishAdmission(true, null, null); }
        public static FinishAdmission Reject(string code, string message, int? claimIndex = null)
        {
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(message) || claimIndex < 0)
                throw new ArgumentException("A typed rejection requires a code and feedback.");
            return new FinishAdmission(false, code, message, claimIndex);
        }
    }

    public sealed class RunStoreException : Exception
    {
        // This is available in memory only. A failed append is never reported as
        // a persisted summary and is never automatically retried by the kernel.
        public RunSummary UnpersistedSummary { get; private set; }

        internal RunStoreException(RunSummary summary, Exception inner)
            : base("Run evidence could not be appended. Execution stopped; replay is required.", inner)
        {
            UnpersistedSummary = summary;
        }
    }
}
