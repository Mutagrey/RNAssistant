using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Storage
{
    internal sealed partial class ConversationJournal
    {
        internal static bool HasCheckpoint(JournalRecord record) { return DecodeChatCommit(record).Checkpoint != null; }

        private PayloadRef StoreChatJson(object value)
        { return PayloadRef.FromBlob(_bodies.StoreText(JsonConvert.SerializeObject(value), "application/json")); }

        private static ChatRunRecord CopyRunHeader(ChatRunRecord run)
        {
            if (run == null) return null;
            var copy = JsonConvert.DeserializeObject<ChatRunRecord>(JsonConvert.SerializeObject(run));
            if (copy.KernelState != null)
            {
                var phase = copy.Phase; ConversationRunProjection.Apply(copy); copy.Phase = phase ?? copy.Phase;
            }
            copy.KernelState = null;
            return copy;
        }

        private static void RequireBasicRun(ChatRunRecord run)
        {
            if (run == null) return;
            var state = run.KernelState; var summary = state?.Summary;
            if (summary != null && (summary.RunId != run.RunId || summary.TurnId != run.TurnId)) throw TextStoreScope();
            // Procedure selections are reference-only run metadata. SkillTaskService
            // revalidates publication and every exact tool pin before granting exposure.
            var selected = run.SelectedSkills ?? new List<SelectedSkill>();
            if (selected.Any(item => item == null || string.IsNullOrWhiteSpace(item.Id) || (item.Revision?.Length != 64 || item.Revision.Any(c => !Uri.IsHexDigit(c))) ||
                item.Publication != null && !item.Publication.IsExact || item.Tools == null ||
                item.Tools.Any(pin => pin == null || string.IsNullOrWhiteSpace(pin.Id) || (pin.Revision?.Length != 64 || pin.Revision.Any(c => !Uri.IsHexDigit(c)))) ||
                item.Tools.Select(pin => pin.Id).Distinct(StringComparer.Ordinal).Count() != item.Tools.Count) ||
                selected.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != selected.Count)
                throw new InvalidDataException("Skill selections require exact immutable publication and tool pins.");
        }

        private string ChatMessageOperation(SqliteConnection db, string chat, string id, bool includeHidden = false)
        {
            using (var command = Command(db, "SELECT operation FROM chat_message WHERE stream=$stream AND chat=$chat AND id=$id" + (includeHidden ? "" : " AND ordinal>=0")))
            {
                command.Parameters.AddWithValue("$chat", chat); command.Parameters.AddWithValue("$id", id);
                return command.ExecuteScalar() as string;
            }
        }

        private ConversationTextEntry ChatEntry(SqliteConnection db, string chat, string id)
        {
            var operation = ChatMessageOperation(db, chat, id);
            if (operation == null) return null;
            var saved = FindChatWrite(db, operation);
            if (saved?.Fact.Card.ChatId != chat) throw new InvalidDataException("Foreign message update locator.");
            return saved.Fact.Messages.Single(item => item.Header.Id == id);
        }

        private ConversationRunCheckpoint PrepareStoreCheckpoint(SqliteConnection db, ConversationRunCommit commit,
            ChatSession session, PayloadRef body, ConversationRunCheckpoint retry)
        {
            var run = session.LastRun; var state = run?.KernelState;
            if (state == null || body == null)
                throw TextStoreScope();
            var prior = CurrentRun(db, session.Id);
            var summary = state.Summary;
            if (commit.ClaimsContinuation && retry == null)
            {
                if (prior?.Fact.RunId != run.RunId || prior.Fact.Lifecycle != RunLifecycle.Waiting ||
                    summary.Lifecycle != RunLifecycle.Waiting || commit.FactKind != AgentRunEventKind.SummaryChanged)
                    throw new InvalidOperationException("Continuation requires its saved waiting run.");
                var saved = JsonConvert.DeserializeObject<AgentRunState>(ReadChatBody(prior.Fact.Body));
                if (!SameRunValue(saved.Summary.PendingWait, summary.PendingWait) || saved.Summary.PauseId != summary.PauseId ||
                    saved.Summary.IterationsUsed != summary.IterationsUsed || saved.Summary.ToolStepsUsed != summary.ToolStepsUsed)
                    throw new InvalidOperationException("Continuation cannot replace its saved condition, deadline or budget.");
            }
            var resume = retry?.Resume ?? (commit.ClaimsContinuation || prior?.Fact.RunId == run.RunId &&
                prior.Fact.Lifecycle == RunLifecycle.AwaitingConfirmation && summary.Lifecycle == RunLifecycle.Running);
            if (resume && commit.FactKind != AgentRunEventKind.SummaryChanged)
                throw new InvalidOperationException("Continuation must claim its saved run before entering the runtime.");
            var invocation = retry?.InvocationId ?? (resume || prior?.Fact.RunId != run.RunId ? commit.OperationId : prior.Fact.InvocationId);
            return new ConversationRunCheckpoint(session.Id, run.RunId, run.TurnId, invocation,
                retry?.Revision ?? (prior?.Fact.Revision ?? 0) + 1, retry == null ? prior?.Position.Hash : retry.PreviousHash,
                resume, summary.Lifecycle, summary.PauseId, summary.IterationsUsed, summary.ToolStepsUsed,
                summary.ToolCounts, state.Limits, ConversationRunPolicy.ForAcceptedRun(session), run.ModelConfiguration,
                body, RunReferences(state));
        }

        private void ValidateChatRun(SqliteConnection db, JournalRecord record, ConversationChatCommit fact)
        {
            RequireBasicRun(fact.Run);
            var prior = CurrentRun(db, fact.Card.ChatId);
            if (fact.Checkpoint != null)
            {
                ValidateRun(db, record.Operation, fact.Checkpoint);
                if (fact.Run?.RunId != fact.Checkpoint.RunId || fact.Run.TurnId != fact.Checkpoint.TurnId ||
                    fact.RunState?.Sha256 != fact.Checkpoint.Body.Sha256) throw new InvalidDataException("Foreign run checkpoint.");
            }
            else if (fact.RunState != null)
            {
                if (prior?.Fact.RunId != fact.Run?.RunId || prior.Fact.TurnId != fact.Run.TurnId ||
                    !SameRunValue(prior.Fact.Body, fact.RunState))
                    throw new InvalidOperationException("Only a kernel commit can change accepted run state.");
            }
            else if (prior != null && (!TerminalRun(prior.Fact.Lifecycle) || prior.Fact.RunId == fact.Run?.RunId))
                throw new InvalidOperationException("A pending run cannot erase accepted runtime state.");
            var accepted = fact.Checkpoint ?? prior?.Fact;
            if (fact.RunState != null && accepted != null && (!SameRunValue(fact.Run.ConversationPolicy, accepted.Policy) ||
                !SameRunValue(fact.Run.ModelConfiguration, accepted.Model)))
                throw new InvalidOperationException("Metadata cannot replace the accepted run policy/model.");
            if (fact.ModelEvent != null && (fact.Run == null || fact.ModelEvent.RunId != fact.Run.RunId ||
                fact.ModelEvent.TurnId != fact.Run.TurnId || fact.RunState == null))
                throw new InvalidOperationException("Model manifest requires its accepted exact run.");
        }

        void IConversationStore.CommitRun(ConversationRunCommit commit)
        {
            if (commit == null) throw new ArgumentNullException(nameof(commit));
            RequireTextRoot();
            var state = commit.CreateStateProjection();
            try { var receipt = CommitChat(state, commit); ApplyChatReceipt(state, receipt); ApplyMessageReferences(commit, receipt); }
            catch (IOException)
            {
                var own = Access((db, cursor) => FindChatWrite(db, commit.OperationId), CancellationToken.None);
                if (own?.Fact.Card.ChatId == state.Id) { ApplyChatReceipt(state, own); ApplyMessageReferences(commit, own); }
                throw;
            }
            finally { commit.RecordStorageState(state, false, true); }
        }

        private ChatRunRecord LoadChatRun(ConversationChatCommit fact)
        {
            var run = CopyRunHeader(fact.Run);
            if (fact.RunState != null)
            {
                run.KernelState = JsonConvert.DeserializeObject<AgentRunState>(ReadChatBody(fact.RunState));
                RequireBasicRun(run);
            }
            return run;
        }
    }
}
