using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Runtime;

namespace RNAssistant.Office.Services
{
    internal sealed partial class ConversationKernelAdapter
    {
        public ToolPolicySnapshot Describe(ToolCall call)
        {
            if (_nativeTools.Handles(call.Name)) return _nativeTools.Describe(call);
            return _toolPack.Describe(call.Name);
        }

        public async Task<ToolExecutionRecord> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
        {
            if (_preparationFailure != null)
                return new ToolExecutionRecord(context, ToolExecutionOutcome.NotDispatched,
                    DateTime.UtcNow, "Model input preparation failed; remaining calls were not dispatched.", mayHaveDispatched: false);
            cancellationToken.ThrowIfCancellationRequested();
            if (_nativeTools.Handles(context.Call.Name))
            {
                var record = await _nativeTools.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
                var nativeMaterialization = _nativeTools.TakeMaterialization(record);
                if (nativeMaterialization != null) _results[context.Call.Id] = nativeMaterialization;
                return record;
            }
            var pinned = _toolPack.Find(context.Call.Name);
            var currentRevision = ToolPackSnapshotFactory.ExecutionFingerprint(
                _catalog, context.Call.Name, _policy.Mode);
            if (pinned == null || !string.Equals(pinned.Revision, currentRevision, StringComparison.Ordinal))
                return RegistrationChanged(context);
            var command = Command(context.Call, context.StepId, context.IsConfirmed);
            var result = _executor.Execute(command, _catalog, _input.Settings, false, context.IsConfirmed,
                _session, context.RemainingToolSteps, _skills, cancellationToken);
            if (result == null) throw new InvalidOperationException("Tool returned no terminal execution evidence.");
            if (!_policy.AllowsConfirmation && AgentTranscript.IsWaitingResult(result))
                result = ToolResult.Fail("This conversation mode cannot execute a tool that requires confirmation.",
                    null, "conversation_policy_denied", false);
            if (AgentTranscript.IsWaitingResult(result))
            {
                // Executor validation may insert defaults/remove optional nulls.
                // Persist the exact accepted arguments; confirmation validates them
                // again under the same fingerprint. Keep the live runtime guard.
                command.Arguments = ReadArguments(context.Call);
                result.ConfirmationCatalogSha256 = context.Policy.Revision;
                result.PendingId = _registrar == null ? Guid.NewGuid().ToString("N") : _registrar(_session, command, result);
            }
            _uiResults[context.Call.Id] = result;
            var outcome = LegacyToolOutcomeAdapter.Map(context.Policy, result);
            var awaitingUser = AgentTranscript.IsAwaitingUserResult(result);
            ToolResultMaterialization materialized = null;
            if (outcome != ToolExecutionOutcome.AwaitingConfirmation && !awaitingUser)
            {
                try { materialized = LegacyToolResultAdapter.Materialize(result, outcome); }
                catch (Exception ex)
                {
                    // Domain execution already established this outcome. Invalid
                    // projection data cannot turn a known write into an unknown one.
                    _preparationFailure = AgentModelResult.Failed(ModelProtocolFailureKind.Infrastructure, ex.Message);
                    materialized = new ToolResultMaterialization(new RNAssistant.Core.Tools.Contracts.ToolResult(
                        outcome == ToolExecutionOutcome.Ok ? RNAssistant.Core.Tools.Contracts.ToolResultStatus.Ok :
                            outcome == ToolExecutionOutcome.Unknown ? RNAssistant.Core.Tools.Contracts.ToolResultStatus.Unknown :
                                RNAssistant.Core.Tools.Contracts.ToolResultStatus.Error,
                        result.Message, new JObject { ["code"] = "result_materialization_failed", ["loaded"] = false,
                            ["complete"] = false }.ToString(Formatting.None)));
                }
                _results[context.Call.Id] = materialized;
            }
            return new ToolExecutionRecord(context, outcome, DateTime.UtcNow, result.Message,
                mayHaveDispatched: outcome != ToolExecutionOutcome.AwaitingConfirmation,
                pendingId: outcome == ToolExecutionOutcome.AwaitingConfirmation ? result.PendingId : null,
                awaitingUser: awaitingUser, toolStepsConsumed: Math.Max(1, result.ToolStepsConsumed),
                documentRuntimeId: _session.LastRun.DocumentRuntimeKey, result: materialized == null ? null : materialized.Result);
        }

