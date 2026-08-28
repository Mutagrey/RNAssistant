using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using LegacyResult = RNAssistant.Core.Models.ToolResult;

namespace RNAssistant.Office.Runtime
{
    // Production composition for migrated handlers. Unmigrated domain tools use
    // the explicit legacy port, including its existing VBA preparation sequence.
    internal sealed class NativeToolRuntimeAdapter : IToolRuntime
    {
        private readonly ToolRuntime _runtime;
        private readonly bool _trace;

        internal NativeToolRuntimeAdapter(ResourceGatewayService gateway, ChatSession session,
            IEnumerable<ToolDefinition> catalog, AppSettings settings, string mode, bool trace = true)
        {
            var registry = new ToolHandlerRegistry();
            var tools = (catalog ?? new ToolDefinition[0]).ToArray();
            var definition = tools.SingleOrDefault(tool => tool != null && Owns(tool.Id));
            if (definition != null && definition.Enabled && definition.BuiltIn &&
                string.Equals(definition.Executor, "builtin", StringComparison.Ordinal) &&
                (string.IsNullOrWhiteSpace(definition.CapabilityStatus) || definition.CapabilityStatus == "available" || definition.CapabilityStatus == "partial"))
            {
                var revision = ResourceListToolHandler.Binding.HandlerId + ":" +
                    ConversationRunService.ToolExecutionFingerprint(tools, definition.Id);
                registry.Register(LegacyToolDefinitionAdapter.Adapt(definition, revision, ResourceListToolHandler.Binding, mode),
                    new ResourceListToolHandler(gateway, session));
            }
            var policy = ConversationRunPolicy.For(mode);
            _runtime = new ToolRuntime(registry, policy.Mode,
                settings != null && settings.AutoConfirmToolActions, policy.AllowsConfirmation);
            _trace = trace;
        }

        internal static bool Owns(string toolId)
        {
            return string.Equals(toolId, ResourceToolExecutor.ListToolId, StringComparison.Ordinal);
        }

        public ToolPolicySnapshot Describe(ToolCall call) { return _runtime.Describe(call); }

        public async Task<ToolExecutionRecord> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
        {
            Trace(context, "tool.execution.started", null);
            try
            {
                var record = await _runtime.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
                Trace(context, "tool.execution.completed", record.Outcome.ToString());
                return record;
            }
            catch (Exception ex)
            {
                Trace(context, "tool.execution.completed", ex is OperationCanceledException ? "cancelled" : "threw");
                throw;
            }
        }

        internal LegacyResult ExecuteCommand(ToolCommand command, int remainingSteps, bool confirmed, CancellationToken token)
        {
            // Manual commands also cross the same schema/policy/handler boundary;
            // these transient identities do not create an accepted model response.
            var call = new ToolCall(string.IsNullOrWhiteSpace(command.ToolCallId) ? Guid.NewGuid().ToString("N") : command.ToolCallId,
                command.ToolId, JsonConvert.SerializeObject(command.Arguments, Formatting.None));
            var policy = Describe(call);
            if (policy == null) return LegacyResult.Fail("No native handler is available for this exact tool id.", null, "unknown_tool", false);
            var identity = Guid.NewGuid().ToString("N");
            var context = new ToolExecutionContext(call, policy, identity, identity,
                string.IsNullOrWhiteSpace(command.RuntimeStepId) ? identity : command.RuntimeStepId,
                DateTime.UtcNow, confirmed, remainingSteps);
            return ProjectLegacy(ExecuteAsync(context, token).GetAwaiter().GetResult());
        }

        // Removed with the coordinated Tool Result v1 writer/readers switch (4B).
        // This is the only projection of native results into the CURRENT wire path.
        internal static LegacyResult ProjectLegacy(ToolExecutionRecord record)
        {
            var typed = record.Result;
            LegacyResult result;
            if (record.Outcome == ToolExecutionOutcome.AwaitingConfirmation)
            {
                result = LegacyResult.WaitingConfirmation(record.Message);
                result.PendingId = record.PendingId;
            }
            else if (typed != null && typed.Status == ToolResultStatus.Ok)
                result = record.AwaitingUser ? LegacyResult.AwaitingUser(typed.Message, typed.DataJson) : LegacyResult.Ok(typed.Message, typed.DataJson);
            else
            {
                var error = typed == null || string.IsNullOrWhiteSpace(typed.DataJson) ? null :
                    JsonConvert.DeserializeObject<JToken>(typed.DataJson,
                        new JsonSerializerSettings { DateParseHandling = DateParseHandling.None }) as JObject;
                var code = error == null ? null : error["code"];
                var retryable = error == null ? null : error["retryable"];
                result = LegacyResult.Fail(record.Message, typed == null ? null : typed.DataJson,
                    code != null && code.Type == JTokenType.String ? (string)code : null,
                    retryable != null && retryable.Type == JTokenType.Boolean ? (bool?)retryable : null);
                if (record.Outcome == ToolExecutionOutcome.Unknown) result.Status = "unknown";
            }
            if (typed != null) result.ModelResourceRefs = typed.Resources.ToList();
            result.ToolStepsConsumed = record.ToolStepsConsumed;
            return result;
        }

        private void Trace(ToolExecutionContext context, string stage, string status)
        {
            if (!_trace) return;
            RunCausalTrace.Record(new CausalTraceRecord
            {
                Stage = stage, StepId = context.StepId, ToolCallId = context.Call.Id,
                ToolId = context.Call.Name, Status = status, Boundary = "tool_runtime"
            });
        }
    }
}
