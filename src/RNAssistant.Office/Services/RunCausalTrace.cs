using System;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Office.Diagnostics;

namespace RNAssistant.Office.Services
{
    // A logging scope only: no routing, retry, recovery or outcome decisions.
    internal sealed class RunCausalTrace : IDisposable
    {
        private static readonly AsyncLocal<RunCausalTrace> Current = new AsyncLocal<RunCausalTrace>();
        private readonly RunCausalTrace _previous;
        private readonly IEventStore _events;
        private readonly ChatSession _session;
        private readonly string _runId;
        private readonly string _turnId;
        private readonly string _documentRuntimeId;
        private int _disposed;

        private RunCausalTrace(IEventStore eventStore, ChatSession session)
        {
            _previous = Current.Value;
            _events = eventStore;
            _session = session;
            var run = session == null ? null : session.LastRun;
            _runId = run == null ? null : run.RunId;
            _turnId = run == null || string.IsNullOrWhiteSpace(run.TurnId) ? _runId : run.TurnId;
            _documentRuntimeId = run == null ? null : run.DocumentRuntimeKey;
        }

        public static RunCausalTrace Begin(IEventStore eventStore, ChatSession session)
        {
            var scope = new RunCausalTrace(eventStore, session);
            Current.Value = scope;
            return scope;
        }

        public static void Record(CausalTraceRecord record)
        {
            var scope = Current.Value;
            if (scope == null || Volatile.Read(ref scope._disposed) != 0 || record == null ||
                record.Kind == SessionEventKind.Unknown || scope._events == null || scope._session == null ||
                string.IsNullOrWhiteSpace(scope._runId)) return;
            record.SessionId = scope._session.Id;
            record.RunId = scope._runId;
            record.TurnId = scope._turnId;
            if (string.IsNullOrWhiteSpace(record.DocumentRuntimeId)) record.DocumentRuntimeId = scope._documentRuntimeId;
            try
            {
                scope._events.Append(scope._session, new SessionEventWrite(
                    SessionEventDescriptors.For(record.Kind),
                    record,
                    null,
                    new SessionEventCorrelation(scope._runId, scope._turnId, record.StepId)));
            }
            catch (Exception)
            {
                // Optional observation must not change a tool result or hide an execution exception.
                // Do not log payloads, paths, exception text or model/tool content here.
                RuntimeLog.Error("Causal trace append failed at " + record.Stage + ".");
            }
        }

        public static void Summary(ChatSession session)
        {
            Record(new CausalTraceRecord(SessionEventKind.RunSummaryCreated)
            {
                Status = session == null || session.LastRun == null ? null : session.LastRun.Status,
                Boundary = "legacy_run_record"
            });
        }

        public static void Projected(string dto)
        {
            Record(new CausalTraceRecord(SessionEventKind.UiProjected) { Boundary = dto });
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
            if (ReferenceEquals(Current.Value, this)) Current.Value = _previous;
        }
    }

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
