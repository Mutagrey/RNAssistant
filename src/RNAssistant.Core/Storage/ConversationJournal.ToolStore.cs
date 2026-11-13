using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using RNAssistant.Core.Agent;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Storage
{
    internal sealed partial class ConversationJournal
    {
        private ConversationRuntimeTools PrepareChatTools(ChatSession session, ConversationRunCommit command, out ResourceJournalReceipt outcome)
        {
            outcome = null;
            if (command == null) return null;
            var context = command.ToolContext; var execution = command.Execution;
            if (context != null && (context.RunId != command.RunId || context.TurnId != session.LastRun.TurnId))
                throw new InvalidOperationException("Tool context belongs to another run.");
            if (command.FactKind == AgentRunEventKind.ToolStarted &&
                !SameRunValue(context, session.LastRun.KernelState.InFlightTool))
                throw new InvalidOperationException("Dispatch requires its exact in-flight checkpoint.");
            if (context?.Policy.MayHaveSideEffects == true && (context.Call.Name != ResourceMutationToolCatalog.Write &&
                context.Call.Name != AgentCommunicationToolCatalog.Group && !IsPeerMutation(context.Call.Name) ||
                context.Policy.Policy?.ExecutionClass != ToolExecutionClass.ManagedMutation))
                throw TextStoreScope();
            if (command.FactKind == AgentRunEventKind.ToolWaiting)
            {
                var waiting = session.LastRun.KernelState.Summary.PendingWait;
                if (waiting == null || execution?.Outcome != ToolExecutionOutcome.Waiting || execution.MayHaveDispatched ||
                    execution.Evidence.Effect != ToolEffectEvidence.None || !SameRunValue(waiting.Context, context) ||
                    !SameRunValue(waiting.Condition, execution.WaitCondition))
                    throw new InvalidDataException("Waiting requires its exact observation without dispatch or terminal.");
            }
            var awaiting = execution?.Outcome == ToolExecutionOutcome.AwaitingConfirmation;
            if (awaiting)
            {
                var pending = session.LastRun.KernelState.Summary.PendingConfirmation;
                if (pending == null || pending.PendingId != execution.PendingId || pending.StepId != context.StepId ||
                    !SameRunValue(pending.Call, context.Call) || !SameRunValue(pending.Policy, context.Policy) ||
                    pending.PreparedStateJson != execution.PreparedStateJson || execution.MayHaveDispatched ||
                    execution.Evidence.Effect != ToolEffectEvidence.None)
                    throw new InvalidDataException("Pending approval requires its exact preparation without dispatch.");
            }
            ConversationResultFact terminal = null;
            if (command.FactKind == AgentRunEventKind.ToolCompleted && !command.ResultProjection &&
                execution?.Outcome != ToolExecutionOutcome.AwaitingConfirmation)
            {
                if (execution == null) throw new InvalidOperationException("A kernel terminal requires execution evidence.");
                if (context.Policy.MayHaveSideEffects && !IsPeerMutation(context.Call.Name))
                    outcome = new ResourceJournal(_paths, _protector).CallOutcome(MutationOrigin(session, context));
                if (execution.AuthorityCommitId != null && execution.AuthorityCommitId != outcome?.CommitId ||
                    execution.ResourceEffect != null && !SameRunValue(execution.ResourceEffect, outcome?.Effect))
                    throw new InvalidDataException("Execution refers to a foreign or unavailable resource outcome.");
                var materialized = new ToolResultMaterialization(ToolResultPersistence.Terminal(execution),
                    resourceEvidence: execution.ResourceEvidence, resourceEffect: outcome?.Effect, authorityCommitId: outcome?.CommitId)
                    { ExecutionProgress = ToolExecutionProgress.Capture(execution) };
                terminal = new ConversationResultFact(session.Id, ToolResultPersistence.Create(context.Call.Id,
                    context.Call.Name, context.StepId, materialized, _bodies, context.RunId), outcome == null ? null : ResourceOutcomeLink.From(outcome));
            }
            return new ConversationRuntimeTools(command.FactKind, context?.Call.Id, context?.Call.Name, context?.StepId,
                command.ActivityId, command.ResultProjection, context?.Policy.MayHaveSideEffects == true, terminal,
                execution?.Evidence.Effect == ToolEffectEvidence.None || execution?.Evidence.Effect == ToolEffectEvidence.VerifiedNoChange,
                awaiting ? execution.PendingId : null, context?.IsConfirmed == true,
                awaiting ? PreparationHash(execution.PreparedStateJson) : context?.IsConfirmed == true ? PreparationHash(context.PreparedStateJson) : null);
        }

        private static string PreparationHash(string value)
        { return value == null ? null : TextPatternEngine.Sha256(value); }

        private static IEnumerable<PayloadRef> MessageReferences(ChatMessage message)
        {
            return CallReferences(message).Concat(ResultReferences(message));
        }

        private static ContextCallKey LookupCallKey(Dictionary<string, ContextCallKey> keys, string fingerprint)
        { ContextCallKey value; return keys != null && keys.TryGetValue(fingerprint, out value) ? value : null; }

        private IEnumerable<ConversationCallFact> ChatCalls(ConversationChatCommit fact)
        {
            if (fact.Tools?.Kind != AgentRunEventKind.ResponseAccepted) return new ConversationCallFact[0];
            return fact.Messages.Where(item => item.Header.AcceptedCallOrigin != null && item.Header.RunId == fact.Run.RunId)
                .Select(item => new ConversationCallFact(fact.Card.ChatId, item.Header));
        }

        private void ValidateChatTools(SqliteConnection db, JournalRecord record, ConversationChatCommit fact,
            Dictionary<string, ContextCallKey> keys, ResourceJournalReceipt outcome)
        {
            var tools = fact.Tools;
            if (tools != null && fact.Checkpoint == null) throw new InvalidDataException("Runtime facts require an atomic checkpoint.");
            var offset = 0;
            foreach (var call in ChatCalls(fact))
            {
                if (ChatMessageOperation(db, fact.Card.ChatId, call.Call.Id) != null) continue;
                ValidateCall(db, call, record.Operation, offset++, true);
                var fingerprint = CallFingerprint(call);
                if (LookupCallKey(keys, fingerprint) == null) throw new ContextCallKeyNeeded(call.Call, fingerprint);
            }
            var priorRun = CurrentRun(db, fact.Card.ChatId);
            if (tools?.Terminal != null)
            {
                if (tools.Kind != AgentRunEventKind.ToolCompleted || tools.ResultProjection ||
                    tools.Terminal.ChatId != fact.Card.ChatId || tools.Terminal.Result.RunId != fact.Run.RunId ||
                    tools.Terminal.Result.ToolCallId != tools.CallId || tools.Terminal.Result.ToolName != tools.ToolName ||
                    tools.Terminal.Result.ResultStepId != tools.StepId) throw new InvalidDataException("Foreign kernel terminal.");
                ValidateResult(db, tools.Terminal, record.Operation, true); ValidateOutcome(tools.Terminal, outcome);
                var progress = tools.Terminal.Result.ExecutionProgress;
                var peer = IsPeerDelivery(tools.ToolName) ? DeliveryForCall(db, fact.Card.ChatId, fact.Run.RunId, tools.CallId) : null;
                var spawn = tools.ToolName == AgentCommunicationToolCatalog.Spawn ? SpawnForCall(db, fact.Card.ChatId, fact.Run.RunId, tools.CallId) : null;
                var control = IsPeerControl(tools.ToolName) ? ControlForCall(db, fact.Card.ChatId, fact.Run.RunId, tools.CallId) : null;
                if (control != null && (control.Position.Sequence > fact.ExpectedRevision || tools.NoEffect ||
                    progress.Outcome == ToolExecutionOutcome.NotDispatched || (control.Fact.Action == QueueControlAction.AgentPauseReleased) !=
                        (tools.ToolName == AgentCommunicationToolCatalog.Resume) ||
                    control.Fact.Cancellation != (tools.ToolName == AgentCommunicationToolCatalog.Cancel)))
                    throw new InvalidDataException("Peer control terminal contradicts its exact acceptance.");
                if (spawn != null && (spawn.Fact.Chat == null || spawn.Position.Sequence > fact.ExpectedRevision ||
                    tools.NoEffect || progress.Outcome == ToolExecutionOutcome.NotDispatched))
                    throw new InvalidDataException("Spawn terminal contradicts its exact accepted child.");
                if (peer != null && (peer.Fact.Input == null || peer.Position.Sequence > fact.ExpectedRevision ||
                    tools.NoEffect || progress.Outcome == ToolExecutionOutcome.NotDispatched))
                    throw new InvalidDataException("Peer terminal contradicts its exact accepted delivery.");
                if (tools.MayHaveSideEffects && (tools.Terminal.ResourceOutcome != null ? outcome?.Fact.Call == null : peer == null && spawn == null && control == null &&
                    !tools.NoEffect && progress.Outcome != ToolExecutionOutcome.Unknown && progress.Outcome != ToolExecutionOutcome.NotDispatched))
                    throw new InvalidDataException("Possible resource effects require exact linked proof or an unknown terminal.");
                if (progress.Outcome != ToolExecutionOutcome.NotDispatched && StartedOperation(db, fact.Card.ChatId, fact.Run.RunId, tools.CallId) == null)
                    throw new InvalidDataException("Terminal has no canonical runtime entry.");
                var before = priorRun?.Fact.RunId == fact.Run.RunId ? priorRun.Fact.Counts : new ToolCounts();
                var ok = progress.Outcome == ToolExecutionOutcome.Ok;
                var counted = progress.Outcome != ToolExecutionOutcome.NotDispatched;
                var unknown = progress.Outcome == ToolExecutionOutcome.Unknown && tools.MayHaveSideEffects;
                var expected = new ToolCounts(before.ReadOk + (counted && !tools.MayHaveSideEffects && ok ? 1 : 0),
                    before.ReadError + (counted && !tools.MayHaveSideEffects && !ok ? 1 : 0),
                    before.WriteOk + (counted && tools.MayHaveSideEffects && ok ? 1 : 0),
                    before.WriteError + (counted && tools.MayHaveSideEffects && !ok && !unknown ? 1 : 0),
                    before.WriteUnknown + (unknown ? 1 : 0));
                if (!SameRunValue(expected, fact.Checkpoint.Counts)) throw new InvalidDataException("Kernel counts disagree with their terminal.");
            }
            else if (fact.Checkpoint != null && priorRun?.Fact.RunId == fact.Run.RunId && !SameRunValue(fact.Checkpoint.Counts, priorRun.Fact.Counts))
                throw new InvalidDataException("Only an execution terminal can change tool counts.");
            if (tools?.CallId != null)
            {
                var accepted = FindCall(db, fact.Card.ChatId, fact.Run.RunId, tools.CallId);
                if (accepted == null || accepted.Call.ToolName != tools.ToolName || accepted.Call.AcceptedCallOrigin.StepId != tools.StepId)
                    throw new InvalidDataException("Runtime entry does not identify its accepted call.");
                var pending = PendingTool(db, fact.Card.ChatId, fact.Run.RunId, tools.CallId);
                var start = StartedOperation(db, fact.Card.ChatId, fact.Run.RunId, tools.CallId);
                var terminal = TerminalOperation(db, fact.Card.ChatId, fact.Run.RunId, tools.CallId);
                if (tools.Kind == AgentRunEventKind.ToolStarted)
                {
                    if (terminal != null || start == null && tools.Confirmed)
                        throw new InvalidOperationException("A terminal or unprepared call cannot be confirmed.");
                    if (start != null && (!tools.Confirmed || pending?.Fact.Tools.PendingId == null ||
                        pending.Position.Sequence <= FindChatWrite(db, start).Position.Sequence ||
                        pending.Fact.Checkpoint.InvocationId == fact.Checkpoint.InvocationId ||
                        pending.Fact.Tools.PreparationHash != tools.PreparationHash))
                        throw new InvalidOperationException("Runtime re-entry requires an unconsumed approval and its claimed invocation.");
                }
                if (pending?.Fact.Tools.Kind == AgentRunEventKind.ToolWaiting && tools.Terminal != null &&
                    tools.Terminal.Result.ExecutionProgress.Outcome != ToolExecutionOutcome.NotDispatched &&
                    pending.Fact.Checkpoint.InvocationId == fact.Checkpoint.InvocationId)
                    throw new InvalidDataException("Wait completion requires a claimed continuation.");
                if (tools.Kind == AgentRunEventKind.ToolWaiting && (terminal != null || start == null || pending != null ||
                    !tools.NoEffect || tools.MayHaveSideEffects || fact.Checkpoint.Lifecycle != RunLifecycle.Waiting))
                    throw new InvalidDataException("Waiting must follow its sole accepted runtime entry.");
                if (tools.PendingId != null && (terminal != null || start == null || !tools.NoEffect ||
                    (tools.ResultProjection ? pending?.Fact.Tools.PendingId != tools.PendingId ||
                        pending.Fact.Tools.PreparationHash != tools.PreparationHash : pending != null)))
                    throw new InvalidDataException("Pending approval conflicts with its accepted runtime entry.");
                if (tools.ResultProjection && terminal == null && tools.PendingId == null)
                    throw new InvalidDataException("Result presentation has no committed terminal.");
                var activity = fact.Messages.SingleOrDefault(item => item.Header.Id == tools.ActivityId)?.Header;
                if (activity?.Activity == null || activity.RunId != fact.Run.RunId || activity.Activity.RunId != fact.Run.RunId ||
                    activity.Activity.ToolCallId != tools.CallId || activity.Activity.ToolId != tools.ToolName || activity.Activity.StepId != tools.StepId ||
                    tools.Terminal != null && !SameRunValue(activity.ExecutionProgress, tools.Terminal.Result.ExecutionProgress) ||
                    tools.PendingId != null && (activity.ExecutionProgress?.Outcome != ToolExecutionOutcome.AwaitingConfirmation || activity.EvidenceReference != null))
                    throw new InvalidDataException("Kernel tool event lacks its exact activity projection.");
            }
            foreach (var entry in fact.Messages)
            {
                var message = entry.Header;
                var prior = ChatEntry(db, fact.Card.ChatId, message.Id)?.Header;
                var callId = message.ToolCallId ?? message.Activity?.ToolCallId;
                if (prior != null && (prior.ToolCallId != message.ToolCallId || prior.ToolName != message.ToolName ||
                    prior.ResultStepId != message.ResultStepId || prior.ResponseProtocolVersion != message.ResponseProtocolVersion ||
                    prior.ToolResultProtocolVersion != message.ToolResultProtocolVersion ||
                    !SameRunValue(prior.AcceptedCallOrigin, message.AcceptedCallOrigin) ||
                    !SameRunValue(prior.AcceptedCallPayload, message.AcceptedCallPayload) ||
                    !SameRunValue(prior.ArgumentPayload, message.ArgumentPayload) ||
                    !SameRunValue(prior.ResultPayload, message.ResultPayload) || !SameRunValue(prior.ResultBody, message.ResultBody) ||
                    prior.Activity == null && (!SameRunValue(prior.ResourceEvidence, message.ResourceEvidence) ||
                        !SameRunValue(prior.ExecutionProgress, message.ExecutionProgress))))
                    throw new InvalidDataException("Presentation cannot rewrite retained tool identity or evidence.");
                if (message.AcceptedCallOrigin != null && prior == null && tools?.Kind != AgentRunEventKind.ResponseAccepted)
                    throw new InvalidDataException("Only accepted responses may append calls.");
                if (callId != null && message.AcceptedCallOrigin == null)
                {
                    var accepted = FindCall(db, fact.Card.ChatId, message.RunId, callId);
                    if (accepted == null || message.ToolCallReference?.Alias != accepted.Call.ToolCallReference.Alias)
                        throw new InvalidDataException("Message refers to a foreign accepted call.");
                    var terminal = tools?.Terminal?.Result.ToolCallId == callId ? tools.Terminal :
                        Find(db, TerminalOperation(db, fact.Card.ChatId, message.RunId, callId) as string)?.Fact;
                    var pendingProgress = terminal != null || message.Activity == null || message.ExecutionProgress == null ? null :
                        message.Id == tools?.ActivityId && (tools.PendingId != null || tools.Kind == AgentRunEventKind.ToolWaiting) ? message.ExecutionProgress :
                        PendingTool(db, fact.Card.ChatId, message.RunId, callId)?.Fact.Messages
                            .Single(item => item.Header.Activity?.ToolCallId == callId).Header.ExecutionProgress;
                    if (message.EvidenceReference?.Alias != terminal?.Result.EvidenceReference?.Alias ||
                        message.ExecutionProgress != null && !SameRunValue(message.ExecutionProgress, terminal?.Result.ExecutionProgress ?? pendingProgress))
                        throw new InvalidDataException("Presentation cannot change a committed execution outcome.");
                }
                if (prior?.Activity != null && message.Id != tools?.ActivityId &&
                    (!SameRunValue(prior.Activity, message.Activity) || !SameRunValue(prior.ExecutionProgress, message.ExecutionProgress)))
                    throw new InvalidDataException("Only the exact kernel tool event may change its activity.");
            }
            ValidateChatReferences(db, fact);
        }

        private string StartedOperation(SqliteConnection db, string chat, string run, string call)
        {
            using (var command = Command(db, "SELECT started FROM call_fact WHERE stream=$stream AND chat=$chat AND run=$run AND call=$call"))
            { AddCallKey(command, chat, run, call); return command.ExecuteScalar() as string; }
        }

        private ConversationChatReceipt PendingTool(SqliteConnection db, string chat, string run, string call)
        {
            using (var command = Command(db, "SELECT pending FROM call_fact WHERE stream=$stream AND chat=$chat AND run=$run AND call=$call"))
            {
                AddCallKey(command, chat, run, call);
                var operation = command.ExecuteScalar() as string;
                if (operation == null) return null;
                var pending = FindChatWrite(db, operation);
                if (pending?.Fact.Card.ChatId != chat || pending.Fact.Run.RunId != run ||
                    pending.Fact.Tools?.CallId != call || (pending.Fact.Tools.PendingId == null && pending.Fact.Tools.Kind != AgentRunEventKind.ToolWaiting) || pending.Fact.Tools.ResultProjection)
                    throw new InvalidDataException("Foreign pending approval locator.");
                return pending;
            }
        }

        private void ApplyChatToolRows(SqliteConnection db, SqliteTransaction tx, JournalRecord record, JournalCursor position,
            ConversationChatCommit fact, Dictionary<string, ContextCallKey> keys, ISet<string> existing, long nextEvidence)
        {
            foreach (var call in ChatCalls(fact))
            {
                if (existing.Contains(call.Call.Id)) continue;
                var fingerprint = CallFingerprint(call);
                ApplyCallRows(db, tx, call, record.Operation, fingerprint, position, LookupCallKey(keys, fingerprint));
            }
            if (fact.Tools?.Kind == AgentRunEventKind.ToolStarted)
                using (var command = Command(db, "UPDATE call_fact SET started=$operation,pending=NULL WHERE stream=$stream AND chat=$chat AND run=$run AND call=$call", tx))
                { AddCallKey(command, fact.Card.ChatId, fact.Run.RunId, fact.Tools.CallId); command.Parameters.AddWithValue("$operation", record.Operation); command.ExecuteNonQuery(); }
            if (fact.Tools?.PendingId != null && !fact.Tools.ResultProjection || fact.Tools?.Kind == AgentRunEventKind.ToolWaiting || fact.Tools?.Terminal != null)
                using (var command = Command(db, "UPDATE call_fact SET pending=$pending WHERE stream=$stream AND chat=$chat AND run=$run AND call=$call", tx))
                { AddCallKey(command, fact.Card.ChatId, fact.Run.RunId, fact.Tools.CallId);
                    command.Parameters.AddWithValue("$pending", fact.Tools.Terminal != null ? (object)DBNull.Value : record.Operation); command.ExecuteNonQuery(); }
            if (fact.Tools?.Terminal != null)
                ApplyResultRows(db, tx, fact.Tools.Terminal, record.Operation, Fingerprint(fact.Tools.Terminal), position, nextEvidence);
            foreach (var reference in fact.References ?? new ModelResourceReference[0])
                using (var command = ReferenceCommand(db, "INSERT INTO chat_reference VALUES($stream,$chat,$kind,$scope,$uri,$revision,$ordinal,$version,$operation)", fact.Card.ChatId, reference.Scope, reference.Resource, tx))
                {
                    command.Parameters.AddWithValue("$ordinal", reference.Reference.Ordinal); command.Parameters.AddWithValue("$version", reference.Version);
                    command.Parameters.AddWithValue("$operation", record.Operation); command.ExecuteNonQuery();
                }
            if (fact.ModelEvent != null)
                using (var command = Command(db, "INSERT INTO chat_event VALUES($stream,$chat,$run,$kind,$operation,$sequence)", tx))
                {
                    command.Parameters.AddWithValue("$chat", fact.Card.ChatId); command.Parameters.AddWithValue("$run", fact.ModelEvent.RunId);
                    command.Parameters.AddWithValue("$kind", (int)fact.ModelEvent.Kind); command.Parameters.AddWithValue("$operation", record.Operation);
                    command.Parameters.AddWithValue("$sequence", position.Sequence); command.ExecuteNonQuery();
                }
        }

        private static void ApplyMessageReferences(ConversationCommit command, ConversationChatReceipt receipt)
        {
            foreach (var write in command.Messages)
            {
                var saved = receipt.Fact.Messages.Single(item => item.Header.Id == write.Message.Id).Header;
                write.Message.ToolCallReference = saved.ToolCallReference; write.Message.EvidenceReference = saved.EvidenceReference;
                write.Message.ResourceModelReferences = saved.ResourceModelReferences;
            }
        }
    }

    internal sealed class ConversationRuntimeTools
    {
        public AgentRunEventKind Kind { get; private set; }
        public string CallId { get; private set; }
        public string ToolName { get; private set; }
        public string StepId { get; private set; }
        public string ActivityId { get; private set; }
        public bool ResultProjection { get; private set; }
        public bool MayHaveSideEffects { get; private set; }
        public ConversationResultFact Terminal { get; private set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public bool NoEffect { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string PendingId { get; private set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public bool Confirmed { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string PreparationHash { get; private set; }
        [JsonConstructor] internal ConversationRuntimeTools(AgentRunEventKind kind, string callId, string toolName, string stepId,
            string activityId, bool resultProjection, bool mayHaveSideEffects, ConversationResultFact terminal, bool noEffect = false,
            string pendingId = null, bool confirmed = false, string preparationHash = null)
        {
            var toolEvent = kind == AgentRunEventKind.ToolStarted || kind == AgentRunEventKind.ToolCompleted || kind == AgentRunEventKind.ToolWaiting;
            if (!Enum.IsDefined(typeof(AgentRunEventKind), kind) || toolEvent &&
                (string.IsNullOrWhiteSpace(callId) || string.IsNullOrWhiteSpace(toolName) || string.IsNullOrWhiteSpace(stepId) || string.IsNullOrWhiteSpace(activityId)) ||
                !toolEvent && (callId != null || toolName != null || stepId != null || activityId != null || mayHaveSideEffects) ||
                (resultProjection || terminal != null) && kind != AgentRunEventKind.ToolCompleted || resultProjection && terminal != null ||
                pendingId != null && (string.IsNullOrWhiteSpace(pendingId) || kind != AgentRunEventKind.ToolCompleted || terminal != null) ||
                !toolEvent && (confirmed || preparationHash != null))
                throw new ArgumentException("An exact typed kernel tool event is required.");
            Kind = kind; CallId = callId; ToolName = toolName; StepId = stepId; ActivityId = activityId;
            ResultProjection = resultProjection; MayHaveSideEffects = mayHaveSideEffects; Terminal = terminal;
            NoEffect = noEffect;
            PendingId = pendingId; Confirmed = confirmed; PreparationHash = preparationHash;
        }
    }
}
