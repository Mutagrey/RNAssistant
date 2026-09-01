using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Services
{
    internal sealed partial class ConversationKernelAdapter
    {
        public ToolPolicySnapshot Describe(ToolCall call)
        {
            return call == null || !_nativeTools.Handles(call.Name)
                ? null : _nativeTools.Describe(call);
        }

        public async Task<ToolExecutionRecord> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
        {
            if (_preparationFailure != null)
                return new ToolExecutionRecord(context, ToolExecutionOutcome.NotDispatched,
                    DateTime.UtcNow, "Model input preparation failed; remaining calls were not dispatched.", mayHaveDispatched: false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_nativeTools.Handles(context.Call.Name))
                return MissingRegistration(context);
            var record = await _nativeTools.ExecuteAsync(context,
                cancellationToken).ConfigureAwait(false);
            var materialization = _nativeTools.TakeMaterialization(record);
            if (materialization != null)
                _results[context.Call.Id] = materialization;
            return record;
        }

        private static ToolExecutionRecord MissingRegistration(
            ToolExecutionContext context)
        {
            const string message =
                "The accepted tool has no direct runtime registration.";
            var data = new JObject
            {
                ["code"] = "tool_registration_missing"
            }.ToString(Formatting.None);
            var terminal = RNAssistant.Core.Tools.Contracts.ToolResult.Error(message, data);
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
            return _registrar(_session, command,
                new PendingToolRegistration(
                    preparation == null
                        ? "Tool requires confirmation before execution: " +
                            context.Call.Name
                        : preparation.Result.Message,
                    preparation == null
                        ? null : preparation.Result.DataJson,
                    context.Policy.Revision));
        }

        private ToolInvocation Command(ToolCall call, string stepId, bool confirmed)
        {
            ToolInvocation command;
            if (_commands.TryGetValue(call.Id, out command)) return command;
            command = confirmed && _confirmedCommand != null ? _confirmedCommand : new ToolInvocation
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

}
