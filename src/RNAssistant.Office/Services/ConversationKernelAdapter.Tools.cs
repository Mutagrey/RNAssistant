using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Services
{
    internal sealed partial class ConversationKernelAdapter
    {
        public ToolPolicySnapshot Describe(ToolCall call)
        {
            var tool = _catalog.SingleOrDefault(item => string.Equals(item.Id, call.Name, StringComparison.Ordinal));
            if (tool == null) return null;
            return new ToolPolicySnapshot(tool.Id, ConversationRunService.ToolExecutionFingerprint(_catalog, tool.Id),
                tool.MutatesDocument || tool.MutatesLocalState, tool.RequiresConfirmation,
                ConversationProtocolContext.BatchSafeReadIds(_catalog).Contains(tool.Id, StringComparer.Ordinal));
        }

        public Task<ToolExecutionRecord> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
        {
            if (_preparationFailure != null)
                return Task.FromResult(new ToolExecutionRecord(context, ToolExecutionOutcome.NotDispatched,
                    DateTime.UtcNow, "Model input preparation failed; remaining calls were not dispatched.", mayHaveDispatched: false));
            cancellationToken.ThrowIfCancellationRequested();
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
            _results[context.Call.Id] = result;
            var outcome = LegacyToolOutcomeAdapter.Map(context.Policy, result);
            return Task.FromResult(new ToolExecutionRecord(context, outcome, DateTime.UtcNow, result.Message,
                mayHaveDispatched: outcome != ToolExecutionOutcome.AwaitingConfirmation,
                pendingId: outcome == ToolExecutionOutcome.AwaitingConfirmation ? result.PendingId : null,
                awaitingUser: AgentTranscript.IsAwaitingUserResult(result), toolStepsConsumed: Math.Max(1, result.ToolStepsConsumed),
                documentRuntimeId: _session.LastRun.DocumentRuntimeKey));
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

    // Phase 4 removes this legacy result mapping. It classifies ONE invocation;
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
