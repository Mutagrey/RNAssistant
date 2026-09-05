using System;
using System.Linq;
using System.Text;
using System.Threading;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    internal sealed class TrajectoryPayloadService
    {
        internal const string Owner = "trajectory-payload";
        internal const int MaximumCharacters = 512 * 1024;
        internal const int MaximumPreviewBytes = 4 * (MaximumCharacters + 1);
        internal const long MaximumSourceBytes = 32L * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly IEventStore _events;
        private readonly ChatBlobStore _payloads;
        private readonly ResourceDataPlaneService _data;

        internal TrajectoryPayloadService(IEventStore events, ChatBlobStore payloads, ResourceDataPlaneService data)
        {
            _events = events ?? throw new ArgumentNullException(nameof(events));
            _payloads = payloads ?? throw new ArgumentNullException(nameof(payloads));
            _data = data ?? throw new ArgumentNullException(nameof(data));
        }

        internal ChatEventPayloadResponse Open(ChatSession session, string eventId, CancellationToken token)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.Id) || string.IsNullOrWhiteSpace(eventId))
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: an explicit chat and event are required.");
            ChatEventPayloadResponse response = null;
            var lease = _data.OpenDownload(session, Owner, MaximumPreviewBytes, cancellation =>
            {
                cancellation.ThrowIfCancellationRequested();
                var matches = _events.Read(session, SessionEventReadMode.RequireComplete)
                    .Where(item => item != null && string.Equals(item.EventId, eventId, StringComparison.Ordinal)).Take(2).ToArray();
                cancellation.ThrowIfCancellationRequested();
                if (matches.Length != 1 || matches[0].SessionId != session.Id || matches[0].Payload == null)
                    throw new InvalidOperationException("RESOURCE_SNAPSHOT_UNAVAILABLE: exact event payload evidence is unavailable or ambiguous.");
                var source = PayloadRef.FromBlob(matches[0].Payload);
                if (source.ByteLength > MaximumSourceBytes)
                    throw new InvalidOperationException("RESOURCE_BATCH_TOO_LARGE: diagnostic payload verification is limited to 32 MiB.");
                var prefix = _payloads.ReadPrefix(source.ToBlobReference(), MaximumPreviewBytes, cancellation);
                if (prefix == null)
                    throw new InvalidOperationException("RESOURCE_SNAPSHOT_UNAVAILABLE: event payload is missing, corrupt or inaccessible.");
                // The source is fully hash-verified; a partial trailing UTF-8 sequence
                // outside the retained prefix is not a character in the preview.
                var characters = new char[prefix.Length];
                var count = Utf8.GetDecoder().GetChars(prefix, 0, prefix.Length, characters, 0, prefix.LongLength == source.ByteLength);
                count = Math.Min(count, MaximumCharacters);
                if (count > 0 && char.IsHighSurrogate(characters[count - 1])) count--;
                var bytes = Utf8.GetBytes(characters, 0, count);
                cancellation.ThrowIfCancellationRequested();
                response = new ChatEventPayloadResponse { ChatId = session.Id, EventId = eventId,
                    Sha256 = source.Sha256, ByteLength = source.ByteLength, ContentType = source.ContentType,
                    ReturnedCharacters = count, TextTruncated = bytes.LongLength < source.ByteLength };
                return new ResourceDownloadContent { Bytes = bytes, ContentType = "text/plain; charset=utf-8" };
            }, token);
            try
            {
                token.ThrowIfCancellationRequested();
                response.Data = lease;
                return response;
            }
            catch { _data.Close(session.Id, Owner, lease.LeaseId); throw; }
        }
    }
}
