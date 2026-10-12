using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Agent;

namespace RNAssistant.Core.ModelProtocol
{
    // Kernel port. The application adapter supplies materialization; the existing
    // IMaterializedModelProtocol owns provider attempts and wire validation.
    public interface IModelProtocol
    {
        Task<AgentModelResult> SendAsync(AgentModelRequest request, CancellationToken cancellationToken);
    }

    public sealed class AgentModelRequest
    {
        public string RunId { get; private set; }
        public string TurnId { get; private set; }
        public string StepId { get; private set; }
        public IReadOnlyList<AgentMessage> AcceptedMessages { get; private set; }

        internal AgentModelRequest(string runId, string turnId, string stepId,
            IEnumerable<AgentMessage> messages)
        {
            RunId = runId;
            TurnId = turnId;
            StepId = stepId;
            AcceptedMessages = Array.AsReadOnly(messages.ToArray());
        }
    }

    public sealed class AgentModelResult
    {
        public AgentResponseDraft Response { get; private set; }
        public ModelProtocolFailureKind? FailureKind { get; private set; }
        public string Message { get; private set; }
        public bool ProviderRefusal { get; private set; }

        private AgentModelResult() { }

        public static AgentModelResult Accepted(AgentResponseDraft response)
        {
            return new AgentModelResult { Response = response ?? throw new ArgumentNullException(nameof(response)) };
        }

        public static AgentModelResult Failed(ModelProtocolFailureKind kind, string message)
        {
            if (!Enum.IsDefined(typeof(ModelProtocolFailureKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            return new AgentModelResult { FailureKind = kind, Message = message ?? string.Empty };
        }

        public static AgentModelResult Refused(string message)
        {
            return new AgentModelResult { ProviderRefusal = true, Message = message ?? string.Empty };
        }
    }
}
