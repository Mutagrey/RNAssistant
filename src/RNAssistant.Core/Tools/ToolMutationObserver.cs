using System.Collections.Generic;

namespace RNAssistant.Core.Tools
{
    // A deliberate domain rejection before a mutation attempt/dispatch exists.
    public sealed class ToolMutationPreparationException : System.InvalidOperationException
    {
        public string Code { get; private set; }
        public ToolMutationPreparationException(string code, string message) : base(message) { Code = code; }
    }
    public interface IToolMutationObserver
    {
        string Prepare(ToolExecutionContext context, IDictionary<string, object> arguments);
        void MarkDispatchMayHaveOccurred(string attemptId);
        RNAssistant.Core.Models.ResourceAuthorityCommit Complete(string attemptId, ToolExecutionRecord record);
        void AbandonBeforeDispatch(string attemptId);
        void ReleaseUnresolved(string attemptId);
    }
}
