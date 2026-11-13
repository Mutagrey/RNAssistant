using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;

namespace RNAssistant.Core.Storage
{
    internal sealed partial class ConversationJournal
    {
        AgentAssignmentPage IEventStore.ReadAgentAssignments(ConversationMetadata session, bool incoming, string afterRequest)
        {
            if (afterRequest != null && (string.IsNullOrWhiteSpace(afterRequest) || afterRequest.Length > 64))
                throw new ArgumentException("Expected a bounded assignment cursor.");
            RequireTextRoot();
            using (RuntimeStorageActivity.Acquire(_paths))
            {
                var truncated = false;
                var rows = Access((db, cursor) => {
                    RequireChatRevision(session, TextChat(db, session.Id));
                    var selected = new List<Tuple<string, string>>();
                    using (var command = Command(db, "SELECT message,operation FROM agent_delivery WHERE stream=$stream AND " +
                        (incoming ? "recipient" : "sender") + "=$chat AND assignment=1 AND message>$after AND sequence<=$through ORDER BY message LIMIT 51"))
                    {
                        command.Parameters.AddWithValue("$chat", session.Id); command.Parameters.AddWithValue("$after", afterRequest ?? "");
                        command.Parameters.AddWithValue("$through", session.Revision);
                        using (var reader = command.ExecuteReader())
                            while (reader.Read()) selected.Add(Tuple.Create(reader.GetString(0), reader.GetString(1)));
                    }
                    truncated = selected.Count > 50;
                    return selected.Take(50).Select(row => {
                        var request = FindDelivery(db, "operation", row.Item2);
                        var fact = request?.Fact;
                        if (fact?.Input == null || !fact.Assignment || fact.MessageId != row.Item1 ||
                            (incoming ? fact.Recipient : fact.Sender) != session.Id || request.Position.Sequence > session.Revision ||
                            afterRequest != null && string.CompareOrdinal(fact.MessageId, afterRequest) <= 0)
                            throw new InvalidDataException("Assignment locator does not identify this chat page.");
                        AgentMessageReceipt report;
                        using (var command = Command(db, "SELECT operation FROM agent_delivery WHERE stream=$stream AND reply_to=$request AND resolution IS NOT NULL AND sequence<=$through"))
                        {
                            command.Parameters.AddWithValue("$request", fact.MessageId); command.Parameters.AddWithValue("$through", session.Revision);
                            var operation = command.ExecuteScalar() as string;
                            report = operation == null ? null : FindDelivery(db, "operation", operation);
                            if (operation != null && (report?.Fact.Input == null || !report.Fact.Resolution.HasValue ||
                                report.Fact.ReplyTo != fact.MessageId || report.Fact.Sender != fact.Recipient || report.Fact.Recipient != fact.Sender ||
                                report.Fact.ChainId != fact.ChainId || report.Fact.Root != fact.Root || report.Position.Sequence > session.Revision))
                                throw new InvalidDataException("Resolution locator does not identify the exact assignment peers.");
                        }
                        // Only this chat's own input status is exposed. The report
                        // body remains in the Inbox behind its FIFO/policy boundary.
                        var message = incoming ? fact.MessageId : report?.Fact.MessageId;
                        var input = message == null ? null : InputState(db, message, session.Revision);
                        if (message != null && (input == null || input.Recipient != session.Id || input.Header == null))
                            throw new InvalidDataException("Assignment input is outside this chat.");
                        var view = new AgentAssignmentView { RequestId = fact.MessageId,
                            AgentId = AgentChatAddress.IdFor(incoming ? fact.Sender : fact.Recipient), InputStatus = input?.Status,
                            State = report != null ? AgentAssignmentState.ResolutionReceived : input?.Status == ConversationInputStatus.Removed
                                ? AgentAssignmentState.Removed : AgentAssignmentState.Open,
                            ReportedOutcome = report?.Fact.Resolution, ResolutionMessageId = report?.Fact.MessageId };
                        return Tuple.Create(view, fact.Body);
                    }).ToArray();
                }, CancellationToken.None);
                // Hydrate only request bodies on this page, outside the index lock.
                // This preserves the existing Trim/surrogate-safe excerpt contract.
                foreach (var row in rows) row.Item1.Summary = AgentAssignmentView.Excerpt(ReadChatBody(row.Item2));
                return new AgentAssignmentPage { Direction = incoming ? "incoming" : "outgoing", Items = rows.Select(row => row.Item1).ToArray(),
                    Truncated = truncated, NextRequest = truncated ? rows[rows.Length - 1].Item1.RequestId : null };
            }
        }
    }
}
