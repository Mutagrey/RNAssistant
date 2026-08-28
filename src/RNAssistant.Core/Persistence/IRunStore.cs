using System;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Agent;

namespace RNAssistant.Core.Persistence
{
    public interface IRunStore
    {
        // Mandatory ordered append with a per-turn compare-and-swap cursor. Zero
        // means no run facts exist. Return a strictly newer cursor only after
        // durability. Stale continuations must fail before another dispatch.
        Task<long> AppendAsync(AgentRunEvent fact, long expectedRevision, CancellationToken cancellationToken);
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
