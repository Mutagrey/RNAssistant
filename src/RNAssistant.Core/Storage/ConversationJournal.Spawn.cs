using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Storage
{
    internal sealed partial class ConversationJournal
    {
        internal AgentSpawnJournalReceipt SpawnAgent(string operation, string sender, string run, string call, string root,
            string chain, int limit, string title, string initialText = null, bool expectReply = false, bool assignment = false,
            CancellationToken token = default(CancellationToken), ConversationCard childCard = null,
            PayloadRef childContext = null, string runtimeKey = null, Action<SqliteConnection> validateDispatch = null, Action dispatch = null)
        {
            if (string.IsNullOrWhiteSpace(operation) || string.IsNullOrWhiteSpace(title) || title.Trim().Length > 200 ||
                title.IndexOfAny(new[] { '\r', '\n' }) >= 0 || initialText != null && (string.IsNullOrWhiteSpace(initialText) || initialText.Length > 128 * 1024) ||
                initialText == null && (expectReply || assignment)) throw new ArgumentException("Invalid spawn request.");
            using (RuntimeStorageActivity.Acquire(_paths, token))
            {
                var body = initialText == null ? null : PayloadRef.FromBlob(_bodies.StoreText(initialText, "text/plain"));
                var identity = TextPatternEngine.Sha256(_scope + "\n" + operation);
                return Access((db, cursor) => {
                    var source = Card(db, sender)?.Item1 ?? throw new InvalidOperationException("Spawn sender is unavailable.");
                    if ((childCard == null) != (childContext == null) || (source.Address == null) != (childCard == null))
                        throw new InvalidOperationException("Spawn requires one native storage path for both chats.");
                    var child = childCard ?? new ConversationCard("child-" + identity, title.Trim(), source.Policy, DateTime.UtcNow, source.Model);
                    if (child.ChatId != "child-" + identity) throw new InvalidOperationException("Spawn child identity is not canonical.");
                    var policy = source.Address == null ? source.Policy : CurrentRun(db, sender)?.Fact.Policy ?? source.Policy;
                    var input = body == null ? null : new AgentMessageAccepted(sender, child.ChatId, run, call, root, chain, limit,
                        "m-" + identity.Substring(0, 32), body, expectReply, null, assignment, null, policy, child.CreatedUtc, runtimeKey);
                    var chat = childContext == null ? null : new ConversationChatCommit(child, 0, 0,
                        new ConversationTextEntry[0], childContext, child.CreatedUtc);
                    var fact = new AgentSpawnAccepted(sender, run, call, root, chain, limit, child, input, chat,
                        chat == null ? (long?)null : TextChat(db, sender).Position.Sequence);
                    var fingerprint = SpawnFingerprint(fact);
                    var prior = FindSpawn(db, operation);
                    if (prior != null)
                    {
                        if (prior.Fingerprint != fingerprint) throw new InvalidOperationException("Spawn operation was reused with different content.");
                        return prior;
                    }
                    if (chat != null)
                    {
                        child.AgentCreation.Fingerprint = fingerprint;
                        if (input != null) input = PreparePeerInput(db, operation, fingerprint, input, source, child, spawn: true);
                        chat = new ConversationChatCommit(child, 0, 0, new ConversationTextEntry[0], childContext, child.CreatedUtc,
                            control: input?.Control, input: input?.Input);
                        fact = new AgentSpawnAccepted(sender, run, call, root, chain, limit, child, input, chat, fact.SenderRevision);
                    }
                    var record = DeliveryRecord(operation, "agent.spawned", fact, fingerprint);
                    ValidateDeliveryRecord(db, record);
                    validateDispatch?.Invoke(db);
                    dispatch?.Invoke();
                    var position = Journal.Append(cursor, operation, fingerprint, record.Kind, record.Data);
                    return new AgentSpawnJournalReceipt(operation, fingerprint, fact, position, ApplyAcceptedDelivery(db, record, position));
                }, token);
            }
        }

        internal AgentSpawnJournalReceipt SpawnReceipt(string operation, CancellationToken token = default(CancellationToken))
        { return Access((db, cursor) => FindSpawn(db, operation), token); }

        private AgentSpawnJournalReceipt FindSpawn(SqliteConnection db, string operation)
        {
            using (var command = Command(db, "SELECT operation,fingerprint,segment,start_offset,end_offset,sequence,hash,first_sequence FROM agent_spawn WHERE stream=$stream AND operation=$operation"))
            {
                command.Parameters.AddWithValue("$operation", operation);
                JournalCursor position; var record = LocatedRecord(command, out position);
                if (record == null) return null;
                if (record.Operation != operation) throw new InvalidDataException("Foreign spawn receipt.");
                return new AgentSpawnJournalReceipt(operation, record.Fingerprint, DecodeSpawn(record), position, true);
            }
        }

        private void RequireNoSpawn(SqliteConnection db, string sender, string run, string call)
        {
            using (var command = Command(db, "SELECT 1 FROM agent_spawn WHERE stream=$stream AND sender=$sender AND run=$run AND call=$call"))
            {
                command.Parameters.AddWithValue("$sender", sender); command.Parameters.AddWithValue("$run", run); command.Parameters.AddWithValue("$call", call);
                if (command.ExecuteScalar() != null) throw new InvalidOperationException("Accepted call already created a child.");
            }
        }

        private AgentSpawnJournalReceipt SpawnForCall(SqliteConnection db, string sender, string run, string call)
        {
            using (var command = Command(db, "SELECT operation FROM agent_spawn WHERE stream=$stream AND sender=$sender AND run=$run AND call=$call"))
            {
                command.Parameters.AddWithValue("$sender", sender); command.Parameters.AddWithValue("$run", run); command.Parameters.AddWithValue("$call", call);
                var operation = command.ExecuteScalar() as string;
                return operation == null ? null : FindSpawn(db, operation);
            }
        }

        private IEnumerable<PayloadRef> ValidateSpawn(SqliteConnection db, JournalRecord record)
        {
            var fact = DecodeSpawn(record);
            var sender = Card(db, fact.Sender)?.Item1;
            if (sender == null || Card(db, fact.Root) == null || Card(db, fact.Child.ChatId) != null)
                throw new InvalidOperationException("Spawn source/root/child identity conflicts.");
            if (sender.Address == null)
            {
                if (fact.Chat != null || fact.Child.Address != null || fact.SenderRevision != null)
                    throw new InvalidDataException("Primitive spawn cannot create a Store chat.");
                sender.Policy.RequireSame(fact.Child.Policy);
                if (!SameRunValue(sender.Model, fact.Child.Model)) throw new InvalidOperationException("Spawn model configuration differs from its source.");
            }
            else ValidateSpawnChat(db, record, fact, sender);
            var call = FindCall(db, fact.Sender, fact.RunId, fact.CallId);
            if (call?.Call.ToolName != AgentCommunicationToolCatalog.Spawn || TerminalOperation(db, fact.Sender, fact.RunId, fact.CallId) != null)
                throw new InvalidOperationException("Spawn requires an exact open accepted call.");
            RequireNoSpawn(db, fact.Sender, fact.RunId, fact.CallId);
            ValidateMembership(db, fact.Sender, fact.Child.ChatId, fact.Root, fact.ChainId, fact.ParticipantLimit);
            if (fact.Input != null)
            {
                (sender.Address == null ? fact.Child.Policy : CurrentRun(db, fact.Sender).Fact.Policy).RequireSame(fact.Input.Policy);
                ValidatePeerInput(db, record, fact.Input, sender, fact.Child);
                if (FindDelivery(db, "message", fact.Input.MessageId) != null) throw new InvalidOperationException("Spawn input identity conflicts.");
            }
            return SpawnReferences(fact);
        }

        private void SaveSpawn(SqliteConnection db, SqliteTransaction tx, JournalRecord record, JournalCursor position)
        {
            var fact = DecodeSpawn(record);
            using (var command = Command(db, "INSERT INTO agent_spawn VALUES($stream,$operation,$sender,$run,$call,$child,$fingerprint,$segment,$start,$end,$sequence,$hash,$first)", tx))
            {
                command.Parameters.AddWithValue("$sender", fact.Sender); command.Parameters.AddWithValue("$run", fact.RunId);
                command.Parameters.AddWithValue("$call", fact.CallId); command.Parameters.AddWithValue("$child", fact.Child.ChatId);
                AddDeliveryPosition(command, record, position); command.ExecuteNonQuery();
            }
            if (fact.Chat != null) SaveChatMetadata(db, tx, record, position, fact.Chat, fact.Child.CreatedUtc.Ticks);
            else using (var command = Command(db, "INSERT INTO chat_card VALUES($stream,$chat,$alias,$operation,$fingerprint,$segment,$start,$end,$sequence,$hash,$first)", tx))
            {
                command.Parameters.AddWithValue("$chat", fact.Child.ChatId); command.Parameters.AddWithValue("$alias", AgentChatAddress.IdFor(fact.Child.ChatId));
                AddDeliveryPosition(command, record, position); command.ExecuteNonQuery();
            }
            if (fact.Input == null)
            {
                SaveMembership(db, tx, fact.Root, fact.ChainId, fact.Child.ChatId);
                if (fact.Chat != null)
                    using (var command = Command(db, "UPDATE chat_state SET head_operation=$operation,last_activity_ticks=$activity WHERE stream=$stream AND chat=$chat", tx))
                    {
                        command.Parameters.AddWithValue("$operation", record.Operation); command.Parameters.AddWithValue("$activity", fact.Child.CreatedUtc.Ticks);
                        command.Parameters.AddWithValue("$chat", fact.Sender);
                        if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("Spawn lost its source chat head.");
                    }
            }
            else SaveDelivery(db, tx, record, position, fact.Input);
        }

        private static AgentSpawnAccepted DecodeSpawn(JournalRecord record)
        {
            var fact = DeliveryData<AgentSpawnAccepted>(record);
            if (record.Kind != "agent.spawned" || fact == null || SpawnFingerprint(fact) != record.Fingerprint)
                throw new InvalidDataException("Invalid spawn fact.");
            return fact;
        }

        private static string SpawnFingerprint(AgentSpawnAccepted fact)
        {
            var data = JObject.FromObject(fact); ((JObject)data["Child"]).Remove("CreatedUtc");
            ((JObject)data["Child"]["AgentCreation"])?.Remove("Fingerprint");
            if (fact.Chat != null) data["Chat"] = JObject.FromObject(fact.Chat.Context);
            data.Remove("SenderRevision");
            var input = data["Input"] as JObject;
            if (input != null) { input.Remove("CreatedUtc"); input.Remove("MessageId");
                input.Remove("Input"); input.Remove("Control"); input.Remove("SenderRevision"); input.Remove("RecipientRevision"); }
            return TextPatternEngine.Sha256(data.ToString(Formatting.None));
        }

        private static IEnumerable<PayloadRef> SpawnReferences(AgentSpawnAccepted fact)
        { return new[] { fact.Chat?.Context, fact.Input?.Body }.Where(item => item != null); }
    }

    internal sealed class AgentSpawnAccepted
    {
        public string Sender { get; private set; }
        public string RunId { get; private set; }
        public string CallId { get; private set; }
        public string Root { get; private set; }
        public string ChainId { get; private set; }
        public int ParticipantLimit { get; private set; }
        public ConversationCard Child { get; private set; }
        public AgentMessageAccepted Input { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public ConversationChatCommit Chat { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public long? SenderRevision { get; private set; }
        [JsonConstructor]
        internal AgentSpawnAccepted(string sender, string runId, string callId, string root, string chainId, int participantLimit,
            ConversationCard child, AgentMessageAccepted input, ConversationChatCommit chat = null, long? senderRevision = null)
        {
            if (string.IsNullOrWhiteSpace(sender) || string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(callId) ||
                string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(chainId) || participantLimit < 1 || child == null || child.ChatId == sender ||
                input != null && (input.Sender != sender || input.Recipient != child.ChatId || input.RunId != runId || input.CallId != callId || input.Root != root ||
                    input.ChainId != chainId || input.ParticipantLimit != participantLimit || input.ReplyTo != null || input.Resolution.HasValue))
                throw new ArgumentException("Invalid atomic spawn identity.");
            Sender = sender; RunId = runId; CallId = callId; Root = root; ChainId = chainId;
            ParticipantLimit = participantLimit; Child = child; Input = input; Chat = chat; SenderRevision = senderRevision;
        }
    }

    internal sealed class AgentSpawnJournalReceipt
    {
        internal readonly string Operation, Fingerprint;
        internal readonly AgentSpawnAccepted Fact;
        internal readonly JournalCursor Position;
        internal readonly bool ProjectionAvailable;
        internal AgentSpawnJournalReceipt(string operation, string fingerprint, AgentSpawnAccepted fact, JournalCursor position, bool projected)
        { Operation = operation; Fingerprint = fingerprint; Fact = fact; Position = position; ProjectionAvailable = projected; }
    }
}
