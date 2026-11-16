using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Services;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;

namespace RNAssistant.Core.Storage
{
    public sealed partial class ChatStore
    {
        internal ChatOverview ReadChatOverview(ChatSession scope, string chatId, bool metadataOnly = false)
        {
            var path = GetSessionPath(scope.Host, scope.DocumentKey, chatId);
            using (AcquirePersistenceRead())
            using (AcquireDocumentPathLockForRead(path))
            {
                if (!File.Exists(path)) throw ChatOverviewProjection.Unavailable();
                return ReadSqliteProjection(path, connection =>
                {
                    for (var attempt = 0; attempt < 2; attempt++)
                    {
                        var cursor = EnsureSqliteProjection(connection, path);
                        if (cursor == null || cursor.SessionId != chatId) throw ChatOverviewProjection.Unavailable();
                        var root = cursor.Root;
                        var source = new ChatSession {
                            Id = cursor.SessionId, Revision = cursor.Sequence,
                            FormatVersion = (int?)root["FormatVersion"] ?? 0,
                            ReferenceContractVersion = (int?)root["ReferenceContractVersion"] ?? 0,
                            Host = (string)root["Host"], DocumentKey = (string)root["DocumentKey"],
                            WorkspaceId = (string)root["WorkspaceId"], DocumentAuthorityId = (string)root["DocumentAuthorityId"],
                            Title = (string)root["Title"], UpdatedUtc = (DateTime?)root["UpdatedUtc"] ?? default(DateTime),
                            TitleAssignedByAgent = (bool?)root["TitleAssignedByAgent"] ?? false
                        };
                        if (!ChatOverviewProjection.SameScope(scope, source)) throw ChatOverviewProjection.Unavailable();
                        var overview = new ChatOverview { Source = source, AgentId = AgentChatAddress.IdFor(chatId),
                            Title = source.Title, UpdatedUtc = source.UpdatedUtc, ProtocolCompatible = source.UsesCurrentProtocol,
                            MessageCount = cursor.MessageCount };
                        using (var transaction = connection.BeginTransaction(true))
                        {
                            if (!SqliteCursorMatches(connection, transaction, cursor)) continue;
                            if (!metadataOnly && source.UsesCurrentProtocol)
                                ReadOverviewExcerpts(connection, transaction, cursor, overview);
                        }
                        if (!source.UsesCurrentProtocol) overview.SummaryStatus = "incompatible_stream";
                        else if (!metadataOnly) ChatOverviewProjection.ReadSummary(overview, DocumentArtifacts);
                        else overview.SummaryStatus = "not_requested";
                        return overview;
                    }
                    throw new ChatConcurrencyException("Chat index changed during overview reading.");
                });
            }
        }

        private void ReadOverviewExcerpts(SqliteConnection connection, SqliteTransaction transaction,
            SqliteCursor cursor, ChatOverview overview)
        {
            const int rowBytes = 65536;
            var remainingBytes = 1048576;
            var protector = Protection();
            var excerpts = new List<ChatOverviewExcerpt>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                // At most 36 rows, independent of history length. Do not load a giant
                // tool/media row merely to discover that it is unsuitable for preview.
                command.CommandText = "SELECT item_id, CASE WHEN length(payload)<=$max THEN payload ELSE NULL END " +
                    "FROM messages WHERE item_id IN (SELECT item_id FROM messages ORDER BY ordinal LIMIT 4) " +
                    "OR item_id IN (SELECT item_id FROM messages ORDER BY ordinal DESC LIMIT 32) ORDER BY ordinal";
                command.Parameters.AddWithValue("$max", rowBytes);
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                    {
                        overview.SampledMessages++;
                        var payload = reader[1] as byte[];
                        if (payload == null || payload.Length > remainingBytes)
                        { overview.SkippedOversizedMessages++; continue; }
                        remainingBytes -= payload.Length;
                        var value = ReadSqliteItem(cursor.SessionId, "messages", reader.GetString(0), payload, protector);
                        var message = value.ToObject<ChatMessage>();
                        var item = ChatOverviewProjection.Excerpt(message);
                        if (item != null) excerpts.Add(item);
                    }
            }
            ChatOverviewProjection.SelectExcerpts(overview, excerpts);
        }

        internal void SetAgentTitle(ChatSession session, string title)
        {
            CommitScalarMetadata(session, new JObject { ["Title"] = title, ["TitleAssignedByAgent"] = true });
            session.Title = title;
            session.TitleAssignedByAgent = true;
        }
    }
}
