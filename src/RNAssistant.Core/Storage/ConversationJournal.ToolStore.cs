using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
        private ConversationRuntimeTools PrepareChatTools(ChatSession session, ConversationRunCommit command,
            out IReadOnlyList<ResourceJournalReceipt> outcomes)
        {
            outcomes = new ResourceJournalReceipt[0];
            if (command == null) return null;
            var context = command.ToolContext; var execution = command.Execution;
            if (context != null && (context.RunId != command.RunId || context.TurnId != session.LastRun.TurnId))
                throw new InvalidOperationException("Tool context belongs to another run.");
            if (command.FactKind == AgentRunEventKind.ToolStarted &&
                !SameRunValue(context, session.LastRun.KernelState.InFlightTool))
                throw new InvalidOperationException("Dispatch requires its exact in-flight checkpoint.");
            // Exposure and dispatch belong to the exact runtime descriptor. Storage
            // accepts both typed mutation classes; the terminal below must still
            // carry its linked owner proof, a verified no-effect or honest Unknown.
            if (context?.Policy.MayHaveSideEffects == true && (context.Policy.Policy == null ||
                context.Policy.Policy.ExecutionClass == ToolExecutionClass.ReadOnly))
                throw new InvalidOperationException("Effectful calls require their captured typed execution policy.");
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
                {
                    var origin = MutationOrigin(session, context);
                    var resources = new ResourceJournal(_paths, _protector);
                    outcomes = context.Call.Name == ResourceMutationToolCatalog.Patch
                        ? resources.CallOutcomes(origin).Where(item => item.Fact.Authority != null ||
                            item.Fact.Publication != null && item.Fact.Attempt?.State == MutationAttemptState.Resolved).ToArray()
                        : new[] { resources.CallOutcome(origin) }.Where(item => item != null).ToArray();
                }
                var effect = execution.ResourceEffect;
                if (!MatchesResourceOutcomes(context.Call.Name, effect, outcomes) && outcomes.Count == 1 && IsUnconfirmedPatch(context, execution))
                {
                    // An owner can lose its publication acknowledgement after a
                    // write. Keep the handler's Unknown report, but retain only
                    // canonical effect proof (if it exists), never its synthetic
                    // diagnostic impact as a successful owner receipt.
                    effect = outcomes[0].Effect;
                }
                if (execution.AuthorityCommitId != null && !outcomes.Any(item => item.CommitId == execution.AuthorityCommitId) ||
                    effect != null && !MatchesResourceOutcomes(context.Call.Name, effect, outcomes))
                    throw new InvalidDataException("Execution refers to a foreign or unavailable resource outcome.");
                var authorityCommitId = execution.AuthorityCommitId ?? (outcomes.Count == 1 ? outcomes[0].CommitId : null);
                var materialized = new ToolResultMaterialization(ToolResultPersistence.Terminal(execution),
                    resourceEvidence: execution.ResourceEvidence, resourceEffect: effect ?? (outcomes.Count == 1 ? outcomes[0].Effect : null),
                    authorityCommitId: authorityCommitId) { ExecutionProgress = ToolExecutionProgress.Capture(execution) };
                terminal = new ConversationResultFact(session.Id, ToolResultPersistence.Create(context.Call.Id,
                    context.Call.Name, context.StepId, materialized, _bodies, context.RunId), resourceOutcomes: outcomes.Select(ResourceOutcomeLink.From).ToArray());
            }
            return new ConversationRuntimeTools(command.FactKind, context?.Call.Id, context?.Call.Name, context?.StepId,
                command.ActivityId, command.ResultProjection, context?.Policy.MayHaveSideEffects == true, terminal,
                execution?.Evidence.Effect == ToolEffectEvidence.None || execution?.Evidence.Effect == ToolEffectEvidence.VerifiedNoChange,
                awaiting ? execution.PendingId : null, context?.IsConfirmed == true,
                awaiting ? PreparationHash(execution.PreparedStateJson) : context?.IsConfirmed == true ? PreparationHash(context.PreparedStateJson) : null);
        }

        private static string PreparationHash(string value)
        { return value == null ? null : TextPatternEngine.Sha256(value); }

        private static bool MatchesResourceOutcomes(string tool, ResourceEffect effect,
            IReadOnlyList<ResourceJournalReceipt> receipts)
        {
            if (receipts.Count == 1) return MatchesResourceOutcome(tool, effect, receipts[0]);
            if (receipts.Count == 0)
            {
                if (tool != ResourceMutationToolCatalog.Patch || effect?.Operation != tool || effect.Targets == null) return false;
                if (effect.Outcome == ResourceEffectOutcome.UnknownAfterDispatch)
                    return effect.Targets.All(item => item.Status == ResourceTargetStatus.Unknown && item.WriteStarted && item.After == null);
                return effect.Outcome == ResourceEffectOutcome.FailedNoEffect &&
                    effect.Targets.All(item => !item.WriteStarted && (item.Status == ResourceTargetStatus.Rejected || item.Status == ResourceTargetStatus.NotStarted));
            }
            if (tool != ResourceMutationToolCatalog.Patch || effect?.Operation != tool || effect.Targets == null ||
                effect.Targets.Count < 2 || receipts.Count > effect.Targets.Count || receipts.Any(item => item?.Fact.Call?.Origin.TargetOrdinal == null))
                return false;
            var expectedOutcome = effect.Targets.Any(item => item.Status == ResourceTargetStatus.Unknown) ? ResourceEffectOutcome.UnknownAfterDispatch :
                effect.Targets.All(item => item.Status == ResourceTargetStatus.Changed || item.Status == ResourceTargetStatus.Unchanged) ?
                    effect.Targets.Any(item => item.Status == ResourceTargetStatus.Changed) ? ResourceEffectOutcome.VerifiedChanged : ResourceEffectOutcome.VerifiedNoChange :
                effect.Targets.Any(item => item.Status == ResourceTargetStatus.Changed) ? ResourceEffectOutcome.VerifiedChanged : ResourceEffectOutcome.FailedNoEffect;
            if (effect.Outcome != expectedOutcome) return false;
            var byOrdinal = new Dictionary<int, ResourceJournalReceipt>();
            foreach (var receipt in receipts)
            {
                var ordinal = receipt.Fact.Call.Origin.TargetOrdinal.Value;
                if (ordinal < 0 || ordinal >= effect.Targets.Count || byOrdinal.ContainsKey(ordinal)) return false;
                if (!MatchesTargetOutcome(effect.Targets[ordinal], receipt)) return false;
                if (receipt.Effect?.Impacts != null && receipt.Effect.Impacts.Any(impact => !effect.Impacts.Any(item => SameRunValue(item, impact)))) return false;
                byOrdinal.Add(ordinal, receipt);
            }
            for (var index = 0; index < effect.Targets.Count; index++)
            {
                var target = effect.Targets[index];
                var requiresReceipt = target.Status == ResourceTargetStatus.Changed || target.Status == ResourceTargetStatus.Unchanged;
                if (requiresReceipt && !byOrdinal.ContainsKey(index)) return false;
                if (target.Status == ResourceTargetStatus.Unknown && !byOrdinal.ContainsKey(index) && !target.WriteStarted) return false;
            }
            return true;
        }

        // Patch aggregates retain one exact owner receipt per dispatched target.
        private static bool MatchesResourceOutcome(string tool, ResourceEffect effect, ResourceJournalReceipt receipt)
        {
            var owner = receipt?.Effect;
            if (SameRunValue(effect, owner)) return true;
            if (tool != ResourceMutationToolCatalog.Patch || effect?.Operation != tool || effect.Targets?.Count != 1 ||
                effect.Verification != null) return false;
            var target = effect.Targets[0];
            if (target?.Source?.IsExact != true || string.IsNullOrWhiteSpace(target.Target) ||
                !SameRunValue(target.Effect, owner) || !SameRunValue(effect.Impacts, owner?.Impacts ?? new ResourceImpact[0])) return false;
            if (!MatchesTargetOutcome(target, receipt)) return false;
            return target.Status == ResourceTargetStatus.Changed && effect.Outcome == ResourceEffectOutcome.VerifiedChanged ||
                target.Status == ResourceTargetStatus.Unchanged && effect.Outcome == ResourceEffectOutcome.VerifiedNoChange ||
                target.Status == ResourceTargetStatus.Unknown && effect.Outcome == ResourceEffectOutcome.UnknownAfterDispatch ||
                (target.Status == ResourceTargetStatus.Rejected || target.Status == ResourceTargetStatus.NotStarted) &&
                    effect.Outcome == ResourceEffectOutcome.FailedNoEffect;
        }

        private static bool MatchesTargetOutcome(ResourceTargetEffect target, ResourceJournalReceipt receipt)
        {
            if (target?.Source?.IsExact != true || string.IsNullOrWhiteSpace(target.Target) ||
                !SameRunValue(target.Effect, receipt?.Effect)) return false;
            var owner = receipt.Effect;
            switch (target.Status)
            {
                case ResourceTargetStatus.Changed:
                    return target.WriteStarted &&
                        (owner.Outcome == ResourceEffectOutcome.VerifiedChanged || owner.Outcome == ResourceEffectOutcome.Restored) &&
                        PatchAfterIsPublished(target, receipt);
                case ResourceTargetStatus.Unchanged:
                    return (owner.Outcome == ResourceEffectOutcome.VerifiedNoChange) && SameRunValue(target.After, target.Source);
                case ResourceTargetStatus.Unknown:
                    return target.WriteStarted && target.After == null &&
                        (owner.Outcome == ResourceEffectOutcome.UnknownAfterDispatch || owner.Outcome == ResourceEffectOutcome.VerifiedChanged ||
                         owner.Outcome == ResourceEffectOutcome.Restored);
                case ResourceTargetStatus.Rejected:
                    if (!target.WriteStarted) return target.After == null && owner.Outcome == ResourceEffectOutcome.FailedNoEffect;
                    return owner.Outcome == ResourceEffectOutcome.VerifiedNoChange && SameRunValue(target.After, target.Source) ||
                        owner.Outcome == ResourceEffectOutcome.FailedNoEffect && target.After == null;
                case ResourceTargetStatus.NotStarted:
                    return target.After == null && owner.Outcome == ResourceEffectOutcome.FailedNoEffect;
                default: return false;
            }
        }

        private static bool PatchAfterIsPublished(ResourceTargetEffect target, ResourceJournalReceipt receipt)
        {
            if (target.After?.IsExact != true) return false;
            var owner = receipt.Effect;
            if ((target.After.Identity.Equals(target.Source.Identity) || target.After.Identity.Equals(receipt.Fact.Attempt.Target)) &&
                owner.Impacts.Any(impact => SameRunValue(impact.After, target.After))) return true;
            var source = ResourceUri.Parse(target.Source.Uri);
            return source.Provider == "catalog" && target.After.Uri == target.Source.Uri && owner.Impacts.Any(impact =>
                impact.Identity.Uri == ResourceUri.Create("catalog", source.Segments[0]) && impact.After?.Revision == target.After.Revision);
        }

        private static bool IsUnconfirmedPatch(ToolExecutionContext context, ToolExecutionRecord execution)
        {
            var effect = execution.ResourceEffect;
            if (context.Call.Name != ResourceMutationToolCatalog.Patch || execution.Outcome != ToolExecutionOutcome.Unknown ||
                execution.AuthorityCommitId != null || !execution.MayHaveDispatched || effect?.Operation != context.Call.Name ||
                effect.Outcome != ResourceEffectOutcome.UnknownAfterDispatch || effect.Targets?.Count != 1) return false;
            var target = effect.Targets[0];
            if (target?.Status != ResourceTargetStatus.Unknown || !target.WriteStarted || target.After != null) return false;
            var accepted = ResourcePatch.Parse(context.Call.ArgumentsJson);
            return accepted.Count == 1 && accepted[0].Target == target.Target;
        }

        private ToolCall AcceptedRuntimeCall(SqliteConnection db, ConversationChatCommit fact, Dictionary<string, ContextCallKey> keys)
        {
            var accepted = FindCall(db, fact.Card.ChatId, fact.Run.RunId, fact.Tools.CallId);
            if (accepted == null) throw new InvalidDataException("Runtime terminal has no accepted call.");
            var fingerprint = CallFingerprint(new ConversationCallFact(fact.Card.ChatId, accepted.Call));
            var key = LookupCallKey(keys, fingerprint);
            if (key == null) throw new ContextCallKeyNeeded(accepted.Call, fingerprint);
            return key.Call;
        }

        private sealed class RenameArguments
        {
            [JsonProperty("title")] public string Title { get; set; }
            [JsonProperty("agent_id")] public string AgentId { get; set; }
        }

        private bool ValidateRename(SqliteConnection db, ConversationChatCommit fact, Dictionary<string, ContextCallKey> keys)
        {
            if (fact.Tools.ToolName != ChatRenameToolHandler.ToolId ||
                fact.Tools.Terminal.Result.ExecutionProgress.Outcome != ToolExecutionOutcome.Ok) return false;
            var call = AcceptedRuntimeCall(db, fact, keys);
            var args = JsonConvert.DeserializeObject<RenameArguments>(call.ArgumentsJson);
            var title = args?.Title?.Trim();
            ToolResultWireReadResult wire; string wireError; ChatRenameResult result;
            try { result = ToolResultHistoryReader.TryRead(fact.Tools.Terminal.Result, out wire, out wireError)
                    ? JsonConvert.DeserializeObject<ChatRenameResult>(wire.Result.DataJson) : null; }
            catch (JsonException) { result = null; }
            if (call.Name != ChatRenameToolHandler.ToolId || string.IsNullOrWhiteSpace(title) || title.Length > 200 ||
                title.Any(char.IsControl) || result == null || result.Title != title)
                throw new InvalidDataException("Rename requires its exact accepted target and result title.");
            if (!string.IsNullOrWhiteSpace(args.AgentId) && args.AgentId != AgentChatAddress.IdFor(fact.Card.ChatId))
            {
                var expectedOperation = ChatRenameToolHandler.AddressedOperationId(fact.Card.ChatId,
                    fact.Run.RunId, fact.Tools.CallId, args.AgentId, title);
                var target = FindChatWrite(db, expectedOperation);
                if (result.AgentId != args.AgentId || result.TargetOperationId != expectedOperation ||
                    !result.TargetRevision.HasValue ||
                    target == null || target.Fact.Card.ChatId == fact.Card.ChatId ||
                    AgentChatAddress.IdFor(target.Fact.Card.ChatId) != args.AgentId ||
                    target.Fact.Card.Title != title || !target.Fact.Card.TitleAssignedByAgent ||
                    target.Position.Sequence != result.TargetRevision.Value ||
                    fact.Tools.Terminal.ResourceOutcome != null || fact.Tools.Terminal.Result.ResourceEffect != null)
                    throw new InvalidDataException("Addressed rename requires its exact target metadata receipt.");
                return true;
            }
            var prior = TextChat(db, fact.Card.ChatId)?.Fact.Card;
            var changed = prior != null && (prior.Title != title || !prior.TitleAssignedByAgent);
            if (args.AgentId != null && args.AgentId != AgentChatAddress.IdFor(fact.Card.ChatId) || prior == null ||
                fact.Card.Title != title || !fact.Card.TitleAssignedByAgent || fact.Tools.NoEffect == changed ||
                fact.Tools.Terminal.ResourceOutcome != null || fact.Tools.Terminal.Result.ResourceEffect != null)
                throw new InvalidDataException("Rename requires its exact accepted self target and atomic metadata change.");
            return true;
        }

        private static IEnumerable<PayloadRef> MessageReferences(ChatMessage message)
        {
            return CallReferences(message).Concat(ResultReferences(message)).Concat(AttachmentReferences(message?.Attachments));
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
            Dictionary<string, ContextCallKey> keys, Dictionary<string, ResourceJournalReceipt> outcomes)
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
                ValidateResult(db, tools.Terminal, record.Operation, true);
                var terminalOutcomes = tools.Terminal.ResourceOutcomes.Select(link => OutcomeFor(link, outcomes)).ToArray();
                ValidateOutcome(tools.Terminal, terminalOutcomes);
                var progress = tools.Terminal.Result.ExecutionProgress;
                var rename = ValidateRename(db, fact, keys);
                if (tools.Terminal.Result.ResourceEffect?.Targets != null)
                {
                    var aggregate = tools.Terminal.Result.ResourceEffect;
                    var partialVerified = tools.ToolName == ResourceMutationToolCatalog.Patch &&
                        aggregate.Targets.Any(item => item.Status == ResourceTargetStatus.Changed) &&
                        aggregate.Targets.Any(item => item.Status == ResourceTargetStatus.Rejected || item.Status == ResourceTargetStatus.NotStarted) &&
                        aggregate.Targets.All(item => item.Status != ResourceTargetStatus.Unknown);
                    if (!MatchesResourceOutcomes(tools.ToolName, aggregate, terminalOutcomes) ||
                        aggregate.Outcome == ResourceEffectOutcome.UnknownAfterDispatch && progress.Outcome != ToolExecutionOutcome.Unknown ||
                        aggregate.Outcome == ResourceEffectOutcome.VerifiedChanged && progress.Outcome != ToolExecutionOutcome.Ok && !partialVerified ||
                        aggregate.Outcome == ResourceEffectOutcome.VerifiedNoChange && progress.Outcome != ToolExecutionOutcome.Ok ||
                        aggregate.Outcome == ResourceEffectOutcome.FailedNoEffect && progress.Outcome != ToolExecutionOutcome.Error &&
                            progress.Outcome != ToolExecutionOutcome.NotDispatched)
                        throw new InvalidDataException("Patch aggregate differs from its accepted owner targets.");
                }
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
                if (tools.MayHaveSideEffects && (terminalOutcomes.Length > 0 ? terminalOutcomes.Any(item => item?.Fact.Call == null) : peer == null && spawn == null && control == null && !rename &&
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
                using (var command = Command(db, "INSERT INTO chat_event VALUES($stream,$chat,$run,$kind,$operation,$sequence,$step,CASE WHEN $kind=$request THEN (SELECT COALESCE(MAX(request_ordinal),0)+1 FROM chat_event WHERE stream=$stream AND chat=$chat) END)", tx))
                {
                    command.Parameters.AddWithValue("$chat", fact.Card.ChatId); command.Parameters.AddWithValue("$run", fact.ModelEvent.RunId);
                    var step = fact.ModelEvent.LogicalStepId;
                    if (step == null && fact.ModelEvent.Kind == SessionEventKind.ModelRequestPrepared)
                        step = (string)JObject.Parse(ReadChatBody(fact.ModelEvent.Body))["StepId"];
                    command.Parameters.AddWithValue("$step", (object)step ?? DBNull.Value);
                    command.Parameters.AddWithValue("$request", (int)SessionEventKind.ModelRequestPrepared);
                    command.Parameters.AddWithValue("$kind", (int)fact.ModelEvent.Kind); command.Parameters.AddWithValue("$operation", record.Operation);
                    command.Parameters.AddWithValue("$sequence", position.Sequence); command.ExecuteNonQuery();
                }
        }

        private static void ApplyMessageReferences(ConversationCommit command, ConversationChatReceipt receipt)
        {
            foreach (var entry in receipt.Fact.Messages)
            {
                var saved = entry.Header;
                var write = command.Messages.Single(item => item.Message.Id == saved.Id);
                write.Message.ToolCallReference = saved.ToolCallReference; write.Message.EvidenceReference = saved.EvidenceReference;
                write.Message.ResourceModelReferences = saved.ResourceModelReferences;
                write.Message.ResourceEffect = saved.ResourceEffect; write.Message.AuthorityCommitId = saved.AuthorityCommitId;
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
