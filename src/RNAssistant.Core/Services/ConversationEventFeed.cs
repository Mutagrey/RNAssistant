using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;

namespace RNAssistant.Core.Services
{
    public sealed class ConversationEventFeed
    {
        private readonly IEventStore _events;
        public ConversationEventFeed(IEventStore events)
        { _events = events ?? throw new ArgumentNullException(nameof(events)); }

        public ConversationEventPage Read(ChatSession session, string after, string through = null, int limit = 50)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (limit < 1 || limit > 100) throw new ArgumentException("Event page limit must be 1–100.");
            var result = new ConversationEventPage { SessionId = session.Id, After = after };
            try
            {
                var history = _events.Read(session, SessionEventReadMode.RequireComplete);
                if (history.Count == 0) { result.Reason = ConversationResyncReason.HistoryUnavailable; return result; }
                long from, until;
                result.Reason = Validate(session.Id, after, history, out from);
                if (result.ResyncRequired) return result;
                result.HighWater = through ?? Cursor(history[history.Count - 1]);
                result.Reason = Validate(session.Id, result.HighWater, history, out until);
                if (result.ResyncRequired) return result;
                if (from > until) { result.Reason = ConversationResyncReason.InvalidCursor; return result; }
                var page = history.Where(item => item.Sequence > from && item.Sequence <= until).Take(limit).ToArray();
                result.NextCursor = page.Length == 0 ? after : Cursor(page[page.Length - 1]);
                result.HasMore = (page.Length == 0 ? from : page[page.Length - 1].Sequence) < until;
                result.Events = page.Select(item => new ConversationEventNotice
                {
                    Sequence = item.Sequence, EventId = item.EventId, CreatedUtc = item.CreatedUtc, Type = item.Type,
                    RunId = item.RunId, TurnId = item.TurnId, StepId = item.StepId,
                    Operations = item.Type == SessionEventTypes.SessionCommit
                        ? (item.Data.ToObject<CommitData>()?.Operations ?? new List<SessionOperation>()).Select(op => op.Type).Distinct().ToArray()
                        : new string[0]
                }).ToArray();
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is JsonException || ex is UnauthorizedAccessException)
            { result = new ConversationEventPage { SessionId = session.Id, After = after, Reason = ConversationResyncReason.HistoryUnavailable }; }
            return result;
        }

        // Only the storage-owned hash/sequence of a loaded or successfully saved
        // session is eligible. Kernel append counters never become transport cursors.
        public static string Cursor(ChatSession session)
        { return Encode(session.Id, session.Revision, session.StorageHeadHash); }
        public static string Cursor(SessionEvent fact)
        { return Encode(fact.SessionId, fact.Sequence, fact.Hash); }

        private static string Encode(string sessionId, long sequence, string hash)
        {
            if (string.IsNullOrEmpty(sessionId) || sequence < 1 || string.IsNullOrEmpty(hash))
                throw new InvalidOperationException("A durable session checkpoint is required.");
            return "events1." + Convert.ToBase64String(Encoding.UTF8.GetBytes(sessionId + ":" +
                sequence.ToString(CultureInfo.InvariantCulture) + ":" + hash)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static ConversationResyncReason Validate(string sessionId, string token, IReadOnlyList<SessionEvent> history, out long sequence)
        {
            sequence = 0;
            if (token == null || token.Length > 512 || !token.StartsWith("events1.", StringComparison.Ordinal))
                return ConversationResyncReason.InvalidCursor;
            string[] fields;
            try
            {
                var data = token.Substring(8).Replace('-', '+').Replace('_', '/');
                data = data.PadRight(data.Length + (4 - data.Length % 4) % 4, '=');
                fields = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(data)).Split(':');
            }
            catch (Exception ex) when (ex is FormatException || ex is DecoderFallbackException)
            { return ConversationResyncReason.InvalidCursor; }
            if (fields.Length != 3 || !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out sequence) || sequence < 1)
                return ConversationResyncReason.InvalidCursor;
            if (fields[0] != sessionId) return ConversationResyncReason.DifferentSession;
            var position = sequence;
            var fact = history.FirstOrDefault(item => item.Sequence == position);
            return fact != null && fact.Hash == fields[2] ? ConversationResyncReason.None : ConversationResyncReason.HistoryChanged;
        }

        private sealed class CommitData
        { public List<SessionOperation> Operations { get; set; } }
    }
}
