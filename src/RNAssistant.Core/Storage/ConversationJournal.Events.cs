using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;

namespace RNAssistant.Core.Storage
{
    internal sealed partial class ConversationJournal : IEventStore
    {
        SessionEvent IEventStore.Append(ChatSession session, SessionEventWrite write)
        {
            if (write == null) throw new ArgumentNullException(nameof(write));
            SessionEventDescriptors.EnsureEventPortWritable(write.Descriptor);
            var trace = write.Data as LlmTraceRecord;
            if (trace == null || write.Payload != null || trace.PayloadUtf8Bytes != null || trace.PayloadJson != null)
                throw TextStoreScope();
            if (session?.LastRun == null || trace.SessionId != session.Id || trace.RunId != session.LastRun.RunId ||
                write.Correlation?.RunId != session.LastRun.RunId || write.Correlation.TurnId != session.LastRun.TurnId)
                throw new InvalidOperationException("Model event belongs to another run.");
            RequireTextRoot();
            using (RuntimeStorageActivity.Acquire(_paths))
            {
                var item = new ConversationModelEvent(write.Descriptor.Kind, write.Correlation.RunId,
                    write.Correlation.TurnId, write.Correlation.StepId, StoreChatJson(trace));
                var command = new ConversationCommit();
                ConversationChatReceipt receipt;
                try { receipt = CommitChat(session, command, item); }
                catch (IOException)
                {
                    var own = Access((db, cursor) => FindChatWrite(db, command.OperationId), CancellationToken.None);
                    if (own?.Fact.Card.ChatId == session.Id) ApplyChatReceipt(session, own);
                    throw;
                }
                ApplyChatReceipt(session, receipt);
                return ProjectChatEvent(receipt);
            }
        }

        private SessionEvent ProjectChatEvent(ConversationChatReceipt receipt)
        {
            var item = receipt.Fact.ModelEvent;
            return new SessionEvent { SessionId = receipt.Fact.Card.ChatId, Sequence = receipt.Position.Sequence,
                EventId = receipt.Operation, CreatedUtc = receipt.Fact.CreatedUtc, Hash = receipt.Position.Hash,
                Type = item == null ? "chat.committed" : SessionEventDescriptors.For(item.Kind).Type,
                RunId = item?.RunId ?? receipt.Fact.Run?.RunId, TurnId = item?.TurnId ?? receipt.Fact.Run?.TurnId, StepId = item?.StepId,
                Data = item == null ? JObject.FromObject(receipt.Fact) : JObject.Parse(ReadChatBody(item.Body)) };
        }

        IReadOnlyList<SessionEvent> IEventStore.ReadToolPackAdmissions(ConversationMetadata session, IReadOnlyList<string> runs)
        {
            // This integration cannot write tool calls/admissions. Validate the
            // addressed canonical generation before reporting that absence.
            if (runs == null) throw new ArgumentNullException(nameof(runs));
            RequireChatRevision(session, ReadChat(session.Id));
            return new SessionEvent[0];
        }

        private static void RequireChatRevision(ConversationMetadata metadata, ConversationChatReceipt chat)
        {
            if (metadata == null || chat == null || metadata.Id != chat.Fact.Card.ChatId || metadata.Revision != chat.Position.Sequence ||
                !MatchesAddress(chat, metadata.Host, metadata.DocumentKey) || metadata.WorkspaceId != chat.Fact.Card.Address.WorkspaceId ||
                metadata.DocumentAuthorityId != chat.Fact.Card.Address.DocumentAuthorityId)
                throw new ChatConcurrencyException("The event read requires this exact chat revision and scope.");
        }

        FinishEvidenceSnapshot IEventStore.ReadFinishEvidence(ChatSession session, string runId, IReadOnlyList<string> references)
        {
            return Access((db, cursor) => {
                var current = TextChat(db, session.Id); RequireChatRevision(ConversationMetadata.From(session), current);
                var run = CurrentRun(db, session.Id);
                if (run == null || run.Fact.RunId != runId || current.Fact.Run?.RunId != runId ||
                    run.Fact.ToolStepsUsed != 0 || !SameRunValue(run.Fact.Counts, new RNAssistant.Core.Agent.ToolCounts()))
                    throw TextStoreScope();
                return new FinishEvidenceSnapshot(new SessionEventPosition(session.Id, current.Position.Sequence, current.Position.Hash),
                    new ConversationFinishEvidenceAdmission.Fact[0], new string[0]);
            }, CancellationToken.None);
        }

        IReadOnlyList<SessionEvent> IEventStore.Read(ConversationMetadata session, SessionEventReadMode mode)
        {
            if (mode != SessionEventReadMode.Validated && mode != SessionEventReadMode.RequireComplete) throw TextStoreScope();
            RequireTextRoot();
            using (RuntimeStorageActivity.Acquire(_paths))
            {
                var receipts = Access((db, cursor) => {
                    var current = TextChat(db, session.Id); RequireChatRevision(session, current);
                    var operations = new List<string>();
                    using (var command = Command(db, "SELECT operation FROM chat_write WHERE stream=$stream AND chat=$chat ORDER BY sequence"))
                    {
                        command.Parameters.AddWithValue("$chat", session.Id);
                        using (var reader = command.ExecuteReader()) while (reader.Read()) operations.Add(reader.GetString(0));
                    }
                    return operations.Select(operation => FindChatWrite(db, operation)).ToArray();
                }, CancellationToken.None);
                return receipts.Select(ProjectChatEvent).ToArray();
            }
        }

        ISessionEventReader IEventStore.OpenReader(ConversationMetadata session) { throw TextStoreScope(); }
        string IEventStore.ReadPayload(ConversationMetadata session, SessionEvent item)
        {
            RequireChatRevision(session, ReadChat(session.Id));
            if (item?.SessionId != session.Id || item.Payload != null) throw TextStoreScope();
            return null; // raw model capture belongs to the existing optional RAM buffer
        }
    }

    internal sealed class ConversationModelEvent
    {
        public SessionEventKind Kind { get; private set; }
        public string RunId { get; private set; }
        public string TurnId { get; private set; }
        public string StepId { get; private set; }
        public PayloadRef Body { get; private set; }
        [JsonConstructor] internal ConversationModelEvent(SessionEventKind kind, string runId, string turnId, string stepId, PayloadRef body)
        {
            if (kind != SessionEventKind.ModelRequestPrepared && kind != SessionEventKind.ModelResponseReceived &&
                kind != SessionEventKind.ModelResponseAccepted && kind != SessionEventKind.ModelAttemptRejected && kind != SessionEventKind.ModelFailure ||
                string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(turnId) || body == null)
                throw new ArgumentException("An exact model manifest/diagnostic event is required.");
            Kind = kind; RunId = runId; TurnId = turnId; StepId = stepId; Body = body;
        }
    }
}
