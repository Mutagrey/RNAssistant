using System;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;

namespace RNAssistant.Office.Services
{
    internal sealed class ModelTracePersistenceService
    {
        private readonly IEventStore _eventStore;
        private readonly SessionTraceWriteQueue _queue;

        public ModelTracePersistenceService(IEventStore eventStore)
            : this(eventStore, new SessionTraceWriteQueue())
        {
        }

        internal ModelTracePersistenceService(IEventStore eventStore, SessionTraceWriteQueue queue)
        {
            _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
            _queue = queue ?? throw new ArgumentNullException("queue");
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
            var data = new
            {
                Stage = Stage(descriptor.Kind),
                SessionId = session.Id,
                RunId = runId,
                TurnId = turnId,
                // Helper requests (title/compaction/media) have one transport attempt per step.
                StepId = options.TraceStepId ?? record.RequestId,
                ModelAttemptId = options.TraceModelAttemptId ?? record.RequestId,
                DocumentRuntimeId = documentRuntimeId,
                record.RequestId,
                record.ResponseStatus,
                record.ToolCallIds,
                record.Purpose,
                record.Endpoint,
                record.Model,
                record.ResponseFormat,
                record.MessageCount,
                record.Attempt,
                record.EstimatedPromptTokens,
                record.PromptTokens,
                record.CompletionTokens,
                record.TotalTokens,
                record.ReasoningTokens,
                record.UsageJson,
                record.StatusCode,
                record.FailureKind,
                record.Error,
                record.ChunkIndex,
                record.ChunkCount,
                record.Completed,
                record.ChunkEncoding
            };
            var payload = record.PayloadUtf8Bytes == null
                ? SessionEventPayload.FromText(record.PayloadJson, record.PayloadContentType)
                : SessionEventPayload.FromBytes(record.PayloadUtf8Bytes, record.PayloadContentType);
            var write = new SessionEventWrite(
                descriptor,
                data,
                payload,
                new SessionEventCorrelation(runId, turnId, record.RequestId));
            Action append = () => _eventStore.Append(session, write);

            if (descriptor.Kind == SessionEventKind.ModelStreamChunk)
            {
                _queue.Enqueue(session.Id, append);
                return;
            }
            _queue.EnqueueAndDrain(session.Id, append);
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
