using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;

namespace RNAssistant.Core.Storage
{
    public sealed partial class ChatStore
    {
        private long _finishEvidenceEventReads;
        internal long FinishEvidenceEventReads { get { return Interlocked.Read(ref _finishEvidenceEventReads); } }

        // Locators in the existing disposable projection, never outcome authority.
        // Admission rereads and verifies each selected canonical JSONL record.
        private sealed class FinishFactLocator
        {
            public string CallId, Kind, RunId, Reference, MessageId, Hash;
            public long Sequence, Offset, NextOffset;
            public int Position;
        }

        private void IndexFinishFacts(SqliteConnection connection, SqliteTransaction transaction,
            SessionEvent item, long nextOffset, StorageProtector protector)
        {
            foreach (var fact in ConversationFinishEvidenceAdmission.Facts(item))
            {
                var locator = new FinishFactLocator { CallId = fact.CallId, Kind = fact.Kind,
                    RunId = fact.RunId, Reference = fact.Reference, MessageId = fact.Message.Id,
                    Sequence = item.Sequence, Hash = item.Hash, Offset = item.StorageByteOffset,
                    NextOffset = nextOffset, Position = fact.Position };
                if (string.IsNullOrEmpty(locator.CallId) || string.IsNullOrEmpty(locator.RunId) ||
                    string.IsNullOrEmpty(locator.MessageId)) throw new JsonException("Tool fact lacks canonical identity.");
                // A confirmation continuation may have another start. The first
                // canonical start suffices; terminals/calls cannot be replaced.
                var insert = fact.Kind == SessionOperationTypes.ToolExecutionStarted ? "INSERT OR IGNORE" : "INSERT";
                ExecuteSql(connection, transaction, insert + " INTO finish_facts(call_id,kind,run_id,reference,payload) VALUES($p0,$p1,$p2,$p3,$p4)",
                    locator.CallId, locator.Kind, locator.RunId, locator.Reference,
                    SealProjection(item.SessionId, "finish/" + locator.CallId + "/" + locator.Kind, JsonConvert.SerializeObject(locator), protector));
            }
        }

        internal FinishEvidenceSnapshot ReadFinishEvidence(ChatSession session, string runId, IReadOnlyList<string> references)
        {
            if (session == null || !session.UsesCurrentProtocol || string.IsNullOrEmpty(runId) || references == null)
                throw new InvalidOperationException("Finish evidence requires the current reference contract and run.");
            var path = GetSessionPath(session.Host, session.DocumentKey, session.Id);
            using (AcquirePersistenceRead())
            using (AcquireDocumentLock(session.Host, session.DocumentKey))
                return ReadSqliteProjection(path, connection => {
                    var cursor = EnsureSqliteProjection(connection, path);
                    if (cursor == null || cursor.IncompleteTail || cursor.SessionId != session.Id ||
                        cursor.Sequence != session.Revision || cursor.HeadHash != session.StorageHeadHash)
                        throw new ChatConcurrencyException("Canonical finish evidence is not current or complete.");
                    var locators = new Dictionary<string, FinishFactLocator>(StringComparer.Ordinal);
                    var protector = Protection();
                    Action<string, object[]> select = (condition, values) => {
                        using (var command = connection.CreateCommand())
                        {
                            command.CommandText = "SELECT call_id,kind,run_id,reference,payload FROM finish_facts WHERE " + condition;
                            for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue("$p" + i, values[i]);
                            using (var reader = command.ExecuteReader())
                            while (reader.Read())
                            {
                                var id = reader.GetString(0); var kind = reader.GetString(1);
                                FinishFactLocator locator;
                                try { locator = JsonConvert.DeserializeObject<FinishFactLocator>(OpenProjection(session.Id,
                                    "finish/" + id + "/" + kind, (byte[])reader[4], protector)); }
                                catch (Exception ex) when (IsInvalidProjectionValue(ex)) { throw new ProjectionDataException(ex); }
                                if (locator == null || locator.CallId != id || locator.Kind != kind || locator.RunId != reader.GetString(2) ||
                                    locator.Reference != (reader.IsDBNull(3) ? null : reader.GetString(3)))
                                    throw new ProjectionDataException(new JsonException("Finish locator binding disagrees with its index."));
                                locators[id + "\n" + kind] = locator;
                            }
                        }
                    };
                    Action<string, IEnumerable<string>> selectKeys = (column, keys) => {
                        var values = keys.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                        for (var offset = 0; offset < values.Length; offset += 200)
                        {
                            var batch = values.Skip(offset).Take(200).Cast<object>().ToArray();
                            select(column + " IN (" + string.Join(",", Enumerable.Range(0, batch.Length).Select(i => "$p" + i)) + ")", batch);
                        }
                    };
                    select("run_id=$p0", new object[] { runId });
                    selectKeys("reference", references);
                    selectKeys("call_id", locators.Values.Select(item => item.CallId).ToArray());
                    var positions = locators.Values.GroupBy(item => item.Sequence).ToDictionary(group => group.Key,
                        group => (ISet<int>)new HashSet<int>(group.Select(item => item.Position)));
                    var events = new Dictionary<long, SessionEvent>();
                    var canonicalFacts = new Dictionary<long, Dictionary<int, ConversationFinishEvidenceAdmission.Fact>>();
                    var retained = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var locator in locators.Values)
                    {
                        SessionEvent item;
                        if (!events.TryGetValue(locator.Sequence, out item))
                        {
                            item = ReadValidatedEventAtOffset(path, session.Id, locator.Sequence, locator.Hash,
                                locator.Offset, locator.NextOffset, cursor.ByteLength);
                            if (item == null) throw new ProjectionDataException(new JsonException("Finish locator does not identify a canonical event."));
                            HydrateEventData(item, protector);
                            events.Add(item.Sequence, item);
                            canonicalFacts.Add(item.Sequence, ConversationFinishEvidenceAdmission.Facts(item, positions[item.Sequence])
                                .ToDictionary(entry => entry.Position));
                            Interlocked.Increment(ref _finishEvidenceEventReads);
                        }
                        ConversationFinishEvidenceAdmission.Fact fact;
                        canonicalFacts[item.Sequence].TryGetValue(locator.Position, out fact);
                        if (fact == null || fact.CallId != locator.CallId || fact.Kind != locator.Kind || fact.RunId != locator.RunId ||
                            fact.Reference != locator.Reference || fact.Message.Id != locator.MessageId)
                            throw new ProjectionDataException(new JsonException("Finish locator disagrees with the canonical tool fact."));
                        if (Convert.ToInt64(Scalar(connection, null, "SELECT COUNT(*) FROM messages WHERE item_id=$p0", locator.MessageId)) == 1)
                            retained.Add(locator.MessageId);
                    }
                    var after = CaptureStorageFileState(path);
                    if (after == null || after.ByteLength != cursor.ByteLength || after.LastWriteUtcTicks != cursor.LastWriteUtcTicks)
                        throw new ChatConcurrencyException("Chat storage changed during finish evidence selection.");
                    return new FinishEvidenceSnapshot(new SessionEventPosition(session.Id, cursor.Sequence, cursor.HeadHash),
                        canonicalFacts.OrderBy(item => item.Key).SelectMany(item => item.Value.Values).ToArray(), retained.ToArray());
                });
        }
    }
}
