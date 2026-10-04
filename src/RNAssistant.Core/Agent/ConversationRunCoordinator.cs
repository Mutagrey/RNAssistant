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
            CancellationToken cancellationToken, IRunInputChannel input = null)
        {
            return new AgentKernel(model, tools, store, input: input).RunAsync(request, cancellationToken);
        }

        public static Task<AgentRunResult> ResumeAsync(string runId, string pendingId,
            AgentRunContinuation continuation, IModelProtocol model, IToolRuntime tools,
            IRunStore store, CancellationToken cancellationToken,
            bool supersedePending = false, IRunInputChannel input = null)
        {
            return new AgentKernel(model, tools, store, input: input).ResumeAsync(
                runId, pendingId, continuation, cancellationToken, supersedePending);
        }
    }
}
