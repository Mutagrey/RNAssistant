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
        private static bool IsChatFact(string kind) { return kind == "chat.committed" || kind == "chat.selected" || IsChatDeletion(kind) || IsChatMove(kind); }

        private static string ChatFingerprint(ConversationChatCommit fact)
        {
            var data = JObject.FromObject(fact); data.Remove("CreatedUtc");
            return TextPatternEngine.Sha256(data.ToString(Formatting.None));
        }

        private static ConversationChatCommit DecodeChatCommit(JournalRecord record)
        {
            if (record.Kind == "agent.spawned") return DecodeSpawn(record).Chat ?? throw new InvalidDataException("Spawn has no integrated chat.");
            var fact = DeliveryData<ConversationChatCommit>(record);
            if (record.Kind != "chat.committed" || fact == null || record.Fingerprint != ChatFingerprint(fact))
                throw new InvalidDataException("Invalid chat commit.");
            return fact;
        }

        private static IEnumerable<PayloadRef> ChatReferences(JournalRecord record)
        {
            if (IsChatMove(record.Kind)) { DecodeChatMove(record); return new PayloadRef[0]; }
            if (record.Kind == "chat.deleted") { DecodeChatDeletion(record); return new PayloadRef[0]; }
            if (record.Kind == "document.deleted") { DecodeDocumentDeletion(record); return new PayloadRef[0]; }
            if (record.Kind == "chat.selected") { DecodeChatSelection(record); return new PayloadRef[0]; }
            var fact = DecodeChatCommit(record);
            return new[] { fact.Context, fact.ContextReceipt, fact.RunState, fact.ModelEvent?.Body, fact.History, fact.Resources }.Concat(fact.Messages.SelectMany(item => new[] { item.Body, item.Reasoning, item.Analysis }.Concat(MessageReferences(item.Header))))
                .Concat(fact.Tools?.Terminal == null ? new PayloadRef[0] : MessageReferences(fact.Tools.Terminal.Result))
                .Where(item => item != null).Concat(fact.Checkpoint?.References ?? new PayloadRef[0]).Concat(fact.Input?.Body == null ? new PayloadRef[0] : new[] { fact.Input.Body })
                .Concat(AttachmentReferences(fact.Input?.Header?.Attachments)).Concat((fact.Snapshots ?? new ChatArtifact[0]).Select(SnapshotBody));
        }

        private ConversationChatReceipt FindChatWrite(SqliteConnection db, string operation, string chat = null)
        {
            using (var command = Command(db, "SELECT operation,fingerprint,segment,start_offset,end_offset,sequence,hash,first_sequence FROM chat_write WHERE stream=$stream AND operation=$operation" + (chat == null ? "" : " AND chat=$chat")))
            {
                command.Parameters.AddWithValue("$operation", operation);
                if (chat != null) command.Parameters.AddWithValue("$chat", chat);
                JournalCursor position; var record = LocatedRecord(command, out position);
                return record == null ? null : new ConversationChatReceipt(ReadChatWrite(db, record, position, chat), record.Operation, position);
            }
        }

        private ConversationChatReceipt CurrentChat(SqliteConnection db, string chat)
        {
            var operation = Scalar(db, "SELECT operation FROM chat_state WHERE stream=$stream AND chat=$chat", chat) as string;
            if (operation == null) return null;
            var receipt = FindChatWrite(db, operation, chat);
            if (receipt == null || receipt.Fact.Card.ChatId != chat || receipt.Fact.MessageCount !=
                Convert.ToInt32(Scalar(db, "SELECT message_count FROM chat_state WHERE stream=$stream AND chat=$chat", chat)))
                throw new InvalidDataException("Chat metadata disagrees with its canonical commit.");
            var head = Scalar(db, "SELECT head_operation FROM chat_state WHERE stream=$stream AND chat=$chat", chat) as string;
            if (head != operation)
            {
                var delivery = FindDelivery(db, "operation", head);
                if (delivery == null)
                {
                    var control = FindControl(db, head);
                    if (ControlAffects(control, chat))
                    {
                        if (control.Position.Sequence <= receipt.Position.Sequence) throw new InvalidDataException("Control head is not newer than chat metadata.");
                        return new ConversationChatReceipt(receipt.Fact, receipt.Operation, control.Position, control.Fact.ChangedUtc);
                    }
                    var spawn = FindSpawn(db, head);
                    if (spawn?.Fact.Chat == null || spawn.Fact.Sender != chat || spawn.Position.Sequence <= receipt.Position.Sequence)
                        throw new InvalidDataException("Chat head does not identify its child creation.");
                    return new ConversationChatReceipt(receipt.Fact, receipt.Operation, spawn.Position, spawn.Fact.Child.CreatedUtc);
                }
                if (delivery?.Fact.Input == null || delivery.Position.Sequence <= receipt.Position.Sequence ||
                    delivery.Fact.Sender != chat && delivery.Fact.Recipient != chat)
                    throw new InvalidDataException("Chat head does not identify its peer delivery.");
                return new ConversationChatReceipt(receipt.Fact, receipt.Operation, delivery.Position, delivery.Fact.CreatedUtc);
            }
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
            if (IsChatMove(record.Kind)) { ValidateChatMove(db, record); return new PayloadRef[0]; }
            if (record.Kind == "chat.deleted") { ValidateChatDeletion(db, record); return new PayloadRef[0]; }
            if (record.Kind == "document.deleted") { ValidateDocumentDeletion(db, record); return new PayloadRef[0]; }
            if (record.Kind == "chat.selected")
            {
                var selected = DecodeChatSelection(record);
                if (selected.ChatId != null && CurrentChat(db, selected.ChatId) == null) throw new InvalidOperationException("Selected chat is unavailable.");
                return new PayloadRef[0];
            }
            var fact = DecodeChatCommit(record); ValidateChatAddress(fact.Card);
            var current = TextChat(db, fact.Card.ChatId);
            if (!SameRunValue(current?.Fact.Card.AgentCreation, fact.Card.AgentCreation))
                throw new InvalidOperationException("Only native spawn can establish immutable child provenance.");
            if (fact.ExpectedRevision != (current?.Position.Sequence ?? 0))
                throw new ChatConcurrencyException("Chat changed before the journal commit.");
            if (current == null && (CardRecord(db, fact.Card.ChatId) != null ||
                Scalar(db, "SELECT 1 FROM call_fact WHERE stream=$stream AND chat=$chat LIMIT 1", fact.Card.ChatId) != null))
                throw new InvalidOperationException("Chat belongs to an integration not supported by the text store yet.");
            if (current != null && (!JToken.DeepEquals(JObject.FromObject(current.Fact.Card.Address), JObject.FromObject(fact.Card.Address)) ||
                !(current.Fact.Card.PreviousDocumentKeys ?? new string[0]).SequenceEqual(fact.Card.PreviousDocumentKeys ?? new string[0]) ||
                current.Fact.Card.CreatedUtc != fact.Card.CreatedUtc))
                throw new InvalidOperationException("A metadata update cannot move chat ownership or replace creation identity.");
            if (current != null && !SameRunValue(current.Fact.Fork, fact.Fork))
                throw new InvalidOperationException("A commit cannot replace fork provenance.");
            if (current == null && fact.Fork != null) ValidateFork(db, fact);
            else if (fact.History != null) ValidateHistory(db, record, fact, current);
            ValidateLocalSnapshots(db, fact, ReadResourceSet(fact.Resources, fact.Card.Address));
            if (fact.Messages.Any(item => item.Header.HtmlWorkspaceCheckpoint != null && !IsHtmlSnapshot(fact.Card.Address, item.Header.HtmlWorkspaceCheckpoint)))
                throw new InvalidDataException("HTML checkpoint must identify an exact document snapshot.");
            ValidateMessageAttachments(db, fact);
            var added = 0;
            foreach (var message in fact.Messages)
            {
                if (fact.History != null) continue; // Explicit user edits were validated with their membership manifest.
                ValidatePeerMessage(db, fact.Card.ChatId, message);
                var prior = ChatEntry(db, fact.Card.ChatId, message.Header.Id);
                if (prior == null)
                {
                    if (ChatMessageOperation(db, fact.Card.ChatId, message.Header.Id, includeHidden: true) != null)
                        throw new InvalidOperationException("A normal append cannot restore a removed occurrence.");
                    added++;
                    if (fact.Checkpoint == null && (message.Header.ProtocolMessage || message.Header.RuntimeProjection || message.Header.Activity != null) &&
                        !IsCheckpointMessage(message.Header, fact.Snapshots ?? new ChatArtifact[0]))
                        throw new InvalidOperationException("New runtime messages require their atomic kernel checkpoint.");
                }
                else if ((prior.Header.Activity == null || message.Header.Id != fact.Tools?.ActivityId) && !SameRunValue(prior.Body, message.Body) ||
                    !SameRunValue(prior.Reasoning, message.Reasoning) ||
                    !SameRunValue(prior.Header.Attachments, message.Header.Attachments) ||
                    prior.Header.Role != message.Header.Role || prior.Header.CreatedUtc != message.Header.CreatedUtc ||
                    prior.Header.ProtocolMessage != message.Header.ProtocolMessage || prior.Header.RuntimeProjection != message.Header.RuntimeProjection ||
                    !SameRunValue(prior.Header.AgentCommunication, message.Header.AgentCommunication) ||
                    prior.Header.ResponseModelAttemptId != message.Header.ResponseModelAttemptId ||
                    prior.Header.RunId != null && prior.Header.RunId != message.Header.RunId)
                    throw new InvalidOperationException("A presentation update cannot rewrite retained text or its occurrence identity.");
            }
            if (fact.History == null && fact.MessageCount != (current?.Fact.MessageCount ?? 0) + added)
                throw new InvalidOperationException("Chat message count does not match its exact append/update delta.");
            if (fact.Control != null)
            {
                if (fact.Control.ChatId != fact.Card.ChatId || fact.Control.NativeChat || fact.Checkpoint != null || fact.ModelEvent != null || fact.Tools != null)
                    throw new InvalidDataException("Queue control must identify its exact chat and separate user intent.");
                ValidateControl(db, record.Operation, fact.Control, preservePause: fact.Input != null && fact.Control.PeerControl);
            }
            ValidateChatInput(db, record, fact);
            ValidateChatRun(db, record, fact);
            ValidateChatTools(db, record, fact, keys, outcome);
            return ChatReferences(record);
        }

        private void ApplyChatFact(SqliteConnection db, JournalRecord record, JournalCursor position, Dictionary<string, ContextCallKey> keys = null, ResourceJournalReceipt outcome = null)
        {
            if (IsChatMove(record.Kind)) { ApplyChatMove(db, record, position); return; }
            if (record.Kind == "document.deleted") { ApplyDocumentDeletion(db, record, position); return; }
            ValidateChatFact(db, record, keys, outcome);
            var committed = record.Kind == "chat.committed" ? DecodeChatCommit(record) : null;
            var history = committed?.History == null ? null : ReadHistory(committed.History);
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
                    var selected = DecodeChatSelection(record).ChatId;
                    using (var command = Command(db, selected == null ? "DELETE FROM chat_selection WHERE stream=$stream" : "INSERT OR REPLACE INTO chat_selection VALUES($stream,$chat)", tx))
                    { if (selected != null) command.Parameters.AddWithValue("$chat", selected); command.ExecuteNonQuery(); }
                }
                else if (record.Kind == "chat.deleted") ApplyChatDeletion(db, tx, record, position);
                else
                {
                    var fact = committed;
                    SaveChatMetadata(db, tx, record, position, fact, lastActivity);
                    if (history != null) ApplyHistory(db, tx, fact.Card.ChatId, history);
                    else foreach (var message in fact.Messages)
                        using (var command = Command(db, "INSERT INTO chat_message VALUES($stream,$chat,$id,$ordinal,$operation) ON CONFLICT(stream,chat,id) DO UPDATE SET operation=$operation", tx))
                        {
                            command.Parameters.AddWithValue("$chat", fact.Card.ChatId); command.Parameters.AddWithValue("$id", message.Header.Id);
                            command.Parameters.AddWithValue("$ordinal", existing.Contains(message.Header.Id) ? -1 : ordinal++); command.Parameters.AddWithValue("$operation", record.Operation);
                            command.ExecuteNonQuery();
                        }
                }
                if (committed != null) ApplyForkRows(db, tx, committed);
                if (committed != null) ApplyAttachmentRows(db, tx, record, committed);
                if (committed != null) ApplySnapshotRows(db, tx, record, committed);
                if (committed != null) ApplyChatToolRows(db, tx, record, position, committed, keys, existing, nextEvidence);
                if (committed?.Checkpoint != null) ApplyRunRows(db, tx, record, position, committed.Checkpoint);
                if (committed?.Control != null) ApplyControlRows(db, tx, record, position, committed.Control);
                if (committed?.Input != null) ApplyChatInputRows(db, tx, record, position, committed.Input);
                SaveCursor(db, position, tx, record); tx.Commit();
            }
        }

        private void SaveChatMetadata(SqliteConnection db, SqliteTransaction tx, JournalRecord record,
            JournalCursor position, ConversationChatCommit fact, long lastActivity)
        {
            using (var command = Command(db, "INSERT INTO chat_write VALUES($stream,$chat,$operation,$fingerprint,$segment,$start,$end,$sequence,$hash,$first)", tx))
            { command.Parameters.AddWithValue("$chat", fact.Card.ChatId); AddDeliveryPosition(command, record, position); command.ExecuteNonQuery(); }
            using (var command = Command(db, "INSERT OR REPLACE INTO chat_card VALUES($stream,$chat,$alias,$operation,$fingerprint,$segment,$start,$end,$sequence,$hash,$first)", tx))
            {
                command.Parameters.AddWithValue("$chat", fact.Card.ChatId); command.Parameters.AddWithValue("$alias", AgentChatAddress.IdFor(fact.Card.ChatId));
                AddDeliveryPosition(command, record, position); command.ExecuteNonQuery();
            }
            using (var command = Command(db, "INSERT INTO chat_state VALUES($stream,$chat,$operation,$count,$activity,$operation,0,0,0) ON CONFLICT(stream,chat) DO UPDATE SET operation=$operation,message_count=$count,last_activity_ticks=$activity,head_operation=$operation", tx))
            {
                command.Parameters.AddWithValue("$chat", fact.Card.ChatId); command.Parameters.AddWithValue("$operation", record.Operation);
                command.Parameters.AddWithValue("$count", fact.MessageCount); command.Parameters.AddWithValue("$activity", lastActivity); command.ExecuteNonQuery();
            }
        }

        private ConversationChatReceipt CommitChat(ChatSession session, ConversationCommit commit, ConversationModelEvent modelEvent = null, ConversationInputEvent queueControl = null, PayloadRef inputBody = null)
        {
            RequireTextSession(session, commit); commit.BindJournal(session);
            using (RuntimeStorageActivity.Acquire(_paths))
            {
                var card = new ConversationCard(session.Id, session.Title, ConversationRunPolicy.ForSession(session), session.CreatedUtc,
                    address: new ConversationMetadata(session.Id, 0, session.Host, session.DocumentKey, session.WorkspaceId, session.DocumentAuthorityId),
                    documentTitle: session.DocumentTitle, documentPath: session.DocumentPath, selectedModel: session.Model, reasoningEnabled: session.ReasoningEnabled,
                    titleAssignedByAgent: session.TitleAssignedByAgent, agentCreation: session.AgentCreation,
                    previousDocumentKeys: session.PreviousDocumentKeys);
                ValidateChatAddress(card);
                var creatingFork = commit.JournalRevision == 0 && session.ParentSessionId != null;
                if (creatingFork) RequireForkScope(session);
                var messages = (creatingFork ? session.Messages : commit.Messages.Select(write => write.Message)).Select(PublishText).ToArray();
                var resources = PrepareResourceSet(session);
                var tasks = PrepareSnapshotCandidates(session);
                ValidatePublishedArtifacts(session, messages);
                if (queueControl?.Input != null)
                    queueControl.Input.Attachments = AttachmentHeaders(queueControl.Input.Attachments);
                ResourceJournalReceipt outcome;
                var tools = PrepareChatTools(session, commit as ConversationRunCommit, out outcome);
                var keys = new Dictionary<string, ContextCallKey>(StringComparer.Ordinal);
                foreach (var message in messages.Where(item => !creatingFork && item.Header.AcceptedCallOrigin != null))
                { var call = new ConversationCallFact(session.Id, message.Header); var key = CallFingerprint(call); keys[key] = PrepareContextCallKey(call.Call, key); }
                var context = PrepareContextNotes(session);
                var receiptBody = session.LastContextReceipt == null ? null : StoreChatJson(session.LastContextReceipt);
                var runBody = session.LastRun?.KernelState == null ? null : StoreChatJson(session.LastRun.KernelState);
                var run = CopyRunHeader(session.LastRun);
                foreach (var reference in new[] { context, receiptBody, runBody, modelEvent?.Body, inputBody, resources }
                    .Concat(messages.SelectMany(item => new[] { item.Body, item.Reasoning, item.Analysis }.Concat(MessageReferences(item.Header))))
                    .Concat(AttachmentReferences(queueControl?.Input?.Attachments))
                    .Concat(tools?.Terminal == null ? new PayloadRef[0] : MessageReferences(tools.Terminal.Result)).Where(item => item != null))
                    if (!_bodies.HasVerifiedReference(reference.ToBlobReference())) throw new InvalidDataException("Chat body is unavailable before acceptance.");
                return Access((db, cursor) => {
                    if (!creatingFork && !(commit is ConversationRunCommit) && messages.Any(item =>
                        (item.Header.ToolCallId != null || item.Header.Activity != null) &&
                        ChatEntry(db, session.Id, item.Header.Id) == null && !IsCheckpointMessage(item.Header, tasks))) throw TextStoreScope();
                    ValidateLocalOriginals(db, session, messages);
                    var prior = FindChatWrite(db, commit.OperationId);
                    PayloadRef history;
                    var fork = PrepareFork(db, session, commit, prior, ref messages, out history);
                    if (!creatingFork) history = PrepareHistory(db, session, commit, prior, ref messages);
                    var checkpoint = commit is ConversationRunCommit runtime
                        ? PrepareStoreCheckpoint(db, runtime, session, runBody, prior?.Fact.Checkpoint) : null;
                    var priorControl = queueControl == null ? null : QueueControl(db, session.Id);
                    var control = queueControl == null ? null : prior?.Fact.Control ??
                        PrepareChatControl(db, session, queueControl, priorControl);
                    var input = queueControl?.Input == null ? null : PrepareChatInput(db, session, queueControl.Input, inputBody);
                    var fact = new ConversationChatCommit(card, commit.JournalRevision, commit.JournalMessageCount, messages, context, DateTime.UtcNow,
                        run, runBody, checkpoint, receiptBody, modelEvent, tools, control: control, input: input, history: history, resources: resources, snapshots: PrepareSnapshotDelta(db, session.Id, tasks, prior), fork: fork);
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
            header.Attachments = AttachmentHeaders(message.Attachments);
            var analysis = message.AttachmentAnalysis == null ? null :
                PayloadRef.FromBlob(_bodies.StoreText(message.AttachmentAnalysis.Content ?? string.Empty, "text/plain"));
            if (header.AttachmentAnalysis != null) header.AttachmentAnalysis.Content = null;
            return new ConversationTextEntry(header, PayloadRef.FromBlob(_bodies.StoreText(message.Content ?? string.Empty, "text/plain")),
                message.ReasoningContent == null ? null : PayloadRef.FromBlob(_bodies.StoreText(message.ReasoningContent, "text/plain")), analysis);
        }

        private ChatMessage ReadText(ConversationTextEntry entry)
        {
            var message = entry.Header.CopyForContext();
            message.Content = ReadChatBody(entry.Body);
            message.ReasoningContent = entry.Reasoning == null ? null : ReadChatBody(entry.Reasoning);
            if (message.AttachmentAnalysis != null) message.AttachmentAnalysis.Content = ReadChatBody(entry.Analysis);
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
            if (commit.RemovedMessageIds.Count != 0 && !commit.RewritesHistory ||
                session.ParentSessionId == null && (session.ParentSessionRevision != null || session.ForkedThroughMessageId != null) ||
                session.ResourceCopies.Count != 0 ||
                session.ActiveHtmlArtifactId == null && (session.HtmlWorkspace.Files.Count != 0 || session.HtmlWorkspace.DataSources.Count != 0 ||
                !string.IsNullOrWhiteSpace(session.HtmlWorkspace.ActiveFileId) || session.HtmlWorkspace.History.Count != 0 || session.HtmlWorkspace.RedoBranches.Count != 0))
                throw TextStoreScope();
            RequireBasicRun(session.LastRun);
            if (session.Revision == 0 && session.ParentSessionId == null && session.Messages.Count != commit.Messages.Count) throw new InvalidOperationException("Creation omitted messages.");
        }

        private static NotSupportedException TextStoreScope()
        { return new NotSupportedException("Journal Store does not yet support this resource or fork operation."); }

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
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public PayloadRef Analysis { get; private set; }
        [JsonConstructor] internal ConversationTextEntry(ChatMessage header, PayloadRef body, PayloadRef reasoning, PayloadRef analysis = null)
        {
            RequireText(header);
            if (header.Content != null || header.ReasoningContent != null || body == null) throw new ArgumentException("Text metadata cannot contain its body.");
            if (header.Attachments?.Any(item => item.ExtractedText != null) == true ||
                header.AttachmentAnalysis?.Content != null || (header.AttachmentAnalysis == null) != (analysis == null))
                throw new ArgumentException("Attachment bodies cannot be stored in the message header.");
            ConversationJournal.AttachmentHeaders(header.Attachments);
            Header = header; Body = body; Reasoning = reasoning; Analysis = analysis;
        }
        internal static void RequireText(ChatMessage message)
        {
            if (message == null || string.IsNullOrWhiteSpace(message.Id) || message.CreatedUtc.Kind != DateTimeKind.Utc ||
                message.Role != "user" && message.Role != "assistant") throw new ArgumentException("An exact user/assistant text message is required.");
            if (message.SyntheticResourceObservation ||
                message.CompletedOperation != null ||
                (message.ContextClaims?.Count ?? 0) != 0)
                throw new NotSupportedException("Runtime/resource messages require their typed journal integration.");
            if ((message.Attachments?.Count > 0 || message.AttachmentAnalysis != null) &&
                (message.Role != "user" || message.ProtocolMessage))
                throw new InvalidDataException("Only a user message can own input attachments.");
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
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public PayloadRef History { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public ConversationFork Fork { get; private set; }
        [JsonProperty("Attachments", NullValueHandling = NullValueHandling.Ignore)] public PayloadRef Resources { get; private set; }
        [JsonProperty("TaskLists", NullValueHandling = NullValueHandling.Ignore)] public IReadOnlyList<ChatArtifact> Snapshots { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public ConversationQueueControl Control { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public ConversationInputState Input { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public IReadOnlyList<ModelResourceReference> References { get; internal set; }
        [JsonConstructor] internal ConversationChatCommit(ConversationCard card, long expectedRevision, int messageCount,
            IReadOnlyList<ConversationTextEntry> messages, PayloadRef context, DateTime createdUtc,
            ChatRunRecord run = null, PayloadRef runState = null, ConversationRunCheckpoint checkpoint = null,
            PayloadRef contextReceipt = null, ConversationModelEvent modelEvent = null, ConversationRuntimeTools tools = null, IReadOnlyList<ModelResourceReference> references = null, ConversationQueueControl control = null, ConversationInputState input = null, PayloadRef history = null, [JsonProperty("Attachments")] PayloadRef resources = null, [JsonProperty("TaskLists")] IReadOnlyList<ChatArtifact> snapshots = null, ConversationFork fork = null)
        {
            if (card == null || expectedRevision < 0 || messageCount < 0 || messages == null || messages.Count > 128 || context == null ||
                messages.Any(item => item == null) || messages.Select(item => item.Header.Id).Distinct(StringComparer.Ordinal).Count() != messages.Count ||
                createdUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("Invalid bounded chat commit.");
            Card = card; ExpectedRevision = expectedRevision; MessageCount = messageCount;
            Messages = messages; Context = context; CreatedUtc = createdUtc;
            if (run?.KernelState != null || runState != null && run == null || checkpoint != null &&
                (checkpoint.ChatId != card.ChatId || checkpoint.RunId != run?.RunId || checkpoint.Body.Sha256 != runState?.Sha256))
                throw new ArgumentException("Runtime metadata and checkpoint identity disagree.");
            Tools = tools; References = references; Control = control; Input = input; History = history; Resources = resources; Snapshots = snapshots; Fork = fork;
            Run = run; RunState = runState; Checkpoint = checkpoint; ContextReceipt = contextReceipt; ModelEvent = modelEvent;
        }
    }

    internal sealed class ConversationChatReceipt
    {
        internal readonly ConversationChatCommit Fact;
        internal readonly string Operation;
        internal readonly JournalCursor Position;
        internal readonly DateTime UpdatedUtc;
        internal ConversationChatReceipt(ConversationChatCommit fact, string operation, JournalCursor position, DateTime? updatedUtc = null)
        { Fact = fact; Operation = operation; Position = position; UpdatedUtc = updatedUtc ?? fact.CreatedUtc; }
    }

    internal sealed class ConversationChatSelection
    {
        public string ChatId { get; private set; }
        [JsonConstructor] internal ConversationChatSelection(string chatId)
        { if (chatId != null && string.IsNullOrWhiteSpace(chatId)) throw new ArgumentException("Chat identity is required."); ChatId = chatId; }
    }
}
