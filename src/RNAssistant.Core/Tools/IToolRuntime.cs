using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Agent;

namespace RNAssistant.Core.Tools
{
    public interface IToolRuntime
    {
        // Lookup only; must not dispatch. The adapter owns exact policy/argument
        // checks and confirmation before effects, including after a resume.
        ToolPolicySnapshot Describe(ToolCall call);
        Task<ToolExecutionRecord> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken);
    }
}
