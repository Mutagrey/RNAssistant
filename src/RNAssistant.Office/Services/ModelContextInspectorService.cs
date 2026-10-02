using System;
using System.Linq;
using System.Threading;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    internal sealed class ModelContextInspectorService
    {
        internal const string Owner = "model-context";
        private readonly IEventStore _events;
        private readonly ChatBlobStore _payloads;
        private readonly ResourceDataPlaneService _data;
        internal ModelContextInspectorService(IEventStore events, ChatBlobStore payloads, ResourceDataPlaneService data)
        { _events = events; _payloads = payloads; _data = data; }

        internal ModelContextResponse Query(ChatSession session, ModelContextQuery query, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var response = new ModelContextResponse { ChatId = session.Id, Revision = session.Revision,
                Events = new ModelContextEventDto[0] };
            if (!query.BeforeSequence.HasValue && string.IsNullOrEmpty(query.RequestEventId) &&
                query.KnownRevision == session.Revision)
            { response.Unchanged = true; return response; }
            var all = _events.Read(session, SessionEventReadMode.RequireComplete);
            token.ThrowIfCancellationRequested();
            response.Revision = all.LastOrDefault()?.Sequence ?? session.Revision;
            if (!string.IsNullOrEmpty(query.RequestEventId))
            {
                var request = all.SingleOrDefault(e => e.EventId == query.RequestEventId && e.Type == SessionEventTypes.LlmRequest);
                if (request == null) throw new InvalidOperationException("RESOURCE_SNAPSHOT_UNAVAILABLE: request event not found.");
                var trace = Trace(request);
                // Never correlate unrelated historical events with absent identifiers.
                var next = all.FirstOrDefault(e => e.Sequence > request.Sequence && e.Type == SessionEventTypes.LlmRequest &&
                    !string.IsNullOrEmpty(trace.RequestId) && Trace(e).RequestId == trace.RequestId);
                response.Events = all.Where(e => e.EventId == request.EventId ||
                    e.Sequence > request.Sequence && (next == null || e.Sequence < next.Sequence) && IsModelEvent(e.Type) &&
                    !string.IsNullOrEmpty(trace.RequestId) && e.RunId == request.RunId && Trace(e).RequestId == trace.RequestId)
                    .Select(e => Describe(e, true)).ToArray();
            }
            else
            {
                var requests = all.Where(e => e.Type == SessionEventTypes.LlmRequest &&
                    (!query.BeforeSequence.HasValue || e.Sequence < query.BeforeSequence.Value))
                    .OrderByDescending(e => e.Sequence).Take(51).ToArray();
                response.Events = requests.Take(50).Select(e => Describe(e, false)).ToArray();
                if (requests.Length > 50) response.NextBeforeSequence = requests[49].Sequence;
            }
            return response;
        }

        internal ModelContextPayloadResponse Open(ChatSession session, ModelContextPayloadQuery query, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var item = _events.Read(session, SessionEventReadMode.RequireComplete)
                .SingleOrDefault(e => e.SessionId == session.Id && e.EventId == query.EventId && IsModelEvent(e.Type));
            if (item == null) throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: exact model event required.");
            var source = item.Payload;
            if (query.OriginalIndex.HasValue)
            {
                var entries = Trace(item).ContextMessages;
                var index = query.OriginalIndex.Value;
                if (item.Type != SessionEventTypes.LlmRequest || entries == null || index < 0 || index >= entries.Count)
                    throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: exact context source required.");
                source = entries[index].OriginalPayload?.ToBlobReference();
            }
            if (source == null) throw new InvalidOperationException("RESOURCE_SNAPSHOT_UNAVAILABLE: saved body unavailable.");
            if (source.ByteLength > AttachmentStore.MaxMessageBytes)
                throw new InvalidOperationException("RESOURCE_BATCH_TOO_LARGE: payload exceeds the shared 50 MiB transfer budget; no truncated body was returned.");
            var lease = _data.OpenDownload(session, Owner, Math.Max(1, source.ByteLength), cancellation =>
            {
                var bytes = _payloads.ReadPrefix(source, checked((int)Math.Max(1, source.ByteLength)), cancellation);
                if (bytes == null || bytes.LongLength != source.ByteLength)
                    throw new InvalidOperationException("RESOURCE_SNAPSHOT_UNAVAILABLE: payload is missing, corrupt or inaccessible.");
                return new ResourceDownloadContent { Bytes = bytes, ContentType = source.ContentType ?? "application/json" };
            }, token);
            try
            {
                token.ThrowIfCancellationRequested();
                return new ModelContextPayloadResponse { ChatId = session.Id, EventId = item.EventId, Data = lease };
            }
            catch { _data.Close(session.Id, Owner, lease.LeaseId); throw; }
        }

        private static bool IsModelEvent(string type)
        { return type == SessionEventTypes.LlmRequest || type == SessionEventTypes.LlmResponse ||
            type == SessionEventTypes.LlmFailure || type == SessionEventTypes.AssistantChunk ||
            type == SessionEventTypes.AgentResponseRejected || type == SessionEventTypes.ModelResponseAccepted; }
        private static LlmTraceRecord Trace(SessionEvent item)
        { return item.Data?.ToObject<LlmTraceRecord>() ?? new LlmTraceRecord(); }
        private static ModelContextEventDto Describe(SessionEvent item, bool detail)
        {
            var trace = Trace(item);
            if (!detail) trace.ContextMessages = null;
            return new ModelContextEventDto { EventId = item.EventId, Sequence = item.Sequence, CreatedUtc = item.CreatedUtc,
                Type = item.Type, HasPayload = item.Payload != null, Trace = trace };
        }
    }
}