        private ToolExecutionRecord RegistrationChanged(ToolExecutionContext context)
        {
            const string message = "The pinned tool registration changed before dispatch.";
            var data = new JObject { ["code"] = "tool_registration_changed" }.ToString(Formatting.None);
            var terminal = RNAssistant.Core.Tools.Contracts.ToolResult.Error(message, data);
            _results[context.Call.Id] = new ToolResultMaterialization(terminal);
            _uiResults[context.Call.Id] = ToolResult.Fail(message, data, "tool_registration_changed", false);
            return new ToolExecutionRecord(context, ToolExecutionOutcome.Error, DateTime.UtcNow,
                message, mayHaveDispatched: false, result: terminal);
        }

        private string RegisterNativePending(
            ToolExecutionContext context,
            ToolPreparationResult preparation)
        {
            if (_registrar == null) return null;
            var command = Command(context.Call, context.StepId, false);
            command.Arguments = ReadArguments(context.Call);
            command.RuntimeGuardJson = null;
            var message = preparation == null
                ? "Tool requires confirmation before execution: " + context.Call.Name
                : preparation.Result.Message;
            var result = ToolResult.WaitingConfirmation(message);
            result.DataJson = preparation == null ? null : preparation.Result.DataJson;
            result.ConfirmationCatalogSha256 = context.Policy.Revision;
            result.PendingId = _registrar(_session, command, result);
            _uiResults[context.Call.Id] = result;
            return result.PendingId;
        }

        private ToolCommand Command(ToolCall call, string stepId, bool confirmed)
        {
            ToolCommand command;
            if (_commands.TryGetValue(call.Id, out command)) return command;
            command = confirmed && _confirmedCommand != null ? _confirmedCommand : new ToolCommand
            {
                ToolId = call.Name, ToolCallId = call.Id,
                Arguments = ReadArguments(call)
            };
            command.RuntimeStepId = stepId;
            _commands.Add(call.Id, command);
            return command;
        }

        private static Dictionary<string, object> ReadArguments(ToolCall call)
        {
            // This boundary must preserve decoded model strings, including ISO
            // text and literal backslashes; it does not normalize domain data.
            return JsonConvert.DeserializeObject<Dictionary<string, object>>(call.ArgumentsJson,
                new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });
        }
    }

    // Each domain handler switch removes its legacy mapping. This classifies ONE invocation;
    // only AgentKernel accumulates counts/health across calls and confirmations.
    internal static class LegacyToolOutcomeAdapter
    {
        internal static ToolExecutionOutcome Map(ToolPolicySnapshot policy, ToolResult result)
        {
            if (result != null && AgentTranscript.IsWaitingResult(result)) return ToolExecutionOutcome.AwaitingConfirmation;
            var uncertain = result == null || EqualsCode(result.Status, "unknown") ||
                EqualsCode(result.Status, "interrupted_unknown") || EqualsCode(result.Status, "partial_failure") ||
                EqualsCode(result.ErrorCode, "tool_effect_uncertain") || EqualsCode(result.ErrorCode, "missing_result");
            if (policy == null || policy.MayHaveSideEffects && uncertain) return ToolExecutionOutcome.Unknown;
            return result != null && result.Success ? ToolExecutionOutcome.Ok : ToolExecutionOutcome.Error;
        }

        private static bool EqualsCode(string value, string code)
        {
            return string.Equals(value, code, StringComparison.OrdinalIgnoreCase);
        }
    }
}
