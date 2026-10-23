using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;

namespace RNAssistant.Core.Services
{
    public sealed class ConversationEventFeed
    {
        private readonly IEventStore _events;
        private ISessionEventReader _reader;
        private string _scope;
        public ConversationEventFeed(IEventStore events)
        { _events = events ?? throw new ArgumentNullException(nameof(events)); }

        public ConversationEventPage Read(ChatSession session, string after, string through = null, int limit = 50,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (limit < 1 || limit > 100) throw new ArgumentException("Event page limit must be 1–100.");
            var result = new ConversationEventPage { SessionId = session.Id, After = after };
            try
            {
                SessionEventPosition from, until = null;
                result.Reason = Decode(session.Id, after, out from);
                if (result.ResyncRequired) return result;
                if (through != null) result.Reason = Decode(session.Id, through, out until);
                if (result.ResyncRequired) return result;
                var scope = session.Host + ":" + session.DocumentKey + ":" + session.Id;
                if (_scope != scope) { _reader = _events.OpenReader(session); _scope = scope; }
                var page = _reader.Read(from, until, limit, cancellationToken);
                result.Reason = page.Reason;
                if (result.ResyncRequired) return result;
                result.HighWater = Encode(session.Id, page.HighWater.Sequence, page.HighWater.Hash);
                result.NextCursor = Encode(session.Id, page.Next.Sequence, page.Next.Hash);
                result.HasMore = page.Next.Sequence < page.HighWater.Sequence;
                result.Events = page.Events.Select(item => new ConversationEventNotice
                {
                    Sequence = item.Sequence, EventId = item.EventId, CreatedUtc = item.CreatedUtc, Type = item.Type,
                    RunId = item.RunId, TurnId = item.TurnId, StepId = item.StepId,
                    Operations = item.Type == SessionEventTypes.SessionCommit
                        ? CommitOperationTypes(item.Data)
                        : new string[0]
                }).ToArray();
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is JsonException || ex is UnauthorizedAccessException)
            { result = new ConversationEventPage { SessionId = session.Id, After = after, Reason = ConversationResyncReason.HistoryUnavailable }; }
            return result;
        }

        // One consumer at a time. Awaiting delivery is backpressure: there is no
        // unbounded queue, and no persistence/run lock survives a wait or callback.
        public async Task SubscribeAsync(ChatSession session, string after, Func<ConversationEventPage, Task> onPage,
            int limit = 50, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (onPage == null) throw new ArgumentNullException(nameof(onPage));
            string through = null;
            var first = true;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = Read(session, after, through, limit, cancellationToken);
                if (first || page.ResyncRequired || page.Events.Count != 0)
                    await onPage(page).ConfigureAwait(false);
                if (page.ResyncRequired) return;
                first = false;
                after = page.NextCursor;
                through = page.HasMore ? page.HighWater : null;
                if (!page.HasMore) await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
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

        private static ConversationResyncReason Decode(string sessionId, string token, out SessionEventPosition position)
        {
            position = null;
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
            long sequence;
            if (fields.Length != 3 || !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out sequence) || sequence < 1 ||
                fields[2].Length != 64 || fields[2].Any(ch => !Uri.IsHexDigit(ch)))
                return ConversationResyncReason.InvalidCursor;
            if (fields[0] != sessionId) return ConversationResyncReason.DifferentSession;
            position = new SessionEventPosition(sessionId, sequence, fields[2]);
            return ConversationResyncReason.None;
        }

        private static string[] CommitOperationTypes(JToken data)
        {
            var root = data as JObject;
            if (root == null) throw new JsonException("Session commit data must be an object.");
            var token = root["Operations"];
            if (token == null || token.Type == JTokenType.Null) return new string[0];
            var operations = token as JArray;
            if (operations == null) throw new JsonException("Session commit operations must be an array.");
            var types = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in operations)
            {
                if (item == null || item.Type == JTokenType.Null) continue;
                var operation = item as JObject;
                if (operation == null) throw new JsonException("Session commit operation must be an object.");
                var type = operation["Type"];
                if (type == null || type.Type == JTokenType.Null) continue;
                if (type.Type != JTokenType.String) throw new JsonException("Session commit operation type must be a string.");
                var name = (string)type;
                if (seen.Add(name)) types.Add(name);
            }
            return types.ToArray();
        }
    }
}
