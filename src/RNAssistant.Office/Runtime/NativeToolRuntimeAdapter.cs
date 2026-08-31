using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
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
        private readonly object _projectionSync = new object();
        private readonly Dictionary<string, IReadOnlyList<ChatAttachment>> _resourceReadAttachments =
            new Dictionary<string, IReadOnlyList<ChatAttachment>>(StringComparer.Ordinal);

        internal NativeToolRuntimeAdapter(ResourceGatewayService gateway, ExcelReadToolAdapter excelReads,
            ExcelWriteToolAdapter excelWrites, ExcelFindReplaceToolAdapter excelFindReplace,
            ExcelSheetToolAdapter excelSheets,
            ExcelRangeMutationToolAdapter excelRangeMutations,
            ExcelTableToolAdapter excelTables,
            ExcelChartToolAdapter excelCharts,
            WordToolAdapter wordTools,
            HostRuntime hostRuntime, ChatSession session,
            ToolPackSnapshot snapshot, AppSettings settings, string mode, bool trace = true)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            var registry = new ToolHandlerRegistry();
            foreach (var registration in snapshot.Registrations.Where(item => Owns(item.Descriptor.Id)))
            {
                var binding = BindingFor(registration.Descriptor.Id);
                if (binding == null ||
                    !string.Equals(binding.HandlerId, registration.Binding.HandlerId, StringComparison.Ordinal) ||
                    !string.Equals(binding.EntryPoint, registration.Binding.EntryPoint, StringComparison.Ordinal))
                    throw new InvalidOperationException("Pinned native binding does not match its handler: " + registration.Descriptor.Id);
                IToolHandler handler;
                if (string.Equals(registration.Descriptor.Id, ResourceToolCatalog.ListToolId, StringComparison.Ordinal))
                {
                    handler = new ResourceListToolHandler(gateway, session);
                }
                else if (string.Equals(registration.Descriptor.Id, ResourceToolCatalog.ResolveToolId, StringComparison.Ordinal))
                {
                    handler = new ResourceResolveToolHandler(gateway, session);
                }
                else if (string.Equals(registration.Descriptor.Id, ResourceToolCatalog.SearchToolId, StringComparison.Ordinal))
                {
                    handler = new ResourceSearchToolHandler(gateway, session);
                }
                else if (string.Equals(registration.Descriptor.Id, ResourceToolCatalog.ReadToolId, StringComparison.Ordinal))
                {
                    handler = new ResourceReadToolHandler(gateway, session, CaptureResourceReadAttachments);
                }
                else if (ExcelReadToolIds.Owns(registration.Descriptor.Id))
                {
                    if (excelReads == null || hostRuntime == null)
                        throw new InvalidOperationException("Excel read handler dependencies are unavailable.");
                    handler = new ExcelReadToolHandler(registration.Descriptor.Id, excelReads, hostRuntime, session);
                }
                else if (ExcelFindReplaceToolIds.Owns(registration.Descriptor.Id))
                {
                    if (excelFindReplace == null || hostRuntime == null)
                        throw new InvalidOperationException("Excel find/replace handler dependencies are unavailable.");
                    handler = new ExcelFindReplaceToolHandler(
                        registration.Descriptor.Id, excelFindReplace, hostRuntime, session);
                }
                else if (ExcelSheetToolIds.Owns(registration.Descriptor.Id))
                {
                    if (excelSheets == null || hostRuntime == null)
                        throw new InvalidOperationException(
                            "Excel sheet handler dependencies are unavailable.");
                    handler = new ExcelSheetToolHandler(
                        registration.Descriptor.Id, excelSheets, hostRuntime, session);
                }
                else if (ExcelRangeMutationToolIds.Owns(registration.Descriptor.Id))
                {
                    if (excelRangeMutations == null || hostRuntime == null)
                        throw new InvalidOperationException(
                            "Excel range mutation handler dependencies are unavailable.");
                    handler = new ExcelRangeMutationToolHandler(
                        registration.Descriptor.Id, excelRangeMutations,
                        hostRuntime, session);
                }
                else if (ExcelTableToolIds.Owns(registration.Descriptor.Id))
                {
                    if (excelTables == null || hostRuntime == null)
                        throw new InvalidOperationException(
                            "Excel table handler dependencies are unavailable.");
                    handler = new ExcelTableToolHandler(
                        excelTables, hostRuntime, session);
                }
                else if (ExcelChartToolIds.Owns(registration.Descriptor.Id))
                {
                    if (excelCharts == null || hostRuntime == null)
                        throw new InvalidOperationException(
                            "Excel chart handler dependencies are unavailable.");
                    handler = new ExcelChartToolHandler(
                        registration.Descriptor.Id, excelCharts,
                        hostRuntime, session);
                }
                else if (WordToolIds.Owns(registration.Descriptor.Id))
                {
                    if (wordTools == null || hostRuntime == null)
                        throw new InvalidOperationException(
                            "Word handler dependencies are unavailable.");
                    handler = new WordToolHandler(
                        registration.Descriptor.Id, wordTools,
                        hostRuntime, session);
                }
                else
                {
                    if (excelWrites == null || hostRuntime == null)
                        throw new InvalidOperationException("Excel write handler dependencies are unavailable.");
                    handler = new ExcelWriteToolHandler(excelWrites, hostRuntime, session);
                }
                registry.Register(registration, handler);
            }
            var policy = ConversationRunPolicy.For(mode);
            _runtime = new ToolRuntime(registry, policy.Mode,
                settings != null && settings.AutoConfirmToolActions, policy.AllowsConfirmation);
            _trace = trace;
        }

        internal static bool Owns(string toolId)
        {
            return string.Equals(toolId, ResourceToolCatalog.ListToolId, StringComparison.Ordinal) ||
                string.Equals(toolId, ResourceToolCatalog.ResolveToolId, StringComparison.Ordinal) ||
                string.Equals(toolId, ResourceToolCatalog.SearchToolId, StringComparison.Ordinal) ||
                string.Equals(toolId, ResourceToolCatalog.ReadToolId, StringComparison.Ordinal) ||
                ExcelReadToolIds.Owns(toolId) || ExcelWriteToolIds.Owns(toolId) ||
                ExcelFindReplaceToolIds.Owns(toolId) || ExcelSheetToolIds.Owns(toolId) ||
                ExcelRangeMutationToolIds.Owns(toolId) ||
                ExcelTableToolIds.Owns(toolId) || ExcelChartToolIds.Owns(toolId) ||
                WordToolIds.Owns(toolId);
        }

        internal static ToolBinding BindingFor(string toolId)
        {
            if (string.Equals(toolId, ResourceToolCatalog.ListToolId, StringComparison.Ordinal))
                return ResourceListToolHandler.Binding;
            if (string.Equals(toolId, ResourceToolCatalog.ResolveToolId, StringComparison.Ordinal))
                return ResourceResolveToolHandler.Binding;
            if (string.Equals(toolId, ResourceToolCatalog.SearchToolId, StringComparison.Ordinal))
                return ResourceSearchToolHandler.Binding;
            if (string.Equals(toolId, ResourceToolCatalog.ReadToolId, StringComparison.Ordinal))
                return ResourceReadToolHandler.Binding;
            if (ExcelReadToolIds.Owns(toolId)) return ExcelReadToolHandler.BindingFor(toolId);
            if (ExcelWriteToolIds.Owns(toolId)) return ExcelWriteToolHandler.Binding;
            if (ExcelFindReplaceToolIds.Owns(toolId))
                return ExcelFindReplaceToolHandler.BindingFor(toolId);
            if (ExcelSheetToolIds.Owns(toolId))
                return ExcelSheetToolHandler.BindingFor(toolId);
            if (ExcelRangeMutationToolIds.Owns(toolId))
                return ExcelRangeMutationToolHandler.BindingFor(toolId);
            if (ExcelTableToolIds.Owns(toolId)) return ExcelTableToolHandler.Binding;
            if (ExcelChartToolIds.Owns(toolId))
                return ExcelChartToolHandler.BindingFor(toolId);
            if (WordToolIds.Owns(toolId))
                return WordToolHandler.BindingFor(toolId);
            return null;
        }

        public ToolPolicySnapshot Describe(ToolCall call) { return _runtime.Describe(call); }

        public async Task<ToolExecutionRecord> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
        {
            Trace(context, SessionEventKind.ToolExecutionStartedObservation, null);
            try
            {
                var record = await _runtime.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
                Trace(context, SessionEventKind.ToolExecutionCompletedObservation, record.Outcome.ToString());
                return record;
            }
            catch (Exception ex)
            {
                Trace(context, SessionEventKind.ToolExecutionCompletedObservation,
                    ex is OperationCanceledException ? "cancelled" : "threw");
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
            var record = ExecuteAsync(context, token).GetAwaiter().GetResult();
            var result = ToolResultUiProjection.Create(record);
            var materialized = TakeMaterialization(record);
            if (materialized != null)
            {
                result.ModelAttachments = materialized.ModelAttachments;
                ToolResultUiProjection.IncludeResources(result, materialized);
            }
            return result;
        }

        internal ToolResultMaterialization TakeMaterialization(ToolExecutionRecord record)
        {
            if (record == null || record.Result == null) return null;
            IReadOnlyList<ChatAttachment> attachments = null;
            lock (_projectionSync)
            {
                _resourceReadAttachments.TryGetValue(record.Context.Call.Id, out attachments);
                _resourceReadAttachments.Remove(record.Context.Call.Id);
            }
            return new ToolResultMaterialization(record.Result, attachments);
        }

        private void CaptureResourceReadAttachments(string callId, IReadOnlyList<ChatAttachment> attachments)
        {
            if (string.IsNullOrWhiteSpace(callId) || attachments == null || attachments.Count == 0) return;
            var captured = attachments.Where(item => item != null).ToArray();
            if (captured.Length == 0) return;
            lock (_projectionSync)
            {
                _resourceReadAttachments[callId] = Array.AsReadOnly(captured);
            }
        }

        private void Trace(ToolExecutionContext context, SessionEventKind kind, string status)
        {
            if (!_trace) return;
            RunCausalTrace.Record(new CausalTraceRecord(kind)
            {
                StepId = context.StepId, ToolCallId = context.Call.Id,
                ToolId = context.Call.Name, Status = status, Boundary = "tool_runtime"
            });
        }
    }
}
