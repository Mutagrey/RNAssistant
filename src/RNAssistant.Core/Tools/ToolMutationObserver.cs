using System.Collections.Generic;

namespace RNAssistant.Core.Tools
{
    public interface IToolMutationObserver
    {
        string Prepare(ToolExecutionContext context, IDictionary<string, object> arguments);
        void MarkDispatchMayHaveOccurred(string attemptId);
        RNAssistant.Core.Models.ResourceAuthorityCommit Complete(string attemptId, ToolExecutionRecord record);
        void AbandonBeforeDispatch(string attemptId);
        void ReleaseUnresolved(string attemptId);
    }
}
