using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
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

        internal NativeToolRuntimeAdapter(ResourceGatewayService gateway, ExcelReadToolAdapter excelReads,
            ExcelWriteToolAdapter excelWrites, HostRuntime hostRuntime, ChatSession session,
            IEnumerable<ToolDefinition> catalog, AppSettings settings, string mode, bool trace = true)
        {
            var registry = new ToolHandlerRegistry();
            var tools = (catalog ?? new ToolDefinition[0]).ToArray();
            foreach (var definition in tools.Where(tool => tool != null && Owns(tool.Id)))
            {
                if (!Runnable(definition)) continue;
                ToolBinding binding;
                IToolHandler handler;
                if (string.Equals(definition.Id, ResourceToolExecutor.ListToolId, StringComparison.Ordinal))
                {
                    binding = ResourceListToolHandler.Binding;
                    handler = new ResourceListToolHandler(gateway, session);
                }
                else if (ExcelReadToolIds.Owns(definition.Id))
                {
                    if (excelReads == null || hostRuntime == null) continue;
                    binding = ExcelReadToolHandler.BindingFor(definition.Id);
                    handler = new ExcelReadToolHandler(definition.Id, excelReads, hostRuntime, session);
                }
                else
                {
                    if (excelWrites == null || hostRuntime == null) continue;
                    binding = ExcelWriteToolHandler.Binding;
                    handler = new ExcelWriteToolHandler(excelWrites, hostRuntime, session);
                }
                var revision = binding.HandlerId + ":" +
                    ConversationRunService.ToolExecutionFingerprint(tools, definition.Id);
                registry.Register(LegacyToolDefinitionAdapter.Adapt(definition, revision, binding, mode), handler);
            }
            var policy = ConversationRunPolicy.For(mode);
            _runtime = new ToolRuntime(registry, policy.Mode,
                settings != null && settings.AutoConfirmToolActions, policy.AllowsConfirmation);
            _trace = trace;
        }

        internal static bool Owns(string toolId)
        {
            return string.Equals(toolId, ResourceToolExecutor.ListToolId, StringComparison.Ordinal) ||
                ExcelReadToolIds.Owns(toolId) || ExcelWriteToolIds.Owns(toolId);
        }

        private static bool Runnable(ToolDefinition definition)
        {
            return definition != null && definition.Enabled && definition.BuiltIn &&
                string.Equals(definition.Executor, "builtin", StringComparison.Ordinal) &&
                (string.IsNullOrWhiteSpace(definition.CapabilityStatus) ||
                 string.Equals(definition.CapabilityStatus, "available", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(definition.CapabilityStatus, "partial", StringComparison.OrdinalIgnoreCase));
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
            return ToolResultUiProjection.Create(ExecuteAsync(context, token).GetAwaiter().GetResult());
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
