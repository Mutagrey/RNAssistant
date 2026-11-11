using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Agent;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Storage
{
    internal sealed partial class ConversationJournal
    {
        internal long ChatBodyReads;
        private static bool IsChatFact(string kind) { return kind == "chat.committed" || kind == "chat.selected"; }

        private static string ChatFingerprint(ConversationChatCommit fact)
        {
            var data = JObject.FromObject(fact); data.Remove("CreatedUtc");
            return TextPatternEngine.Sha256(data.ToString(Formatting.None));
        }

        private static ConversationChatCommit DecodeChatCommit(JournalRecord record)
        {
            var fact = DeliveryData<ConversationChatCommit>(record);
            if (record.Kind != "chat.committed" || fact == null || record.Fingerprint != ChatFingerprint(fact))
                throw new InvalidDataException("Invalid chat commit.");
            return fact;
        }

        private static IEnumerable<PayloadRef> ChatReferences(JournalRecord record)
        {
            if (record.Kind == "chat.selected") { DecodeChatSelection(record); return new PayloadRef[0]; }
            var fact = DecodeChatCommit(record);
            return new[] { fact.Context, fact.ContextReceipt, fact.RunState, fact.ModelEvent?.Body }.Concat(fact.Messages.SelectMany(item => new[] { item.Body, item.Reasoning }.Concat(MessageReferences(item.Header))))
                .Concat(fact.Tools?.Terminal == null ? new PayloadRef[0] : MessageReferences(fact.Tools.Terminal.Result))
                .Where(item => item != null).Concat(fact.Checkpoint?.References ?? new PayloadRef[0]);
        }

        private ConversationChatReceipt FindChatWrite(SqliteConnection db, string operation)
        {
            using (var command = Command(db, "SELECT operation,fingerprint,segment,start_offset,end_offset,sequence,hash,first_sequence FROM chat_write WHERE stream=$stream AND operation=$operation"))
            {
                command.Parameters.AddWithValue("$operation", operation);
                JournalCursor position; var record = LocatedRecord(command, out position);
                return record == null ? null : new ConversationChatReceipt(DecodeChatCommit(record), record.Operation, position);
            }
        }

        private ConversationChatReceipt CurrentChat(SqliteConnection db, string chat)
        {
            var operation = Scalar(db, "SELECT operation FROM chat_state WHERE stream=$stream AND chat=$chat", chat) as string;
            if (operation == null) return null;
            var receipt = FindChatWrite(db, operation);
            if (receipt == null || receipt.Fact.Card.ChatId != chat || receipt.Fact.MessageCount !=
                Convert.ToInt32(Scalar(db, "SELECT message_count FROM chat_state WHERE stream=$stream AND chat=$chat", chat)))
                throw new InvalidDataException("Chat metadata disagrees with its canonical commit.");
            return receipt;
        }

        private void ValidateChatAddress(ConversationCard card)
        {
            var address = card?.Address;
            if (address == null || address.Id != card.ChatId || address.Revision != 0 ||
                string.IsNullOrWhiteSpace(address.Host) || string.IsNullOrWhiteSpace(address.DocumentKey) ||
                (_scope.StartsWith("workspace:", StringComparison.Ordinal)
                    ? address.Host != "Workspace" || address.WorkspaceId != _scope.Substring(10) ||
                        address.DocumentKey != address.WorkspaceId || address.DocumentAuthorityId != null
                    : address.DocumentAuthorityId != _scope.Substring(9) || address.WorkspaceId != null))
                throw new InvalidDataException("Chat address is outside this exact journal scope.");
        }

        private IEnumerable<PayloadRef> ValidateChatFact(SqliteConnection db, JournalRecord record, Dictionary<string, ContextCallKey> keys = null, ResourceJournalReceipt outcome = null)
        {
            if (OperationExists(db, record.Operation)) throw new InvalidDataException("Duplicate chat operation.");
            if (record.Kind == "chat.selected")
            {
                var selected = DecodeChatSelection(record);
                if (CurrentChat(db, selected.ChatId) == null) throw new InvalidOperationException("Selected chat is unavailable.");
                return new PayloadRef[0];
            }
            var fact = DecodeChatCommit(record); ValidateChatAddress(fact.Card);
            var current = TextChat(db, fact.Card.ChatId);
            if (fact.ExpectedRevision != (current?.Position.Sequence ?? 0))
                throw new ChatConcurrencyException("Chat changed before the journal commit.");
            if (current == null && (Card(db, fact.Card.ChatId) != null ||
                Scalar(db, "SELECT 1 FROM call_fact WHERE stream=$stream AND chat=$chat LIMIT 1", fact.Card.ChatId) != null))
                throw new InvalidOperationException("Chat belongs to an integration not supported by the text store yet.");
            if (current != null && (!JToken.DeepEquals(JObject.FromObject(current.Fact.Card.Address), JObject.FromObject(fact.Card.Address)) ||
                current.Fact.Card.CreatedUtc != fact.Card.CreatedUtc))
                throw new InvalidOperationException("A metadata update cannot move chat ownership or replace creation identity.");
            var added = 0;
            foreach (var message in fact.Messages)
            {
                var prior = ChatEntry(db, fact.Card.ChatId, message.Header.Id);
                if (prior == null)
                {
                    added++;
                    if (fact.Checkpoint == null && (message.Header.ProtocolMessage || message.Header.RuntimeProjection || message.Header.Activity != null))
                        throw new InvalidOperationException("New runtime messages require their atomic kernel checkpoint.");
                }
                else if ((prior.Header.Activity == null || message.Header.Id != fact.Tools?.ActivityId) && !SameRunValue(prior.Body, message.Body) ||
                    !SameRunValue(prior.Reasoning, message.Reasoning) ||
                    prior.Header.Role != message.Header.Role || prior.Header.CreatedUtc != message.Header.CreatedUtc ||
                    prior.Header.ProtocolMessage != message.Header.ProtocolMessage || prior.Header.RuntimeProjection != message.Header.RuntimeProjection ||
                    prior.Header.ResponseModelAttemptId != message.Header.ResponseModelAttemptId ||
                    prior.Header.RunId != null && prior.Header.RunId != message.Header.RunId)
                    throw new InvalidOperationException("A presentation update cannot rewrite retained text or its occurrence identity.");
            }
            if (fact.MessageCount != (current?.Fact.MessageCount ?? 0) + added)
                throw new InvalidOperationException("Chat message count does not match its exact append/update delta.");
            ValidateChatRun(db, record, fact);
            ValidateChatTools(db, record, fact, keys, outcome);
            return ChatReferences(record);
        }

        private void ApplyChatFact(SqliteConnection db, JournalRecord record, JournalCursor position, Dictionary<string, ContextCallKey> keys = null, ResourceJournalReceipt outcome = null)
        {
            ValidateChatFact(db, record, keys, outcome);
            var committed = record.Kind == "chat.committed" ? DecodeChatCommit(record) : null;
            long lastActivity = 0;
            var existing = new HashSet<string>(StringComparer.Ordinal);
            var ordinal = 0;
            if (committed != null)
            {
                var priorActivity = Scalar(db, "SELECT last_activity_ticks FROM chat_state WHERE stream=$stream AND chat=$chat", committed.Card.ChatId);
                lastActivity = priorActivity == null ? committed.Card.CreatedUtc.Ticks : Convert.ToInt64(priorActivity);
                ordinal = Convert.ToInt32(Scalar(db, "SELECT message_count FROM chat_state WHERE stream=$stream AND chat=$chat", committed.Card.ChatId) ?? 0);
                foreach (var message in committed.Messages)
                {
                    lastActivity = Math.Max(lastActivity, message.Header.CreatedUtc.Ticks);
                    if (ChatMessageOperation(db, committed.Card.ChatId, message.Header.Id) != null) existing.Add(message.Header.Id);
                }
            }
            var nextEvidence = committed?.Tools?.Terminal == null ? 0 : Convert.ToInt64(Scalar(db,
                "SELECT next_e FROM chat_sequence WHERE stream=$stream AND chat=$chat", committed.Card.ChatId));
            using (var tx = db.BeginTransaction())
            {
                RegisterOperation(db, tx, record.Operation, record.Kind);
                if (record.Kind == "chat.selected")
                {
                    using (var command = Command(db, "INSERT OR REPLACE INTO chat_selection VALUES($stream,$chat)", tx))
                    { command.Parameters.AddWithValue("$chat", DecodeChatSelection(record).ChatId); command.ExecuteNonQuery(); }
                }
                else
                {
                    var fact = committed;
                    using (var command = Command(db, "INSERT INTO chat_write VALUES($stream,$chat,$operation,$fingerprint,$segment,$start,$end,$sequence,$hash,$first)", tx))
                    { command.Parameters.AddWithValue("$chat", fact.Card.ChatId); AddDeliveryPosition(command, record, position); command.ExecuteNonQuery(); }
                    using (var command = Command(db, "INSERT OR REPLACE INTO chat_card VALUES($stream,$chat,$alias,$operation,$fingerprint,$segment,$start,$end,$sequence,$hash,$first)", tx))
                    {
                        command.Parameters.AddWithValue("$chat", fact.Card.ChatId); command.Parameters.AddWithValue("$alias", AgentChatAddress.IdFor(fact.Card.ChatId));
                        AddDeliveryPosition(command, record, position); command.ExecuteNonQuery();
                    }
                    using (var command = Command(db, "INSERT OR REPLACE INTO chat_state VALUES($stream,$chat,$operation,$count,$activity)", tx))
                    {
                        command.Parameters.AddWithValue("$chat", fact.Card.ChatId); command.Parameters.AddWithValue("$operation", record.Operation);
                        command.Parameters.AddWithValue("$count", fact.MessageCount);
                        command.Parameters.AddWithValue("$activity", lastActivity); command.ExecuteNonQuery();
                    }
                    foreach (var message in fact.Messages)
                        using (var command = Command(db, "INSERT INTO chat_message VALUES($stream,$chat,$id,$ordinal,$operation) ON CONFLICT(stream,chat,id) DO UPDATE SET operation=$operation", tx))
                        {
                            command.Parameters.AddWithValue("$chat", fact.Card.ChatId); command.Parameters.AddWithValue("$id", message.Header.Id);
                            command.Parameters.AddWithValue("$ordinal", existing.Contains(message.Header.Id) ? -1 : ordinal++); command.Parameters.AddWithValue("$operation", record.Operation);
                            command.ExecuteNonQuery();
                        }
                }
                if (committed != null) ApplyChatToolRows(db, tx, record, position, committed, keys, existing, nextEvidence);
                if (committed?.Checkpoint != null) ApplyRunRows(db, tx, record, position, committed.Checkpoint);
                SaveCursor(db, position, tx); tx.Commit();
            }
        }

        private ConversationChatReceipt CommitChat(ChatSession session, ConversationCommit commit, ConversationModelEvent modelEvent = null)
        {
            RequireTextSession(session, commit); commit.BindJournal(session);
            using (RuntimeStorageActivity.Acquire(_paths))
            {
                var card = new ConversationCard(session.Id, session.Title, ConversationRunPolicy.ForSession(session), session.CreatedUtc,
                    address: new ConversationMetadata(session.Id, 0, session.Host, session.DocumentKey, session.WorkspaceId, session.DocumentAuthorityId),
                    documentTitle: session.DocumentTitle, documentPath: session.DocumentPath, selectedModel: session.Model, reasoningEnabled: session.ReasoningEnabled);
                ValidateChatAddress(card);
                var messages = commit.Messages.Select(write => PublishText(write.Message)).ToArray();
                ResourceJournalReceipt outcome;
                var tools = PrepareChatTools(session, commit as ConversationRunCommit, out outcome);
                var keys = new Dictionary<string, ContextCallKey>(StringComparer.Ordinal);
                foreach (var message in messages.Where(item => item.Header.AcceptedCallOrigin != null))
                { var call = new ConversationCallFact(session.Id, message.Header); var key = CallFingerprint(call); keys[key] = PrepareContextCallKey(call.Call, key); }
                var context = PayloadRef.FromBlob(_bodies.StoreText(JsonConvert.SerializeObject(session.Context), "application/json"));
                var receiptBody = session.LastContextReceipt == null ? null : StoreChatJson(session.LastContextReceipt);
                var runBody = session.LastRun?.KernelState == null ? null : StoreChatJson(session.LastRun.KernelState);
                var run = CopyRunHeader(session.LastRun);
                foreach (var reference in new[] { context, receiptBody, runBody, modelEvent?.Body }
                    .Concat(messages.SelectMany(item => new[] { item.Body, item.Reasoning }.Concat(MessageReferences(item.Header))))
                    .Concat(tools?.Terminal == null ? new PayloadRef[0] : MessageReferences(tools.Terminal.Result)).Where(item => item != null))
                    if (!_bodies.HasVerifiedReference(reference.ToBlobReference())) throw new InvalidDataException("Chat body is unavailable before acceptance.");
                return Access((db, cursor) => {
                    if (!(commit is ConversationRunCommit) && messages.Any(item =>
                        (item.Header.ToolCallId != null || item.Header.Activity != null) &&
                        ChatEntry(db, session.Id, item.Header.Id) == null)) throw TextStoreScope();
                    var prior = FindChatWrite(db, commit.OperationId);
                    var checkpoint = commit is ConversationRunCommit runtime
                        ? PrepareStoreCheckpoint(db, runtime, session, runBody, prior?.Fact.Checkpoint) : null;
                    var fact = new ConversationChatCommit(card, commit.JournalRevision, commit.JournalMessageCount, messages, context, DateTime.UtcNow,
                        run, runBody, checkpoint, receiptBody, modelEvent, tools);
                    AssignChatReferences(db, fact, prior?.Fact);
                    var fingerprint = ChatFingerprint(fact);
                    if (prior != null)
                    {
                        if (ChatFingerprint(prior.Fact) != fingerprint) throw new InvalidOperationException("Chat operation was reused with different content.");
                        return prior;
                    }
                    var record = DeliveryRecord(commit.OperationId, "chat.committed", fact, fingerprint);
                    ValidateChatFact(db, record, keys, outcome);
                    var position = Journal.Append(cursor, record.Operation, fingerprint, record.Kind, record.Data);
                    try { Fault?.Invoke("before_index"); ApplyChatFact(db, record, position, keys, outcome); }
                    catch (Exception error) when (error is IOException || error is SqliteException) { /* canonical receipt remains accepted */ }
                    return new ConversationChatReceipt(fact, record.Operation, position);
                }, CancellationToken.None);
            }
        }

        private ConversationTextEntry PublishText(ChatMessage message)
        {
            ConversationTextEntry.RequireText(message);
            if (message.AcceptedCallOrigin != null)
            {
                message = message.CopyForContext();
                if (message.AcceptedCallPayload == null)
                {
                    var parsed = ConversationResponseHistoryReader.Read(message);
                    if (!parsed.Success || parsed.Response.ToolCalls.Count != 1) throw new InvalidDataException("Invalid accepted call.");
                    message.ArgumentPayload = PayloadRef.FromBlob(_bodies.StoreText(parsed.Response.ToolCalls[0].ArgumentsJson, "application/json"));
                    AcceptedCallPayloadService.Externalize(message, _bodies);
                }
            }
            else if (message.ProtocolMessage && message.ToolCallId == null && !ConversationResponseHistoryReader.Read(message).Success)
                throw new InvalidDataException("Invalid accepted response.");
            var header = message.CopyForContext(); header.Content = null; header.ReasoningContent = null;
            return new ConversationTextEntry(header, PayloadRef.FromBlob(_bodies.StoreText(message.Content ?? string.Empty, "text/plain")),
                message.ReasoningContent == null ? null : PayloadRef.FromBlob(_bodies.StoreText(message.ReasoningContent, "text/plain")));
        }

        private ChatMessage ReadText(ConversationTextEntry entry)
        {
            var message = entry.Header.CopyForContext();
            message.Content = ReadChatBody(entry.Body);
            message.ReasoningContent = entry.Reasoning == null ? null : ReadChatBody(entry.Reasoning);
            return message;
        }

        private string ReadChatBody(PayloadRef reference)
        {
            Interlocked.Increment(ref ChatBodyReads);
            return _bodies.ReadText(reference.ToBlobReference()) ?? throw new InvalidDataException("Retained chat body is unavailable.");
        }

        private static void RequireTextSession(ChatSession session, ConversationCommit commit)
        {
            if (session == null || commit == null || !session.UsesCurrentProtocol) throw new ArgumentException("A current chat and exact commit are required.");
            if (commit.RemovedMessageIds.Count != 0 ||
                session.ParentSessionId != null || session.ParentSessionRevision != null || session.ForkedThroughMessageId != null ||
                session.TitleAssignedByAgent || session.PreviousDocumentKeys.Count != 0 ||
                session.ActiveContextCheckpointId != null || session.ActiveHtmlArtifactId != null ||
                session.ActiveTaskListArtifactId != null || session.ActivePlanDocumentArtifactId != null ||
                session.AgentCreation != null ||
                session.Artifacts.Count != 0 || session.ArtifactLinks.Count != 0 || session.ContextCheckpoints.Count != 0 ||
                session.ResourceCopies.Count != 0 || session.Context.Notes.Count != 0 ||
                session.HtmlWorkspace.Files.Count != 0 || session.HtmlWorkspace.DataSources.Count != 0 ||
                session.HtmlWorkspace.ActiveFileId != null || session.HtmlWorkspace.History.Count != 0 || session.HtmlWorkspace.RedoBranches.Count != 0)
                throw TextStoreScope();
            RequireBasicRun(session.LastRun);
            if (session.Revision == 0 && session.Messages.Count != commit.Messages.Count) throw new InvalidOperationException("Creation omitted messages.");
        }

        private static NotSupportedException TextStoreScope()
        { return new NotSupportedException("Journal Store does not yet support this resource, agent, history-rewrite, move/delete or usage operation."); }

        private static ConversationChatSelection DecodeChatSelection(JournalRecord record)
        {
            var fact = DeliveryData<ConversationChatSelection>(record);
            if (record.Kind != "chat.selected" || fact == null || record.Fingerprint != TextPatternEngine.Sha256(JsonConvert.SerializeObject(fact)))
                throw new InvalidDataException("Invalid chat selection.");
            return fact;
        }
    }

    internal sealed class ConversationTextEntry
    {
        public ChatMessage Header { get; private set; }
        public PayloadRef Body { get; private set; }
        public PayloadRef Reasoning { get; private set; }
        [JsonConstructor] internal ConversationTextEntry(ChatMessage header, PayloadRef body, PayloadRef reasoning)
        {
            RequireText(header);
            if (header.Content != null || header.ReasoningContent != null || body == null) throw new ArgumentException("Text metadata cannot contain its body.");
            Header = header; Body = body; Reasoning = reasoning;
        }
        internal static void RequireText(ChatMessage message)
        {
            if (message == null || string.IsNullOrWhiteSpace(message.Id) || message.CreatedUtc.Kind != DateTimeKind.Utc ||
                message.Role != "user" && message.Role != "assistant") throw new ArgumentException("An exact user/assistant text message is required.");
            if (message.SyntheticResourceObservation || message.AgentCommunication != null ||
                message.CompletedOperation != null || message.HtmlWorkspaceCheckpoint != null ||
                message.AttachmentAnalysis != null || (message.Attachments?.Count ?? 0) != 0 ||
                (message.ContextClaims?.Count ?? 0) != 0)
                throw new NotSupportedException("Runtime/resource messages require their typed journal integration.");
        }
    }

    internal sealed class ConversationChatCommit
    {
        public ConversationCard Card { get; private set; }
        public long ExpectedRevision { get; private set; }
        public int MessageCount { get; private set; }
        public IReadOnlyList<ConversationTextEntry> Messages { get; private set; }
        public PayloadRef Context { get; private set; }
        public DateTime CreatedUtc { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public ChatRunRecord Run { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public PayloadRef RunState { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public ConversationRunCheckpoint Checkpoint { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public PayloadRef ContextReceipt { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public ConversationModelEvent ModelEvent { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public ConversationRuntimeTools Tools { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public IReadOnlyList<ModelResourceReference> References { get; internal set; }
        [JsonConstructor] internal ConversationChatCommit(ConversationCard card, long expectedRevision, int messageCount,
            IReadOnlyList<ConversationTextEntry> messages, PayloadRef context, DateTime createdUtc,
            ChatRunRecord run = null, PayloadRef runState = null, ConversationRunCheckpoint checkpoint = null,
            PayloadRef contextReceipt = null, ConversationModelEvent modelEvent = null, ConversationRuntimeTools tools = null, IReadOnlyList<ModelResourceReference> references = null)
        {
            if (card == null || expectedRevision < 0 || messageCount < 0 || messages == null || messages.Count > 128 || context == null ||
                messages.Any(item => item == null) || messages.Select(item => item.Header.Id).Distinct(StringComparer.Ordinal).Count() != messages.Count ||
                createdUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("Invalid bounded chat commit.");
            Card = card; ExpectedRevision = expectedRevision; MessageCount = messageCount;
            Messages = messages; Context = context; CreatedUtc = createdUtc;
            if (run?.KernelState != null || runState != null && run == null || checkpoint != null &&
                (checkpoint.ChatId != card.ChatId || checkpoint.RunId != run?.RunId || checkpoint.Body.Sha256 != runState?.Sha256))
                throw new ArgumentException("Runtime metadata and checkpoint identity disagree.");
            Tools = tools; References = references;
            Run = run; RunState = runState; Checkpoint = checkpoint; ContextReceipt = contextReceipt; ModelEvent = modelEvent;
        }
    }

    internal sealed class ConversationChatReceipt
    {
        internal readonly ConversationChatCommit Fact;
        internal readonly string Operation;
        internal readonly JournalCursor Position;
        internal ConversationChatReceipt(ConversationChatCommit fact, string operation, JournalCursor position)
        { Fact = fact; Operation = operation; Position = position; }
    }

    internal sealed class ConversationChatSelection
    {
        public string ChatId { get; private set; }
        [JsonConstructor] internal ConversationChatSelection(string chatId)
        { if (string.IsNullOrWhiteSpace(chatId)) throw new ArgumentException("Chat identity is required."); ChatId = chatId; }
    }
}
