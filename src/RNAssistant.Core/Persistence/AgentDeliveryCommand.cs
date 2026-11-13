using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Persistence
{
    public sealed class AgentDeliveryCommand
    {
        public string OperationId { get; set; }
        public ToolExecutionContext Execution { get; set; }
        public string AgentId { get; set; }
        public string RootChatId { get; set; }
        public string ChainId { get; set; }
        public int ParticipantLimit { get; set; }
        public string Text { get; set; }
        public string RecipientRuntimeKey { get; set; }
        public bool ExpectReply { get; set; }
        public string ReplyTo { get; set; }
        public bool Assignment { get; set; }
        public AgentAssignmentOutcome? Resolution { get; set; }
    }

    public sealed class AgentSpawnCommand
    {
        public string OperationId { get; set; }
        public ToolExecutionContext Execution { get; set; }
        public string RootChatId { get; set; }
        public string ChainId { get; set; }
        public int ParticipantLimit { get; set; }
        public string Title { get; set; }
        public string InitialText { get; set; }
        public string RuntimeKey { get; set; }
        public bool ExpectReply { get; set; }
        public bool Assignment { get; set; }
    }

    public sealed class AgentPauseCommand
    {
        public string OperationId { get; set; }
        public ToolExecutionContext Execution { get; set; }
        public string AgentId { get; set; }
        public string TargetRunId { get; set; }
        public string LiveInvocationId { get; set; }
        public string RuntimeKey { get; set; }
        public string PauseId { get; set; }
        public bool Resume { get; set; }
    }
}
