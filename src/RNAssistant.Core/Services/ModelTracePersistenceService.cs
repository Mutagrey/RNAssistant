using System;
using System.Diagnostics;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;

namespace RNAssistant.Core.Services
{
    public sealed class ModelTracePersistenceService
    {
        private readonly IEventStore _eventStore;
        private readonly SessionTraceWriteQueue _queue;
        private readonly Action<string> _diagnosticLog;

        public ModelTracePersistenceService(IEventStore eventStore, Action<string> diagnosticLog = null)
            : this(eventStore, new SessionTraceWriteQueue(), diagnosticLog)
        {
        }

        internal ModelTracePersistenceService(IEventStore eventStore, SessionTraceWriteQueue queue,
            Action<string> diagnosticLog = null)
        {
            _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
            _queue = queue ?? throw new ArgumentNullException("queue");
            _diagnosticLog = diagnosticLog;
        }

        public void Configure(LlmRequestOptions options)
        {
            if (options == null || options.TraceSession == null || options.TraceSinkConfigured) return;
            var session = options.TraceSession;
            var previousSink = options.TraceSink;
            var run = session.LastRun;
            var runId = run == null ? null : run.RunId;
            var turnId = run == null || string.IsNullOrWhiteSpace(run.TurnId) ? runId : run.TurnId;
            var documentRuntimeId = run == null ? null : run.DocumentRuntimeKey;
            options.TraceSink = record =>
            {
                if (record == null) return;
                if (string.Equals(record.Type, "request", StringComparison.OrdinalIgnoreCase))
                {
                    options.TraceRequestId = record.RequestId;
                }
                if (previousSink != null) previousSink(record);
                Persist(session, options, record, runId, turnId, documentRuntimeId);
            };
            options.TraceSinkConfigured = true;
        }

        private void Persist(ChatSession session, LlmRequestOptions options, LlmTraceRecord record,
            string runId, string turnId, string documentRuntimeId)
        {
            var descriptor = Descriptor(record.Type);
            record.Stage = Stage(descriptor.Kind);
            record.SessionId = session.Id;
            record.RunId = runId;
            record.TurnId = turnId;
            record.StepId = options.TraceStepId ?? record.RequestId;
            record.ModelAttemptId = options.TraceModelAttemptId ?? record.RequestId;
            record.ContextSnapshotId = options.TraceContextReceipt?.SnapshotId;
            record.DocumentRuntimeId = documentRuntimeId;
            var payload = record.PayloadUtf8Bytes == null
                ? SessionEventPayload.FromText(record.PayloadJson, record.PayloadContentType)
                : SessionEventPayload.FromBytes(record.PayloadUtf8Bytes, record.PayloadContentType);
            var write = new SessionEventWrite(
                descriptor,
                record,
                payload,
                new SessionEventCorrelation(runId, turnId, record.RequestId));
            Action append = () => _eventStore.Append(session, write);

            if (descriptor.Kind == SessionEventKind.ModelStreamChunk)
            {
                _queue.Enqueue(session.Id, append);
                return;
            }
            var timer = Stopwatch.StartNew();
            try { _queue.EnqueueAndDrain(session.Id, append); }
            finally
            {
                if (timer.ElapsedMilliseconds >= 250)
                    _diagnosticLog?.Invoke(
                        "Model trace timing: stage=" + descriptor.Kind + ", persist=" +
                        timer.ElapsedMilliseconds + "ms.");
            }
        }

        private static SessionEventDescriptor Descriptor(string type)
        {
            if (string.Equals(type, "request", StringComparison.OrdinalIgnoreCase))
                return SessionEventDescriptors.For(SessionEventKind.ModelRequestPrepared);
            if (string.Equals(type, "response", StringComparison.OrdinalIgnoreCase))
                return SessionEventDescriptors.For(SessionEventKind.ModelResponseReceived);
            if (string.Equals(type, "chunk", StringComparison.OrdinalIgnoreCase))
                return SessionEventDescriptors.For(SessionEventKind.ModelStreamChunk);
            if (string.Equals(type, "rejected", StringComparison.OrdinalIgnoreCase))
                return SessionEventDescriptors.For(SessionEventKind.ModelAttemptRejected);
            if (string.Equals(type, "accepted", StringComparison.OrdinalIgnoreCase))
                return SessionEventDescriptors.For(SessionEventKind.ModelResponseAccepted);
            if (string.Equals(type, "failure", StringComparison.OrdinalIgnoreCase))
                return SessionEventDescriptors.For(SessionEventKind.ModelFailure);
            throw new InvalidOperationException("Unsupported model trace type: " + (type ?? "<null>") + ".");
        }

        private static string Stage(SessionEventKind kind)
        {
            if (kind == SessionEventKind.ModelRequestPrepared) return "model.request.prepared";
            if (kind == SessionEventKind.ModelAttemptRejected) return "model.attempt.rejected";
            if (kind == SessionEventKind.ModelResponseAccepted) return SessionEventTypes.ModelResponseAccepted;
            return null;
        }
    }
}
