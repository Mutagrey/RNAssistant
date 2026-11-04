using System;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Agent
{
    // Shared production entry for every host. The kernel remains the only loop/outcome owner.
    public static class ConversationRunCoordinator
    {
        public static Task<AgentRunResult> RunAsync(AgentRunRequest request,
            IModelProtocol model, IToolRuntime tools, IRunStore store,
            CancellationToken cancellationToken, IRunInputChannel input = null, Func<bool> suspendOnCancellation = null)
        {
            var kernel = new AgentKernel(model, tools, store, input: input, suspendOnCancellation: suspendOnCancellation);
            return DrainWaitsAsync(kernel, kernel.RunAsync(request, cancellationToken), cancellationToken, suspendOnCancellation);
        }

        public static Task<AgentRunResult> ResumeAsync(string runId, string pendingId,
            AgentRunContinuation continuation, IModelProtocol model, IToolRuntime tools,
            IRunStore store, CancellationToken cancellationToken,
            bool supersedePending = false, IRunInputChannel input = null, bool rejectPending = false, Func<bool> suspendOnCancellation = null)
        {
            var kernel = new AgentKernel(model, tools, store, input: input, suspendOnCancellation: suspendOnCancellation);
            return DrainWaitsAsync(kernel, kernel.ResumeAsync(
                runId, pendingId, continuation, cancellationToken, supersedePending, rejectPending), cancellationToken, suspendOnCancellation);
        }

        public static Task<AgentRunResult> ContinueAsync(AgentRunContinuation continuation,
            IModelProtocol model, IToolRuntime tools, IRunStore store, CancellationToken cancellationToken, IRunInputChannel input = null, Func<bool> suspendOnCancellation = null)
        {
            var kernel = new AgentKernel(model, tools, store, input: input, suspendOnCancellation: suspendOnCancellation);
            return DrainWaitsAsync(kernel, continuation.Summary.Lifecycle == RunLifecycle.Waiting
                ? kernel.ResumeWaitAsync(continuation, cancellationToken) : kernel.ContinueAsync(continuation, cancellationToken), cancellationToken, suspendOnCancellation);
        }
        private static async Task<AgentRunResult> DrainWaitsAsync(AgentKernel kernel, Task<AgentRunResult> segment, CancellationToken token, Func<bool> suspendOnCancellation)
        {
            var result = await segment.ConfigureAwait(false);
            while (result.Summary.Lifecycle == RunLifecycle.Waiting && !(token.IsCancellationRequested && suspendOnCancellation?.Invoke() == true))
                result = await kernel.ResumeWaitAsync(result.Continuation, token).ConfigureAwait(false);
            return result;
        }
    }
}
