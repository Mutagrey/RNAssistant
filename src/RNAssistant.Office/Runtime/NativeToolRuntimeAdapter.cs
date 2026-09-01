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
    // Production composition for migrated handlers. Unmigrated controller
    // families still use the explicit legacy port until their atomic switch.
    internal sealed class NativeToolRuntimeAdapter : IToolRuntime
    {
        private readonly ToolRuntime _runtime;
        private readonly bool _trace;
        private readonly ChatSession _session;
        private readonly object _projectionSync = new object();
        private readonly Dictionary<string, IReadOnlyList<ChatAttachment>> _resourceReadAttachments =
            new Dictionary<string, IReadOnlyList<ChatAttachment>>(StringComparer.Ordinal);

        internal NativeToolRuntimeAdapter(ResourceGatewayService gateway,
            ExcelReadToolAdapter excelReads,
            ExcelWriteToolAdapter excelWrites,
            ExcelFindReplaceToolAdapter excelFindReplace,
            ExcelSheetToolAdapter excelSheets,
            ExcelRangeMutationToolAdapter excelRangeMutations,
            ExcelTableToolAdapter excelTables,
            ExcelChartToolAdapter excelCharts,
            WordToolAdapter wordTools,
            PowerPointToolAdapter powerPointTools,
            OutlookToolAdapter outlookTools,
            HostRuntime hostRuntime, ChatSession session,
            ToolPackSnapshot snapshot, AppSettings settings, string mode,
            bool trace = true)
            : this(gateway, excelReads, excelWrites, excelFindReplace,
                excelSheets, excelRangeMutations, excelTables, excelCharts,
                wordTools, powerPointTools, outlookTools, null, null, null,
                null, null, null, null, false, hostRuntime,
                session, snapshot, settings, mode, null, trace)
        {
        }

        internal NativeToolRuntimeAdapter(ResourceGatewayService gateway, ExcelReadToolAdapter excelReads,
            ExcelWriteToolAdapter excelWrites, ExcelFindReplaceToolAdapter excelFindReplace,
            ExcelSheetToolAdapter excelSheets,
            ExcelRangeMutationToolAdapter excelRangeMutations,
            ExcelTableToolAdapter excelTables,
            ExcelChartToolAdapter excelCharts,
            WordToolAdapter wordTools,
            PowerPointToolAdapter powerPointTools,
            OutlookToolAdapter outlookTools,
            VbaToolExecutor vbaTools,
            HtmlWorkspaceToolService htmlWorkspaceTools,
            CapabilityCatalogService capabilityTools,
            PromptSettingsService promptTools,
            ToolAuthoringService toolAuthoring,
            IReadOnlyList<ToolDefinition> discoveryCatalog,
            IReadOnlyList<SkillDefinition> skillCatalog,
            bool manualRun,
            HostRuntime hostRuntime, ChatSession session,
            ToolPackSnapshot snapshot, AppSettings settings, string mode,
            Func<ToolExecutionContext, ToolPreparationResult, string> pendingRegistrar = null,
            bool trace = true)
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
                else if (PowerPointToolIds.Owns(registration.Descriptor.Id))
                {
                    if (powerPointTools == null || hostRuntime == null)
                        throw new InvalidOperationException(
                            "PowerPoint handler dependencies are unavailable.");
                    handler = new PowerPointToolHandler(
                        registration.Descriptor.Id, powerPointTools,
                        hostRuntime, session);
                }
                else if (OutlookToolIds.Owns(registration.Descriptor.Id))
                {
                    if (outlookTools == null || hostRuntime == null)
                        throw new InvalidOperationException(
                            "Outlook handler dependencies are unavailable.");
                    handler = new OutlookToolHandler(
                        registration.Descriptor.Id, outlookTools,
                        hostRuntime, session);
                }
                else if (VbaToolCatalog.Owns(registration.Descriptor.Id))
                {
                    if (vbaTools == null || hostRuntime == null)
                        throw new InvalidOperationException(
                            "VBA handler dependencies are unavailable.");
                    handler = new VbaToolHandler(
                        registration.Descriptor.Id, vbaTools,
                        hostRuntime, session);
                }
                else if (string.Equals(registration.Descriptor.Id,
                    UserQuestionToolCatalog.AskToolId,
                    StringComparison.Ordinal))
                {
                    handler = new UserQuestionToolHandler();
                }
                else if (PlanDocumentToolCatalog.Owns(
                    registration.Descriptor.Id))
                {
                    handler = new PlanDocumentToolHandler(
                        registration.Descriptor.Id, session);
                }
                else if (TaskListToolCatalog.Owns(
                    registration.Descriptor.Id))
                {
                    handler = new TaskListToolHandler(
                        registration.Descriptor.Id, session);
                }
                else if (HtmlWorkspaceToolCatalog.Owns(
                    registration.Descriptor.Id))
                {
                    if (htmlWorkspaceTools == null)
                        throw new InvalidOperationException(
                            "HTML workspace handler dependencies are unavailable.");
                    handler = new HtmlWorkspaceToolHandler(
                        registration.Descriptor.Id, session,
                        htmlWorkspaceTools);
                }
                else if (CapabilityToolCatalog.Owns(
                    registration.Descriptor.Id))
                {
                    if (capabilityTools == null)
                        throw new InvalidOperationException(
                            "Capability handler dependencies are unavailable.");
                    handler = new CapabilityToolHandler(
                        registration.Descriptor.Id, capabilityTools,
                        discoveryCatalog, skillCatalog, manualRun);
                }
                else if (PromptToolCatalog.Owns(
                    registration.Descriptor.Id))
                {
                    if (promptTools == null)
                        throw new InvalidOperationException(
                            "Prompt handler dependencies are unavailable.");
                    handler = string.Equals(registration.Descriptor.Id,
                            PromptToolCatalog.ReadToolId,
                            StringComparison.Ordinal)
                        ? (IToolHandler)new PromptReadToolHandler(promptTools)
                        : new PromptSaveToolHandler(promptTools);
                }
                else if (ToolAuthoringCatalog.Owns(
                    registration.Descriptor.Id))
                {
                    if (toolAuthoring == null)
                        throw new InvalidOperationException(
                            "Tool authoring handler dependencies are unavailable.");
                    handler = ToolAuthoringCatalog.IsMutation(
                            registration.Descriptor.Id)
                        ? (IToolHandler)new ToolAuthoringMutationToolHandler(
                            registration.Descriptor.Id, toolAuthoring)
                        : new ToolAuthoringReadToolHandler(
                            registration.Descriptor.Id, toolAuthoring);
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
                settings != null && settings.AutoConfirmToolActions,
                policy.AllowsConfirmation, pendingRegistrar);
            _session = session;
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
                WordToolIds.Owns(toolId) || PowerPointToolIds.Owns(toolId) ||
                OutlookToolIds.Owns(toolId) || VbaToolCatalog.Owns(toolId) ||
                string.Equals(toolId, UserQuestionToolCatalog.AskToolId,
                    StringComparison.Ordinal) ||
                PlanDocumentToolCatalog.Owns(toolId) ||
                TaskListToolCatalog.Owns(toolId) ||
                HtmlWorkspaceToolCatalog.Owns(toolId) ||
                CapabilityToolCatalog.Owns(toolId) ||
                PromptToolCatalog.Owns(toolId) ||
                ToolAuthoringCatalog.Owns(toolId);
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
            if (PowerPointToolIds.Owns(toolId))
                return PowerPointToolHandler.BindingFor(toolId);
            if (OutlookToolIds.Owns(toolId))
                return OutlookToolHandler.BindingFor(toolId);
            if (VbaToolCatalog.Owns(toolId))
                return VbaToolHandler.BindingFor(toolId);
            if (string.Equals(toolId, UserQuestionToolCatalog.AskToolId,
                StringComparison.Ordinal))
                return UserQuestionToolHandler.Binding;
            if (PlanDocumentToolCatalog.Owns(toolId))
                return PlanDocumentToolHandler.BindingFor(toolId);
            if (TaskListToolCatalog.Owns(toolId))
                return TaskListToolHandler.BindingFor(toolId);
            if (HtmlWorkspaceToolCatalog.Owns(toolId))
                return HtmlWorkspaceToolHandler.BindingFor(toolId);
            if (CapabilityToolCatalog.Owns(toolId))
                return CapabilityToolHandler.BindingFor(toolId);
            if (string.Equals(toolId, PromptToolCatalog.ReadToolId,
                StringComparison.Ordinal))
                return PromptReadToolHandler.Binding;
            if (string.Equals(toolId, PromptToolCatalog.SaveToolId,
                StringComparison.Ordinal))
                return PromptSaveToolHandler.Binding;
            if (ToolAuthoringCatalog.IsMutation(toolId))
                return ToolAuthoringMutationToolHandler.BindingFor(toolId);
            if (ToolAuthoringCatalog.Owns(toolId))
                return ToolAuthoringReadToolHandler.BindingFor(toolId);
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
            var runId = _session == null || _session.LastRun == null ||
                string.IsNullOrWhiteSpace(_session.LastRun.RunId)
                    ? identity : _session.LastRun.RunId;
            var turnId = _session == null || _session.LastRun == null ||
                string.IsNullOrWhiteSpace(_session.LastRun.TurnId)
                    ? identity : _session.LastRun.TurnId;
            var context = new ToolExecutionContext(call, policy, runId, turnId,
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
