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
            options.TraceSink = record =>
            {
                if (previousSink != null) previousSink(record);
                if (record == null) return;
                Persist(session, record);
            };
            options.TraceSinkConfigured = true;
        }

        private void Persist(ChatSession session, LlmTraceRecord record)
        {
            var type = EventType(record.Type);
            var runId = session.LastRun == null ? null : session.LastRun.RunId;
            var turnId = session.LastRun == null || string.IsNullOrWhiteSpace(session.LastRun.TurnId)
                ? runId
                : session.LastRun.TurnId;
            var data = new
            {
                record.RequestId,
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
            Action append = () => _chatStore.AppendTrace(
                session,
                type,
                data,
                record.PayloadJson,
                record.PayloadContentType,
                runId,
                turnId,
                record.RequestId);

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
            return SessionEventTypes.LlmFailure;
        }
    }
}
