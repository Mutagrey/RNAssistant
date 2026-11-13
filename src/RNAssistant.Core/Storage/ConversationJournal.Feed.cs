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
                    using (var command = _journal.Command(db, ChatEventQuery + " ORDER BY sequence LIMIT $limit"))
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
                        if (receipt?.Fact.Card.ChatId != _address.Id) receipt = null;
                        var spawn = receipt == null ? _journal.FindSpawn(db, operation) : null;
                        var peer = receipt == null && spawn == null ? _journal.FindDelivery(db, "operation", operation) : null;
                        var control = receipt == null && spawn == null && peer == null ? _journal.FindControl(db, operation) : null;
                        var position = receipt?.Position ?? spawn?.Position ?? peer?.Position ?? control?.Position;
                        if ((spawn != null ? spawn.Fact.Chat == null || spawn.Fact.Sender != _address.Id :
                            receipt == null && !DeliveryAffects(peer, _address.Id) && !ControlAffects(control, _address.Id)) ||
                            position.Sequence <= result.Next.Sequence || position.Sequence > highWater.Sequence)
                            throw new System.IO.InvalidDataException("Event locator does not identify the requested chat page.");
                        if (receipt != null) notices.Add(FeedNotice(receipt));
                        else if (spawn != null) notices.Add(new ConversationEventNotice { Sequence = position.Sequence,
                            EventId = spawn.Operation, CreatedUtc = spawn.Fact.Child.CreatedUtc, Type = "agent.spawned",
                            RunId = spawn.Fact.RunId, Operations = new string[0] });
                        else if (control != null) notices.Add(new ConversationEventNotice { Sequence = position.Sequence,
                            EventId = control.Operation, CreatedUtc = control.Fact.ChangedUtc,
                            Type = control.Fact.ChatId == _address.Id ? SessionEventTypes.ConversationInputChanged : "agent.control",
                            RunId = control.Fact.ChatId == _address.Id ? control.Fact.RunId : control.Fact.Origin.RunId, Operations = new string[0] });
                        else notices.Add(new ConversationEventNotice { Sequence = position.Sequence, EventId = peer.Operation,
                            CreatedUtc = peer.Fact.CreatedUtc, Type = peer.Fact.Recipient == _address.Id ? SessionEventTypes.ConversationInputChanged : "agent.accepted",
                            RunId = peer.Fact.Sender == _address.Id ? peer.Fact.RunId : null, Operations = new string[0] });
                        result.Next = new SessionEventPosition(_address.Id, position.Sequence, position.Hash);
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
                var position = _journal.ChatPosition(db, _address.Id, anchor.Sequence);
                return position != null && position.Hash == anchor.Hash;
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
