using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;
using ToolResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Runtime
{
    // One call, one captured registration, one handler invocation. The kernel
    // owns response batching, accounting, pending lifecycle and the run store.
    public sealed class ToolRuntime : IToolRuntime
    {
        private readonly ToolHandlerRegistry _registry;
        private readonly string _mode;
        private readonly bool _autoConfirm;
        private readonly bool _allowsConfirmation;
        private readonly Func<ToolExecutionContext, ToolPreparationResult, string> _pendingRegistrar;
        private readonly IToolMutationObserver _mutationObserver;

        public ToolRuntime(ToolHandlerRegistry registry, string mode, bool autoConfirm, bool allowsConfirmation,
            Func<ToolExecutionContext, ToolPreparationResult, string> pendingRegistrar = null,
            IToolMutationObserver mutationObserver = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            if (mode != "agent" && mode != "plan" && mode != "chat") throw new ArgumentException("A supported conversation mode is required.", nameof(mode));
            _mode = mode;
            _autoConfirm = autoConfirm;
            _allowsConfirmation = allowsConfirmation;
            _pendingRegistrar = pendingRegistrar;
            _mutationObserver = mutationObserver;
        }

        public ToolPolicySnapshot Describe(ToolCall call)
        {
            var tool = call == null ? null : _registry.Lookup(call.Name);
            return tool == null ? null : tool.Policy();
        }

        public async Task<ToolExecutionRecord> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (cancellationToken.IsCancellationRequested) return NotDispatched(context, "Cancelled before handler dispatch.");
            var tool = _registry.Lookup(context.Call.Name);
            if (tool == null) return Reject(context, "unknown_tool", "No handler is registered for this exact tool id.");
            if (!context.Policy.Matches(tool.Policy())) return Reject(context, "tool_policy_changed", "The captured tool policy or revision changed.");
            var policy = tool.Registration.Policy;
            if (!policy.AllowedModes.Contains(_mode, StringComparer.Ordinal))
                return Reject(context, "tool_mode_denied", "The tool is not allowed in this conversation mode.");
            if (policy.RequiresConfirmation && !_allowsConfirmation)
                return Reject(context, "confirmation_not_allowed", "This conversation mode cannot authorize confirmation-required tools.");

            Dictionary<string, object> arguments;
            try
            {
                var value = ParseArguments(context.Call.ArgumentsJson);
                var schema = tool.Schema();
                ToolSchemaSupport.RemoveOptionalNulls(value, schema);
                string error;
                if (!ToolSchemaSupport.ValidateArguments(value, schema, true, out error))
                    return Reject(context, "invalid_arguments", error);
                arguments = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                ToolArgumentNormalizer.AddProperties(value, arguments);
            }
            catch (Exception ex) when (ex is JsonException || ex is FormatException || ex is OverflowException || ex is InvalidCastException)
            {
                return Reject(context, "invalid_arguments", ex.Message);
            }

            if (cancellationToken.IsCancellationRequested) return NotDispatched(context, "Cancelled before handler dispatch.");
            var preparable = tool.Handler as IPreparableToolHandler;
            if (preparable == null && context.PreparedStateJson != null)
                return Reject(context, "tool_preparation_state_unexpected", "This handler does not accept prepared state.");
            if (preparable != null && context.IsConfirmed && string.IsNullOrWhiteSpace(context.PreparedStateJson))
                return Reject(context, "tool_preparation_missing", "Confirmed execution is missing its prepared state.");
            if (!context.IsConfirmed && context.PreparedStateJson != null)
                return Reject(context, "tool_preparation_state_unexpected", "Unconfirmed execution cannot supply prepared state.");

            ToolPreparationResult preparation = null;
            if (preparable != null && !context.IsConfirmed)
            {
                var preparationContext = new ToolHandlerContext(context, arguments);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    preparation = await preparable.PrepareAsync(preparationContext, cancellationToken).ConfigureAwait(false);
                    if (preparation == null) throw new InvalidOperationException("Handler returned no preparation result.");
                    if (preparationContext.MayHaveDispatched)
                        return Record(context, ToolExecutionOutcome.Unknown,
                            ToolResult.Unknown("Preparation crossed an effect boundary.", Code("preparation_dispatched")),
                            true, ToolEffectEvidence.Unknown);
                    if (preparation.Result.Status != ToolResultStatus.Ok)
                    {
                        var failed = preparation.Result.Status == ToolResultStatus.Unknown
                            ? ToolResult.Error(preparation.Result.Message, preparation.Result.DataJson, preparation.Result.Resources)
                            : preparation.Result;
                        return Record(context, ToolExecutionOutcome.Error, failed, false, ToolEffectEvidence.None);
                    }
                }
                catch (OperationCanceledException)
                {
                    if (preparationContext.MayHaveDispatched)
                        return Record(context, ToolExecutionOutcome.Unknown,
                            ToolResult.Unknown(
                                "Preparation was cancelled after an effect boundary.",
                                Code("preparation_effect_unknown")),
                            true, ToolEffectEvidence.Unknown);
                    return NotDispatched(context, "Preparation cancelled before handler dispatch.");
                }
                catch (Exception ex)
                {
                    if (preparationContext.MayHaveDispatched)
                        return Record(context, ToolExecutionOutcome.Unknown,
                            ToolResult.Unknown(ex.Message,
                                Code("preparation_effect_unknown")),
                            true, ToolEffectEvidence.Unknown);
                    return Reject(context, "tool_preparation_failed", ex.Message);
                }
            }
            if (policy.RequiresConfirmation && !context.IsConfirmed && !_autoConfirm)
            {
                if (_pendingRegistrar == null) return Reject(context, "confirmation_unavailable", "No confirmation registrar is available.");
                try
                {
                    var pendingId = _pendingRegistrar(context, preparation);
                    if (cancellationToken.IsCancellationRequested) return NotDispatched(context, "Cancelled during confirmation registration.");
                    if (string.IsNullOrWhiteSpace(pendingId)) return Reject(context, "confirmation_unavailable", "Confirmation registration returned no identity.");
                    return Record(context, ToolExecutionOutcome.AwaitingConfirmation, null, false, ToolEffectEvidence.None,
                        pendingId: pendingId,
                        message: preparation == null ? "Confirmation required before handler dispatch." : preparation.Result.Message,
                        preparedStateJson: preparation == null ? null : preparation.PreparedStateJson,
                        confirmationDataJson: preparation == null ? null : preparation.Result.DataJson);
                }
                catch (OperationCanceledException)
                {
                    return NotDispatched(context, "Confirmation registration cancelled before handler dispatch.");
                }
                catch (Exception ex)
                {
                    return Reject(context, "confirmation_registration_failed", ex.Message);
                }
            }

            string mutationAttemptId = null;
            if (policy.MayHaveSideEffects && _mutationObserver != null)
                mutationAttemptId = _mutationObserver.Prepare(context, arguments);
            ToolExecutionRecord publishedTerminal = null;
            var publicationAttempted = false;
            ToolHandlerContext handlerContext = null;
            handlerContext = new ToolHandlerContext(context, arguments,
                context.PreparedStateJson ?? (preparation == null ? null : preparation.PreparedStateJson),
                mutationAttemptId == null ? (Action)null : () =>
                    _mutationObserver.MarkDispatchMayHaveOccurred(mutationAttemptId), completed =>
                    {
                        if (publicationAttempted) throw new InvalidOperationException("Mutation terminal was published twice.");
                        publicationAttempted = true;
                        publishedTerminal = CompleteMutation(mutationAttemptId, FromHandler(context, policy, handlerContext, completed));
                    });
            ToolExecutionRecord terminal;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await tool.Handler.ExecuteAsync(handlerContext, cancellationToken).ConfigureAwait(false);
                if (result == null) throw new InvalidOperationException("Handler returned no terminal result.");
                // Cancellation cannot discard a terminal result/effect that the
                // handler already established. The kernel observes lifecycle next.
                terminal = FromHandler(context, policy, handlerContext, result);
            }
            catch (Exception ex)
            {
                if (publicationAttempted)
                {
                    if (publishedTerminal != null) return publishedTerminal;
                    if (mutationAttemptId != null) _mutationObserver.ReleaseUnresolved(mutationAttemptId);
                    throw;
                }
                if (ex is OperationCanceledException && !handlerContext.MayHaveDispatched)
                    terminal = NotDispatched(context, "Cancelled before a possible effect.");
                else
                {
                    var unknown = policy.MayHaveSideEffects && handlerContext.MayHaveDispatched;
                    var result = unknown ? ToolResult.Unknown(ex.Message, Code("handler_effect_unknown"))
                        : ToolResult.Error(ex.Message, Code("handler_failed"));
                    terminal = Record(context, unknown ? ToolExecutionOutcome.Unknown : ToolExecutionOutcome.Error,
                        result, handlerContext.MayHaveDispatched, unknown ? ToolEffectEvidence.Unknown : ToolEffectEvidence.None);
                }
            }
            return publishedTerminal ?? CompleteMutation(mutationAttemptId, terminal);
        }

        private ToolExecutionRecord CompleteMutation(string attemptId, ToolExecutionRecord record)
        {
            if (string.IsNullOrWhiteSpace(attemptId) || _mutationObserver == null) return record;
            if (record != null && record.MayHaveDispatched)
                return record.WithAuthorityCommit(_mutationObserver.Complete(attemptId, record));
            else
                _mutationObserver.AbandonBeforeDispatch(attemptId);
            return record;
        }

        private static ToolExecutionRecord FromHandler(ToolExecutionContext context, ToolPolicy policy,
            ToolHandlerContext handlerContext, ToolHandlerResult completed)
        {
            var result = completed.Result;
            var effect = completed.Effect;
            var dispatched = handlerContext.MayHaveDispatched;
            if (!dispatched && (effect == ToolEffectEvidence.VerifiedChange || effect == ToolEffectEvidence.Unknown))
            {
                // Contradictory evidence must never certify non-dispatch.
                return Record(context, policy.MayHaveSideEffects ? ToolExecutionOutcome.Unknown : ToolExecutionOutcome.Error,
                    policy.MayHaveSideEffects ? ToolResult.Unknown("Handler effect evidence lacked a dispatch boundary.", Code("invalid_effect_evidence"), result.Resources)
                        : ToolResult.Error("Read handler reported an effect.", Code("invalid_effect_evidence"), result.Resources),
                    true, policy.MayHaveSideEffects ? ToolEffectEvidence.Unknown : ToolEffectEvidence.Unreported);
            }
            if (!policy.MayHaveSideEffects)
            {
                if (result.Status == ToolResultStatus.Unknown || effect == ToolEffectEvidence.Unknown || effect == ToolEffectEvidence.VerifiedChange)
                    result = ToolResult.Error(result.Message, result.DataJson, result.Resources);
                if (effect == ToolEffectEvidence.Unknown || effect == ToolEffectEvidence.VerifiedChange) effect = ToolEffectEvidence.Unreported;
            }
            else if (result.Status == ToolResultStatus.Unknown || effect == ToolEffectEvidence.Unknown ||
                dispatched && (effect == ToolEffectEvidence.Unreported ||
                    result.Status == ToolResultStatus.Ok && effect != ToolEffectEvidence.VerifiedNoChange && effect != ToolEffectEvidence.VerifiedChange))
            {
                result = ToolResult.Unknown(result.Message, result.DataJson, result.Resources);
                effect = ToolEffectEvidence.Unknown;
                dispatched = true;
            }
            if (result.Status == ToolResultStatus.Ok && policy.Verification == ToolVerification.Tool &&
                effect != ToolEffectEvidence.VerifiedNoChange && effect != ToolEffectEvidence.VerifiedChange)
            {
                result = ToolResult.Error("Handler returned no required verification evidence.", Code("verification_missing"), result.Resources);
            }
            var outcome = result.Status == ToolResultStatus.Ok ? ToolExecutionOutcome.Ok
                : result.Status == ToolResultStatus.Unknown ? ToolExecutionOutcome.Unknown : ToolExecutionOutcome.Error;
            return Record(context, outcome, result, dispatched, effect,
                awaitingUser: completed.AwaitingUser && outcome == ToolExecutionOutcome.Ok,
                resourceEvidence: completed.ResourceEvidence, resourceReadBack: completed.ResourceReadBack);
        }

        private static JObject ParseArguments(string json)
        {
            using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None, MaxDepth = 64 })
            {
                var value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) throw new JsonReaderException("More than one argument JSON value.");
                if (value.DescendantsAndSelf().OfType<JObject>().Any(obj => obj.Properties()
                    .GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)))
                    throw new JsonReaderException("Argument names cannot differ only by case.");
                return value;
            }
        }

        private static string Code(string code) { return new JObject { ["code"] = code }.ToString(Formatting.None); }

        private static ToolExecutionRecord Reject(ToolExecutionContext context, string code, string message)
        {
            return Record(context, ToolExecutionOutcome.Error, ToolResult.Error(message, Code(code)), false, ToolEffectEvidence.None);
        }

        private static ToolExecutionRecord NotDispatched(ToolExecutionContext context, string message)
        {
            return Record(context, ToolExecutionOutcome.NotDispatched, null, false, ToolEffectEvidence.None, message: message);
        }

        private static ToolExecutionRecord Record(ToolExecutionContext context, ToolExecutionOutcome outcome,
            ToolResult result, bool dispatched, ToolEffectEvidence effect, string pendingId = null, bool awaitingUser = false,
            string message = null, string preparedStateJson = null, string confirmationDataJson = null,
            IReadOnlyList<RNAssistant.Core.Models.ResourceEvidence> resourceEvidence = null,
            IReadOnlyList<RNAssistant.Core.Models.ResourceMutationReadBack> resourceReadBack = null)
        {
            var completed = DateTime.UtcNow;
            if (completed < context.StartedUtc) completed = context.StartedUtc;
            return new ToolExecutionRecord(context, outcome, completed, message ?? (result == null ? string.Empty : result.Message),
                mayHaveDispatched: dispatched, pendingId: pendingId, awaitingUser: awaitingUser,
                evidence: new ToolExecutionEvidence(dispatched ? ToolDispatchEvidence.MayHaveDispatched : ToolDispatchEvidence.NotDispatched, effect),
                result: result, preparedStateJson: preparedStateJson, confirmationDataJson: confirmationDataJson,
                resourceEvidence: resourceEvidence, resourceReadBack: resourceReadBack);
        }
    }
}
