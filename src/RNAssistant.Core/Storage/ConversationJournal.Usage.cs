using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    internal sealed partial class ConversationJournal
    {
        // Attribution to retained chat facts, not exclusive ownership of shared
        // frames/blobs. Resource-journal bodies remain with their resource owner.
        private void ApplyStorageUsage(SqliteConnection db, SqliteTransaction tx,
            JournalRecord record, JournalCursor position)
        {
            var chats = UsageChats(db, tx, record).Where(id => id != null).Distinct(StringComparer.Ordinal).ToArray();
            if (chats.Length == 0) return;
            var references = CanonicalReferences(record).GroupBy(reference => reference.Sha256, StringComparer.Ordinal)
                .Select(group => {
                    var reference = group.First();
                    if (group.Any(item => item.ByteLength != reference.ByteLength)) throw new InvalidDataException("Conflicting retained CAS lengths.");
                    return reference;
                }).ToArray();
            foreach (var chat in chats)
            {
                using (var command = Command(db, "UPDATE chat_state SET journal_bytes=MIN(9223372036854775807-$bytes,journal_bytes)+$bytes WHERE stream=$stream AND chat=$chat", tx))
                {
                    command.Parameters.AddWithValue("$chat", chat); command.Parameters.AddWithValue("$bytes", position.End - position.Start);
                    if (command.ExecuteNonQuery() == 0) continue; // Primitive/deleted identities have no Store catalog card.
                }
                foreach (var reference in references)
                {
                    int added;
                    using (var command = Command(db, "INSERT INTO chat_blob VALUES($stream,$chat,$hash,$length) ON CONFLICT(stream,chat,hash) DO NOTHING", tx))
                    {
                        command.Parameters.AddWithValue("$chat", chat); command.Parameters.AddWithValue("$hash", reference.Sha256);
                        command.Parameters.AddWithValue("$length", reference.ByteLength); added = command.ExecuteNonQuery();
                    }
                    if (added == 0)
                    {
                        using (var command = Command(db, "SELECT logical_bytes FROM chat_blob WHERE stream=$stream AND chat=$chat AND hash=$hash", tx))
                        {
                            command.Parameters.AddWithValue("$chat", chat); command.Parameters.AddWithValue("$hash", reference.Sha256);
                            if (Convert.ToInt64(command.ExecuteScalar()) != reference.ByteLength) throw new InvalidDataException("Retained CAS length changed.");
                        }
                        continue;
                    }
                    using (var command = Command(db, "UPDATE chat_state SET blob_count=MIN(2147483647,blob_count+1),logical_bytes=MIN(9223372036854775807-$length,logical_bytes)+$length WHERE stream=$stream AND chat=$chat", tx))
                    {
                        command.Parameters.AddWithValue("$chat", chat); command.Parameters.AddWithValue("$length", reference.ByteLength);
                        command.ExecuteNonQuery();
                    }
                }
            }
            Fault?.Invoke("after_usage_index");
        }

        private IEnumerable<string> UsageChats(SqliteConnection db, SqliteTransaction tx, JournalRecord record)
        {
            if (IsChatDeletion(record.Kind)) return new string[0];
            if (IsChatMove(record.Kind))
            {
                var ids = new List<string>();
                using (var command = Command(db, "SELECT chat FROM chat_write WHERE stream=$stream AND operation=$operation", tx))
                {
                    command.Parameters.AddWithValue("$operation", record.Operation);
                    using (var reader = command.ExecuteReader()) while (reader.Read()) ids.Add(reader.GetString(0));
                }
                return ids;
            }
            if (record.Kind == "chat.committed") return new[] { DecodeChatCommit(record).Card.ChatId };
            if (record.Kind == "chat.selected") return new[] { DecodeChatSelection(record).ChatId };
            if (record.Kind == "chat.created") return new[] { DecodeCard(record).ChatId };
            if (record.Kind == "agent.spawned") { var spawn = DecodeSpawn(record); return new[] { spawn.Sender, spawn.Child.ChatId }; }
            if (record.Kind == "agent.accepted") { var delivery = DecodeDelivery(record); return new[] { delivery.Sender, delivery.Recipient }; }
            if (record.Kind == "input.changed") return new[] { DecodeInput(record).Recipient };
            if (record.Kind == "queue.control") { var control = DecodeControl(record); return new[] { control.ChatId, control.Origin?.Sender }; }
            if (record.Kind == "run.checkpoint") return new[] { DecodeRun(record).ChatId };
            if (record.Kind == "tool.accepted") return new[] { DecodeCall(record).ChatId };
            if (record.Kind == "resource.linked") return new[] { DecodeLink(record).ChatId };
            return new[] { Decode(record).ChatId };
        }

        private void ReadStorageUsage(SqliteConnection db, ChatSessionHeader header, ChatBlobStore.StorageSizeSnapshot sizes)
        {
            using (var command = Command(db, "SELECT journal_bytes,blob_count,logical_bytes FROM chat_state WHERE stream=$stream AND chat=$chat"))
            {
                command.Parameters.AddWithValue("$chat", header.Id);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read()) throw new InvalidDataException("Chat storage counters are unavailable.");
                    header.JsonlByteLength = reader.GetInt64(0); header.CasBlobCount = reader.GetInt32(1);
                    header.CasLogicalByteLength = reader.GetInt64(2);
                }
            }
            if (sizes != null)
                using (var command = Command(db, "SELECT hash FROM chat_blob WHERE stream=$stream AND chat=$chat"))
                {
                    command.Parameters.AddWithValue("$chat", header.Id);
                    using (var reader = command.ExecuteReader()) while (reader.Read())
                    {
                        long bytes;
                        if (sizes.TryGetStoredByteLength(reader.GetString(0), out bytes))
                            header.CasStoredByteLength = header.CasStoredByteLength > long.MaxValue - bytes ? long.MaxValue : header.CasStoredByteLength + bytes;
                        else if (header.CasMissingBlobCount < int.MaxValue) header.CasMissingBlobCount++;
                    }
                }
            header.StorageWarningLevel = ChatStorageUsagePolicy.GetWarningLevel(header.JsonlByteLength,
                header.CasLogicalByteLength, header.CasStoredByteLength, header.CasMissingBlobCount, 0);
        }
    }
}
