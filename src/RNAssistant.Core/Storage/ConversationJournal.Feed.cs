using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Data.Sqlite;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;

namespace RNAssistant.Core.Storage
{
    internal sealed partial class ConversationJournal
    {
        // A reader retains only its address. The existing index locates canonical
        // frames; neither event bodies nor a second history are cached here.
        private sealed class JournalEventReader : ISessionEventReader
        {
            private readonly ConversationJournal _journal;
            private readonly ConversationMetadata _address;
            internal JournalEventReader(ConversationJournal journal, ConversationMetadata address)
            { _journal = journal; _address = address; }

            public SessionEventSlice Read(SessionEventPosition after, SessionEventPosition through, int limit,
                CancellationToken cancellationToken)
            {
                if (after == null) throw new ArgumentNullException(nameof(after));
                if (limit < 1 || limit > 100) throw new ArgumentOutOfRangeException(nameof(limit));
                cancellationToken.ThrowIfCancellationRequested();
                if (after.SessionId != _address.Id || through != null && through.SessionId != _address.Id)
                    return new SessionEventSlice { Reason = ConversationResyncReason.DifferentSession };
                if (after.Sequence < 1 || through != null && after.Sequence > through.Sequence)
                    return new SessionEventSlice { Reason = ConversationResyncReason.InvalidCursor };
                return _journal.Access((db, cursor) => {
                    var current = _journal.TextChat(db, _address.Id);
                    if (current == null) return new SessionEventSlice { Reason = ConversationResyncReason.HistoryUnavailable };
                    RequireChatRevision(new ConversationMetadata(_address.Id, current.Position.Sequence, _address.Host,
                        _address.DocumentKey, _address.WorkspaceId, _address.DocumentAuthorityId), current);
                    var highWater = through ?? FeedPosition(current);
                    if (!MatchesAnchor(db, after, current) || !MatchesAnchor(db, highWater, current))
                        return new SessionEventSlice { Reason = ConversationResyncReason.HistoryChanged };
                    var operations = new List<string>();
                    using (var command = _journal.Command(db, "SELECT operation FROM chat_write WHERE stream=$stream AND chat=$chat AND sequence>$after AND sequence<=$through ORDER BY sequence LIMIT $limit"))
                    {
                        command.Parameters.AddWithValue("$chat", _address.Id); command.Parameters.AddWithValue("$after", after.Sequence);
                        command.Parameters.AddWithValue("$through", highWater.Sequence); command.Parameters.AddWithValue("$limit", limit);
                        using (var reader = command.ExecuteReader()) while (reader.Read()) operations.Add(reader.GetString(0));
                    }
                    var result = new SessionEventSlice { HighWater = highWater, Next = after };
                    var notices = new List<ConversationEventNotice>();
                    foreach (var operation in operations)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var receipt = _journal.FindChatWrite(db, operation);
                        if (receipt == null || receipt.Fact.Card.ChatId != _address.Id ||
                            receipt.Position.Sequence <= result.Next.Sequence || receipt.Position.Sequence > highWater.Sequence)
                            throw new System.IO.InvalidDataException("Event locator does not identify the requested chat page.");
                        notices.Add(FeedNotice(receipt)); result.Next = FeedPosition(receipt);
                    }
                    if (notices.Count < limit && result.Next.Sequence != highWater.Sequence)
                        throw new System.IO.InvalidDataException("Event page does not reach its canonical high water.");
                    result.Events = notices;
                    return result;
                }, cancellationToken);
            }

            private bool MatchesAnchor(SqliteConnection db, SessionEventPosition anchor, ConversationChatReceipt current)
            {
                if (anchor.Sequence == current.Position.Sequence) return anchor.Hash == current.Position.Hash;
                using (var command = _journal.Command(db, "SELECT operation FROM chat_write WHERE stream=$stream AND chat=$chat AND sequence=$sequence"))
                {
                    command.Parameters.AddWithValue("$chat", _address.Id); command.Parameters.AddWithValue("$sequence", anchor.Sequence);
                    var operation = command.ExecuteScalar() as string;
                    var receipt = operation == null ? null : _journal.FindChatWrite(db, operation);
                    return receipt != null && receipt.Fact.Card.ChatId == _address.Id &&
                        receipt.Position.Sequence == anchor.Sequence && receipt.Position.Hash == anchor.Hash;
                }
            }
        }

        private static SessionEventPosition FeedPosition(ConversationChatReceipt receipt)
        { return new SessionEventPosition(receipt.Fact.Card.ChatId, receipt.Position.Sequence, receipt.Position.Hash); }

        private static ConversationEventNotice FeedNotice(ConversationChatReceipt receipt)
        {
            var fact = receipt.Fact; var model = fact.ModelEvent;
            var changes = new List<string>();
            if (model == null && fact.Control == null)
            {
                // Invalidation categories, not legacy replay operations. A native
                // chat frame carries its metadata/context plus bounded message delta.
                changes.Add(SessionOperationTypes.SessionMetadataSet); changes.Add(SessionOperationTypes.ContextSet);
                if (fact.Messages.Count > 0) changes.Add(SessionOperationTypes.MessageUpdated);
                if (fact.Run != null) changes.Add(SessionOperationTypes.RunUpdated);
            }
            return new ConversationEventNotice {
                Sequence = receipt.Position.Sequence, EventId = receipt.Operation, CreatedUtc = fact.CreatedUtc,
                Type = fact.Control != null ? SessionEventTypes.ConversationInputChanged : model == null
                    ? "chat.committed" : SessionEventDescriptors.For(model.Kind).Type,
                RunId = fact.Control != null ? fact.Input?.RunId : model?.RunId ?? fact.Run?.RunId,
                TurnId = fact.Control != null ? fact.Input?.RunId : model?.TurnId ?? fact.Run?.TurnId,
                StepId = model?.StepId, Operations = changes
            };
        }
    }
}
