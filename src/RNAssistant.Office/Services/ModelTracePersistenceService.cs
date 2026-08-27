using System;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Services
{
    internal sealed class ModelTracePersistenceService
    {
        private readonly ChatStore _chatStore;
        private readonly SessionTraceWriteQueue _queue;

        public ModelTracePersistenceService(ChatStore chatStore)
            : this(chatStore, new SessionTraceWriteQueue())
        {
        }

        internal ModelTracePersistenceService(ChatStore chatStore, SessionTraceWriteQueue queue)
        {
            _chatStore = chatStore ?? throw new ArgumentNullException("chatStore");
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
            var type = EventType(record.Type);
            var data = new
            {
                Stage = Stage(record.Type),
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
            Action append;
            if (record.PayloadUtf8Bytes == null)
            {
                append = () => _chatStore.AppendTrace(
                    session,
                    type,
                    data,
                    record.PayloadJson,
                    record.PayloadContentType,
                    runId,
                    turnId,
                    record.RequestId);
            }
            else
            {
                append = () => _chatStore.AppendTraceBytes(
                    session,
                    type,
                    data,
                    record.PayloadUtf8Bytes,
                    record.PayloadContentType,
                    runId,
                    turnId,
                    record.RequestId);
            }

            if (string.Equals(type, SessionEventTypes.AssistantChunk, StringComparison.Ordinal))
            {
                _queue.Enqueue(session.Id, append);
                return;
            }
            _queue.EnqueueAndDrain(session.Id, append);
        }

        private static string EventType(string type)
        {
            if (string.Equals(type, "request", StringComparison.OrdinalIgnoreCase)) return SessionEventTypes.LlmRequest;
            if (string.Equals(type, "response", StringComparison.OrdinalIgnoreCase)) return SessionEventTypes.LlmResponse;
            if (string.Equals(type, "chunk", StringComparison.OrdinalIgnoreCase)) return SessionEventTypes.AssistantChunk;
            if (string.Equals(type, "rejected", StringComparison.OrdinalIgnoreCase)) return SessionEventTypes.AgentResponseRejected;
            if (string.Equals(type, "accepted", StringComparison.OrdinalIgnoreCase)) return "model.response.accepted";
            return SessionEventTypes.LlmFailure;
        }

        private static string Stage(string type)
        {
            if (string.Equals(type, "request", StringComparison.OrdinalIgnoreCase)) return "model.request.prepared";
            if (string.Equals(type, "rejected", StringComparison.OrdinalIgnoreCase)) return "model.attempt.rejected";
            if (string.Equals(type, "accepted", StringComparison.OrdinalIgnoreCase)) return "model.response.accepted";
            return null;
        }
    }
}
