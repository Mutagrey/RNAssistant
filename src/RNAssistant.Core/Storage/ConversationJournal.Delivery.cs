using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Storage
{
    // The same scope journal accepts local delivery, receipt and membership in
    // one frame. Scheduling, live run leases and UI remain application concerns.
    internal sealed partial class ConversationJournal
    {
        internal ConversationCard CreateChat(string operation, string chat, string title, ConversationRunPolicy policy,
            CancellationToken token = default(CancellationToken), ModelRunMetadata model = null)
        {
            var fact = new ConversationCard(chat, title, policy, DateTime.UtcNow, model);
            var fingerprint = CardFingerprint(fact);
            return Access((db, cursor) => {
                var prior = Card(db, chat);
                if (prior != null)
                {
                    if (prior.Item2 != operation || CardFingerprint(prior.Item1) != fingerprint)
                        throw new InvalidOperationException("Chat creation identity was reused.");
                    return prior.Item1;
                }
                var record = DeliveryRecord(operation, "chat.created", fact, fingerprint);
                ValidateDeliveryRecord(db, record);
                var position = Journal.Append(cursor, operation, fingerprint, record.Kind, record.Data);
                ApplyAcceptedDelivery(db, record, position);
                return fact;
            }, token);
        }

        internal ConversationCard FindAgent(string alias, CancellationToken token = default(CancellationToken))
        { return Access((db, cursor) => FindAgent(db, alias), token); }

        internal AgentMessageReceipt SendAgent(string operation, string sender, string run, string call, string recipientAlias,
            string root, string chain, int participantLimit, string text, bool expectReply = false, string replyTo = null,
            bool assignment = false, AgentAssignmentOutcome? resolution = null, CancellationToken token = default(CancellationToken),
            string recipientRuntimeKey = null, Action<SqliteConnection, AgentMessageAccepted> validateDispatch = null,
            Action dispatch = null)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 128 * 1024) throw new ArgumentException("Bounded nonempty agent text is required.");
            using (RuntimeStorageActivity.Acquire(_paths, token))
            {
                var body = PayloadRef.FromBlob(_bodies.StoreText(text, "text/plain"));
                return Access((db, cursor) => {
                    // Resolve only the requested alias in this exact document/workspace.
                    var target = FindAgent(db, recipientAlias) ?? throw new InvalidOperationException("Agent is unavailable in this scope.");
                    var source = Card(db, sender)?.Item1 ?? throw new InvalidOperationException("Sender is unavailable in this scope.");
                    var fact = new AgentMessageAccepted(sender, target.ChatId, run, call, root, chain, participantLimit,
                        "m-" + Guid.NewGuid().ToString("N"), body, expectReply, replyTo, assignment, resolution,
                        source.Address == null ? source.Policy : CurrentRun(db, sender)?.Fact.Policy ?? source.Policy, DateTime.UtcNow,
                        recipientRuntimeKey: recipientRuntimeKey);
                    var fingerprint = DeliveryFingerprint(fact);
                    var prior = FindDelivery(db, "operation", operation);
                    if (prior != null)
                    {
                        if (prior.Fingerprint != fingerprint) throw new InvalidOperationException("Delivery operation was reused with different content.");
                        return prior;
                    }
                    fact = PreparePeerInput(db, operation, fingerprint, fact, source, target);
                    var record = DeliveryRecord(operation, "agent.accepted", fact, fingerprint);
                    ValidateDeliveryRecord(db, record);
                    validateDispatch?.Invoke(db, fact);
                    dispatch?.Invoke();
                    var position = Journal.Append(cursor, operation, fingerprint, record.Kind, record.Data);
                    var projected = ApplyAcceptedDelivery(db, record, position);
                    return new AgentMessageReceipt(operation, fingerprint, fact, position, projected);
                }, token);
            }
        }

        internal AgentMessageReceipt AgentReceipt(string operation, CancellationToken token = default(CancellationToken))
        { return Access((db, cursor) => FindDelivery(db, "operation", operation), token); }

        internal AgentMessageReceipt AgentMessage(string message, CancellationToken token = default(CancellationToken))
        { return Access((db, cursor) => FindDelivery(db, "message", message), token); }

        internal string ReadAgentText(AgentMessageReceipt receipt)
        {
            if (receipt == null) throw new ArgumentNullException(nameof(receipt));
            var record = Journal.Read(receipt.Position); var fact = DecodeDelivery(record);
            if (record.Operation != receipt.Operation || record.Fingerprint != receipt.Fingerprint)
                throw new InvalidDataException("Foreign delivery receipt.");
            return _bodies.ReadText(fact.Body.ToBlobReference());
        }

        internal ConversationInputState InputState(string message, CancellationToken token = default(CancellationToken))
        { return Access((db, cursor) => InputState(db, message), token); }

        internal ConversationInputState ChangeInput(string operation, string recipient, string message, long revision,
            ConversationInputStatus status, string run = null, CancellationToken token = default(CancellationToken))
        {
            var fact = new ConversationInputState(recipient, message, checked(revision + 1), status, run);
            var fingerprint = TextPatternEngine.Sha256(JsonConvert.SerializeObject(fact));
            return Access((db, cursor) => {
                var current = InputState(db, message);
                if (OperationExists(db, operation))
                {
                    using (var command = Command(db, "SELECT operation,fingerprint,segment,start_offset,end_offset,sequence,hash,first_sequence FROM input_transition WHERE stream=$stream AND operation=$operation"))
                    {
                        command.Parameters.AddWithValue("$operation", operation);
                        var record = LocatedRecord(command);
                        if (record != null && DecodeInput(record).MessageId == message && record.Fingerprint == fingerprint) return current;
                    }
                    throw new InvalidOperationException("Input operation identity conflicts with an accepted transition.");
                }
                var proposed = DeliveryRecord(operation, "input.changed", fact, fingerprint);
                ValidateDeliveryRecord(db, proposed);
                var position = Journal.Append(cursor, operation, fingerprint, proposed.Kind, proposed.Data);
                ApplyAcceptedDelivery(db, proposed, position);
                return fact;
            }, token);
        }

        internal IReadOnlyList<AgentMessageReceipt> PendingAgentInputs(string recipient, long afterSequence, int limit,
            CancellationToken token = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(recipient) || afterSequence < 0 || limit < 1 || limit > 128)
                throw new ArgumentException("Invalid pending input window.");
            return Access((db, cursor) => {
                var ids = new List<string>();
                using (var command = Command(db, "SELECT message FROM input_state WHERE stream=$stream AND recipient=$recipient AND status='Pending' AND accepted_sequence>$after ORDER BY accepted_sequence LIMIT $limit"))
                {
                    command.Parameters.AddWithValue("$recipient", recipient); command.Parameters.AddWithValue("$after", afterSequence);
                    command.Parameters.AddWithValue("$limit", limit);
                    using (var reader = command.ExecuteReader()) while (reader.Read()) ids.Add(reader.GetString(0));
                }
                return (IReadOnlyList<AgentMessageReceipt>)ids.Select(id => FindDelivery(db, "message", id)).ToArray();
            }, token);
        }

        internal AgentReplySnapshot MatchReplies(string sender, AgentWaitCondition condition, CancellationToken token = default(CancellationToken))
        {
            if (condition == null || condition.RequestIds.Count == 0) throw new ArgumentException("Exact wait requests are required.");
            return Access((db, cursor) => {
                var matches = new List<AgentMessageReceipt>();
                foreach (var id in condition.RequestIds)
                {
                    var request = FindDelivery(db, "message", id);
                    if (request?.Fact.Sender != sender || (condition.Completion ? !request.Fact.Assignment : !request.Fact.ExpectReply && !request.Fact.Assignment))
                        throw new InvalidOperationException("Wait request is not an accepted own request.");
                    string match;
                    using (var command = Command(db, "SELECT d.message FROM agent_delivery d JOIN input_state i ON i.stream=d.stream AND i.message=d.message WHERE d.stream=$stream AND d.reply_to=$reply AND d.sender=$sender AND d.recipient=$recipient AND d.created_ticks<=$deadline AND i.status<>'Removed'" +
                        (condition.Completion ? " AND d.resolution IS NOT NULL" : "") + " ORDER BY d.sequence LIMIT 1"))
                    {
                        command.Parameters.AddWithValue("$reply", id); command.Parameters.AddWithValue("$sender", request.Fact.Recipient);
                        command.Parameters.AddWithValue("$recipient", sender); command.Parameters.AddWithValue("$deadline", condition.DeadlineUtc.Ticks);
                        match = command.ExecuteScalar() as string;
                    }
                    if (match != null) matches.Add(FindDelivery(db, "message", match));
                }
                return new AgentReplySnapshot(matches.ToArray(), condition.All ? matches.Count == condition.RequestIds.Count : matches.Count != 0);
            }, token);
        }

        internal ChatMessage AppliedAgentMessage(string message, CancellationToken token = default(CancellationToken))
        {
            using (RuntimeStorageActivity.Acquire(_paths, token))
            {
                var selected = Access((db, cursor) => {
                    var state = InputState(db, message);
                    if (state?.Status != ConversationInputStatus.Applied) throw new InvalidOperationException("Input has not been applied to a run.");
                    var receipt = FindDelivery(db, "message", message);
                    var fact = receipt.Fact;
                    return Tuple.Create(state, receipt, Card(db, fact.Sender).Item1, Card(db, fact.Root).Item1);
                }, token);
                var accepted = selected.Item2.Fact;
                return new ChatMessage { Id = message, Role = "user", RunId = selected.Item1.RunId, CreatedUtc = accepted.CreatedUtc,
                    Content = ReadAgentText(selected.Item2), AgentCommunication = new AgentCommunicationContext {
                        MessageId = message, ReplyTo = accepted.ReplyTo, ExpectReply = accepted.ExpectReply, Assignment = accepted.Assignment,
                        Resolution = accepted.Resolution, ChainId = accepted.ChainId, SenderRunId = accepted.RunId, Policy = accepted.Policy,
                        Sender = CardAddress(selected.Item3), Root = CardAddress(selected.Item4) } };
            }
        }

        private AgentChatAddress CardAddress(ConversationCard card)
        { return new AgentChatAddress { ChatId = card.ChatId, Title = card.Title, ScopeTitle = card.DocumentTitle, Source = _scope.StartsWith("workspace:", StringComparison.Ordinal) ? "workspace" : "office",
            WorkspaceId = _scope.StartsWith("workspace:", StringComparison.Ordinal) ? _scope.Substring("workspace:".Length) : null }; }

        private ConversationCard FindAgent(SqliteConnection db, string alias)
        {
            if (string.IsNullOrWhiteSpace(alias)) throw new ArgumentException("Exact agent alias is required.");
            var ids = new List<string>(2);
            using (var command = Command(db, "SELECT chat FROM chat_card WHERE stream=$stream AND alias=$alias LIMIT 2"))
            {
                command.Parameters.AddWithValue("$alias", alias);
                using (var reader = command.ExecuteReader()) while (reader.Read()) ids.Add(reader.GetString(0));
            }
            if (ids.Count > 1) throw new InvalidOperationException("Agent alias is ambiguous.");
            return ids.Count == 0 ? null : Card(db, ids[0])?.Item1;
        }

        private Tuple<ConversationCard, string> Card(SqliteConnection db, string chat)
        {
            var record = CardRecord(db, chat);
            if (record == null || IsChatDeletion(record.Kind)) return null;
            var fact = DecodeCard(record);
            if (fact.ChatId != chat) throw new InvalidDataException("Foreign conversation card.");
            return Tuple.Create(fact, record.Operation);
        }

        private JournalRecord CardRecord(SqliteConnection db, string chat)
        {
            using (var command = Command(db, "SELECT operation,fingerprint,segment,start_offset,end_offset,sequence,hash,first_sequence FROM chat_card WHERE stream=$stream AND chat=$chat"))
            {
                command.Parameters.AddWithValue("$chat", chat);
                var record = LocatedRecord(command);
                if (record == null) return null;
                if (record.Kind == "chat.deleted" && DecodeChatDeletion(record).Address.Id != chat)
                    throw new InvalidDataException("Foreign chat deletion.");
                if (record.Kind == "document.deleted") ValidateDeletedDocumentCard(db, chat, record);
                return record;
            }
        }

        private AgentMessageReceipt FindDelivery(SqliteConnection db, string key, string value)
        {
            if (key != "message" && key != "operation") throw new ArgumentException(nameof(key));
            using (var command = Command(db, "SELECT operation,fingerprint,segment,start_offset,end_offset,sequence,hash,first_sequence FROM agent_delivery WHERE stream=$stream AND " + key + "=$value"))
            {
                command.Parameters.AddWithValue("$value", value);
                JournalCursor position; var record = LocatedRecord(command, out position);
                if (record == null) return null;
                var fact = DecodeDelivery(record);
                if ((key == "operation" ? record.Operation : fact.MessageId) != value) throw new InvalidDataException("Foreign delivery locator.");
                return new AgentMessageReceipt(record.Operation, record.Fingerprint, fact, position, true);
            }
        }

        private AgentMessageReceipt DeliveryForCall(SqliteConnection db, string sender, string run, string call)
        {
            using (var command = Command(db, "SELECT operation FROM agent_delivery WHERE stream=$stream AND sender=$sender AND run=$run AND call=$call"))
            {
                command.Parameters.AddWithValue("$sender", sender); command.Parameters.AddWithValue("$run", run);
                command.Parameters.AddWithValue("$call", call);
                var operation = command.ExecuteScalar() as string;
                var receipt = operation == null ? null : FindDelivery(db, "operation", operation);
                if (receipt != null && (receipt.Fact.Sender != sender || receipt.Fact.RunId != run || receipt.Fact.CallId != call))
                    throw new InvalidDataException("Foreign delivery call locator.");
                return receipt;
            }
        }

        private JournalRecord LocatedRecord(SqliteCommand command)
        { JournalCursor ignored; return LocatedRecord(command, out ignored); }

        private JournalRecord LocatedRecord(SqliteCommand command, out JournalCursor position)
        {
            position = null; string operation, fingerprint;
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read()) return null;
                operation = reader.GetString(0); fingerprint = reader.GetString(1); position = JournalIndex.ReadCursor(reader, 2);
            }
            var record = Journal.Read(position);
            if (record.Operation != operation || record.Fingerprint != fingerprint) throw new InvalidDataException("Derived delivery locator mismatch.");
            return record;
        }

        private JournalRecord InputRecord(SqliteConnection db, string message, long? through = null)
        {
            using (var command = Command(db, "SELECT operation,fingerprint,segment,start_offset,end_offset,sequence,hash,first_sequence FROM input_state WHERE stream=$stream AND message=$message"))
            {
                command.Parameters.AddWithValue("$message", message);
                JournalCursor position; var record = LocatedRecord(command, out position);
                if (through.HasValue && position?.Sequence > through.Value) throw new InvalidDataException("Input is beyond the observed chat revision.");
                return record;
            }
        }

        private ConversationInputState InputState(SqliteConnection db, string message, long? through = null)
        {
            var record = InputRecord(db, message, through);
            if (record == null) return null;
            ConversationInputState state;
            if (record.Kind == "agent.accepted" || record.Kind == "agent.spawned")
            {
                var accepted = DecodeDelivery(record);
                state = accepted.Input ?? new ConversationInputState(accepted.Recipient, accepted.MessageId, 0, ConversationInputStatus.Pending, null);
            }
            else state = DecodeInput(record);
            if (state.MessageId != message) throw new InvalidDataException("Foreign input state.");
            return state;
        }

        private bool Participant(SqliteConnection db, string chain, string chat)
        {
            using (var command = Command(db, "SELECT 1 FROM agent_participant WHERE stream=$stream AND chain=$chain AND chat=$chat"))
            { command.Parameters.AddWithValue("$chain", chain); command.Parameters.AddWithValue("$chat", chat); return command.ExecuteScalar() != null; }
        }

        private static bool IsDeliveryFact(string kind)
        { return kind == "chat.created" || kind == "agent.accepted" || kind == "input.changed" || kind == "agent.spawned"; }

        private IEnumerable<PayloadRef> ValidateDeliveryRecord(SqliteConnection db, JournalRecord record)
        {
            if (string.IsNullOrWhiteSpace(record.Operation) || OperationExists(db, record.Operation)) throw new InvalidDataException("Conversation operation already exists.");
            if (record.Kind == "agent.spawned") return ValidateSpawn(db, record);
            if (record.Kind == "chat.created")
            {
                if (CardRecord(db, DecodeCard(record).ChatId) != null) throw new InvalidDataException("Chat identity already exists or was deleted.");
            }
            else if (record.Kind == "input.changed")
            {
                var fact = DecodeInput(record); var prior = InputState(db, fact.MessageId);
                if (prior?.Header != null) throw new InvalidOperationException("Chat input transitions require the atomic chat port.");
                if (fact.Status == ConversationInputStatus.Delivering && QueueControl(db, fact.Recipient)?.Fact.Paused == true)
                    throw new InvalidOperationException("Paused input remains pending.");
                if (prior == null || prior.Recipient != fact.Recipient || fact.Revision != checked(prior.Revision + 1) ||
                    !(prior.Status == ConversationInputStatus.Pending && (fact.Status == ConversationInputStatus.Removed || fact.Status == ConversationInputStatus.Delivering) ||
                      prior.Status == ConversationInputStatus.Delivering && fact.Status == ConversationInputStatus.Applied && fact.RunId == prior.RunId))
                    throw new InvalidOperationException("Input transition does not match its accepted state/run.");
            }
            else
            {
                var fact = DecodeDelivery(record);
                if (FindDelivery(db, "message", fact.MessageId) != null) throw new InvalidDataException("Agent message identity is already accepted.");
                var sender = Card(db, fact.Sender)?.Item1; var recipient = Card(db, fact.Recipient)?.Item1;
                if (sender == null || recipient == null || Card(db, fact.Root)?.Item1 == null) throw new InvalidOperationException("Delivery scope member is unavailable.");
                (sender.Address == null ? sender.Policy : CurrentRun(db, fact.Sender)?.Fact.Policy ?? sender.Policy).RequireSame(fact.Policy);
                recipient.Policy.ForPeerInput(new AgentCommunicationContext { Sender = new AgentChatAddress { ChatId = sender.ChatId }, Policy = fact.Policy });
                ValidatePeerInput(db, record, fact, sender, recipient);
                var call = FindCall(db, fact.Sender, fact.RunId, fact.CallId);
                if (call == null || (call.Call.ToolName != AgentCommunicationToolCatalog.Send && call.Call.ToolName != AgentCommunicationToolCatalog.Resolve) ||
                    TerminalOperation(db, fact.Sender, fact.RunId, fact.CallId) != null)
                    throw new InvalidOperationException("Delivery requires an exact open accepted call.");
                RequireNoSpawn(db, fact.Sender, fact.RunId, fact.CallId);
                using (var command = Command(db, "SELECT 1 FROM agent_delivery WHERE stream=$stream AND sender=$sender AND run=$run AND call=$call"))
                {
                    command.Parameters.AddWithValue("$sender", fact.Sender); command.Parameters.AddWithValue("$run", fact.RunId); command.Parameters.AddWithValue("$call", fact.CallId);
                    if (command.ExecuteScalar() != null) throw new InvalidOperationException("Accepted call already delivered a message.");
                }
                ValidateMembership(db, fact.Sender, fact.Recipient, fact.Root, fact.ChainId, fact.ParticipantLimit);
                if (fact.ReplyTo != null)
                {
                    var original = FindDelivery(db, "message", fact.ReplyTo)?.Fact;
                    if (original == null || original.Sender != fact.Recipient || original.Recipient != fact.Sender ||
                        original.ChainId != fact.ChainId || original.Root != fact.Root || InputState(db, original.MessageId)?.Status != ConversationInputStatus.Applied ||
                        fact.Resolution.HasValue && !original.Assignment) throw new InvalidOperationException("Reply is not bound to an applied input from this exact peer/chain.");
                    if (fact.Resolution.HasValue)
                        using (var command = Command(db, "SELECT 1 FROM agent_delivery WHERE stream=$stream AND reply_to=$reply AND resolution IS NOT NULL"))
                        { command.Parameters.AddWithValue("$reply", fact.ReplyTo); if (command.ExecuteScalar() != null) throw new InvalidOperationException("Assignment already has an explicit resolution."); }
                }
                return new[] { fact.Body };
            }
            return new PayloadRef[0];
        }

        private void ValidateMembership(SqliteConnection db, string sender, string recipient, string root, string chain, int limit)
        {
                var count = 1;
                using (var command = Command(db, "SELECT root,participants FROM agent_chain WHERE stream=$stream AND chain=$chain"))
                {
                    command.Parameters.AddWithValue("$chain", chain);
                    using (var reader = command.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            if (reader.GetString(0) != root) throw new InvalidOperationException("Delivery chain root differs.");
                            count = reader.GetInt32(1);
                        }
                        else if (root != sender) throw new InvalidOperationException("Only the root can start a delivery chain.");
                    }
                }
                if (sender != root && !Participant(db, chain, sender)) throw new InvalidOperationException("Sender is not a chain participant.");
                if (recipient != root && !Participant(db, chain, recipient) && count >= limit)
                    throw new InvalidOperationException("Participant limit reached.");
        }

        private bool ApplyAcceptedDelivery(SqliteConnection db, JournalRecord record, JournalCursor position)
        {
            try { Fault?.Invoke("before_index"); ApplyDeliveryRecord(db, record, position); return true; }
            catch (Exception error) when (error is SqliteException || error is IOException) { return false; }
        }

        private void ApplyDeliveryRecord(SqliteConnection db, JournalRecord record, JournalCursor position)
        {
            ValidateDeliveryRecord(db, record);
            using (var tx = db.BeginTransaction())
            {
                RegisterOperation(db, tx, record.Operation, record.Kind);
                if (record.Kind == "agent.spawned") SaveSpawn(db, tx, record, position);
                else if (record.Kind == "chat.created")
                {
                    var fact = DecodeCard(record);
                    using (var command = Command(db, "INSERT INTO chat_card VALUES($stream,$chat,$alias,$operation,$fingerprint,$segment,$start,$end,$sequence,$hash,$first)", tx))
                    {
                        command.Parameters.AddWithValue("$chat", fact.ChatId); command.Parameters.AddWithValue("$alias", AgentChatAddress.IdFor(fact.ChatId));
                        AddDeliveryPosition(command, record, position); command.ExecuteNonQuery();
                    }
                }
                else if (record.Kind == "agent.accepted")
                {
                    var fact = DecodeDelivery(record);
                    SaveDelivery(db, tx, record, position, fact);
                }
                else
                {
                    var fact = DecodeInput(record);
                    using (var command = Command(db, "INSERT INTO input_transition VALUES($stream,$operation,$message,$fingerprint,$segment,$start,$end,$sequence,$hash,$first)", tx))
                    {
                        command.Parameters.AddWithValue("$message", fact.MessageId); AddDeliveryPosition(command, record, position); command.ExecuteNonQuery();
                    }
                    SaveInputState(db, tx, record, position, fact, false);
                }
                SaveCursor(db, position, tx); tx.Commit();
            }
        }

        private void SaveDelivery(SqliteConnection db, SqliteTransaction tx, JournalRecord record, JournalCursor position, AgentMessageAccepted fact)
        {
                    using (var command = Command(db, "INSERT INTO agent_delivery VALUES($stream,$operation,$message,$sender,$recipient,$run,$call,$chain,$reply,$resolution,$created,$fingerprint,$segment,$start,$end,$sequence,$hash,$first,$assignment)", tx))
                    {
                        command.Parameters.AddWithValue("$assignment", fact.Assignment ? 1 : 0);
                        command.Parameters.AddWithValue("$message", fact.MessageId); command.Parameters.AddWithValue("$sender", fact.Sender);
                        command.Parameters.AddWithValue("$recipient", fact.Recipient); command.Parameters.AddWithValue("$run", fact.RunId); command.Parameters.AddWithValue("$call", fact.CallId);
                        command.Parameters.AddWithValue("$chain", fact.ChainId); command.Parameters.AddWithValue("$reply", (object)fact.ReplyTo ?? DBNull.Value);
                        command.Parameters.AddWithValue("$resolution", (object)fact.Resolution?.ToString() ?? DBNull.Value); command.Parameters.AddWithValue("$created", fact.CreatedUtc.Ticks);
                        AddDeliveryPosition(command, record, position); command.ExecuteNonQuery();
                    }
                    SaveMembership(db, tx, fact.Root, fact.ChainId, fact.Recipient);
                    SaveInputState(db, tx, record, position, fact.Input ??
                        new ConversationInputState(fact.Recipient, fact.MessageId, 0, ConversationInputStatus.Pending, null), true);
                    if (fact.Input != null) ApplyPeerInputRows(db, tx, record, position, fact);
        }

        private void SaveMembership(SqliteConnection db, SqliteTransaction tx, string root, string chain, string recipient)
        {
                    using (var command = Command(db, "INSERT OR IGNORE INTO agent_chain VALUES($stream,$chain,$root,1); INSERT OR IGNORE INTO agent_participant VALUES($stream,$chain,$root)", tx))
                    { command.Parameters.AddWithValue("$chain", chain); command.Parameters.AddWithValue("$root", root); command.ExecuteNonQuery(); }
                    using (var command = Command(db, "INSERT OR IGNORE INTO agent_participant VALUES($stream,$chain,$chat)", tx))
                    {
                        command.Parameters.AddWithValue("$chain", chain); command.Parameters.AddWithValue("$chat", recipient);
                        if (command.ExecuteNonQuery() == 1)
                            using (var counter = Command(db, "UPDATE agent_chain SET participants=participants+1 WHERE stream=$stream AND chain=$chain", tx))
                            { counter.Parameters.AddWithValue("$chain", chain); counter.ExecuteNonQuery(); }
                    }
        }

        private void SaveInputState(SqliteConnection db, SqliteTransaction tx, JournalRecord record, JournalCursor position, ConversationInputState state, bool initial)
        {
            using (var command = Command(db, initial
                ? "INSERT INTO input_state VALUES($stream,$message,$recipient,$status,$revision,$run,$sequence,$operation,$fingerprint,$segment,$start,$end,$sequence,$hash,$first,$request)"
                : "UPDATE input_state SET status=$status,revision=$revision,run=$run,request_id=$request,operation=$operation,fingerprint=$fingerprint,segment=$segment,start_offset=$start,end_offset=$end,sequence=$sequence,hash=$hash,first_sequence=$first WHERE stream=$stream AND message=$message AND recipient=$recipient", tx))
            {
                command.Parameters.AddWithValue("$message", state.MessageId); command.Parameters.AddWithValue("$recipient", state.Recipient);
                command.Parameters.AddWithValue("$status", state.Status.ToString()); command.Parameters.AddWithValue("$revision", state.Revision);
                command.Parameters.AddWithValue("$request", (object)state.Header?.OperationId ?? DBNull.Value);
                command.Parameters.AddWithValue("$run", (object)state.RunId ?? DBNull.Value); AddDeliveryPosition(command, record, position); command.ExecuteNonQuery();
            }
        }

        private static void AddDeliveryPosition(SqliteCommand command, JournalRecord record, JournalCursor position)
        { command.Parameters.AddWithValue("$operation", record.Operation); command.Parameters.AddWithValue("$fingerprint", record.Fingerprint); JournalIndex.AddPosition(command, position); }
        private static JournalRecord DeliveryRecord(string operation, string kind, object fact, string fingerprint)
        { return new JournalRecord { Operation = operation, Kind = kind, Fingerprint = fingerprint, Data = JObject.FromObject(fact) }; }
        private static T DeliveryData<T>(JournalRecord record)
        { return record.Data.ToObject<T>(JsonSerializer.Create(new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error })); }
        private static ConversationCard DecodeCard(JournalRecord record)
        {
            if (record.Kind == "chat.committed") return DecodeChatCommit(record).Card;
            if (record.Kind == "agent.spawned") return DecodeSpawn(record).Child;
            var fact = DeliveryData<ConversationCard>(record);
            if (record.Kind != "chat.created" || fact == null || record.Fingerprint != CardFingerprint(fact)) throw new InvalidDataException("Invalid conversation card.");
            return fact;
        }
        private static AgentMessageAccepted DecodeDelivery(JournalRecord record)
        {
            if (record.Kind == "agent.spawned") return DecodeSpawn(record).Input ?? throw new InvalidDataException("Spawn has no input.");
            var fact = DeliveryData<AgentMessageAccepted>(record);
            if (record.Kind != "agent.accepted" || fact == null || record.Fingerprint != DeliveryFingerprint(fact)) throw new InvalidDataException("Invalid accepted agent message.");
            return fact;
        }
        private static ConversationInputState DecodeInput(JournalRecord record)
        {
            if (record.Kind == "chat.committed") return DecodeChatCommit(record).Input ?? throw new InvalidDataException("Missing input transition.");
            if (record.Kind == "agent.accepted" || record.Kind == "agent.spawned") return DecodeDelivery(record).Input ?? throw new InvalidDataException("Missing peer input.");
            var fact = DeliveryData<ConversationInputState>(record);
            if (record.Kind != "input.changed" || fact == null || record.Fingerprint != TextPatternEngine.Sha256(JsonConvert.SerializeObject(fact))) throw new InvalidDataException("Invalid input state.");
            return fact;
        }
        private static string CardFingerprint(ConversationCard fact)
        { var data = JObject.FromObject(fact); data.Remove("CreatedUtc"); return TextPatternEngine.Sha256(data.ToString(Formatting.None)); }
        private static string DeliveryFingerprint(AgentMessageAccepted fact)
        {
            var data = JObject.FromObject(fact); data.Remove("MessageId"); data.Remove("CreatedUtc");
            data.Remove("Input"); data.Remove("Control"); data.Remove("SenderRevision"); data.Remove("RecipientRevision");
            return TextPatternEngine.Sha256(data.ToString(Formatting.None));
        }
    }

    internal sealed class ConversationCard
    {
        public string ChatId { get; private set; }
        public string Title { get; private set; }
        public ConversationRunPolicy Policy { get; private set; }
        public ModelRunMetadata Model { get; private set; }
        public DateTime CreatedUtc { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public ConversationMetadata Address { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string DocumentTitle { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string DocumentPath { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string SelectedModel { get; private set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public bool ReasoningEnabled { get; private set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public bool TitleAssignedByAgent { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public AgentThreadCreation AgentCreation { get; private set; }
        [JsonConstructor]
        internal ConversationCard(string chatId, string title, ConversationRunPolicy policy, DateTime createdUtc, ModelRunMetadata model = null,
            ConversationMetadata address = null, string documentTitle = null, string documentPath = null,
            string selectedModel = null, bool reasoningEnabled = false, bool titleAssignedByAgent = false,
            AgentThreadCreation agentCreation = null)
        {
            if (string.IsNullOrWhiteSpace(chatId) || string.IsNullOrWhiteSpace(title) || title.Length > 512 || policy == null || createdUtc.Kind != DateTimeKind.Utc)
                throw new ArgumentException("Invalid conversation metadata.");
            ChatId = chatId; Title = title; Policy = policy; CreatedUtc = createdUtc;
            Model = model == null ? null : JsonConvert.DeserializeObject<ModelRunMetadata>(JsonConvert.SerializeObject(model));
            Address = address; DocumentTitle = documentTitle; DocumentPath = documentPath;
            SelectedModel = selectedModel; ReasoningEnabled = reasoningEnabled;
            TitleAssignedByAgent = titleAssignedByAgent;
            AgentCreation = agentCreation == null ? null : JsonConvert.DeserializeObject<AgentThreadCreation>(JsonConvert.SerializeObject(agentCreation));
        }
    }

    internal sealed class AgentMessageAccepted
    {
        public string Sender { get; private set; }
        public string Recipient { get; private set; }
        public string RunId { get; private set; }
        public string CallId { get; private set; }
        public string Root { get; private set; }
        public string ChainId { get; private set; }
        public int ParticipantLimit { get; private set; }
        public string MessageId { get; private set; }
        public PayloadRef Body { get; private set; }
        public bool ExpectReply { get; private set; }
        public string ReplyTo { get; private set; }
        public bool Assignment { get; private set; }
        public AgentAssignmentOutcome? Resolution { get; private set; }
        public ConversationRunPolicy Policy { get; private set; }
        public DateTime CreatedUtc { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string RecipientRuntimeKey { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public ConversationInputState Input { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public ConversationQueueControl Control { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public long? SenderRevision { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public long? RecipientRevision { get; private set; }
        [JsonConstructor]
        internal AgentMessageAccepted(string sender, string recipient, string runId, string callId, string root, string chainId,
            int participantLimit, string messageId, PayloadRef body, bool expectReply, string replyTo, bool assignment,
            AgentAssignmentOutcome? resolution, ConversationRunPolicy policy, DateTime createdUtc, string recipientRuntimeKey = null,
            ConversationInputState input = null, ConversationQueueControl control = null, long? senderRevision = null, long? recipientRevision = null)
        {
            if (new[] { sender, recipient, runId, callId, root, chainId, messageId }.Any(string.IsNullOrWhiteSpace) || sender == recipient ||
                participantLimit < 1 || body == null || policy == null || createdUtc.Kind != DateTimeKind.Utc || replyTo != null && string.IsNullOrWhiteSpace(replyTo) ||
                assignment && replyTo != null || resolution.HasValue && (replyTo == null || assignment || expectReply || !Enum.IsDefined(typeof(AgentAssignmentOutcome), resolution.Value)))
                throw new ArgumentException("Invalid accepted agent message.");
            Sender = sender; Recipient = recipient; RunId = runId; CallId = callId; Root = root; ChainId = chainId;
            ParticipantLimit = participantLimit; MessageId = messageId; Body = body; ExpectReply = expectReply; ReplyTo = replyTo;
            Assignment = assignment; Resolution = resolution; Policy = policy; CreatedUtc = createdUtc;
            RecipientRuntimeKey = recipientRuntimeKey; Input = input; Control = control;
            SenderRevision = senderRevision; RecipientRevision = recipientRevision;
        }
    }

    internal sealed class AgentMessageReceipt
    {
        internal readonly string Operation, Fingerprint;
        internal readonly AgentMessageAccepted Fact;
        internal readonly JournalCursor Position;
        internal readonly bool ProjectionAvailable;
        internal AgentMessageReceipt(string operation, string fingerprint, AgentMessageAccepted fact, JournalCursor position, bool projected)
        { Operation = operation; Fingerprint = fingerprint; Fact = fact; Position = position; ProjectionAvailable = projected; }
    }

    internal sealed class ConversationInputState
    {
        public string Recipient { get; private set; }
        public string MessageId { get; private set; }
        public long Revision { get; private set; }
        public ConversationInputStatus Status { get; private set; }
        public string RunId { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public ConversationInput Header { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public PayloadRef Body { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public JournalCursor AppliedMessage { get; private set; }
        [JsonConstructor]
        internal ConversationInputState(string recipient, string messageId, long revision, ConversationInputStatus status, string runId, ConversationInput header = null, PayloadRef body = null, JournalCursor appliedMessage = null)
        {
            if (string.IsNullOrWhiteSpace(recipient) || string.IsNullOrWhiteSpace(messageId) || revision < 0 || !Enum.IsDefined(typeof(ConversationInputStatus), status) ||
                (header == null && ((status == ConversationInputStatus.Delivering || status == ConversationInputStatus.Applied) ? string.IsNullOrWhiteSpace(runId) : runId != null)) ||
                (header == null ? body != null || appliedMessage != null : body == null || header.Text != null ||
                    header.Id != messageId || header.Revision != revision || header.Status != status || header.AcceptedRunId != runId ||
                    (status == ConversationInputStatus.Applied) != (appliedMessage != null)))
                throw new ArgumentException("Invalid input delivery state.");
            Header = header; Body = body; AppliedMessage = appliedMessage;
            Recipient = recipient; MessageId = messageId; Revision = revision; Status = status; RunId = runId;
        }
    }

    internal sealed class AgentReplySnapshot
    {
        internal readonly IReadOnlyList<AgentMessageReceipt> Matches;
        internal readonly bool Ready;
        internal AgentReplySnapshot(AgentMessageReceipt[] matches, bool ready) { Matches = matches; Ready = ready; }
    }
}
