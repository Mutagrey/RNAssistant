using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Storage
{
    internal sealed partial class ConversationJournal
    {
        ChatOverview IConversationStore.ReadOverview(ChatSession scope, string id, bool metadataOnly)
        {
            RequireTextRoot();
            using (RuntimeStorageActivity.Acquire(_paths))
            {
                var selected = Access((db, cursor) => {
                    var current = TextChat(db, id);
                    if (current == null) throw ChatOverviewProjection.Unavailable();
                    var card = current.Fact.Card; var address = card.Address;
                    var source = new ChatSession { Id = id, Revision = current.Position.Sequence,
                        Host = address.Host, DocumentKey = address.DocumentKey, WorkspaceId = address.WorkspaceId,
                        DocumentAuthorityId = address.DocumentAuthorityId, Title = card.Title, UpdatedUtc = current.UpdatedUtc,
                        TitleAssignedByAgent = card.TitleAssignedByAgent };
                    if (!ChatOverviewProjection.SameScope(scope, source)) throw ChatOverviewProjection.Unavailable();
                    var overview = new ChatOverview { Source = source, AgentId = AgentChatAddress.IdFor(id),
                        Title = source.Title, UpdatedUtc = source.UpdatedUtc, ProtocolCompatible = true,
                        MessageCount = current.Fact.MessageCount };
                    var ids = new List<Tuple<string, string>>();
                    if (!metadataOnly)
                        using (var command = Command(db, "SELECT id,operation,ordinal FROM chat_message WHERE stream=$stream AND chat=$chat AND ordinal>=0 AND ordinal<4 " +
                            "UNION ALL SELECT id,operation,ordinal FROM chat_message WHERE stream=$stream AND chat=$chat AND ordinal>=$tail ORDER BY ordinal"))
                        {
                            command.Parameters.AddWithValue("$chat", id);
                            command.Parameters.AddWithValue("$tail", Math.Max(4, current.Fact.MessageCount - 32));
                            using (var reader = command.ExecuteReader())
                                while (reader.Read()) ids.Add(Tuple.Create(reader.GetString(0), reader.GetString(1)));
                        }
                    if (ids.Count != (metadataOnly ? 0 : Math.Min(current.Fact.MessageCount, 36)))
                        throw new InvalidDataException("Overview sample contains missing ordinals.");
                    var writes = ids.Select(item => item.Item2).Distinct(StringComparer.Ordinal)
                        .ToDictionary(operation => operation, operation => FindChatWrite(db, operation));
                    var entries = ids.Select(item => {
                        var write = writes[item.Item2]; RequireHistoryOwner(db, id, write);
                        return write.Fact.Messages.Single(message => message.Header.Id == item.Item1);
                    }).ToArray();
                    return Tuple.Create(overview, entries);
                }, CancellationToken.None);
                var result = selected.Item1;
                if (metadataOnly) { result.SummaryStatus = "not_requested"; return result; }
                var remaining = 1048576L;
                var excerpts = new List<ChatOverviewExcerpt>();
                foreach (var entry in selected.Item2)
                {
                    result.SampledMessages++;
                    if (entry.Body.ByteLength > 65536 || entry.Body.ByteLength > remaining)
                    { result.SkippedOversizedMessages++; continue; }
                    // Tool/media/activity and reasoning bodies stay cold; original
                    // locators also work for a fork after its ancestors are deleted.
                    if (!ChatOverviewProjection.IsVisible(entry.Header)) continue;
                    remaining -= entry.Body.ByteLength;
                    var message = entry.Header.CopyForContext(); message.Content = ReadChatBody(entry.Body);
                    var excerpt = ChatOverviewProjection.Excerpt(message);
                    if (excerpt != null) excerpts.Add(excerpt);
                }
                ChatOverviewProjection.SelectExcerpts(result, excerpts);
                ChatOverviewProjection.ReadSummary(result, ResourceStore());
                return result;
            }
        }

        void IConversationStore.SetAgentTitle(ChatSession session, string title)
        {
            session.Title = title; session.TitleAssignedByAgent = true;
            ((IConversationStore)this).Commit(session, new ConversationCommit());
        }
    }
}
