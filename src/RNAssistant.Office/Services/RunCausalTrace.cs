using System;
using System.Threading;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Diagnostics;

namespace RNAssistant.Office.Services
{
    // A logging scope only: no routing, retry, recovery or outcome decisions.
    internal sealed class RunCausalTrace : IDisposable
    {
        private static readonly AsyncLocal<RunCausalTrace> Current = new AsyncLocal<RunCausalTrace>();
        private readonly RunCausalTrace _previous;
        private readonly ChatStore _store;
        private readonly ChatSession _session;
        private readonly string _runId;
        private readonly string _turnId;
        private readonly string _documentRuntimeId;
        private int _disposed;

        private RunCausalTrace(ChatStore store, ChatSession session)
        {
            _previous = Current.Value;
            _store = store;
            _session = session;
            var run = session == null ? null : session.LastRun;
            _runId = run == null ? null : run.RunId;
            _turnId = run == null || string.IsNullOrWhiteSpace(run.TurnId) ? _runId : run.TurnId;
            _documentRuntimeId = run == null ? null : run.DocumentRuntimeKey;
        }

        public static RunCausalTrace Begin(ChatStore store, ChatSession session)
        {
            var scope = new RunCausalTrace(store, session);
            Current.Value = scope;
            return scope;
        }

        public static void Record(CausalTraceRecord record)
        {
            var scope = Current.Value;
            if (scope == null || Volatile.Read(ref scope._disposed) != 0 || record == null ||
                scope._store == null || scope._session == null || string.IsNullOrWhiteSpace(scope._runId)) return;
            record.SessionId = scope._session.Id;
            record.RunId = scope._runId;
            record.TurnId = scope._turnId;
            if (string.IsNullOrWhiteSpace(record.DocumentRuntimeId)) record.DocumentRuntimeId = scope._documentRuntimeId;
            try
            {
                scope._store.AppendTrace(scope._session, record.Stage, record, null, null,
                    scope._runId, scope._turnId, record.StepId);
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
            Record(new CausalTraceRecord
            {
                Stage = "run.summary.created",
                Status = session == null || session.LastRun == null ? null : session.LastRun.Status,
                Boundary = "legacy_run_record"
            });
        }

        public static void Projected(string dto)
        {
            Record(new CausalTraceRecord { Stage = "ui.projected", Boundary = dto });
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
            if (ReferenceEquals(Current.Value, this)) Current.Value = _previous;
        }
    }

    internal sealed class CausalTraceRecord
    {
        public string Stage { get; set; }
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
