using System;
using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Persistence
{
    internal sealed class CausalTraceRecord
    {
        public CausalTraceRecord(SessionEventKind kind)
        {
            var descriptor = SessionEventDescriptors.For(kind);
            if (descriptor.Lane != SessionEventLane.DomainDiagnostic ||
                descriptor.Authority != SessionEventAuthority.Diagnostic ||
                descriptor.Durability != SessionEventDurability.BestEffort)
            {
                throw new ArgumentOutOfRangeException(nameof(kind), kind,
                    "Causal trace accepts only best-effort Domain Diagnostic events.");
            }
            Kind = kind;
        }

        [JsonIgnore]
        public SessionEventKind Kind { get; private set; }
        public string Stage
        {
            get
            {
                return Kind == SessionEventKind.Unknown
                    ? null
                    : SessionEventDescriptors.For(Kind).Type;
            }
        }
        public string SessionId { get; set; }
        public string RunId { get; set; }
        public string TurnId { get; set; }
        public string StepId { get; set; }
        public string ModelAttemptId { get; set; }
        public string ToolCallId { get; set; }
        public string DocumentRuntimeId { get; set; }
        public string MutationId { get; set; }
        public string JournalRunId { get; set; }
        public string ToolId { get; set; }
        public string Status { get; set; }
        public string Code { get; set; }
        public string Boundary { get; set; }
    }
}
