using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Runtime;

namespace RNAssistant.Office.Tools
{
    // Office retains its bound host gate across mutation and authority publication.
    internal sealed class DocumentTaskListToolHandler : IPlanningArtifactToolHandler
    {
        private readonly TaskListToolHandler _handler;

        internal DocumentTaskListToolHandler(string toolId, ChatSession session)
        { _handler = new TaskListToolHandler(toolId, session); }

        public Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken cancellationToken)
        {
            using (DocumentAccessGate.BeginOperation())
                return _handler.ExecuteAsync(context, cancellationToken);
        }
    }
}
