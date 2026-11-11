using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.ModelProtocol;
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
                if (!File.Exists(path)) throw OverviewUnavailable();
                return ReadSqliteProjection(path, connection =>
                {
                    for (var attempt = 0; attempt < 2; attempt++)
                    {
                        var cursor = EnsureSqliteProjection(connection, path);
                        if (cursor == null || cursor.SessionId != chatId) throw OverviewUnavailable();
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
                        if (!SameOverviewScope(scope, source)) throw OverviewUnavailable();
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
                        else if (!metadataOnly) ReadOverviewSummary(overview);
                        else overview.SummaryStatus = "not_requested";
                        return overview;
                    }
                    throw new ChatConcurrencyException("Chat index changed during overview reading.");
                });
            }
        }

        internal static bool SameOverviewScope(ChatSession left, ChatSession right)
        {
            return left != null && right != null &&
                string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(left.DocumentKey, right.DocumentKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(left.WorkspaceId, right.WorkspaceId, StringComparison.Ordinal) &&
                string.Equals(left.DocumentAuthorityId, right.DocumentAuthorityId, StringComparison.Ordinal);
        }

        private static ResourceRequestException OverviewUnavailable()
        { return new ResourceRequestException("The requested chat is unavailable in this scope.", "chat_unavailable", false); }

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
                        if (message.ExcludeFromModelContext || message.RuntimeProjection || message.Activity != null ||
                            message.ToolCallId != null || message.ToolName != null || message.ToolCalls?.Count > 0)
                            continue;
                        string text = null;
                        if (message.Role == "user" && !message.ProtocolMessage) text = message.Content;
                        else if (message.Role == "assistant" && message.ProtocolMessage)
                        {
                            var accepted = ConversationResponseHistoryReader.Read(message);
                            if (accepted.Success && accepted.Response.Action == ConversationResponse.FinishAction)
                                text = accepted.Response.Message;
                        }
                        else if (message.Role == "assistant" && !message.ProtocolMessage) text = message.Content;
                        if (string.IsNullOrWhiteSpace(text)) continue;
                        excerpts.Add(new ChatOverviewExcerpt { Role = message.Role, CreatedUtc = message.CreatedUtc,
                            FromAgent = message.AgentCommunication?.Sender?.AgentId,
                            Text = OverviewClip(text, 600), Truncated = text.Length > 600 });
                    }
            }
            overview.Excerpts.AddRange(excerpts.Count <= 6 ? excerpts : excerpts.Take(2).Concat(excerpts.Skip(excerpts.Count - 4)));
        }

        private void ReadOverviewSummary(ChatOverview overview)
        {
            try
            {
                // ContextCheckpoints is a disposable message-derived projection, not
                // scalar chat metadata. Only the existing published resource head is read.
                var reference = DocumentArtifacts.PublishedContext(overview.Source);
                if (reference == null) return;
                var artifact = DocumentArtifacts.Read(ConversationMetadata.From(overview.Source), reference, includeBody: false);
                if (!artifact.ContentByteLength.HasValue || artifact.ContentByteLength > 2097152)
                { overview.SummaryStatus = "size_unavailable_or_too_large"; return; }
                artifact = DocumentArtifacts.Read(ConversationMetadata.From(overview.Source), reference, includeBody: true);
                var published = JsonConvert.DeserializeObject<SharedContextDocument>(artifact.InlineText);
                if (published?.SourceChatId != overview.Source.Id || published.Version != ContextCheckpoint.CurrentPromptVersion)
                { overview.SummaryStatus = "unavailable"; return; }
                overview.SharedContextReference = reference;
                overview.SummaryUtc = artifact.CreatedUtc;
                overview.SummaryClaims.AddRange((published.Claims ?? new List<StructuredContextClaim>())
                    .Where(claim => claim?.SourceRoles?.Count > 0 && claim.SourceRoles.All(role => role == "user" || role == "assistant") &&
                        !string.IsNullOrWhiteSpace(claim.Text)).Take(4).Select(claim => OverviewClip(claim.Text, 400)));
                overview.SummaryStatus = "published_excerpt";
            }
            catch (Exception error) when (error is IOException || error is InvalidOperationException || error is JsonException || error is ArgumentException)
            { overview.SummaryStatus = "unavailable"; }
        }

        private static string OverviewClip(string text, int length)
        { return text.Length <= length ? text : text.Substring(0, char.IsHighSurrogate(text[length - 1]) ? length - 1 : length) + "…"; }

        internal void SetAgentTitle(ChatSession session, string title)
        {
            CommitScalarMetadata(session, new JObject { ["Title"] = title, ["TitleAssignedByAgent"] = true });
            session.Title = title;
            session.TitleAssignedByAgent = true;
        }
    }
}
